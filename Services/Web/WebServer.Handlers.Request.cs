using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace RemotePlay;

internal sealed partial class WebServer
{
    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        // Cancelling the token stops the listener, which causes GetContextAsync to throw
        // HttpListenerException — the loop then exits cleanly.
        using var registration = cancellationToken.Register(() =>
        {
            try { _listener.Stop(); } catch { }
        });

        while (_listener.IsListening && !cancellationToken.IsCancellationRequested)
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
        var sw = Stopwatch.StartNew();
        var method = ctx.Request.HttpMethod;
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var clientIp = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? string.Empty;
        try
        {
            if (IsRateLimited(clientIp))
            {
                Logger.Warning("WebServer", $"Rate limit exceeded for {clientIp} on {method} {path}");
                TrySendResponse(ctx, 429, "text/plain", "Too Many Requests");
                return;
            }

            await HandleRequestAsync(ctx).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("Error handling request", ex);
            TrySendResponse(ctx, 500, "text/plain", "Internal server error");
        }
        finally
        {
            // Skip verbose logging for high-frequency polling endpoints to avoid log spam.
            if (!IsPollingPath(path))
                Logger.Detail("WebServer", $"{method} {path} {sw.ElapsedMilliseconds}ms [ip={clientIp}]");
        }
    }

    private static readonly HashSet<string> _pollingPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/status",
        "/api/expert-mode",
        "/api/music/status",
        "/api/thumbnails/status",
        "/api/version",
        "/api/peers",
        "/api/dlna/renderers",
        "/api/next-in-folder",
        "/api/library-status",
        "/api/server-log",
        // SSE long-poll: one persistent connection per client, not a repeated request.
        "/api/events",
    };

    private static bool IsPollingPath(string path) => _pollingPaths.Contains(path);

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var urlPath = req.Url?.AbsolutePath ?? "/";
        _lastRequestUtc = DateTimeOffset.UtcNow;

        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

        // Handle CORS pre-flight for all endpoints so cross-origin browser requests work.
        if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
            ctx.Response.AddHeader("Access-Control-Max-Age", "86400");
            TrySendResponse(ctx, 204, "text/plain", string.Empty);
            return;
        }

        switch (urlPath)
        {
            case "/" or "/index.html":
                ctx.Response.AddHeader("Cache-Control", "no-cache");
                TrySendResponse(ctx, 200, "text/html; charset=utf-8",
                    GetHtmlPage().Replace("__INSTANCE_NAME__", System.Web.HttpUtility.HtmlEncode(_config.InstanceName), StringComparison.Ordinal));
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

            case "/world-50m.json":
                ctx.Response.AddHeader("Cache-Control", "public, max-age=86400");
                TrySendResponse(ctx, 200, "application/json; charset=utf-8", GetWorld50m());
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

            case "/settings":
                HandleSettingsPage(ctx);
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
                TrySendResponse(ctx, 404, "text/plain", "Not found.");
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

            case "/api/restart":
                // Return the port the server will be listening on after restart,
                // so the browser can redirect itself to the correct origin.
                var restartNewPort = _config.Port;
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, newPort = restartNewPort }));
                _ = Task.Run(() => { Thread.Sleep(500); _callbacks.RestartServer(); });
                break;

            case "/api/rescan-music":
                StartMusicIndexRefresh();
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true }));
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

            case "/api/durations":
                HandleDurations(ctx);
                break;

            case "/api/recent":
                HandleRecent(ctx);
                break;

            case "/api/recent/clear":
                HandleRecentClear(ctx);
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

            case "/api/log":
                HandleClientLog(ctx);
                break;

            case "/api/local-playing":
                HandleLocalPlayingAsync(ctx);
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
                ScheduleSsePush();
                break;

            case "/api/stop":
                _callbacks.Stop();
                Logger.Info("Playback", "Stopping Video on Server");
                TrySendResponse(ctx, 200, "text/plain", "OK");
                ScheduleSsePush();
                break;

            case "/api/pause":
                _callbacks.Pause();
                TrySendResponse(ctx, 200, "text/plain", "OK");
                ScheduleSsePush();
                break;

            case "/api/thumb":
                HandleThumb(ctx);
                break;

            case "/api/status":
                HandleStatus(ctx);
                break;

            // Long-lived Server-Sent Events stream: replaces 1 s polling on clients.
            case "/api/events":
                await HandleSseEventsAsync(ctx, _listenerCts.Token).ConfigureAwait(false);
                break;

            case "/api/expert-mode":
                HandleExpertMode(ctx);
                break;

            case "/api/debug-mode":
                HandleDebugMode(ctx);
                break;

            case "/api/settings":
                if (req.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    HandleApiSettingsGet(ctx);
                else
                    HandleApiSettingsPost(ctx);
                break;

            case "/api/settings/test-port":
                HandleApiSettingsTestPort(ctx);
                break;

            case "/api/settings/test-path":
                HandleApiSettingsTestPath(ctx);
                break;

            case "/api/settings/test-url":
                HandleApiSettingsTestUrl(ctx);
                break;

            case "/api/audio-devices":
                HandleApiAudioDevices(ctx);
                break;

            case "/api/displays":
                HandleApiDisplays(ctx);
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

            case "/api/chapter":
                HandleChapter(ctx);
                break;

            case "/api/eq-preset":
                HandleEqPreset(ctx);
                break;

            case "/api/reverb-preset":
                HandleReverbPreset(ctx);
                break;

            case "/api/adjacent":
                HandleAdjacent(ctx);
                break;

            // -- Next video in the same folder (for the "Up Next" browser card) --------
            case "/api/next-in-folder":
                HandleNextInFolder(ctx);
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

            // ── DLNA / UPnP renderer discovery ────────────────────────────────
            case "/api/dlna/renderers":
                HandleDlnaRenderers(ctx);
                break;

            case "/api/dlna/play":
                HandleDlnaPlay(ctx);
                break;

            case "/api/version":
                HandleVersion(ctx);
                break;

            case "/api/music/browse":
                HandleMusicBrowse(ctx);
                break;

            case "/api/music/playlist":
                HandleMusicPlaylist(ctx);
                break;

            case "/api/music/dynamic":
                if (string.Equals(ctx.Request.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase))
                    HandleMusicDynamicCreate(ctx);
                else if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    HandleMusicDynamicSave(ctx);
                else if (string.Equals(ctx.Request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
                    HandleMusicDynamicDelete(ctx);
                else
                    HandleMusicDynamicGet(ctx);
                break;

            case "/api/music/dynamic/expand":
                HandleMusicDynamicExpand(ctx);
                break;

            case "/api/music/recent":
                HandleMusicRecent(ctx);
                break;

            case "/api/music/recent/clear":
                HandleMusicRecentClear(ctx);
                break;

            case "/api/music/stream":
                HandleMusicStream(ctx);
                break;

            case "/api/music/search":
                HandleMusicSearch(ctx);
                break;

            case "/api/music/rescan":
                StartMusicIndexRefresh();
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true }));
                break;

            case "/api/music/status":
            {
                var pb = _callbacks.GetMusicStatus();
                if (pb.IsPlaying && !pb.IsPaused
                    && !string.IsNullOrWhiteSpace(pb.CurrentPath)
                    && pb.Position >= 10
                    && pb.Duration > 0)
                {
                    // Only save position for the client that initiated this playback session.
                    // Saving to every IP that polls would re-add a cleared entry for bystander clients.
                    var pollerIp = GetClientIp(ctx);
                    if (_musicPlayInitiatorIp is null || _musicPlayInitiatorIp == pollerIp
                        || pollerIp == "127.0.0.1" || pollerIp == "::1" || pollerIp == "localhost")
                    {
                        GetHistoryForIp(pollerIp).SavePosition(
                            pb.CurrentPath,
                            TimeSpan.FromSeconds(pb.Position),
                            TimeSpan.FromSeconds(pb.Duration),
                            _config.PlaybackHistoryLimit);
                    }
                }
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(GetMusicScanStatus()));
                break;
            }

            case "/api/music/play":
                HandleMusicPlay(ctx);
                break;

            case "/api/music/queue-next":
                HandleMusicQueueNext(ctx);
                break;

            case "/api/music/cover":
                HandleMusicCover(ctx);
                break;

            case "/api/music/album-art":
                await HandleMusicAlbumArtAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/album-art/export":
                await HandleMusicAlbumArtExportAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/album-art/import":
                await HandleMusicAlbumArtImportAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/lyrics":
                await HandleMusicLyricsAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/lyrics/clear-cache":
                await HandleMusicLyricsClearCacheAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/lyrics/offsets":
                await HandleMusicLyricsOffsetsAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/lyrics/offsets/export":
                await HandleMusicLyricsOffsetsExportAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/lyrics/export":
                await HandleMusicLyricsExportAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/lyrics/import":
                await HandleMusicLyricsImportAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/sync/all":
                await HandleSyncAllAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/music/pause":
                _callbacks.PauseMusic();
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                ScheduleSsePush();
                break;

            case "/api/music/stop":
                _callbacks.StopMusic();
                Logger.Info("Playback", "Stopping Music on Server");
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                ScheduleSsePush();
                break;

            case "/api/music/seek":
                HandleMusicSeek(ctx);
                break;

            case "/api/music/volume":
                HandleMusicVolume(ctx);
                break;

            case "/api/music/boost":
                HandleMusicBoost(ctx);
                break;

            case "/api/music/reverb-preset":
                HandleMusicReverbPreset(ctx);
                break;

            case "/api/music/eq-preset":
                HandleMusicEqPreset(ctx);
                break;

            case "/api/music/reset-m3u-cache":
                HandleResetM3uCache(ctx);
                break;

            // ── Radio ─────────────────────────────────────────────────────────────
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
                Logger.Info("Playback", "Stopping Radio on Server");
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                ScheduleSsePush();
                break;

            case "/api/radio/volume":
                HandleRadioVolume(ctx);
                break;

            case "/api/radio/boost":
                HandleRadioBoost(ctx);
                break;

            case "/api/radio/reverb-preset":
                HandleRadioReverbPreset(ctx);
                break;

            case "/api/radio/eq-preset":
                HandleRadioEqPreset(ctx);
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

            case "/api/radio/stream-proxy":
                await HandleRadioStreamProxyAsync(ctx).ConfigureAwait(false);
                break;

            case "/api/server-log":
            {
                // Returns the last N lines of the log file as a JSON array of parsed entries.
                // Query params: lines (default 500), level (INFO/DETAIL/WARN/ERROR), source.
                var qp = ctx.Request.QueryString;
                int maxLines = int.TryParse(qp["lines"], out var ml) ? Math.Clamp(ml, 1, 5000) : 500;
                string? levelFilter = qp["level"];
                string? sourceFilter = qp["source"];
                try
                {
                    var entries = ReadLogEntries(Logger.FilePath, maxLines, levelFilter, sourceFilter);
                    TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(entries));
                }
                catch (Exception ex)
                {
                    TrySendResponse(ctx, 500, "application/json",
                        JsonSerializer.Serialize(new { error = ex.Message }));
                }
                break;
            }

            case "/api/server-log/clear":
                if (ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Clear();
                    TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                }
                else
                {
                    TrySendResponse(ctx, 405, "text/plain", "Method not allowed");
                }
                break;

            default:
                TrySendResponse(ctx, 404, "text/plain", "Not found");
                break;
        }
    }
}
