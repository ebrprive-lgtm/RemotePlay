using System.Globalization;

namespace RemotePlay;

internal enum VideoByteRangeKind
{
    FullContent,
    PartialContent,
    Unsatisfiable,
}

internal readonly record struct VideoByteRangeResult(
    VideoByteRangeKind Kind,
    long Start,
    long End);

internal static class VideoByteRangeParser
{
    public static VideoByteRangeResult Parse(string? header, long fileLength)
    {
        if (fileLength < 0)
            throw new ArgumentOutOfRangeException(nameof(fileLength));

        if (string.IsNullOrWhiteSpace(header)
            || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return FullContent();

        var range = header[6..].Trim();
        if (range.Length == 0 || range.Contains(','))
            return FullContent();

        var separator = range.IndexOf('-');
        if (separator < 0 || separator != range.LastIndexOf('-'))
            return FullContent();

        var first = range[..separator].Trim();
        var last = range[(separator + 1)..].Trim();

        if (first.Length == 0)
        {
            if (!TryParseNonNegative(last, out var suffixLength))
                return FullContent();
            if (suffixLength == 0 || fileLength == 0)
                return Unsatisfiable();

            var start = suffixLength >= fileLength ? 0 : fileLength - suffixLength;
            return PartialContent(start, fileLength - 1);
        }

        if (!TryParseNonNegative(first, out var rangeStart))
            return FullContent();

        if (last.Length == 0)
        {
            if (fileLength == 0 || rangeStart >= fileLength)
                return Unsatisfiable();

            return PartialContent(rangeStart, fileLength - 1);
        }

        if (!TryParseNonNegative(last, out var requestedEnd) || requestedEnd < rangeStart)
            return FullContent();
        if (fileLength == 0 || rangeStart >= fileLength)
            return Unsatisfiable();

        return PartialContent(rangeStart, Math.Min(requestedEnd, fileLength - 1));
    }

    private static bool TryParseNonNegative(string value, out long result) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static VideoByteRangeResult FullContent() =>
        new(VideoByteRangeKind.FullContent, 0, 0);

    private static VideoByteRangeResult PartialContent(long start, long end) =>
        new(VideoByteRangeKind.PartialContent, start, end);

    private static VideoByteRangeResult Unsatisfiable() =>
        new(VideoByteRangeKind.Unsatisfiable, 0, 0);
}
