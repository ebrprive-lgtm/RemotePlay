using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using RemotePlay.Services;

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
                _ = Task.Run(() => HandleRequestSafeAsync(ctx));
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

    private async Task HandleRequestSafeAsync(HttpListenerContext ctx)
    {
        try { await HandleRequestAsync(ctx).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Logger.Error("Error handling request", ex);
            TrySendResponse(ctx, 500, "text/plain", "Internal server error");
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var urlPath = req.Url?.AbsolutePath ?? "/";
        _lastRequestUtc = DateTimeOffset.UtcNow;

        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

        switch (urlPath)
        {
            case "/" or "/index.html":
                ctx.Response.AddHeader("Cache-Control", "no-cache");
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

            case "/world-110m.json":
                ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");
                TrySendResponse(ctx, 200, "application/json; charset=utf-8", GetWorld110m());
                break;

            case "/us-states.json":
                ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");
                TrySendResponse(ctx, 200, "application/json; charset=utf-8", GetUsStates());
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

            case "/api/delete-link":
                HandleDeleteLink(ctx);
                break;

            case "/api/history/clear":
                HandleHistoryClear(ctx);
                break;

            case "/api/history/watched/set":
                HandleHistoryWatchedSet(ctx);
                break;

            case "/api/play":
                HandlePlay(ctx);
                break;

            case "/api/fix-audio":
                _callbacks.FixAudio();
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
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

            case "/api/music/browse":
                HandleMusicBrowse(ctx);
                break;

            case "/api/music/search":
                HandleMusicSearch(ctx);
                break;

            case "/api/music/rescan":
                StartMusicIndexRefresh();
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true }));
                break;

            case "/api/music/status":
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(GetMusicScanStatus()));
                break;

            case "/api/music/play":
                HandleMusicPlay(ctx);
                break;

            case "/api/music/pause":
                _callbacks.PauseMusic();
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                break;

            case "/api/music/stop":
                _callbacks.StopMusic();
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                break;

            case "/api/music/seek":
                HandleMusicSeek(ctx);
                break;

            case "/api/music/volume":
                HandleMusicVolume(ctx);
                break;

            // ── Radio ─────────────────────────────────────────────────────
            case "/api/radio/search":
                await HandleRadioSearchAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/radio/top":
                await HandleRadioTopAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/radio/tags":
                await HandleRadioTagsAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/radio/countries":
                await HandleRadioCountriesAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/radio/play":
                HandleRadioPlay(ctx);
                break;

            case "/api/radio/stop":
                _callbacks.RadioStop();
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                break;

            case "/api/radio/volume":
                HandleRadioVolume(ctx);
                break;

            case "/api/radio/playback-status":
                TrySendResponse(ctx, 200, "application/json",
                    JsonSerializer.Serialize(_callbacks.RadioGetStatus()));
                break;

            case "/api/radio/favorites":
                HandleRadioFavorites(ctx);
                break;

            case "/api/radio/favorite":
                HandleRadioFavoriteToggle(ctx);
                break;

            case "/api/radio/notify-alive":
                _callbacks.RadioNotifyAlive();
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                break;

            case "/api/radio/resolve":
                await HandleRadioResolveAsync(ctx).ConfigureAwait(false);
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

    private static readonly JsonSerializerOptions _camelCaseJson =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void HandleDisplayDiagnostics(HttpListenerContext ctx)
    {
        var diagnostics = _callbacks.GetDisplayDiagnostics();
        var json = JsonSerializer.Serialize(diagnostics, _camelCaseJson);
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
                <section class="card"><h2>Media codec info <span class="pill {{{{(displayDiagnostics.CodecInfo is not null ? "ok" : "warn")}}}}">{{{{(displayDiagnostics.CodecInfo is not null ? "CAPTURED" : "NO DATA")}}}}</span></h2>
                  {{{{(displayDiagnostics.CodecInfo is null
                    ? "<p style=\"color:var(--muted);font-size:.88rem\">Codec information is captured when a video starts playing. Start a movie and refresh this page.</p>"
                    : $"""
                      <dl class="grid">
                        <div class="metric wide"><dt>&#127902; File</dt><dd class="mono">{HtmlEncode(displayDiagnostics.CodecInfo.FileName)}</dd></div>
                        <div class="metric"><dt>Container</dt><dd>{HtmlEncode(displayDiagnostics.CodecInfo.ContainerFormat)}</dd></div>
                        <div class="metric"><dt>Total tracks</dt><dd>{displayDiagnostics.CodecInfo.TotalTracks}</dd></div>
                        <div class="metric"><dt>&#128250; Video tracks</dt><dd>{displayDiagnostics.CodecInfo.VideoTracks.Length}</dd></div>
                        <div class="metric"><dt>&#127925; Audio tracks</dt><dd>{displayDiagnostics.CodecInfo.AudioTracks.Length}</dd></div>
                        <div class="metric"><dt>&#128221; Subtitle tracks</dt><dd>{displayDiagnostics.CodecInfo.SubtitleTracks.Length}</dd></div>
                        <div class="metric"><dt>Captured UTC</dt><dd>{HtmlEncode(displayDiagnostics.CodecInfo.CapturedAtUtc)}</dd></div>
                      </dl>
                      {string.Join("", displayDiagnostics.CodecInfo.VideoTracks.Select((t, i) => $"""
                        <h3 style="margin:.8rem 0 .3rem;font-size:.85rem;text-transform:uppercase;letter-spacing:.04em;color:var(--accent)">&#128250; Video track {i + 1}</h3>
                        <dl class="grid">
                          <div class="metric"><dt>Codec</dt><dd><strong>{HtmlEncode(t.CodecDescription)}</strong> <span style="color:var(--muted);font-size:.8rem">({HtmlEncode(t.Codec)})</span></dd></div>
                          <div class="metric"><dt>Resolution</dt><dd>{t.Width}&times;{t.Height}</dd></div>
                          <div class="metric"><dt>Frame rate</dt><dd>{HtmlEncode(t.FrameRate)}</dd></div>
                          <div class="metric"><dt>Aspect ratio (SAR)</dt><dd>{HtmlEncode(t.AspectRatio)}</dd></div>
                          <div class="metric"><dt>Orientation</dt><dd>{HtmlEncode(t.Orientation)}</dd></div>
                          {(string.IsNullOrEmpty(t.Language) ? "" : $"<div class=\"metric\"><dt>Language</dt><dd>{HtmlEncode(t.Language)}</dd></div>")}
                          {(string.IsNullOrEmpty(t.Description) ? "" : $"<div class=\"metric\"><dt>Description</dt><dd>{HtmlEncode(t.Description)}</dd></div>")}
                        </dl>
                      """))}
                      {string.Join("", displayDiagnostics.CodecInfo.AudioTracks.Select((t, i) => $"""
                        <h3 style="margin:.8rem 0 .3rem;font-size:.85rem;text-transform:uppercase;letter-spacing:.04em;color:var(--accent)">&#127925; Audio track {i + 1}</h3>
                        <dl class="grid">
                          <div class="metric"><dt>Codec</dt><dd><strong>{HtmlEncode(t.CodecDescription)}</strong> <span style="color:var(--muted);font-size:.8rem">({HtmlEncode(t.Codec)})</span></dd></div>
                          <div class="metric"><dt>Channels</dt><dd>{HtmlEncode(t.ChannelLayout)}</dd></div>
                          <div class="metric"><dt>Sample rate</dt><dd>{t.SampleRate} Hz</dd></div>
                          {(string.IsNullOrEmpty(t.Language) ? "" : $"<div class=\"metric\"><dt>Language</dt><dd>{HtmlEncode(t.Language)}</dd></div>")}
                          {(string.IsNullOrEmpty(t.Description) ? "" : $"<div class=\"metric\"><dt>Description</dt><dd>{HtmlEncode(t.Description)}</dd></div>")}
                        </dl>
                      """))}
                      {(displayDiagnostics.CodecInfo.SubtitleTracks.Length == 0 ? "" : string.Join("", displayDiagnostics.CodecInfo.SubtitleTracks.Select((t, i) => $"""
                        <h3 style="margin:.8rem 0 .3rem;font-size:.85rem;text-transform:uppercase;letter-spacing:.04em;color:var(--accent)">&#128221; Subtitle track {i + 1}</h3>
                        <dl class="grid">
                          <div class="metric"><dt>Codec</dt><dd><strong>{HtmlEncode(t.CodecDescription)}</strong> <span style="color:var(--muted);font-size:.8rem">({HtmlEncode(t.Codec)})</span></dd></div>
                          {(string.IsNullOrEmpty(t.Language) ? "" : $"<div class=\"metric\"><dt>Language</dt><dd>{HtmlEncode(t.Language)}</dd></div>")}
                          {(string.IsNullOrEmpty(t.Description) ? "" : $"<div class=\"metric\"><dt>Description</dt><dd>{HtmlEncode(t.Description)}</dd></div>")}
                          {(string.IsNullOrEmpty(t.Encoding) ? "" : $"<div class=\"metric\"><dt>Encoding</dt><dd>{HtmlEncode(t.Encoding)}</dd></div>")}
                        </dl>
                      """)))}
                      """
                  )}}}}</section>
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

    private void HandleMusicBrowse(HttpListenerContext ctx)
    {
        if (_musicIndex.Length == 0 && !_isMusicIndexing)
            StartMusicIndexRefresh();

        var folderParam = (ctx.Request.QueryString["folder"] ?? string.Empty).Trim();

        // If the user navigates to a specific folder while a scan is running,
        // promote that folder to the front of the scan queue so its tracks
        // are indexed immediately.
        if (!string.IsNullOrEmpty(folderParam) && _isMusicIndexing)
        {
            MusicScanJob? activeJob;
            lock (_musicIndexGate)
                activeJob = _activeMusicScanJob;
            activeJob?.Prioritize(folderParam);
        }
        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);

        var allTracks = _musicIndex;
        var musicRoot = _config.ResolvedMusicPath;

        // Files are only returned when a specific subfolder path is requested,
        // and only direct children of that folder (not nested subdirectories).
        var filteredAll = string.IsNullOrEmpty(folderParam)
            ? []
            : allTracks.Where(f =>
                string.Equals(Path.GetDirectoryName(f.FullPath), folderParam, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var page = filteredAll.Skip(offset).Take(limit).ToArray();

        // At root: show direct subdirectories of the music root.
        // Inside a subfolder: no nested folders shown (flat file list).
        object[] folders;
        if (string.IsNullOrEmpty(folderParam))
        {
            try
            {
                folders = Directory.Exists(musicRoot)
                    ? Directory.GetDirectories(musicRoot)
                        .OrderBy(d => d, _naturalComparer)
                        .Select(d => (object)new { name = Path.GetFileName(d), folder = d })
                        .ToArray()
                    : [];
            }
            catch
            {
                folders = [];
            }
        }
        else
        {
            // Show direct sub-directories of the requested folder
            try
            {
                folders = Directory.Exists(folderParam)
                    ? Directory.GetDirectories(folderParam)
                        .OrderBy(d => d, _naturalComparer)
                        .Select(d => (object)new { name = Path.GetFileName(d), folder = d })
                        .ToArray()
                    : [];
            }
            catch
            {
                folders = [];
            }
        }

        var files = page
            .Select(f => new
            {
                name = f.Name,
                path = WebPathHelpers.EncodePath(f.FullPath),
                folder = Path.GetFileName(Path.GetDirectoryName(f.FullPath)) ?? string.Empty
            })
            .ToArray<object>();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            folders,
            files,
            total = files.Length,
            totalInFolder = filteredAll.Length,
            offset,
            limit,
            hasMore = offset + files.Length < filteredAll.Length,
            indexedFiles = allTracks.Length,
            indexing = _isMusicIndexing,
            lastError = _lastMusicScanError,
            lastRefreshUtc = _lastMusicIndexRefreshUtc,
            musicRoot = musicRoot,
            folder = string.IsNullOrEmpty(folderParam) ? musicRoot : folderParam
        }));
    }

    private void HandleMusicSearch(HttpListenerContext ctx)
    {
        var query = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        if (_musicIndex.Length == 0 && !_isMusicIndexing)
            StartMusicIndexRefresh();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);

        var matching = terms.Length == 0
            ? []
            : _musicIndex
                .Where(f => terms.All(t =>
                    f.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    (Path.GetFileName(Path.GetDirectoryName(f.FullPath)) ?? string.Empty).Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Name)
                .ToArray();

        var files = matching
            .Skip(offset)
            .Take(limit)
            .Select(f => new
            {
                name = f.Name,
                path = WebPathHelpers.EncodePath(f.FullPath),
                folder = Path.GetFileName(Path.GetDirectoryName(f.FullPath)) ?? string.Empty
            })
            .ToArray<object>();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            files,
            total = matching.Length,
            offset,
            limit,
            hasMore = offset + files.Length < matching.Length,
            indexedFiles = _musicIndex.Length,
            indexing = _isMusicIndexing
        }));
    }

    private object GetMusicScanStatus()
    {
        var pb = _callbacks.GetMusicStatus();
        return new
        {
            sessionId     = _serverSessionId,
            isScanning    = _isMusicIndexing,
            indexedFiles  = _isMusicIndexing ? _musicScanProgress : _musicIndex.Length,
            currentScanFolder = _musicScanFolder,
            musicRoot     = _config.ResolvedMusicPath,
            lastRefreshUtc = _lastMusicIndexRefreshUtc,
            lastError     = _lastMusicScanError,
            // playback fields
            isPlaying     = pb.IsPlaying,
            isPaused      = pb.IsPaused,
            currentPath   = pb.CurrentPath,
            title         = pb.Title,
            position      = pb.Position,
            duration      = pb.Duration,
            playbackError = pb.LastError
        };
    }

    private void HandleMusicPlay(HttpListenerContext ctx)
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
        _callbacks.Stop();
        _callbacks.RadioStop();
        _callbacks.PlayMusic(filePath);
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
    }

    private void HandleMusicSeek(HttpListenerContext ctx)
    {
        if (!double.TryParse(ctx.Request.QueryString["pos"],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pos))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing pos");
            return;
        }
        _callbacks.SeekMusic(pos);
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
    }

    private void HandleMusicVolume(HttpListenerContext ctx)
    {
        if (!double.TryParse(ctx.Request.QueryString["v"],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var vol))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing v");
            return;
        }
        _callbacks.SetMusicVolume(Math.Clamp(vol, 0, 1));
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
    }

    private void HandleSearch(HttpListenerContext ctx)
    {
        var query = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        if (_libraryIndex.Length == 0)
            StartLibraryIndexRefresh(force: false);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var history = GetHistoryForIp(GetClientIp(ctx));
        var resumeMap = history.GetProgressMap();
        var watchedSet = history.GetWatchedSet();
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
            .Select(f => BuildCardFile(f.Name, f.FilePath, resumeMap, f.FolderName, IsFavorite(f.FilePath), f.IsLink, f.LinkSourcePath, watchedSet))
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
        var files = GetHistoryForIp(GetClientIp(ctx)).GetRecent(_config.PlaybackHistoryLimit)
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
        var history = GetHistoryForIp(GetClientIp(ctx));
        var resumeMap = history.GetProgressMap();
        var watchedSet = history.GetWatchedSet();
        var files = GetFavoritePaths()
            .Where(path => WebPathHelpers.IsUnderRoot(path, root) && File.Exists(path))
            .OrderBy(path => Path.GetFileNameWithoutExtension(path), _naturalComparer)
            .Select(path => BuildCardFile(Path.GetFileNameWithoutExtension(path), path, resumeMap, Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty, isFavorite: true, watchedSet: watchedSet))
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

    private void HandleHistoryWatchedSet(HttpListenerContext ctx)
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

        var watchedParam = ctx.Request.QueryString["watched"] ?? "true";
        var watched = !string.Equals(watchedParam, "false", StringComparison.OrdinalIgnoreCase);
        GetHistoryForIp(GetClientIp(ctx)).MarkWatched(filePath, watched);
        TrySendResponse(ctx, 200, "text/plain", "OK");
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

        GetHistoryForIp(GetClientIp(ctx)).Clear(filePath);
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    /// <summary>Deletes a <c>.rplink</c> file.
    /// path of the <c>.rplink</c> file itself (returned as <c>linkPath</c> from browse/search).</summary>
    private void HandleDeleteLink(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }

        var rplinkPath = WebPathHelpers.DecodePath(encodedPath);
        if (!RplinkHelper.IsRplinkFile(rplinkPath)
            || !WebPathHelpers.IsUnderRoot(rplinkPath, _config.ResolvedMoviesPath))
        {
            TrySendResponse(ctx, 403, "text/plain", "Forbidden");
            return;
        }

        try
        {
            File.Delete(rplinkPath);
            StartLibraryIndexRefresh(force: true);
            TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true }));
        }
        catch (Exception ex)
        {
            Logger.Error("HandleDeleteLink", "Failed to delete .rplink file", ex);
            TrySendResponse(ctx, 500, "text/plain", "Failed to delete link: " + ex.Message);
        }
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

        // Regular sub-folders
        var regularFolders = Directory.EnumerateDirectories(targetDir)
            .Where(d => !_hiddenFolderNames.Contains(Path.GetFileName(d)))
            .Select(d => (name: Path.GetFileName(d), dir: WebPathHelpers.EncodePath(d), isLink: false));

        // Folder-link .rplink entries (target is a directory)
        var folderLinkFolders = Directory.EnumerateFiles(targetDir, "*" + RplinkHelper.Extension)
            .Where(RplinkHelper.IsTargetFolder)
            .Select(f =>
            {
                var target = RplinkHelper.TryReadTarget(f);
                return (name: Path.GetFileNameWithoutExtension(f),
                        dir: target is not null ? WebPathHelpers.EncodePath(target) : string.Empty,
                        isLink: true);
            })
            .Where(x => !string.IsNullOrEmpty(x.dir));

        var folders = regularFolders.Concat(folderLinkFolders)
            .OrderBy(f => f.name, naturalComparer)
            .Select(f => new { f.name, f.dir, f.isLink })
            .ToArray();

        var history = GetHistoryForIp(GetClientIp(ctx));
        var resumeMap = history.GetProgressMap();
        var watchedSet = history.GetWatchedSet();
        var offset = ReadNonNegativeInt(ctx.Request.QueryString["offset"]);
        var limit = ReadPositiveInt(ctx.Request.QueryString["limit"], _config.EffectiveLibraryPageSize, 1000);

        // Collect regular video files
        var videoFiles = Directory.EnumerateFiles(targetDir)
            .Where(f => WebPathHelpers.IsVideoFile(f, _videoExtensions))
            .Select(f => (name: Path.GetFileNameWithoutExtension(f), filePath: f, isLink: false, linkSourcePath: (string?)null));

        // Collect resolved .rplink files (file targets only — folder targets appear as folder rows above)
        var linkFiles = Directory.EnumerateFiles(targetDir)
            .Where(RplinkHelper.IsRplinkFile)
            .Where(f => !RplinkHelper.IsTargetFolder(f))
            .Select(f => (name: Path.GetFileNameWithoutExtension(f), target: RplinkHelper.TryReadTarget(f), rplinkPath: f))
            .Where(t => t.target is not null)
            .Select(t => (name: t.name, filePath: t.target!, isLink: true, linkSourcePath: (string?)t.rplinkPath));

        var matchingFiles = videoFiles.Concat(linkFiles)
            .OrderBy(f => f.name, naturalComparer)
            .ToArray();

        var files = matchingFiles
            .Skip(offset)
            .Take(limit)
            .Select(f => BuildCardFile(f.name, f.filePath, resumeMap, isFavorite: IsFavorite(f.filePath), isLink: f.isLink, linkSourcePath: f.linkSourcePath, watchedSet: watchedSet))
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

    private static int ReadNonNegativeInt(string? value) => MediaControlValueParser.ReadNonNegativeInt(value);

    private static int ReadPositiveInt(string? value, int defaultValue, int maxValue) => MediaControlValueParser.ReadPositiveInt(value, defaultValue, maxValue);

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

        _callbacks.StopMusic();
        _callbacks.RadioStop();
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

                var allFiles = EnumerateLibraryVideoFiles(root, _hiddenFolderNames, () => Interlocked.Increment(ref _scannedFolders))
                    .Select(f =>
                    {
                        Interlocked.Increment(ref _scannedFiles);
                        return f;
                    })
                    .ToArray();

                var videoFiles = allFiles
                    .Where(f => WebPathHelpers.IsVideoFile(f, _videoExtensions))
                    .Select(f => BuildLibraryFile(root, f));

                var linkFiles = allFiles
                    .Where(RplinkHelper.IsRplinkFile)
                    .Select(f => BuildLibraryFileForLink(root, f))
                    .Where(f => f is not null)
                    .Select(f => f!);

                // Index videos that live inside folder-linked directories so
                // search, thumbnail generation, and favourites all work on them.
                var folderLinkFiles = allFiles
                    .Where(RplinkHelper.IsRplinkFile)
                    .SelectMany(f =>
                    {
                        var items = BuildLibraryFilesForFolderLink(root, f).ToList();
                        if (items.Count > 0)
                            Interlocked.Add(ref _scannedFiles, items.Count);
                        return items;
                    });

                var files = videoFiles.Concat(linkFiles).Concat(folderLinkFiles)
                    // deduplicate: prefer the link entry over a plain entry for the same real path
                    .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(f => f.IsLink).First())
                    .OrderBy(f => f.SearchText, StringComparer.OrdinalIgnoreCase)
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

    private void StartMusicIndexRefresh()
    {
        CancellationTokenSource cts;
        MusicScanJob job;
        lock (_musicIndexGate)
        {
            if (_isMusicIndexing)
                _musicScanCts?.Cancel();

            _musicScanCts?.Dispose();
            cts = _musicScanCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            job = _activeMusicScanJob = new MusicScanJob(_config.ResolvedMusicPath);
            _isMusicIndexing = true;
            _musicScanProgress = 0;
            _musicScanFolder = string.Empty;
            _lastMusicScanError = string.Empty;
        }

        Task.Run(() =>
        {
            try
            {
                var root = _config.ResolvedMusicPath;

                // Accumulate all files; merge each folder's batch into the live index as it arrives
                var allFiles = new List<MusicFile>();

                var progress = new Progress<int>(count =>
                {
                    lock (_musicIndexGate)
                        _musicScanProgress = count;
                });

                void onFolder(string folder)
                {
                    lock (_musicIndexGate)
                        _musicScanFolder = folder;
                }

                void onFolderComplete(IReadOnlyList<MusicFile> batch)
                {
                    allFiles.AddRange(batch);
                    // Update the live index after every folder so browse requests
                    // immediately see the newly indexed tracks.
                    var snapshot = allFiles.ToArray();
                    Array.Sort(snapshot, (a, b) =>
                        string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
                    lock (_musicIndexGate)
                        _musicIndex = snapshot;
                }

                MusicScanner.Scan(job, _musicExtensions, onFolderComplete, onFolder, progress, cts.Token);

                lock (_musicIndexGate)
                {
                    _musicScanProgress = _musicIndex.Length;
                    _lastMusicIndexRefreshUtc = DateTimeOffset.UtcNow;
                    _activeMusicScanJob = null;
                }
                Logger.Info($"Music index refreshed: {_musicIndex.Length} tracks");
                SaveMusicIndexCache();
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Music scan was cancelled");
                lock (_musicIndexGate)
                    _activeMusicScanJob = null;
            }
            catch (Exception ex)
            {
                lock (_musicIndexGate)
                {
                    _lastMusicScanError = ex.Message;
                    _activeMusicScanJob = null;
                }
                Logger.Error("Music index refresh failed", ex);
            }
            finally
            {
                lock (_musicIndexGate)
                    _isMusicIndexing = false;
            }
        }, cts.Token);
    }

    private static string BuildSearchText(string root, string filePath) =>
        LibraryIndexHelpers.BuildSearchText(root, filePath);

    private static LibraryFile BuildLibraryFile(string root, string filePath) =>
        LibraryIndexHelpers.BuildLibraryFile(root, filePath);

    private static LibraryFile? BuildLibraryFileForLink(string root, string rplinkPath) =>
        LibraryIndexHelpers.BuildLibraryFileForLink(root, rplinkPath);

    /// <summary>Enumerates all video files inside the target directory of a folder-type <c>.rplink</c>.
    /// Each yielded entry is tagged with <see cref="LibraryFile.IsLink"/> and
    /// <see cref="LibraryFile.LinkSourcePath"/> so all index-driven operations
    /// (search, thumbnail queue, favourites) work on the linked content.</summary>
    private IEnumerable<LibraryFile> BuildLibraryFilesForFolderLink(string root, string rplinkPath)
    {
        var targetDir = RplinkHelper.TryReadTarget(rplinkPath);
        if (targetDir is null || !Directory.Exists(targetDir))
            yield break;

        var linkLabel = Path.GetFileNameWithoutExtension(rplinkPath);

        foreach (var file in LibraryIndexHelpers.EnumerateLibraryVideoFiles(targetDir, _hiddenFolderNames))
        {
            if (!WebPathHelpers.IsVideoFile(file, _videoExtensions))
                continue;

            long sizeBytes = 0;
            DateTime lastWriteUtc = default;
            try
            {
                var info = new FileInfo(file);
                sizeBytes = info.Length;
                lastWriteUtc = info.LastWriteTimeUtc;
            }
            catch { }

            var relativeInTarget = Path.GetRelativePath(targetDir, file)
                .Replace(Path.DirectorySeparatorChar, ' ')
                .Replace(Path.AltDirectorySeparatorChar, ' ');
            var searchText = $"{linkLabel} {relativeInTarget}";

            yield return new LibraryFile(
                Path.GetFileNameWithoutExtension(file),
                file,
                WebPathHelpers.EncodePath(file),
                Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty,
                searchText,
                sizeBytes,
                lastWriteUtc,
                IsLink: true,
                LinkSourcePath: rplinkPath);
        }
    }

    private static IEnumerable<string> EnumerateLibraryVideoFiles(string root, IReadOnlySet<string> ignoredFolderNames, Action? onFolderScanned = null) =>
        LibraryIndexHelpers.EnumerateLibraryVideoFiles(root, ignoredFolderNames, onFolderScanned);

    private static object[] BuildBreadcrumbs(string root, string targetDir) =>
        LibraryIndexHelpers.BuildBreadcrumbs(root, targetDir);

    private static object BuildCardFile(string name, string filePath, IReadOnlyDictionary<string, RecentPlaybackItem> resumeMap, string folder = "", bool isFavorite = false, bool isLink = false, string? linkSourcePath = null, IReadOnlySet<string>? watchedSet = null)
    {
        resumeMap.TryGetValue(filePath, out var resume);
        var progress = resume is null || resume.DurationSeconds <= 0 ? 0 : Math.Round(resume.PositionSeconds / resume.DurationSeconds, 3);
        var watched = (watchedSet?.Contains(filePath) ?? false) || progress >= 0.95;
        return new
        {
            name,
            displayName = CleanDisplayTitle(name),
            path = WebPathHelpers.EncodePath(filePath),
            folder,
            favorite = isFavorite,
            position = resume is null ? 0 : Math.Round(resume.PositionSeconds, 1),
            duration = resume is null ? 0 : Math.Round(resume.DurationSeconds, 1),
            progress,
            resume = resume is null ? string.Empty : FormatTime(resume.PositionSeconds),
            watched,
            isLink,
            linkPath = isLink && linkSourcePath is not null ? WebPathHelpers.EncodePath(linkSourcePath) : null
        };
    }

    private static string CleanDisplayTitle(string name) => DisplayFormatHelpers.CleanDisplayTitle(name);

    private static string FormatTime(double seconds) => DisplayFormatHelpers.FormatTime(seconds);

    private static readonly NaturalStringComparer _naturalComparer = new();

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

    /// <summary>Extracts the client's IP address as a plain string, stripping the IPv4-mapped
    /// IPv6 prefix (<c>::ffff:</c>) so IPv4 clients always resolve to their dotted-decimal address.</summary>
    private static string GetClientIp(HttpListenerContext ctx)
    {
        var addr = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? string.Empty;
        if (addr.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
            addr = addr["::ffff:".Length..];
        return addr;
    }

    // ── Radio handlers ───────────────────────────────────────────────────────

    private async Task HandleRadioSearchAsync(HttpListenerContext ctx)
    {
        var q       = ctx.Request.QueryString["q"]       ?? string.Empty;
        var country = ctx.Request.QueryString["country"] ?? string.Empty;
        var tag     = ctx.Request.QueryString["tag"]     ?? string.Empty;
        _ = int.TryParse(ctx.Request.QueryString["limit"]  ?? "40", out var limit);
        _ = int.TryParse(ctx.Request.QueryString["offset"] ?? "0",  out var offset);
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);

        var stations = await _callbacks.RadioSearch(q, country, tag, limit, offset).ConfigureAwait(false);
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(stations));
    }

    private async Task HandleRadioTopAsync(HttpListenerContext ctx)
    {
        _ = int.TryParse(ctx.Request.QueryString["limit"]  ?? "40", out var limit);
        _ = int.TryParse(ctx.Request.QueryString["offset"] ?? "0",  out var offset);
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);
        var stations = await _callbacks.RadioTopStations(limit, offset).ConfigureAwait(false);
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(stations));
    }

    private async Task HandleRadioTagsAsync(HttpListenerContext ctx)
    {
        var countryCode = (ctx.Request.QueryString["country"] ?? string.Empty).Trim();
        var tags = await _callbacks.RadioGetTags(countryCode).ConfigureAwait(false);
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(tags));
    }

    private async Task HandleRadioCountriesAsync(HttpListenerContext ctx)
    {
        var countries = (await _callbacks.RadioGetCountries().ConfigureAwait(false))
            .Select(c => new { code = c.Code, name = c.Name });
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(countries));
    }

    private void HandleRadioPlay(HttpListenerContext ctx)
    {
        var url  = ctx.Request.QueryString["url"]  ?? string.Empty;
        var name = ctx.Request.QueryString["name"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"missing url\"}");
            return;
        }
        _callbacks.Stop();
        _callbacks.StopMusic();
        _callbacks.RadioPlay(url, name);
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
    }

    private void HandleRadioVolume(HttpListenerContext ctx)
    {
        var raw = ctx.Request.QueryString["v"] ?? "0.8";
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var vol))
            vol = 0.8;
        _callbacks.RadioSetVolume(Math.Clamp(vol, 0.0, 1.0));
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
    }

    private void HandleRadioFavorites(HttpListenerContext ctx)
    {
        var favs = _callbacks.RadioGetFavorites();
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(favs));
    }

    private void HandleRadioFavoriteToggle(HttpListenerContext ctx)
    {
        try
        {
            using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
            var body = sr.ReadToEnd();
            var station = System.Text.Json.JsonSerializer.Deserialize<RadioStation>(body);
            if (station is null)
            {
                TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid body\"}");
                return;
            }
            _callbacks.RadioToggleFavorite(station);
            var isFav = _callbacks.RadioIsFavorite(station.Uuid);
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { ok = true, isFavorite = isFav }));
        }
        catch (Exception ex)
        {
            TrySendResponse(ctx, 500, "application/json",
                JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    /// <summary>
    /// Resolves a Radio Browser station UUID to its stream URL via the click endpoint,
    /// which also registers a listen-count with Radio Browser.
    /// Returns { resolvedUrl } or falls back to the original url if resolution fails.
    /// </summary>
    private async Task HandleRadioResolveAsync(HttpListenerContext ctx)
    {
        var uuid = ctx.Request.QueryString["uuid"] ?? string.Empty;
        var fallbackUrl = ctx.Request.QueryString["url"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(uuid))
        {
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { resolvedUrl = fallbackUrl }));
            return;
        }
        var resolvedUrl = await _callbacks.RadioResolveUrl(uuid, fallbackUrl).ConfigureAwait(false);
        TrySendResponse(ctx, 200, "application/json",
            JsonSerializer.Serialize(new { resolvedUrl }));
    }

}

