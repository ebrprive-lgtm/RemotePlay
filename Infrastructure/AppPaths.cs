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
    public static string ThumbnailCacheDirectory => Path.Combine(UserDataDirectory, "thumbnail-cache");
    public static string LibraryIndexCacheFile => Path.Combine(UserDataDirectory, "library-index.json");
    public static string FavoritesFile => Path.Combine(UserDataDirectory, "favorites.json");
    public static string LogFile => Path.Combine(UserDataDirectory, "remoteplay.log");
}
