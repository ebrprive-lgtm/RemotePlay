using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace RemotePlay.Services;

/// <summary>
/// Pure file-comparison and version-reading helpers used by <see cref="AppUpdater"/>.
/// Extracted as a separate class so the logic can be unit-tested independently of
/// the update orchestration that touches Process and WPF Dispatcher.
/// </summary>
internal static class UpdateFileHelper
{
    /// <summary>
    /// Returns <c>true</c> when both files have the same length and the same SHA-256 hash.
    /// Returns <c>false</c> on any I/O error.
    /// </summary>
    public static bool FilesAreIdentical(string pathA, string pathB)
    {
        try
        {
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);

            // Quick check: different size = definitely different.
            if (infoA.Length != infoB.Length)
                return false;

            // Same size: compare SHA-256.
            using var sha = SHA256.Create();
            using var streamA = File.OpenRead(pathA);
            using var streamB = File.OpenRead(pathB);
            var hashA = sha.ComputeHash(streamA);
            sha.Initialize();
            var hashB = sha.ComputeHash(streamB);
            return hashA.AsSpan().SequenceEqual(hashB);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the trimmed first-line version string from <c>version.txt</c> inside
    /// <paramref name="directory"/>. Returns <c>null</c> when the file is absent or blank.
    /// </summary>
    public static string? ReadVersionFile(string directory)
    {
        var versionFile = Path.Combine(directory, "version.txt");
        if (!File.Exists(versionFile))
            return null;

        var text = File.ReadAllText(versionFile).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Returns the executing assembly's Major.Minor.Build version string.</summary>
    public static string GetAssemblyVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// Enumerates all files under <paramref name="sourcePath"/> and returns the pairs whose
    /// destination counterpart is missing or has different content.
    /// </summary>
    public static List<(string Source, string Destination)> CollectChangedFiles(
        string sourcePath, string targetPath)
    {
        var changed = new List<(string, string)>();

        foreach (var srcFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, srcFile);
            var dstFile = Path.Combine(targetPath, relative);

            if (!File.Exists(dstFile) || !FilesAreIdentical(srcFile, dstFile))
                changed.Add((srcFile, dstFile));
        }

        return changed;
    }
}
