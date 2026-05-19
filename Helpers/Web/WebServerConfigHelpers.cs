using System.Collections.Generic;
using System.Linq;

namespace RemotePlay;

/// <summary>Pure helpers for building the extension and folder-name sets that the web server uses to classify files.</summary>
internal static class WebServerConfigHelpers
{
    /// <summary>
    /// Builds a case-insensitive extension set from <paramref name="values"/>.
    /// Each entry is normalised to start with a dot.
    /// Falls back to <paramref name="defaultExtensions"/> when <paramref name="values"/> is empty or null.
    /// </summary>
    public static HashSet<string> BuildExtensionSet(
        IEnumerable<string>? values,
        IEnumerable<string> defaultExtensions)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var extension = value.Trim();
            extensions.Add(extension.StartsWith('.') ? extension : "." + extension);
        }

        return extensions.Count > 0
            ? extensions
            : new HashSet<string>(defaultExtensions, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a case-insensitive name set from <paramref name="values"/>.
    /// Falls back to <paramref name="fallback"/> when <paramref name="values"/> is empty or null.
    /// </summary>
    public static HashSet<string> BuildNameSet(
        IEnumerable<string>? values,
        IEnumerable<string> fallback)
    {
        var names = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray() ?? [];

        return names.Length > 0
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(fallback, StringComparer.OrdinalIgnoreCase);
    }
}
