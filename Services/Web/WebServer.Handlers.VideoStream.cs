using System.IO;
using System.Net;

namespace RemotePlay;

internal sealed partial class WebServer
{
    private const int MaxEncodedVideoPathLength = 65536;
    private const int VideoStreamBufferSize = 81920;

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

        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        if (encodedPath.Length > MaxEncodedVideoPathLength)
        {
            TrySendResponse(ctx, 400, "text/plain", "Invalid path");
            return;
        }

        string filePath;
        try
        {
            filePath = WebPathHelpers.DecodePath(encodedPath);
        }
        catch (FormatException)
        {
            TrySendResponse(ctx, 400, "text/plain", "Invalid path");
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Invalid path");
            return;
        }

        string? linkPath = null;
        var encodedLinkPath = ctx.Request.QueryString["linkPath"];
        if (!string.IsNullOrWhiteSpace(encodedLinkPath))
        {
            if (encodedLinkPath.Length > MaxEncodedVideoPathLength)
            {
                TrySendResponse(ctx, 400, "text/plain", "Invalid link path");
                return;
            }

            try
            {
                linkPath = WebPathHelpers.DecodePath(encodedLinkPath);
            }
            catch (FormatException)
            {
                TrySendResponse(ctx, 400, "text/plain", "Invalid link path");
                return;
            }
        }

        bool authorized;
        try
        {
            authorized = IsAuthorizedVideoStreamPath(filePath, linkPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            TrySendResponse(ctx, 400, "text/plain", "Invalid path");
            return;
        }

        if (!authorized)
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }

        if (!_videoExtensions.Contains(Path.GetExtension(filePath)))
        {
            TrySendResponse(ctx, 415, "text/plain", "Unsupported media type");
            return;
        }

        FileStream source;
        try
        {
            source = new FileStream(
                filePath,
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
        var extension = Path.GetExtension(filePath);
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
