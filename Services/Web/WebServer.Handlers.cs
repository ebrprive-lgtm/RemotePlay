using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RemotePlay;

internal sealed partial class WebServer
{
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

        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

        switch (urlPath)
        {
            case "/" or "/index.html":
                TrySendResponse(ctx, 200, "text/html; charset=utf-8", GetHtmlPage());
                break;

            case "/offline.html":
                TrySendResponse(ctx, 200, "text/html; charset=utf-8", LoadWebAsset("offline.html"));
                break;

            case "/manifest.webmanifest":
                TrySendResponse(ctx, 200, "application/manifest+json; charset=utf-8", ManifestJson);
                break;

            case "/styles.css":
                ctx.Response.AddHeader("Cache-Control", "no-cache");
                TrySendResponse(ctx, 200, "text/css; charset=utf-8", GetStylesCss());
                break;

            case "/app.js":
                ctx.Response.AddHeader("Cache-Control", "no-cache");
                TrySendResponse(ctx, 200, "application/javascript; charset=utf-8", GetAppJs());
                break;

            case "/service-worker.js":
                ctx.Response.AddHeader("Cache-Control", "no-cache");
                TrySendResponse(ctx, 200, "application/javascript; charset=utf-8", GetServiceWorkerJs());
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

            case "/setup":
                HandleSetupPage(ctx);
                break;

            case "/remoteplay.log":
                HandleLogDownload(ctx);
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
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, scan = GetLibraryScanStatus() }));
                break;

            case "/api/library-status":
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(GetLibraryScanStatus()));
                break;

            case "/api/thumbnails/status":
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(GetThumbnailQueueStatus()));
                break;

            case "/api/thumbnails/start":
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(StartThumbnailQueue()));
                break;

            case "/api/thumbnails/cancel":
                CancelThumbnailQueue();
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(GetThumbnailQueueStatus()));
                break;

            case "/api/recent":
                HandleRecent(ctx);
                break;

            case "/api/favorites":
                HandleFavorites(ctx);
                break;

            case "/api/favorite":
                HandleFavorite(ctx);
                break;

            case "/api/history/clear":
                HandleHistoryClear(ctx);
                break;

            case "/api/play":
                HandlePlay(ctx);
                break;

            case "/api/queue/add":
                HandleQueueAdd(ctx);
                break;

            case "/api/queue/remove":
                HandleQueueRemove(ctx);
                break;

            case "/api/queue/move":
                HandleQueueMove(ctx);
                break;

            case "/api/queue/clear":
                _callbacks.ClearQueue();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/stop":
                _callbacks.Stop();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/pause":
                _callbacks.Pause();
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

            case "/api/saturation":
                HandleSaturation(ctx);
                break;

            case "/api/zoom":
                HandleZoom(ctx);
                break;

            case "/api/audio-boost":
                HandleAudioBoost(ctx);
                break;

            case "/api/speed":
                HandlePlaybackSpeed(ctx);
                break;

            case "/api/audio-track":
                HandleAudioTrack(ctx);
                break;

            case "/api/subtitle-track":
                HandleSubtitleTrack(ctx);
                break;

            case "/api/adjacent":
                HandleAdjacent(ctx);
                break;

            case "/api/subtitles":
                _callbacks.ToggleSubtitles();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/mute":
                _callbacks.ToggleMute();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                break;

            case "/api/health":
                HandleHealth(ctx);
                break;

            case "/api/display-diagnostics":
                HandleDisplayDiagnostics(ctx);
                break;

            case "/api/peers":
                HandlePeers(ctx);
                break;

            case "/api/version":
                HandleVersion(ctx);
                break;

            default:
                TrySendResponse(ctx, 404, "text/plain", "Not found");
                break;
        }
    }

    private void HandleThumb(HttpListenerContext ctx)
    {
        if (!_config.EnableThumbnailGeneration)
        {
            TrySendResponse(ctx, 204, "text/plain", string.Empty);
            return;
        }

        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        byte[]? jpeg;
        try
        {
            var filePath = WebPathHelpers.DecodePath(encodedPath);
            var cacheFile = GetThumbnailCacheFile(filePath);
            if (File.Exists(cacheFile))
                jpeg = File.ReadAllBytes(cacheFile);
            else
                jpeg = _thumbCache.GetOrAdd(encodedPath, key => GenerateAndPersistThumbnail(key, cacheFile));
        }
        catch
        {
            jpeg = null;
        }

        if (jpeg is null)
        {
            TrySendResponse(ctx, 404, "text/plain", "No thumbnail");
            return;
        }

        ctx.Response.AddHeader("Cache-Control", "public, max-age=3600");
        TrySendBytes(ctx, 200, "image/jpeg", jpeg);
    }

    private static byte[]? GenerateAndPersistThumbnail(string encodedPath, string cacheFile)
    {
        try
        {
            var filePath = WebPathHelpers.DecodePath(encodedPath);
            var jpeg = ThumbnailHelper.GetJpegThumbnail(filePath);
            if (jpeg is null)
                return null;

            Directory.CreateDirectory(ThumbnailCacheDirectory);
            File.WriteAllBytes(cacheFile, jpeg);
            return jpeg;
        }
        catch
        {
            return null;
        }
    }

    private static string GetThumbnailCacheFile(string filePath)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(filePath)));
        return Path.Combine(ThumbnailCacheDirectory, key + ".jpg");
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
        var s = _callbacks.GetStatus();
        var json = JsonSerializer.Serialize(new
        {
            isPlaying = s.IsPlaying,
            isPaused = s.IsPaused,
            position = Math.Round(s.PositionSeconds, 1),
            duration = Math.Round(s.DurationSeconds, 1),
            title = s.Title ?? string.Empty,
            volume = Math.Round(s.Volume, 2),
            brightness = Math.Round(s.Brightness, 2),
            saturation = Math.Round(s.Saturation, 2),
            zoom = Math.Round(s.Zoom, 2),
            audioBoost = Math.Round(s.AudioBoost, 2),
            playbackSpeed = Math.Round(s.PlaybackSpeed, 2),
            isMuted = s.IsMuted,
            lastError = s.LastError ?? string.Empty,
            canResume = s.CanResume,
            resumePosition = Math.Round(s.ResumePositionSeconds, 1),
            smartResumeApplied = s.SmartResumeApplied,
            subtitlesEnabled = s.SubtitlesEnabled,
            hasSubtitles = s.HasSubtitles,
            audioTracks = s.AudioTracks.Select(t => new { id = t.Id, name = t.Name }).ToArray(),
            subtitleTracks = s.SubtitleTracks.Select(t => new { id = t.Id, name = t.Name }).ToArray(),
            currentAudioTrackId = s.CurrentAudioTrackId,
            currentSubtitleTrackId = s.CurrentSubtitleTrackId,
            previousTitle = s.PreviousTitle ?? string.Empty,
            nextTitle = s.NextTitle ?? string.Empty,
            filePath = s.FilePath is not null ? WebPathHelpers.EncodePath(s.FilePath) : string.Empty,
            queue = s.Queue.Select(q => new
            {
                path = q.Path,
                title = q.Title
            }).ToArray(),
            queueCount = s.QueueCount,
            playbackEndBehavior = _config.PlaybackEndBehavior.ToString(),
            preferredAudioLanguage = _config.PreferredAudioLanguage,
            preferredSubtitleLanguage = _config.PreferredSubtitleLanguage,
            preferForcedSubtitles = _config.PreferForcedSubtitles
        });
        TrySendResponse(ctx, 200, "application/json", json);
    }

    private void HandleDisplayDiagnostics(HttpListenerContext ctx)
    {
        var diagnostics = _callbacks.GetDisplayDiagnostics();
        var json = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true });
        TrySendResponse(ctx, 200, "application/json; charset=utf-8", json);
    }

    private void HandleSeek(HttpListenerContext ctx)
    {
        var posParam = ctx.Request.QueryString["pos"];
        if (MediaControlValueParser.TryParseDouble(posParam, out var secs))
        {
            _callbacks.Seek(secs);
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
        if (MediaControlValueParser.TryParseDouble(secondsParam, out var seconds))
        {
            _callbacks.Skip(seconds);
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
        if (MediaControlValueParser.TryParseClampedDouble(valueParam, 0, 1, out var volume))
        {
            _callbacks.SetVolume(volume);
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
        if (MediaControlValueParser.TryParseClampedDouble(valueParam, 0, 1, out var brightness))
        {
            _callbacks.SetBrightness(brightness);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad brightness");
        }
    }

    private void HandleSaturation(HttpListenerContext ctx)
    {
        var valueParam = ctx.Request.QueryString["value"];
        if (MediaControlValueParser.TryParseClampedDouble(valueParam, 0, 2, out var saturation))
        {
            _callbacks.SetSaturation(saturation);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad saturation");
        }
    }

    private void HandleZoom(HttpListenerContext ctx)
    {
        var valueParam = ctx.Request.QueryString["value"];
        if (MediaControlValueParser.TryParseClampedDouble(valueParam, 1, 2, out var zoom))
        {
            _callbacks.SetZoom(zoom);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad zoom");
        }
    }

    private void HandleAudioBoost(HttpListenerContext ctx)
    {
        var valueParam = ctx.Request.QueryString["value"];
        if (MediaControlValueParser.TryParseClampedDouble(valueParam, 1, 2, out var audioBoost))
        {
            _callbacks.SetAudioBoost(audioBoost);
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
        if (MediaControlValueParser.TryParseClampedDouble(valueParam, 0.5, 2, out var playbackSpeed))
        {
            _callbacks.SetPlaybackSpeed(playbackSpeed);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad speed");
        }
    }

    private void HandleAudioTrack(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var trackId))
        {
            _callbacks.SetAudioTrack(trackId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad audio track");
        }
    }

    private void HandleSubtitleTrack(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var trackId))
        {
            _callbacks.SetSubtitleTrack(trackId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad subtitle track");
        }
    }

    private void HandleAdjacent(HttpListenerContext ctx)
    {
        var directionParam = ctx.Request.QueryString["direction"];
        var direction = string.Equals(directionParam, "previous", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        _callbacks.PlayAdjacent(direction);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleVersion(HttpListenerContext ctx)
    {
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            version = _appUpdater?.CurrentVersion ?? string.Empty,
            availableVersion = _appUpdater?.AvailableVersion ?? string.Empty,
            isUpdating = _appUpdater?.IsUpdating ?? false,
            lastUpdateError = _appUpdater?.LastUpdateError ?? string.Empty
        }));
    }

    private void HandleHealth(HttpListenerContext ctx)
    {
        var status = _callbacks.GetStatus();
        var runtime = BuildRuntimeHealth();
            var scanStatus = GetLibraryScanStatus();
        var json = JsonSerializer.Serialize(new
        {
            ok = true,
            requestedScheme = _config.Scheme,
            activeScheme = _activeScheme,
            startupWarning = _startupWarning ?? string.Empty,
            port = _config.Port,
            urls = new
            {
                local = $"{_activeScheme}://localhost:{_config.Port}/",
                listener = $"{_activeScheme}://*:{_config.Port}/"
            },
            moviesPath = _config.ResolvedMoviesPath,
            isPlaying = status.IsPlaying,
            lastError = status.LastError ?? string.Empty,
            indexedFiles = _libraryIndex.Length,
            isIndexing = _isIndexing,
            lastIndexRefreshUtc = _lastIndexRefreshUtc,
            thumbnailCache = BuildThumbnailCacheHealth(),
                scanStatus,
            preferences = new
            {
                playbackEndBehavior = _config.PlaybackEndBehavior.ToString(),
                preferredAudioLanguage = _config.PreferredAudioLanguage,
                preferredSubtitleLanguage = _config.PreferredSubtitleLanguage,
                preferForcedSubtitles = _config.PreferForcedSubtitles
            },
            runtime
        });
        TrySendResponse(ctx, 200, "application/json", json);
    }

    private static object BuildRuntimeHealth()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var libVlcDirectory = Path.Combine(baseDirectory, "libvlc", "win-x64");
        var libVlcPath = Path.Combine(libVlcDirectory, "libvlc.dll");
        var libVlcCorePath = Path.Combine(libVlcDirectory, "libvlccore.dll");
        return new
        {
            baseDirectory,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            libVlcDirectory,
            libVlcFound = File.Exists(libVlcPath),
            libVlcCoreFound = File.Exists(libVlcCorePath),
            logFile = Logger.FilePath,
            logFileFound = File.Exists(Logger.FilePath),
            logFileBytes = TryGetFileLength(Logger.FilePath)
        };
    }

    private static long TryGetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static object BuildThumbnailCacheHealth()
    {
        try
        {
            if (!Directory.Exists(ThumbnailCacheDirectory))
                return new { directory = ThumbnailCacheDirectory, files = 0, bytes = 0L };

            var files = Directory.EnumerateFiles(ThumbnailCacheDirectory, "*.jpg").ToArray();
            return new
            {
                directory = ThumbnailCacheDirectory,
                files = files.Length,
                bytes = files.Sum(TryGetFileLength)
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Diagnostics", "Could not read thumbnail cache diagnostics", ex);
            return new { directory = ThumbnailCacheDirectory, files = 0, bytes = 0L };
        }
    }

    private void HandleHealthPage(HttpListenerContext ctx)
    {
        var status = _callbacks.GetStatus();
        var displayDiagnostics = _callbacks.GetDisplayDiagnostics();
        var scanStatus = GetLibraryScanStatus();
        var certificate = TryGetHttpsCertificate();
        var runtime = BuildRuntimeHealthJson();
        var startupWarning = string.IsNullOrWhiteSpace(_startupWarning) ? "None" : _startupWarning;
        var playbackError = string.IsNullOrWhiteSpace(status.LastError) ? "None" : status.LastError;
        var scanError = string.IsNullOrWhiteSpace(scanStatus.LastError) ? "None" : scanStatus.LastError;
        var serverState = string.IsNullOrWhiteSpace(_startupWarning) ? "ok" : "warn";
        var playbackState = string.IsNullOrWhiteSpace(status.LastError) ? "ok" : "warn";
        var libraryState = string.IsNullOrWhiteSpace(scanStatus.LastError) ? (_isIndexing ? "warn" : "ok") : "warn";
        var displayState = displayDiagnostics.NeedsFullscreenRepair ? "warn" : "ok";
        var certificateState = certificate is not null ? "ok" : "warn";
        var playbackLabel = status.IsPlaying ? "Playing" : "Idle";
        var libraryLabel = _isIndexing ? "Indexing" : "Ready";
        var displayLabel = displayDiagnostics.NeedsFullscreenRepair ? "Repair needed" : "Fullscreen OK";
        var certificateLabel = certificate is not null ? "Available" : "Missing";
        var html = $$$$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width,initial-scale=1"/>
            <meta name="theme-color" content="#0b1020"/>
            <title>RemotePlay Health</title>
            <style>
            :root{color-scheme:dark;--bg:#090d18;--bg2:#121832;--card:rgba(23,28,53,.82);--card2:rgba(16,20,38,.92);--line:rgba(148,163,184,.18);--text:#eef4ff;--muted:#9aa8c2;--dim:#6f7b94;--accent:#e94560;--cyan:#00d4aa;--warn:#ffaa00;--bad:#ff5c77;--blue:#5b8cff;--shadow:0 20px 60px rgba(0,0,0,.34)}
            *{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(circle at top left,rgba(91,140,255,.22),transparent 34rem),radial-gradient(circle at top right,rgba(233,69,96,.18),transparent 30rem),linear-gradient(135deg,var(--bg),#05070d 70%);color:var(--text);font-family:Segoe UI,Arial,sans-serif}a{color:var(--cyan);text-decoration:none}a:hover{text-decoration:underline}.page{width:min(1440px,100%);margin:0 auto;padding:18px}.hero{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:1rem;align-items:end;padding:1.1rem;border:1px solid var(--line);border-radius:24px;background:linear-gradient(135deg,rgba(26,32,61,.9),rgba(12,16,32,.86));box-shadow:var(--shadow);position:sticky;top:10px;z-index:3;backdrop-filter:blur(14px)}.eyebrow{color:var(--muted);font-weight:800;font-size:.76rem;text-transform:uppercase;letter-spacing:.1em}.hero h1{margin:.15rem 0 .25rem;font-size:clamp(1.55rem,4vw,3rem);line-height:1;color:#fff}.subtitle{color:var(--muted);font-size:.95rem;line-height:1.35}.actions{display:flex;gap:.55rem;flex-wrap:wrap;justify-content:flex-end}.button,button{display:inline-flex;align-items:center;justify-content:center;gap:.35rem;border:1px solid rgba(255,255,255,.12);border-radius:999px;background:rgba(34,41,76,.9);color:#eaf0ff;padding:.68rem .95rem;font-weight:800;cursor:pointer;min-height:42px}.button.primary,button.primary{background:linear-gradient(135deg,var(--accent),#ff6f61);color:#fff}.button:hover,button:hover{filter:brightness(1.12);text-decoration:none}.overview{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:.85rem;margin:1rem 0}.tile{background:var(--card);border:1px solid var(--line);border-radius:20px;padding:1rem;box-shadow:0 12px 30px rgba(0,0,0,.2);min-width:0}.tile-label{color:var(--muted);font-size:.72rem;font-weight:800;text-transform:uppercase;letter-spacing:.08em}.tile-value{font-size:1.2rem;font-weight:900;margin:.35rem 0 .45rem;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.pill{display:inline-flex;align-items:center;gap:.35rem;border-radius:999px;padding:.28rem .55rem;font-size:.75rem;font-weight:900;background:rgba(148,163,184,.13);color:var(--muted)}.pill::before{content:'';width:.55rem;height:.55rem;border-radius:50%;background:var(--dim);box-shadow:0 0 0 3px rgba(148,163,184,.12)}.pill.ok{color:#a7ffe9;background:rgba(0,212,170,.12)}.pill.ok::before{background:var(--cyan)}.pill.warn{color:#ffd48a;background:rgba(255,170,0,.13)}.pill.warn::before{background:var(--warn)}.dashboard{display:grid;grid-template-columns:1.1fr .9fr;gap:1rem;align-items:start}.stack{display:grid;gap:1rem}.card{background:var(--card);border:1px solid var(--line);border-radius:22px;padding:1rem;box-shadow:0 12px 32px rgba(0,0,0,.2);overflow:hidden}.card h2{display:flex;align-items:center;justify-content:space-between;gap:.7rem;margin:0 0 .8rem;font-size:1rem}.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.55rem .8rem}.metric{background:rgba(255,255,255,.035);border:1px solid rgba(255,255,255,.06);border-radius:14px;padding:.65rem;min-width:0}.metric dt{color:var(--muted);font-size:.72rem;font-weight:800;text-transform:uppercase;letter-spacing:.055em;margin:0 0 .3rem}.metric dd{margin:0;color:#fff;word-break:break-word;line-height:1.3}.metric.wide{grid-column:1/-1}.mono{font-family:Consolas,ui-monospace,monospace}.runtime-card{grid-column:1/-1}pre{white-space:pre-wrap;background:rgba(0,0,0,.32);border:1px solid rgba(255,255,255,.08);border-radius:16px;padding:1rem;overflow:auto;max-height:420px;color:#dce6ff}.admin-result{min-height:1.2rem;color:#ffd48a;font-weight:700}.footer-note{color:var(--dim);font-size:.78rem;text-align:center;padding:1.2rem}.compact-list{display:grid;gap:.5rem}.divider{height:1px;background:var(--line);margin:.85rem 0}.ok-text{color:var(--cyan)}.warn-text{color:var(--warn)}@media (max-width:1100px){.overview{grid-template-columns:repeat(2,minmax(0,1fr))}.dashboard{grid-template-columns:1fr}.runtime-card{grid-column:auto}}@media (max-width:640px){.page{padding:.6rem}.hero{position:static;grid-template-columns:1fr;border-radius:18px;padding:.85rem}.actions{display:grid;grid-template-columns:1fr 1fr;justify-content:stretch}.button,button{width:100%;padding:.72rem .7rem}.overview{grid-template-columns:1fr;gap:.55rem;margin:.65rem 0}.tile{display:grid;grid-template-columns:1fr auto;align-items:center;padding:.75rem;border-radius:16px}.tile-value{font-size:1rem;margin:.1rem 0}.tile .pill{grid-row:1/3;grid-column:2}.card{padding:.75rem;border-radius:16px}.grid{grid-template-columns:1fr;gap:.5rem}.metric{padding:.6rem}.hero h1{font-size:1.55rem}.subtitle{font-size:.84rem}pre{max-height:300px;font-size:.78rem}}
            </style>
            </head>
            <body>
            <main class="page">
            <section class="hero">
              <div><div class="eyebrow">RemotePlay diagnostics</div><h1>Health dashboard</h1><div class="subtitle">Live server, playback, display, library, certificate, and runtime checks for this media computer.</div></div>
              <div class="actions"><a class="button" href="/">Open remote</a><button class="primary" onclick="refreshRuntime()">Refresh</button><button onclick="location.href='/remoteplay.log'">Log</button></div>
            </section>
            <section class="overview" aria-label="Health summary">
              <article class="tile"><div><div class="tile-label">Server</div><div class="tile-value">{{{{HtmlEncode(_activeScheme.ToUpperInvariant())}}}} : {{{{_config.Port}}}}</div></div><span class="pill {{{{serverState}}}}">{{{{serverState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div><div class="tile-label">Playback</div><div class="tile-value">{{{{HtmlEncode(playbackLabel)}}}}</div></div><span class="pill {{{{playbackState}}}}">{{{{playbackState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div><div class="tile-label">Library</div><div class="tile-value">{{{{_libraryIndex.Length}}}} videos</div></div><span class="pill {{{{libraryState}}}}">{{{{HtmlEncode(libraryLabel)}}}}</span></article>
              <article class="tile"><div><div class="tile-label">Display</div><div class="tile-value">{{{{HtmlEncode(displayLabel)}}}}</div></div><span class="pill {{{{displayState}}}}">{{{{displayState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div><div class="tile-label">HTTPS cert</div><div class="tile-value">{{{{HtmlEncode(certificateLabel)}}}}</div></div><span class="pill {{{{certificateState}}}}">{{{{certificateState.ToUpperInvariant()}}}}</span></article>
            </section>
            <section class="dashboard">
              <div class="stack">
                <section class="card"><h2>Server <span class="pill {{{{serverState}}}}">{{{{serverState.ToUpperInvariant()}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Requested mode</dt><dd>{{{{HtmlEncode(_config.Scheme.ToUpperInvariant())}}}}</dd></div>
                  <div class="metric"><dt>Active mode</dt><dd>{{{{HtmlEncode(_activeScheme.ToUpperInvariant())}}}}</dd></div>
                  <div class="metric"><dt>Port</dt><dd>{{{{_config.Port}}}}</dd></div>
                  <div class="metric"><dt>Indexed videos</dt><dd>{{{{_libraryIndex.Length}}}}</dd></div>
                  <div class="metric wide"><dt>Movies folder</dt><dd class="mono">{{{{HtmlEncode(_config.ResolvedMoviesPath)}}}}</dd></div>
                  <div class="metric wide"><dt>Startup warning</dt><dd class="{{{{(serverState == "ok" ? "ok-text" : "warn-text")}}}}">{{{{HtmlEncode(startupWarning)}}}}</dd></div>
                </dl></section>
                <section class="card"><h2>Display diagnostics <span class="pill {{{{displayState}}}}">{{{{HtmlEncode(displayLabel)}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Preferred display</dt><dd>{{{{displayDiagnostics.PreferredDisplayIndex}}}}</dd></div>
                  <div class="metric"><dt>Target display</dt><dd>{{{{displayDiagnostics.TargetDisplayIndex}}}} &mdash; {{{{HtmlEncode(displayDiagnostics.TargetDisplayName)}}}}</dd></div>
                  <div class="metric"><dt>Target bounds</dt><dd>{{{{displayDiagnostics.TargetLeft}}}}, {{{{displayDiagnostics.TargetTop}}}}, {{{{displayDiagnostics.TargetWidth}}}}&times;{{{{displayDiagnostics.TargetHeight}}}}</dd></div>
                  <div class="metric"><dt>Window bounds</dt><dd>{{{{displayDiagnostics.WindowLeft}}}}, {{{{displayDiagnostics.WindowTop}}}}, {{{{displayDiagnostics.WindowWidth}}}}&times;{{{{displayDiagnostics.WindowHeight}}}}</dd></div>
                  <div class="metric"><dt>DPI scale</dt><dd>{{{{displayDiagnostics.DpiScaleX}}}} &times; {{{{displayDiagnostics.DpiScaleY}}}}</dd></div>
                  <div class="metric"><dt>Fullscreen repair</dt><dd>{{{{displayDiagnostics.NeedsFullscreenRepair}}}}</dd></div>
                  <div class="metric wide"><dt>Window state</dt><dd>{{{{HtmlEncode(displayDiagnostics.WindowState)}}}} / {{{{HtmlEncode(displayDiagnostics.WindowStyle)}}}} / {{{{HtmlEncode(displayDiagnostics.ResizeMode)}}}} / Topmost={{{{displayDiagnostics.Topmost}}}}</dd></div>
                  <div class="metric wide"><dt>Current video</dt><dd>{{{{HtmlEncode(string.IsNullOrWhiteSpace(displayDiagnostics.CurrentTitle) ? "N/A" : displayDiagnostics.CurrentTitle)}}}}</dd></div>
                  <div class="metric wide"><dt>Video file</dt><dd class="mono">{{{{HtmlEncode(string.IsNullOrWhiteSpace(displayDiagnostics.CurrentFilePath) ? "N/A" : displayDiagnostics.CurrentFilePath)}}}}</dd></div>
                  <div class="metric"><dt>Display settings</dt><dd>Zoom {{{{Math.Round(displayDiagnostics.Zoom * 100)}}}}%, Brightness {{{{Math.Round(displayDiagnostics.Brightness * 100)}}}}%, Saturation {{{{Math.Round(displayDiagnostics.Saturation * 100)}}}}%</dd></div>
                  <div class="metric"><dt>Video surface</dt><dd>Panel {{{{displayDiagnostics.VideoSurfaceWidth}}}}&times;{{{{displayDiagnostics.VideoSurfaceHeight}}}}, Player {{{{displayDiagnostics.VideoPlayerActualWidth}}}}&times;{{{{displayDiagnostics.VideoPlayerActualHeight}}}}</dd></div>
                </dl><div class="divider"></div><a href="/api/display-diagnostics" target="_blank" rel="noopener">Open display diagnostics JSON</a></section>
              </div>
              <div class="stack">
                <section class="card"><h2>Playback <span class="pill {{{{playbackState}}}}">{{{{HtmlEncode(playbackLabel)}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Playing</dt><dd>{{{{status.IsPlaying}}}}</dd></div>
                  <div class="metric"><dt>Paused</dt><dd>{{{{status.IsPaused}}}}</dd></div>
                  <div class="metric"><dt>Previous video</dt><dd>{{{{HtmlEncode(string.IsNullOrWhiteSpace(status.PreviousTitle) ? "N/A" : status.PreviousTitle)}}}}</dd></div>
                  <div class="metric"><dt>Next video</dt><dd>{{{{HtmlEncode(string.IsNullOrWhiteSpace(status.NextTitle) ? "N/A" : status.NextTitle)}}}}</dd></div>
                  <div class="metric wide"><dt>Last error</dt><dd class="{{{{(playbackState == "ok" ? "ok-text" : "warn-text")}}}}">{{{{HtmlEncode(playbackError)}}}}</dd></div>
                </dl></section>
                <section class="card"><h2>Playback preferences</h2><dl class="grid">
                  <div class="metric"><dt>End behavior</dt><dd>{{{{HtmlEncode(_config.PlaybackEndBehavior.ToString())}}}}</dd></div>
                  <div class="metric"><dt>Forced subtitles</dt><dd>{{{{_config.PreferForcedSubtitles}}}}</dd></div>
                  <div class="metric"><dt>Audio language</dt><dd>{{{{HtmlEncode(_config.PreferredAudioLanguage)}}}}</dd></div>
                  <div class="metric"><dt>Subtitle language</dt><dd>{{{{HtmlEncode(_config.PreferredSubtitleLanguage)}}}}</dd></div>
                </dl></section>
                <section class="card"><h2>Library index <span class="pill {{{{libraryState}}}}">{{{{HtmlEncode(libraryLabel)}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Indexed videos</dt><dd>{{{{_libraryIndex.Length}}}}</dd></div>
                  <div class="metric"><dt>Indexing now</dt><dd>{{{{_isIndexing}}}}</dd></div>
                  <div class="metric"><dt>Scanned files</dt><dd>{{{{scanStatus.ScannedFiles}}}}</dd></div>
                  <div class="metric"><dt>Scanned folders</dt><dd>{{{{scanStatus.ScannedFolders}}}}</dd></div>
                  <div class="metric"><dt>Scan started UTC</dt><dd>{{{{scanStatus.StartedUtc?.ToString("u") ?? "N/A"}}}}</dd></div>
                  <div class="metric"><dt>Last refresh UTC</dt><dd>{{{{_lastIndexRefreshUtc?.ToString("u") ?? "Never"}}}}</dd></div>
                  <div class="metric wide"><dt>Last scan error</dt><dd class="{{{{(scanError == "None" ? "ok-text" : "warn-text")}}}}">{{{{HtmlEncode(scanError)}}}}</dd></div>
                </dl></section>
                <section class="card"><h2>HTTPS certificate <span class="pill {{{{certificateState}}}}">{{{{HtmlEncode(certificateLabel)}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Certificate present</dt><dd>{{{{certificate is not null}}}}</dd></div>
                  <div class="metric"><dt>Expires</dt><dd>{{{{certificate?.NotAfter.ToString("u") ?? "N/A"}}}}</dd></div>
                  <div class="metric wide"><dt>Thumbprint</dt><dd class="mono">{{{{HtmlEncode(certificate?.Thumbprint ?? "N/A")}}}}</dd></div>
                </dl><div class="divider"></div><a href="/certificate.cer">Download certificate</a></section>
              </div>
              <section class="card runtime-card"><h2>Runtime diagnostics</h2><pre id="runtime-json">{{{{HtmlEncode(runtime)}}}}</pre></section>
              <section class="card runtime-card"><h2>Admin actions</h2><div class="actions" style="justify-content:flex-start"><button class="primary" onclick="rescanLibrary()">Start rescan</button><button onclick="location.href='/remoteplay.log'">Download log</button><button onclick="refreshRuntime()">Refresh runtime</button></div><p id="admin-result" class="admin-result"></p></section>
            </section>
            <div class="footer-note">RemotePlay health page generated locally by this media computer.</div>
            </main>
            <script>
            async function refreshRuntime(){
              const result=document.getElementById('admin-result');
              try{
                const [healthResponse,displayResponse]=await Promise.all([fetch('/api/health'),fetch('/api/display-diagnostics')]);
                const health=await healthResponse.json();
                const display=await displayResponse.json();
                document.getElementById('runtime-json').textContent=JSON.stringify(health.runtime,null,2);
                result.textContent='Runtime refreshed. Indexed videos: '+health.indexedFiles+'. Fullscreen repair needed: '+display.needsFullscreenRepair+'.';
              }catch(error){result.textContent='Runtime refresh failed: '+error;}
            }
            async function rescanLibrary(){
              const result=document.getElementById('admin-result');
              try{
                await fetch('/api/rescan');
                result.textContent='Library rescan started.';
              }catch(error){result.textContent='Rescan failed: '+error;}
            }
            </script>
            </body>
            </html>
            """;
        certificate?.Dispose();
        TrySendResponse(ctx, 200, "text/html; charset=utf-8", html);
    }

    private static string BuildRuntimeHealthJson() =>
        JsonSerializer.Serialize(BuildRuntimeHealth(), new JsonSerializerOptions { WriteIndented = true });

    private static void HandleLogDownload(HttpListenerContext ctx)
    {
        if (!File.Exists(Logger.FilePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "Log file not found.");
            return;
        }

        ctx.Response.AddHeader("Content-Disposition", "attachment; filename=remoteplay.log");
        TrySendBytes(ctx, 200, "text/plain; charset=utf-8", File.ReadAllBytes(Logger.FilePath));
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

    private void HandleSetupPage(HttpListenerContext ctx)
    {
        var endpoints = GetLocalIpAddresses()
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => $"{_activeScheme}://{address}:{_config.Port}/")
            .Prepend($"{_activeScheme}://localhost:{_config.Port}/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var localCards = string.Join(Environment.NewLine, endpoints.Select(endpoint => BuildSetupCard(
            endpoint,
            "This instance",
            "Scan from a phone or tablet connected to the same network.",
            "Local")));

        var peers = _broadcaster?.GetPeers()
            .Where(peer => !peer.IsSelf && !string.IsNullOrWhiteSpace(peer.Host) && peer.Port > 0)
            .OrderBy(peer => peer.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        var peerCards = peers.Length == 0
            ? "<p class=\"empty\">No other RemotePlay instances discovered yet. Keep the other instance running on the same LAN and refresh this page.</p>"
            : string.Join(Environment.NewLine, peers.Select(peer =>
            {
                var endpoint = peer.Url.EndsWith("/", StringComparison.Ordinal) ? peer.Url : peer.Url + "/";
                return BuildSetupCard(
                    endpoint,
                    peer.Name,
                    $"Last seen {Math.Max(0, Math.Round((DateTimeOffset.UtcNow - peer.LastSeenUtc).TotalSeconds))} seconds ago.",
                    "Peer");
            }));

        static string BuildSetupCard(string endpoint, string title, string description, string badge) => $$"""
            <article class="card">
              <div class="qr-wrap"><span class="badge">{{HtmlEncode(badge)}}</span>
              <img src="/setup-code.png?url={{Uri.EscapeDataString(endpoint)}}" alt="QR code for {{HtmlEncode(endpoint)}}"/>
              </div><div><h2>{{HtmlEncode(title)}}</h2><p class="endpoint">{{HtmlEncode(endpoint)}}</p><p>{{HtmlEncode(description)}}</p><a href="{{HtmlEncode(endpoint)}}">Open remote</a></div>
            </article>
            """;

        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width,initial-scale=1"/>
            <title>RemotePlay Setup</title>
            <style>
            *{box-sizing:border-box}body{margin:0;min-height:100vh;background:#090d18;color:#eef4ff;font-family:Segoe UI,Arial,sans-serif}.page{width:min(1080px,100%);margin:0 auto;padding:1rem}.hero,.section{background:linear-gradient(135deg,#1a203d,#0c1020);border:1px solid rgba(148,163,184,.2);border-radius:22px;padding:1rem;margin-bottom:1rem}h1,h2{margin:.1rem 0;color:#fff}.section h2{font-size:1.1rem;margin-bottom:.75rem}.muted{color:#9aa8c2}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:1rem}.card{display:grid;grid-template-columns:112px minmax(0,1fr);gap:.9rem;align-items:center;background:#171c35;border:1px solid rgba(148,163,184,.18);border-radius:18px;padding:.85rem}.qr-wrap{position:relative}.badge{position:absolute;left:.25rem;top:.25rem;background:#e94560;color:#fff;border-radius:999px;padding:.12rem .4rem;font-size:.62rem;font-weight:900}.card img{width:112px;height:112px;background:#fff;border-radius:10px;padding:.35rem}.card h2{font-size:.98rem;word-break:break-word;margin:0 0 .25rem}.card p{color:#9aa8c2;margin:.2rem 0 .6rem}.card .endpoint{font-family:Consolas,ui-monospace,monospace;color:#cfe0ff;word-break:break-all}.card a{display:inline-flex;background:#e94560;color:#fff;text-decoration:none;border-radius:999px;padding:.48rem .75rem;font-weight:800}.actions{display:flex;gap:.6rem;flex-wrap:wrap;margin-top:.75rem}.actions a{color:#00d4aa}.empty{color:#9aa8c2;background:#171c35;border:1px dashed rgba(148,163,184,.25);border-radius:14px;padding:1rem}
            </style>
            </head>
            <body><main class="page"><section class="hero"><h1>RemotePlay setup</h1><p class="muted">Use these QR codes to connect devices. IPv6 addresses are hidden here because most mobile scanners and browsers handle LAN IPv4 links more reliably.</p><div class="actions"><a href="/">Open remote here</a><a href="/health">Health dashboard</a></div></section><section class="section"><h2>This RemotePlay instance</h2><div class="grid">{{localCards}}</div></section><section class="section"><h2>Other discovered instances</h2><div class="grid">{{peerCards}}</div></section></main></body></html>
            """;

        TrySendResponse(ctx, 200, "text/html; charset=utf-8", html);
    }

    private void HandleSearch(HttpListenerContext ctx)
    {
        var query = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        if (_libraryIndex.Length == 0)
            StartLibraryIndexRefresh(force: false);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resumeMap = _playbackHistory.GetProgressMap();
        var naturalComparer = _naturalComparer;

        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);

        var matchingFiles = terms.Length == 0
            ? []
            : _libraryIndex
                .Where(f => terms.All(t => f.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Name)
                .ToArray();

        var files = matchingFiles
            .Skip(offset)
            .Take(limit)
            .Select(f => BuildCardFile(Path.GetFileNameWithoutExtension(f.FilePath), f.FilePath, resumeMap, f.FolderName, IsFavorite(f.FilePath)))
            .ToArray<object>();

        // Find unique folders that match the search terms
        var folders = terms.Length == 0
            ? Array.Empty<object>()
            : _libraryIndex
                .Where(f => terms.All(t => f.FolderName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(f => f.FolderName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, naturalComparer)
                .Select(g => new
                {
                    name = g.Key,
                    folder = g.Key
                })
                .ToArray<object>();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            folders,
            files,
            total = folders.Length + files.Length,
            totalFiles = matchingFiles.Length,
            offset,
            limit,
            hasMoreFiles = offset + files.Length < matchingFiles.Length,
            indexedFiles = _libraryIndex.Length,
            indexing = _isIndexing,
            lastRefreshUtc = _lastIndexRefreshUtc
        }));
    }

    private void HandleRecent(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var files = _playbackHistory.GetRecent(_config.PlaybackHistoryLimit)
            .Where(item => WebPathHelpers.IsUnderRoot(item.FilePath, root))
            .Select(item => new
            {
                name = Path.GetFileNameWithoutExtension(item.FilePath),
                displayName = CleanDisplayTitle(Path.GetFileNameWithoutExtension(item.FilePath)),
                path = WebPathHelpers.EncodePath(item.FilePath),
                favorite = IsFavorite(item.FilePath),
                position = Math.Round(item.PositionSeconds, 1),
                duration = Math.Round(item.DurationSeconds, 1),
                progress = item.DurationSeconds > 0 ? Math.Round(item.PositionSeconds / item.DurationSeconds, 3) : 0,
                resume = FormatTime(item.PositionSeconds),
                folder = Path.GetFileName(Path.GetDirectoryName(item.FilePath)) ?? string.Empty
            })
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { files }));
    }

    private void HandleFavorites(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var resumeMap = _playbackHistory.GetProgressMap();
        var files = GetFavoritePaths()
            .Where(path => WebPathHelpers.IsUnderRoot(path, root) && File.Exists(path))
            .OrderBy(path => Path.GetFileNameWithoutExtension(path), _naturalComparer)
            .Select(path => BuildCardFile(Path.GetFileNameWithoutExtension(path), path, resumeMap, Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty, isFavorite: true))
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { files }));
    }

    private void HandleFavorite(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!WebPathHelpers.IsUnderRoot(filePath, _config.ResolvedMoviesPath) || !File.Exists(filePath))
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }

        var favorite = bool.TryParse(ctx.Request.QueryString["value"], out var parsed)
            ? parsed
            : !IsFavorite(filePath);
        SetFavorite(filePath, favorite);
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, favorite }));
    }

    private void HandleHistoryClear(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!WebPathHelpers.IsUnderRoot(filePath, _config.ResolvedMoviesPath))
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }

        _callbacks.ClearPlaybackHistory(filePath);
        TrySendResponse(ctx, 200, "text/plain", "OK");
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

        var naturalComparer = _naturalComparer;
        var folders = Directory.EnumerateDirectories(targetDir)
            .Where(d => !_hiddenFolderNames.Contains(Path.GetFileName(d)))
            .OrderBy(d => Path.GetFileName(d), naturalComparer)
            .Select(d => new
            {
                name = Path.GetFileName(d),
                dir = WebPathHelpers.EncodePath(d)
            })
            .ToArray();

        var resumeMap = _playbackHistory.GetProgressMap();
        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);
        var matchingFiles = Directory.EnumerateFiles(targetDir)
            .Where(f => WebPathHelpers.IsVideoFile(f, _videoExtensions))
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), naturalComparer)
            .ToArray();

        var files = matchingFiles
            .Skip(offset)
            .Take(limit)
            .Select(f => BuildCardFile(Path.GetFileNameWithoutExtension(f), f, resumeMap, isFavorite: IsFavorite(f)))
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
            isRoot = string.Equals(targetDir, root, StringComparison.OrdinalIgnoreCase),
            totalFiles = matchingFiles.Length,
            offset,
            limit,
            hasMoreFiles = offset + files.Length < matchingFiles.Length
        });

        TrySendResponse(ctx, 200, "application/json", result);
    }

    private static int ReadNonNegativeInt(string? value)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;
    }

    private static int ReadPositiveInt(string? value, int defaultValue, int maxValue)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            return defaultValue;

        return Math.Min(parsed, maxValue);
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

        _callbacks.Play(filePath);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleQueueAdd(HttpListenerContext ctx)
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

        _callbacks.Enqueue(filePath);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleQueueRemove(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        _callbacks.RemoveFromQueue(WebPathHelpers.DecodePath(encodedPath));
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleQueueMove(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        var directionParam = ctx.Request.QueryString["direction"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var direction = string.Equals(directionParam, "up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        _callbacks.MoveQueueItem(WebPathHelpers.DecodePath(encodedPath), direction);
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
            {
                Logger.Info($"Library index refresh skipped; already indexing (force={force})");
                return;
            }

            if (!force && HasFreshLibraryIndex())
            {
                Logger.Info($"Library index refresh skipped; cache is fresh ({_libraryIndex.Length} videos)");
                return;
            }

            _isIndexing = true;
            _scanStartedUtc = DateTimeOffset.UtcNow;
            _scannedFiles = 0;
            _scannedFolders = 0;
            _lastScanError = string.Empty;
            Logger.Info($"Library index refresh starting (force={force}, cached={_libraryIndex.Length})");
        }

        Task.Run(() =>
        {
            try
            {
                var root = _config.ResolvedMoviesPath;
                if (!Directory.Exists(root))
                {
                    _libraryIndex = [];
                    _lastIndexRefreshUtc = DateTimeOffset.UtcNow;
                    return;
                }

                var files = EnumerateLibraryVideoFiles(root, _hiddenFolderNames, () => Interlocked.Increment(ref _scannedFolders))
                    .Where(f => WebPathHelpers.IsVideoFile(f, _videoExtensions))
                    .Select(f =>
                    {
                        Interlocked.Increment(ref _scannedFiles);
                        return f;
                    })
                    .Select(f => BuildLibraryFile(root, f))
                    .ToArray();

                _libraryIndex = files;
                _lastIndexRefreshUtc = DateTimeOffset.UtcNow;
                SaveLibraryIndexCache();
                Logger.Info($"Library index refreshed: {files.Length} videos");
            }
            catch (Exception ex)
            {
                _lastScanError = ex.Message;
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

    private static LibraryFile BuildLibraryFile(string root, string filePath)
    {
        long sizeBytes = 0;
        DateTime lastWriteUtc = default;
        try
        {
            var info = new FileInfo(filePath);
            sizeBytes = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
        }
        catch
        {
        }

        return new LibraryFile(
            Path.GetFileNameWithoutExtension(filePath),
            filePath,
            WebPathHelpers.EncodePath(filePath),
            Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty,
            BuildSearchText(root, filePath),
            sizeBytes,
            lastWriteUtc);
    }

    private static IEnumerable<string> EnumerateLibraryVideoFiles(string root, IReadOnlySet<string> ignoredFolderNames, Action? onFolderScanned = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            onFolderScanned?.Invoke();

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
                if (!ignoredFolderNames.Contains(Path.GetFileName(subdir)))
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

    private static object BuildCardFile(string name, string filePath, IReadOnlyDictionary<string, RecentPlaybackItem> resumeMap, string folder = "", bool isFavorite = false)
    {
        resumeMap.TryGetValue(filePath, out var resume);
        return new
        {
            name,
            displayName = CleanDisplayTitle(name),
            path = WebPathHelpers.EncodePath(filePath),
            folder,
            favorite = isFavorite,
            position = resume is null ? 0 : Math.Round(resume.PositionSeconds, 1),
            duration = resume is null ? 0 : Math.Round(resume.DurationSeconds, 1),
            progress = resume is null || resume.DurationSeconds <= 0 ? 0 : Math.Round(resume.PositionSeconds / resume.DurationSeconds, 3),
            resume = resume is null ? string.Empty : FormatTime(resume.PositionSeconds)
        };
    }

    private static string CleanDisplayTitle(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var cleaned = name.Replace('.', ' ').Replace('_', ' ');
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\b(19|20)\d{2}\b", string.Empty);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\b(1080p|720p|2160p|4k|x264|x265|h264|h265|web[- ]?dl|webrip|bluray|brrip|dvdrip|hdrip|aac|dts)\b", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '-', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? name : cleaned;
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private static object[] GetNaturalSortKey(string path)
    {
        var name = Path.GetFileName(path);
        var parts = System.Text.RegularExpressions.Regex.Split(name, @"(\d+)");
        var result = new List<object>();

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;

            if (int.TryParse(part, out var num))
                result.Add(num);
            else
                result.Add(part);
        }

        return result.ToArray();
    }

    private static readonly NaturalStringComparer _naturalComparer = new();

    private class NaturalStringComparer : IComparer<string>
    {
        private static readonly System.Text.RegularExpressions.Regex DigitSplitRegex =
            new(@"(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

        public int Compare(string? x, string? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var xParts = DigitSplitRegex.Split(x);
            var yParts = DigitSplitRegex.Split(y);

            var maxLen = Math.Max(xParts.Length, yParts.Length);
            for (int i = 0; i < maxLen; i++)
            {
                var xPart = i < xParts.Length ? xParts[i] : string.Empty;
                var yPart = i < yParts.Length ? yParts[i] : string.Empty;

                if (string.IsNullOrEmpty(xPart) && string.IsNullOrEmpty(yPart))
                    continue;
                if (string.IsNullOrEmpty(xPart))
                    return -1;
                if (string.IsNullOrEmpty(yPart))
                    return 1;

                if (int.TryParse(xPart, out var xNum) && int.TryParse(yPart, out var yNum))
                {
                    var numCmp = xNum.CompareTo(yNum);
                    if (numCmp != 0) return numCmp;
                }
                else
                {
                    var strCmp = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
                    if (strCmp != 0) return strCmp;
                }
            }

            return 0;
        }
    }

    private void HandlePeers(HttpListenerContext ctx)
    {
        if (_broadcaster is null)
        {
            TrySendResponse(ctx, 200, "application/json", "[]");
            return;
        }

        var peers = _broadcaster.GetPeers().Select(p => new
        {
            name = p.Name,
            scheme = p.Scheme,
            host = p.Host,
            port = p.Port,
            url = p.Url,
            isSelf = p.IsSelf
        });

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(peers));
    }

}

