using RemotePlay.Models;

namespace RemotePlay;

internal sealed partial class WebServer
{
    /// <summary>
    /// Returns (or lazily creates) the <see cref="PlaybackHistory"/> for the given client IP.
    /// Uses the shared local history for <c>127.0.0.1</c> / <c>::1</c> so the WPF player and
    /// localhost browser clients share a single history file.
    /// </summary>
    internal PlaybackHistory GetHistoryForIp(string clientIp)
    {
        if (string.IsNullOrWhiteSpace(clientIp)
            || clientIp == "127.0.0.1"
            || clientIp == "::1"
            || clientIp == "localhost")
            return _playbackHistory;

        return _perIpHistories.GetOrAdd(clientIp,
            ip => new PlaybackHistory(AppPaths.HistoryFileForIp(ip)));
    }

    /// <summary>
    /// Returns a progress map that merges the shared WPF history with the per-IP history.
    /// The per-IP entry wins when both have a record for the same path.
    /// This ensures remote browser clients see progress recorded by the local WPF player.
    /// </summary>
    private Dictionary<string, RecentPlaybackItem> GetMergedProgressMap(PlaybackHistory ipHistory)
    {
        var shared = _playbackHistory.GetProgressMap();
        if (ReferenceEquals(ipHistory, _playbackHistory))
            return shared;

        var perIp = ipHistory.GetProgressMap();
        // Start from shared, then overwrite/add with per-IP entries (per-IP wins).
        foreach (var (key, value) in perIp)
            shared[key] = value;
        return shared;
    }

    /// <summary>
    /// Returns a watched set that merges the shared WPF history with the per-IP history.
    /// </summary>
    private HashSet<string> GetMergedWatchedSet(PlaybackHistory ipHistory)
    {
        var shared = _playbackHistory.GetWatchedSet();
        if (ReferenceEquals(ipHistory, _playbackHistory))
            return shared;

        foreach (var path in ipHistory.GetWatchedSet())
            shared.Add(path);
        return shared;
    }
}
