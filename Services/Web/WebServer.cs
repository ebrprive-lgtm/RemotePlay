using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using QRCoder;
using RemotePlay.Models;
using RemotePlay.Services.Discovery;
using System.Diagnostics.CodeAnalysis;
using Timer = System.Threading.Timer;

namespace RemotePlay;

[ExcludeFromCodeCoverage]
internal sealed record PlaybackStatus
{
    public bool IsPlaying { get; init; }
    public bool IsPaused  { get; init; }
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public string Title { get; init; } = string.Empty;
    public double Volume { get; init; } = 1;
    public bool IsMuted { get; init; }
    public string LastError { get; init; } = string.Empty;
    public bool CanResume { get; init; }
    public double ResumePositionSeconds { get; init; }
    public bool SmartResumeApplied { get; init; }
    public double Brightness { get; init; }
    public double Saturation { get; init; } = 1;
    public double Zoom { get; init; } = 1;
    public double AudioBoost { get; init; } = 1;
    public double PlaybackSpeed { get; init; } = 1;
    public bool SubtitlesEnabled { get; init; }
    public bool HasSubtitles { get; init; }
    public TrackOption[] AudioTracks { get; init; } = [];
    public TrackOption[] SubtitleTracks { get; init; } = [];
    public int CurrentAudioTrackId { get; init; }
    public int CurrentSubtitleTrackId { get; init; } = -1;
    public string? PreviousTitle { get; init; }
    public string? NextTitle { get; init; }
    public string? FilePath { get; init; }
    public PlaybackQueueItem[] Queue { get; init; } = [];
    public int QueueCount { get; init; }
}

[ExcludeFromCodeCoverage]
internal sealed record TrackOption(int Id, string Name, string Language = "", bool IsForced = false, bool IsDefault = false);

[ExcludeFromCodeCoverage]
internal sealed record PlaybackQueueItem(string Path, string Title);

[ExcludeFromCodeCoverage]
internal sealed record LibraryScanStatus
{
    public bool IsScanning { get; init; }
    public int IndexedFiles { get; init; }
    public int IndexedMovies { get; init; }
    public int IndexedLinks { get; init; }
    public int ScannedFiles { get; init; }
    public int ScannedFolders { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string LastError { get; init; } = string.Empty;
    /// <summary>Number of .rplink files found to have missing targets during the last stale-link background check.</summary>
    public int StaleLinkCount { get; init; }
}

[ExcludeFromCodeCoverage]
internal sealed record ThumbnailQueueStatus
{
    public bool IsRunning { get; init; }
    public int Total { get; init; }
    public int Processed { get; init; }
    public int Generated { get; init; }
    public int Cached { get; init; }
    public string CurrentTitle { get; init; } = string.Empty;
    public string LastError { get; init; } = string.Empty;
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
}

[ExcludeFromCodeCoverage]
internal sealed record LibraryIndexCache
{
    public string RootPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public LibraryFile[] Files { get; init; } = [];
}

[ExcludeFromCodeCoverage]
internal sealed record MusicIndexCache
{
    public string RootPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public MusicFile[] Files { get; init; } = [];
}

[ExcludeFromCodeCoverage]
internal sealed record LibraryFile(
    string Name,
    string FilePath,
    string EncodedPath,
    string FolderName,
    string SearchText,
    long SizeBytes = 0,
    DateTime LastWriteUtc = default,
    bool IsLink = false,
    string? LinkSourcePath = null,
    bool IsFolderLink = false);

[ExcludeFromCodeCoverage]
internal sealed partial class WebServer
{
    private const string WebAssetsDirectoryName = "WebAssets";
    private static readonly string ThumbnailCacheDirectory = AppPaths.ThumbnailCacheDirectory;
    private static readonly string LibraryIndexCacheFile = AppPaths.LibraryIndexCacheFile;
    private static readonly string MusicIndexCacheFile   = AppPaths.MusicIndexCacheFile;
    private static readonly string FavoritesFile = AppPaths.FavoritesFile;

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv" };
    private static readonly HashSet<string> HiddenFolderNames =
        new(StringComparer.OrdinalIgnoreCase) { "Subs", "Alt" };
    private static readonly Guid HttpsCertificateAppId = new("be2a8b40-850e-4ef1-a893-b0b13f5c7fd9");
    private const string HttpsCertificateSubject = "CN=RemotePlay Local HTTPS";

    private static readonly JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions IndentedOptions =
        new() { WriteIndented = true };

    private static readonly Lazy<string> _cachedHtmlPage =
        new(() => LoadWebAsset("index.html"));
    private static readonly Lazy<string> _cachedStylesCss =
        new(() => LoadWebAsset("styles.css"));
    private static readonly string[] _appJsModules =
    [
        "app-core.js",
        "app-diagnostics.js",
        "app-playback.js",
        "app-library.js",
        "app-radio.js",
        "app-globe.js",
        "app-radio-status.js",
        "app-local.js",
    ];
    private static readonly Lazy<string> _cachedAppJs =
        new(() => string.Join("\n", _appJsModules.Select(f => LoadWebAsset(f))));
    private static readonly Lazy<string> _cachedServiceWorkerJsRaw =
        new(() => LoadWebAsset("service-worker.js"));
    private static readonly Lazy<string> _cachedWorld110m =
        new(() => LoadWebAsset("world-110m.json"));
    private static readonly Lazy<string> _cachedWorld50m =
        new(() => LoadWebAsset("world-50m.json"));
    private static readonly Lazy<string> _cachedUsStates =
        new(() => LoadWebAsset("us-states.json"));
    private static readonly string _appVersion = ReadAppVersion();

    private readonly AppConfig _config;
    private readonly WebServerCallbacks _callbacks;
    private readonly PresenceBroadcaster? _broadcaster;
    private readonly PlaybackHistory _playbackHistory;
    // Per-IP histories: keyed on sanitized client IP string, lazy-created on first request.
    private readonly ConcurrentDictionary<string, PlaybackHistory> _perIpHistories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _videoExtensions;
    private readonly HashSet<string> _musicExtensions;
    private readonly HashSet<string> _hiddenFolderNames;
    private HttpListener _listener = new();
    // Thumbnail cache: base64-encoded path ? JPEG bytes (null = not available)
    private readonly ConcurrentDictionary<string, byte[]?> _thumbCache = new();
    private readonly object _favoritesGate = new();
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _libraryIndexTimer;
    private readonly Timer _libraryWatcherDebounceTimer;
    // True while the browser-side local player is active (set via /api/local-playing)
    public bool BrowserLocalPlaying => _browserLocalPlaying;

    private readonly object _libraryIndexGate = new();
    private readonly object _musicIndexGate = new();
    private FileSystemWatcher? _libraryWatcher;
    private LibraryFile[] _libraryIndex = [];
    private MusicFile[] _musicIndex = [];
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastIndexRefreshUtc;
    private DateTimeOffset? _lastMusicIndexRefreshUtc;
    private DateTimeOffset _libraryWatcherStartedUtc;
    private DateTimeOffset? _pendingLibraryRescanUtc;
    private DateTimeOffset? _scanStartedUtc;
    private int _scannedFiles;
    private int _scannedFolders;
    private string _lastScanError = string.Empty;
    // A unique token generated once per process lifetime; clients use it to detect server restarts.
    private static readonly string _serverSessionId = Guid.NewGuid().ToString("N");
    private bool _isIndexing;
    private bool _isMusicIndexing;
    private bool _browserLocalPlaying;   // true while browser is playing audio locally
    private string _lastMusicScanError = string.Empty;
    private int _musicScanProgress;
    private string _musicScanFolder = string.Empty;
    private MusicScanJob? _activeMusicScanJob;
    private CancellationTokenSource? _musicScanCts;
    private int _staleLinkCount;
    private readonly Timer _staleLinkTimer;
    private readonly object _thumbnailQueueGate = new();
    private CancellationTokenSource? _thumbnailQueueCancellation;
    private bool _thumbnailQueueRunning;
    private int _thumbnailQueueTotal;
    private int _thumbnailQueueProcessed;
    private int _thumbnailQueueGenerated;
    private int _thumbnailQueueCached;
    private string _thumbnailQueueCurrentTitle = string.Empty;
    private string _thumbnailQueueLastError = string.Empty;
    private DateTimeOffset? _thumbnailQueueStartedUtc;
    private DateTimeOffset? _thumbnailQueueCompletedUtc;
    private string _activeScheme;
    private string? _startupWarning;
    private readonly RemotePlay.Services.AppUpdater? _appUpdater;

    public WebServer(AppConfig config, WebServerCallbacks callbacks, PresenceBroadcaster? broadcaster = null, PlaybackHistory? playbackHistory = null, RemotePlay.Services.AppUpdater? appUpdater = null)
    {
        _config = config;
        _callbacks = callbacks;
        _broadcaster = broadcaster;
        _playbackHistory = playbackHistory ?? new PlaybackHistory();
        _videoExtensions = BuildExtensionSet(config.EffectiveVideoFileExtensions);
        _musicExtensions = BuildExtensionSet(config.EffectiveMusicFileExtensions);
        _hiddenFolderNames = BuildNameSet(config.EffectiveIgnoredLibraryFolders, HiddenFolderNames);
        _activeScheme = config.Scheme;
        _appUpdater = appUpdater;
        LoadFavorites();
        _libraryIndexTimer = new Timer(_ => RefreshLibraryIndexIfIdle(), null,
            TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        _libraryWatcherDebounceTimer = new Timer(_ => RunDelayedLibraryRescan(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _staleLinkTimer = new Timer(_ => RunStaleLinkCheck(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
    }

    public string ActiveScheme => _activeScheme;
    public string? StartupWarning => _startupWarning;
    public int LibraryVideoCount => _libraryIndex.Length;

    /// <summary>Returns (or lazily creates) the <see cref="PlaybackHistory"/> for the given client IP.
    /// Uses the shared local history for <c>127.0.0.1</c> / <c>::1</c> so the WPF player and
    /// localhost browser clients share a single history file.</summary>
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
    public LibraryScanStatus LibraryStatus => GetLibraryScanStatus();

    /// <summary>
    /// Returns the number of indexed link entries whose resolved target is inside
    /// (or equal to) <paramref name="folderPath"/>. Uses the in-memory index — O(n) with no disk I/O.
    /// </summary>
    public int CountIndexedLinksPointingIntoFolder(string folderPath)
    {
        var index = _libraryIndex; // snapshot — array reference is replaced atomically on rescan
        var prefix = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return index.Count(f =>
            f.IsLink &&
            (string.Equals(f.FilePath, folderPath, StringComparison.OrdinalIgnoreCase) ||
             f.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Returns the .rplink file paths from the index whose resolved target equals
    /// <paramref name="targetFilePath"/>. Uses the in-memory index — no disk I/O.
    /// Returns <c>null</c> when the index is empty or not yet built.
    /// </summary>
    public string[]? GetIndexedLinkSourcesForFile(string targetFilePath)
    {
        var index = _libraryIndex;
        if (index.Length == 0) return null;

        return index
            .Where(f => f.IsLink &&
                        f.LinkSourcePath is not null &&
                        string.Equals(f.FilePath, targetFilePath, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.LinkSourcePath!)
            .ToArray();
    }

    /// <summary>
    /// Returns a set of all paths tracked by the current index: for regular files this is
    /// the file path; for links this is the .rplink file path. Used by the Check Index command.
    /// Returns an empty set when the index has not been built yet.
    /// </summary>
    public HashSet<string> GetIndexedPathSet()
    {
        var index = _libraryIndex;
        var set = new HashSet<string>(index.Length * 2, StringComparer.OrdinalIgnoreCase);
        foreach (var f in index)
        {
            // Always add FilePath (the video file path) so browser movie-rows can be matched
            // regardless of whether the index entry is a direct file or a deduplicated link entry.
            set.Add(Path.GetFullPath(f.FilePath));

            // Also add the .rplink file path so browser link-rows can be matched.
            if (f.IsLink && f.LinkSourcePath is not null)
                set.Add(Path.GetFullPath(f.LinkSourcePath));
        }
        return set;
    }

    /// <summary>
    /// Returns the set of folder names that the library scanner ignores (e.g. "Subs", "Alt").
    /// Files inside these folders are intentionally excluded from the index.
    /// </summary>
    public IReadOnlySet<string> GetIgnoredFolderNames() => _hiddenFolderNames;

    /// <summary>
    /// Prevents the <see cref="FileSystemWatcher"/> from scheduling a rescan for
    /// <paramref name="duration"/> by advancing the watcher-started timestamp.
    /// Call this before making file-system changes that you want to handle via targeted index updates.
    /// </summary>
    public void SuppressWatcher(TimeSpan duration)
    {
        // By moving _libraryWatcherStartedUtc into the future, any watcher event that fires
        // while we are making changes will be dropped by the warm-up guard in ScheduleLibraryRescan.
        _libraryWatcherStartedUtc = DateTimeOffset.UtcNow.Add(duration);
    }

    /// <summary>Removes all index entries whose <see cref="LibraryFile.FilePath"/> or
    /// <see cref="LibraryFile.LinkSourcePath"/> starts with <paramref name="prefix"/> (folder delete/move).</summary>
    public void IndexRemoveUnderPath(string prefix)
    {
        var normalPrefix = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
        // Also match an exact path (single file / link)
        var exact = Path.GetFullPath(prefix);

        lock (_libraryIndexGate)
        {
            _libraryIndex = _libraryIndex.Where(f =>
            {
                var fp  = Path.GetFullPath(f.FilePath);
                var lsp = f.LinkSourcePath is not null ? Path.GetFullPath(f.LinkSourcePath) : null;
                bool matchFile = string.Equals(fp, exact, StringComparison.OrdinalIgnoreCase)
                                 || fp.StartsWith(normalPrefix, StringComparison.OrdinalIgnoreCase);
                bool matchLink = lsp is not null && (
                                 string.Equals(lsp, exact, StringComparison.OrdinalIgnoreCase)
                                 || lsp.StartsWith(normalPrefix, StringComparison.OrdinalIgnoreCase));
                return !matchFile && !matchLink;
            }).ToArray();
        }
        Logger.Info($"IndexRemoveUnderPath: removed entries under '{prefix}'; index now has {_libraryIndex.Length} entries");
        SaveLibraryIndexCache();
    }

    /// <summary>Adds or replaces the index entry for a single video file or .rplink file.</summary>
    public void IndexAddOrUpdateFile(string filePath)
    {
        var root = _config.ResolvedMoviesPath;
        LibraryFile? entry = RplinkHelper.IsRplinkFile(filePath)
            ? LibraryIndexHelpers.BuildLibraryFileForLink(root, filePath)
            : WebPathHelpers.IsVideoFile(filePath, _videoExtensions)
                ? LibraryIndexHelpers.BuildLibraryFile(root, filePath)
                : null;

        if (entry is null) return;

        lock (_libraryIndexGate)
        {
            // Remove any existing entry for the same FilePath/LinkSourcePath, then add the new one.
            var without = _libraryIndex.Where(f =>
                !string.Equals(f.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase) &&
                !(f.LinkSourcePath is not null && entry.LinkSourcePath is not null &&
                  string.Equals(f.LinkSourcePath, entry.LinkSourcePath, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            _libraryIndex = [.. without, entry];
        }
        Logger.Info($"IndexAddOrUpdateFile: upserted '{filePath}'; index now has {_libraryIndex.Length} entries");
        SaveLibraryIndexCache();
    }

    /// <summary>Updates every entry whose <see cref="LibraryFile.FilePath"/> or
    /// <see cref="LibraryFile.LinkSourcePath"/> begins with <paramref name="oldPrefix"/>
    /// by replacing that prefix with <paramref name="newPrefix"/> (folder rename).</summary>
    public void IndexRenamePrefix(string oldPrefix, string newPrefix)
    {
        var normOld = Path.GetFullPath(oldPrefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
        var normNew = Path.GetFullPath(newPrefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;

        static string Reprefix(string path, string oldP, string newP) =>
            newP + path.Substring(oldP.Length);

        lock (_libraryIndexGate)
        {
            _libraryIndex = _libraryIndex.Select(f =>
            {
                var fp  = Path.GetFullPath(f.FilePath);
                var lsp = f.LinkSourcePath is not null ? Path.GetFullPath(f.LinkSourcePath) : null;

                bool fpMatch  = fp.StartsWith(normOld, StringComparison.OrdinalIgnoreCase);
                bool lspMatch = lsp is not null && lsp.StartsWith(normOld, StringComparison.OrdinalIgnoreCase);

                if (!fpMatch && !lspMatch) return f;

                var newFp  = fpMatch  ? Reprefix(fp,  normOld, normNew) : fp;
                var newLsp = lspMatch ? Reprefix(lsp!, normOld, normNew) : f.LinkSourcePath;

                return f with { FilePath = newFp, LinkSourcePath = newLsp };
            }).ToArray();
        }
        Logger.Info($"IndexRenamePrefix: '{oldPrefix}' -> '{newPrefix}'; index now has {_libraryIndex.Length} entries");
        SaveLibraryIndexCache();
    }

    /// <summary>Renames a single file entry in the index (file rename, not folder).</summary>
    public void IndexRenameFile(string oldPath, string newPath)
    {
        lock (_libraryIndexGate)
        {
            _libraryIndex = _libraryIndex.Select(f =>
            {
                bool fpMatch  = string.Equals(Path.GetFullPath(f.FilePath),      Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase);
                bool lspMatch = f.LinkSourcePath is not null &&
                                string.Equals(Path.GetFullPath(f.LinkSourcePath), Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase);

                if (fpMatch)  return f with { FilePath = newPath };
                if (lspMatch) return f with { LinkSourcePath = newPath };
                return f;
            }).ToArray();
        }
        Logger.Info($"IndexRenameFile: '{oldPath}' -> '{newPath}'");
        SaveLibraryIndexCache();
    }

    private bool IsFavorite(string filePath)
    {
        lock (_favoritesGate)
            return _favorites.Contains(filePath);
    }

    private void SetFavorite(string filePath, bool favorite)
    {
        lock (_favoritesGate)
        {
            if (favorite)
                _favorites.Add(filePath);
            else
                _favorites.Remove(filePath);

            SaveFavorites();
        }
    }

    private string[] GetFavoritePaths()
    {
        lock (_favoritesGate)
            return _favorites.ToArray();
    }

    private LibraryScanStatus GetLibraryScanStatus()
    {
        var index = _libraryIndex;
        var indexedLinks  = index.Count(f => f.IsLink);
        var indexedMovies = index.Length - indexedLinks;
        return new()
        {
            IsScanning    = _isIndexing,
            IndexedFiles  = index.Length,
            IndexedMovies = indexedMovies,
            IndexedLinks  = indexedLinks,
            ScannedFiles  = _scannedFiles,
            ScannedFolders = _scannedFolders,
            StartedUtc    = _scanStartedUtc,
            CompletedUtc  = _lastIndexRefreshUtc,
            LastError     = _lastScanError,
            StaleLinkCount = _staleLinkCount
        };
    }

    private ThumbnailQueueStatus GetThumbnailQueueStatus()
    {
        lock (_thumbnailQueueGate)
        {
            return new ThumbnailQueueStatus
            {
                IsRunning = _thumbnailQueueRunning,
                Total = _thumbnailQueueTotal,
                Processed = _thumbnailQueueProcessed,
                Generated = _thumbnailQueueGenerated,
                Cached = _thumbnailQueueCached,
                CurrentTitle = _thumbnailQueueCurrentTitle,
                LastError = _thumbnailQueueLastError,
                StartedUtc = _thumbnailQueueStartedUtc,
                CompletedUtc = _thumbnailQueueCompletedUtc
            };
        }
    }

    private ThumbnailQueueStatus StartThumbnailQueue()
    {
        if (!_config.EnableThumbnailGeneration)
            return GetThumbnailQueueStatus();

        LibraryFile[] files;
        lock (_thumbnailQueueGate)
        {
            if (_thumbnailQueueRunning)
                return GetThumbnailQueueStatus();

            files = _libraryIndex.Where(f => File.Exists(f.FilePath)).ToArray();
            _thumbnailQueueCancellation?.Dispose();
            _thumbnailQueueCancellation = new CancellationTokenSource();
            _thumbnailQueueRunning = true;
            _thumbnailQueueTotal = files.Length;
            _thumbnailQueueProcessed = 0;
            _thumbnailQueueGenerated = 0;
            _thumbnailQueueCached = 0;
            _thumbnailQueueCurrentTitle = string.Empty;
            _thumbnailQueueLastError = string.Empty;
            _thumbnailQueueStartedUtc = DateTimeOffset.UtcNow;
            _thumbnailQueueCompletedUtc = null;
        }

        var token = _thumbnailQueueCancellation.Token;
        _ = Task.Run(() => RunThumbnailQueue(files, token), token);
        return GetThumbnailQueueStatus();
    }

    private void CancelThumbnailQueue()
    {
        lock (_thumbnailQueueGate)
            _thumbnailQueueCancellation?.Cancel();
    }

    private void RunThumbnailQueue(LibraryFile[] files, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_thumbnailQueueGate)
                    _thumbnailQueueCurrentTitle = file.Name;

                var cacheFile = GetThumbnailCacheFile(file.FilePath);
                if (File.Exists(cacheFile))
                {
                    lock (_thumbnailQueueGate)
                    {
                        _thumbnailQueueCached++;
                        _thumbnailQueueProcessed++;
                    }
                    continue;
                }

                var jpeg = GenerateAndPersistThumbnail(file.EncodedPath, cacheFile);
                lock (_thumbnailQueueGate)
                {
                    if (jpeg is not null)
                        _thumbnailQueueGenerated++;

                    _thumbnailQueueProcessed++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_thumbnailQueueGate)
                _thumbnailQueueLastError = "Thumbnail generation canceled.";
        }
        catch (Exception ex)
        {
            Logger.Error("ThumbnailQueue", "Thumbnail queue failed", ex);
            lock (_thumbnailQueueGate)
                _thumbnailQueueLastError = ex.Message;
        }
        finally
        {
            lock (_thumbnailQueueGate)
            {
                _thumbnailQueueRunning = false;
                _thumbnailQueueCurrentTitle = string.Empty;
                _thumbnailQueueCompletedUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Scans all .rplink files in the library and counts those whose resolved target no longer exists.
    /// Updates <c>_staleLinkCount</c> which is exposed through <see cref="LibraryStatus"/>.
    /// Runs on the thread-pool timer thread — should never throw or block.
    /// </summary>
    private void RunStaleLinkCheck()
    {
        try
        {
            var root = _config.ResolvedMoviesPath;
            if (!Directory.Exists(root)) return;

            int stale = 0;
            foreach (var f in Directory.EnumerateFiles(root, "*" + RplinkHelper.Extension, SearchOption.AllDirectories))
            {
                var target = RplinkHelper.TryReadTarget(f);
                if (target is null)
                    stale++;
            }

            Interlocked.Exchange(ref _staleLinkCount, stale);
            if (stale > 0)
                Logger.Info($"Stale-link check: {stale} broken link(s) found in '{root}'");
        }
        catch (Exception ex)
        {
            Logger.Error("Stale-link check failed", ex);
        }
    }

    private bool HasFreshLibraryIndex()
    {
        try
        {
            if (_libraryIndex.Length == 0 || _lastIndexRefreshUtc is null)
                return false;

            var root = _config.ResolvedMoviesPath;
            if (!Directory.Exists(root))
                return false;

            var latestWriteUtc = Directory.GetLastWriteTimeUtc(root);
            return latestWriteUtc <= _lastIndexRefreshUtc.Value.UtcDateTime;
        }
        catch
        {
            return false;
        }
    }

    private void LoadLibraryIndexCache()
    {
        try
        {
            if (!File.Exists(LibraryIndexCacheFile))
                return;

            var json = File.ReadAllText(LibraryIndexCacheFile);
            var cache = JsonSerializer.Deserialize<LibraryIndexCache>(json, CaseInsensitiveOptions);
            if (cache is null || !string.Equals(NormalizePath(cache.RootPath), NormalizePath(_config.ResolvedMoviesPath), StringComparison.OrdinalIgnoreCase))
                return;

            _libraryIndex = cache.Files;
            _lastIndexRefreshUtc = cache.CreatedUtc;
            _scannedFiles = _libraryIndex.Length;
            Logger.Info($"Loaded persistent library index: {_libraryIndex.Length} videos");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load library index cache", ex);
        }
    }

    private void SaveLibraryIndexCache()
    {
        try
        {
            var cache = new LibraryIndexCache
            {
                RootPath = _config.ResolvedMoviesPath,
                CreatedUtc = _lastIndexRefreshUtc ?? DateTimeOffset.UtcNow,
                Files = _libraryIndex
            };

            var json = JsonSerializer.Serialize(cache, IndentedOptions);
            File.WriteAllText(LibraryIndexCacheFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save library index cache", ex);
        }
    }

    private void LoadMusicIndexCache()
    {
        try
        {
            if (!File.Exists(MusicIndexCacheFile))
                return;

            var json = File.ReadAllText(MusicIndexCacheFile);
            var cache = JsonSerializer.Deserialize<MusicIndexCache>(json, CaseInsensitiveOptions);
            if (cache is null || !string.Equals(NormalizePath(cache.RootPath), NormalizePath(_config.ResolvedMusicPath), StringComparison.OrdinalIgnoreCase))
                return;

            lock (_musicIndexGate)
            {
                _musicIndex = cache.Files;
                _lastMusicIndexRefreshUtc = cache.CreatedUtc;
            }
            Logger.Info($"Loaded persistent music index: {cache.Files.Length} tracks");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load music index cache", ex);
        }
    }

    private void SaveMusicIndexCache()
    {
        try
        {
            MusicFile[] snapshot;
            DateTimeOffset ts;
            lock (_musicIndexGate)
            {
                snapshot = _musicIndex;
                ts = _lastMusicIndexRefreshUtc ?? DateTimeOffset.UtcNow;
            }
            var cache = new MusicIndexCache
            {
                RootPath = _config.ResolvedMusicPath,
                CreatedUtc = ts,
                Files = snapshot
            };
            var json = JsonSerializer.Serialize(cache, IndentedOptions);
            File.WriteAllText(MusicIndexCacheFile, json);
            Logger.Info($"Saved music index cache: {snapshot.Length} tracks");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save music index cache", ex);
        }
    }

    private void LoadFavorites()
    {
        try
        {
            if (!File.Exists(FavoritesFile))
                return;

            var favorites = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FavoritesFile), CaseInsensitiveOptions) ?? [];
            _favorites = new HashSet<string>(favorites, StringComparer.OrdinalIgnoreCase);
            Logger.Info($"Loaded favorites: {_favorites.Count}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load favorites", ex);
        }
    }

    /// <summary>Persists the current in-memory library index to disk. Safe to call from any thread.
    /// Browser operations that mutate the index (add/remove/rename) call this so the cache
    /// stays consistent between application restarts without waiting for a full rescan.</summary>
    public void PersistIndexCache() => SaveLibraryIndexCache();

    private void SaveFavorites()
    {
        try
        {
            File.WriteAllText(FavoritesFile, JsonSerializer.Serialize(_favorites.Order(StringComparer.OrdinalIgnoreCase), IndentedOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save favorites", ex);
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void ValidateStartupConfiguration()
    {
        if (_config.Port is < 1024 or > 65535)
            throw new InvalidOperationException($"Configured port {_config.Port} is outside the supported range 1024-65535.");

        if (!Directory.Exists(_config.ResolvedMoviesPath))
            Logger.Warning("Config", $"Movies folder does not exist and will be created on browse if possible: {_config.ResolvedMoviesPath}");

        if (_videoExtensions.Count == 0)
            throw new InvalidOperationException("At least one video file extension must be configured.");
    }

    private static HashSet<string> BuildExtensionSet(IEnumerable<string>? values) =>
        WebServerConfigHelpers.BuildExtensionSet(values, VideoExtensions);

    private static HashSet<string> BuildNameSet(IEnumerable<string>? values, IEnumerable<string> fallback) =>
        WebServerConfigHelpers.BuildNameSet(values, fallback);

    public void Start()
    {
        ValidateStartupConfiguration();
        _activeScheme = _config.Scheme;
        _startupWarning = null;
        _ = Task.Run(() =>
        {
            LoadLibraryIndexCache();
            StartLibraryIndexRefresh(force: true);
            LoadMusicIndexCache();
            StartMusicIndexRefreshIfNeeded();
        });

        if (_config.UseHttps)
        {
            try
            {
                EnsureHttpsBinding(_config.Port);
            }

            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or CryptographicException)
            {
                _activeScheme = "http";
                _startupWarning = "HTTPS setup failed; RemotePlay started in HTTP mode instead. " + ex.Message;
                Logger.Error("HTTPS setup failed; falling back to HTTP", ex);
            }
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add($"{_activeScheme}://*:{_config.Port}/");
        _listener.Start();
        Logger.Info("WebServer", $"Web server listening on {_activeScheme}://*:{_config.Port}");
        StartLibraryWatcher();
        Task.Run(ListenLoopAsync);
    }

    public void RequestLibraryRescan() => StartLibraryIndexRefresh(force: true);

    /// <summary>Creates a <c>.rplink</c> file in <paramref name="destinationDirectory"/> pointing to
    /// <paramref name="targetFilePath"/>. Triggers an immediate library rescan.</summary>
    public void CreateRplink(string targetPath, string destinationDirectory, string? linkName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var root = _config.ResolvedMoviesPath;
        if (!WebPathHelpers.IsUnderRoot(destinationDirectory, root))
            throw new InvalidOperationException("Destination directory must be inside the library root.");

        // For folders use the folder name as the stem; for files strip the extension.
        var stem = string.IsNullOrWhiteSpace(linkName)
            ? (Directory.Exists(targetPath)
                ? Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : Path.GetFileNameWithoutExtension(targetPath))
            : linkName.Trim();

        var linkPath = Path.Combine(destinationDirectory, stem + RplinkHelper.Extension);

        // Store a relative path when both the link file and the target are on the same volume,
        // making the library portable if the entire library root is moved to another drive letter.
        var storedTarget = RplinkHelper.MakeRelativeIfPossible(linkPath, targetPath);

        RplinkHelper.Create(linkPath, storedTarget);
        Logger.Info($"Created .rplink: {linkPath} -> {storedTarget} (target: {targetPath})");
        SuppressWatcher(TimeSpan.FromSeconds(10));
        IndexAddOrUpdateFile(linkPath);
    }

    public void Stop()
    {
        try { CancelThumbnailQueue(); } catch { }
        try { _thumbnailQueueCancellation?.Dispose(); } catch { }
        try { _libraryIndexTimer.Dispose(); } catch { }
        try { _libraryWatcherDebounceTimer.Dispose(); } catch { }
        try { _staleLinkTimer.Dispose(); } catch { }
        try { _libraryWatcher?.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
    }

    private void StartLibraryWatcher()
    {
        try
        {
            _libraryWatcher?.Dispose();
            var root = _config.ResolvedMoviesPath;
            if (!Directory.Exists(root))
                return;

            _libraryWatcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _libraryWatcher.Created += OnLibraryChanged;
            _libraryWatcher.Deleted += OnLibraryChanged;
            _libraryWatcher.Renamed += OnLibraryRenamed;
            _libraryWatcherStartedUtc = DateTimeOffset.UtcNow;
            Logger.Info($"Library watcher started: {root}");
        }
        catch (Exception ex)
        {
            Logger.Error("Library watcher failed to start", ex);
        }
    }

    private void OnLibraryChanged(object sender, FileSystemEventArgs e)
    {
        if (!WebPathHelpers.IsVideoFile(e.FullPath, _videoExtensions)
            && !RplinkHelper.IsRplinkFile(e.FullPath)
            && !Directory.Exists(e.FullPath))
            return;

        ScheduleLibraryRescan();
    }

    private void OnLibraryRenamed(object sender, RenamedEventArgs e)
    {
        if (!WebPathHelpers.IsVideoFile(e.FullPath, _videoExtensions)
            && !WebPathHelpers.IsVideoFile(e.OldFullPath, _videoExtensions)
            && !RplinkHelper.IsRplinkFile(e.FullPath)
            && !RplinkHelper.IsRplinkFile(e.OldFullPath)
            && !Directory.Exists(e.FullPath))
            return;

        ScheduleLibraryRescan();
    }

    private void ScheduleLibraryRescan()
    {
        if (DateTimeOffset.UtcNow - _libraryWatcherStartedUtc < TimeSpan.FromSeconds(10))
        {
            Logger.Info("Library change ignored during watcher warm-up");
            return;
        }

        if (_config.LibraryRescanDelayMinutes <= 0)
        {
            Logger.Info("Library change detected; automatic delayed rescan is disabled");
            return;
        }

        var delay = TimeSpan.FromMinutes(_config.LibraryRescanDelayMinutes);
        var dueUtc = DateTimeOffset.UtcNow.Add(delay);
        _pendingLibraryRescanUtc = dueUtc;
        Logger.Info($"Library change detected; delayed rescan scheduled for {dueUtc:u} ({_config.LibraryRescanDelayMinutes} minute(s))");
        _libraryWatcherDebounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void RunDelayedLibraryRescan()
    {
        var pendingUtc = _pendingLibraryRescanUtc;
        _pendingLibraryRescanUtc = null;
        Logger.Info($"Delayed library rescan starting (scheduled for {pendingUtc:u})");
        StartLibraryIndexRefresh(force: true);
    }

    private static void EnsureHttpsBinding(int port)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("HTTPS mode requires Windows HTTP.sys certificate binding.");

        using var certificate = GetOrCreateHttpsCertificate();
        var ipPort = $"0.0.0.0:{port}";
        var deleteResult = RunNetsh($"http delete sslcert ipport={ipPort}");
        if (deleteResult.ExitCode != 0 && !deleteResult.Output.Contains("The system cannot find the file specified", StringComparison.OrdinalIgnoreCase))
            Logger.Info($"Existing HTTPS binding delete returned {deleteResult.ExitCode}: {deleteResult.Output.Trim()}");

        var addResult = RunNetsh($"http add sslcert ipport={ipPort} certhash={certificate.Thumbprint} appid={{{HttpsCertificateAppId}}}");
        if (addResult.ExitCode != 0)
            throw new InvalidOperationException("Could not configure HTTPS certificate binding. Run RemotePlay as administrator once, then enable HTTPS again. " + addResult.Output.Trim());
    }

    private static X509Certificate2 GetOrCreateHttpsCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, HttpsCertificateSubject, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(c => c.NotAfter > DateTimeOffset.Now.AddDays(30) && c.HasPrivateKey)
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();

        if (existing is not null)
            return existing;

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(HttpsCertificateSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var address in GetLocalIpAddresses())
            sanBuilder.AddIpAddress(address);
        request.CertificateExtensions.Add(sanBuilder.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        var exportableCertificate = new X509Certificate2(
            certificate.Export(X509ContentType.Pfx),
            string.Empty,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        store.Add(exportableCertificate);

        var created = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, HttpsCertificateSubject, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotAfter)
            .First();

        Logger.Info($"Created RemotePlay HTTPS certificate: {created.Thumbprint}");
        return created;
    }

    internal static X509Certificate2? TryGetHttpsCertificate()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates
                .Find(X509FindType.FindBySubjectDistinguishedName, HttpsCertificateSubject, validOnly: false)
                .OfType<X509Certificate2>()
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not read RemotePlay HTTPS certificate", ex);
            return null;
        }
    }

    private static IEnumerable<IPAddress> GetLocalIpAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => (a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) && !IPAddress.IsLoopback(a))
                .ToArray();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not add local IP addresses to HTTPS certificate", ex);
            return [];
        }
    }

    private static (int ExitCode, string Output) RunNetsh(string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start netsh.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }


    private static readonly string ManifestJson = JsonSerializer.Serialize(new
    {
        name = "RemotePlay",
        short_name = "RemotePlay",
        description = "Remote control for local TV playback",
        start_url = "/",
        scope = "/",
        display = "standalone",
        orientation = "any",
        background_color = "#111111",
        theme_color = "#1a1a2e",
        icons = new object[]
        {
            new { src = "/icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any maskable" },
            new { src = "/icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any maskable" }
        }
    });

    private static string GetHtmlPage() =>
        _cachedHtmlPage.Value.Replace("__ASSET_VERSION__", _appVersion, StringComparison.Ordinal);

    private static string GetStylesCss() => _cachedStylesCss.Value;

    private static string GetAppJs() => _cachedAppJs.Value;
    private static string GetWorld110m() => _cachedWorld110m.Value;
    private static string GetWorld50m()  => _cachedWorld50m.Value;
    private static string GetUsStates()  => _cachedUsStates.Value;

    private static string GetServiceWorkerJs() =>
        _cachedServiceWorkerJsRaw.Value.Replace("__CACHE_VERSION__", _appVersion, StringComparison.Ordinal);

    private static string ReadAppVersion()
    {
        var versionFile = Path.Combine(AppContext.BaseDirectory, "version.txt");
        if (File.Exists(versionFile))
        {
            var text = File.ReadAllText(versionFile).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Replace('.', '-');
        }
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "1-0-0" : $"{v.Major}-{v.Minor}-{v.Build}";
    }

    private static string LoadWebAsset(string fileName, string? fallback = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, WebAssetsDirectoryName, fileName);
        try
        {
            if (File.Exists(path))
                return File.ReadAllText(path, Encoding.UTF8);

            Logger.Error($"Web asset not found: {path}");
            return fallback ?? string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load web asset {path}", ex);
            return fallback ?? string.Empty;
        }
    }

    private static void TrySendResponse(HttpListenerContext ctx, int status,
        string contentType, string body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentEncoding = Encoding.UTF8;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch (HttpListenerException)
        {
            // Client disconnected before the response was fully sent — not an error.
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to send HTTP response", ex);
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static void TrySendBytes(HttpListenerContext ctx, int status,
        string contentType, byte[] body)
    {
        try
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
        }
        catch (HttpListenerException)
        {
            // Client disconnected before the response was fully sent — not an error.
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to send binary HTTP response", ex);
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static string HtmlEncode(string value) =>
        WebUtility.HtmlEncode(value);

    private static string BuildSetupQrSvg(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var qr = new SvgQRCode(data);
        return qr.GetGraphic(8);
    }

    private static byte[] BuildSetupCodePng(string value)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        return qr.GetGraphic(12);
    }

    /// <summary>Returns a QR code PNG for the given URL, generated locally without an HTTP round-trip.</summary>
    public static byte[] GenerateQrCodePng(string url) => BuildSetupCodePng(url);

}
