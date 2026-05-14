using System.Collections.Generic;
using System.IO;
using System.Net;
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

        Logger.Info($"{req.HttpMethod} {urlPath}");

        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

        switch (urlPath)
        {
            case "/" or "/index.html":
                TrySendResponse(ctx, 200, "text/html; charset=utf-8", GetHtmlPage());
                break;

            case "/manifest.webmanifest":
                TrySendResponse(ctx, 200, "application/manifest+json; charset=utf-8", ManifestJson);
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

            case "/api/recent":
                HandleRecent(ctx);
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
                path = WebPathHelpers.EncodePath(q.Path),
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

    private void HandleHealth(HttpListenerContext ctx)
    {
        var status = _callbacks.GetStatus();
        var runtime = BuildRuntimeHealth();
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
            lastIndexRefreshUtc = _lastIndexRefreshUtc,
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
            logFileFound = File.Exists(Logger.FilePath)
        };
    }

    private void HandleHealthPage(HttpListenerContext ctx)
    {
        var status = _callbacks.GetStatus();
        var displayDiagnostics = _callbacks.GetDisplayDiagnostics();
        var certificate = TryGetHttpsCertificate();
        var runtime = BuildRuntimeHealthJson();
        var html = $$"""
            <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width,initial-scale=1"/>
            <title>RemotePlay Health</title>
            <style>body{background:#111;color:#eee;font-family:Segoe UI,Arial,sans-serif;margin:0;padding:24px}main{max-width:860px;margin:auto}h1{color:#e94560}.card{background:#1a1a2e;border:1px solid #333;border-radius:8px;padding:16px;margin:12px 0}.ok{color:#00d4aa}.warn{color:#ffaa00}dt{color:#888;margin-top:10px}dd{margin:3px 0 0 0;word-break:break-word}a,button{color:#00d4aa}button{background:#22263b;border:1px solid #3a3a57;border-radius:6px;padding:8px 10px;cursor:pointer;margin:4px 6px 4px 0}pre{white-space:pre-wrap;background:#0a0a0a;border:1px solid #333;border-radius:8px;padding:12px;overflow:auto}</style>
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
            <dt>Previous video</dt><dd>{{HtmlEncode(string.IsNullOrWhiteSpace(status.PreviousTitle) ? "N/A" : status.PreviousTitle)}}</dd>
            <dt>Next video</dt><dd>{{HtmlEncode(string.IsNullOrWhiteSpace(status.NextTitle) ? "N/A" : status.NextTitle)}}</dd>
            </dl></section>
            <section class="card"><h2>Playback preferences</h2><dl>
            <dt>End behavior</dt><dd>{{HtmlEncode(_config.PlaybackEndBehavior.ToString())}}</dd>
            <dt>Preferred audio language</dt><dd>{{HtmlEncode(_config.PreferredAudioLanguage)}}</dd>
            <dt>Preferred subtitle language</dt><dd>{{HtmlEncode(_config.PreferredSubtitleLanguage)}}</dd>
            <dt>Prefer forced subtitles</dt><dd>{{_config.PreferForcedSubtitles}}</dd>
            </dl></section>
            <section class="card"><h2 class="{{(displayDiagnostics.NeedsFullscreenRepair ? "warn" : "ok")}}">Display diagnostics</h2><dl>
            <dt>Preferred display index</dt><dd>{{displayDiagnostics.PreferredDisplayIndex}}</dd>
            <dt>Target display</dt><dd>{{displayDiagnostics.TargetDisplayIndex}} — {{HtmlEncode(displayDiagnostics.TargetDisplayName)}}</dd>
            <dt>Target bounds</dt><dd>{{displayDiagnostics.TargetLeft}}, {{displayDiagnostics.TargetTop}}, {{displayDiagnostics.TargetWidth}}×{{displayDiagnostics.TargetHeight}}</dd>
            <dt>Window bounds</dt><dd>{{displayDiagnostics.WindowLeft}}, {{displayDiagnostics.WindowTop}}, {{displayDiagnostics.WindowWidth}}×{{displayDiagnostics.WindowHeight}}</dd>
            <dt>Window state</dt><dd>{{HtmlEncode(displayDiagnostics.WindowState)}} / {{HtmlEncode(displayDiagnostics.WindowStyle)}} / {{HtmlEncode(displayDiagnostics.ResizeMode)}} / Topmost={{displayDiagnostics.Topmost}}</dd>
            <dt>DPI scale</dt><dd>{{displayDiagnostics.DpiScaleX}} × {{displayDiagnostics.DpiScaleY}}</dd>
            <dt>Needs fullscreen repair</dt><dd>{{displayDiagnostics.NeedsFullscreenRepair}}</dd>
            <dt>Current video</dt><dd>{{HtmlEncode(string.IsNullOrWhiteSpace(displayDiagnostics.CurrentTitle) ? "N/A" : displayDiagnostics.CurrentTitle)}}</dd>
            <dt>Video file</dt><dd>{{HtmlEncode(string.IsNullOrWhiteSpace(displayDiagnostics.CurrentFilePath) ? "N/A" : displayDiagnostics.CurrentFilePath)}}</dd>
            <dt>Display settings</dt><dd>Zoom={{Math.Round(displayDiagnostics.Zoom * 100)}}%, Brightness={{Math.Round(displayDiagnostics.Brightness * 100)}}%, Saturation={{Math.Round(displayDiagnostics.Saturation * 100)}}%</dd>
            <dt>Video surface</dt><dd>Panel {{displayDiagnostics.VideoSurfaceWidth}}×{{displayDiagnostics.VideoSurfaceHeight}}, Player {{displayDiagnostics.VideoPlayerActualWidth}}×{{displayDiagnostics.VideoPlayerActualHeight}}</dd>
            </dl><p><a href="/api/display-diagnostics" target="_blank" rel="noopener">Open display diagnostics JSON</a></p></section>
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
            <section class="card"><h2>Runtime diagnostics</h2><pre id="runtime-json">{{HtmlEncode(runtime)}}</pre></section>
            <section class="card"><h2>Admin actions</h2><p>
            <button onclick="rescanLibrary()">Start rescan</button>
            <button onclick="location.href='/remoteplay.log'">Download log</button>
            <button onclick="refreshRuntime()">Refresh runtime</button>
            </p><p id="admin-result" class="warn"></p></section>
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
            </main></body></html>
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

    private void HandleSearch(HttpListenerContext ctx)
    {
        var query = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        StartLibraryIndexRefresh(force: false);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resumeMap = PlaybackHistory.GetDefaultResumeMap();
        var naturalComparer = new NaturalStringComparer();

        // Find files matching the search terms
        var files = terms.Length == 0
            ? Array.Empty<object>()
            : _libraryIndex
                .Where(f => terms.All(t => f.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(f => f.Name)
                .Take(200)
                .Select(f => BuildCardFile(Path.GetFileNameWithoutExtension(f.FilePath), f.FilePath, resumeMap, f.FolderName))
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
            indexedFiles = _libraryIndex.Length,
            indexing = _isIndexing,
            lastRefreshUtc = _lastIndexRefreshUtc
        }));
    }

    private void HandleRecent(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var files = PlaybackHistory.GetDefaultRecent(_config.PlaybackHistoryLimit)
            .Where(item => WebPathHelpers.IsUnderRoot(item.FilePath, root))
            .Select(item => new
            {
                name = Path.GetFileNameWithoutExtension(item.FilePath),
                displayName = CleanDisplayTitle(Path.GetFileNameWithoutExtension(item.FilePath)),
                path = WebPathHelpers.EncodePath(item.FilePath),
                position = Math.Round(item.PositionSeconds, 1),
                duration = Math.Round(item.DurationSeconds, 1),
                progress = item.DurationSeconds > 0 ? Math.Round(item.PositionSeconds / item.DurationSeconds, 3) : 0,
                resume = FormatTime(item.PositionSeconds),
                folder = Path.GetFileName(Path.GetDirectoryName(item.FilePath)) ?? string.Empty
            })
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { files }));
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

        var naturalComparer = new NaturalStringComparer();
        var folders = Directory.EnumerateDirectories(targetDir)
            .Where(d => !HiddenFolderNames.Contains(Path.GetFileName(d)))
            .OrderBy(d => Path.GetFileName(d), naturalComparer)
            .Select(d => new
            {
                name = Path.GetFileName(d),
                dir = WebPathHelpers.EncodePath(d)
            })
            .ToArray();

        var resumeMap = PlaybackHistory.GetDefaultResumeMap();
        var files = Directory.EnumerateFiles(targetDir)
            .Where(f => WebPathHelpers.IsVideoFile(f, VideoExtensions))
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), naturalComparer)
            .Select(f => BuildCardFile(Path.GetFileNameWithoutExtension(f), f, resumeMap))
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
                return;

            if (!force && _libraryIndex.Length > 0 && _lastIndexRefreshUtc is not null)
                return;

            _isIndexing = true;
            _scanStartedUtc = DateTimeOffset.UtcNow;
            _scannedFiles = 0;
            _scannedFolders = 0;
            _lastScanError = string.Empty;
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

                var files = EnumerateLibraryVideoFiles(root, () => Interlocked.Increment(ref _scannedFolders))
                    .Where(f => WebPathHelpers.IsVideoFile(f, VideoExtensions))
                    .Select(f =>
                    {
                        Interlocked.Increment(ref _scannedFiles);
                        return f;
                    })
                    .Select(f => new LibraryFile(
                        Path.GetFileNameWithoutExtension(f),
                        f,
                        WebPathHelpers.EncodePath(f),
                        Path.GetFileName(Path.GetDirectoryName(f)) ?? string.Empty,
                        BuildSearchText(root, f)))
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

    private static IEnumerable<string> EnumerateLibraryVideoFiles(string root, Action? onFolderScanned = null)
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

    private static object BuildCardFile(string name, string filePath, IReadOnlyDictionary<string, RecentPlaybackItem> resumeMap, string folder = "")
    {
        resumeMap.TryGetValue(filePath, out var resume);
        return new
        {
            name,
            displayName = CleanDisplayTitle(name),
            path = WebPathHelpers.EncodePath(filePath),
            folder,
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

    private class NaturalStringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var xParts = System.Text.RegularExpressions.Regex.Split(x, @"(\d+)");
            var yParts = System.Text.RegularExpressions.Regex.Split(y, @"(\d+)");

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

}
