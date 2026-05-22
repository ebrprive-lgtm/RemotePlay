using System.IO;
using System.Text;

namespace RemotePlay;

internal static class WebPathHelpers
{
    public static string EncodePath(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

    public static string DecodePath(string encodedPath)
    {
        // Query-string parsing turns '+' → ' ' and may strip trailing '='.
        // Restore standard Base64 before decoding.
        var normalized = encodedPath
            .Replace(' ', '+')                          // undo query-string '+'-as-space
            .Replace('-', '+').Replace('_', '/');       // accept URL-safe Base64 too

        // Re-pad to a multiple of 4 if padding was stripped
        int pad = normalized.Length % 4;
        if (pad == 2) normalized += "==";
        else if (pad == 3) normalized += "=";

        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    public static bool IsUnderRoot(string path, string root)
    {
        var fullPath = NormalizePath(path);
        var fullRoot = NormalizePath(root);

        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVideoFile(string path, IReadOnlySet<string> videoExtensions) =>
        videoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
