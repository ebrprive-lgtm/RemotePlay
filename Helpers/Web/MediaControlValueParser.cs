using System.Globalization;

namespace RemotePlay;

internal static class MediaControlValueParser
{
    public static bool TryParseDouble(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result);

    public static bool TryParseClampedDouble(string? value, double min, double max, out double result)
    {
        if (!TryParseDouble(value, out var parsed))
        {
            result = default;
            return false;
        }

        result = Math.Clamp(parsed, min, max);
        return true;
    }

    public static bool TryParseInteger(string? value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    /// <summary>Parses a query-string value as a non-negative integer; returns 0 if missing or invalid.</summary>
    public static int ReadNonNegativeInt(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;

    /// <summary>Parses a query-string value as a positive integer clamped to <paramref name="maxValue"/>; returns <paramref name="defaultValue"/> if missing or invalid.</summary>
    public static int ReadPositiveInt(string? value, int defaultValue, int maxValue)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            return defaultValue;

        return Math.Min(parsed, maxValue);
    }
}
