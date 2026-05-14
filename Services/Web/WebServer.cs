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

internal sealed record TrackOption(int Id, string Name);

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

internal sealed partial class WebServer
{
    private const string WebAssetsDirectoryName = "WebAssets";

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv" };
    private static readonly HashSet<string> HiddenFolderNames =
        new(StringComparer.OrdinalIgnoreCase) { "Subs", "Alt" };
    private static readonly Guid HttpsCertificateAppId = new("be2a8b40-850e-4ef1-a893-b0b13f5c7fd9");
    private const string HttpsCertificateSubject = "CN=RemotePlay Local HTTPS";

    private readonly AppConfig _config;
    private readonly WebServerCallbacks _callbacks;
    private HttpListener _listener = new();
    // Thumbnail cache: base64-encoded path → JPEG bytes (null = not available)
    private readonly ConcurrentDictionary<string, byte[]?> _thumbCache = new();
    private readonly Timer _libraryIndexTimer;
    private readonly object _libraryIndexGate = new();
    private LibraryFile[] _libraryIndex = [];
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastIndexRefreshUtc;
    private DateTimeOffset? _scanStartedUtc;
    private int _scannedFiles;
    private int _scannedFolders;
    private string _lastScanError = string.Empty;
    private bool _isIndexing;
    private string _activeScheme;
    private string? _startupWarning;

    public WebServer(AppConfig config, WebServerCallbacks callbacks)
    {
        _config = config;
        _callbacks = callbacks;
        _activeScheme = config.Scheme;
        _libraryIndexTimer = new Timer(_ => RefreshLibraryIndexIfIdle(), null,
            TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    public string ActiveScheme => _activeScheme;
    public string? StartupWarning => _startupWarning;

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

    private static string GetHtmlPage() =>
        LoadWebAsset("index.html", HtmlPage);

    private static string GetServiceWorkerJs() =>
        LoadWebAsset("service-worker.js", ServiceWorkerJs);

    private static string LoadWebAsset(string fileName, string fallback)
    {
        var path = Path.Combine(AppContext.BaseDirectory, WebAssetsDirectoryName, fileName);
        try
        {
            return File.Exists(path)
                ? File.ReadAllText(path, Encoding.UTF8)
                : fallback;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load web asset {path}; using embedded fallback", ex);
            return fallback;
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
        #brand-actions{display:flex;gap:.35rem;align-items:center}
        #health-link{display:inline-flex;align-items:center;gap:.3rem;background:#162033;border:1px solid #2e426a;color:#cfe0ff;text-decoration:none;border-radius:999px;padding:.34rem .55rem;font-size:.75rem;font-weight:700}
        #health-link:hover{background:#203252}
        #diag-dot{width:.65rem;height:.65rem;border-radius:999px;background:#ffaa00;box-shadow:0 0 0 3px rgba(255,170,0,.18)}
        #diag-dot.ok{background:#00d4aa;box-shadow:0 0 0 3px rgba(0,212,170,.18)}
        #diag-dot.error{background:#ff5959;box-shadow:0 0 0 3px rgba(255,89,89,.2)}
        #search-wrap{display:flex;flex-direction:column;gap:.2rem;min-width:0}
        #search-row{display:flex;gap:.35rem;min-width:0}
        #search{background:#222;color:#eee;border:1px solid #444;padding:.38rem .6rem;border-radius:4px;font-size:.86rem;flex:1;min-width:100px}
        .btn{border:none;border-radius:4px;cursor:pointer;font-size:.78rem;padding:.36rem .65rem;color:#fff;transition:background .15s}
        .btn-red{background:#e94560}.btn-red:hover{background:#c73652}
        .btn-dim{background:#333}.btn-dim:hover{background:#555}
        .btn-nav{background:#2b3f6b}.btn-nav:hover{background:#36528a}
        .btn-nav{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:.28rem;line-height:1.05;min-height:46px;width:100%}
        .nav-main{font-size:.9rem;font-weight:800;text-transform:uppercase;letter-spacing:.02em}
        .nav-title{font-size:.58rem;font-weight:600;opacity:.78;width:100%;max-width:100%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;text-transform:none;letter-spacing:0}
        .btn:disabled{opacity:.45;cursor:not-allowed;filter:saturate(.6)}
        .btn:disabled:hover{background:inherit}
        .btn-seek{background:#38404f}.btn-seek:hover{background:#4a5568}
        .btn-stop{background:#7d2432}.btn-stop:hover{background:#a83143}
        #install-hint{display:none;background:#10251f;border-bottom:1px solid #00d4aa;color:#ddfff7;padding:.55rem .75rem;font-size:.82rem;align-items:center;gap:.55rem;justify-content:space-between}
        #install-hint button{background:#00d4aa;color:#06110f;border:none;border-radius:4px;padding:.3rem .55rem;font-weight:700;cursor:pointer}
        #now-playing-bar{display:none;min-width:0;width:100%;flex-direction:column;gap:.6rem;background:linear-gradient(135deg,#0d0d1a,#151526);padding:.7rem;border:1px solid #30304a;border-radius:12px;box-shadow:0 6px 18px rgba(0,0,0,.38)}
        #player-title{font-size:.9rem;color:#fff;font-weight:700;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;min-width:0}
        .player-top{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:.7rem}
        .transport-controls{display:flex;gap:.45rem;align-items:stretch;justify-content:flex-end;flex-wrap:wrap}
        .button-group{display:flex;gap:.25rem;background:#111627;border:1px solid #2d314a;border-radius:10px;padding:.25rem}
        .transport-nav-group{flex-direction:column;min-width:180px}
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
          body.desktop-player-docked .transport-controls{display:flex;gap:.5rem;justify-content:stretch}
          body.desktop-player-docked .button-group{flex:1;display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.35rem}
          body.desktop-player-docked .transport-nav-group{display:grid;grid-template-columns:1fr;min-width:0}
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
          body.desktop-player-docked .display-icon-btn{width:auto;height:auto}
          body.desktop-player-docked #volume,
          body.desktop-player-docked #brightness,
          body.desktop-player-docked #saturation,
          body.desktop-player-docked #zoom,
          body.desktop-player-docked #audio-boost{min-height:42px}
          body.desktop-player-docked #volume-label,
          body.desktop-player-docked #brightness-label,
          body.desktop-player-docked #saturation-label,
          body.desktop-player-docked #zoom-label,
          body.desktop-player-docked #audio-boost-label{min-width:56px;font-size:1rem}
        }
        @media (min-width:1281px){
          body.desktop-player-docked .button-group{grid-template-columns:repeat(2,minmax(0,1fr))}
          body.desktop-player-docked .transport-nav-group{grid-template-columns:1fr}
        }
        .control-title{font-size:.68rem;color:#888;text-transform:uppercase;letter-spacing:.08em;font-weight:700}
        .control-actions{display:flex;gap:.35rem;flex-wrap:wrap;align-items:center}
        .track-select{width:100%;min-width:0;background:#22263b;color:#eee;border:1px solid #3a3a57;border-radius:8px;padding:.45rem .55rem;font-size:.86rem}
        .track-select-row{display:flex;flex-direction:column;gap:.5rem}
        .track-group{display:flex;flex-direction:column;gap:.22rem}
        .track-group-label{font-size:.68rem;color:#9ea2b8;text-transform:uppercase;letter-spacing:.06em;font-weight:700}
        #player-meta{font-size:.76rem;color:#9ea2b8;line-height:1.25;min-height:1rem}
        #connection-status{font-size:.7rem;color:#ffaa00;line-height:1.2;min-height:.9rem}
        #connection-status.connected{color:#00d4aa}
        #connection-status.error{color:#ff7777}
        .slider-wrap{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:.45rem;min-width:0}
        .display-slider-wrap{grid-template-columns:minmax(96px,auto) minmax(0,1fr) auto}
        .zoom-slider-wrap{grid-template-columns:minmax(96px,auto) minmax(0,1fr) auto;gap:.45rem;width:100%;margin-top:.28rem}
        .icon-btn{width:28px;height:28px;border:none;background:transparent;display:grid;place-items:center;cursor:pointer;border-radius:6px;transition:background .15s}
        .display-icon-btn{width:auto;height:auto;display:flex;align-items:center;gap:.35rem;padding:.2rem .25rem .2rem 0}
        .display-icon-btn .icon-caption{font-size:.62rem;color:#9ea2b8;line-height:1;text-transform:uppercase;letter-spacing:.04em;font-weight:700;white-space:nowrap}
        .icon-btn:hover{background:#22263c}
        .icon-btn .control-icon{width:20px;height:20px;fill:#aaa;flex:0 0 auto}
        .icon-btn .icon-strike{display:none;stroke:#ff7777;stroke-width:2.2;stroke-linecap:round;fill:none}
        .icon-btn.off .control-icon{fill:#ff7777}
        .icon-btn.off .icon-strike{display:block}
        .range-shell{position:relative;display:flex;align-items:center;width:100%}
        .mid-marker{position:absolute;left:50%;transform:translateX(-50%);width:2px;height:7px;background:#9ea2b8;border-radius:1px;pointer-events:none;opacity:.9}
        .mid-marker-bottom{bottom:-6px}
        #volume,#brightness,#saturation,#audio-boost{width:100%;accent-color:#e94560;cursor:pointer;min-height:24px}
        #zoom{width:100%;accent-color:#3b82f6;cursor:pointer;min-height:24px}
        #volume-label,#brightness-label,#saturation-label,#zoom-label,#audio-boost-label{font-size:.9rem;color:#ddd;min-width:42px;text-align:right;font-weight:600}
        @media (max-width:900px){header{grid-template-columns:1fr;gap:.5rem}#brand{align-items:flex-start}.player-top{grid-template-columns:1fr}.transport-controls{justify-content:flex-start}#media-controls{grid-template-columns:1fr}.btn{padding:.62rem .85rem;font-size:.9rem}}
        @media (min-width:761px) and (max-width:1280px) and (pointer:coarse){
          header{padding:.75rem .9rem;gap:.75rem}
          .btn{min-height:44px;padding:.7rem .9rem;font-size:.95rem;border-radius:8px}
          .transport-controls .btn{min-height:50px;font-size:1rem}
          .button-group{gap:.45rem;padding:.4rem;border-radius:12px}
          .speed-chip{min-height:42px;padding:.55rem .85rem;font-size:.9rem}
          #progress{min-height:48px}
          .control-card{padding:.75rem;border-radius:12px}
          .track-select{min-height:46px;font-size:.95rem}
          #volume,#brightness,#saturation,#zoom,#audio-boost{min-height:42px}
          .movie-grid{grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:.9rem}
          .movie-card{min-height:165px;border-radius:11px}
          .movie-card-inner{padding:1rem;gap:.65rem}
          .movie-title{font-size:.95rem}
        }
        @media (max-width:760px) and (pointer:coarse){
          body.phone-remote-only{overflow-x:hidden;overflow-y:auto}
          body.phone-remote-only.phone-playing{overflow-x:hidden;overflow-y:auto;min-height:100dvh}
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
          body.phone-remote-only.phone-playing #now-playing-bar{display:flex;gap:.75rem;padding:.9rem;margin-bottom:1rem;border-radius:14px;border:1px solid #37375a;box-shadow:0 10px 24px rgba(0,0,0,.45)}
          body.phone-remote-only.phone-playing.controls-dimmed #now-playing-bar{opacity:.34;transition:opacity .25s}
          body.phone-remote-only.phone-playing.controls-dimmed #player-title,
          body.phone-remote-only.phone-playing.controls-dimmed #player-meta,
          body.phone-remote-only.phone-playing.controls-dimmed #connection-status{opacity:1}
          body.phone-remote-only.phone-playing:not(.controls-dimmed) #now-playing-bar{opacity:1;transition:opacity .18s}
          body.phone-remote-only:not(.phone-playing) #now-playing-bar{display:none !important}
          body.phone-remote-only .player-top{grid-template-columns:1fr}
          body.phone-remote-only .transport-controls{display:grid;grid-template-columns:1fr;gap:.55rem}
          body.phone-remote-only .button-group{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.35rem;width:100%}
          body.phone-remote-only .transport-nav-group{grid-template-columns:1fr;min-width:0}
          body.phone-remote-only .transport-controls .btn{width:100%;min-height:46px;font-size:.95rem;font-weight:700}
          body.phone-remote-only #pause-btn{min-width:0}
          body.phone-remote-only #progress-row{grid-template-columns:1fr}
          body.phone-remote-only #time-label{text-align:left;min-width:0;font-size:.82rem;color:#aaa}
          body.phone-remote-only #speed-row{margin-top:.1rem}
          body.phone-remote-only #media-controls{grid-template-columns:1fr;gap:.6rem;border-top:0;padding-top:.15rem}
          body.phone-remote-only .speed-chip{min-height:40px;padding:.42rem .72rem;font-size:.86rem}
          body.phone-remote-only .track-select{min-height:42px;font-size:.9rem}
          body.phone-remote-only #progress{min-height:44px}
          body.phone-remote-only.phone-playing.landscape-controls #media-controls{grid-template-columns:repeat(2,minmax(0,1fr));gap:.55rem}
          body.phone-remote-only.phone-playing.landscape-controls .transport-controls{grid-template-columns:repeat(3,minmax(0,1fr))}
          body.phone-remote-only .control-card{padding:.65rem;border-radius:12px;background:#18182c}
          body.phone-remote-only .slider-wrap{gap:.55rem}
          body.phone-remote-only #volume,
          body.phone-remote-only #brightness,
          body.phone-remote-only #saturation,
          body.phone-remote-only #zoom,
          body.phone-remote-only #audio-boost{min-height:34px}
          body.phone-remote-only .zoom-slider-wrap{margin-top:.24rem}
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
        .movie-card::before{content:'';position:absolute;inset:0;background:linear-gradient(to bottom,rgba(0,0,0,.32) 0%,rgba(0,0,0,.76) 100%);border-radius:6px;pointer-events:none;transition:background .15s}
        .movie-card::after{content:'';position:absolute;left:0;right:0;bottom:0;height:44%;background:linear-gradient(to top,rgba(6,8,16,.92),rgba(6,8,16,0));opacity:0;pointer-events:none;transition:opacity .15s}
        @media (hover:hover) and (pointer:fine){.movie-card:not(.playing):hover::after{opacity:1}}
        .movie-card:hover{transform:translateY(-2px);box-shadow:0 5px 18px rgba(233,69,96,.35)}
        .movie-card.playing{border-color:#00d46a}
        .movie-card.played{border-color:#2d8cff}
        .movie-card.playing.played{border-color:#00d46a}
        .movie-card-inner{position:relative;z-index:1;display:flex;flex-direction:column;gap:.5rem;padding:.8rem;flex:1}
        .movie-card{-webkit-user-select:none;user-select:none}
        .movie-title{font-size:.85rem;line-height:1.3;word-break:break-word;flex:1;text-shadow:0 1px 3px rgba(0,0,0,.8)}
        .movie-meta{font-size:.72rem;color:#cfd3e9;text-shadow:0 1px 3px rgba(0,0,0,.8)}
        .resume-badge{align-self:flex-start;background:rgba(0,212,170,.92);color:#071513;border-radius:999px;padding:.22rem .5rem;font-size:.7rem;font-weight:800;box-shadow:0 2px 8px rgba(0,0,0,.35)}
        .card-actions{display:none;grid-template-columns:repeat(3,minmax(0,1fr));gap:.25rem;background:rgba(8,10,20,.72);border:1px solid rgba(255,255,255,.12);border-radius:8px;padding:.28rem;backdrop-filter:blur(4px)}
        @media (hover:hover) and (pointer:fine){.movie-card:not(.playing):hover .card-actions{display:grid}}
        .card-actions button{background:rgba(20,20,34,.82);color:#fff;border:1px solid rgba(255,255,255,.18);padding:.38rem .25rem;border-radius:4px;cursor:pointer;font-size:.72rem;backdrop-filter:blur(2px)}
        .card-actions button:hover{background:rgba(45,140,255,.85)}
        @media (pointer:coarse){.movie-card:not(.playing).actions-open .card-actions{display:grid}}
        .stop-btn{background:rgba(0,180,90,.9);color:#fff;border:none;padding:.5rem .35rem;border-radius:4px;cursor:pointer;font-size:.85rem;width:100%;backdrop-filter:blur(2px)}
        .stop-btn:hover{background:#00d46a}
        #empty{text-align:center;padding:3rem;color:#555}
        </style></head><body>
        <div id="install-hint"><span>Install RemotePlay from your browser menu for a fullscreen app-like remote.</span><button onclick="dismissInstallHint()">Hide</button></div>
        <header>
          <div id="brand">
            <h1>&#127916; RemotePlay</h1>
            <div id="brand-actions"><a id="health-link" href="/health" target="_blank" rel="noopener"><span id="diag-dot"></span>Health</a></div>
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
              <div><div id="player-title">Nothing playing</div><div id="player-meta"></div><div id="connection-status">Connecting...</div></div>
              <div class="transport-controls">
                <div class="button-group transport-nav-group">
                  <button id="previous-btn" class="btn btn-nav" onclick="playAdjacent('previous')"><span class="nav-main">Prev</span><span class="nav-title"></span></button>
                  <button id="next-btn" class="btn btn-nav" onclick="playAdjacent('next')"><span class="nav-main">Next</span><span class="nav-title"></span></button>
                </div>
                <div class="button-group transport-main-group">
                  <button id="seek-back-btn" class="btn btn-seek" onclick="quickSkip(-10)" onpointerdown="beginSeekHold(event,-10)" onpointerup="endSeekHold()" onpointercancel="endSeekHold()" onpointerleave="endSeekHold()">-10s</button>
                  <button id="pause-btn" class="btn btn-red" onclick="togglePause()">&#9646;&#9646; Pause</button>
                  <button id="seek-forward-btn" class="btn btn-seek" onclick="quickSkip(10)" onpointerdown="beginSeekHold(event,10)" onpointerup="endSeekHold()" onpointercancel="endSeekHold()" onpointerleave="endSeekHold()">+10s</button>
                  <button class="btn btn-stop" onclick="stop()">&#9632; STOP</button>
                </div>
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
                <div class="slider-wrap display-slider-wrap"><button class="icon-btn display-icon-btn" type="button" onclick="resetBrightnessMid()" aria-label="Reset brightness to midpoint"><svg class="control-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4.5a7.5 7.5 0 1 0 0 15V4.5zm0-2v2-2zm0 19v-2 2zm9.5-9.5h-2 2zm-19 0h2-2zm16.4-6.4-1.4 1.4 1.4-1.4zM5.1 18.9l1.4-1.4-1.4 1.4zm13.8 0-1.4-1.4 1.4 1.4zM5.1 5.1l1.4 1.4-1.4-1.4z"/></svg><span class="icon-caption">Brightness</span></button><span class="range-shell"><input id="brightness" type="range" min="0.2" max="0.8" value="0.5" step="0.01" oninput="setBrightness(this.value)" onchange="setBrightness(this.value)"/><span class="mid-marker mid-marker-bottom" aria-hidden="true"></span></span><span id="brightness-label">50%</span></div>
                <div class="slider-wrap display-slider-wrap"><button class="icon-btn display-icon-btn" type="button" onclick="resetSaturationMid()" aria-label="Reset saturation to midpoint"><svg class="control-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 2 3 7v10l9 5 9-5V7l-9-5zm0 2.2L19 8v8l-7 3.8L5 16V8l7-3.8z"/></svg><span class="icon-caption">Saturation</span></button><span class="range-shell"><input id="saturation" type="range" min="0" max="2" value="1" step="0.01" oninput="setSaturation(this.value)" onchange="setSaturation(this.value)"/></span><span id="saturation-label">100%</span></div>
                <div class="slider-wrap display-slider-wrap zoom-slider-wrap"><button class="icon-btn display-icon-btn" type="button" onclick="resetZoomDefault()" aria-label="Reset zoom to default"><svg class="control-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M10 4a6 6 0 1 0 3.78 10.66l4.28 4.28 1.41-1.41-4.28-4.28A6 6 0 0 0 10 4zm0 2a4 4 0 1 1 0 8 4 4 0 0 1 0-8z"/><path d="M10 7v6M7 10h6"/></svg><span class="icon-caption">Zoom</span></button><span class="range-shell"><input id="zoom" type="range" min="1" max="2" value="1" step="0.01" onpointerdown="onZoomPointerDown()" onpointerup="onZoomPointerUp()" onpointercancel="onZoomPointerUp()" oninput="setZoomPreview(this.value)" onchange="commitZoom(this.value)"/></span><span id="zoom-label">100%</span></div>
              </div>
              <div id="options-card" class="control-card" style="display:none">
                <div id="track-controls" class="track-select-row" style="display:none">
                  <div id="audio-track-group" class="track-group" style="display:none">
                    <div class="track-group-label">Audio</div>
                    <select id="audio-track-select" class="track-select" onchange="setAudioTrack(this.value)" aria-label="Audio"></select>
                  </div>
                  <div id="subtitle-track-group" class="track-group" style="display:none">
                    <div class="track-group-label">Subtitles</div>
                    <select id="subtitle-track-select" class="track-select" onchange="setSubtitleTrack(this.value)" aria-label="Subtitles"></select>
                  </div>
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
        let statusFailures=0;
        let cardHoldTimer=null,cardHoldOpened=false;
        let phoneIdleTimer=null;
        let zoomDragging=false;
        const playedVideos=new Set(loadPlayedVideos());
        const installHint=document.getElementById('install-hint');
        const phoneLayoutQuery=window.matchMedia('(max-width:760px) and (pointer:coarse)');
        let isPhoneRemoteOnly=phoneLayoutQuery.matches;
        if(!localStorage.getItem('remotePlayInstallHintDismissed')&&window.matchMedia('(display-mode: browser)').matches)installHint.style.display='flex';

        function applyPhonePlaybackState(isPlaying){
          if(!isPhoneRemoteOnly)return;
          document.body.classList.toggle('phone-playing',Boolean(isPlaying));
          document.getElementById('now-playing-bar').style.display=isPlaying?'flex':'none';
          if(isPlaying)resetPhoneIdleTimer();else clearPhoneIdleTimer();
        }

        function clearPhoneIdleTimer(){
          clearTimeout(phoneIdleTimer);
          phoneIdleTimer=null;
          document.body.classList.remove('controls-dimmed');
        }

        function resetPhoneIdleTimer(){
          if(!isPhoneRemoteOnly||!document.body.classList.contains('phone-playing'))return;
          document.body.classList.remove('controls-dimmed');
          clearTimeout(phoneIdleTimer);
          phoneIdleTimer=setTimeout(()=>{
            if(isPhoneRemoteOnly&&document.body.classList.contains('phone-playing'))document.body.classList.add('controls-dimmed');
          },4200);
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
            clearPhoneIdleTimer();
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
        ['pointerdown','touchstart','keydown','scroll'].forEach(eventName=>{
          document.addEventListener(eventName,()=>resetPhoneIdleTimer(),{passive:true});
        });
        function startPolling(){if(pollInterval)return;pollInterval=setInterval(pollStatus,1000);}
        function stopPolling(){clearInterval(pollInterval);pollInterval=null;}

        async function pollStatus(){
          try{
            const res=await fetch('/api/status');
            if(!res.ok){setConnectionStatus('Retrying connection...',true);statusFailures++;return;}
            const s=await res.json();
            statusFailures=0;
            setConnectionStatus('Connected',false,true);
            updateDiagnosticsIndicator(s.lastError? 'error':'ok');
            const bar=document.getElementById('now-playing-bar');
            if(s.isPlaying){
              bar.style.display='flex';
              const pb=document.getElementById('pause-btn');
              pb.textContent=s.isPaused?'\u25B6 Resume':'\u23F8 Pause';
              document.getElementById('player-title').textContent=(s.title||'Now playing').replace(/^\s*[▶⏸]\s*/,'');
              const optionsCard=document.getElementById('options-card');
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
              const brightness=Math.max(0.2,Math.min(0.8,Number(s.brightness)||0.5));
              document.getElementById('brightness').value=brightness;
              document.getElementById('brightness-label').textContent=Math.round(brightness*100)+'%';
              const rawSaturation=Number(s.saturation);
              const saturation=Math.max(0,Math.min(2,Number.isFinite(rawSaturation)?rawSaturation:1));
              document.getElementById('saturation').value=saturation;
              document.getElementById('saturation-label').textContent=Math.round(saturation*100)+'%';
              const rawZoom=Number(s.zoom);
              const zoom=Math.max(1,Math.min(2,Number.isFinite(rawZoom)?rawZoom:1));
              if(!zoomDragging){
                document.getElementById('zoom').value=zoom;
                document.getElementById('zoom-label').textContent=Math.round(zoom*100)+'%';
              }
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
              updateTrackControls(s);
              updateAdjacentButtons(s);
              document.getElementById('player-meta').textContent=buildPlayerMeta(s,volume,boostAmount);
              requestWakeLock();
              if(isPhoneRemoteOnly)applyPhonePlaybackState(true);
              else applyDesktopDockedLayout(true);
            }else if(isPhoneRemoteOnly){
              releaseWakeLock();
              applyPhonePlaybackState(false);
            }else{
              releaseWakeLock();
              applyDesktopDockedLayout(false);
              bar.style.display='none';
            }
          }catch(e){statusFailures++;setConnectionStatus(statusFailures>2?'Connection lost - retrying...':'Retrying connection...',true);updateDiagnosticsIndicator('error');}
        }

        function updateDiagnosticsIndicator(state){
          const dot=document.getElementById('diag-dot');
          if(!dot)return;
          dot.classList.toggle('ok',state==='ok');
          dot.classList.toggle('error',state==='error');
          dot.title=state==='ok'?'Server connected':state==='error'?'Server or playback issue':'Checking server';
        }

        function setConnectionStatus(message,isError,isConnected){
          const el=document.getElementById('connection-status');
          if(!el)return;
          el.textContent=message;
          el.classList.toggle('error',Boolean(isError));
          el.classList.toggle('connected',Boolean(isConnected));
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
        async function setBrightness(value){const brightness=Math.max(0.2,Math.min(0.8,parseFloat(value)||0.5));document.getElementById('brightness').value=brightness.toFixed(2);document.getElementById('brightness-label').textContent=Math.round(brightness*100)+'%';await api('/api/brightness?value='+encodeURIComponent(brightness.toFixed(2)));}
        async function setSaturation(value){const parsed=parseFloat(value);const saturation=Math.max(0,Math.min(2,Number.isFinite(parsed)?parsed:1));document.getElementById('saturation').value=saturation.toFixed(2);document.getElementById('saturation-label').textContent=Math.round(saturation*100)+'%';await api('/api/saturation?value='+encodeURIComponent(saturation.toFixed(2)));}
        function onZoomPointerDown(){zoomDragging=true;}
        function onZoomPointerUp(){zoomDragging=false;}
        function setZoomPreview(value){const parsed=parseFloat(value);const zoom=Math.max(1,Math.min(2,Number.isFinite(parsed)?parsed:1));document.getElementById('zoom').value=zoom.toFixed(2);document.getElementById('zoom-label').textContent=Math.round(zoom*100)+'%';}
        async function commitZoom(value){setZoomPreview(value);await api('/api/zoom?value='+encodeURIComponent(parseFloat(document.getElementById('zoom').value).toFixed(2)));zoomDragging=false;}
        function resetBrightnessMid(){haptic(8);setBrightness(0.5);}
        function resetSaturationMid(){haptic(8);setSaturation(1);}
        function resetZoomDefault(){haptic(8);commitZoom(1);}
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
        function updateTrackControls(s){
          const audioSelect=document.getElementById('audio-track-select');
          const subtitleSelect=document.getElementById('subtitle-track-select');
          const audioGroup=document.getElementById('audio-track-group');
          const subtitleGroup=document.getElementById('subtitle-track-group');
          const trackControls=document.getElementById('track-controls');
          const audioTracks=Array.isArray(s.audioTracks)?s.audioTracks:[];
          const subtitleTracks=Array.isArray(s.subtitleTracks)?s.subtitleTracks:[];
          const showAudio=audioTracks.length>1;
          const showSubtitles=subtitleTracks.some(t=>Number(t.id)>=0);
          renderTrackSelect(audioSelect,audioTracks,s.currentAudioTrackId);
          renderTrackSelect(subtitleSelect,subtitleTracks,s.currentSubtitleTrackId);
          audioGroup.style.display=showAudio?'flex':'none';
          subtitleGroup.style.display=showSubtitles?'flex':'none';
          trackControls.style.display=(showAudio||showSubtitles)?'flex':'none';
          document.getElementById('options-card').style.display=(showAudio||showSubtitles)?'flex':'none';
        }
        function renderTrackSelect(select,tracks,currentId){
          const signature=JSON.stringify((tracks||[]).map(t=>[t.id,t.name]));
          if(select.dataset.signature!==signature){
            select.innerHTML=(tracks||[]).map(t=>'<option value="'+esc(String(t.id))+'">'+esc(t.name||('Track '+t.id))+'</option>').join('');
            select.dataset.signature=signature;
          }
          select.value=String(currentId);
        }
        function updateAdjacentButtons(s){
          const previous=document.getElementById('previous-btn');
          const next=document.getElementById('next-btn');
          const previousTitle=(s.previousTitle||'').trim();
          const nextTitle=(s.nextTitle||'').trim();
          previous.querySelector('.nav-title').textContent=previousTitle?shortTitle(previousTitle):'';
          next.querySelector('.nav-title').textContent=nextTitle?shortTitle(nextTitle):'';
          previous.title=previousTitle?('Play previous: '+previousTitle):'No previous video';
          next.title=nextTitle?('Play next: '+nextTitle):'No next video';
          previous.disabled=!previousTitle;
          next.disabled=!nextTitle;
        }
        function shortTitle(value){return value.length>80?value.slice(0,77)+'...':value;}
        function buildPlayerMeta(s,volume,boostAmount){
          const watched=s.duration>0?Math.round((s.position/s.duration)*100)+'% watched':'';
          const bits=[watched,currentPlaybackSpeed.toFixed(2).replace(/\.00$/,'')+'x','Vol '+Math.round(volume*100)+'%','Boost '+Math.round(boostAmount*100)+'%'];
          return bits.filter(Boolean).join(' • ');
        }
        async function setAudioTrack(id){haptic(8);await api('/api/audio-track?id='+encodeURIComponent(id));}
        async function setSubtitleTrack(id){haptic(8);await api('/api/subtitle-track?id='+encodeURIComponent(id));}
        async function playAdjacent(direction){
          haptic(10);
          await api('/api/adjacent?direction='+encodeURIComponent(direction));
          // Poll until the new file is playing (up to ~2s)
          let tries=0;
          const poll=setInterval(async()=>{
            try{
              const res=await fetch('/api/status');
              if(!res.ok){if(++tries>8)clearInterval(poll);return;}
              const s=await res.json();
              if(s.isPlaying&&s.filePath&&s.filePath!==playingPath){
                clearInterval(poll);
                const oldPath=playingPath;
                playingPath=s.filePath;
                markPlayed(s.filePath);
                updatePlayingCard(s.filePath,oldPath);
              }else if(++tries>8){clearInterval(poll);}
            }catch(e){if(++tries>8)clearInterval(poll);}
          },250);
        }
        async function rescan(){setStatus('Refreshing search index...');await api('/api/rescan');}

        function resetCardsScrollTop(){
          const browser=document.getElementById('browser');
          if(browser)browser.scrollTop=0;
          document.documentElement.scrollTop=0;
          document.body.scrollTop=0;
          window.scrollTo(0,0);
        }
        async function loadRecent(){
          try{
            const res=await fetch('/api/recent');
            if(!res.ok)return [];
            const data=await res.json();
            return Array.isArray(data.files)?data.files:[];
          }catch(e){return [];}
        }
        async function browse(d){
          setStatus('Loading...');currentDir=d;document.getElementById('search').value='';
          try{
            const url=d?'/api/browse?dir='+encodeURIComponent(d):'/api/browse';
            const res=await fetch(url);
            if(!res.ok){setStatus('Server error '+res.status);return;}
            currentData=await res.json();render(currentData);
            if(!d)renderRecent(await loadRecent());
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
              const action=isPlaying?'':' onclick="onCardClick(event,\''+f.path+'\')"';
              const stopButton=isPlaying?'<button class="stop-btn" onclick="stopPlayingCard(event,\''+f.path+'\')">&#9632; STOP</button>':'';
              const cardActions=isPlaying?'':'<div class="card-actions"><button onclick="playCardAction(event,\''+f.path+'\',\''+esc(f.name)+'\')">Play</button><button onclick="startOverCard(event,\''+f.path+'\',\''+esc(f.name)+'\')">Start over</button><button onclick="toggleWatchedCard(event,\''+f.path+'\')">'+(played?'Unwatch':'Watched')+'</button></div>';
              const displayName=f.displayName||f.name;
              const resumeBadge=f.resume?'<div class="resume-badge">Resume '+esc(f.resume)+'</div>':'';
              return '<div class="'+cardClass+'" id="card-'+f.path+'" '+bg+action+' onpointerdown="beginCardHold(event,\''+f.path+'\')" onpointerup="endCardHold()" onpointercancel="endCardHold()" onpointerleave="endCardHold()">'+
                '<div class="movie-card-inner">'+
                '<div class="movie-title">'+esc(displayName)+'</div>'+
                resumeBadge+
                stopButton+
                cardActions+
                '</div></div>';
            }).join('');
            html+='</div>';}
          if(!data.folders.length&&!data.files.length)html='<div id="empty">No subfolders or video files here.</div>';
          document.getElementById('browser').innerHTML=html;
        }
        function renderRecent(files){
          if(!files.length)return;
          const browser=document.getElementById('browser');
          const cards=files.map(f=>{
            const thumbUrl='/api/thumb?path='+encodeURIComponent(f.path);
            const bg='style="background-image:url('+thumbUrl+')"';
            const pct=Math.max(0,Math.min(99,Math.round((Number(f.progress)||0)*100)));
            const displayName=f.displayName||f.name;
            const resume=f.resume||fmt(Number(f.position)||0);
            return '<div class="movie-card played" id="recent-card-'+f.path+'" '+bg+' onclick="onCardClick(event,\''+f.path+'\')" onpointerdown="beginCardHold(event,\''+f.path+'\')" onpointerup="endCardHold()" onpointercancel="endCardHold()" onpointerleave="endCardHold()">'+
              '<div class="movie-card-inner"><div class="movie-title">'+esc(displayName)+'</div>'+
              '<div class="resume-badge">Resume '+esc(resume)+'</div>'+
              '<div class="movie-meta">'+pct+'% • '+esc(f.folder||'')+'</div>'+
              '<div class="card-actions"><button onclick="playCardAction(event,\''+f.path+'\',\''+esc(displayName)+'\')">Play</button><button onclick="startOverCard(event,\''+f.path+'\',\''+esc(displayName)+'\')">Start over</button><button onclick="toggleWatchedCard(event,\''+f.path+'\')">Unwatch</button></div></div></div>';
          }).join('');
          const html='<div class="section-label recent-toggle" onclick="toggleRecentSection(this)" style="cursor:pointer;user-select:none;">'+
            'Recently watched <span class="recent-chevron" style="font-size:0.75em;margin-left:6px;">▶</span></div>'+
            '<div class="movie-grid recent-grid" style="display:none;">'+cards+'</div>';
          browser.innerHTML=html+browser.innerHTML;
        }
        function toggleRecentSection(label){
          const grid=label.nextElementSibling;
          const chevron=label.querySelector('.recent-chevron');
          const open=grid.style.display==='none';
          grid.style.display=open?'':'none';
          if(chevron)chevron.textContent=open?'▼':'▶';
        }
        function goBack(){const d=document.getElementById('back-button').dataset.dir;if(d)browse(d);}
        function onCardClick(event,p){
          if(cardHoldOpened){cardHoldOpened=false;event.preventDefault();event.stopPropagation();return;}
          play(p);
        }
        function beginCardHold(event,p){
          if(!window.matchMedia('(pointer:coarse)').matches)return;
          clearTimeout(cardHoldTimer);
          cardHoldOpened=false;
          const targetCard=event.currentTarget;
          cardHoldTimer=setTimeout(()=>{
            document.querySelectorAll('.movie-card.actions-open').forEach(card=>card.classList.remove('actions-open'));
            if(targetCard){targetCard.classList.add('actions-open');cardHoldOpened=true;haptic(18);}
          },520);
        }
        function endCardHold(){clearTimeout(cardHoldTimer);cardHoldTimer=null;}
        async function play(p,name){
          const title=name||'';
          playingPath=p;setStatus(title?'Playing: '+title:'Playing...');
          setPlayerPoster(p);
          markPlayed(p);
          updatePlayingCard(p);
          await api('/api/play?path='+encodeURIComponent(p));
          startPolling();
        }
        function setPlayerPoster(p){
          const bar=document.getElementById('now-playing-bar');
          if(!bar||!p)return;
          const thumb='/api/thumb?path='+encodeURIComponent(p);
          bar.style.background='linear-gradient(135deg,rgba(13,13,26,.92),rgba(21,21,38,.88)),url('+thumb+') center/cover';
        }
        async function playCardAction(event,p,name){
          event.stopPropagation();
          await play(p,name);
        }
        async function startOverCard(event,p,name){
          event.stopPropagation();
          await play(p,name);
          await api('/api/seek?pos=0');
        }
        function toggleWatchedCard(event,p){
          event.stopPropagation();
          if(playedVideos.has(p))unmarkPlayed(p);else markPlayed(p);
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
          document.getElementById('now-playing-bar').style.background='linear-gradient(135deg,#0d0d1a,#151526)';
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
          if(card){
            card.classList.add('played');
            const buttons=card.querySelectorAll('.card-actions button');
            if(buttons.length>=3)buttons[2].textContent='Unwatch';
          }
        }
        function unmarkPlayed(p){
          playedVideos.delete(p);savePlayedVideos();
          const card=document.getElementById('card-'+p);
          if(card){
            card.classList.remove('played');
            const buttons=card.querySelectorAll('.card-actions button');
            if(buttons.length>=3)buttons[2].textContent='Watched';
          }
        }
        function updatePlayingCard(newPath,oldPath){
          const paths=[oldPath,playingPath,newPath].filter(Boolean);
          document.querySelectorAll('.movie-card.playing').forEach(c=>paths.push(c.id.substring(5)));
          for(const p of new Set(paths)){
            const card=document.getElementById('card-'+p);if(!card)continue;
            const isActive=p===newPath;
            card.classList.toggle('playing',isActive);
            card.classList.remove('actions-open');
            card.classList.toggle('played',!isActive&&playedVideos.has(p));
            card.onclick=isActive?null:event=>onCardClick(event,p);
            const actions=card.querySelector('.card-actions');
            if(actions)actions.style.display=isActive?'none':'';
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
        let wakeLock=null;
        async function requestWakeLock(){
          if(wakeLock||!('wakeLock' in navigator))return;
          try{wakeLock=await navigator.wakeLock.request('screen');wakeLock.addEventListener('release',()=>wakeLock=null);}catch(e){}
        }
        async function releaseWakeLock(){
          if(!wakeLock)return;
          try{await wakeLock.release();}catch(e){}
          wakeLock=null;
        }
        document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible'&&playingPath)requestWakeLock();});
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
