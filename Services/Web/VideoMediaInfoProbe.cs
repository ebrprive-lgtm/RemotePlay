using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemotePlay.Models;

namespace RemotePlay;

internal static class VideoMediaInfoProbe
{
    internal static ProcessStartInfo CreateStartInfo(string ffprobeExecutable, string sourcePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobeExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(sourcePath);
        return startInfo;
    }

    internal static async Task<MediaCodecInfo> ProbeAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo("ffprobe", sourcePath),
        };

        try
        {
            if (!process.Start())
                throw new VideoMediaInfoProbeException("FFprobe did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new VideoMediaInfoProbeUnavailableException(
                "FFprobe is not available on the RemotePlay host. Install FFmpeg (including FFprobe) and try again.",
                ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = error.Trim();
            if (detail.Length > 2000)
                detail = detail[^2000..];
            throw new VideoMediaInfoProbeException(
                $"FFprobe exited with code {process.ExitCode}. {detail}");
        }

        try
        {
            return Parse(sourcePath, output);
        }
        catch (JsonException ex)
        {
            throw new VideoMediaInfoProbeException("FFprobe returned invalid media information.", ex);
        }
    }

    internal static MediaCodecInfo Parse(string sourcePath, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamArray)
            && streamArray.ValueKind == JsonValueKind.Array
                ? streamArray.EnumerateArray().ToArray()
                : [];
        var format = root.TryGetProperty("format", out var formatInfo)
            ? ReadString(formatInfo, "format_name")
            : string.Empty;

        var videoTracks = streams
            .Where(stream => ReadString(stream, "codec_type") == "video")
            .Select(stream =>
            {
                var width = ReadInt(stream, "width");
                var height = ReadInt(stream, "height");
                var codec = ReadString(stream, "codec_name");
                return new VideoTrackInfo
                {
                    Id = ReadInt(stream, "index"),
                    Codec = codec,
                    CodecDescription = ReadString(stream, "codec_long_name") is { Length: > 0 } description
                        ? description
                        : codec,
                    Width = width,
                    Height = height,
                    FrameRate = FormatFrameRate(
                        ReadString(stream, "avg_frame_rate"),
                        ReadString(stream, "r_frame_rate")),
                    AspectRatio = ReadOptionalString(stream, "sample_aspect_ratio"),
                    Orientation = width > 0 && height > 0
                        ? width >= height ? "Landscape" : "Portrait"
                        : string.Empty,
                    Language = ReadTag(stream, "language"),
                    Description = ReadTag(stream, "title"),
                };
            })
            .ToArray();

        var audioTracks = streams
            .Where(stream => ReadString(stream, "codec_type") == "audio")
            .Select(stream =>
            {
                var channels = ReadInt(stream, "channels");
                var codec = ReadString(stream, "codec_name");
                var channelLayout = ReadOptionalString(stream, "channel_layout");
                return new AudioTrackInfo
                {
                    Id = ReadInt(stream, "index"),
                    Codec = codec,
                    CodecDescription = ReadString(stream, "codec_long_name") is { Length: > 0 } description
                        ? description
                        : codec,
                    Profile = ReadString(stream, "profile"),
                    Channels = channels,
                    ChannelLayout = channelLayout.Length > 0 ? channelLayout : channels > 0 ? $"{channels} ch" : string.Empty,
                    SampleRate = ReadInt(stream, "sample_rate"),
                    Language = ReadTag(stream, "language"),
                    Description = ReadTag(stream, "title"),
                };
            })
            .ToArray();

        var subtitleTracks = streams
            .Where(stream => ReadString(stream, "codec_type") == "subtitle")
            .Select(stream =>
            {
                var codec = ReadString(stream, "codec_name");
                return new SubtitleTrackInfo
                {
                    Id = ReadInt(stream, "index"),
                    Codec = codec,
                    CodecDescription = ReadString(stream, "codec_long_name") is { Length: > 0 } description
                        ? description
                        : codec,
                    Language = ReadTag(stream, "language"),
                    Description = ReadTag(stream, "title"),
                    Encoding = codec,
                };
            })
            .ToArray();

        return new MediaCodecInfo
        {
            FilePath = Path.GetFullPath(sourcePath),
            FileName = Path.GetFileName(sourcePath),
            ContainerFormat = format.Length > 0
                ? format
                : Path.GetExtension(sourcePath).TrimStart('.').ToUpperInvariant(),
            TotalTracks = streams.Length,
            CapturedAtUtc = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
            VideoTracks = videoTracks,
            AudioTracks = audioTracks,
            SubtitleTracks = subtitleTracks,
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return string.Empty;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty,
        };
    }

    private static int ReadInt(JsonElement element, string propertyName) =>
        int.TryParse(
            ReadString(element, propertyName),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : 0;

    private static string ReadOptionalString(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
    }

    private static string ReadTag(JsonElement element, string tagName)
    {
        if (!element.TryGetProperty("tags", out var tags))
            return string.Empty;

        return ReadString(tags, tagName);
    }

    private static string FormatFrameRate(string averageFrameRate, string frameRate)
    {
        var value = FormatRate(averageFrameRate);
        if (value <= 0)
            value = FormatRate(frameRate);
        return value > 0
            ? $"{Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture)} fps"
            : string.Empty;
    }

    private static double FormatRate(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || denominator <= 0)
            return 0;

        return numerator / denominator;
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
            Logger.Error("Could not stop FFprobe after a Local Video diagnostics request was cancelled.", ex);
        }
        catch (NotSupportedException ex)
        {
            Logger.Error("Could not stop FFprobe after a Local Video diagnostics request was cancelled.", ex);
        }
    }
}

internal sealed class VideoMediaInfoProbeUnavailableException : Exception
{
    internal VideoMediaInfoProbeUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class VideoMediaInfoProbeException : Exception
{
    internal VideoMediaInfoProbeException(string message)
        : base(message)
    {
    }

    internal VideoMediaInfoProbeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
