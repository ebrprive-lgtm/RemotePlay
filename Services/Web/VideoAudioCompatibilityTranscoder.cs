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

    private readonly string _cacheDirectory;
    private readonly string _ffmpegExecutable;
    private readonly object _transcodeTasksLock = new();
    private readonly Dictionary<string, TranscodeOperation<VideoAudioCompatibilityStream>> _transcodeTasks =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VideoAudioTranscodeProgress> _transcodeProgress = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheCleanupGate = new(1, 1);
    private DateTime _lastCacheCleanupUtc = DateTime.MinValue;

    internal VideoAudioCompatibilityTranscoder(
        string? cacheDirectory = null,
        string ffmpegExecutable = "ffmpeg")
    {
        _cacheDirectory = Path.GetFullPath(
            cacheDirectory ?? Path.Combine(Path.GetTempPath(), "RemotePlay", "VideoAudioCompatibility"));
        _ffmpegExecutable = string.IsNullOrWhiteSpace(ffmpegExecutable) ? "ffmpeg" : ffmpegExecutable;
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

    internal Task<VideoAudioCompatibilityStream> GetOrCreateCompatibleStreamAsync(
        string sourcePath,
        IReadOnlyCollection<string> supportedAudioCodecs,
        CancellationToken cancellationToken,
        double durationSeconds = 0) =>
        GetOrCreateCompatibleStreamAsync(
            sourcePath,
            supportedAudioCodecs,
            cancellationToken,
            durationSeconds,
            forceAac: false);

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
                    cancellationToken);
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

    internal bool CancelTranscode(string sourcePath)
    {
        var sourceFile = new FileInfo(Path.GetFullPath(sourcePath));
        sourceFile.Refresh();
        if (!sourceFile.Exists)
            throw new FileNotFoundException("The source video file was not found.", sourceFile.FullName);

        var cacheKey = CreateCacheKey(sourceFile.FullName, sourceFile.Length, sourceFile.LastWriteTimeUtc);
        TranscodeOperation<VideoAudioCompatibilityStream>[] operations;
        lock (_transcodeTasksLock)
        {
            var operationPrefix = cacheKey + ":";
            operations = _transcodeTasks
                .Where(entry => entry.Key.StartsWith(operationPrefix, StringComparison.Ordinal))
                .Select(entry => entry.Value)
                .ToArray();
        }

        var cancelled = false;
        foreach (var operation in operations)
            cancelled |= operation.Cancel();
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
                return progress;
        }
        else
        {
            var operationPrefix = cacheKey + ":";
            var activeProgress = _transcodeProgress.FirstOrDefault(entry =>
                entry.Key.StartsWith(operationPrefix, StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(activeProgress.Key))
                return activeProgress.Value;
        }

        if (supportedAudioCodecs is not null)
            return new VideoAudioTranscodeProgress(false, false, null, null, null, null, null);

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
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostats",
            "-stats_period", "0.5",
            "-progress", "pipe:1",
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

        arguments.AddRange(
        [
            "-sn",
            "-dn",
            "-movflags", "+faststart",
            "-f", "mp4",
            temporaryOutputPath,
        ]);

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

    private async Task<string> TranscodeAsync(
        string sourcePath,
        long sourceLength,
        DateTime sourceLastWriteUtc,
        string outputPath,
        string operationKey,
        double durationSeconds,
        VideoAudioCompatibilityMode mode,
        CancellationToken cancellationToken)
    {
        var knownDurationSeconds = double.IsFinite(durationSeconds) && durationSeconds > 0
            ? durationSeconds
            : (double?)null;
        var startedTimestamp = Stopwatch.GetTimestamp();
        var temporaryOutputPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".partial";

        try
        {
            var progressMode = GetProgressMode(mode);
            _transcodeProgress[operationKey] = CalculateProgress(knownDurationSeconds, 0, 0, progressMode);
            Directory.CreateDirectory(_cacheDirectory);
            using var process = new Process
            {
                StartInfo = CreateStartInfo(_ffmpegExecutable, sourcePath, temporaryOutputPath, mode),
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

            var standardErrorTask = process.StandardError.ReadToEndAsync();
            var progressTask = ReadProgressAsync(
                process.StandardOutput,
                operationKey,
                knownDurationSeconds,
                progressMode,
                startedTimestamp);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StopProcess(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(standardErrorTask, progressTask).ConfigureAwait(false);
                throw;
            }

            var standardError = await standardErrorTask.ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);
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

            if (!IsReadyFile(temporaryOutputPath))
                throw new VideoAudioTranscodingException("FFmpeg completed without producing a usable MP4 file.");

            File.Move(temporaryOutputPath, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            _transcodeProgress.TryRemove(operationKey, out _);
            DeletePartialFile(temporaryOutputPath);
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
        CancellationToken cancellationToken)
    {
        var outputPath = GetCompatibleOutputPath(cacheKey, mode);
        await PurgeExpiredCacheFilesAsync(cancellationToken).ConfigureAwait(false);
        if (IsReadyFile(outputPath))
            return outputPath;

        return await TranscodeAsync(
            sourcePath,
            sourceLength,
            sourceLastWriteUtc,
            outputPath,
            operationKey,
            durationSeconds,
            mode,
            cancellationToken).ConfigureAwait(false);
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
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

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
        if (!File.Exists(path))
            return false;

        return new FileInfo(path).Length > 0;
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

        internal TranscodeOperation(
            Func<CancellationToken, Task<TResult>> transcode,
            CancellationToken serverCancellationToken)
        {
            _cancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
            _task = new Lazy<Task<TResult>>(
                () => transcode(_cancellationTokenSource.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal Task<TResult> Task => _task.Value;

        internal bool Cancel()
        {
            if (Task.IsCompleted)
                return false;

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
    string? Mode = null);

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
