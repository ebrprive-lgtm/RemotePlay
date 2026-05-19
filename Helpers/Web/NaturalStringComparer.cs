using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RemotePlay;

/// <summary>Compares strings in natural (human) sort order, so that "Episode 9" sorts before "Episode 10".</summary>
internal sealed class NaturalStringComparer : IComparer<string>
{
    private static readonly Regex DigitSplitRegex =
        new(@"(\d+)", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        var xParts = DigitSplitRegex.Split(x);
        var yParts = DigitSplitRegex.Split(y);

        var maxLen = Math.Max(xParts.Length, yParts.Length);
        for (int i = 0; i < maxLen; i++)
        {
            var xPart = i < xParts.Length ? xParts[i] : string.Empty;
            var yPart = i < yParts.Length ? yParts[i] : string.Empty;

            if (string.IsNullOrEmpty(xPart) && string.IsNullOrEmpty(yPart))
                continue;
            if (string.IsNullOrEmpty(xPart))
                return -1;
            if (string.IsNullOrEmpty(yPart))
                return 1;

            if (int.TryParse(xPart, out var xNum) && int.TryParse(yPart, out var yNum))
            {
                var numCmp = xNum.CompareTo(yNum);
                if (numCmp != 0) return numCmp;
            }
            else
            {
                var strCmp = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
                if (strCmp != 0) return strCmp;
            }
        }

        return 0;
    }
}
