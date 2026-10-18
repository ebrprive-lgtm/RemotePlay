using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RemotePlay;

internal sealed class VideoAudioCompatibilityTranscoder
{
    private static readonly TimeSpan CacheEntryLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan TranscodeFailureLifetime = TimeSpan.FromMinutes(2);
    internal const string ReadyMarkerContents = "RemotePlay.VideoAudioCompatibilityCache:faststart-v1";

    private readonly string _cacheDirectory;
    private readonly string _ffmpegExecutable;
    private readonly Func<string, string, VideoAudioCompatibilityMode, bool, ProcessStartInfo> _processStartInfoFactory;
    private readonly object _transcodeTasksLock = new();
    private readonly Dictionary<string, TranscodeOperation<VideoAudioCompatibilityStream>> _transcodeTasks =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VideoAudioTranscodeProgress> _transcodeProgress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _transcodeFailureExpirationsUtc = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheCleanupGate = new(1, 1);
    private DateTime _lastCacheCleanupUtc = DateTime.MinValue;

    internal VideoAudioCompatibilityTranscoder(
        string? cacheDirectory = null,
        string ffmpegExecutable = "ffmpeg",
        Func<string, string, VideoAudioCompatibilityMode, bool, ProcessStartInfo>? processStartInfoFactory = null)
    {
        _cacheDirectory = Path.GetFullPath(
            cacheDirectory ?? Path.Combine(Path.GetTempPath(), "RemotePlay", "VideoAudioCompatibility"));
        _ffmpegExecutable = string.IsNullOrWhiteSpace(ffmpegExecutable) ? "ffmpeg" : ffmpegExecutable;
        _processStartInfoFactory = processStartInfoFactory
            ?? ((sourcePath, temporaryOutputPath, mode, progressive) =>
                progressive
                    ? CreateProgressiveStartInfo(_ffmpegExecutable, sourcePath, mode)
                    : CreateStartInfo(_ffmpegExecutable, sourcePath, temporaryOutputPath, mode));
    }

    internal async Task<string> GetOrCreateCompatibleMp4Async(
        string sourcePath,
        CancellationToken cancellationToken,
        double durationSeconds = 0)
    {
        var result = await GetOrCreateCompatibleStreamAsync(
            sourcePath,
            supportedAudioCodecs: [],
            cancellationToken,
            durationSeconds,
            forceAac: true).ConfigureAwait(false);
        return result.Path;
    }

    internal async Task<VideoAudioCompatibilityStream> GetOrCreateCompatibleStreamAsync(
        string sourcePath,
        IReadOnlyCollection<string> supportedAudioCodecs,
        CancellationToken cancellationToken,
        double durationSeconds = 0)
    {
        var request = await StartCompatibleStreamAsync(
            sourcePath,
            supportedAudioCodecs,
            cancellationToken,
            durationSeconds).ConfigureAwait(false);
        if (request.CompletedStream is not null)
            return request.CompletedStream;

        using var subscription = request.Subscription;
        return await request.CompletionTask!.ConfigureAwait(false);
    }

    internal async Task<VideoAudioCompatibilityStreamRequest> StartCompatibleStreamAsync(
        string sourcePath,
        IReadOnlyCollection<string> supportedAudioCodecs,
        CancellationToken cancellationToken,
        double durationSeconds = 0,
        string? subscriberId = null)
    {
        var sourceFile = new FileInfo(Path.GetFullPath(sourcePath));
        sourceFile.Refresh();
        if (!sourceFile.Exists)
            throw new FileNotFoundException("The source video file was not found.", sourceFile.FullName);

        var sourceLength = sourceFile.Length;
        var sourceLastWriteUtc = sourceFile.LastWriteTimeUtc;
        var cacheKey = CreateCacheKey(sourceFile.FullName, sourceLength, sourceLastWriteUtc);
        var capabilitySignature =
            VideoAudioCompatibilitySelector.CreateCapabilitySignature(supportedAudioCodecs);
        var operationKey = cacheKey + ":" + capabilitySignature;
        _transcodeFailureExpirationsUtc.TryRemove(operationKey, out _);
        _transcodeProgress[operationKey] =
            new VideoAudioTranscodeProgress(false, false, null, null, null, null, null, "checking");

        try
        {
            var mode = await SelectCompatibilityModeAsync(
                sourceFile.FullName,
                supportedAudioCodecs,
                cancellationToken).ConfigureAwait(false);
            if (mode == VideoAudioCompatibilityMode.Original)
            {
                RemoveProgressIfNoActiveOperation(operationKey);
                return VideoAudioCompatibilityStreamRequest.Completed(
                    new VideoAudioCompatibilityStream(sourceFile.FullName, mode));
            }

            var outputPath = GetCompatibleOutputPath(cacheKey, mode);
            await PurgeExpiredCacheFilesAsync(cancellationToken).ConfigureAwait(false);
            if (IsReadyFile(outputPath))
            {
                TouchCacheFile(outputPath);
                RemoveProgressIfNoActiveOperation(operationKey);
                return VideoAudioCompatibilityStreamRequest.Completed(
                    new VideoAudioCompatibilityStream(outputPath, mode));
            }

            while (true)
            {
                TranscodeOperation<VideoAudioCompatibilityStream>? operation = null;
                ProgressiveVideoAudioOutput? progressiveOutput = null;
                ProgressiveVideoAudioOutput.ProgressiveVideoAudioSubscription? subscription = null;
                TranscodeOperation<VideoAudioCompatibilityStream>? retiredOperation = null;
                var createdOperation = false;
                var completedCacheHit = false;

                lock (_transcodeTasksLock)
                {
                    if (_transcodeTasks.TryGetValue(operationKey, out var currentOperation)
                        && (currentOperation.IsCancellationRequested || currentOperation.IsCompleted))
                    {
                        _transcodeTasks.Remove(operationKey);
                        retiredOperation = currentOperation;
                    }

                    if (retiredOperation is null)
                    {
                        if (IsReadyFile(outputPath))
                        {
                            completedCacheHit = true;
                        }
                        else if (_transcodeTasks.TryGetValue(operationKey, out var existingOperation))
                        {
                            operation = existingOperation;
                            progressiveOutput = operation.ProgressiveOutput
                                ?? throw new InvalidOperationException(
                                    "The active Local Video conversion cannot be streamed progressively.");
                        }
                        else
                        {
                            progressiveOutput = new ProgressiveVideoAudioOutput();
                            operation = new TranscodeOperation<VideoAudioCompatibilityStream>(
                                token => PrepareProgressiveStreamAsync(
                                    sourceFile.FullName,
                                    sourceLength,
                                    sourceLastWriteUtc,
                                    cacheKey,
                                    operationKey,
                                    durationSeconds,
                                    mode,
                                    progressiveOutput,
                                    token),
                                cancellationToken,
                                progressiveOutput,
                                sourceFile.FullName);
                            _transcodeTasks.Add(operationKey, operation);
                            createdOperation = true;
                        }

                        if (!completedCacheHit)
                        {
                            var output = progressiveOutput
                                ?? throw new InvalidOperationException(
                                    "The progressive Local Video output was not initialized.");
                            subscription = output.Subscribe(subscriberId);
                        }
                    }
                }

                if (retiredOperation is not null)
                {
                    await WaitForRetiredProgressiveOperationAsync(retiredOperation).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReadyFile(outputPath))
                    {
                        TouchCacheFile(outputPath);
                        RemoveProgressIfNoActiveOperation(operationKey);
                        return VideoAudioCompatibilityStreamRequest.Completed(
                            new VideoAudioCompatibilityStream(outputPath, mode));
                    }
                    continue;
                }

                if (completedCacheHit)
                {
                    TouchCacheFile(outputPath);
                    RemoveProgressIfNoActiveOperation(operationKey);
                    return VideoAudioCompatibilityStreamRequest.Completed(
                        new VideoAudioCompatibilityStream(outputPath, mode));
                }

                var activeOperation = operation
                    ?? throw new InvalidOperationException("The Local Video conversion operation was not initialized.");
                var activeOutput = progressiveOutput
                    ?? throw new InvalidOperationException("The progressive Local Video output was not initialized.");
                var activeSubscription = subscription
                    ?? throw new InvalidOperationException("The progressive Local Video subscriber was not initialized.");
                var completionTask = activeOperation.Task;
                if (createdOperation)
                    ObserveAndCleanUpProgressiveOperation(operationKey, activeOperation, completionTask);

                if (!createdOperation
                    && (activeOperation.IsCancellationRequested
                        || completionTask.IsCanceled
                        || completionTask.IsFaulted))
                {
                    activeSubscription.Dispose();
                    var removedOperation = false;
                    lock (_transcodeTasksLock)
                    {
                        if (_transcodeTasks.TryGetValue(operationKey, out var currentOperation)
                            && ReferenceEquals(currentOperation, activeOperation))
                        {
                            _transcodeTasks.Remove(operationKey);
                            removedOperation = true;
                        }
                    }

                    if (removedOperation)
                        await WaitForRetiredProgressiveOperationAsync(activeOperation).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReadyFile(outputPath))
                    {
                        TouchCacheFile(outputPath);
                        RemoveProgressIfNoActiveOperation(operationKey);
                        return VideoAudioCompatibilityStreamRequest.Completed(
                            new VideoAudioCompatibilityStream(outputPath, mode));
                    }
                    continue;
                }

                if (completionTask.IsCompletedSuccessfully)
                {
                    activeSubscription.Dispose();
                    var completedStream = completionTask.Result;
                    if (completedStream.Mode != VideoAudioCompatibilityMode.Original)
                        TouchCacheFile(completedStream.Path);
                    return VideoAudioCompatibilityStreamRequest.Completed(completedStream);
                }

                return VideoAudioCompatibilityStreamRequest.Progressive(
                    completionTask,
                    activeOutput,
                    activeSubscription);
            }
        }
        catch
        {
            RemoveProgressIfNoActiveOperation(operationKey);
            throw;
        }
    }

    private void ObserveAndCleanUpProgressiveOperation(
        string operationKey,
        TranscodeOperation<VideoAudioCompatibilityStream> operation,
        Task<VideoAudioCompatibilityStream> task)
    {
        _ = task.ContinueWith(
            completedTask =>
            {
                var error = completedTask.Exception?.GetBaseException();
                if (completedTask.IsFaulted
                    && error is not OperationCanceledException
                    && error is not null)
                {
                    Logger.Error("Progressive Local Video audio compatibility conversion failed.", error);
                }

                var removedOperation = false;
                lock (_transcodeTasksLock)
                {
                    if (_transcodeTasks.TryGetValue(operationKey, out var currentOperation)
                        && ReferenceEquals(currentOperation, operation))
                    {
                        _transcodeTasks.Remove(operationKey);
                        removedOperation = true;
                    }
                }

                if (!removedOperation)
                    return;

                if (completedTask.IsCanceled
                    || completedTask.IsFaulted && error is OperationCanceledException)
                {
                    _transcodeProgress.TryRemove(operationKey, out _);
                }
                else if (completedTask.IsCompletedSuccessfully)
                {
                    _transcodeProgress.TryRemove(operationKey, out _);
                    _transcodeFailureExpirationsUtc.TryRemove(operationKey, out _);
                    var stream = completedTask.Result;
                    if (stream.Mode != VideoAudioCompatibilityMode.Original)
                        TouchCacheFile(stream.Path);
                }

                operation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RemoveProgressIfNoActiveOperation(string operationKey)
    {
        lock (_transcodeTasksLock)
        {
            if (!_transcodeTasks.ContainsKey(operationKey))
                _transcodeProgress.TryRemove(operationKey, out _);
        }
    }

    private async Task<VideoAudioCompatibilityStream> GetOrCreateCompatibleStreamAsync(
        string sourcePath,
        IReadOnlyCollection<string> supportedAudioCodecs,
        CancellationToken cancellationToken,
        double durationSeconds,
        bool forceAac)
    {
        var sourceFile = new FileInfo(Path.GetFullPath(sourcePath));
        sourceFile.Refresh();
        if (!sourceFile.Exists)
            throw new FileNotFoundException("The source video file was not found.", sourceFile.FullName);

        var sourceLength = sourceFile.Length;
        var sourceLastWriteUtc = sourceFile.LastWriteTimeUtc;
        var cacheKey = CreateCacheKey(sourceFile.FullName, sourceLength, sourceLastWriteUtc);
        var capabilitySignature = forceAac
            ? "forced-aac"
            : VideoAudioCompatibilitySelector.CreateCapabilitySignature(supportedAudioCodecs);
        var operationKey = cacheKey + ":" + capabilitySignature;

        TranscodeOperation<VideoAudioCompatibilityStream> operation;
        lock (_transcodeTasksLock)
        {
            if (_transcodeTasks.TryGetValue(operationKey, out var existingOperation))
            {
                operation = existingOperation;
            }
            else
            {
                operation = new TranscodeOperation<VideoAudioCompatibilityStream>(
                    token => PrepareCompatibleStreamAsync(
                        sourceFile.FullName,
                        sourceLength,
                        sourceLastWriteUtc,
                        cacheKey,
                        operationKey,
                        durationSeconds,
                        supportedAudioCodecs,
                        forceAac,
                        token),
                    cancellationToken,
                    sourcePath: sourceFile.FullName);
                _transcodeTasks.Add(operationKey, operation);
            }
        }

        var transcodeTask = operation.Task;

        try
        {
            var compatibleStream = await transcodeTask.ConfigureAwait(false);
            if (compatibleStream.Mode != VideoAudioCompatibilityMode.Original)
                TouchCacheFile(compatibleStream.Path);
            return compatibleStream;
        }
        finally
        {
            if (transcodeTask.IsCompleted)
            {
                lock (_transcodeTasksLock)
                {
                    if (_transcodeTasks.TryGetValue(operationKey, out var currentOperation)
                        && ReferenceEquals(currentOperation, operation))
                    {
                        _transcodeTasks.Remove(operationKey);
                        operation.Dispose();
                    }
                }
                _transcodeProgress.TryRemove(operationKey, out _);
            }
        }
    }

    internal bool CancelTranscode(string sourcePath, string? subscriberId = null)
    {
        var normalizedSourcePath = Path.GetFullPath(sourcePath);
        KeyValuePair<string, TranscodeOperation<VideoAudioCompatibilityStream>>[] operations;
        lock (_transcodeTasksLock)
        {
            operations = _transcodeTasks
                .Where(entry => string.Equals(
                    entry.Value.SourcePath,
                    normalizedSourcePath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var cancelled = false;
        foreach (var (operationKey, operation) in operations)
        {
            if (subscriberId is null)
            {
                var shouldCancel = false;
                lock (_transcodeTasksLock)
                {
                    if (_transcodeTasks.TryGetValue(operationKey, out var currentOperation)
                        && ReferenceEquals(currentOperation, operation))
                    {
                        shouldCancel = operation.RequestCancellation();
                    }
                }

                if (shouldCancel)
                    cancelled |= operation.Cancel();
                continue;
            }

            if (operation.ProgressiveOutput is not { } progressiveOutput
                || !progressiveOutput.CancelSubscriber(subscriberId, out var hasOtherSubscribers))
            {
                continue;
            }

            cancelled = true;
            if (!hasOtherSubscribers)
            {
                var shouldCancel = false;
                lock (_transcodeTasksLock)
                {
                    if (_transcodeTasks.TryGetValue(operationKey, out var currentOperation)
                        && ReferenceEquals(currentOperation, operation)
                        && !progressiveOutput.HasActiveSubscribers)
                    {
                        shouldCancel = operation.RequestCancellation();
                    }
                }

                if (shouldCancel)
                    operation.Cancel();
            }
        }
        return cancelled;
    }

    internal static string CreateCacheKey(string sourcePath, long sourceLength, DateTime sourceLastWriteUtc)
    {
        var normalizedPath = Path.GetFullPath(sourcePath).ToUpperInvariant();
        var key = $"{normalizedPath}\n{sourceLength}\n{sourceLastWriteUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    internal VideoAudioTranscodeProgress GetProgress(
        string sourcePath,
        IReadOnlyCollection<string>? supportedAudioCodecs = null)
    {
        var sourceFile = new FileInfo(Path.GetFullPath(sourcePath));
        sourceFile.Refresh();
        if (!sourceFile.Exists)
            throw new FileNotFoundException("The source video file was not found.", sourceFile.FullName);

        var cacheKey = CreateCacheKey(sourceFile.FullName, sourceFile.Length, sourceFile.LastWriteTimeUtc);
        if (supportedAudioCodecs is not null)
        {
            var operationKey = cacheKey + ":"
                + VideoAudioCompatibilitySelector.CreateCapabilitySignature(supportedAudioCodecs);
            if (_transcodeProgress.TryGetValue(operationKey, out var progress))
            {
                if (progress.ErrorMessage is not null
                    && _transcodeFailureExpirationsUtc.TryGetValue(operationKey, out var expiresUtc)
                    && expiresUtc <= DateTime.UtcNow)
                {
                    _transcodeProgress.TryRemove(operationKey, out _);
                    _transcodeFailureExpirationsUtc.TryRemove(operationKey, out _);
                }
                else
                {
                    return progress;
                }
            }
        }
        else
        {
            var operationPrefix = cacheKey + ":";
            var activeProgress = _transcodeProgress.FirstOrDefault(entry =>
                entry.Key.StartsWith(operationPrefix, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(activeProgress.Key))
                return activeProgress.Value;
        }

        var aacOutputPath = GetCompatibleOutputPath(cacheKey, VideoAudioCompatibilityMode.EncodeAac);
        var copyOutputPath = GetCompatibleOutputPath(cacheKey, VideoAudioCompatibilityMode.CopyAudioToMp4);
        var readyMode = IsReadyFile(aacOutputPath)
            ? "aac"
            : IsReadyFile(copyOutputPath) ? "copy" : null;
        return readyMode is not null
            ? new VideoAudioTranscodeProgress(false, true, null, null, 100, 0, null, readyMode)
            : new VideoAudioTranscodeProgress(false, false, null, null, null, null, null);
    }

    internal static bool TryParseOutputTimeMicroseconds(string? value, out double seconds)
    {
        seconds = 0;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
            || microseconds < 0)
        {
            return false;
        }

        seconds = microseconds / 1_000_000d;
        return double.IsFinite(seconds);
    }

    internal static bool TryParseSpeedMultiplier(string? value, out double multiplier)
    {
        multiplier = 0;
        var numericValue = value?.Trim();
        if (numericValue?.EndsWith('x') == true)
            numericValue = numericValue[..^1];

        return double.TryParse(
                numericValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out multiplier)
            && double.IsFinite(multiplier)
            && multiplier > 0;
    }

    internal static VideoAudioTranscodeProgress CalculateProgress(
        double? durationSeconds,
        double processedSeconds,
        double speedMultiplier,
        string? mode = null)
    {
        double? duration = durationSeconds.HasValue
            && double.IsFinite(durationSeconds.Value)
            && durationSeconds.Value > 0
                ? durationSeconds
                : null;
        double? processed = double.IsFinite(processedSeconds) && processedSeconds >= 0
            ? processedSeconds
            : null;
        double? speed = double.IsFinite(speedMultiplier) && speedMultiplier > 0
            ? speedMultiplier
            : null;
        double? progressPercent = null;
        double? estimatedRemainingSeconds = null;

        if (duration.HasValue && processed.HasValue)
        {
            progressPercent = Math.Clamp(processed.Value / duration.Value * 100, 0, 100);
            if (speed.HasValue)
            {
                estimatedRemainingSeconds = Math.Max(0, duration.Value - processed.Value) / speed.Value;
            }
        }

        return new VideoAudioTranscodeProgress(
            true,
            false,
            duration,
            processed,
            progressPercent,
            estimatedRemainingSeconds,
            speed,
            mode);
    }

    internal static ProcessStartInfo CreateStartInfo(
        string ffmpegExecutable,
        string sourcePath,
        string temporaryOutputPath,
        VideoAudioCompatibilityMode mode = VideoAudioCompatibilityMode.EncodeAac)
    {
        var arguments = CreateConversionArguments(sourcePath, mode, "pipe:1");
        arguments.AddRange(
        [
            "-movflags", "+faststart",
            "-f", "mp4",
            temporaryOutputPath,
        ]);

        return CreateProcessStartInfo(ffmpegExecutable, arguments);
    }

    internal static ProcessStartInfo CreateProgressiveStartInfo(
        string ffmpegExecutable,
        string sourcePath,
        VideoAudioCompatibilityMode mode = VideoAudioCompatibilityMode.EncodeAac)
    {
        var arguments = CreateConversionArguments(sourcePath, mode, "pipe:2");
        arguments.AddRange(
        [
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "1000000",
            "-flush_packets", "1",
            "-f", "mp4",
            "pipe:1",
        ]);

        return CreateProcessStartInfo(ffmpegExecutable, arguments);
    }

    private static List<string> CreateConversionArguments(
        string sourcePath,
        VideoAudioCompatibilityMode mode,
        string progressPipe)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostats",
            "-stats_period", "0.5",
            "-progress", progressPipe,
            "-y",
            "-i", sourcePath,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-map_metadata", "0",
            "-map_chapters", "0",
            "-c:v", "copy",
        };

        switch (mode)
        {
            case VideoAudioCompatibilityMode.CopyAudioToMp4:
                arguments.AddRange(["-c:a", "copy"]);
                break;
            case VideoAudioCompatibilityMode.EncodeAac:
                arguments.AddRange(["-c:a", "aac", "-ac", "2", "-b:a", "192k"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        arguments.AddRange(["-sn", "-dn"]);
        return arguments;
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string ffmpegExecutable,
        IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    private async Task<VideoAudioCompatibilityStream> PrepareCompatibleStreamAsync(
        string sourcePath,
        long sourceLength,
        DateTime sourceLastWriteUtc,
        string cacheKey,
        string operationKey,
        double durationSeconds,
        IReadOnlyCollection<string> supportedAudioCodecs,
        bool forceAac,
        CancellationToken cancellationToken)
    {
        _transcodeProgress[operationKey] =
            new VideoAudioTranscodeProgress(false, false, null, null, null, null, null, "checking");
        try
        {
            var mode = forceAac
                ? VideoAudioCompatibilityMode.EncodeAac
                : await SelectCompatibilityModeAsync(
                    sourcePath,
                    supportedAudioCodecs,
                    cancellationToken).ConfigureAwait(false);

            if (mode == VideoAudioCompatibilityMode.Original)
                return new VideoAudioCompatibilityStream(sourcePath, mode);

            try
            {
                var outputPath = await PrepareCompatibleMp4Async(
                    sourcePath,
                    sourceLength,
                    sourceLastWriteUtc,
                    cacheKey,
                    operationKey,
                    durationSeconds,
                    mode,
                    cancellationToken).ConfigureAwait(false);
                return new VideoAudioCompatibilityStream(outputPath, mode);
            }
            catch (VideoAudioTranscodingException ex)
                when (mode == VideoAudioCompatibilityMode.CopyAudioToMp4)
            {
                Logger.Warning(
                    "WebServer",
                    $"Could not copy Local Video audio into the compatibility MP4; retrying with stereo AAC: {ex.Message}");
                var outputPath = await PrepareCompatibleMp4Async(
                    sourcePath,
                    sourceLength,
                    sourceLastWriteUtc,
                    cacheKey,
                    operationKey,
                    durationSeconds,
                    VideoAudioCompatibilityMode.EncodeAac,
                    cancellationToken).ConfigureAwait(false);
                return new VideoAudioCompatibilityStream(
                    outputPath,
                    VideoAudioCompatibilityMode.EncodeAac);
            }
        }
        finally
        {
            _transcodeProgress.TryRemove(operationKey, out _);
        }
    }

    private static async Task<VideoAudioCompatibilityMode> SelectCompatibilityModeAsync(
        string sourcePath,
        IReadOnlyCollection<string> supportedAudioCodecs,
        CancellationToken cancellationToken)
    {
        try
        {
            var mediaInfo = await VideoMediaInfoProbe.ProbeAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            return VideoAudioCompatibilitySelector.Select(
                sourcePath,
                mediaInfo,
                new HashSet<string>(supportedAudioCodecs, StringComparer.Ordinal));
        }
        catch (Exception ex) when (
            ex is VideoMediaInfoProbeUnavailableException or VideoMediaInfoProbeException)
        {
            Logger.Warning(
                "WebServer",
                $"Could not inspect Local Video audio codecs; falling back to stereo AAC: {ex.Message}");
            return VideoAudioCompatibilityMode.EncodeAac;
        }
    }

    private async Task<VideoAudioCompatibilityStream> PrepareProgressiveStreamAsync(
        string sourcePath,
        long sourceLength,
        DateTime sourceLastWriteUtc,
        string cacheKey,
        string operationKey,
        double durationSeconds,
        VideoAudioCompatibilityMode mode,
        ProgressiveVideoAudioOutput progressiveOutput,
        CancellationToken cancellationToken)
    {
        var progressMode = GetProgressMode(mode);
        try
        {
            string outputPath;
            try
            {
                outputPath = await PrepareCompatibleMp4Async(
                    sourcePath,
                    sourceLength,
                    sourceLastWriteUtc,
                    cacheKey,
                    operationKey,
                    durationSeconds,
                    mode,
                    cancellationToken,
                    progressiveOutput).ConfigureAwait(false);
            }
            catch (VideoAudioTranscodingException ex)
                when (mode == VideoAudioCompatibilityMode.CopyAudioToMp4
                    && !progressiveOutput.HasFirstMediaFragment)
            {
                Logger.Warning(
                    "WebServer",
                    $"Could not copy Local Video audio into the progressive compatibility MP4; retrying with stereo AAC: {ex.Message}");
                progressMode = GetProgressMode(VideoAudioCompatibilityMode.EncodeAac);
                outputPath = await PrepareCompatibleMp4Async(
                    sourcePath,
                    sourceLength,
                    sourceLastWriteUtc,
                    cacheKey,
                    operationKey,
                    durationSeconds,
                    VideoAudioCompatibilityMode.EncodeAac,
                    cancellationToken,
                    progressiveOutput).ConfigureAwait(false);
                mode = VideoAudioCompatibilityMode.EncodeAac;
            }

            if (!progressiveOutput.IsComplete)
                progressiveOutput.CompleteFromExistingFile(outputPath);
            return new VideoAudioCompatibilityStream(outputPath, mode);
        }
        catch (Exception ex)
        {
            progressiveOutput.Fail(ex);
            if (ex is not OperationCanceledException)
            {
                _transcodeProgress[operationKey] = new VideoAudioTranscodeProgress(
                    false,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    progressMode,
                    "The tablet-compatible Local Video conversion failed. Choose Play on Server and check the RemotePlay log.");
                _transcodeFailureExpirationsUtc[operationKey] =
                    DateTime.UtcNow + TranscodeFailureLifetime;
            }
            throw;
        }
    }

    private async Task<string> TranscodeAsync(
        string sourcePath,
        long sourceLength,
        DateTime sourceLastWriteUtc,
        string outputPath,
        string operationKey,
        double durationSeconds,
        VideoAudioCompatibilityMode mode,
        CancellationToken cancellationToken,
        ProgressiveVideoAudioOutput? progressiveOutput = null)
    {
        var knownDurationSeconds = double.IsFinite(durationSeconds) && durationSeconds > 0
            ? durationSeconds
            : (double?)null;
        var startedTimestamp = Stopwatch.GetTimestamp();
        var temporaryOutputPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        string? finalizedOutputTemporaryPath = null;
        var retainProgressiveOutput = false;

        try
        {
            var progressMode = GetProgressMode(mode);
            _transcodeProgress[operationKey] = CalculateProgress(knownDurationSeconds, 0, 0, progressMode);
            Directory.CreateDirectory(_cacheDirectory);
            if (progressiveOutput is not null)
                await progressiveOutput.BeginAttemptAsync(temporaryOutputPath).ConfigureAwait(false);

            using var process = new Process
            {
                StartInfo = _processStartInfoFactory(
                    sourcePath,
                    temporaryOutputPath,
                    mode,
                    progressiveOutput is not null),
            };

            try
            {
                if (!process.Start())
                    throw new VideoAudioTranscodingException("FFmpeg did not start the audio-compatible conversion.");
            }
            catch (Win32Exception ex)
            {
                throw new VideoAudioTranscoderUnavailableException(
                    "FFmpeg is not available on the RemotePlay host. Install FFmpeg and restart RemotePlay to play Local video on this touch device.",
                    ex);
            }

            Task mediaOutputTask = Task.CompletedTask;
            Task progressTask;
            Task<string> standardErrorTask;
            if (progressiveOutput is not null)
            {
                mediaOutputTask = PumpMediaOutputAsync(
                    process.StandardOutput.BaseStream,
                    progressiveOutput,
                    cancellationToken);
                progressTask = Task.CompletedTask;
                standardErrorTask = ReadProgressAndCollectErrorsAsync(
                    process.StandardError,
                    operationKey,
                    knownDurationSeconds,
                    progressMode,
                    startedTimestamp);
            }
            else
            {
                standardErrorTask = process.StandardError.ReadToEndAsync();
                progressTask = ReadProgressAsync(
                    process.StandardOutput,
                    operationKey,
                    knownDurationSeconds,
                    progressMode,
                    startedTimestamp);
            }

            try
            {
                var waitForExitTask = process.WaitForExitAsync(cancellationToken);
                if (progressiveOutput is not null
                    && await Task.WhenAny(waitForExitTask, mediaOutputTask).ConfigureAwait(false) == mediaOutputTask)
                {
                    await mediaOutputTask.ConfigureAwait(false);
                }
                await waitForExitTask.ConfigureAwait(false);
                await Task.WhenAll(mediaOutputTask, progressTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StopProcess(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(mediaOutputTask, standardErrorTask, progressTask).ConfigureAwait(false);
                throw;
            }
            catch
            {
                StopProcess(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(mediaOutputTask, standardErrorTask, progressTask).ConfigureAwait(false);
                throw;
            }

            var standardError = await standardErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = standardError.Trim();
                if (detail.Length > 2000)
                    detail = detail[^2000..];
                throw new VideoAudioTranscodingException(
                    $"FFmpeg exited with code {process.ExitCode}. {detail}");
            }

            var currentSource = new FileInfo(sourcePath);
            currentSource.Refresh();
            if (!currentSource.Exists
                || currentSource.Length != sourceLength
                || currentSource.LastWriteTimeUtc != sourceLastWriteUtc)
            {
                throw new VideoAudioTranscodingException(
                    "The source video changed while its tablet-compatible stream was being prepared.");
            }

            if (progressiveOutput is not null)
            {
                if (!progressiveOutput.HasFirstMediaFragment)
                    throw new VideoAudioTranscodingException(
                        "FFmpeg completed without producing a playable fragmented MP4.");
                await progressiveOutput.CloseWriterAsync().ConfigureAwait(false);
            }

            if (!IsNonEmptyFile(temporaryOutputPath))
                throw new VideoAudioTranscodingException("FFmpeg completed without producing a usable MP4 file.");

            if (progressiveOutput is not null)
            {
                finalizedOutputTemporaryPath =
                    outputPath + "." + Guid.NewGuid().ToString("N") + ".partial";
                await FinalizeProgressiveCacheAsync(
                    temporaryOutputPath,
                    finalizedOutputTemporaryPath,
                    operationKey,
                    knownDurationSeconds,
                    cancellationToken).ConfigureAwait(false);

                if (!IsNonEmptyFile(finalizedOutputTemporaryPath))
                    throw new VideoAudioTranscodingException(
                        "FFmpeg completed without producing a seekable Local Video cache file.");

                File.Move(finalizedOutputTemporaryPath, outputPath, overwrite: true);
                WriteReadyMarker(outputPath);
                progressiveOutput.Commit(temporaryOutputPath, DeletePartialFile);
                retainProgressiveOutput = true;
            }
            else
            {
                File.Move(temporaryOutputPath, outputPath, overwrite: true);
                WriteReadyMarker(outputPath);
            }

            return outputPath;
        }
        finally
        {
            if (progressiveOutput is null)
                _transcodeProgress.TryRemove(operationKey, out _);
            try
            {
                if (progressiveOutput is not null)
                    await progressiveOutput.CloseWriterAsync().ConfigureAwait(false);
            }
            finally
            {
                if (finalizedOutputTemporaryPath is not null)
                    DeletePartialFile(finalizedOutputTemporaryPath);
                if (!retainProgressiveOutput)
                    DeletePartialFile(temporaryOutputPath);
            }
        }
    }

    private async Task FinalizeProgressiveCacheAsync(
        string fragmentedOutputPath,
        string finalizedOutputPath,
        string operationKey,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = _processStartInfoFactory(
                fragmentedOutputPath,
                finalizedOutputPath,
                VideoAudioCompatibilityMode.CopyAudioToMp4,
                false),
        };

        try
        {
            if (!process.Start())
                throw new VideoAudioTranscodingException("FFmpeg did not start finalizing the Local Video cache.");
        }
        catch (Win32Exception ex)
        {
            throw new VideoAudioTranscoderUnavailableException(
                "FFmpeg is not available on the RemotePlay host. Install FFmpeg and restart RemotePlay to prepare a seekable Local Video cache.",
                ex);
        }

        var progressTask = ReadProgressAsync(
            process.StandardOutput,
            operationKey,
            durationSeconds,
            "finalizing",
            Stopwatch.GetTimestamp());
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(progressTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }
        catch
        {
            StopProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(progressTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }

        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = standardError.Trim();
            if (detail.Length > 2000)
                detail = detail[^2000..];
            throw new VideoAudioTranscodingException(
                $"FFmpeg exited with code {process.ExitCode} while finalizing the seekable Local Video cache. {detail}");
        }
    }

    private async Task<string> PrepareCompatibleMp4Async(
        string sourcePath,
        long sourceLength,
        DateTime sourceLastWriteUtc,
        string cacheKey,
        string operationKey,
        double durationSeconds,
        VideoAudioCompatibilityMode mode,
        CancellationToken cancellationToken,
        ProgressiveVideoAudioOutput? progressiveOutput = null)
    {
        var outputPath = GetCompatibleOutputPath(cacheKey, mode);
        await PurgeExpiredCacheFilesAsync(cancellationToken).ConfigureAwait(false);
        if (IsReadyFile(outputPath))
        {
            progressiveOutput?.CompleteFromExistingFile(outputPath);
            return outputPath;
        }

        return await TranscodeAsync(
            sourcePath,
            sourceLength,
            sourceLastWriteUtc,
            outputPath,
            operationKey,
            durationSeconds,
            mode,
            cancellationToken,
            progressiveOutput).ConfigureAwait(false);
    }

    private async Task ReadProgressAsync(
        StreamReader progressReader,
        string operationKey,
        double? durationSeconds,
        string mode,
        long startedTimestamp)
    {
        var processedSeconds = 0d;
        var reportedSpeed = 0d;
        string? line;
        while ((line = await progressReader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            TryProcessProgressLine(
                line,
                operationKey,
                durationSeconds,
                mode,
                startedTimestamp,
                ref processedSeconds,
                ref reportedSpeed);
        }
    }

    private async Task<string> ReadProgressAndCollectErrorsAsync(
        StreamReader progressAndErrorReader,
        string operationKey,
        double? durationSeconds,
        string mode,
        long startedTimestamp)
    {
        var processedSeconds = 0d;
        var reportedSpeed = 0d;
        var errors = new StringBuilder();
        string? line;
        while ((line = await progressAndErrorReader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (TryProcessProgressLine(
                line,
                operationKey,
                durationSeconds,
                mode,
                startedTimestamp,
                ref processedSeconds,
                ref reportedSpeed))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
                errors.AppendLine(line);
        }

        return errors.ToString();
    }

    private bool TryProcessProgressLine(
        string line,
        string operationKey,
        double? durationSeconds,
        string mode,
        long startedTimestamp,
        ref double processedSeconds,
        ref double reportedSpeed)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
            return false;

        var key = line[..separator];
        var value = line[(separator + 1)..].Trim();
        if (key == "out_time_us"
            && TryParseOutputTimeMicroseconds(value, out var parsedSeconds))
        {
            processedSeconds = Math.Max(processedSeconds, parsedSeconds);
        }
        else if (key == "speed"
            && TryParseSpeedMultiplier(value, out var parsedSpeed))
        {
            reportedSpeed = parsedSpeed;
        }
        else if (key == "progress")
        {
            if (value == "end" && durationSeconds.HasValue)
                processedSeconds = Math.Max(processedSeconds, durationSeconds.Value);

            var elapsedSeconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds;
            var speed = reportedSpeed > 0
                ? reportedSpeed
                : elapsedSeconds > 0 ? processedSeconds / elapsedSeconds : 0;
            _transcodeProgress[operationKey] = CalculateProgress(
                durationSeconds,
                processedSeconds,
                speed,
                mode);
        }

        return key is "frame"
            or "fps"
            or "stream_0_0_q"
            or "bitrate"
            or "total_size"
            or "out_time_us"
            or "out_time_ms"
            or "out_time"
            or "dup_frames"
            or "drop_frames"
            or "speed"
            or "progress";
    }

    private static async Task PumpMediaOutputAsync(
        Stream mediaOutput,
        ProgressiveVideoAudioOutput progressiveOutput,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await mediaOutput.ReadAsync(
            buffer.AsMemory(),
            cancellationToken).ConfigureAwait(false)) > 0)
        {
            await progressiveOutput.AppendAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetProgressMode(VideoAudioCompatibilityMode mode) =>
        mode == VideoAudioCompatibilityMode.CopyAudioToMp4 ? "copy" : "aac";

    private async Task PurgeExpiredCacheFilesAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastCacheCleanupUtc < CacheCleanupInterval)
            return;

        await _cacheCleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastCacheCleanupUtc < CacheCleanupInterval)
                return;

            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                var cutoff = now - CacheEntryLifetime;
                foreach (var path in Directory.EnumerateFiles(_cacheDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                            File.Delete(path);
                    }
                    catch (IOException ex)
                    {
                        Logger.Detail("WebServer", $"Could not remove an expired Local Video compatibility cache file: {ex.Message}");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Logger.Warning("WebServer", $"Could not remove an expired Local Video compatibility cache file: {ex.Message}");
                    }
                }
            }
            catch (IOException ex)
            {
                Logger.Warning("WebServer", $"Could not inspect the Local Video compatibility cache: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Warning("WebServer", $"Could not inspect the Local Video compatibility cache: {ex.Message}");
            }

            _lastCacheCleanupUtc = now;
        }
        finally
        {
            _cacheCleanupGate.Release();
        }
    }

    private static bool IsReadyFile(string path)
    {
        if (!IsNonEmptyFile(path))
            return false;

        var markerPath = GetReadyMarkerPath(path);
        return File.Exists(markerPath)
            && string.Equals(
                File.ReadAllText(markerPath, Encoding.ASCII),
                ReadyMarkerContents,
                StringComparison.Ordinal);
    }

    private static bool IsNonEmptyFile(string path)
    {
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    internal static string GetReadyMarkerPath(string path) => path + ".ready";

    private static void WriteReadyMarker(string outputPath)
    {
        var markerPath = GetReadyMarkerPath(outputPath);
        var temporaryMarkerPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var marker = new FileStream(
                temporaryMarkerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                var contents = Encoding.ASCII.GetBytes(ReadyMarkerContents);
                marker.Write(contents, 0, contents.Length);
                marker.Flush(flushToDisk: true);
            }
            File.Move(temporaryMarkerPath, markerPath, overwrite: true);
        }
        finally
        {
            DeletePartialFile(temporaryMarkerPath);
        }
    }

    private string GetCompatibleOutputPath(
        string cacheKey,
        VideoAudioCompatibilityMode mode) =>
        mode switch
        {
            VideoAudioCompatibilityMode.EncodeAac => Path.Combine(_cacheDirectory, cacheKey + ".mp4"),
            VideoAudioCompatibilityMode.CopyAudioToMp4 => Path.Combine(_cacheDirectory, cacheKey + ".copy.mp4"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static void TouchCacheFile(string path)
    {
        TouchCachePath(path);
        TouchCachePath(GetReadyMarkerPath(path));
    }

    private static void TouchCachePath(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException ex)
        {
            Logger.Detail("WebServer", $"Could not update Local Video compatibility cache age: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warning("WebServer", $"Could not update Local Video compatibility cache age: {ex.Message}");
        }
    }

    private static async Task WaitForRetiredProgressiveOperationAsync(
        TranscodeOperation<VideoAudioCompatibilityStream> operation)
    {
        try
        {
            var completedTask = await Task.WhenAny(operation.Task).ConfigureAwait(false);
            if (completedTask.IsFaulted)
                _ = completedTask.Exception;
        }
        finally
        {
            operation.Dispose();
        }
    }

    private static void DeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            Logger.Detail("WebServer", $"Could not remove an incomplete Local Video compatibility file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warning("WebServer", $"Could not remove an incomplete Local Video compatibility file: {ex.Message}");
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception ex)
        {
            Logger.Error("Could not stop FFmpeg after the Local Video stream was cancelled.", ex);
        }
        catch (NotSupportedException ex)
        {
            Logger.Error("Could not stop FFmpeg after the Local Video stream was cancelled.", ex);
        }
    }

    internal sealed class TranscodeOperation<TResult> : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Lazy<Task<TResult>> _task;
        private int _cancellationRequested;

        internal TranscodeOperation(
            Func<CancellationToken, Task<TResult>> transcode,
            CancellationToken serverCancellationToken,
            ProgressiveVideoAudioOutput? progressiveOutput = null,
            string? sourcePath = null)
        {
            _cancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
            ProgressiveOutput = progressiveOutput;
            SourcePath = sourcePath;
            _task = new Lazy<Task<TResult>>(
                () => transcode(_cancellationTokenSource.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal Task<TResult> Task => _task.Value;
        internal ProgressiveVideoAudioOutput? ProgressiveOutput { get; }
        internal string? SourcePath { get; }
        internal bool IsCancellationRequested =>
            Volatile.Read(ref _cancellationRequested) != 0
            || _cancellationTokenSource.IsCancellationRequested;
        internal bool IsCompleted => _task.IsValueCreated && _task.Value.IsCompleted;

        internal bool RequestCancellation()
        {
            if (IsCompleted)
                return false;

            Interlocked.Exchange(ref _cancellationRequested, 1);
            return true;
        }

        internal bool Cancel()
        {
            if (Task.IsCompleted)
                return false;

            Interlocked.Exchange(ref _cancellationRequested, 1);
            try
            {
                _cancellationTokenSource.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose() => _cancellationTokenSource.Dispose();
    }
}

internal sealed record VideoAudioTranscodeProgress(
    bool IsTranscoding,
    bool IsComplete,
    double? DurationSeconds,
    double? ProcessedSeconds,
    double? ProgressPercent,
    double? EstimatedRemainingSeconds,
    double? SpeedMultiplier,
    string? Mode = null,
    string? ErrorMessage = null);

internal sealed class VideoAudioTranscoderUnavailableException : Exception
{
    internal VideoAudioTranscoderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class VideoAudioTranscodingException : Exception
{
    internal VideoAudioTranscodingException(string message)
        : base(message)
    {
    }
}
