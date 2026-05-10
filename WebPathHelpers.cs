using System.IO;
using System.Text;

namespace RemotePlay;

internal static class WebPathHelpers
{
    public static string EncodePath(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

    public static string DecodePath(string encodedPath) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));

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
