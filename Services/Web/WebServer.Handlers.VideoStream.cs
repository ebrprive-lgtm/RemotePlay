using System.IO;
using System.Net;
using System.Text.Json;

namespace RemotePlay;

internal sealed partial class WebServer
{
    private const int MaxEncodedVideoPathLength = 65536;
    private const int VideoStreamBufferSize = 81920;
    private readonly VideoAudioCompatibilityTranscoder _videoAudioCompatibilityTranscoder = new();

    private static readonly Dictionary<string, string> _videoStreamMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".wmv"] = "video/x-ms-wmv",
        [".ts"] = "video/mp2t",
        [".flv"] = "video/x-flv",
    };

    private async Task HandleVideoStreamAsync(HttpListenerContext ctx)
    {
        CancellationToken cancellationToken;
        try
        {
            cancellationToken = _listenerCts.Token;
        }
        catch (ObjectDisposedException)
        {
            CloseVideoStreamResponse(ctx.Response);
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            CloseVideoStreamResponse(ctx.Response);
            return;
        }

        if (!ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Allow"] = "GET";
            TrySendResponse(ctx, 405, "text/plain", "Method Not Allowed");
            return;
        }

        if (!TryResolveAuthorizedVideoPath(
            ctx,
            out var filePath,
            out var linkPath,
            out var pathErrorStatus,
            out var pathError))
        {
            TrySendResponse(ctx, pathErrorStatus, "text/plain", pathError);
            return;
        }

        if (!_videoExtensions.Contains(Path.GetExtension(filePath)))
        {
            TrySendResponse(ctx, 415, "text/plain", "Unsupported media type");
            return;
        }

        var streamPath = filePath;
        var audioCompatibility = ctx.Request.QueryString["audioCompat"];
        if (string.Equals(audioCompatibility, "aac", StringComparison.OrdinalIgnoreCase)
            || string.Equals(audioCompatibility, "auto", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var durationSeconds = _videoDurationCache.TryGetValue(filePath, out var cachedDuration)
                    && cachedDuration > 0
                        ? cachedDuration
                        : ReadFileDurationSeconds(filePath);
                if (durationSeconds > 0)
                    _videoDurationCache[filePath] = durationSeconds;
                if (string.Equals(audioCompatibility, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    var supportedAudioCodecs = VideoAudioCompatibilitySelector.ParseSupportedCodecs(
                        ctx.Request.QueryString["audioCodecs"]);
                    var compatibleStream = await _videoAudioCompatibilityTranscoder
                        .GetOrCreateCompatibleStreamAsync(
                            filePath,
                            supportedAudioCodecs,
                            cancellationToken,
                            durationSeconds)
                        .ConfigureAwait(false);
                    streamPath = compatibleStream.Path;
                }
                else
                {
                    streamPath = await _videoAudioCompatibilityTranscoder
                        .GetOrCreateCompatibleMp4Async(filePath, cancellationToken, durationSeconds)
                        .ConfigureAwait(false);
                }
            }
            catch (VideoAudioTranscoderUnavailableException ex)
            {
                Logger.Error($"FFmpeg is required to prepare tablet-compatible Local Video audio for '{Path.GetFileName(filePath)}'.", ex);
                TrySendResponse(
                    ctx,
                    503,
                    "text/plain; charset=utf-8",
                    "FFmpeg is required on the RemotePlay host to prepare this tablet-compatible Local Video stream.");
                return;
            }
            catch (FileNotFoundException)
            {
                TrySendResponse(ctx, 404, "text/plain", "File not found");
                return;
            }
            catch (OperationCanceledException)
            {
                CloseVideoStreamResponse(ctx.Response);
                return;
            }
            catch (VideoAudioTranscodingException ex)
            {
                Logger.Error($"Failed to prepare tablet-compatible Local Video audio for '{Path.GetFileName(filePath)}'.", ex);
                TrySendResponse(
                    ctx,
                    500,
                    "text/plain; charset=utf-8",
                    "The tablet-compatible Local Video stream could not be prepared. Choose Play on Server and check the RemotePlay log.");
                return;
            }
            catch (IOException ex)
            {
                Logger.Error($"Failed to create the tablet-compatible Local Video stream for '{Path.GetFileName(filePath)}'.", ex);
                TrySendResponse(ctx, 500, "text/plain", "Unable to prepare audio-compatible video");
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Error($"Access denied while preparing tablet-compatible Local Video audio for '{Path.GetFileName(filePath)}'.", ex);
                TrySendResponse(ctx, 500, "text/plain", "Unable to prepare audio-compatible video");
                return;
            }
        }

        FileStream source;
        try
        {
            source = new FileStream(
                streamPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                VideoStreamBufferSize,
                FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }
        catch (DirectoryNotFoundException)
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }
        catch (IOException ex)
        {
            Logger.Error("Failed to open video stream source", ex);
            TrySendResponse(ctx, 500, "text/plain", "Unable to open video");
            return;
        }

        using var sourceStream = source;
        var fileLength = sourceStream.Length;
        var range = VideoByteRangeParser.Parse(ctx.Request.Headers["Range"], fileLength);

        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.Headers["Cache-Control"] = "no-store";

        if (range.Kind == VideoByteRangeKind.Unsatisfiable)
        {
            ctx.Response.Headers["Content-Range"] = $"bytes */{fileLength}";
            TrySendResponse(ctx, 416, "text/plain", "Range Not Satisfiable");
            return;
        }

        var isPartial = range.Kind == VideoByteRangeKind.PartialContent;
        var rangeStart = isPartial ? range.Start : 0;
        var rangeEnd = isPartial ? range.End : fileLength - 1;
        var contentLength = isPartial ? rangeEnd - rangeStart + 1 : fileLength;
        var extension = Path.GetExtension(streamPath);
        var contentType = _videoStreamMimeTypes.TryGetValue(extension, out var mimeType)
            ? mimeType
            : "application/octet-stream";

        try
        {
            sourceStream.Seek(rangeStart, SeekOrigin.Begin);
        }
        catch (IOException ex)
        {
            Logger.Error("Failed to seek video stream source", ex);
            TrySendResponse(ctx, 500, "text/plain", "Unable to seek video");
            return;
        }

        ctx.Response.StatusCode = isPartial ? 206 : 200;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = contentLength;
        ctx.Response.Headers["Content-Disposition"] = "inline";
        if (isPartial)
            ctx.Response.Headers["Content-Range"] = $"bytes {rangeStart}-{rangeEnd}/{fileLength}";

        try
        {
            var buffer = new byte[VideoStreamBufferSize];
            var remaining = contentLength;

            while (remaining > 0)
            {
                int read;
                try
                {
                    read = await sourceStream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.Detail("WebServer", "Video stream cancelled during server shutdown.");
                    return;
                }
                catch (IOException ex)
                {
                    Logger.Error("Failed to read video stream source", ex);
                    return;
                }

                if (read == 0)
                {
                    Logger.Warning("WebServer", $"Video stream source changed before all bytes were sent: '{Path.GetFileName(filePath)}'.");
                    return;
                }

                try
                {
                    await ctx.Response.OutputStream.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.Detail("WebServer", "Video stream cancelled during server shutdown.");
                    return;
                }
                catch (HttpListenerException)
                {
                    Logger.Detail("WebServer", "Video stream client disconnected.");
                    return;
                }
                catch (IOException ex)
                {
                    Logger.Detail("WebServer", $"Video stream client disconnected: {ex.Message}");
                    return;
                }
                catch (ObjectDisposedException)
                {
                    Logger.Detail("WebServer", "Video stream response closed by client.");
                    return;
                }

                remaining -= read;
            }
        }
        finally
        {
            CloseVideoStreamResponse(ctx.Response);
        }
    }

    private void HandleVideoTranscodeStatus(HttpListenerContext ctx)
    {
        if (!ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Allow"] = "GET";
            TrySendResponse(ctx, 405, "text/plain", "Method Not Allowed");
            return;
        }

        if (!TryResolveAuthorizedVideoPath(
            ctx,
            out var filePath,
            out _,
            out var pathErrorStatus,
            out var pathError))
        {
            TrySendResponse(ctx, pathErrorStatus, "text/plain", pathError);
            return;
        }

        if (!_videoExtensions.Contains(Path.GetExtension(filePath)))
        {
            TrySendResponse(ctx, 415, "text/plain", "Unsupported media type");
            return;
        }

        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }

        try
        {
            var audioCodecsValue = ctx.Request.QueryString["audioCodecs"];
            var supportedAudioCodecs = audioCodecsValue is null
                ? null
                : VideoAudioCompatibilitySelector.ParseSupportedCodecs(audioCodecsValue);
            var progress = _videoAudioCompatibilityTranscoder.GetProgress(
                filePath,
                supportedAudioCodecs);
            TrySendResponse(
                ctx,
                200,
                "application/json; charset=utf-8",
                JsonSerializer.Serialize(progress, _camelCaseJson));
        }
        catch (FileNotFoundException)
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error($"Access denied while reading Local Video transcode status for '{Path.GetFileName(filePath)}'.", ex);
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
        }
        catch (IOException ex)
        {
            Logger.Error($"Could not read Local Video transcode status for '{Path.GetFileName(filePath)}'.", ex);
            TrySendResponse(ctx, 500, "text/plain", "Unable to read video transcode status");
        }
    }

    private void HandleVideoTranscodeCancel(HttpListenerContext ctx)
    {
        if (!ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Allow"] = "POST";
            TrySendResponse(ctx, 405, "text/plain", "Method Not Allowed");
            return;
        }

        if (!TryResolveAuthorizedVideoPath(
            ctx,
            out var filePath,
            out _,
            out var pathErrorStatus,
            out var pathError))
        {
            TrySendResponse(ctx, pathErrorStatus, "text/plain", pathError);
            return;
        }

        if (!_videoExtensions.Contains(Path.GetExtension(filePath)))
        {
            TrySendResponse(ctx, 415, "text/plain", "Unsupported media type");
            return;
        }

        try
        {
            var cancelled = _videoAudioCompatibilityTranscoder.CancelTranscode(filePath);
            TrySendResponse(
                ctx,
                200,
                "application/json; charset=utf-8",
                JsonSerializer.Serialize(new { cancelled }, _camelCaseJson));
        }
        catch (FileNotFoundException)
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error($"Access denied while cancelling Local Video transcode for '{Path.GetFileName(filePath)}'.", ex);
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
        }
        catch (IOException ex)
        {
            Logger.Error($"Could not cancel Local Video transcode for '{Path.GetFileName(filePath)}'.", ex);
            TrySendResponse(ctx, 500, "text/plain", "Unable to cancel video transcode");
        }
    }

    private async Task HandleVideoCodecInfoAsync(HttpListenerContext ctx)
    {
        CancellationToken cancellationToken;
        try
        {
            cancellationToken = _listenerCts.Token;
        }
        catch (ObjectDisposedException)
        {
            CloseVideoStreamResponse(ctx.Response);
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            CloseVideoStreamResponse(ctx.Response);
            return;
        }

        if (!ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Allow"] = "GET";
            TrySendResponse(ctx, 405, "text/plain", "Method Not Allowed");
            return;
        }

        if (!TryResolveAuthorizedVideoPath(
            ctx,
            out var filePath,
            out _,
            out var pathErrorStatus,
            out var pathError))
        {
            TrySendResponse(ctx, pathErrorStatus, "text/plain", pathError);
            return;
        }

        if (!_videoExtensions.Contains(Path.GetExtension(filePath)))
        {
            TrySendResponse(ctx, 415, "text/plain", "Unsupported media type");
            return;
        }

        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }

        try
        {
            var mediaInfo = await VideoMediaInfoProbe
                .ProbeAsync(filePath, cancellationToken)
                .ConfigureAwait(false);
            TrySendResponse(
                ctx,
                200,
                "application/json; charset=utf-8",
                JsonSerializer.Serialize(mediaInfo, _camelCaseJson));
        }
        catch (VideoMediaInfoProbeUnavailableException ex)
        {
            Logger.Warning("WebServer", $"FFprobe is required for Local Video track diagnostics: {ex.Message}");
            TrySendResponse(
                ctx,
                503,
                "text/plain; charset=utf-8",
                "FFprobe is required on the RemotePlay host to inspect Local Video tracks.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CloseVideoStreamResponse(ctx.Response);
        }
        catch (VideoMediaInfoProbeException ex)
        {
            Logger.Error($"Could not inspect Local Video tracks for '{Path.GetFileName(filePath)}'.", ex);
            TrySendResponse(ctx, 500, "text/plain; charset=utf-8", "Unable to read media track information");
        }
        catch (IOException ex)
        {
            Logger.Error($"Could not access Local Video tracks for '{Path.GetFileName(filePath)}'.", ex);
            TrySendResponse(ctx, 500, "text/plain; charset=utf-8", "Unable to read media track information");
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error($"Access denied while reading Local Video tracks for '{Path.GetFileName(filePath)}'.", ex);
            TrySendResponse(ctx, 403, "text/plain; charset=utf-8", "Forbidden");
        }
    }

    private bool TryResolveAuthorizedVideoPath(
        HttpListenerContext ctx,
        out string filePath,
        out string? linkPath,
        out int errorStatus,
        out string errorMessage)
    {
        filePath = string.Empty;
        linkPath = null;
        errorStatus = 400;
        errorMessage = "Invalid path";

        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            errorMessage = "Missing path";
            return false;
        }

        if (encodedPath.Length > MaxEncodedVideoPathLength)
            return false;

        try
        {
            filePath = WebPathHelpers.DecodePath(encodedPath);
        }
        catch (FormatException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var encodedLinkPath = ctx.Request.QueryString["linkPath"];
        if (!string.IsNullOrWhiteSpace(encodedLinkPath))
        {
            if (encodedLinkPath.Length > MaxEncodedVideoPathLength)
            {
                errorMessage = "Invalid link path";
                return false;
            }

            try
            {
                linkPath = WebPathHelpers.DecodePath(encodedLinkPath);
            }
            catch (FormatException)
            {
                errorMessage = "Invalid link path";
                return false;
            }
        }

        bool authorized;
        try
        {
            authorized = IsAuthorizedVideoStreamPath(filePath, linkPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!authorized)
        {
            errorStatus = 403;
            errorMessage = "Forbidden";
            return false;
        }

        return true;
    }

    private bool IsAuthorizedVideoStreamPath(string filePath, string? linkPath)
    {
        if (linkPath is null)
            return IsUnderConfiguredMovieRoot(filePath);

        if (!RplinkHelper.IsRplinkFile(linkPath)
            || !IsUnderConfiguredMovieRoot(linkPath)
            || !File.Exists(linkPath))
            return false;

        var targetPath = RplinkHelper.TryReadTarget(linkPath);
        if (targetPath is null)
            return false;

        return Directory.Exists(targetPath)
            ? WebPathHelpers.IsUnderRoot(filePath, targetPath)
            : PathsEqual(filePath, targetPath);
    }

    private bool IsUnderConfiguredMovieRoot(string path)
    {
        foreach (var root in _config.AllResolvedMoviesPaths)
        {
            if (!string.IsNullOrWhiteSpace(root) && WebPathHelpers.IsUnderRoot(path, root))
                return true;
        }

        return false;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static void CloseVideoStreamResponse(HttpListenerResponse response)
    {
        try
        {
            response.Close();
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException ex)
        {
            Logger.Detail("WebServer", $"Video stream response closed: {ex.Message}");
        }
    }
}
