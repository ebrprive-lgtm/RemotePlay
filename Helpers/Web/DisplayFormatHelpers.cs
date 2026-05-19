using System.Text.RegularExpressions;

namespace RemotePlay;

/// <summary>Formatting helpers for display titles and time values shown in the web UI.</summary>
internal static class DisplayFormatHelpers
{
    private static readonly Regex YearPattern =
        new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex QualityTagPattern =
        new(@"\b(1080p|720p|2160p|4k|x264|x265|h264|h265|web[- ]?dl|webrip|bluray|brrip|dvdrip|hdrip|aac|dts)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MultiSpacePattern =
        new(@"\s+", RegexOptions.Compiled);

    /// <summary>Strips encoding metadata and formatting noise from a raw file-stem so it reads as a clean movie title.</summary>
    public static string CleanDisplayTitle(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var cleaned = name.Replace('.', ' ').Replace('_', ' ');
        cleaned = YearPattern.Replace(cleaned, string.Empty);
        cleaned = QualityTagPattern.Replace(cleaned, string.Empty);
        cleaned = MultiSpacePattern.Replace(cleaned, " ").Trim(' ', '-', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? name : cleaned;
    }

    /// <summary>Formats a duration in seconds as <c>H:MM:SS</c> (hours omitted when under one hour).</summary>
    public static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }
}
