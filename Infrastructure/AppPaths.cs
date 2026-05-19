using System;
using System.IO;

namespace RemotePlay;

internal static class AppPaths
{
    private static readonly string _userDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemotePlay");

    public static string UserDataDirectory
    {
        get
        {
            try
            {
                Directory.CreateDirectory(_userDataDirectory);
            }
            catch
            {
                // Best-effort directory creation; callers will handle IO exceptions when writing files.
            }

            return _userDataDirectory;
        }
    }

    public static string ConfigFile => Path.Combine(UserDataDirectory, "remoteplay.json");
    public static string HistoryFile => Path.Combine(UserDataDirectory, "playback-history.json");

    /// <summary>Returns the per-IP playback history file path.
    /// Strips IPv4-mapped IPv6 prefix (<c>::ffff:</c>) and sanitizes the address for use as a filename.</summary>
    public static string HistoryFileForIp(string clientIp)
    {
        var normalized = clientIp.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)
            ? clientIp["::ffff:".Length..]
            : clientIp;
        // Replace characters that are illegal or awkward in file names
        var safe = normalized.Replace(':', '-').Replace('.', '-').Trim('-');
        if (string.IsNullOrEmpty(safe)) safe = "unknown";
        return Path.Combine(UserDataDirectory, $"playback-history-{safe}.json");
    }

    public static string ThumbnailCacheDirectory => Path.Combine(UserDataDirectory, "thumbnail-cache");
    public static string LibraryIndexCacheFile => Path.Combine(UserDataDirectory, "library-index.json");
    public static string FavoritesFile => Path.Combine(UserDataDirectory, "favorites.json");
    public static string LogFile => Path.Combine(UserDataDirectory, "remoteplay.log");
}
