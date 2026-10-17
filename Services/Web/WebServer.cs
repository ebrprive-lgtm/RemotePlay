using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using QRCoder;
using RemotePlay.Models;
using RemotePlay.Services.Discovery;
using System.Diagnostics.CodeAnalysis;
using Timer = System.Threading.Timer;

namespace RemotePlay;

[ExcludeFromCodeCoverage]
internal sealed partial class WebServer
{
    private const string WebAssetsDirectoryName = "WebAssets";
    private static readonly string ThumbnailCacheDirectory = AppPaths.ThumbnailCacheDirectory;
    private static readonly string LibraryIndexCacheFile = AppPaths.LibraryIndexCacheFile;
    private static readonly string MusicIndexCacheFile   = AppPaths.MusicIndexCacheFile;
    private static readonly string M3uIndexCacheFile      = AppPaths.M3uIndexCacheFile;
    private static readonly string LiveTvCatalogCacheFile = AppPaths.LiveTvCatalogCacheFile;
    private static readonly string FavoritesFile = AppPaths.FavoritesFile;

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv" };
    private static readonly HashSet<string> HiddenFolderNames =
        new(StringComparer.OrdinalIgnoreCase) { "Subs", "Alt", "#Recycle" };
    private static readonly Guid HttpsCertificateAppId = new("be2a8b40-850e-4ef1-a893-b0b13f5c7fd9");
    private const string HttpsCertificateSubject = "CN=RemotePlay Local HTTPS";

    private static readonly JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions IndentedOptions =
        new() { WriteIndented = true };
    private static readonly System.Net.Http.HttpClient _liveTvCatalogHttpClient = CreateLiveTvCatalogHttpClient();

    private static readonly Lazy<string> _cachedHtmlPage =
        new(() => LoadWebAsset("index.html"));
    private static readonly Lazy<string> _cachedStylesCss =
        new(() => LoadWebAsset("styles.css"));
    private static readonly string[] _appJsModules =
    [
        "app-sse.js",
        "app-core.js",
        "app-diagnostics.js",
        "app-playback.js",
        "app-context-menu.js",
        "app-library.js",
        "app-live-tv.js",
        "app-podcasts.js",
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

    private AppConfig _config;
    private readonly WebServerCallbacks _callbacks;
    private readonly PodcastFeedHttpClient _podcastFeedHttpClient;
    private readonly PodcastFeedCache _podcastFeedCache;
    private readonly TimeProvider _podcastFeedCacheTimeProvider;
    private readonly string _podcastTopPoolCacheFilePath;
    private readonly PresenceBroadcaster? _broadcaster;
    private readonly RemotePlay.Services.Discovery.DlnaDiscovery? _dlna;
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

    public void SetBrowserLocalPlaying(bool isPlaying) => _browserLocalPlaying = isPlaying;

    public void UpdateExpertMode(bool expertMode) => _expertMode = expertMode;
    public void UpdateDebugMode(bool debugMode) => _debugMode = debugMode;

    private readonly object _libraryIndexGate = new();
    private readonly object _musicIndexGate = new();
    private readonly object _libraryIndexCacheSaveLock = new();
    private readonly object _musicIndexCacheSaveLock = new();
    private readonly object _m3uIndexCacheSaveLock   = new();
    private readonly object _liveTvCatalogGate = new();
    private readonly object _liveTvCatalogCacheSaveLock = new();
    private readonly object _liveTvCatalogDiagGate = new();
    private readonly object _liveTvCatalogRefreshGate = new();
    // Lazily populated by background probes so the browse path stays fast.
    private readonly ConcurrentDictionary<string, double> _videoDurationCache = new(StringComparer.OrdinalIgnoreCase);
    // Folder item/track count cache for music browse: keys are "vis:<path>", "top:<path>", "rec:<path>"
    private readonly ConcurrentDictionary<string, (int count, DateTime expiry)> _folderCountCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FolderCountCacheTtl = TimeSpan.FromDays(1);
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
    private bool _isMusicEnriching;
    private static readonly TimeSpan LiveTvCatalogCacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan LiveTvCatalogRefreshTimeout = TimeSpan.FromSeconds(45);
    private DateTimeOffset? _liveTvCatalogSavedAtUtc;
    private LiveTvCatalogEntry[] _liveTvCatalog = [];
    private DateTimeOffset? _liveTvCatalogLastAttemptUtc;
    private DateTimeOffset? _liveTvCatalogLastSuccessUtc;
    private int _liveTvCatalogLastChannelsStatusCode;
    private int _liveTvCatalogLastStreamsStatusCode;
    private long _liveTvCatalogLastFetchDurationMs;
    private string _liveTvCatalogLastError = string.Empty;
    private Task<(LiveTvCatalogEntry[] channels, DateTimeOffset savedAtUtc)>? _liveTvCatalogRefreshTask;
    // In-memory M3U index: keyed by absolute M3U file path. Rebuilt alongside _musicIndex.
    private Dictionary<string, M3uEntry> _m3uIndex = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _m3uTrackYearHints = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _m3uTrackAlbumHints = new(StringComparer.OrdinalIgnoreCase);
    private bool _expertMode;             // updated live when settings change without server restart
    private bool _debugMode;              // updated live when settings change without server restart
    private bool _browserLocalPlaying;   // true while browser is playing audio locally
    private string? _musicPlayInitiatorIp; // IP of the client that last triggered /api/music/play
    private readonly object _videoPlayInitiatorGate = new();
    private string? _videoPlayInitiatorIp;
    private string? _videoPlayInitiatorPath;
    private DateTimeOffset _videoPlayInitiatorSetAtUtc;
    private bool _videoPlayInitiatorObservedActive;
    private bool _videoPlayInitiatorRecorded;
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

    // â”€â”€ Rate limiting â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private sealed class RateLimitBucket
    {
        public int Count;
        public long WindowStartTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    private readonly ConcurrentDictionary<string, RateLimitBucket> _rateLimitBuckets = new(StringComparer.OrdinalIgnoreCase);

    // â”€â”€ Graceful-shutdown token â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private CancellationTokenSource _listenerCts = new();

    public WebServer(
        AppConfig config,
        WebServerCallbacks callbacks,
        PresenceBroadcaster? broadcaster = null,
        PlaybackHistory? playbackHistory = null,
        RemotePlay.Services.AppUpdater? appUpdater = null,
        RemotePlay.Services.Discovery.DlnaDiscovery? dlna = null,
        PodcastFeedHttpClient? podcastFeedHttpClient = null,
        TimeProvider? podcastFeedCacheTimeProvider = null,
        System.Net.Http.HttpClient? podcastCatalogHttpClient = null,
        string? podcastTopPoolCacheFilePath = null)
    {
        _config = config;
        _expertMode = config.ExpertMode;
        _debugMode  = config.DebugMode;
        _callbacks = callbacks;
        _podcastHttpClient = podcastCatalogHttpClient ?? _defaultPodcastHttpClient;
        _podcastFeedHttpClient = podcastFeedHttpClient ?? _defaultPodcastFeedHttpClient;
        _podcastFeedCacheTimeProvider = podcastFeedCacheTimeProvider ?? TimeProvider.System;
        _podcastFeedCache = new PodcastFeedCache(_podcastFeedCacheTimeProvider);
        _podcastTopPoolCacheFilePath = string.IsNullOrWhiteSpace(podcastTopPoolCacheFilePath)
            ? AppPaths.PodcastTopPoolCacheFile
            : Path.GetFullPath(podcastTopPoolCacheFilePath);
        LoadPodcastTopPoolCache();
        _broadcaster = broadcaster;
        _dlna = dlna;
        _playbackHistory = playbackHistory ?? new PlaybackHistory();
        _videoExtensions = BuildExtensionSet(config.EffectiveVideoFileExtensions);
        _musicExtensions = BuildExtensionSet(config.EffectiveMusicFileExtensions);
        _hiddenFolderNames = BuildNameSet(config.EffectiveIgnoredLibraryFolders, HiddenFolderNames);
        _activeScheme = config.Scheme;
        _appUpdater = appUpdater;
        LoadFavorites();
        _libraryIndexTimer = new Timer(_ => RefreshLibraryIndexIfIdle(), null,
            TimeSpan.FromHours(24), TimeSpan.FromHours(24));
        _libraryWatcherDebounceTimer = new Timer(_ => RunDelayedLibraryRescan(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _staleLinkTimer = new Timer(_ => RunStaleLinkCheck(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
        PurgeStaleLyricsCacheEntries();
    }

    public string ActiveScheme => _activeScheme;
    public string? StartupWarning => _startupWarning;
    public int LibraryVideoCount => _libraryIndex.Length;

    /// <summary>
    /// Deletes negative-result lyrics cache entries (found=false) that are older than 2 hours.
    /// Runs once at startup so transient failures (timeouts, network issues) self-heal quickly.
    /// </summary>
    private static void PurgeStaleLyricsCacheEntries()
    {
        try
        {
            var dir = AppPaths.LyricsCacheDirectory;
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    if (!json.Contains("\"found\":false", StringComparison.Ordinal)) continue;
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("found", out var fEl) || fEl.GetBoolean()) continue;
                    // It's a negative result â€” check age
                    if (doc.RootElement.TryGetProperty("cachedUtc", out var tsEl) &&
                        DateTimeOffset.TryParse(tsEl.GetString(), out var cachedAt) &&
                        cachedAt < cutoff)
                    {
                        File.Delete(file);
                        Logger.Detail("Lyrics", $"Purged stale negative cache: {Path.GetFileName(file)}");
                    }
                }
                catch { /* skip individual bad files */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Detail("Lyrics", $"PurgeStaleLyricsCacheEntries error: {ex.Message}");
        }
    }

    public LibraryScanStatus LibraryStatus => GetLibraryScanStatus();
    public MusicIndexStatus MusicStatus => GetMusicIndexStatus();

    /// <summary>

    private bool IsFavorite(string filePath)
    {
        lock (_favoritesGate)
            return _favorites.Contains(filePath);
    }

    private void SetFavorite(string filePath, bool favorite)
    {
        lock (_favoritesGate)
        {
            var latest = LoadFavoritesFromDisk();
            if (favorite)
                latest.Add(filePath);
            else
                latest.Remove(filePath);

            _favorites = latest;

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
            StaleLinkCount = _staleLinkCount,
            AllPathsInvalid      = !_isIndexing && !_config.AllResolvedMoviesPaths.Any(Directory.Exists),
            AllMusicPathsInvalid = !_isMusicIndexing && !_config.AllResolvedMusicPaths.Any(Directory.Exists),
        };
    }

    private MusicIndexStatus GetMusicIndexStatus()
    {
        int count;
        lock (_musicIndexGate) count = _musicIndex.Length;
        return new()
        {
            IsIndexing    = _isMusicIndexing,
            IsEnriching   = _isMusicEnriching,
            IndexedTracks = count,
            LastError     = _lastMusicScanError,
            CompletedUtc  = _lastMusicIndexRefreshUtc
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
    /// Runs on the thread-pool timer thread â€” should never throw or block.
    /// </summary>
    private void RunStaleLinkCheck()
    {
        try
        {
            var root = _config.ResolvedMoviesPath;
            if (!Directory.Exists(root)) return;

            int stale = 0;
            int unreadable = 0;
            int inaccessible = 0;
            int totalLinks = 0;
            const int sampleLimit = 5;
            var staleSamples = new List<string>(sampleLimit);
            var unreadableSamples = new List<string>(sampleLimit);
            var inaccessibleSamples = new List<string>(sampleLimit);

            foreach (var f in Directory.EnumerateFiles(root, "*" + RplinkHelper.Extension, SearchOption.AllDirectories))
            {
                totalLinks++;
                var inspection = RplinkHelper.InspectTarget(f);

                switch (inspection.Status)
                {
                    case RplinkHelper.RplinkTargetStatus.Resolved:
                        break;

                    case RplinkHelper.RplinkTargetStatus.Missing:
                        stale++;
                        if (staleSamples.Count < sampleLimit)
                        {
                            staleSamples.Add($"{f} -> {inspection.ResolvedTarget ?? "<unknown target>"}");
                        }
                        break;

                    case RplinkHelper.RplinkTargetStatus.UnreadableLink:
                        unreadable++;
                        if (unreadableSamples.Count < sampleLimit)
                        {
                            unreadableSamples.Add($"{f} ({inspection.Details ?? "unreadable link"})");
                        }
                        break;

                    case RplinkHelper.RplinkTargetStatus.InaccessibleTarget:
                        inaccessible++;
                        if (inaccessibleSamples.Count < sampleLimit)
                        {
                            inaccessibleSamples.Add($"{f} -> {inspection.ResolvedTarget ?? "<unknown target>"} ({inspection.Details ?? "inaccessible"})");
                        }
                        break;
                }
            }

            Interlocked.Exchange(ref _staleLinkCount, stale);
            if (stale > 0 || unreadable > 0 || inaccessible > 0)
            {
                Logger.Info(
                    $"Stale-link check in '{root}': confirmed-missing={stale}, inaccessible={inaccessible}, unreadable-link={unreadable}, total-links={totalLinks}. " +
                    $"Missing samples: [{string.Join(" | ", staleSamples)}]. " +
                    $"Inaccessible samples: [{string.Join(" | ", inaccessibleSamples)}]. " +
                    $"Unreadable samples: [{string.Join(" | ", unreadableSamples)}].");
            }
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
        if (!Monitor.TryEnter(_libraryIndexCacheSaveLock))
            return;  // another save is already in progress; skip this one
        try
        {
            var cache = new LibraryIndexCache
            {
                RootPath = _config.ResolvedMoviesPath,
                CreatedUtc = _lastIndexRefreshUtc ?? DateTimeOffset.UtcNow,
                Files = _libraryIndex
            };

            var json = JsonSerializer.Serialize(cache, IndentedOptions);
            var tmp = LibraryIndexCacheFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(LibraryIndexCacheFile))
                File.Replace(tmp, LibraryIndexCacheFile, destinationBackupFileName: null);
            else
                File.Move(tmp, LibraryIndexCacheFile);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save library index cache", ex);
        }
        finally
        {
            Monitor.Exit(_libraryIndexCacheSaveLock);
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
        if (!Monitor.TryEnter(_musicIndexCacheSaveLock))
            return;  // another save is already in progress; skip this one
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
            var tmp = MusicIndexCacheFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(MusicIndexCacheFile))
                File.Replace(tmp, MusicIndexCacheFile, destinationBackupFileName: null);
            else
                File.Move(tmp, MusicIndexCacheFile);
                Logger.Info($"Saved music index cache: {snapshot.Length} tracks");
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to save music index cache", ex);
                }
                finally
                {
                    Monitor.Exit(_musicIndexCacheSaveLock);
                }
            }

            private static int? ParseYearHintFromM3uPath(string m3uPath)
            {
                var stem = Path.GetFileNameWithoutExtension(m3uPath);
                if (string.IsNullOrWhiteSpace(stem))
                    return null;

                var match = Regex.Match(stem, @"(?<!\d)(19\d{2}|20\d{2}|2100)(?!\d)");
                return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;
            }

            private static Dictionary<string, int> BuildM3uTrackYearHints(Dictionary<string, M3uEntry> m3uIndex)
            {
                var hints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in m3uIndex)
                {
                    var yearHint = ParseYearHintFromM3uPath(kv.Key);
                    if (!yearHint.HasValue)
                        continue;

                    foreach (var trackPath in kv.Value.TrackPaths)
                    {
                        if (!hints.ContainsKey(trackPath))
                            hints[trackPath] = yearHint.Value;
                    }
                }

                return hints;
            }

            private static bool IsGenericAlbumHintName(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return true;

                var normalized = value.Trim();
                return normalized.Equals("all", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("all songs", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("albums", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("singles", StringComparison.OrdinalIgnoreCase);
            }

            private static int GetAlbumHintPriority(string m3uPath)
            {
                var dir = Path.GetDirectoryName(m3uPath) ?? string.Empty;
                var parts = dir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Any(p => p.Equals("albums", StringComparison.OrdinalIgnoreCase)))
                    return 2;
                if (parts.Any(p => p.Equals("singles", StringComparison.OrdinalIgnoreCase)))
                    return 1;
                return 0;
            }

            private static string? ResolveAlbumHintFromM3u(string m3uPath, string? albumHint)
            {
                var resolved = albumHint?.Trim();
                if (!IsGenericAlbumHintName(resolved))
                    return resolved;

                var parentDir = Path.GetDirectoryName(m3uPath);
                var parentName = Path.GetFileName(parentDir);
                if (!IsGenericAlbumHintName(parentName))
                    return parentName;

                var grandParentDir = Path.GetDirectoryName(parentDir ?? string.Empty);
                var grandParentName = Path.GetFileName(grandParentDir);
                if (!IsGenericAlbumHintName(grandParentName))
                    return grandParentName;

                return null;
            }

            private static Dictionary<string, string> BuildM3uTrackAlbumHints(Dictionary<string, M3uEntry> m3uIndex)
            {
                var albumHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var kv in m3uIndex)
                {
                    var albumHint = ResolveAlbumHintFromM3u(kv.Key, kv.Value.AlbumHint);
                    if (string.IsNullOrWhiteSpace(albumHint))
                        continue;

                    var priority = GetAlbumHintPriority(kv.Key);
                    foreach (var trackPath in kv.Value.TrackPaths)
                    {
                        if (!albumHints.TryGetValue(trackPath, out _))
                        {
                            albumHints[trackPath] = albumHint;
                            priorities[trackPath] = priority;
                            continue;
                        }

                        if (priority > priorities[trackPath])
                        {
                            albumHints[trackPath] = albumHint;
                            priorities[trackPath] = priority;
                        }
                    }
                }

                return albumHints;
            }

                    private void LoadM3uIndexCache()
                    {
                try
                {
                    if (!File.Exists(M3uIndexCacheFile))
                        return;

                    var json = File.ReadAllText(M3uIndexCacheFile);
                    var cache = JsonSerializer.Deserialize<M3uIndexCache>(json, CaseInsensitiveOptions);
                    if (cache is null) return;

                    // Use a simple trimmed comparison — Path.GetFullPath() misbehaves on UNC paths
                    // (e.g. \\server\share) and would cause a valid cache to be silently discarded.
                    var cachedRoot  = cache.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var configRoot  = _config.ResolvedMusicPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!string.Equals(cachedRoot, configRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Info($"M3U index cache root '{cachedRoot}' does not match current root '{configRoot}' — discarding cache.");
                        return;
                    }

                    var dict = new Dictionary<string, M3uEntry>(cache.Entries.Length, StringComparer.OrdinalIgnoreCase);
                    foreach (var e in cache.Entries)
                        dict[e.M3uPath] = e;

                    var yearHints = BuildM3uTrackYearHints(dict);
                    var albumHints = BuildM3uTrackAlbumHints(dict);
                    lock (_musicIndexGate)
                    {
                        _m3uIndex = dict;
                        _m3uTrackYearHints = yearHints;
                        _m3uTrackAlbumHints = albumHints;
                    }

                    Logger.Info($"Loaded persistent M3U index: {dict.Count} playlist(s), {yearHints.Count} track-year hint(s), {albumHints.Count} track-album hint(s)");
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to load M3U index cache", ex);
                }
            }

            private void SaveM3uIndexCache()
            {
                if (!Monitor.TryEnter(_m3uIndexCacheSaveLock))
                    return;
                try
                {
                    Dictionary<string, M3uEntry> snapshot;
                    lock (_musicIndexGate)
                        snapshot = _m3uIndex;

                    var cache = new M3uIndexCache
                    {
                        RootPath   = _config.ResolvedMusicPath,
                        CreatedUtc = DateTimeOffset.UtcNow,
                        Entries    = [.. snapshot.Values]
                    };
                    var json = JsonSerializer.Serialize(cache, IndentedOptions);
                    var tmp = M3uIndexCacheFile + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(M3uIndexCacheFile))
                        File.Replace(tmp, M3uIndexCacheFile, destinationBackupFileName: null);
                    else
                        File.Move(tmp, M3uIndexCacheFile);
                    Logger.Info($"Saved M3U index cache: {snapshot.Count} playlist(s)");
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to save M3U index cache", ex);
                }
                finally
                {
                    Monitor.Exit(_m3uIndexCacheSaveLock);
                }
            }

    private void LoadFavorites()
    {
        try
        {
            _favorites = LoadFavoritesFromDisk();
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
            var payload = JsonSerializer.Serialize(_favorites.Order(StringComparer.OrdinalIgnoreCase), IndentedOptions);
            WriteAllTextAtomic(FavoritesFile, payload);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save favorites", ex);
        }
    }

    private static HashSet<string> LoadFavoritesFromDisk()
    {
        if (!File.Exists(FavoritesFile))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var favorites = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FavoritesFile), CaseInsensitiveOptions) ?? [];
        return new HashSet<string>(favorites, StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteAllTextAtomic(string filePath, string content)
    {
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, content);

        if (File.Exists(filePath))
            File.Replace(temporaryPath, filePath, destinationBackupFileName: null);
        else
            File.Move(temporaryPath, filePath);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

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

    private static System.Net.Http.HttpClient CreateLiveTvCatalogHttpClient()
    {
        var client = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(35)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/LiveTV");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static string GetLiveTvCountryName(string countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
            return string.Empty;

        try
        {
            return new RegionInfo(countryCode.ToUpperInvariant()).EnglishName;
        }
        catch
        {
            return countryCode.ToUpperInvariant();
        }
    }

    private static Dictionary<string, string> BuildGuideLanguageMap(JsonElement guidesRoot)
    {
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in guidesRoot.EnumerateArray())
        {
            if (!g.TryGetProperty("channel", out var chEl) || chEl.ValueKind != JsonValueKind.String)
                continue;
            var channelId = (chEl.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(channelId))
                continue;

            if (!g.TryGetProperty("lang", out var langEl) || langEl.ValueKind != JsonValueKind.String)
                continue;
            var lang = (langEl.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(lang))
                continue;

            if (!counts.TryGetValue(channelId, out var map))
            {
                map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                counts[channelId] = map;
            }

            map[lang] = map.TryGetValue(lang, out var c) ? c + 1 : 1;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in counts)
        {
            var best = kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(best.Key))
                result[kv.Key] = best.Key;
        }

        return result;
    }

    private static int GetLiveTvStreamScore(string url, bool hasChannelRef)
    {
        var score = hasChannelRef ? 2 : 0;
        var u = (url ?? string.Empty).ToLowerInvariant();
        if (u.Contains(".m3u8", StringComparison.Ordinal))
            score += 3;
        if (u.Contains("master", StringComparison.Ordinal))
            score += 2;
        if (u.Contains("720", StringComparison.Ordinal) || u.Contains("1080", StringComparison.Ordinal))
            score += 1;
        return score;
    }

    private static string GetLiveTvCatalogDedupKey(string name, string countryCode, string sourceHost)
    {
        var normalizedName = (name ?? string.Empty).Trim();
        var normalizedCountryCode = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedCountryCode == "UK")
            normalizedCountryCode = "GB";

        var normalizedSourceHost = (sourceHost ?? string.Empty).Trim().ToLowerInvariant();
        return $"{normalizedName}|{normalizedCountryCode}|{normalizedSourceHost}";
    }

    private static LiveTvCatalogEntry[] BuildLiveTvCatalog(JsonElement channelsRoot, JsonElement streamsRoot, Dictionary<string, string>? guideLangByChannel)
    {
        var channelsById = new Dictionary<string, (string Name, string CountryCode, string Category, string Language)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in channelsRoot.EnumerateArray())
        {
            if (!c.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                continue;
            var id = (idEl.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = c.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? (nameEl.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                name = id;

            var cc = c.TryGetProperty("country", out var ccEl) && ccEl.ValueKind == JsonValueKind.String
                ? (ccEl.GetString() ?? string.Empty).Trim().ToUpperInvariant()
                : string.Empty;
            if (cc == "UK")
                cc = "GB";

            string category = string.Empty;
            if (c.TryGetProperty("categories", out var catsEl) && catsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in catsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;

                    var candidate = (item.GetString() ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        category = candidate;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(category)
                && c.TryGetProperty("category", out var catEl)
                && catEl.ValueKind == JsonValueKind.String)
            {
                category = (catEl.GetString() ?? string.Empty).Trim();
            }
            var language = string.Empty;
            if (guideLangByChannel is not null && guideLangByChannel.TryGetValue(id, out var guideLang))
                language = guideLang;

            channelsById[id] = (name, cc, category, language);
        }

        var bestBySignature = new Dictionary<string, (LiveTvCatalogEntry Entry, int Score)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in streamsRoot.EnumerateArray())
        {
            if (!s.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;
            var url = (urlEl.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                continue;

            var channelId = s.TryGetProperty("channel", out var chEl) && chEl.ValueKind == JsonValueKind.String
                ? (chEl.GetString() ?? string.Empty).Trim()
                : string.Empty;

            string name;
            string countryCode;
            string category;
            string language;
            if (!string.IsNullOrWhiteSpace(channelId) && channelsById.TryGetValue(channelId, out var meta))
            {
                name = meta.Name;
                countryCode = meta.CountryCode;
                category = meta.Category;
                language = meta.Language;
            }
            else
            {
                name = s.TryGetProperty("name", out var snEl) && snEl.ValueKind == JsonValueKind.String
                    ? (snEl.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                countryCode = string.Empty;
                category = string.Empty;
                language = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var entry = new LiveTvCatalogEntry
            {
                Name = name,
                CountryCode = countryCode,
                CountryName = GetLiveTvCountryName(countryCode),
                Category = category,
                Language = language,
                SourceHost = uri.Host,
                Url = uri.ToString(),
            };
            var score = GetLiveTvStreamScore(entry.Url, !string.IsNullOrWhiteSpace(channelId));
            var dedupeKey = GetLiveTvCatalogDedupKey(entry.Name, entry.CountryCode, entry.SourceHost);

            if (!bestBySignature.TryGetValue(dedupeKey, out var current) || score > current.Score)
                bestBySignature[dedupeKey] = (entry, score);
        }

        return bestBySignature.Values
            .Select(v => v.Entry)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void LoadLiveTvCatalogCache()
    {
        try
        {
            if (!File.Exists(LiveTvCatalogCacheFile))
                return;

            var json = File.ReadAllText(LiveTvCatalogCacheFile);
            var cache = JsonSerializer.Deserialize<LiveTvCatalogCache>(json, CaseInsensitiveOptions);
            if (cache is null)
                return;

            var channels = cache.Channels ?? [];
            lock (_liveTvCatalogGate)
            {
                _liveTvCatalog = channels;
                _liveTvCatalogSavedAtUtc = cache.SavedAtUtc;
            }

            Logger.Info($"Loaded Live TV catalog cache: {channels.Length} channels");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load Live TV catalog cache", ex);
        }
    }

    private void SaveLiveTvCatalogCache()
    {
        if (!Monitor.TryEnter(_liveTvCatalogCacheSaveLock))
            return;

        try
        {
            LiveTvCatalogEntry[] snapshot;
            DateTimeOffset savedAt;
            lock (_liveTvCatalogGate)
            {
                snapshot = _liveTvCatalog;
                savedAt = _liveTvCatalogSavedAtUtc ?? DateTimeOffset.UtcNow;
            }

            var cache = new LiveTvCatalogCache
            {
                SavedAtUtc = savedAt,
                Channels = snapshot,
            };
            var json = JsonSerializer.Serialize(cache, IndentedOptions);
            WriteAllTextAtomic(LiveTvCatalogCacheFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save Live TV catalog cache", ex);
        }
        finally
        {
            Monitor.Exit(_liveTvCatalogCacheSaveLock);
        }
    }

    private bool TryGetLiveTvCatalogFromCache(out LiveTvCatalogEntry[] channels, out DateTimeOffset savedAtUtc)
    {
        lock (_liveTvCatalogGate)
        {
            channels = _liveTvCatalog;
            savedAtUtc = _liveTvCatalogSavedAtUtc ?? DateTimeOffset.MinValue;
            if (channels.Length == 0 || savedAtUtc == DateTimeOffset.MinValue)
                return false;

            // Older cache schema had empty category/language for nearly all channels.
            // Force one refresh attempt so command-bar filters can populate properly.
            var hasCategory = channels.Any(c => !string.IsNullOrWhiteSpace(c.Category));
            var hasLanguage = channels.Any(c => !string.IsNullOrWhiteSpace(c.Language));
            if (!hasCategory && !hasLanguage)
                return false;

            return DateTimeOffset.UtcNow - savedAtUtc < LiveTvCatalogCacheTtl;
        }
    }

    private async Task<(LiveTvCatalogEntry[] channels, DateTimeOffset savedAtUtc)> FetchAndCacheLiveTvCatalogAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var attemptUtc = DateTimeOffset.UtcNow;
        var channelsStatusCode = 0;
        var streamsStatusCode = 0;
        var lastError = string.Empty;
        try
        {
            using var channelsResp = await _liveTvCatalogHttpClient.GetAsync("https://iptv-org.github.io/api/channels.json", ct).ConfigureAwait(false);
            channelsStatusCode = (int)channelsResp.StatusCode;
            channelsResp.EnsureSuccessStatusCode();

            using var streamsResp = await _liveTvCatalogHttpClient.GetAsync("https://iptv-org.github.io/api/streams.json", ct).ConfigureAwait(false);
            streamsStatusCode = (int)streamsResp.StatusCode;
            streamsResp.EnsureSuccessStatusCode();

            JsonDocument? guidesDoc = null;
            Dictionary<string, string>? guideLangByChannel = null;
            try
            {
                using var guidesResp = await _liveTvCatalogHttpClient.GetAsync("https://iptv-org.github.io/api/guides.json", ct).ConfigureAwait(false);
                if (guidesResp.IsSuccessStatusCode)
                {
                    await using var guidesStream = await guidesResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    guidesDoc = await JsonDocument.ParseAsync(guidesStream, cancellationToken: ct).ConfigureAwait(false);
                    if (guidesDoc.RootElement.ValueKind == JsonValueKind.Array)
                        guideLangByChannel = BuildGuideLanguageMap(guidesDoc.RootElement);
                }
            }
            catch
            {
                // Guides are best-effort; catalog should still load without them.
            }

            await using var channelsStream = await channelsResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var streamsStream = await streamsResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var channelsDoc = await JsonDocument.ParseAsync(channelsStream, cancellationToken: ct).ConfigureAwait(false);
            using var streamsDoc = await JsonDocument.ParseAsync(streamsStream, cancellationToken: ct).ConfigureAwait(false);

            if (channelsDoc.RootElement.ValueKind != JsonValueKind.Array || streamsDoc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Live TV source payload is not in expected array format.");

            var merged = BuildLiveTvCatalog(channelsDoc.RootElement, streamsDoc.RootElement, guideLangByChannel);
            guidesDoc?.Dispose();
            var savedAt = DateTimeOffset.UtcNow;
            lock (_liveTvCatalogGate)
            {
                _liveTvCatalog = merged;
                _liveTvCatalogSavedAtUtc = savedAt;
            }
            SaveLiveTvCatalogCache();

            lock (_liveTvCatalogDiagGate)
            {
                _liveTvCatalogLastSuccessUtc = savedAt;
                _liveTvCatalogLastError = string.Empty;
            }

            return (merged, savedAt);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            lock (_liveTvCatalogDiagGate)
            {
                _liveTvCatalogLastAttemptUtc = attemptUtc;
                _liveTvCatalogLastFetchDurationMs = sw.ElapsedMilliseconds;
                _liveTvCatalogLastChannelsStatusCode = channelsStatusCode;
                _liveTvCatalogLastStreamsStatusCode = streamsStatusCode;
                if (!string.IsNullOrWhiteSpace(lastError))
                    _liveTvCatalogLastError = lastError;
            }
        }
    }

    private object GetLiveTvCatalogDiagnosticsSnapshot()
    {
        DateTimeOffset? savedAtUtc;
        var channelCount = 0;
        lock (_liveTvCatalogGate)
        {
            savedAtUtc = _liveTvCatalogSavedAtUtc;
            channelCount = _liveTvCatalog.Length;
        }

        lock (_liveTvCatalogDiagGate)
        {
            return new
            {
                lastAttemptUtc = _liveTvCatalogLastAttemptUtc,
                lastSuccessUtc = _liveTvCatalogLastSuccessUtc,
                lastSavedAtUtc = savedAtUtc,
                channelCount,
                channelsStatusCode = _liveTvCatalogLastChannelsStatusCode,
                streamsStatusCode = _liveTvCatalogLastStreamsStatusCode,
                fetchDurationMs = _liveTvCatalogLastFetchDurationMs,
                lastError = _liveTvCatalogLastError,
            };
        }
    }

    private async Task<(LiveTvCatalogEntry[] channels, DateTimeOffset savedAtUtc)> GetLiveTvCatalogAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh)
        {
            if (TryGetLiveTvCatalogFromCache(out var cached, out var savedAtUtc))
                return (cached, savedAtUtc);

            // First-request safety: if startup background load has not populated memory yet,
            // load from disk synchronously before attempting remote refresh.
            LoadLiveTvCatalogCache();
            if (TryGetLiveTvCatalogFromCache(out cached, out savedAtUtc))
                return (cached, savedAtUtc);
        }

        Task<(LiveTvCatalogEntry[] channels, DateTimeOffset savedAtUtc)> refreshTask;
        lock (_liveTvCatalogRefreshGate)
        {
            if (_liveTvCatalogRefreshTask is null || _liveTvCatalogRefreshTask.IsCompleted)
            {
                _liveTvCatalogRefreshTask = FetchLiveTvCatalogSingleFlightAsync();
            }

            refreshTask = _liveTvCatalogRefreshTask;
        }

        try
        {
            var refreshed = await refreshTask.WaitAsync(ct).ConfigureAwait(false);
            Logger.Info($"Live TV catalog refreshed: {refreshed.channels.Length} channels");
            return refreshed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to refresh Live TV catalog", ex);

            // If refresh fails, retry loading persisted disk cache in case memory cache
            // was not yet hydrated during startup race conditions.
            LoadLiveTvCatalogCache();

            lock (_liveTvCatalogGate)
            {
                if (_liveTvCatalog.Length > 0)
                    return (_liveTvCatalog, _liveTvCatalogSavedAtUtc ?? DateTimeOffset.UtcNow);
            }

            throw;
        }
    }

    private async Task<(LiveTvCatalogEntry[] channels, DateTimeOffset savedAtUtc)> FetchLiveTvCatalogSingleFlightAsync()
    {
        using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_listenerCts.Token);
        refreshCts.CancelAfter(LiveTvCatalogRefreshTimeout);
        return await FetchAndCacheLiveTvCatalogAsync(refreshCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="clientIp"/> has exceeded the configured
    /// request quota for the current sliding window. Always returns <c>false</c> when
    /// <see cref="AppConfig.MaxRequestsPerIpPerWindow"/> is zero (rate limiting disabled).
    /// </summary>
    private static bool IsLocalAddress(string clientIp)
    {
        if (string.Equals(clientIp, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(clientIp, out var addr))
            return false;
        if (IPAddress.IsLoopback(addr))
            return true;
        var bytes = addr.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsPrivateOrLoopbackHost(string host)
    {
        var normalizedHost = (host ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return true;

        if (string.Equals(normalizedHost, "localhost", StringComparison.OrdinalIgnoreCase)
            || normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(normalizedHost, out var address))
            return false;

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;

        var mappedAddress = address.MapToIPv4();
        var bytes = mappedAddress.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    internal bool IsRateLimited(string clientIp)
    {
        if (IsLocalAddress(clientIp))
            return false;

        var limit = _config.MaxRequestsPerIpPerWindow;
        if (limit <= 0)
            return false;

        var windowTicks = TimeSpan.FromSeconds(Math.Max(1, _config.RateLimitWindowSeconds)).Ticks;
        var bucket = _rateLimitBuckets.GetOrAdd(clientIp, _ => new RateLimitBucket());

        lock (bucket)
        {
            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            if (nowTicks - bucket.WindowStartTicks >= windowTicks)
            {
                bucket.Count = 1;
                bucket.WindowStartTicks = nowTicks;
                return false;
            }

            bucket.Count++;
            return bucket.Count > limit;
        }
    }

    public void Start()
    {
        ValidateStartupConfiguration();
        _activeScheme = _config.Scheme;
        _startupWarning = null;
        _ = Task.Run(() =>
        {
            LoadLibraryIndexCache();
            StartLibraryIndexRefresh(force: false);
            LoadMusicIndexCache();
            LoadM3uIndexCache();
            LoadLiveTvCatalogCache();
            // If no cache exists yet (first run), do a full scan to populate the index.
            // Otherwise, treat the loaded cache as authoritative until the user triggers /api/music/rescan.
            // Either way, always run a lightweight M3U-only scan so _m3uIndex is populated
            // immediately - without it, dynamic-folder include/exclude filtering returns no tracks.
            int cached;
            lock (_musicIndexGate) cached = _musicIndex.Length;
            if (cached == 0)
            {
                Logger.Info("No music index cache found - starting initial scan.");
                StartMusicIndexRefresh(); // full scan includes M3U pass
            }
            else
            {
                StartM3uIndexRefresh(); // fast M3U-only pass; track index already loaded
            }
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
        _listenerCts = new CancellationTokenSource();
        Task.Run(() => ListenLoopAsync(_listenerCts.Token));
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
        try { _listenerCts.Cancel(); } catch { }
        try { DisposeAllSseClients(); } catch { }
        try { _listenerCts.Dispose(); } catch { }
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
        var isNewSchedule = _pendingLibraryRescanUtc == null;
        _pendingLibraryRescanUtc = dueUtc;
        if (isNewSchedule)
            Logger.Info($"Library change detected; rescan scheduled in {_config.LibraryRescanDelayMinutes} minute(s)");
        _libraryWatcherDebounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void RunDelayedLibraryRescan()
    {
        var pendingUtc = _pendingLibraryRescanUtc;
        _pendingLibraryRescanUtc = null;
        Logger.Info($"Delayed library rescan starting (scheduled for {pendingUtc:u})");
        StartLibraryIndexRefresh(force: true);
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
            // Client disconnected before the response was fully sent â€” not an error.
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
            // Client disconnected before the response was fully sent â€” not an error.
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
