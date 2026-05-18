using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using QRCoder;
using RemotePlay.Models;
using RemotePlay.Services.Discovery;
using Timer = System.Threading.Timer;

namespace RemotePlay;

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

internal sealed record TrackOption(int Id, string Name, string Language = "", bool IsForced = false, bool IsDefault = false);

internal sealed record PlaybackQueueItem(string Path, string Title);

internal sealed record LibraryScanStatus
{
    public bool IsScanning { get; init; }
    public int IndexedFiles { get; init; }
    public int ScannedFiles { get; init; }
    public int ScannedFolders { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string LastError { get; init; } = string.Empty;
}

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

internal sealed record LibraryIndexCache
{
    public string RootPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public LibraryFile[] Files { get; init; } = [];
}

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

internal sealed partial class WebServer
{
    private const string WebAssetsDirectoryName = "WebAssets";
    private static readonly string ThumbnailCacheDirectory = AppPaths.ThumbnailCacheDirectory;
    private static readonly string LibraryIndexCacheFile = AppPaths.LibraryIndexCacheFile;
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
    private static readonly Lazy<string> _cachedAppJs =
        new(() => LoadWebAsset("app.js"));
    private static readonly Lazy<string> _cachedServiceWorkerJsRaw =
        new(() => LoadWebAsset("service-worker.js"));
    private static readonly string _appVersion = ReadAppVersion();

    private readonly AppConfig _config;
    private readonly WebServerCallbacks _callbacks;
    private readonly PresenceBroadcaster? _broadcaster;
    private readonly PlaybackHistory _playbackHistory;
    private readonly HashSet<string> _videoExtensions;
    private readonly HashSet<string> _hiddenFolderNames;
    private HttpListener _listener = new();
    // Thumbnail cache: base64-encoded path ? JPEG bytes (null = not available)
    private readonly ConcurrentDictionary<string, byte[]?> _thumbCache = new();
    private readonly object _favoritesGate = new();
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _libraryIndexTimer;
    private readonly Timer _libraryWatcherDebounceTimer;
    private readonly object _libraryIndexGate = new();
    private FileSystemWatcher? _libraryWatcher;
    private LibraryFile[] _libraryIndex = [];
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastIndexRefreshUtc;
    private DateTimeOffset _libraryWatcherStartedUtc;
    private DateTimeOffset? _pendingLibraryRescanUtc;
    private DateTimeOffset? _scanStartedUtc;
    private int _scannedFiles;
    private int _scannedFolders;
    private string _lastScanError = string.Empty;
    private bool _isIndexing;
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
        _hiddenFolderNames = BuildNameSet(config.EffectiveIgnoredLibraryFolders, HiddenFolderNames);
        _activeScheme = config.Scheme;
        _appUpdater = appUpdater;
        LoadFavorites();
        _libraryIndexTimer = new Timer(_ => RefreshLibraryIndexIfIdle(), null,
            TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        _libraryWatcherDebounceTimer = new Timer(_ => RunDelayedLibraryRescan(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string ActiveScheme => _activeScheme;
    public string? StartupWarning => _startupWarning;
    public int LibraryVideoCount => _libraryIndex.Length;
    public LibraryScanStatus LibraryStatus => GetLibraryScanStatus();

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

    private LibraryScanStatus GetLibraryScanStatus() => new()
    {
        IsScanning = _isIndexing,
        IndexedFiles = _libraryIndex.Length,
        ScannedFiles = _scannedFiles,
        ScannedFolders = _scannedFolders,
        StartedUtc = _scanStartedUtc,
        CompletedUtc = _lastIndexRefreshUtc,
        LastError = _lastScanError
    };

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

    private static HashSet<string> BuildExtensionSet(IEnumerable<string>? values)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var extension = value.Trim();
            extensions.Add(extension.StartsWith('.') ? extension : "." + extension);
        }

        return extensions.Count > 0 ? extensions : new HashSet<string>(VideoExtensions, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildNameSet(IEnumerable<string>? values, IEnumerable<string> fallback)
    {
        var names = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray() ?? [];

        return names.Length > 0
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(fallback, StringComparer.OrdinalIgnoreCase);
    }

    public void Start()
    {
        ValidateStartupConfiguration();
        _activeScheme = _config.Scheme;
        _startupWarning = null;
        _ = Task.Run(() =>
        {
            LoadLibraryIndexCache();
            if (_config.RescanLibraryOnStartup || _libraryIndex.Length == 0)
                StartLibraryIndexRefresh(force: false);
            else
                Logger.Info($"Library index cache ready: {_libraryIndex.Length} videos; background watcher will keep it fresh");
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
        StartLibraryIndexRefresh(force: true);
    }

    public void Stop()
    {
        try { CancelThumbnailQueue(); } catch { }
        try { _thumbnailQueueCancellation?.Dispose(); } catch { }
        try { _libraryIndexTimer.Dispose(); } catch { }
        try { _libraryWatcherDebounceTimer.Dispose(); } catch { }
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
