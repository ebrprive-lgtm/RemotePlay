using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public void UpdateExpertMode(bool expertMode) => _expertMode = expertMode;
    public void UpdateDebugMode(bool debugMode) => _debugMode = debugMode;

    private readonly object _libraryIndexGate = new();
    private readonly object _musicIndexGate = new();
    private readonly object _libraryIndexCacheSaveLock = new();
    private readonly object _musicIndexCacheSaveLock = new();
    private readonly object _m3uIndexCacheSaveLock   = new();
    // Lazily populated by background probes so the browse path stays fast.
    private readonly ConcurrentDictionary<string, double> _videoDurationCache = new(StringComparer.OrdinalIgnoreCase);
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
    private bool _isM3uIndexing;    // true while the lightweight M3U-only background scan is running
    // In-memory M3U index: keyed by absolute M3U file path. Rebuilt alongside _musicIndex.
    private Dictionary<string, M3uEntry> _m3uIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _expertMode;             // updated live when settings change without server restart
    private bool _debugMode;              // updated live when settings change without server restart
    private bool _browserLocalPlaying;   // true while browser is playing audio locally
    private string? _musicPlayInitiatorIp; // IP of the client that last triggered /api/music/play
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

    public WebServer(AppConfig config, WebServerCallbacks callbacks, PresenceBroadcaster? broadcaster = null, PlaybackHistory? playbackHistory = null, RemotePlay.Services.AppUpdater? appUpdater = null, RemotePlay.Services.Discovery.DlnaDiscovery? dlna = null)
    {
        _config = config;
        _expertMode = config.ExpertMode;
        _debugMode  = config.DebugMode;
        _callbacks = callbacks;
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

                    lock (_musicIndexGate)
                        _m3uIndex = dict;

                    Logger.Info($"Loaded persistent M3U index: {dict.Count} playlist(s)");
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

    /// <summary>
    /// Returns <c>true</c> when <paramref name="clientIp"/> has exceeded the configured
    /// request quota for the current sliding window. Always returns <c>false</c> when
    /// <see cref="AppConfig.MaxRequestsPerIpPerWindow"/> is zero (rate limiting disabled).
    /// </summary>
    private static bool IsLocalAddress(string clientIp)
    {
        if (!IPAddress.TryParse(clientIp, out var addr))
            return false;
        if (IPAddress.IsLoopback(addr))
            return true;
        var bytes = addr.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private bool IsRateLimited(string clientIp)
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
