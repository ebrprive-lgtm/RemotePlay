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
    public double Brightness { get; init; }
    public double AudioBoost { get; init; } = 1;
    public double PlaybackSpeed { get; init; } = 1;
    public bool SubtitlesEnabled { get; init; }
    public bool HasSubtitles { get; init; }
}

internal sealed class WebServer
{
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv" };
    private static readonly HashSet<string> HiddenFolderNames =
        new(StringComparer.OrdinalIgnoreCase) { "Subs", "Alt" };
    private static readonly Guid HttpsCertificateAppId = new("be2a8b40-850e-4ef1-a893-b0b13f5c7fd9");
    private const string HttpsCertificateSubject = "CN=RemotePlay Local HTTPS";

    private readonly AppConfig _config;
    private HttpListener _listener = new();
    private readonly Action<string> _onPlay;
    private readonly Action _onStop;
    private readonly Action _onPause;
    private readonly Func<PlaybackStatus> _onGetStatus;
    private readonly Action<double> _onSeek;
    private readonly Action<double> _onSkip;
    private readonly Action<double> _onSetVolume;
    private readonly Action _onToggleMute;
    private readonly Action<double> _onSetBrightness;
    private readonly Action<double> _onSetAudioBoost;
    private readonly Action<double> _onSetPlaybackSpeed;
    private readonly Action _onToggleSubtitles;
    // Thumbnail cache: base64-encoded path → JPEG bytes (null = not available)
    private readonly ConcurrentDictionary<string, byte[]?> _thumbCache = new();
    private readonly Timer _libraryIndexTimer;
    private readonly object _libraryIndexGate = new();
    private LibraryFile[] _libraryIndex = [];
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastIndexRefreshUtc;
    private bool _isIndexing;
    private string _activeScheme;
    private string? _startupWarning;

    public WebServer(AppConfig config, Action<string> onPlay, Action onStop, Action onPause,
        Func<PlaybackStatus> onGetStatus, Action<double> onSeek, Action<double> onSkip,
        Action<double> onSetVolume, Action onToggleMute, Action<double> onSetBrightness,
        Action<double> onSetAudioBoost, Action<double> onSetPlaybackSpeed, Action onToggleSubtitles)
    {
        _config = config;
        _onPlay = onPlay;
        _onStop = onStop;
        _onPause = onPause;
        _onGetStatus = onGetStatus;
        _onSeek = onSeek;
        _onSkip = onSkip;
        _onSetVolume = onSetVolume;
        _onToggleMute = onToggleMute;
        _onSetBrightness = onSetBrightness;
        _onSetAudioBoost = onSetAudioBoost;
        _onSetPlaybackSpeed = onSetPlaybackSpeed;
        _onToggleSubtitles = onToggleSubtitles;
        _activeScheme = config.Scheme;
        _libraryIndexTimer = new Timer(_ => RefreshLibraryIndexIfIdle(), null,
            TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    public string ActiveScheme => _activeScheme;
    public string? StartupWarning => _startupWarning;

    public void Start()
    {
        _activeScheme = _config.Scheme;
        _startupWarning = null;

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
        Logger.Info($"Web server listening on {_activeScheme}://*:{_config.Port}");
        Task.Run(ListenLoopAsync);
    }

    public void Stop()
    {
        try { _libraryIndexTimer.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
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

    private async Task ListenLoopAsync()
    {
        while (_listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestSafe(ctx));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error("Unexpected error in listen loop", ex);
            }
        }
    }

    private void HandleRequestSafe(HttpListenerContext ctx)
    {
        try { HandleRequest(ctx); }
        catch (Exception ex)
        {
            Logger.Error("Error handling request", ex);
            TrySendResponse(ctx, 500, "text/plain", "Internal server error");
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var urlPath = req.Url?.AbsolutePath ?? "/";
        _lastRequestUtc = DateTimeOffset.UtcNow;

        Logger.Info($"{req.HttpMethod} {urlPath}");

        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

        switch (urlPath)
        {
            case "/" or "/index.html":
                TrySendResponse(ctx, 200, "text/html; charset=utf-8", HtmlPage);
                break;

            case "/manifest.webmanifest":
                TrySendResponse(ctx, 200, "application/manifest+json; charset=utf-8", ManifestJson);
                break;

            case "/service-worker.js":
                ctx.Response.AddHeader("Cache-Control", "no-cache");
                TrySendResponse(ctx, 200, "application/javascript; charset=utf-8", ServiceWorkerJs);
                break;

            case "/icons/icon-192.png":
                HandleStaticIcon(ctx, "icon-192.png");
                break;

            case "/icons/icon-512.png":
                HandleStaticIcon(ctx, "icon-512.png");
                break;

            case "/icons/apple-touch-icon.png":
                HandleStaticIcon(ctx, "apple-touch-icon.png");
                break;

            case "/health":
                HandleHealthPage(ctx);
                break;

            case "/certificate.cer":
                HandleCertificateDownload(ctx);
                break;

            case "/qr.svg":
                HandleQrCode(ctx);
                break;

            case "/setup-code.png":
                HandleSetupCodePng(ctx);
                break;

            // Returns the subfolders and video files in a given directory.
            // Query param: dir=<base64-encoded absolute path>  (omit for root movies folder)
            case "/api/browse":
                HandleBrowse(ctx);
                break;

            case "/api/search":
                HandleSearch(ctx);
                break;

            case "/api/rescan":
                StartLibraryIndexRefresh(force: true);
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, indexing = true }));
                break;

            case "/api/play":
                HandlePlay(ctx);
                break;

            case "/api/stop":
                _onStop();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/pause":
                _onPause();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/thumb":
                HandleThumb(ctx);
                break;

            case "/api/status":
                HandleStatus(ctx);
                break;

            case "/api/seek":
                HandleSeek(ctx);
                break;

            case "/api/skip":
                HandleSkip(ctx);
                break;

            case "/api/volume":
                HandleVolume(ctx);
                break;

            case "/api/brightness":
                HandleBrightness(ctx);
                break;

            case "/api/audio-boost":
                HandleAudioBoost(ctx);
                break;

            case "/api/speed":
                HandlePlaybackSpeed(ctx);
                break;

            case "/api/subtitles":
                _onToggleSubtitles();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/mute":
                _onToggleMute();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/health":
                HandleHealth(ctx);
                break;

            default:
                TrySendResponse(ctx, 404, "text/plain", "Not found");
                break;
        }
    }

    private void HandleThumb(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var jpeg = _thumbCache.GetOrAdd(encodedPath, key =>
        {
            try
            {
                var filePath = WebPathHelpers.DecodePath(key);
                return ThumbnailHelper.GetJpegThumbnail(filePath);
            }
            catch { return null; }
        });

        if (jpeg is null)
        {
            TrySendResponse(ctx, 404, "text/plain", "No thumbnail");
            return;
        }

        ctx.Response.AddHeader("Cache-Control", "public, max-age=3600");
        TrySendBytes(ctx, 200, "image/jpeg", jpeg);
    }

    private static void HandleStaticIcon(HttpListenerContext ctx, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Pwa", fileName);
        if (!File.Exists(path))
        {
            TrySendResponse(ctx, 404, "text/plain", "Icon not found");
            return;
        }

        ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");
        TrySendBytes(ctx, 200, "image/png", File.ReadAllBytes(path));
    }

    private void HandleStatus(HttpListenerContext ctx)
    {
        var s = _onGetStatus();
        var json = JsonSerializer.Serialize(new
        {
            isPlaying  = s.IsPlaying,
            isPaused   = s.IsPaused,
            position   = Math.Round(s.PositionSeconds, 1),
            duration   = Math.Round(s.DurationSeconds, 1),
            title      = s.Title ?? string.Empty,
            volume     = Math.Round(s.Volume, 2),
            brightness = Math.Round(s.Brightness, 2),
            audioBoost = Math.Round(s.AudioBoost, 2),
            playbackSpeed = Math.Round(s.PlaybackSpeed, 2),
            isMuted    = s.IsMuted,
            lastError  = s.LastError ?? string.Empty,
            canResume  = s.CanResume,
            subtitlesEnabled = s.SubtitlesEnabled,
            hasSubtitles = s.HasSubtitles
        });
        TrySendResponse(ctx, 200, "application/json", json);
    }

    private void HandleSeek(HttpListenerContext ctx)
    {
        var posParam = ctx.Request.QueryString["pos"];
        if (double.TryParse(posParam, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
        {
            _onSeek(secs);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad pos");
        }
    }

    private void HandleSkip(HttpListenerContext ctx)
    {
        var secondsParam = ctx.Request.QueryString["seconds"];
        if (double.TryParse(secondsParam, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            _onSkip(seconds);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad seconds");
        }
    }

    private void HandleVolume(HttpListenerContext ctx)
    {
        var valueParam = ctx.Request.QueryString["value"];
        if (double.TryParse(valueParam, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var volume))
        {
            _onSetVolume(Math.Clamp(volume, 0, 1));
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad volume");
        }
    }

    private void HandleBrightness(HttpListenerContext ctx)
    {
        var valueParam = ctx.Request.QueryString["value"];
        if (double.TryParse(valueParam, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var brightness))
        {
            _onSetBrightness(Math.Clamp(brightness, 0, 1));
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad brightness");
        }
    }

    private void HandleAudioBoost(HttpListenerContext ctx)
    {
        var valueParam = ctx.Request.QueryString["value"];
        if (double.TryParse(valueParam, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var audioBoost))
        {
            _onSetAudioBoost(Math.Clamp(audioBoost, 1, 2));
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad audio boost");
        }
    }

    private void HandlePlaybackSpeed(HttpListenerContext ctx)
    {
        var valueParam = ctx.Request.QueryString["value"];
        if (double.TryParse(valueParam, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var playbackSpeed))
        {
            _onSetPlaybackSpeed(Math.Clamp(playbackSpeed, 0.5, 2));
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad speed");
        }
    }

    private void HandleHealth(HttpListenerContext ctx)
    {
        var status = _onGetStatus();
        var json = JsonSerializer.Serialize(new
        {
            ok = true,
            requestedScheme = _config.Scheme,
            activeScheme = _activeScheme,
            startupWarning = _startupWarning ?? string.Empty,
            port = _config.Port,
            moviesPath = _config.ResolvedMoviesPath,
            isPlaying = status.IsPlaying,
            lastError = status.LastError ?? string.Empty,
            indexedFiles = _libraryIndex.Length,
            isIndexing = _isIndexing,
            lastIndexRefreshUtc = _lastIndexRefreshUtc
        });
        TrySendResponse(ctx, 200, "application/json", json);
    }

    private void HandleHealthPage(HttpListenerContext ctx)
    {
        var status = _onGetStatus();
        var certificate = TryGetHttpsCertificate();
        var html = $$"""
            <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width,initial-scale=1"/>
            <title>RemotePlay Health</title>
            <style>body{background:#111;color:#eee;font-family:Segoe UI,Arial,sans-serif;margin:0;padding:24px}main{max-width:760px;margin:auto}h1{color:#e94560}.card{background:#1a1a2e;border:1px solid #333;border-radius:8px;padding:16px;margin:12px 0}.ok{color:#00d4aa}.warn{color:#ffaa00}dt{color:#888;margin-top:10px}dd{margin:3px 0 0 0;word-break:break-word}a{color:#00d4aa}</style>
            </head><body><main><h1>RemotePlay Health</h1>
            <section class="card"><h2 class="ok">Server</h2><dl>
            <dt>Requested mode</dt><dd>{{HtmlEncode(_config.Scheme.ToUpperInvariant())}}</dd>
            <dt>Active mode</dt><dd>{{HtmlEncode(_activeScheme.ToUpperInvariant())}}</dd>
            <dt>Port</dt><dd>{{_config.Port}}</dd>
            <dt>Movies folder</dt><dd>{{HtmlEncode(_config.ResolvedMoviesPath)}}</dd>
            <dt>Startup warning</dt><dd class="warn">{{HtmlEncode(_startupWarning ?? "None")}}</dd>
            </dl></section>
            <section class="card"><h2>Playback</h2><dl>
            <dt>Playing</dt><dd>{{status.IsPlaying}}</dd>
            <dt>Last error</dt><dd>{{HtmlEncode(string.IsNullOrWhiteSpace(status.LastError) ? "None" : status.LastError)}}</dd>
            </dl></section>
            <section class="card"><h2>Library index</h2><dl>
            <dt>Indexed videos</dt><dd>{{_libraryIndex.Length}}</dd>
            <dt>Indexing now</dt><dd>{{_isIndexing}}</dd>
            <dt>Last refresh UTC</dt><dd>{{_lastIndexRefreshUtc?.ToString("u") ?? "Never"}}</dd>
            </dl></section>
            <section class="card"><h2>HTTPS certificate</h2><dl>
            <dt>Certificate present</dt><dd>{{certificate is not null}}</dd>
            <dt>Expires</dt><dd>{{certificate?.NotAfter.ToString("u") ?? "N/A"}}</dd>
            <dt>Thumbprint</dt><dd>{{HtmlEncode(certificate?.Thumbprint ?? "N/A")}}</dd>
            </dl><p><a href="/certificate.cer">Download certificate</a></p></section>
            </main></body></html>
            """;
        certificate?.Dispose();
        TrySendResponse(ctx, 200, "text/html; charset=utf-8", html);
    }

    private static void HandleCertificateDownload(HttpListenerContext ctx)
    {
        using var certificate = TryGetHttpsCertificate();
        if (certificate is null)
        {
            TrySendResponse(ctx, 404, "text/plain", "HTTPS certificate not found. Enable HTTPS once to create it.");
            return;
        }

        ctx.Response.AddHeader("Content-Disposition", "attachment; filename=RemotePlay-Local-HTTPS.cer");
        TrySendBytes(ctx, 200, "application/x-x509-ca-cert", certificate.Export(X509ContentType.Cert));
    }

    private void HandleQrCode(HttpListenerContext ctx)
    {
        var target = ctx.Request.QueryString["url"];
        if (string.IsNullOrWhiteSpace(target))
            target = $"{_activeScheme}://{ctx.Request.UserHostName?.Split(':')[0]}:{_config.Port}/";

        TrySendResponse(ctx, 200, "image/svg+xml; charset=utf-8", BuildSetupQrSvg(target));
    }

    private void HandleSetupCodePng(HttpListenerContext ctx)
    {
        var target = ctx.Request.QueryString["url"];
        if (string.IsNullOrWhiteSpace(target))
            target = $"{_activeScheme}://{ctx.Request.UserHostName?.Split(':')[0]}:{_config.Port}/";

        TrySendBytes(ctx, 200, "image/png", BuildSetupCodePng(target));
    }

    private void HandleSearch(HttpListenerContext ctx)
    {
        var query = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        StartLibraryIndexRefresh(force: false);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = terms.Length == 0
            ? Array.Empty<object>()
            : _libraryIndex
                .Where(f => terms.All(t => f.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Name)
                .Take(200)
                .Select(f => new { name = f.Name, path = f.EncodedPath, folder = f.FolderName })
                .ToArray<object>();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            files,
            total = files.Length,
            indexedFiles = _libraryIndex.Length,
            indexing = _isIndexing,
            lastRefreshUtc = _lastIndexRefreshUtc
        }));
    }

    private void HandleBrowse(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var dirParam = ctx.Request.QueryString["dir"];

        string targetDir;
        if (string.IsNullOrWhiteSpace(dirParam))
        {
            targetDir = root;
        }
        else
        {
            targetDir = WebPathHelpers.DecodePath(dirParam);
            // Security: ensure path stays within the configured root
            if (!WebPathHelpers.IsUnderRoot(targetDir, root))
            {
                TrySendResponse(ctx, 403, "text/plain", "Forbidden");
                return;
            }
        }

        if (!Directory.Exists(targetDir))
        {
            try { Directory.CreateDirectory(targetDir); } catch { }
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { folders = Array.Empty<object>(), files = Array.Empty<object>(), current = targetDir, isRoot = true, breadcrumbs = Array.Empty<object>() }));
            return;
        }

        var folders = Directory.EnumerateDirectories(targetDir)
            .Where(d => !HiddenFolderNames.Contains(Path.GetFileName(d)))
            .OrderBy(d => d)
            .Select(d => new
            {
                name = Path.GetFileName(d),
                dir = WebPathHelpers.EncodePath(d)
            })
            .ToArray();

        var files = Directory.EnumerateFiles(targetDir)
            .Where(f => WebPathHelpers.IsVideoFile(f, VideoExtensions))
            .OrderBy(f => f)
            .Select(f => new
            {
                name = Path.GetFileNameWithoutExtension(f),
                path = WebPathHelpers.EncodePath(f)
            })
            .ToArray();

        // Parent dir (null if we're already at root)
        string? parentEncoded = null;
        var parent = Path.GetDirectoryName(targetDir);
        if (parent is not null && WebPathHelpers.IsUnderRoot(parent, root))
            parentEncoded = WebPathHelpers.EncodePath(parent);

        var result = JsonSerializer.Serialize(new
        {
            folders,
            files,
            current = Path.GetFileName(targetDir),
            currentFull = targetDir,
            parent = parentEncoded,
            breadcrumbs = BuildBreadcrumbs(root, targetDir),
            isRoot = string.Equals(targetDir, root, StringComparison.OrdinalIgnoreCase)
        });

        TrySendResponse(ctx, 200, "application/json", result);
    }

    private void HandlePlay(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }

        _onPlay(filePath);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void RefreshLibraryIndexIfIdle()
    {
        if (DateTimeOffset.UtcNow - _lastRequestUtc < TimeSpan.FromHours(1))
            return;

        if (_lastIndexRefreshUtc is not null && DateTimeOffset.UtcNow - _lastIndexRefreshUtc.Value < TimeSpan.FromDays(1))
            return;

        StartLibraryIndexRefresh(force: false);
    }

    private void StartLibraryIndexRefresh(bool force)
    {
        lock (_libraryIndexGate)
        {
            if (_isIndexing)
                return;

            if (!force && _libraryIndex.Length > 0 && _lastIndexRefreshUtc is not null)
                return;

            _isIndexing = true;
        }

        Task.Run(() =>
        {
            try
            {
                var root = _config.ResolvedMoviesPath;
                if (!Directory.Exists(root))
                {
                    _libraryIndex = [];
                    return;
                }

                var files = EnumerateLibraryVideoFiles(root)
                    .Where(f => WebPathHelpers.IsVideoFile(f, VideoExtensions))
                    .Select(f => new LibraryFile(
                        Path.GetFileNameWithoutExtension(f),
                        WebPathHelpers.EncodePath(f),
                        Path.GetFileName(Path.GetDirectoryName(f)) ?? string.Empty,
                        BuildSearchText(root, f)))
                    .ToArray();

                _libraryIndex = files;
                _lastIndexRefreshUtc = DateTimeOffset.UtcNow;
                Logger.Info($"Library index refreshed: {files.Length} videos");
            }
            catch (Exception ex)
            {
                Logger.Error("Library index refresh failed", ex);
            }
            finally
            {
                lock (_libraryIndexGate)
                    _isIndexing = false;
            }
        });
    }

    private static string BuildSearchText(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        return relative.Replace(Path.DirectorySeparatorChar, ' ')
            .Replace(Path.AltDirectorySeparatorChar, ' ');
    }

    private static IEnumerable<string> EnumerateLibraryVideoFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
                yield return file;

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var subdir in subdirs)
            {
                if (!HiddenFolderNames.Contains(Path.GetFileName(subdir)))
                    pending.Push(subdir);
            }
        }
    }

    private static object[] BuildBreadcrumbs(string root, string targetDir)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var crumbs = new List<object>
        {
            new { name = Path.GetFileName(normalizedRoot), dir = WebPathHelpers.EncodePath(normalizedRoot) }
        };

        var relative = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        if (relative == ".")
            return crumbs.ToArray();

        var current = normalizedRoot;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            crumbs.Add(new { name = part, dir = WebPathHelpers.EncodePath(current) });
        }

        return crumbs.ToArray();
    }

    private sealed record LibraryFile(string Name, string EncodedPath, string FolderName, string SearchText);

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

    private const string ServiceWorkerJs = """
        const CACHE_NAME = 'remoteplay-shell-v1';
        const SHELL_ASSETS = [
          '/',
          '/manifest.webmanifest',
          '/icons/icon-192.png',
          '/icons/icon-512.png',
          '/icons/apple-touch-icon.png'
        ];

        self.addEventListener('install', event => {
          event.waitUntil(caches.open(CACHE_NAME).then(cache => cache.addAll(SHELL_ASSETS)));
          self.skipWaiting();
        });

        self.addEventListener('activate', event => {
          event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k)))));
          self.clients.claim();
        });

        self.addEventListener('fetch', event => {
          const url = new URL(event.request.url);
          if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/icons/') || url.pathname === '/service-worker.js') {
            event.respondWith(fetch(event.request));
            return;
          }

          event.respondWith(fetch(event.request).catch(() => caches.match(event.request).then(r => r || caches.match('/'))));
        });
        """;

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

    private static readonly string HtmlPage = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8"/>
        <meta name="viewport" content="width=device-width, initial-scale=1.0"/>
        <meta name="theme-color" content="#1a1a2e"/>
        <meta name="mobile-web-app-capable" content="yes"/>
        <meta name="apple-mobile-web-app-capable" content="yes"/>
        <meta name="apple-mobile-web-app-title" content="RemotePlay"/>
        <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent"/>
        <link rel="manifest" href="/manifest.webmanifest"/>
        <link rel="apple-touch-icon" href="/icons/apple-touch-icon.png"/>
        <title>RemotePlay</title>
        <style>
        *{box-sizing:border-box;margin:0;padding:0}
        body{background:#111;color:#eee;font-family:'Segoe UI',sans-serif;min-height:100vh;display:flex;flex-direction:column}
        header{background:#1a1a2e;padding:.55rem .75rem;display:grid;grid-template-columns:auto minmax(160px,1fr) minmax(360px,720px);gap:.7rem;align-items:start;position:sticky;top:0;z-index:10}
        #brand{display:flex;flex-direction:column;gap:.35rem;align-items:stretch}
        h1{font-size:1.05rem;color:#e94560;white-space:nowrap;line-height:1.9rem}
        #search-wrap{display:flex;flex-direction:column;gap:.2rem;min-width:0}
        #search-row{display:flex;gap:.35rem;min-width:0}
        #search{background:#222;color:#eee;border:1px solid #444;padding:.38rem .6rem;border-radius:4px;font-size:.86rem;flex:1;min-width:100px}
        .btn{border:none;border-radius:4px;cursor:pointer;font-size:.78rem;padding:.36rem .65rem;color:#fff;transition:background .15s}
        .btn-red{background:#e94560}.btn-red:hover{background:#c73652}
        .btn-dim{background:#333}.btn-dim:hover{background:#555}
        #install-hint{display:none;background:#10251f;border-bottom:1px solid #00d4aa;color:#ddfff7;padding:.55rem .75rem;font-size:.82rem;align-items:center;gap:.55rem;justify-content:space-between}
        #install-hint button{background:#00d4aa;color:#06110f;border:none;border-radius:4px;padding:.3rem .55rem;font-weight:700;cursor:pointer}
        #now-playing-bar{display:none;min-width:0;width:100%;flex-direction:column;gap:.6rem;background:linear-gradient(135deg,#0d0d1a,#151526);padding:.7rem;border:1px solid #30304a;border-radius:12px;box-shadow:0 6px 18px rgba(0,0,0,.38)}
        #player-title{font-size:.9rem;color:#fff;font-weight:700;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;min-width:0}
        .player-top{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:.7rem}
        .transport-controls{display:flex;gap:.4rem;align-items:center;justify-content:flex-end;flex-wrap:wrap}
        .transport-controls .btn{font-weight:700;min-width:58px}
        #pause-btn{min-width:92px}
        #progress-row{display:grid;grid-template-columns:1fr auto;align-items:center;gap:.65rem}
        #progress{width:100%;accent-color:#e94560;cursor:pointer;height:12px;min-height:36px;-webkit-appearance:none;background:transparent}
        #progress::-webkit-slider-runnable-track{height:6px;background:#34344f;border-radius:999px}
        #progress::-webkit-slider-thumb{-webkit-appearance:none;width:22px;height:22px;border-radius:50%;background:#e94560;border:2px solid #fff;box-shadow:0 0 0 6px rgba(233,69,96,.2);margin-top:-8px}
        #progress::-moz-range-track{height:6px;background:#34344f;border-radius:999px}
        #progress::-moz-range-thumb{width:22px;height:22px;border-radius:50%;background:#e94560;border:2px solid #fff;box-shadow:0 0 0 6px rgba(233,69,96,.2)}
        #time-label{font-size:.9rem;color:#ddd;white-space:nowrap;min-width:112px;text-align:right;font-weight:600}
        #speed-row{display:flex;gap:.35rem;flex-wrap:wrap;align-items:center;justify-content:center;padding:0 .35rem}
        .speed-chip{border:1px solid #3a3a57;background:#22263b;color:#cfd3e9;padding:.28rem .52rem;border-radius:999px;font-size:.74rem;font-weight:700;cursor:pointer;min-width:52px}
        .speed-chip.active{background:#e94560;border-color:#e94560;color:#fff}
        #media-controls{display:grid;grid-template-columns:repeat(3,minmax(150px,1fr));gap:.55rem;align-items:end;margin-top:.1rem;padding-top:.55rem;border-top:1px solid #292941}
        .control-card{background:#17172a;border:1px solid #2b2b45;border-radius:9px;padding:.5rem;display:flex;flex-direction:column;gap:.35rem;min-width:0}
        @media (min-width:900px){
          body.desktop-player-docked header{grid-template-columns:auto minmax(280px,1fr)}
          body.desktop-player-docked #now-playing-bar{position:fixed;top:70px;right:12px;width:clamp(320px,34vw,460px);max-height:calc(100vh - 82px);overflow:auto;z-index:26;padding:1rem 1rem .95rem;border-radius:14px;gap:.75rem}
          body.desktop-player-docked #browser{padding-right:calc(clamp(320px,34vw,460px) + 18px)}
          body.desktop-player-docked .player-top{grid-template-columns:1fr}
          body.desktop-player-docked #player-title{white-space:normal;overflow:visible;text-overflow:clip;line-height:1.25;max-height:none}
          body.desktop-player-docked .transport-controls{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.55rem;justify-content:stretch}
          body.desktop-player-docked .transport-controls .btn{width:100%;min-height:42px;font-size:.9rem}
          body.desktop-player-docked #pause-btn{min-width:0}
          body.desktop-player-docked #speed-row{justify-content:center;padding:0}
          body.desktop-player-docked .speed-chip{min-height:36px;padding:.36rem .68rem;font-size:.82rem}
          body.desktop-player-docked #progress-row{grid-template-columns:1fr}
          body.desktop-player-docked #time-label{text-align:left;min-width:0;color:#b6bad4}
          body.desktop-player-docked #media-controls{grid-template-columns:1fr;gap:.55rem;border-top:0;padding-top:.1rem}
          body.desktop-player-docked .control-card{padding:.65rem .75rem;border-radius:11px;background:#18182c}
          body.desktop-player-docked .slider-wrap{gap:.6rem}
          body.desktop-player-docked .icon-btn{width:34px;height:34px}
          body.desktop-player-docked #volume,
          body.desktop-player-docked #brightness,
          body.desktop-player-docked #audio-boost{min-height:42px}
          body.desktop-player-docked #volume-label,
          body.desktop-player-docked #brightness-label,
          body.desktop-player-docked #audio-boost-label{min-width:56px;font-size:1rem}
        }
        @media (min-width:1281px){
          body.desktop-player-docked .transport-controls{grid-template-columns:repeat(4,minmax(0,1fr))}
        }
        .control-title{font-size:.68rem;color:#888;text-transform:uppercase;letter-spacing:.08em;font-weight:700}
        .control-actions{display:flex;gap:.35rem;flex-wrap:wrap;align-items:center}
        .slider-wrap{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:.45rem;min-width:0}
        .icon-btn{width:28px;height:28px;border:none;background:transparent;display:grid;place-items:center;cursor:pointer;border-radius:6px;transition:background .15s}
        .icon-btn:hover{background:#22263c}
        .icon-btn .control-icon{width:20px;height:20px;fill:#aaa;flex:0 0 auto}
        .icon-btn .icon-strike{display:none;stroke:#ff7777;stroke-width:2.2;stroke-linecap:round;fill:none}
        .icon-btn.off .control-icon{fill:#ff7777}
        .icon-btn.off .icon-strike{display:block}
        .range-shell{position:relative;display:flex;align-items:center;width:100%}
        .mid-marker{position:absolute;left:50%;transform:translateX(-50%);width:2px;height:7px;background:#9ea2b8;border-radius:1px;pointer-events:none;opacity:.9}
        .mid-marker-top{top:-6px}
        .mid-marker-bottom{bottom:-6px}
        #volume,#brightness,#audio-boost{width:100%;accent-color:#e94560;cursor:pointer;min-height:24px}
        #volume-label,#brightness-label,#audio-boost-label{font-size:.9rem;color:#ddd;min-width:42px;text-align:right;font-weight:600}
        @media (max-width:900px){header{grid-template-columns:1fr;gap:.5rem}#brand{align-items:flex-start}.player-top{grid-template-columns:1fr}.transport-controls{justify-content:flex-start}#media-controls{grid-template-columns:1fr}.btn{padding:.62rem .85rem;font-size:.9rem}}
        @media (max-width:760px) and (pointer:coarse){
          body.phone-remote-only{overflow-x:hidden;overflow-y:auto}
          body.phone-remote-only.phone-playing{overflow:hidden}
          body.phone-remote-only header{grid-template-columns:1fr;gap:.55rem;padding:.7rem .65rem;position:static}
          body.phone-remote-only:not(.phone-playing) header{position:sticky;top:0;z-index:20}
          body.phone-remote-only #brand{flex-direction:row;align-items:center;justify-content:space-between}
          body.phone-remote-only h1{font-size:1.1rem;line-height:1.4rem}
          body.phone-remote-only #search-wrap,
          body.phone-remote-only #count-line,
          body.phone-remote-only #breadcrumb,
          body.phone-remote-only #status{display:none !important}
          body.phone-remote-only.phone-playing #back-button{display:none !important}
          body.phone-remote-only.phone-playing #browser{display:none !important}
          body.phone-remote-only.phone-playing #now-playing-bar{display:flex;gap:.75rem;padding:.9rem;border-radius:14px;border:1px solid #37375a;box-shadow:0 10px 24px rgba(0,0,0,.45)}
          body.phone-remote-only:not(.phone-playing) #now-playing-bar{display:none !important}
          body.phone-remote-only .player-top{grid-template-columns:1fr}
          body.phone-remote-only .transport-controls{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.55rem}
          body.phone-remote-only .transport-controls .btn,
          body.phone-remote-only #subtitles-btn{width:100%;min-height:46px;font-size:.95rem;font-weight:700}
          body.phone-remote-only #pause-btn{min-width:0}
          body.phone-remote-only #progress-row{grid-template-columns:1fr}
          body.phone-remote-only #time-label{text-align:left;min-width:0;font-size:.82rem;color:#aaa}
          body.phone-remote-only #speed-row{margin-top:.1rem}
          body.phone-remote-only #media-controls{grid-template-columns:1fr;gap:.6rem;border-top:0;padding-top:.15rem}
          body.phone-remote-only .speed-chip{min-height:40px;padding:.42rem .72rem;font-size:.86rem}
          body.phone-remote-only #progress{min-height:44px}
          body.phone-remote-only.phone-playing.landscape-controls #media-controls{grid-template-columns:repeat(2,minmax(0,1fr));gap:.55rem}
          body.phone-remote-only.phone-playing.landscape-controls .transport-controls{grid-template-columns:repeat(4,minmax(0,1fr))}
          body.phone-remote-only .control-card{padding:.65rem;border-radius:12px;background:#18182c}
          body.phone-remote-only .slider-wrap{gap:.55rem}
          body.phone-remote-only #volume,
          body.phone-remote-only #brightness,
          body.phone-remote-only #audio-boost{min-height:34px}
        }
        #error{display:none;color:#ff7777;font-size:.78rem;margin-top:.2rem}
        #breadcrumb{font-size:.92rem;color:#888;display:flex;align-items:center;gap:.3rem;flex-wrap:wrap;min-height:1.25rem;line-height:1.25rem}
        #breadcrumb a{color:#00d4aa;cursor:pointer;text-decoration:none}
        #count-line{font-size:.78rem;color:#777;line-height:1rem;min-height:1rem}
        .crumb-back{color:#00d4aa;background:#161624;border:1px solid #2a2a3e;border-radius:8px;padding:.75rem .95rem;cursor:pointer;text-decoration:none;font-size:1.05rem;font-weight:700;text-align:center;min-width:96px}
        .crumb-back:hover{background:#1e1e30}
        .crumb-current{color:#ddd;font-weight:600}
        #status{padding:.35rem .75rem;background:#0a0a0a;font-size:.74rem;color:#777;border-bottom:1px solid #1a1a1a}
        #browser{padding:.7rem .75rem;flex:1}
        .section-label{color:#555;font-size:.75rem;text-transform:uppercase;letter-spacing:.08em;margin-bottom:.5rem;margin-top:1rem}
        .section-label:first-child{margin-top:0}
        .folder-list{display:flex;flex-direction:column;gap:.3rem;margin-bottom:.5rem}
        .folder-row{display:flex;align-items:center;gap:.6rem;background:#1a1a2e;border-radius:6px;padding:.55rem .9rem;cursor:pointer;transition:background .15s}
        .folder-row:hover{background:#252540}
        .folder-icon{font-size:1.1rem}
        .folder-name{font-size:.9rem;word-break:break-word}
        .movie-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:.7rem}
        .movie-card{position:relative;background:#1e1e2e;background-size:cover;background-position:center;border-radius:8px;overflow:hidden;display:flex;flex-direction:column;min-height:140px;cursor:pointer;border:2px solid #e94560;transition:transform .12s,box-shadow .12s,border-color .12s}
        .movie-card::before{content:'';position:absolute;inset:0;background:linear-gradient(to bottom,rgba(0,0,0,.45) 0%,rgba(0,0,0,.75) 100%);border-radius:6px;pointer-events:none}
        .movie-card:hover{transform:translateY(-2px);box-shadow:0 5px 18px rgba(233,69,96,.35)}
        .movie-card.playing{border-color:#00d46a}
        .movie-card.played{border-color:#2d8cff}
        .movie-card.playing.played{border-color:#00d46a}
        .movie-card-inner{position:relative;z-index:1;display:flex;flex-direction:column;gap:.5rem;padding:.8rem;flex:1}
        .movie-title{font-size:.85rem;line-height:1.3;word-break:break-word;flex:1;text-shadow:0 1px 3px rgba(0,0,0,.8)}
        .stop-btn{background:rgba(0,180,90,.9);color:#fff;border:none;padding:.5rem .35rem;border-radius:4px;cursor:pointer;font-size:.85rem;width:100%;backdrop-filter:blur(2px)}
        .stop-btn:hover{background:#00d46a}
        #empty{text-align:center;padding:3rem;color:#555}
        </style></head><body>
        <div id="install-hint"><span>Install RemotePlay from your browser menu for a fullscreen app-like remote.</span><button onclick="dismissInstallHint()">Hide</button></div>
        <header>
          <div id="brand">
            <h1>&#127916; RemotePlay</h1>
            <button id="back-button" class="crumb-back" onclick="goBack()" style="display:none">&#8592; Back</button>
          </div>
          <div id="search-wrap">
            <div id="search-row">
              <input id="search" type="search" placeholder="Search entire library..." oninput="onSearch()"/>
              <button class="btn btn-dim" onclick="rescan()">Rescan</button>
            </div>
            <div id="breadcrumb"></div>
            <div id="count-line"></div>
          </div>
          <div id="now-playing-bar">
            <div class="player-top">
              <div id="player-title">Nothing playing</div>
              <div class="transport-controls">
                <button id="seek-back-btn" class="btn btn-dim" onclick="quickSkip(-10)" onpointerdown="beginSeekHold(event,-10)" onpointerup="endSeekHold()" onpointercancel="endSeekHold()" onpointerleave="endSeekHold()">-10s</button>
                <button id="pause-btn" class="btn btn-red" onclick="togglePause()">&#9646;&#9646; Pause</button>
                <button id="seek-forward-btn" class="btn btn-dim" onclick="quickSkip(10)" onpointerdown="beginSeekHold(event,10)" onpointerup="endSeekHold()" onpointercancel="endSeekHold()" onpointerleave="endSeekHold()">+10s</button>
                <button class="btn btn-dim" onclick="stop()">&#9632;</button>
              </div>
            </div>
            <div id="speed-row">
              <button class="speed-chip" data-speed="0.75" onclick="setPlaybackSpeed(0.75)">0.75x</button>
              <button class="speed-chip active" data-speed="1" onclick="setPlaybackSpeed(1)">1.0x</button>
              <button class="speed-chip" data-speed="1.25" onclick="setPlaybackSpeed(1.25)">1.25x</button>
              <button class="speed-chip" data-speed="1.5" onclick="setPlaybackSpeed(1.5)">1.5x</button>
            </div>
            <div id="progress-row">
              <input id="progress" type="range" min="0" max="0" value="0" step="0.1" oninput="onSeekDrag()" onchange="onSeekCommit()"/>
              <span id="time-label">0:00 / 0:00</span>
            </div>
            <div id="media-controls">
              <div class="control-card">
                <div class="control-title">Audio</div>
                <span class="slider-wrap"><button id="volume-icon-btn" class="icon-btn" type="button" onclick="toggleVolumeMute()" aria-label="Toggle volume mute"><svg class="control-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M3 9v6h4l5 4V5L7 9H3zm13.5 3c0-1.8-1-3.3-2.5-4.1v8.2c1.5-.8 2.5-2.3 2.5-4.1zm-2.5-8.7v2.1c2.9 1 5 3.6 5 6.6s-2.1 5.6-5 6.6v2.1c4-1.1 7-4.7 7-8.7s-3-7.6-7-8.7z"/><path class="icon-strike" d="M3 21L21 3"/></svg></button><input id="volume" type="range" min="0" max="1" value="1" step="0.05" oninput="setVolume(this.value)" onchange="setVolume(this.value)"/><span id="volume-label">100%</span></span>
                <span class="slider-wrap"><button id="boost-icon-btn" class="icon-btn off" type="button" onclick="toggleAudioBoostMute()" aria-label="Toggle audio boost"><svg class="control-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M13 2L5 14h5l-1 8 8-12h-5l1-8z"/><path class="icon-strike" d="M3 21L21 3"/></svg></button><input id="audio-boost" type="range" min="0" max="1" value="0" step="0.05" oninput="setAudioBoost(this.value)" onchange="setAudioBoost(this.value)"/><span id="audio-boost-label">0%</span></span>
              </div>
              <div class="control-card">
                <div class="control-title">Display</div>
                <span class="slider-wrap"><svg class="control-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4.5a7.5 7.5 0 1 0 0 15V4.5zm0-2v2-2zm0 19v-2 2zm9.5-9.5h-2 2zm-19 0h2-2zm16.4-6.4-1.4 1.4 1.4-1.4zM5.1 18.9l1.4-1.4-1.4 1.4zm13.8 0-1.4-1.4 1.4 1.4zM5.1 5.1l1.4 1.4-1.4-1.4z"/></svg><span class="range-shell"><input id="brightness" type="range" min="0" max="1" value="0" step="0.05" oninput="setBrightness(this.value)" onchange="setBrightness(this.value)"/><span class="mid-marker mid-marker-top" aria-hidden="true"></span><span class="mid-marker mid-marker-bottom" aria-hidden="true"></span></span><span id="brightness-label">0%</span></span>
              </div>
              <div id="options-card" class="control-card" style="display:none">
                <div class="control-title">Options</div>
                <div class="control-actions">
                  <button id="subtitles-btn" class="btn btn-dim" onclick="toggleSubtitles()" style="display:none">Subtitles</button>
                </div>
              </div>
            </div>
            <div id="error"></div>
          </div>
        </header>
        <div id="status" style="display:none">Loading...</div>
        <div id="browser"></div>
        <script>
        let currentDir=null,currentData=null,playingPath=null;
        let pollInterval=null,seekDragging=false,searchTimer=null;
        let lastVolumeBeforeMute=.7,lastBoostBeforeMute=.3;
        let seekHoldTimer=null,seekHoldInterval=null,seekHoldTriggered=false,suppressNextSeekTap=false;
        let currentPlaybackSpeed=1;
        const playedVideos=new Set(loadPlayedVideos());
        const installHint=document.getElementById('install-hint');
        const phoneLayoutQuery=window.matchMedia('(max-width:760px) and (pointer:coarse)');
        let isPhoneRemoteOnly=phoneLayoutQuery.matches;
        if(!localStorage.getItem('remotePlayInstallHintDismissed')&&window.matchMedia('(display-mode: browser)').matches)installHint.style.display='flex';

        function applyPhonePlaybackState(isPlaying){
          if(!isPhoneRemoteOnly)return;
          document.body.classList.toggle('phone-playing',Boolean(isPlaying));
          document.getElementById('now-playing-bar').style.display=isPlaying?'flex':'none';
        }

        function applyDesktopDockedLayout(isPlaying){
          const useDocked=!isPhoneRemoteOnly&&Boolean(isPlaying);
          document.body.classList.toggle('desktop-player-docked',useDocked);
          document.body.classList.toggle('tablet-docked',useDocked&&window.matchMedia('(min-width:900px) and (max-width:1280px)').matches);
        }
        function applyPhoneLayout(){
          isPhoneRemoteOnly=phoneLayoutQuery.matches;
          document.body.classList.toggle('phone-remote-only',isPhoneRemoteOnly);
          if(!isPhoneRemoteOnly){
            document.body.classList.remove('phone-playing');
            document.body.classList.remove('landscape-controls');
            applyDesktopDockedLayout(Boolean(playingPath));
            if(!playingPath)document.getElementById('now-playing-bar').style.display='none';
            return;
          }

          document.body.classList.remove('desktop-player-docked');
          document.body.classList.remove('tablet-docked');
          const isLandscape=window.matchMedia('(orientation: landscape)').matches;
          document.body.classList.toggle('landscape-controls',isLandscape);
          applyPhonePlaybackState(Boolean(playingPath));
        }

        function haptic(ms){
          if(!('vibrate' in navigator))return;
          try{navigator.vibrate(ms);}catch{}
        }
        function startPolling(){if(pollInterval)return;pollInterval=setInterval(pollStatus,1000);}
        function stopPolling(){clearInterval(pollInterval);pollInterval=null;}

        async function pollStatus(){
          try{
            const res=await fetch('/api/status');
            if(!res.ok)return;
            const s=await res.json();
            const bar=document.getElementById('now-playing-bar');
            if(s.isPlaying){
              bar.style.display='flex';
              const pb=document.getElementById('pause-btn');
              pb.textContent=s.isPaused?'\u25B6 Resume':'\u23F8 Pause';
              document.getElementById('player-title').textContent=(s.title||'Now playing').replace(/^\s*[▶⏸]\s*/,'');
              const subtitlesBtn=document.getElementById('subtitles-btn');
              const optionsCard=document.getElementById('options-card');
              const showSubtitles=s.hasSubtitles;
              subtitlesBtn.style.display=showSubtitles?'inline-block':'none';
              optionsCard.style.display=showSubtitles?'flex':'none';
              subtitlesBtn.textContent=s.subtitlesEnabled?'Subtitles On':'Subtitles Off';
              const volume=Math.max(0,Math.min(1,Number(s.volume)||0));
              document.getElementById('volume').value=volume;
              document.getElementById('volume-label').textContent=Math.round(volume*100)+'%';
              if(volume>0.001)lastVolumeBeforeMute=volume;
              updateVolumeIcon(volume);
              const boostAmount=Math.max(0,Math.min(1,(Number(s.audioBoost)||1)-1));
              document.getElementById('audio-boost').value=boostAmount;
              document.getElementById('audio-boost-label').textContent=Math.round(boostAmount*100)+'%';
              if(boostAmount>0.001)lastBoostBeforeMute=boostAmount;
              updateAudioBoostIcon(boostAmount);
              document.getElementById('brightness').value=s.brightness;
              document.getElementById('brightness-label').textContent=Math.round(s.brightness*100)+'%';
              const err=document.getElementById('error');
              if(s.lastError){err.style.display='block';err.textContent='Playback error: '+s.lastError;}else{err.style.display='none';err.textContent='';}
              const progress=document.getElementById('progress');
              if(s.duration>0){
                progress.max=s.duration;
                if(!seekDragging)progress.value=s.position;
              }
              document.getElementById('time-label').textContent=fmt(s.position)+' / '+fmt(s.duration);
              currentPlaybackSpeed=Math.max(0.5,Math.min(2,Number(s.playbackSpeed)||1));
              syncSpeedChips(currentPlaybackSpeed);
              if(isPhoneRemoteOnly)applyPhonePlaybackState(true);
              else applyDesktopDockedLayout(true);
            }else if(isPhoneRemoteOnly){
              applyPhonePlaybackState(false);
            }else{
              applyDesktopDockedLayout(false);
              bar.style.display='none';
            }
          }catch(e){}
        }

        function fmt(sec){
          const m=Math.floor(sec/60),s=Math.floor(sec%60);
          return m+':'+(s<10?'0':'')+s;
        }

        async function onSeekDrag(){seekDragging=true;}
        async function onSeekCommit(){
          const pos=parseFloat(document.getElementById('progress').value);
          if(!Number.isNaN(pos)){
            haptic(8);
            await api('/api/seek?pos='+pos.toFixed(1));
          }
          seekDragging=false;
        }

        function updateVolumeIcon(value){
          document.getElementById('volume-icon-btn').classList.toggle('off',(Number(value)||0)<=0.001);
        }
        function updateAudioBoostIcon(value){
          document.getElementById('boost-icon-btn').classList.toggle('off',(Number(value)||0)<=0.001);
        }
        async function togglePause(){haptic(12);await api('/api/pause');}
        async function skip(seconds){await api('/api/skip?seconds='+encodeURIComponent(seconds));}
        async function quickSkip(seconds){
          if(suppressNextSeekTap){
            suppressNextSeekTap=false;
            return;
          }
          haptic(8);
          await skip(seconds);
        }
        function beginSeekHold(event,step){
          if(event.pointerType==='mouse'&&event.button!==0)return;
          seekHoldTriggered=false;
          clearTimeout(seekHoldTimer);
          clearInterval(seekHoldInterval);
          seekHoldTimer=setTimeout(()=>{
            seekHoldTriggered=true;
            suppressNextSeekTap=true;
            haptic(16);
            skip(step);
            seekHoldInterval=setInterval(()=>skip(step),220);
          },350);
        }
        function endSeekHold(){
          clearTimeout(seekHoldTimer);
          clearInterval(seekHoldInterval);
          seekHoldTimer=null;
          seekHoldInterval=null;
        }
        async function setVolume(value){
          const volume=Math.max(0,Math.min(1,parseFloat(value)||0));
          if(volume>0.001)lastVolumeBeforeMute=volume;
          document.getElementById('volume-label').textContent=Math.round(volume*100)+'%';
          updateVolumeIcon(volume);
          await api('/api/volume?value='+encodeURIComponent(volume.toFixed(2)));
        }
        async function toggleVolumeMute(){
          haptic(10);
          const slider=document.getElementById('volume');
          const current=Math.max(0,Math.min(1,parseFloat(slider.value)||0));
          const next=current>0.001?0:Math.max(0.05,lastVolumeBeforeMute||0.7);
          slider.value=next.toFixed(2);
          await setVolume(slider.value);
        }
        async function setAudioBoost(value){
          const boostAmount=Math.max(0,Math.min(1,parseFloat(value)||0));
          if(boostAmount>0.001)lastBoostBeforeMute=boostAmount;
          document.getElementById('audio-boost-label').textContent=Math.round(boostAmount*100)+'%';
          updateAudioBoostIcon(boostAmount);
          await api('/api/audio-boost?value='+encodeURIComponent((1+boostAmount).toFixed(2)));
        }
        async function toggleAudioBoostMute(){
          haptic(10);
          const slider=document.getElementById('audio-boost');
          const current=Math.max(0,Math.min(1,parseFloat(slider.value)||0));
          const next=current>0.001?0:Math.max(0.05,lastBoostBeforeMute||0.3);
          slider.value=next.toFixed(2);
          await setAudioBoost(slider.value);
        }
        async function setBrightness(value){document.getElementById('brightness-label').textContent=Math.round(parseFloat(value)*100)+'%';await api('/api/brightness?value='+encodeURIComponent(value));}
        function syncSpeedChips(speed){
          const chips=Array.from(document.querySelectorAll('.speed-chip'));
          let selected=null;
          for(const chip of chips){
            const value=parseFloat(chip.dataset.speed||'1');
            const isActive=Math.abs(value-speed)<0.02;
            chip.classList.toggle('active',isActive);
            if(isActive)selected=chip;
          }
          if(!selected){
            let nearest=null;
            let nearestDelta=Number.POSITIVE_INFINITY;
            for(const chip of chips){
              const value=parseFloat(chip.dataset.speed||'1');
              const delta=Math.abs(value-speed);
              if(delta<nearestDelta){nearestDelta=delta;nearest=chip;}
            }
            if(nearest)nearest.classList.add('active');
          }
        }
        async function setPlaybackSpeed(value){
          const speed=Math.max(0.5,Math.min(2,parseFloat(value)||1));
          currentPlaybackSpeed=speed;
          syncSpeedChips(speed);
          haptic(10);
          await api('/api/speed?value='+encodeURIComponent(speed.toFixed(2)));
        }
        async function toggleSubtitles(){haptic(8);await api('/api/subtitles');}
        async function rescan(){setStatus('Refreshing search index...');await api('/api/rescan');}

        function resetCardsScrollTop(){
          const browser=document.getElementById('browser');
          if(browser)browser.scrollTop=0;
          document.documentElement.scrollTop=0;
          document.body.scrollTop=0;
          window.scrollTo(0,0);
        }
        async function browse(d){
          setStatus('Loading...');currentDir=d;document.getElementById('search').value='';
          try{
            const url=d?'/api/browse?dir='+encodeURIComponent(d):'/api/browse';
            const res=await fetch(url);
            if(!res.ok){setStatus('Server error '+res.status);return;}
            currentData=await res.json();render(currentData);
            resetCardsScrollTop();
          }catch(e){setStatus('Error: '+e);}
        }
        function onSearch(){
          const q=document.getElementById('search').value.toLowerCase().trim();
          clearTimeout(searchTimer);
          if(!q){if(currentData)render(currentData);return;}
          searchTimer=setTimeout(()=>searchLibrary(q),180);
        }
        async function searchLibrary(q){
          setStatus('Searching library...');
          try{
            const res=await fetch('/api/search?q='+encodeURIComponent(q));
            if(!res.ok){setStatus('Search error '+res.status);return;}
            const data=await res.json();
            render({folders:[],files:data.files,current:'Search',currentFull:'Search',parent:null,isRoot:false},true);
            const note=data.indexing?' (indexing...)':'';
            setStatus(data.files.length+' result(s) from '+data.indexedFiles+' indexed video(s)'+note);
          }catch(e){setStatus('Search failed: '+e);}
        }
        function render(data,searching){
          const bc=document.getElementById('breadcrumb');
          const countLine=document.getElementById('count-line');
          const back=document.getElementById('back-button');
          back.style.display=(!data.isRoot&&data.parent!=null&&!searching)?'block':'none';
          back.dataset.dir=(!data.isRoot&&data.parent!=null&&!searching)?data.parent:'';
          bc.innerHTML='';
          if(searching){bc.innerHTML+='<a onclick="browse(null)">&#8962; Root</a><span> &rsaquo; </span><span class="crumb-current">Search results</span>';}
          else if(data.breadcrumbs&&data.breadcrumbs.length){
            bc.innerHTML+=data.breadcrumbs.map((c,i)=>{
              const sep=i>0?'<span> &rsaquo; </span>':'';
              const label=i===0?'&#8962; Root':esc(c.name);
              const cls=i===data.breadcrumbs.length-1?' class="crumb-current"':'';
              return sep+'<a'+cls+' onclick="browse(\''+c.dir+'\')">'+label+'</a>';
            }).join('');
          }else{bc.innerHTML+='<a onclick="browse(null)">&#8962; Root</a>';}
          if(searching)countLine.textContent=data.files.length+' result(s)';
          else countLine.textContent=data.folders.length+' folder(s), '+data.files.length+' video(s)';
          let html='';
          if(data.folders.length&&!searching){
            html+='<div class="folder-list">';
            html+=data.folders.map(f=>'<div class="folder-row" onclick="browse(\''+f.dir+'\')">'+'<span class="folder-icon">&#128193;</span><span class="folder-name">'+esc(f.name)+'</span></div>').join('');
            html+='</div>';}
          if(data.files.length){
            html+='<div class="section-label">Videos</div><div class="movie-grid">';
            html+=data.files.map(f=>{
              const played=playedVideos.has(f.path);
              const thumbUrl='/api/thumb?path='+encodeURIComponent(f.path);
              const bg='style="background-image:url('+thumbUrl+')"';
              const cardClass='movie-card '+(f.path===playingPath?'playing ':'')+(played?'played':'');
              const isPlaying=f.path===playingPath;
              const action=isPlaying?'':' onclick="play(\''+f.path+'\')"';
              const stopButton=isPlaying?'<button class="stop-btn" onclick="stopPlayingCard(event,\''+f.path+'\')">&#9632; STOP</button>':'';
              return '<div class="'+cardClass+'" id="card-'+f.path+'" '+bg+action+'>'+
                '<div class="movie-card-inner">'+
                '<div class="movie-title">'+esc(f.name)+'</div>'+
                stopButton+
                '</div></div>';
            }).join('');
            html+='</div>';}
          if(!data.folders.length&&!data.files.length)html='<div id="empty">No subfolders or video files here.</div>';
          document.getElementById('browser').innerHTML=html;
        }
        function goBack(){const d=document.getElementById('back-button').dataset.dir;if(d)browse(d);}
        async function play(p,name){
          const title=name||'';
          playingPath=p;setStatus(title?'Playing: '+title:'Playing...');
          markPlayed(p);
          updatePlayingCard(p);
          await api('/api/play?path='+encodeURIComponent(p));
          startPolling();
        }
        async function stopPlayingCard(event,p){
          event.stopPropagation();
          if(p!==playingPath)return;
          await stop();
        }
        async function stop(){
          const stoppedPath=playingPath;
          playingPath=null;
          updatePlayingCard(null,stoppedPath);
          if(isPhoneRemoteOnly)applyPhonePlaybackState(false);
          else{
            applyDesktopDockedLayout(false);
            document.getElementById('now-playing-bar').style.display='none';
          }
          setStatus('Stopped.');
          await api('/api/stop');
        }
        async function api(url){try{await fetch(url);}catch(e){setStatus('Command failed: '+e);}}
        function loadPlayedVideos(){
          const m=document.cookie.match(/(?:^|; )playedVideos=([^;]*)/);
          if(!m)return [];
          try{return JSON.parse(decodeURIComponent(m[1]));}catch(e){return [];}
        }
        function savePlayedVideos(){
          const values=Array.from(playedVideos).slice(-1000);
          document.cookie='playedVideos='+encodeURIComponent(JSON.stringify(values))+'; max-age=31536000; path=/; SameSite=Lax';
        }
        function markPlayed(p){
          playedVideos.add(p);savePlayedVideos();
          const card=document.getElementById('card-'+p);
          if(card)card.classList.add('played');
        }
        function updatePlayingCard(newPath,oldPath){
          const paths=[oldPath,playingPath,newPath].filter(Boolean);
          document.querySelectorAll('.movie-card.playing').forEach(c=>paths.push(c.id.substring(5)));
          for(const p of new Set(paths)){
            const card=document.getElementById('card-'+p);if(!card)continue;
            const isActive=p===newPath;
            card.classList.toggle('playing',isActive);
            card.classList.toggle('played',!isActive&&playedVideos.has(p));
            card.onclick=isActive?null:()=>play(p,card.querySelector('.movie-title')?.textContent||'');
            const existing=card.querySelector('.stop-btn');
            if(isActive&&!existing){
              const btn=document.createElement('button');
              btn.className='stop-btn';
              btn.innerHTML='&#9632; STOP';
              btn.onclick=e=>stopPlayingCard(e,p);
              card.querySelector('.movie-card-inner')?.appendChild(btn);
            }else if(!isActive&&existing){
              existing.remove();
            }
          }
        }
        function setStatus(m){document.getElementById('status').textContent=m;}
        function dismissInstallHint(){localStorage.setItem('remotePlayInstallHintDismissed','1');installHint.style.display='none';}
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');}
        if('serviceWorker' in navigator){
          window.addEventListener('load',()=>navigator.serviceWorker.register('/service-worker.js').catch(()=>{}));
        }
        applyPhoneLayout();
        phoneLayoutQuery.addEventListener('change',()=>{
          const wasPhone=isPhoneRemoteOnly;
          applyPhoneLayout();
          if(wasPhone&&!isPhoneRemoteOnly&&!currentData)browse(null);
        });
        window.addEventListener('orientationchange',()=>setTimeout(applyPhoneLayout,80));
        window.addEventListener('resize',()=>{if(document.body.classList.contains('desktop-player-docked'))applyDesktopDockedLayout(true);});
        browse(null);
        updateVolumeIcon(parseFloat(document.getElementById('volume').value)||0);
        updateAudioBoostIcon(parseFloat(document.getElementById('audio-boost').value)||0);
        syncSpeedChips(currentPlaybackSpeed);
        startPolling();
        </script></body></html>
        """;
}
