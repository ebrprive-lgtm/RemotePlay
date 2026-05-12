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
}
