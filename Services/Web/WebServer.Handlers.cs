using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using RemotePlay.Services;
using File = System.IO.File;

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
        "/api/music/status",
        "/api/thumbnails/status",
        "/api/version",
        "/api/peers",
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
                break;

            case "/api/stop":
                _callbacks.Stop();
                Logger.Info("Playback", "Stopping Video on Server");
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

            case "/api/music/playlist":
                HandleMusicPlaylist(ctx);
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

            case "/api/music/pause":
                _callbacks.PauseMusic();
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
                break;

            case "/api/music/stop":
                _callbacks.StopMusic();
                Logger.Info("Playback", "Stopping Music on Server");
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
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
                Logger.Info("Playback", "Stopping Radio on Server");
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
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
            chapters = s.Chapters.Select(c => new { id = c.Id, name = c.Name, startSeconds = Math.Round(c.StartSeconds, 1), durationSeconds = Math.Round(c.DurationSeconds, 1) }).ToArray(),
            currentChapter = s.CurrentChapter,
            eqPreset = s.EqPreset,
            reverbPreset = s.ReverbPreset,
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

    private void HandleChapter(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var chapterId))
        {
            _callbacks.SeekToChapter(chapterId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad chapter id");
        }
    }

    private void HandleEqPreset(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var presetId))
        {
            _callbacks.SetEqPreset(presetId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad eq-preset id");
        }
    }

    private void HandleMusicEqPreset(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var presetId))
        {
            _callbacks.SetMusicEqPreset(presetId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad eq-preset id");
        }
    }

    private void HandleRadioEqPreset(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var presetId))
        {
            _callbacks.RadioSetEqPreset(presetId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad eq-preset id");
        }
    }

    private void HandleReverbPreset(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var presetId))
        {
            _callbacks.SetReverbPreset(presetId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad reverb-preset id");
        }
    }

    private void HandleMusicReverbPreset(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var presetId))
        {
            _callbacks.SetMusicReverbPreset(presetId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad reverb-preset id");
        }
    }

    private void HandleRadioReverbPreset(HttpListenerContext ctx)
    {
        var idParam = ctx.Request.QueryString["id"];
        if (MediaControlValueParser.TryParseInteger(idParam, out var presetId))
        {
            _callbacks.RadioSetReverbPreset(presetId);
            TrySendResponse(ctx, 200, "text/plain", "OK");
        }
        else
        {
            TrySendResponse(ctx, 400, "text/plain", "Bad reverb-preset id");
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

    private static (int files, long bytes) BuildAlbumArtCacheHealth()
    {
        try
        {
            if (!Directory.Exists(AlbumArtCacheDir)) return (0, 0);
            var files = Directory.EnumerateFiles(AlbumArtCacheDir, "*.jpg")
                                 .Where(f => new FileInfo(f).Length > 0).ToArray();
            return (files.Length, files.Sum(TryGetFileLength));
        }
        catch { return (0, 0); }
    }

    private void HandleHealthPage(HttpListenerContext ctx)
    {
        var status = _callbacks.GetStatus();
        var displayDiagnostics = _callbacks.GetDisplayDiagnostics();
        var scanStatus = GetLibraryScanStatus();
        var certificate = TryGetHttpsCertificate();
        var runtime = BuildRuntimeHealthJson();
        var musicStatus = _callbacks.GetMusicStatus();
        var radioStatus = _callbacks.RadioGetStatus();
        var radioFavorites = _callbacks.RadioGetFavorites();

        var startupWarning  = string.IsNullOrWhiteSpace(_startupWarning) ? "None" : _startupWarning;
        var playbackError   = string.IsNullOrWhiteSpace(status.LastError) ? "None" : status.LastError;
        var scanError       = string.IsNullOrWhiteSpace(scanStatus.LastError) ? "None" : scanStatus.LastError;
        var serverState     = string.IsNullOrWhiteSpace(_startupWarning) ? "ok" : "warn";
        var playbackState   = string.IsNullOrWhiteSpace(status.LastError) ? "ok" : "warn";
        var libraryState    = string.IsNullOrWhiteSpace(scanStatus.LastError) ? (_isIndexing ? "warn" : "ok") : "warn";
        var displayState    = displayDiagnostics.NeedsFullscreenRepair ? "warn" : "ok";
        var certificateState = certificate is not null ? "ok" : "warn";
        var musicState      = string.IsNullOrWhiteSpace(musicStatus.LastError) ? "ok" : "warn";
        var radioState      = radioStatus.IsStalled || !string.IsNullOrWhiteSpace(radioStatus.Error) ? "warn" : "ok";

        var (artFiles, artBytes) = BuildAlbumArtCacheHealth();
        var artSizeLabel = artBytes >= 1024 * 1024
            ? $"{artBytes / (1024.0 * 1024):F1} MB"
            : $"{artBytes / 1024.0:F0} KB";

        // Serialise known peers for the export UI dropdown (exclude self)
        var peersJson = _broadcaster is null ? "[]" : JsonSerializer.Serialize(
            _broadcaster.GetPeers()
                .Where(p => !p.IsSelf)
                .Select(p => new { p.Name, p.Url }));

        var playbackLabel   = status.IsPlaying ? "Playing" : "Idle";
        var libraryLabel    = _isIndexing ? "Indexing" : "Ready";
        var displayLabel    = displayDiagnostics.NeedsFullscreenRepair ? "Repair needed" : "Fullscreen OK";
        var certificateLabel = certificate is not null ? "Available" : "Missing";
        var musicLabel      = musicStatus.IsPlaying ? "Playing" : (musicStatus.IsPaused ? "Paused" : "Idle");
        var radioLabel      = radioStatus.IsPlaying ? (radioStatus.IsStalled ? "Stalled" : "Streaming") : "Idle";

        // Music active card
        var musicActiveCard = (musicStatus.IsPlaying || musicStatus.IsPaused) ? $"""
              <section class="card accent-card accent-music"><div class="accent-bar"></div><h2>&#127925; Now playing &mdash; Music <span class="pill ok">ACTIVE</span></h2><dl class="grid">
                <div class="metric wide"><dt>Track</dt><dd><strong>{HtmlEncode(string.IsNullOrWhiteSpace(musicStatus.Title) ? Path.GetFileName(musicStatus.CurrentPath) : musicStatus.Title)}</strong>{(string.IsNullOrWhiteSpace(musicStatus.Artist) ? "" : $" <span style='color:var(--muted)'>by {HtmlEncode(musicStatus.Artist)}</span>")}</dd></div>
                <div class="metric wide"><dt>File</dt><dd class="mono">{HtmlEncode(musicStatus.CurrentPath)}</dd></div>
                <div class="metric"><dt>Format</dt><dd>{HtmlEncode(Path.GetExtension(musicStatus.CurrentPath).TrimStart('.').ToUpperInvariant())}</dd></div>
                <div class="metric"><dt>State</dt><dd>{(musicStatus.IsPaused ? "&#9208; Paused" : "&#9654; Playing")}</dd></div>
                <div class="metric"><dt>Position</dt><dd>{TimeSpan.FromSeconds(musicStatus.Position).ToString(@"hh\:mm\:ss")}</dd></div>
                <div class="metric"><dt>Duration</dt><dd>{(musicStatus.Duration > 0 ? TimeSpan.FromSeconds(musicStatus.Duration).ToString(@"hh\:mm\:ss") : "N/A")}</dd></div>
                {(string.IsNullOrWhiteSpace(musicStatus.LastError) ? "" : $"<div class=\"metric wide\"><dt>Last error</dt><dd class=\"warn-text\">{HtmlEncode(musicStatus.LastError)}</dd></div>")}
              </dl></section>
            """ : string.Empty;

        // Radio active card
        var radioActiveCard = radioStatus.IsPlaying ? $"""
              <section class="card accent-card accent-radio"><div class="accent-bar"></div><h2>&#128251; Now playing &mdash; Radio <span class="pill {(radioStatus.IsStalled ? "warn" : "ok")}">{(radioStatus.IsStalled ? "STALLED" : "LIVE")}</span></h2><dl class="grid">
                <div class="metric wide"><dt>Station</dt><dd><strong>{HtmlEncode(radioStatus.StationName)}</strong></dd></div>
                {(string.IsNullOrWhiteSpace(radioStatus.StreamTitle) ? "" : $"<div class=\"metric wide\"><dt>&#127925; Now on air</dt><dd>{HtmlEncode(radioStatus.StreamTitle)}</dd></div>")}
                <div class="metric wide"><dt>Stream URL</dt><dd class="mono">{HtmlEncode(radioStatus.StationUrl)}</dd></div>
                <div class="metric"><dt>Elapsed</dt><dd>{TimeSpan.FromSeconds(radioStatus.ElapsedSeconds).ToString(@"hh\:mm\:ss")}</dd></div>
                <div class="metric"><dt>Stalled</dt><dd class="{(radioStatus.IsStalled ? "warn-text" : "ok-text")}">{radioStatus.IsStalled}</dd></div>
                <div class="metric"><dt>Volume</dt><dd>{Math.Round(radioStatus.Volume * 100)}%</dd></div>
                <div class="metric"><dt>Boost</dt><dd>{radioStatus.Boost:F1}&#215;</dd></div>
                {(string.IsNullOrWhiteSpace(radioStatus.Error) ? "" : $"<div class=\"metric wide\"><dt>Last error</dt><dd class=\"warn-text\">{HtmlEncode(radioStatus.Error)}</dd></div>")}
              </dl></section>
            """ : string.Empty;

        // Radio favorites list
        var radioFavRows = radioFavorites.Count == 0
            ? "<p style=\"color:var(--muted);font-size:.88rem;margin:.4rem 0 0\">No radio favorites saved yet.</p>"
            : string.Join("", radioFavorites.Select(f =>
                $"<div class=\"fav-row\"><span class=\"fav-flag\">{HtmlEncode(string.IsNullOrWhiteSpace(f.CountryCode) ? "🌐" : f.CountryCode)}</span><span class=\"fav-name\">{HtmlEncode(f.Name)}</span><span class=\"fav-meta\">{HtmlEncode(f.Country)}{(f.Bitrate > 0 ? $" &middot; {f.Bitrate} kbps" : "")}{(string.IsNullOrWhiteSpace(f.Codec) ? "" : $" &middot; {HtmlEncode(f.Codec.ToUpperInvariant())}")}</span></div>"));

        var html = $$$$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width,initial-scale=1"/>
            <meta name="theme-color" content="#0b1020"/>
            <title>RemotePlay Health</title>
            <style>
            :root{color-scheme:dark;--bg:#090d18;--card:rgba(18,24,48,.88);--line:rgba(148,163,184,.16);--text:#eef4ff;--muted:#8898b8;--dim:#5a6880;--accent:#e94560;--cyan:#00d4aa;--warn:#ffaa00;--bad:#ff5c77;--blue:#5b8cff;--purple:#9f7efe;--shadow:0 20px 60px rgba(0,0,0,.4)}
            *{box-sizing:border-box}body{margin:0;min-height:100vh;background:radial-gradient(ellipse 80% 50% at 0% 0%,rgba(91,140,255,.18),transparent),radial-gradient(ellipse 60% 40% at 100% 0%,rgba(233,69,96,.16),transparent),linear-gradient(160deg,#0a0f1e 0%,#060810 100%);color:var(--text);font-family:Segoe UI,system-ui,Arial,sans-serif}
            a{color:var(--cyan);text-decoration:none}a:hover{text-decoration:underline}
            .page{width:min(1480px,100%);margin:0 auto;padding:16px 20px}
            /* ── Hero ─────────────────────────────────────── */
            .hero{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:1.2rem;align-items:center;padding:1.1rem 1.4rem;border:1px solid rgba(255,255,255,.1);border-radius:24px;background:linear-gradient(135deg,rgba(24,32,68,.92) 0%,rgba(10,14,30,.94) 100%);box-shadow:var(--shadow),inset 0 1px 0 rgba(255,255,255,.06);position:sticky;top:10px;z-index:4;backdrop-filter:blur(18px)}
            .eyebrow{color:var(--muted);font-weight:800;font-size:.72rem;text-transform:uppercase;letter-spacing:.12em}
            .hero h1{margin:.12rem 0 .2rem;font-size:clamp(1.5rem,3.5vw,2.6rem);line-height:1;background:linear-gradient(135deg,#fff 40%,var(--muted));-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text}
            .subtitle{color:var(--muted);font-size:.9rem;line-height:1.4}
            .actions{display:flex;gap:.5rem;flex-wrap:wrap;justify-content:flex-end}
            .button,button{display:inline-flex;align-items:center;justify-content:center;gap:.35rem;border:1px solid rgba(255,255,255,.1);border-radius:999px;background:rgba(30,40,80,.9);color:#dce8ff;padding:.6rem .95rem;font-size:.88rem;font-weight:800;cursor:pointer;min-height:40px;transition:filter .15s,background .15s}
            .button.primary,button.primary{background:linear-gradient(135deg,var(--accent),#c4294a);border-color:transparent;color:#fff}
            .button:hover,button:hover{filter:brightness(1.15);text-decoration:none}
            /* ── Overview tiles ───────────────────────────── */
            .overview{display:grid;grid-template-columns:repeat(8,minmax(0,1fr));gap:.7rem;margin:1rem 0}
            .tile{background:var(--card);border:1px solid var(--line);border-radius:18px;padding:.85rem .9rem;display:flex;flex-direction:column;gap:.35rem;position:relative;overflow:hidden;box-shadow:0 8px 24px rgba(0,0,0,.22)}
            .tile::after{content:'';position:absolute;inset:0;border-radius:inherit;background:linear-gradient(135deg,rgba(255,255,255,.04),transparent 60%);pointer-events:none}
            .tile-icon{font-size:1.4rem;line-height:1}
            .tile-label{color:var(--muted);font-size:.68rem;font-weight:800;text-transform:uppercase;letter-spacing:.08em}
            .tile-value{font-size:1.05rem;font-weight:900;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:#fff}
            .tile-sub{color:var(--dim);font-size:.72rem;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            /* ── Pills ────────────────────────────────────── */
            .pill{display:inline-flex;align-items:center;gap:.3rem;border-radius:999px;padding:.22rem .5rem;font-size:.7rem;font-weight:900;background:rgba(148,163,184,.1);color:var(--muted)}
            .pill::before{content:'';width:.48rem;height:.48rem;border-radius:50%;background:var(--dim)}
            .pill.ok{color:#9fffe8;background:rgba(0,212,170,.1)}.pill.ok::before{background:var(--cyan);box-shadow:0 0 6px var(--cyan)}
            .pill.warn{color:#ffd480;background:rgba(255,170,0,.1)}.pill.warn::before{background:var(--warn);box-shadow:0 0 6px var(--warn)}
            .pill.live{color:#ff9fba;background:rgba(233,69,96,.12)}.pill.live::before{background:var(--accent);box-shadow:0 0 6px var(--accent)}
            /* ── Dashboard grid ───────────────────────────── */
            .dashboard{display:grid;grid-template-columns:1.15fr .85fr;gap:.9rem;align-items:start;margin-top:.2rem}
            .stack{display:grid;gap:.9rem}
            /* ── Cards ────────────────────────────────────── */
            .card{background:var(--card);border:1px solid var(--line);border-radius:20px;padding:1rem 1.1rem;box-shadow:0 10px 28px rgba(0,0,0,.22);overflow:hidden;position:relative}
            .card::before{content:'';position:absolute;inset:0;border-radius:inherit;background:linear-gradient(160deg,rgba(255,255,255,.03),transparent 50%);pointer-events:none}
            .card h2{display:flex;align-items:center;justify-content:space-between;gap:.6rem;margin:0 0 .75rem;font-size:.95rem;font-weight:800;color:#d8e8ff}
            .card h3{font-size:.8rem;font-weight:800;text-transform:uppercase;letter-spacing:.05em;color:var(--accent);margin:.75rem 0 .3rem}
            /* Accent cards for live state */
            .accent-card{border-top:none;padding-top:0}
            .accent-bar{height:3px;margin:-1px -1.1rem .85rem;border-radius:20px 20px 0 0}
            .accent-music .accent-bar{background:linear-gradient(90deg,var(--purple),var(--cyan))}
            .accent-radio .accent-bar{background:linear-gradient(90deg,var(--accent),var(--warn))}
            /* ── Metric grid ──────────────────────────────── */
            .grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.5rem .7rem}
            .metric{background:rgba(255,255,255,.03);border:1px solid rgba(255,255,255,.05);border-radius:12px;padding:.6rem .65rem;min-width:0}
            .metric dt{color:var(--muted);font-size:.67rem;font-weight:800;text-transform:uppercase;letter-spacing:.05em;margin:0 0 .25rem}
            .metric dd{margin:0;color:#e8f0ff;word-break:break-word;line-height:1.35;font-size:.92rem}
            .metric.wide{grid-column:1/-1}
            .mono{font-family:Consolas,ui-monospace,monospace;font-size:.82rem}
            /* ── Radio favorites ──────────────────────────── */
            .fav-list{display:grid;gap:.35rem;margin-top:.5rem;max-height:260px;overflow-y:auto;scrollbar-width:thin}
            .fav-row{display:grid;grid-template-columns:2rem 1fr auto;gap:.4rem .55rem;align-items:baseline;padding:.4rem .55rem;border-radius:10px;background:rgba(255,255,255,.03);border:1px solid rgba(255,255,255,.05)}
            .fav-flag{font-size:.92rem;text-align:center}
            .fav-name{font-size:.88rem;font-weight:700;color:#d8e8ff;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            .fav-meta{font-size:.72rem;color:var(--dim);white-space:nowrap}
            /* ── Other ────────────────────────────────────── */
            .runtime-card{grid-column:1/-1}
            pre{white-space:pre-wrap;background:rgba(0,0,0,.3);border:1px solid rgba(255,255,255,.07);border-radius:14px;padding:.9rem;overflow:auto;max-height:400px;color:#c8d8f8;font-size:.82rem}
            .admin-result{min-height:1.1rem;color:#ffd48a;font-weight:700;font-size:.88rem}
            .footer-note{color:var(--dim);font-size:.75rem;text-align:center;padding:1rem}
            .divider{height:1px;background:var(--line);margin:.75rem 0}
            .ok-text{color:var(--cyan)}.warn-text{color:var(--warn)}
            @media(max-width:1180px){.overview{grid-template-columns:repeat(4,minmax(0,1fr))}.dashboard{grid-template-columns:1fr}.runtime-card{grid-column:auto}}
            @media(max-width:700px){.overview{grid-template-columns:repeat(2,minmax(0,1fr))}.page{padding:.7rem}.hero{position:static;grid-template-columns:1fr}.actions{display:grid;grid-template-columns:1fr 1fr}}
            </style>
            </head>
            <body>
            <main class="page">
            <section class="hero">
              <div>
                <div class="eyebrow">RemotePlay &mdash; diagnostics</div>
                <h1>Health dashboard</h1>
                <div class="subtitle">Live server, video, music, radio, display, library, and runtime checks for this media computer.</div>
              </div>
              <div class="actions">
                <a class="button" href="/">Open remote</a>
                <button class="primary" onclick="refreshRuntime()">&#8635; Refresh</button>
                <button onclick="location.href='/remoteplay.log'">Log</button>
              </div>
            </section>

            <section class="overview" aria-label="Health summary">
              <article class="tile"><div class="tile-icon">&#128421;</div><div class="tile-label">Server</div><div class="tile-value">{{{{HtmlEncode(_activeScheme.ToUpperInvariant())}}}}&thinsp;:&thinsp;{{{{_config.Port}}}}</div><span class="pill {{{{serverState}}}}">{{{{serverState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#127902;</div><div class="tile-label">Video</div><div class="tile-value">{{{{HtmlEncode(playbackLabel)}}}}</div><span class="pill {{{{playbackState}}}}">{{{{playbackState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#127925;</div><div class="tile-label">Music</div><div class="tile-value">{{{{HtmlEncode(musicLabel)}}}}</div><div class="tile-sub">{{{{_musicIndex.Length}}}} tracks indexed</div><span class="pill {{{{musicState}}}}">{{{{musicState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#128251;</div><div class="tile-label">Radio</div><div class="tile-value">{{{{HtmlEncode(radioLabel)}}}}</div><div class="tile-sub">{{{{radioFavorites.Count}}}} favorites saved</div><span class="pill {{{{(radioStatus.IsPlaying ? (radioStatus.IsStalled ? "warn" : "live") : radioState)}}}}}">{{{{(radioStatus.IsPlaying ? (radioStatus.IsStalled ? "STALLED" : "LIVE") : radioState.ToUpperInvariant())}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#128250;</div><div class="tile-label">Video lib</div><div class="tile-value">{{{{_libraryIndex.Length}}}}</div><div class="tile-sub">{{{{HtmlEncode(libraryLabel)}}}}</div><span class="pill {{{{libraryState}}}}">{{{{libraryState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#127926;</div><div class="tile-label">Music lib</div><div class="tile-value">{{{{_musicIndex.Length}}}}</div><div class="tile-sub">{{{{(_isMusicIndexing ? "Indexing…" : "Ready")}}}}}</div><span class="pill {{{{(_isMusicIndexing ? "warn" : "ok")}}}}">{{{{(_isMusicIndexing ? "SCANNING" : "OK")}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#128444;</div><div class="tile-label">Display</div><div class="tile-value">{{{{HtmlEncode(displayLabel)}}}}</div><span class="pill {{{{displayState}}}}">{{{{displayState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#128274;</div><div class="tile-label">HTTPS cert</div><div class="tile-value">{{{{HtmlEncode(certificateLabel)}}}}</div><span class="pill {{{{certificateState}}}}">{{{{certificateState.ToUpperInvariant()}}}}</span></article>
            </section>

            <section class="dashboard">
              <div class="stack">
                {{{{musicActiveCard}}}}
                {{{{radioActiveCard}}}}
                <section class="card"><h2>&#127902; Video playback <span class="pill {{{{playbackState}}}}">{{{{HtmlEncode(playbackLabel)}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Playing</dt><dd>{{{{status.IsPlaying}}}}</dd></div>
                  <div class="metric"><dt>Paused</dt><dd>{{{{status.IsPaused}}}}</dd></div>
                  <div class="metric"><dt>Previous</dt><dd>{{{{HtmlEncode(string.IsNullOrWhiteSpace(status.PreviousTitle) ? "N/A" : status.PreviousTitle)}}}}</dd></div>
                  <div class="metric"><dt>Next</dt><dd>{{{{HtmlEncode(string.IsNullOrWhiteSpace(status.NextTitle) ? "N/A" : status.NextTitle)}}}}</dd></div>
                  <div class="metric"><dt>End behavior</dt><dd>{{{{HtmlEncode(_config.PlaybackEndBehavior.ToString())}}}}</dd></div>
                  <div class="metric"><dt>Forced subtitles</dt><dd>{{{{_config.PreferForcedSubtitles}}}}</dd></div>
                  <div class="metric"><dt>Audio lang</dt><dd>{{{{HtmlEncode(_config.PreferredAudioLanguage)}}}}</dd></div>
                  <div class="metric"><dt>Subtitle lang</dt><dd>{{{{HtmlEncode(_config.PreferredSubtitleLanguage)}}}}</dd></div>
                  <div class="metric wide"><dt>Last error</dt><dd class="{{{{(playbackState == "ok" ? "ok-text" : "warn-text")}}}}">{{{{HtmlEncode(playbackError)}}}}</dd></div>
                </dl></section>

                <section class="card"><h2>&#127925; Music library <span class="pill {{{{(_isMusicIndexing ? "warn" : "ok")}}}}">{{{{(_isMusicIndexing ? "SCANNING" : "READY")}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Indexed tracks</dt><dd>{{{{_musicIndex.Length}}}}</dd></div>
                  <div class="metric"><dt>Indexing now</dt><dd>{{{{_isMusicIndexing}}}}</dd></div>
                  <div class="metric"><dt>Scan progress</dt><dd>{{{{(_isMusicIndexing ? $"{_musicScanProgress} files scanned" : "Idle")}}}}</dd></div>
                  <div class="metric"><dt>Extensions</dt><dd>{{{{HtmlEncode(string.Join(", ", _config.MusicFileExtensions))}}}}</dd></div>
                  <div class="metric wide"><dt>Music folder</dt><dd class="mono">{{{{HtmlEncode(_config.ResolvedMusicPath)}}}}</dd></div>
                  {{{{(string.IsNullOrWhiteSpace(musicStatus.LastError) ? "" : $"<div class=\"metric wide\"><dt>Last error</dt><dd class=\"warn-text\">{HtmlEncode(musicStatus.LastError)}</dd></div>")}}}}
                </dl></section>

                <section class="card"><h2>&#128251; Radio <span class="pill {{{{radioState}}}}">{{{{radioState.ToUpperInvariant()}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Playing</dt><dd>{{{{radioStatus.IsPlaying}}}}</dd></div>
                  <div class="metric"><dt>Stalled</dt><dd class="{{{{(radioStatus.IsStalled ? "warn-text" : "ok-text")}}}}">{{{{radioStatus.IsStalled}}}}</dd></div>
                  <div class="metric"><dt>Volume</dt><dd>{{{{(radioStatus.IsPlaying ? $"{Math.Round(radioStatus.Volume * 100)}%" : "—")}}}}</dd></div>
                  <div class="metric"><dt>Boost</dt><dd>{{{{(radioStatus.IsPlaying ? $"{radioStatus.Boost:F1}×" : "—")}}}}</dd></div>
                  <div class="metric wide"><dt>Current station</dt><dd>{{{{HtmlEncode(string.IsNullOrWhiteSpace(radioStatus.StationName) ? "None" : radioStatus.StationName)}}}}</dd></div>
                  {{{{(string.IsNullOrWhiteSpace(radioStatus.Error) ? "" : $"<div class=\"metric wide\"><dt>Last error</dt><dd class=\"warn-text\">{HtmlEncode(radioStatus.Error)}</dd></div>")}}}}
                </dl>
                <div class="divider"></div>
                <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:.5rem">
                  <span style="color:var(--muted);font-size:.75rem;font-weight:800;text-transform:uppercase;letter-spacing:.07em">&#10084;&#65039; Favorites ({{{{radioFavorites.Count}}}})</span>
                </div>
                <div class="fav-list">{{{{radioFavRows}}}}</div></section>

                <section class="card"><h2>&#128250; Video library <span class="pill {{{{libraryState}}}}">{{{{HtmlEncode(libraryLabel)}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Indexed videos</dt><dd>{{{{_libraryIndex.Length}}}}</dd></div>
                  <div class="metric"><dt>Indexing now</dt><dd>{{{{_isIndexing}}}}</dd></div>
                  <div class="metric"><dt>Scanned files</dt><dd>{{{{scanStatus.ScannedFiles}}}}</dd></div>
                  <div class="metric"><dt>Scanned folders</dt><dd>{{{{scanStatus.ScannedFolders}}}}</dd></div>
                  <div class="metric"><dt>Scan started UTC</dt><dd>{{{{scanStatus.StartedUtc?.ToString("u") ?? "N/A"}}}}</dd></div>
                  <div class="metric"><dt>Last refresh UTC</dt><dd>{{{{_lastIndexRefreshUtc?.ToString("u") ?? "Never"}}}}</dd></div>
                  <div class="metric wide"><dt>Movies folder</dt><dd class="mono">{{{{HtmlEncode(_config.ResolvedMoviesPath)}}}}</dd></div>
                  <div class="metric wide"><dt>Last scan error</dt><dd class="{{{{(scanError == "None" ? "ok-text" : "warn-text")}}}}">{{{{HtmlEncode(scanError)}}}}</dd></div>
                </dl></section>
              </div>

              <div class="stack">
                <section class="card"><h2>&#128421; Server <span class="pill {{{{serverState}}}}">{{{{serverState.ToUpperInvariant()}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Requested mode</dt><dd>{{{{HtmlEncode(_config.Scheme.ToUpperInvariant())}}}}</dd></div>
                  <div class="metric"><dt>Active mode</dt><dd>{{{{HtmlEncode(_activeScheme.ToUpperInvariant())}}}}</dd></div>
                  <div class="metric"><dt>Port</dt><dd>{{{{_config.Port}}}}</dd></div>
                  <div class="metric"><dt>Startup warning</dt><dd class="{{{{(serverState == "ok" ? "ok-text" : "warn-text")}}}}">{{{{HtmlEncode(startupWarning)}}}}</dd></div>
                </dl></section>

                <section class="card"><h2>&#128444; Display diagnostics <span class="pill {{{{displayState}}}}">{{{{HtmlEncode(displayLabel)}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Preferred display</dt><dd>{{{{displayDiagnostics.PreferredDisplayIndex}}}}</dd></div>
                  <div class="metric"><dt>Target display</dt><dd>{{{{displayDiagnostics.TargetDisplayIndex}}}} &mdash; {{{{HtmlEncode(displayDiagnostics.TargetDisplayName)}}}}</dd></div>
                  <div class="metric"><dt>Target bounds</dt><dd>{{{{displayDiagnostics.TargetLeft}}}}, {{{{displayDiagnostics.TargetTop}}}}, {{{{displayDiagnostics.TargetWidth}}}}&times;{{{{displayDiagnostics.TargetHeight}}}}</dd></div>
                  <div class="metric"><dt>Window bounds</dt><dd>{{{{displayDiagnostics.WindowLeft}}}}, {{{{displayDiagnostics.WindowTop}}}}, {{{{displayDiagnostics.WindowWidth}}}}&times;{{{{displayDiagnostics.WindowHeight}}}}</dd></div>
                  <div class="metric"><dt>DPI scale</dt><dd>{{{{displayDiagnostics.DpiScaleX}}}}&times;{{{{displayDiagnostics.DpiScaleY}}}}</dd></div>
                  <div class="metric"><dt>Fullscreen repair</dt><dd>{{{{displayDiagnostics.NeedsFullscreenRepair}}}}</dd></div>
                  <div class="metric wide"><dt>Window state</dt><dd>{{{{HtmlEncode(displayDiagnostics.WindowState)}}}} / {{{{HtmlEncode(displayDiagnostics.WindowStyle)}}}} / {{{{HtmlEncode(displayDiagnostics.ResizeMode)}}}} / Topmost={{{{displayDiagnostics.Topmost}}}}</dd></div>
                  <div class="metric wide"><dt>Current video</dt><dd>{{{{HtmlEncode(string.IsNullOrWhiteSpace(displayDiagnostics.CurrentTitle) ? "N/A" : displayDiagnostics.CurrentTitle)}}}}</dd></div>
                  <div class="metric wide"><dt>Video file</dt><dd class="mono">{{{{HtmlEncode(string.IsNullOrWhiteSpace(displayDiagnostics.CurrentFilePath) ? "N/A" : displayDiagnostics.CurrentFilePath)}}}}</dd></div>
                  <div class="metric"><dt>Zoom / Brightness / Sat</dt><dd>{{{{Math.Round(displayDiagnostics.Zoom*100)}}}}% / {{{{Math.Round(displayDiagnostics.Brightness*100)}}}}% / {{{{Math.Round(displayDiagnostics.Saturation*100)}}}}%</dd></div>
                  <div class="metric"><dt>Video surface</dt><dd>{{{{displayDiagnostics.VideoSurfaceWidth}}}}&times;{{{{displayDiagnostics.VideoSurfaceHeight}}}}</dd></div>
                </dl><div class="divider"></div><a href="/api/display-diagnostics" target="_blank" rel="noopener">Open display diagnostics JSON &#8599;</a></section>

                <section class="card"><h2>&#127502; Media codec info <span class="pill {{{{(displayDiagnostics.CodecInfo is not null ? "ok" : "warn")}}}}">{{{{(displayDiagnostics.CodecInfo is not null ? "CAPTURED" : "NO DATA")}}}}</span></h2>
                  {{{{(displayDiagnostics.CodecInfo is null
                    ? "<p style=\"color:var(--muted);font-size:.88rem\">Codec info is captured when a video starts playing. Start a movie and refresh.</p>"
                    : $"""
                      <dl class="grid">
                        <div class="metric wide"><dt>&#127902; File</dt><dd class="mono">{HtmlEncode(displayDiagnostics.CodecInfo.FileName)}</dd></div>
                        <div class="metric"><dt>Container</dt><dd>{HtmlEncode(displayDiagnostics.CodecInfo.ContainerFormat)}</dd></div>
                        <div class="metric"><dt>Total tracks</dt><dd>{displayDiagnostics.CodecInfo.TotalTracks}</dd></div>
                        <div class="metric"><dt>&#128250; Video</dt><dd>{displayDiagnostics.CodecInfo.VideoTracks.Length}</dd></div>
                        <div class="metric"><dt>&#127925; Audio</dt><dd>{displayDiagnostics.CodecInfo.AudioTracks.Length}</dd></div>
                        <div class="metric"><dt>&#128221; Subtitles</dt><dd>{displayDiagnostics.CodecInfo.SubtitleTracks.Length}</dd></div>
                        <div class="metric"><dt>Captured UTC</dt><dd>{HtmlEncode(displayDiagnostics.CodecInfo.CapturedAtUtc)}</dd></div>
                      </dl>
                      {string.Join("", displayDiagnostics.CodecInfo.VideoTracks.Select((t, i) => $"""
                        <h3>&#128250; Video track {i + 1}</h3>
                        <dl class="grid">
                          <div class="metric"><dt>Codec</dt><dd><strong>{HtmlEncode(t.CodecDescription)}</strong> <span style="color:var(--muted);font-size:.8rem">({HtmlEncode(t.Codec)})</span></dd></div>
                          <div class="metric"><dt>Resolution</dt><dd>{t.Width}&times;{t.Height}</dd></div>
                          <div class="metric"><dt>Frame rate</dt><dd>{HtmlEncode(t.FrameRate)}</dd></div>
                          <div class="metric"><dt>Aspect ratio</dt><dd>{HtmlEncode(t.AspectRatio)}</dd></div>
                          {(string.IsNullOrEmpty(t.Language) ? "" : $"<div class=\"metric\"><dt>Language</dt><dd>{HtmlEncode(t.Language)}</dd></div>")}
                        </dl>
                      """))}
                      {string.Join("", displayDiagnostics.CodecInfo.AudioTracks.Select((t, i) => $"""
                        <h3>&#127925; Audio track {i + 1}</h3>
                        <dl class="grid">
                          <div class="metric"><dt>Codec</dt><dd><strong>{HtmlEncode(t.CodecDescription)}</strong> <span style="color:var(--muted);font-size:.8rem">({HtmlEncode(t.Codec)})</span></dd></div>
                          <div class="metric"><dt>Channels</dt><dd>{HtmlEncode(t.ChannelLayout)}</dd></div>
                          <div class="metric"><dt>Sample rate</dt><dd>{t.SampleRate} Hz</dd></div>
                          {(string.IsNullOrEmpty(t.Language) ? "" : $"<div class=\"metric\"><dt>Language</dt><dd>{HtmlEncode(t.Language)}</dd></div>")}
                        </dl>
                      """))}
                      {(displayDiagnostics.CodecInfo.SubtitleTracks.Length == 0 ? "" : string.Join("", displayDiagnostics.CodecInfo.SubtitleTracks.Select((t, i) => $"""
                        <h3>&#128221; Subtitle track {i + 1}</h3>
                        <dl class="grid">
                          <div class="metric"><dt>Codec</dt><dd><strong>{HtmlEncode(t.CodecDescription)}</strong></dd></div>
                          {(string.IsNullOrEmpty(t.Language) ? "" : $"<div class=\"metric\"><dt>Language</dt><dd>{HtmlEncode(t.Language)}</dd></div>")}
                          {(string.IsNullOrEmpty(t.Encoding) ? "" : $"<div class=\"metric\"><dt>Encoding</dt><dd>{HtmlEncode(t.Encoding)}</dd></div>")}
                        </dl>
                      """)))}
                      """
                  )}}}}</section>

                <section class="card"><h2>&#128274; HTTPS certificate <span class="pill {{{{certificateState}}}}">{{{{HtmlEncode(certificateLabel)}}}}</span></h2><dl class="grid">
                  <div class="metric"><dt>Present</dt><dd>{{{{certificate is not null}}}}</dd></div>
                  <div class="metric"><dt>Expires</dt><dd>{{{{certificate?.NotAfter.ToString("u") ?? "N/A"}}}}</dd></div>
                  <div class="metric wide"><dt>Thumbprint</dt><dd class="mono">{{{{HtmlEncode(certificate?.Thumbprint ?? "N/A")}}}}</dd></div>
                </dl><div class="divider"></div><a href="/certificate.cer">Download certificate &#8599;</a></section>

                <section class="card runtime-card"><h2>&#128296; Runtime diagnostics</h2><pre id="runtime-json">{{{{HtmlEncode(runtime)}}}}</pre></section>

                <section class="card runtime-card"><h2>&#9881; Admin actions</h2>
                  <div class="actions" style="justify-content:flex-start;margin-bottom:.6rem">
                    <button class="primary" onclick="rescanLibrary()">&#8635; Rescan video lib</button>
                    <button onclick="location.href='/remoteplay.log'">&#128196; Download log</button>
                    <button onclick="refreshRuntime()">&#8635; Refresh runtime</button>
                  </div>
                  <p id="admin-result" class="admin-result"></p>
                </section>

                <section class="card runtime-card" id="data-sync-card">
                  <h2>&#8679; Data sync to peer</h2>
                  <dl class="grid">
                    <div class="metric"><dt>Cached covers</dt><dd>{{{{artFiles}}}} images</dd></div>
                    <div class="metric"><dt>Cover cache size</dt><dd>{{{{artSizeLabel}}}}</dd></div>
                  </dl>
                  <div class="divider"></div>
                  <p style="color:var(--muted);font-size:.82rem;margin:0 0 .6rem">Push cached data to another RemotePlay instance. Only items that differ are sent &mdash; identical files are skipped automatically.</p>
                  <div style="display:flex;gap:.5rem;flex-wrap:wrap;align-items:center;margin-bottom:.8rem">
                    <input id="sync-target-url" type="url" placeholder="http://192.168.1.x:8080" style="flex:1;min-width:200px;padding:.4rem .6rem;background:rgba(0,0,0,.35);border:1px solid rgba(255,255,255,.15);border-radius:8px;color:#d8e8ff;font-size:.88rem" />
                    <select id="sync-peer-pick" style="padding:.4rem .5rem;background:rgba(0,0,0,.35);border:1px solid rgba(255,255,255,.15);border-radius:8px;color:#d8e8ff;font-size:.82rem" onchange="syncPeerPick(this)">
                      <option value="">&#8212; pick peer &#8212;</option>
                    </select>
                  </div>
                  <div style="display:grid;grid-template-columns:1fr auto;gap:.4rem .6rem;align-items:center;margin-bottom:.4rem">
                    <span style="font-size:.82rem;color:var(--muted)">&#127912; Album covers</span>
                    <button class="primary" id="sync-art-btn" onclick="startSyncItem('art')">&#8679; Sync covers</button>
                    <div id="sync-art-bar-wrap" style="display:none;grid-column:1/-1"><div style="height:5px;border-radius:3px;background:rgba(255,255,255,.1)"><div id="sync-art-fill" style="height:100%;border-radius:3px;background:var(--cyan,#22d3ee);width:0%;transition:width .3s"></div></div></div>
                    <p id="sync-art-result" class="admin-result" style="grid-column:1/-1;margin:0"></p>
                    <span style="font-size:.82rem;color:var(--muted);margin-top:.5rem">&#127911; Lyrics cache</span>
                    <button id="sync-lyrics-btn" onclick="startSyncItem('lyrics')" style="margin-top:.5rem">&#8679; Sync lyrics</button>
                    <div id="sync-lyrics-bar-wrap" style="display:none;grid-column:1/-1"><div style="height:5px;border-radius:3px;background:rgba(255,255,255,.1)"><div id="sync-lyrics-fill" style="height:100%;border-radius:3px;background:var(--cyan,#22d3ee);width:0%;transition:width .3s"></div></div></div>
                    <p id="sync-lyrics-result" class="admin-result" style="grid-column:1/-1;margin:0"></p>
                    <span style="font-size:.82rem;color:var(--muted);margin-top:.5rem">&#9201; Lyric offsets</span>
                    <button id="sync-offsets-btn" onclick="startSyncItem('offsets')" style="margin-top:.5rem">&#8679; Sync offsets</button>
                    <p id="sync-offsets-result" class="admin-result" style="grid-column:1/-1;margin:0"></p>
                  </div>
                </section>
              </div>
            </section>
            <div class="footer-note">RemotePlay health page &mdash; generated locally by this media computer &middot; <a href="/api/health" target="_blank">API JSON</a></div>
            </main>
            <script>
            async function refreshRuntime(){
              const result=document.getElementById('admin-result');
              result.textContent='Refreshing\u2026';
              try{
                const [h,d]=await Promise.all([fetch('/api/health'),fetch('/api/display-diagnostics')]);
                const health=await h.json();
                const display=await d.json();
                document.getElementById('runtime-json').textContent=JSON.stringify(health.runtime,null,2);
                result.textContent='Refreshed. Videos: '+health.indexedFiles+' | Fullscreen repair: '+display.needsFullscreenRepair+'.';
              }catch(e){result.textContent='Refresh failed: '+e;}
            }
            async function rescanLibrary(){
              const result=document.getElementById('admin-result');
              result.textContent='Starting rescan\u2026';
              try{await fetch('/api/rescan');result.textContent='Video library rescan started.';} 
              catch(e){result.textContent='Rescan failed: '+e;}
            }
            (function(){
              const peers={{{{peersJson}}}};
              const sel=document.getElementById('sync-peer-pick');
              peers.forEach(p=>{
                const o=document.createElement('option');
                o.value=p.Url||p.url;
                o.textContent=(p.Name||p.name)+' ('+((p.Url||p.url).replace(/^https?:\/\//,''))+')';
                sel.appendChild(o);
              });
            })();
            function syncPeerPick(sel){if(sel.value)document.getElementById('sync-target-url').value=sel.value;}
            function _syncEl(id){return document.getElementById(id);}
            function _showSyncFail(type,errors){
              if(!errors||!errors.length)return;
              const res=_syncEl('sync-'+type+'-result');
              const det=document.createElement('details');
              det.style.cssText='margin-top:.4rem;font-size:.78rem;color:#ffb86c';
              const sum=document.createElement('summary');
              sum.textContent='Show failure details ('+errors.length+' samples)';
              det.appendChild(sum);
              const pre=document.createElement('pre');
              pre.style.cssText='white-space:pre-wrap;margin:.3rem 0 0;font-size:.75rem;max-height:180px;overflow:auto';
              pre.textContent=errors.join('\n');
              det.appendChild(pre);
              res.parentNode.insertBefore(det,res.nextSibling);
            }
            async function startSyncItem(type){
              const url=(_syncEl('sync-target-url').value||'').trim();
              const result=_syncEl('sync-'+type+'-result');
              const btn=_syncEl('sync-'+type+'-btn');
              const fill=_syncEl('sync-'+type+'-fill');
              const barWrap=_syncEl('sync-'+type+'-bar-wrap');
              if(!url){result.textContent='Please enter a target URL.';return;}
              btn.disabled=true;
              if(barWrap){barWrap.style.display='';fill.style.width='5%';}
              result.textContent='Syncing\u2026';
              try{
                if(type==='offsets'){
                  let map={};
                  try{const raw=localStorage.getItem('remotePlayLyricOffsets');if(raw)map=JSON.parse(raw);}catch(_){}
                  const count=Object.keys(map).length;
                  if(count===0){result.textContent='No offsets saved locally yet.';btn.disabled=false;return;}
                  const r=await fetch('/api/music/lyrics/offsets/export',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({targetUrl:url,offsets:map})});
                  const d=await r.json();
                  result.textContent=d.ok?'\u2714 Done \u2014 Pushed '+count+' offset(s) to peer.':'Sync failed: '+(d.error||'unknown error');
                  btn.disabled=false;return;
                }
                const endpoint=type==='art'?'/api/music/album-art/export':'/api/music/lyrics/export';
                const r=await fetch(endpoint,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({targetUrl:url})});
                if(fill)fill.style.width='100%';
                const d=await r.json();
                if(d.ok){
                  result.textContent='\u2714 Done \u2014 Sent: '+d.sent+' | Skipped: '+d.skipped+' | Failed: '+d.failed+' | Total: '+d.total;
                  if(d.failed>0&&d.failSamples&&d.failSamples.length)_showSyncFail(type,d.failSamples);
                }else{result.textContent='Sync failed: '+(d.error||'unknown error');}
              }catch(e){result.textContent='Sync failed: '+e;}
              finally{btn.disabled=false;setTimeout(()=>{if(barWrap)barWrap.style.display='none';if(fill)fill.style.width='0%';},4000);}
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

        // If the user navigates to a specific folder that has no entries in the index yet,
        // scan it immediately on this thread so results are available right now — regardless
        // of whether the background BFS is still running or has already completed.
        if (!string.IsNullOrEmpty(folderParam) && Directory.Exists(folderParam))
        {
            // Check whether this folder already has entries in the index.
            bool alreadyIndexed;
            lock (_musicIndexGate)
                alreadyIndexed = _musicIndex.Any(f =>
                    string.Equals(Path.GetDirectoryName(f.FullPath), folderParam, StringComparison.OrdinalIgnoreCase));

            if (!alreadyIndexed)
            {
                // Quick synchronous scan of just this one directory.
                var instant = new List<MusicFile>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(folderParam, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        if (!_musicExtensions.Contains(Path.GetExtension(f))) continue;
                        instant.Add(new MusicFile(Name: Path.GetFileNameWithoutExtension(f), FullPath: f));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("MusicScanner", $"Instant browse scan failed for '{folderParam}': {ex.Message}");
                }

                if (instant.Count > 0)
                {
                    // Merge into the live index; background scan will skip this dir via _scanned.
                    lock (_musicIndexGate)
                    {
                        var merged = _musicIndex.Concat(instant).ToArray();
                        Array.Sort(merged, (a, b) =>
                            string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
                        _musicIndex = merged;
                    }
                    // Tell the background job not to re-scan this folder.
                    MusicScanJob? activeJob;
                    lock (_musicIndexGate)
                        activeJob = _activeMusicScanJob;
                    activeJob?.MarkScanned(folderParam);

                    Logger.Detail("MusicScanner", $"Instant browse scan: {instant.Count} track(s) from '{folderParam}'");
                }
            }
            else
            {
                // Folder is already indexed — still tell the background job to skip it
                // and promote it so BFS moves on faster.
                MusicScanJob? activeJob;
                lock (_musicIndexGate)
                    activeJob = _activeMusicScanJob;
                activeJob?.Prioritize(folderParam);
            }
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
                        .Where(d => !_hiddenFolderNames.Contains(Path.GetFileName(d)))
                        .OrderBy(d => string.Equals(Path.GetFileName(d), "All", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(d => d, _naturalComparer)
                        .Select(d => (object)new { name = Path.GetFileName(d), folder = d, isAll = string.Equals(Path.GetFileName(d), "All", StringComparison.OrdinalIgnoreCase) })
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
                        .Where(d => !_hiddenFolderNames.Contains(Path.GetFileName(d)))
                        .OrderBy(d => string.Equals(Path.GetFileName(d), "All", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(d => d, _naturalComparer)
                        .Select(d => (object)new { name = Path.GetFileName(d), folder = d, isAll = string.Equals(Path.GetFileName(d), "All", StringComparison.OrdinalIgnoreCase) })
                        .ToArray()
                    : [];
            }
            catch
            {
                folders = [];
            }
        }

        var files = page
            .Select(f => (object)BuildMusicFileObj(f))
            .ToArray();

        var scanDir = string.IsNullOrEmpty(folderParam) ? musicRoot : folderParam;
        object[] playlists = [];
        if (Directory.Exists(scanDir))
        {
            try
            {
                playlists = Directory.EnumerateFiles(scanDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        return string.Equals(ext, ".m3u", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ext, ".m3u8", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(f => f, _naturalComparer)
                    .Select(f =>
                    {
                        int trackCount = 0;
                        string? artist = null;
                        try
                        {
                            var lines = File.ReadLines(f).Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')).ToList();
                            trackCount = lines.Count;
                            // Peek at the first track to extract the artist tag
                            if (lines.Count > 0)
                            {
                                var firstEntry = lines[0].Trim();
                                var firstPath = Path.IsPathRooted(firstEntry)
                                    ? firstEntry
                                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, firstEntry));
                                if (File.Exists(firstPath))
                                {
                                    try
                                    {
                                        using var tagFile = TagLib.File.Create(firstPath);
                                        var performers = tagFile.Tag.AlbumArtists.Length > 0
                                            ? tagFile.Tag.AlbumArtists
                                            : tagFile.Tag.Performers;
                                        artist = performers.Length > 0 ? performers[0] : null;
                                    }
                                    catch { /* tag read failed, leave artist null */ }
                                }
                            }
                        }
                        catch { /* ignore unreadable playlists */ }
                        return (object)new { name = Path.GetFileNameWithoutExtension(f), path = WebPathHelpers.EncodePath(f), trackCount, artist };
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                Logger.Warning("MusicBrowse", $"Cannot scan playlists in '{scanDir}': {ex.Message}");
            }
        }

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new
        {
            folders,
            playlists,
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

    private void HandleMusicPlaylist(HttpListenerContext ctx)
    {
        var encodedPath = (ctx.Request.QueryString["path"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing path");
            return;
        }
        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "Playlist not found");
            return;
        }
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var tracks = new List<object>();
        try
        {
            foreach (var raw in File.ReadLines(filePath))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;
                // Resolve relative or absolute path; normalise any forward-slash separators
                var normalised = line.Replace('/', Path.DirectorySeparatorChar);
                var resolved = Path.IsPathRooted(normalised)
                    ? normalised
                    : Path.GetFullPath(Path.Combine(dir, normalised));
                if (File.Exists(resolved))
                    tracks.Add(new { path = WebPathHelpers.EncodePath(resolved), name = Path.GetFileNameWithoutExtension(resolved) });
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("MusicPlaylist", $"Error reading playlist '{filePath}': {ex.Message}");
        }
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { name, tracks }));
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
            .Select(f => (object)BuildMusicFileObj(f))
            .ToArray();

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

    private static object BuildMusicFileObj(MusicFile f)
    {
        var dir    = Path.GetDirectoryName(f.FullPath) ?? string.Empty;
        var ext    = Path.GetExtension(f.FullPath).TrimStart('.').ToLowerInvariant();

        // Fallback values from path structure
        string album    = Path.GetFileName(dir);
        string artist   = Path.GetFileName(Path.GetDirectoryName(dir)) ?? string.Empty;
        string tagTitle = string.Empty;
        int?   trackNum = null;
        int?   durationSec = null;
        string genre = string.Empty;
        int?   year  = null;
        bool   hasCover = false;

        // Try to read embedded ID3/Vorbis/etc. tags
        try
        {
            using var tfile = TagLib.File.Create(f.FullPath);
            var tag = tfile.Tag;

            if (!string.IsNullOrWhiteSpace(tag.Title))   tagTitle = tag.Title;
            if (!string.IsNullOrWhiteSpace(tag.Album))   album  = tag.Album;
            var performers = tag.AlbumArtists.Length > 0 ? tag.AlbumArtists : tag.Performers;
            if (performers.Length > 0 && !string.IsNullOrWhiteSpace(performers[0]))
                artist = performers[0];
            if (tag.Track > 0)  trackNum = (int)tag.Track;
            if (tag.Year > 0)   year     = (int)tag.Year;
            if (tag.Genres.Length > 0 && !string.IsNullOrWhiteSpace(tag.Genres[0]))
                genre = tag.Genres[0];
            hasCover = tfile.Tag.Pictures.Length > 0
                    || FolderHasCoverImage(dir);
            durationSec = (int)Math.Round(tfile.Properties.Duration.TotalSeconds);
        }
        catch
        {
            // TagLib can't read this file — fall back to path-based values
            var tnMatch = System.Text.RegularExpressions.Regex.Match(f.Name, @"^(\d{1,3})[.\s\-]");
            if (tnMatch.Success && int.TryParse(tnMatch.Groups[1].Value, out var tn))
                trackNum = tn;
            hasCover = FolderHasCoverImage(dir);
        }

        return new
        {
            name        = f.Name,
            tagTitle,
            path        = WebPathHelpers.EncodePath(f.FullPath),
            folder      = Path.GetFileName(dir),
            ext,
            album,
            artist,
            trackNum,
            durationSec,
            genre,
            year,
            hasCover
        };
    }

    private static readonly string[] _coverNames =
        ["cover.jpg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png", "album.jpg", "album.png"];

    private static bool FolderHasCoverImage(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (var name in _coverNames)
            if (System.IO.File.Exists(Path.Combine(dir, name))) return true;
        return false;
    }

    private void HandleMusicCover(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        { TrySendResponse(ctx, 400, "text/plain", "missing path"); return; }

        string fullPath;
        try   { fullPath = WebPathHelpers.DecodePath(encodedPath); }
        catch { TrySendResponse(ctx, 400, "text/plain", "bad path"); return; }

        var musicRoot = _config.ResolvedMusicPath;
        if (!WebPathHelpers.IsUnderRoot(fullPath, musicRoot))
        { TrySendResponse(ctx, 403, "text/plain", "forbidden"); return; }

        // 1. Try embedded picture
        try
        {
            using var tfile = TagLib.File.Create(fullPath);
            var pic = tfile.Tag.Pictures.FirstOrDefault();
            if (pic is not null)
            {
                ctx.Response.ContentType = string.IsNullOrEmpty(pic.MimeType) ? "image/jpeg" : pic.MimeType;
                ctx.Response.ContentLength64 = pic.Data.Data.Length;
                ctx.Response.OutputStream.Write(pic.Data.Data, 0, pic.Data.Data.Length);
                ctx.Response.OutputStream.Close();
                return;
            }
        }
        catch { /* fall through to folder art */ }

        // 2. Try folder cover image
        var dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        foreach (var name in _coverNames)
        {
            var candidate = Path.Combine(dir, name);
            if (!System.IO.File.Exists(candidate)) continue;
            var mime = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            try
            {
                var bytes = System.IO.File.ReadAllBytes(candidate);
                ctx.Response.ContentType = mime;
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
                return;
            }
            catch { }
        }

        TrySendResponse(ctx, 404, "text/plain", "no cover");
    }

    // ── Album Art Cache ──────────────────────────────────────────────────────────
    // In-memory: null = confirmed-miss, byte[] = image bytes (already on disk too)
    // Disk:      %AppData%\RemotePlay\AlbumArtCache\<safeKey>.jpg
    // ─────────────────────────────────────────────────────────────────────────────
    private static readonly Dictionary<string, byte[]?> _albumArtCache = new(StringComparer.OrdinalIgnoreCase);
    // On first use, purge stale zero-byte miss-sentinels written by older logic (e.g. before release-group fallback)
    private static int _albumArtMissPurged = 0;
    private static void PurgeStaleAlbumArtMisses()
    {
        if (Interlocked.Exchange(ref _albumArtMissPurged, 1) != 0) return;
        try
        {
            var dir = Path.Combine(AppPaths.UserDataDirectory, "AlbumArtCache");
            if (!Directory.Exists(dir)) return;
            int count = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*.jpg"))
            {
                if (new FileInfo(f).Length == 0) { File.Delete(f); count++; }
            }
            if (count > 0) Logger.Detail("AlbumArt", $"Purged {count} stale miss-sentinel(s) from disk cache");
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Miss-sentinel purge failed: {ex.Message}");
        }
    }
    private static readonly SemaphoreSlim _albumArtLock = new(1, 1);
    // In-flight deduplication: if N requests arrive for the same key simultaneously,
    // only one fetch runs; the rest await the same Task.
    private static readonly Dictionary<string, Task<byte[]?>> _albumArtInflight = new(StringComparer.OrdinalIgnoreCase);
    // MusicBrainz rate-limit: max 1 request per second
    private static readonly SemaphoreSlim _mbRateLimit = new(1, 1);
    private static long _mbLastRequestTick = 0;
    private static readonly System.Net.Http.HttpClient _albumArtHttpClient = CreateAlbumArtHttpClient();

    private static string AlbumArtCacheDir =>
        Path.Combine(AppPaths.UserDataDirectory, "AlbumArtCache");

    private static System.Net.Http.HttpClient CreateAlbumArtHttpClient()
    {
        var client = new System.Net.Http.HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0 (album-art-lookup)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>Returns a filesystem-safe filename for a cache key.</summary>
    private static string AlbumArtCacheFile(string cacheKey)
    {
        // Replace characters that are invalid in filenames
        var safe = string.Concat(cacheKey.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(AlbumArtCacheDir, safe + ".jpg");
    }

    // ── Album art export / import ──────────────────────────────────────────────

    /// <summary>
    /// Accepts a JPEG body and stores it in the local AlbumArtCache under the provided key.
    /// Called by a remote server's export push. No auth required beyond network access.
    /// Query params: key (the cache key, e.g. "artist|album")
    /// </summary>
    // -----------------------------------------------------------------------
    // Lyric offsets persistence (GET = load, POST = save)
    // -----------------------------------------------------------------------

    /// <summary>
    /// GET  /api/music/lyrics/offsets  — returns the stored offset map as JSON.
    /// POST /api/music/lyrics/offsets  — accepts a JSON object and writes it to disk.
    /// </summary>
    private static async Task HandleMusicLyricsOffsetsAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var path = AppPaths.LyricOffsetsFile;
            if (!File.Exists(path))
            {
                TrySendResponse(ctx, 200, "application/json", "{}");
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                TrySendResponse(ctx, 200, "application/json", json);
            }
            catch (Exception ex)
            {
                Logger.Warning("Lyrics", $"Failed reading offsets file: {ex.Message}");
                TrySendResponse(ctx, 500, "application/json", "{\"error\":\"read failed\"}");
            }

            return;
        }

        if (ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
                var body = await sr.ReadToEndAsync().ConfigureAwait(false);

                // Validate it is parseable JSON before writing
                using var _ = JsonDocument.Parse(body);

                await File.WriteAllTextAsync(AppPaths.LyricOffsetsFile, body).ConfigureAwait(false);
                TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
            }
            catch (JsonException)
            {
                TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid JSON\"}");
            }
            catch (Exception ex)
            {
                Logger.Warning("Lyrics", $"Failed writing offsets file: {ex.Message}");
                TrySendResponse(ctx, 500, "application/json", "{\"error\":\"write failed\"}");
            }

            return;
        }

        TrySendResponse(ctx, 405, "application/json", "{\"error\":\"method not allowed\"}");
    }

    /// <summary>
    /// Server-side relay: reads offsets from the request body and POSTs them to targetUrl/api/music/lyrics/offsets.
    /// Body (JSON): { "targetUrl": "http://host:port", "offsets": {...} }
    /// This avoids browser-to-peer cross-origin issues by routing the request through the local server.
    /// </summary>
    private static async Task HandleMusicLyricsOffsetsExportAsync(HttpListenerContext ctx)
    {
        if (!ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            TrySendResponse(ctx, 405, "application/json", "{\"error\":\"method not allowed\"}");
            return;
        }

        string targetUrl;
        JsonElement offsets;
        try
        {
            using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
            var body = await sr.ReadToEndAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            targetUrl = (doc.RootElement.TryGetProperty("targetUrl", out var tu) ? tu.GetString() : null) ?? string.Empty;
            offsets = doc.RootElement.TryGetProperty("offsets", out var o) ? o.Clone() : default;
        }
        catch
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid JSON body\"}");
            return;
        }

        targetUrl = targetUrl.TrimEnd('/');
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != "http" && targetUri.Scheme != "https"))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid targetUrl\"}");
            return;
        }

        if (offsets.ValueKind != JsonValueKind.Object)
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"offsets must be a JSON object\"}");
            return;
        }

        var count = 0;
        foreach (var _ in offsets.EnumerateObject()) count++;
        if (count == 0)
        {
            TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, pushed = 0 }));
            return;
        }

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0 (offsets-export)");
            var json = offsets.GetRawText();
            using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{targetUrl}/api/music/lyrics/offsets", content).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                Logger.Info("Lyrics", $"Offsets export: pushed {count} offset(s) to {targetUrl}");
                TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { ok = true, pushed = count }));
            }
            else
            {
                var errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                Logger.Warning("Lyrics", $"Offsets export: peer returned HTTP {(int)resp.StatusCode}: {errBody.Trim()}");
                TrySendResponse(ctx, 200, "application/json",
                    JsonSerializer.Serialize(new { ok = false, error = $"Peer returned HTTP {(int)resp.StatusCode}" }));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("Lyrics", $"Offsets export failed: {ex.Message}");
            TrySendResponse(ctx, 500, "application/json", JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    // -----------------------------------------------------------------------
    // Lyric cache file export / import (peer-sync)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pushes every .json file in the lyrics cache to the target server's
    /// /api/music/lyrics/import endpoint, skipping files that are byte-for-byte identical
    /// (compared by file size; the receiver checks the ETag header).
    /// Body (JSON): { "targetUrl": "http://host:port" }
    /// </summary>
    private static async Task HandleMusicLyricsExportAsync(HttpListenerContext ctx)
    {
        string targetUrl;
        try
        {
            using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
            var body = await sr.ReadToEndAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            targetUrl = (doc.RootElement.TryGetProperty("targetUrl", out var el)
                ? el.GetString() : null) ?? string.Empty;
        }
        catch
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid JSON body\"}");
            return;
        }

        targetUrl = targetUrl.TrimEnd('/');
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != "http" && targetUri.Scheme != "https"))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid targetUrl\"}");
            return;
        }

        var cacheDir = AppPaths.LyricsCacheDirectory;
        if (!Directory.Exists(cacheDir))
        {
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { ok = true, sent = 0, skipped = 0, failed = 0, total = 0 }));
            return;
        }

        var files = Directory.EnumerateFiles(cacheDir, "*.json")
            .Where(f => new FileInfo(f).Length > 0)
            .ToArray();

        Logger.Info("Lyrics", $"Export: pushing {files.Length} cached lyric files to {targetUrl}");

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0 (lyrics-export)");

        int sent = 0, skipped = 0, failed = 0;
        var importBase = $"{targetUrl}/api/music/lyrics/import";
        var failReasons = new System.Collections.Generic.List<string>();
        const int InterRequestDelayMs = 30;
        const int MaxRetries = 3;

        // First, ask the target which files it already has and their sizes so we can skip identical ones.
        System.Collections.Generic.Dictionary<string, long>? remoteIndex = null;
        try
        {
            var idxRes = await http.GetAsync($"{targetUrl}/api/music/lyrics/import?index=1").ConfigureAwait(false);
            if (idxRes.IsSuccessStatusCode)
            {
                var idxJson = await idxRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var idxDoc = JsonDocument.Parse(idxJson);
                remoteIndex = new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in idxDoc.RootElement.EnumerateObject())
                    remoteIndex[prop.Name] = prop.Value.GetInt64();
            }
        }
        catch { /* remote may not support the index endpoint yet; proceed without skipping */ }

        foreach (var file in files)
        {
            var key = Path.GetFileNameWithoutExtension(file);
            try
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);

                // Skip if the remote already has a file with the same size (byte-identical proxy).
                if (remoteIndex is not null
                    && remoteIndex.TryGetValue(key, out var remoteSize)
                    && remoteSize == bytes.LongLength)
                {
                    skipped++;
                    continue;
                }

                var url = $"{importBase}?key={Uri.EscapeDataString(key)}";

                System.Net.Http.HttpResponseMessage resp;
                int retryDelayMs = 2000;
                int attempt = 0;
                while (true)
                {
                    using var content = new System.Net.Http.ByteArrayContent(bytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    resp = await http.PostAsync(url, content).ConfigureAwait(false);

                    if ((int)resp.StatusCode != 429 || attempt >= MaxRetries)
                        break;

                    if (resp.Headers.RetryAfter?.Delta is { } delta)
                        retryDelayMs = (int)delta.TotalMilliseconds + 200;

                    Logger.Warning("Lyrics", $"Export: 429 for '{key}', retrying in {retryDelayMs}ms (attempt {attempt + 1}/{MaxRetries})");
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                    retryDelayMs = Math.Min(retryDelayMs * 2, 30_000);
                    attempt++;
                }

                if (resp.IsSuccessStatusCode)
                {
                    sent++;
                }
                else
                {
                    var reason = $"HTTP {(int)resp.StatusCode} for '{key}': {(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim()}";
                    Logger.Warning("Lyrics", $"Export: {reason}");
                    if (failReasons.Count < 10) failReasons.Add(reason);
                    failed++;
                }
            }
            catch (Exception ex)
            {
                var reason = $"Exception for '{key}': {ex.Message}";
                Logger.Warning("Lyrics", $"Export: {reason}");
                if (failReasons.Count < 10) failReasons.Add(reason);
                failed++;
            }

            await Task.Delay(InterRequestDelayMs).ConfigureAwait(false);
        }

        Logger.Info("Lyrics", $"Export complete: sent={sent} skipped={skipped} failed={failed} total={files.Length}");
        TrySendResponse(ctx, 200, "application/json",
            JsonSerializer.Serialize(new { ok = true, sent, skipped, failed, total = files.Length, failSamples = failReasons }));
    }

    /// <summary>
    /// Receives a lyrics cache file from another instance.
    /// GET  ?index=1  — returns a JSON object {filename: fileSize} for diff-checking.
    /// POST ?key=...  — writes the body as a .json file in the lyrics cache directory.
    /// </summary>
    private static async Task HandleMusicLyricsImportAsync(HttpListenerContext ctx)
    {
        // Index request: return a map of filename → file size so exporter can skip identical items.
        if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && ctx.Request.QueryString["index"] == "1")
        {
            var cacheDir = AppPaths.LyricsCacheDirectory;
            var index = new System.Collections.Generic.Dictionary<string, long>();
            if (Directory.Exists(cacheDir))
            {
                foreach (var f in Directory.EnumerateFiles(cacheDir, "*.json"))
                    index[Path.GetFileNameWithoutExtension(f)] = new FileInfo(f).Length;
            }

            TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(index));
            return;
        }

        var key = (ctx.Request.QueryString["key"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"missing key\"}"); return; }

        if (ctx.Request.ContentLength64 <= 0 || ctx.Request.ContentLength64 > 2 * 1024 * 1024)
        { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid content length\"}"); return; }

        try
        {
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            var bytes = ms.ToArray();

            if (bytes.Length == 0)
            { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"empty body\"}"); return; }

            // Strip UTF-8 BOM if present (older cache files may have been written with BOM)
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                bytes = bytes[3..];

            // Basic validation: must be JSON
            using var _ = JsonDocument.Parse(bytes);

            Directory.CreateDirectory(AppPaths.LyricsCacheDirectory);
            var destFile = Path.Combine(AppPaths.LyricsCacheDirectory, key + ".json");
            await File.WriteAllBytesAsync(destFile, bytes).ConfigureAwait(false);

            Logger.Detail("Lyrics", $"Import: stored '{key}' ({bytes.Length} bytes)");
            TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
        }
        catch (JsonException)
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"not valid JSON\"}");
        }
        catch (Exception ex)
        {
            Logger.Warning("Lyrics", $"Import failed for key '{key}': {ex.Message}");
            TrySendResponse(ctx, 500, "application/json", "{\"error\":\"write failed\"}");
        }
    }

    private static async Task HandleMusicAlbumArtImportAsync(HttpListenerContext ctx)
    {
        var key = (ctx.Request.QueryString["key"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"missing key\"}"); return; }

        if (ctx.Request.ContentLength64 <= 0 || ctx.Request.ContentLength64 > 5 * 1024 * 1024)
        { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid content length\"}"); return; }

        try
        {
            using var ms = new System.IO.MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
            var bytes = ms.ToArray();

            if (bytes.Length == 0)
            { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"empty body\"}"); return; }

            // Validate it looks like a JPEG (FF D8 FF)
            if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
            { TrySendResponse(ctx, 400, "application/json", "{\"error\":\"not a JPEG\"}"); return; }

            Directory.CreateDirectory(AlbumArtCacheDir);
            var destFile = AlbumArtCacheFile(key);
            await File.WriteAllBytesAsync(destFile, bytes).ConfigureAwait(false);

            // Update in-memory cache so it's immediately available
            lock (_albumArtCache) _albumArtCache[key] = bytes;

            Logger.Detail("AlbumArt", $"Import: stored '{key}' ({bytes.Length} bytes)");
            TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Import failed for key '{key}': {ex.Message}");
            TrySendResponse(ctx, 500, "application/json", "{\"error\":\"write failed\"}");
        }
    }

    /// <summary>
    /// Iterates AlbumArtCache on disk and pushes every non-empty JPEG to the target server's
    /// /api/music/album-art/import endpoint. Returns a JSON summary with totals.
    /// Body (JSON): { "targetUrl": "http://host:port" }
    /// </summary>
    private static async Task HandleMusicAlbumArtExportAsync(HttpListenerContext ctx)
    {
        string targetUrl;
        try
        {
            using var sr = new System.IO.StreamReader(ctx.Request.InputStream);
            var body = await sr.ReadToEndAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            targetUrl = (doc.RootElement.TryGetProperty("targetUrl", out var el)
                ? el.GetString() : null) ?? string.Empty;
        }
        catch
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid JSON body\"}");
            return;
        }

        targetUrl = targetUrl.TrimEnd('/');
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)
            || (targetUri.Scheme != "http" && targetUri.Scheme != "https"))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid targetUrl\"}");
            return;
        }

        if (!Directory.Exists(AlbumArtCacheDir))
        {
            TrySendResponse(ctx, 200, "application/json",
                JsonSerializer.Serialize(new { ok = true, sent = 0, skipped = 0, failed = 0, total = 0 }));
            return;
        }

        var files = Directory.EnumerateFiles(AlbumArtCacheDir, "*.jpg")
            .Where(f => new FileInfo(f).Length > 0)
            .ToArray();

        Logger.Info("AlbumArt", $"Export: pushing {files.Length} cached images to {targetUrl}");

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0 (album-art-export)");

        int sent = 0, skipped = 0, failed = 0;
        var importBase = $"{targetUrl}/api/music/album-art/import";
        var failReasons = new System.Collections.Generic.List<string>();
        // Throttle to ~20 req/s to stay well under the target's default 300-req/10-s rate limit.
        const int InterRequestDelayMs = 50;
        const int MaxRetries          = 3;

        foreach (var file in files)
        {
            // Derive the original cache key from the filename (strip .jpg extension)
            var fileName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
                { skipped++; continue; }

                var url = $"{importBase}?key={Uri.EscapeDataString(fileName)}";

                System.Net.Http.HttpResponseMessage resp;
                int retryDelayMs = 2000;
                int attempt = 0;
                while (true)
                {
                    using var content = new System.Net.Http.ByteArrayContent(bytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    resp = await http.PostAsync(url, content).ConfigureAwait(false);

                    if ((int)resp.StatusCode != 429 || attempt >= MaxRetries)
                        break;

                    // Honour Retry-After if present, otherwise use exponential back-off.
                    if (resp.Headers.RetryAfter?.Delta is { } delta)
                        retryDelayMs = (int)delta.TotalMilliseconds + 200;

                    Logger.Warning("AlbumArt", $"Export: 429 for '{fileName}', retrying in {retryDelayMs}ms (attempt {attempt + 1}/{MaxRetries})");
                    await Task.Delay(retryDelayMs).ConfigureAwait(false);
                    retryDelayMs = Math.Min(retryDelayMs * 2, 30_000);
                    attempt++;
                }

                if (resp.IsSuccessStatusCode)
                {
                    sent++;
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var reason = $"HTTP {(int)resp.StatusCode} for '{fileName}': {body.Trim()}";
                    Logger.Warning("AlbumArt", $"Export: {reason}");
                    if (failReasons.Count < 10) failReasons.Add(reason);
                    failed++;
                }
            }
            catch (Exception ex)
            {
                var reason = $"Exception for '{fileName}': {ex.Message}";
                Logger.Warning("AlbumArt", $"Export: {reason}");
                if (failReasons.Count < 10) failReasons.Add(reason);
                failed++;
            }

            await Task.Delay(InterRequestDelayMs).ConfigureAwait(false);
        }

        Logger.Info("AlbumArt", $"Export complete: sent={sent} skipped={skipped} failed={failed} total={files.Length}");
        TrySendResponse(ctx, 200, "application/json",
            JsonSerializer.Serialize(new { ok = true, sent, skipped, failed, total = files.Length, failSamples = failReasons }));
    }

    /// <summary>Proxy-fetches album art from MusicBrainz/CAA. Cached in memory + on disk.</summary>
    private async Task HandleMusicAlbumArtAsync(HttpListenerContext ctx)
    {
        // One-time purge of zero-byte miss-sentinels written before the release-group fallback was added
        PurgeStaleAlbumArtMisses();

        var album  = (ctx.Request.QueryString["album"]  ?? string.Empty).Trim();
        var artist = (ctx.Request.QueryString["artist"] ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(album))
        { TrySendResponse(ctx, 400, "text/plain", "missing album"); return; }

        // Cap the wait time so a navigating-away client doesn't hold the queue open indefinitely.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = cts.Token;

        var cacheKey  = $"{artist}|{album}".ToLowerInvariant();
        var cacheFile = AlbumArtCacheFile(cacheKey);

        // 1. Fast path — in-memory cache
        await _albumArtLock.WaitAsync(ct).ConfigureAwait(false);
        bool hasCached = _albumArtCache.TryGetValue(cacheKey, out var cached);
        _albumArtLock.Release();

        if (hasCached)
        {
            if (cached is null) { TrySendResponse(ctx, 404, "text/plain", "not found"); return; }
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.ContentLength64 = cached.Length;
            await ctx.Response.OutputStream.WriteAsync(cached, ct).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
            return;
        }

        // 2. Disk cache — load without going to the network
        if (File.Exists(cacheFile))
        {
            try
            {
                var diskBytes = await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false);
                if (diskBytes.Length > 0)
                {
                    await _albumArtLock.WaitAsync(ct).ConfigureAwait(false);
                    _albumArtCache[cacheKey] = diskBytes;
                    _albumArtLock.Release();
                    Logger.Detail("AlbumArt", $"Served from disk cache: '{cacheKey}'");
                    ctx.Response.ContentType = "image/jpeg";
                    ctx.Response.ContentLength64 = diskBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(diskBytes, ct).ConfigureAwait(false);
                    ctx.Response.OutputStream.Close();
                    return;
                }
                // Zero-byte file = persisted miss — don't hit network again
                await _albumArtLock.WaitAsync(ct).ConfigureAwait(false);
                _albumArtCache[cacheKey] = null;
                _albumArtLock.Release();
                TrySendResponse(ctx, 404, "text/plain", "not found");
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Warning("AlbumArt", $"Failed to read disk cache '{cacheFile}': {ex.Message}");
            }
        }

        // 3. Fetch from MusicBrainz / Cover Art Archive.
        //    In-flight deduplication: if another request is already fetching this key, await its
        //    Task instead of firing a second network fetch.  This is the primary fix for the hang
        //    when many playlist cards load at the same time — only one outbound chain runs per key.
        Task<byte[]?> fetchTask;
        bool isOwner = false;
        await _albumArtLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_albumArtInflight.TryGetValue(cacheKey, out var existing))
            {
                fetchTask = existing;   // join the already-running fetch
            }
            else
            {
                fetchTask = FetchAndCacheAlbumArtAsync(artist, album, cacheKey, cacheFile);
                _albumArtInflight[cacheKey] = fetchTask;
                isOwner = true;
            }
        }
        finally
        {
            _albumArtLock.Release();
        }

        byte[]? imageBytes;
        try
        {
            imageBytes = await fetchTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;  // client navigated away — drop silently
        }
        finally
        {
            // Owner cleans up the inflight entry once the task finishes
            if (isOwner)
            {
                await _albumArtLock.WaitAsync().ConfigureAwait(false);
                _albumArtInflight.Remove(cacheKey);
                _albumArtLock.Release();
            }
        }

        if (imageBytes is null) { TrySendResponse(ctx, 404, "text/plain", "not found"); return; }

        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.ContentLength64 = imageBytes.Length;
        try
        {
            await ctx.Response.OutputStream.WriteAsync(imageBytes, ct).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Performs the actual MusicBrainz/CAA network fetch and persists the result to disk and
    /// in-memory cache.  Extracted so multiple concurrent requests for the same key can share
    /// a single Task via <c>_albumArtInflight</c>.
    /// </summary>
    private static async Task<byte[]?> FetchAndCacheAlbumArtAsync(
        string artist, string album, string cacheKey, string cacheFile)
    {
        byte[]? imageBytes = null;
        bool fetchWasTransient = false;
        try
        {
            imageBytes = await FetchAlbumArtFromMusicBrainzAsync(artist, album).ConfigureAwait(false);
        }
        catch (AlbumArtTransientException ex)
        {
            Logger.Warning("AlbumArt", $"Fetch failed (transient) for '{artist} - {album}': {ex.Message}");
            fetchWasTransient = true;
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Fetch failed for '{artist} - {album}': {ex.Message}");
        }

        // Persist to disk
        //   image found        → write bytes (positive hit)
        //   permanent miss     → write 0-byte sentinel so we don't retry
        //   transient failure  → don't write; allow retry on next request
        if (imageBytes is not null)
        {
            try
            {
                Directory.CreateDirectory(AlbumArtCacheDir);
                await File.WriteAllBytesAsync(cacheFile, imageBytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning("AlbumArt", $"Failed to write disk cache '{cacheFile}': {ex.Message}");
            }
        }
        else if (!fetchWasTransient)
        {
            try
            {
                Directory.CreateDirectory(AlbumArtCacheDir);
                await File.WriteAllBytesAsync(cacheFile, []).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning("AlbumArt", $"Failed to write miss sentinel '{cacheFile}': {ex.Message}");
            }
        }

        // Update in-memory cache (don't cache transient failures)
        if (!fetchWasTransient)
        {
            await _albumArtLock.WaitAsync().ConfigureAwait(false);
            _albumArtCache[cacheKey] = imageBytes;
            _albumArtLock.Release();
        }

        return imageBytes;
    }

    /// <summary>Thrown when the failure is transient (rate-limit/timeout) — must not be persisted as a miss sentinel.</summary>
    private sealed class AlbumArtTransientException(string message) : Exception(message) { }

    /// <summary>Enforces MusicBrainz 1-request-per-second policy before each HTTP call.</summary>
    private static async Task ThrottleMusicBrainzAsync(CancellationToken ct = default)
    {
        await _mbRateLimit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now   = Environment.TickCount64;
            var delta = now - Interlocked.Read(ref _mbLastRequestTick);
            if (delta >= 0 && delta < 1100)
                await Task.Delay((int)(1100 - delta), ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _mbLastRequestTick, Environment.TickCount64);
        }
        finally
        {
            _mbRateLimit.Release();
        }
    }

    private static async Task<byte[]?> FetchAlbumArtFromMusicBrainzAsync(string artist, string album,
        CancellationToken ct = default)
    {
        // Step 1: Search MusicBrainz for matching releases (up to 5), including release-group ids
        var searchTerms = string.IsNullOrWhiteSpace(artist)
            ? Uri.EscapeDataString($"release:\"{album}\"")
            : Uri.EscapeDataString($"release:\"{album}\" AND artist:\"{artist}\"");

        var searchUrl = $"https://musicbrainz.org/ws/2/release/?query={searchTerms}&limit=5&fmt=json&inc=release-groups";
        Logger.Detail("AlbumArt", $"MusicBrainz search: {searchUrl}");

        await ThrottleMusicBrainzAsync(ct).ConfigureAwait(false);

        System.Net.Http.HttpResponseMessage searchResponse;
        try
        {
            searchResponse = await _albumArtHttpClient.GetAsync(searchUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new AlbumArtTransientException($"MB search request failed: {ex.Message}");
        }

        if ((int)searchResponse.StatusCode == 503 || (int)searchResponse.StatusCode == 429)
            throw new AlbumArtTransientException($"MB search returned {(int)searchResponse.StatusCode}");

        if (!searchResponse.IsSuccessStatusCode)
        {
            Logger.Detail("AlbumArt", $"MB search non-success {(int)searchResponse.StatusCode} for '{artist} - {album}'");
            return null;
        }

        var searchJson = await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var (releaseIds, releaseGroupIds) = ExtractMusicBrainzIds(searchJson);

        if (releaseIds.Count == 0 && releaseGroupIds.Count == 0)
        {
            Logger.Detail("AlbumArt", $"No MBID found for '{artist} - {album}'");
            return null;
        }
        Logger.Detail("AlbumArt", $"Found {releaseIds.Count} release(s) + {releaseGroupIds.Count} release-group(s) for '{artist} - {album}'");

        // Step 2a: Try each release MBID against Cover Art Archive
        var result = await TryCaaUrlsAsync(
            releaseIds.Select(id => $"https://coverartarchive.org/release/{id}/front-500"),
            artist, album, ct).ConfigureAwait(false);
        if (result is not null) return result;

        // Step 2b: Fall back to release-group CAA endpoint (covers the common case where
        //          individual releases have no art but the release group does)
        result = await TryCaaUrlsAsync(
            releaseGroupIds.Select(id => $"https://coverartarchive.org/release-group/{id}/front-500"),
            artist, album, ct).ConfigureAwait(false);
        if (result is not null) return result;

        // Step 3: Recording search — finds the parent album when the title is a single track,
        //         not an album/EP name.  E.g. "Three Drinks Behind" by George Strait exists as
        //         a single release with no CAA art, but the same recording appears on the album
        //         "Cowboys and Dreamers" which does have CAA art.
        result = await FetchAlbumArtViaRecordingSearchAsync(artist, album, ct).ConfigureAwait(false);
        if (result is not null) return result;

        Logger.Detail("AlbumArt", $"No CAA cover found for '{artist} - {album}'");
        return null;
    }

    /// <summary>
    /// Searches MusicBrainz by recording (track) name, collects the release-groups of every
    /// release the recording appears on, then tries Cover Art Archive on those groups.
    /// Results are ordered so Album release-groups are tried before EP, then Single.
    /// </summary>
    private static async Task<byte[]?> FetchAlbumArtViaRecordingSearchAsync(string artist, string trackName,
        CancellationToken ct = default)
    {
        var searchTerms = string.IsNullOrWhiteSpace(artist)
            ? Uri.EscapeDataString($"recording:\"{trackName}\"")
            : Uri.EscapeDataString($"recording:\"{trackName}\" AND artist:\"{artist}\"");

        var searchUrl = $"https://musicbrainz.org/ws/2/recording/?query={searchTerms}&limit=10&fmt=json&inc=releases+release-groups";
        Logger.Detail("AlbumArt", $"MB recording search: {searchUrl}");

        await ThrottleMusicBrainzAsync(ct).ConfigureAwait(false);

        System.Net.Http.HttpResponseMessage searchResponse;
        try
        {
            searchResponse = await _albumArtHttpClient.GetAsync(searchUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new AlbumArtTransientException($"MB recording search failed: {ex.Message}");
        }

        if ((int)searchResponse.StatusCode == 503 || (int)searchResponse.StatusCode == 429)
            throw new AlbumArtTransientException($"MB recording search returned {(int)searchResponse.StatusCode}");

        if (!searchResponse.IsSuccessStatusCode)
        {
            Logger.Detail("AlbumArt", $"MB recording search non-success {(int)searchResponse.StatusCode} for '{artist} - {trackName}'");
            return null;
        }

        var json = await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var releaseGroupIds = ExtractReleaseGroupsFromRecordingSearch(json);

        if (releaseGroupIds.Count == 0)
        {
            Logger.Detail("AlbumArt", $"No release-groups found via recording search for '{artist} - {trackName}'");
            return null;
        }

        Logger.Detail("AlbumArt", $"Recording search found {releaseGroupIds.Count} release-group(s) for '{artist} - {trackName}'");

        return await TryCaaUrlsAsync(
            releaseGroupIds.Select(id => $"https://coverartarchive.org/release-group/{id}/front-500"),
            artist, trackName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a MusicBrainz recording-search JSON response and returns release-group IDs
    /// ordered by primary-type preference: Album first, then EP, then others (e.g. Single).
    /// Duplicate release-group IDs are removed.
    /// </summary>
    private static List<string> ExtractReleaseGroupsFromRecordingSearch(string json)
    {
        // MB JSON structure:
        // { "recordings": [ { "releases": [ { "id": "...", "release-group": { "id": "...", "primary-type": "Album" } } ] } ] }
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var albums  = new List<string>();
        var eps     = new List<string>();
        var others  = new List<string>();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recordings", out var recordings)) return albums;

            foreach (var recording in recordings.EnumerateArray())
            {
                if (!recording.TryGetProperty("releases", out var releases)) continue;
                foreach (var release in releases.EnumerateArray())
                {
                    if (!release.TryGetProperty("release-group", out var rg)) continue;
                    if (!rg.TryGetProperty("id", out var idProp)) continue;
                    var rgId = idProp.GetString() ?? string.Empty;
                    if (string.IsNullOrEmpty(rgId) || !seen.Add(rgId)) continue;

                    var primaryType = rg.TryGetProperty("primary-type", out var pt)
                        ? (pt.GetString() ?? string.Empty)
                        : string.Empty;

                    if (string.Equals(primaryType, "Album", StringComparison.OrdinalIgnoreCase))
                        albums.Add(rgId);
                    else if (string.Equals(primaryType, "EP", StringComparison.OrdinalIgnoreCase))
                        eps.Add(rgId);
                    else
                        others.Add(rgId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Failed to parse MB recording JSON: {ex.Message}");
        }

        // Prefer Album > EP > Single/other
        albums.AddRange(eps);
        albums.AddRange(others);
        return albums;
    }

    /// <summary>Tries a sequence of CAA URLs, returning the first successful image bytes.</summary>
    private static async Task<byte[]?> TryCaaUrlsAsync(IEnumerable<string> urls, string artist, string album,
        CancellationToken ct = default)
    {
        foreach (var coverUrl in urls)
        {
            Logger.Detail("AlbumArt", $"CAA fetch: {coverUrl}");

            System.Net.Http.HttpResponseMessage coverResponse;
            try
            {
                coverResponse = await _albumArtHttpClient.GetAsync(coverUrl, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new AlbumArtTransientException($"CAA request failed: {ex.Message}");
            }

            if ((int)coverResponse.StatusCode == 503 || (int)coverResponse.StatusCode == 429)
                throw new AlbumArtTransientException($"CAA returned {(int)coverResponse.StatusCode}");

            if (coverResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Detail("AlbumArt", $"CAA 404 for {coverUrl}, trying next");
                continue;
            }

            if (!coverResponse.IsSuccessStatusCode)
            {
                Logger.Detail("AlbumArt", $"CAA {(int)coverResponse.StatusCode} for {coverUrl}, trying next");
                continue;
            }

            var imageBytes = await coverResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (imageBytes.Length > 0)
            {
                Logger.Detail("AlbumArt", $"Got {imageBytes.Length} bytes for '{artist} - {album}' via {coverUrl}");
                return imageBytes;
            }
        }
        return null;
    }

    /// <summary>
    /// Parses a MusicBrainz release-search JSON response and returns
    /// (releaseIds, releaseGroupIds) — both in result order, deduplicated.
    /// </summary>
    private static (List<string> ReleaseIds, List<string> ReleaseGroupIds) ExtractMusicBrainzIds(string json)
    {
        // MB JSON structure (simplified):
        // { "releases": [ { "id": "<release-mbid>", "release-group": { "id": "<rg-mbid>" }, ... } ] }
        var releaseIds      = new List<string>();
        var releaseGroupIds = new List<string>();
        var seenRg          = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("releases", out var releases)) return (releaseIds, releaseGroupIds);

            foreach (var release in releases.EnumerateArray())
            {
                if (release.TryGetProperty("id", out var idProp))
                    releaseIds.Add(idProp.GetString() ?? string.Empty);

                if (release.TryGetProperty("release-group", out var rg) &&
                    rg.TryGetProperty("id", out var rgId))
                {
                    var rgIdStr = rgId.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(rgIdStr) && seenRg.Add(rgIdStr))
                        releaseGroupIds.Add(rgIdStr);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning("AlbumArt", $"Failed to parse MB JSON: {ex.Message}");
        }

        releaseIds.RemoveAll(string.IsNullOrEmpty);
        return (releaseIds, releaseGroupIds);
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
            currentPath   = pb.CurrentPath is not null ? WebPathHelpers.EncodePath(pb.CurrentPath) : null,
            title         = pb.Title,
            artist        = pb.Artist,
            position      = pb.Position,
            duration      = pb.Duration,
            playbackError = pb.LastError,
            eqPreset      = pb.EqPreset,
            reverbPreset  = pb.ReverbPreset
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
        double startPos = 0;
        var posParam = ctx.Request.QueryString["pos"];
        if (!string.IsNullOrEmpty(posParam))
            MediaControlValueParser.TryParseDouble(posParam, out startPos);
        var recordOnly = string.Equals(ctx.Request.QueryString["recordOnly"], "1", StringComparison.Ordinal);
        var skipRecord = string.Equals(ctx.Request.QueryString["skipRecord"], "1", StringComparison.Ordinal);
        var initiatorIp = GetClientIp(ctx);
        if (!skipRecord)
        {
            var history = GetHistoryForIp(initiatorIp);
            // Recording a playlist: wipe individual track entries so only the playlist card appears in recent
            if (recordOnly && string.Equals(Path.GetExtension(filePath), ".m3u", StringComparison.OrdinalIgnoreCase))
                history.ClearMusicTracks();
            history.RecordPlayed(filePath);
        }
        if (!recordOnly)
        {
            _musicPlayInitiatorIp = initiatorIp;
            _callbacks.Stop();
            _callbacks.RadioStop();
            _callbacks.PlayMusic(filePath, startPos);
            Logger.Info("Playback", $"Playing Music: '{Path.GetFileName(filePath)}' on Server" + (startPos > 0 ? $" at {startPos:F1}s" : ""));
        }
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
    }

    private void HandleMusicQueueNext(HttpListenerContext ctx)
    {
        var encodedPath = ctx.Request.QueryString["path"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(encodedPath))
        {
            // Empty path clears the queued next track
            _callbacks.SetMusicNextTrack(null);
            TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
            return;
        }
        var filePath = WebPathHelpers.DecodePath(encodedPath);
        if (!File.Exists(filePath))
        {
            TrySendResponse(ctx, 404, "text/plain", "File not found");
            return;
        }
        _callbacks.SetMusicNextTrack(filePath);
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
    }

    private static readonly Dictionary<string, string> _audioMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"]  = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".ogg"]  = "audio/ogg",
        [".opus"] = "audio/ogg; codecs=opus",
        [".m4a"]  = "audio/mp4",
        [".aac"]  = "audio/aac",
        [".wav"]  = "audio/wav",
        [".wma"]  = "audio/x-ms-wma",
    };

    private void HandleMusicStream(HttpListenerContext ctx)
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
        var ext = Path.GetExtension(filePath);
        var mime = _audioMimeTypes.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        var fileInfo = new FileInfo(filePath);
        long fileLength = fileInfo.Length;

        // Parse Range header for seek support
        long rangeStart = 0;
        long rangeEnd = fileLength - 1;
        bool isRange = false;
        var rangeHeader = ctx.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = rangeHeader[6..].Split('-');
            if (parts.Length == 2)
            {
                if (long.TryParse(parts[0], out var rs)) rangeStart = rs;
                if (long.TryParse(parts[1], out var re)) rangeEnd = re;
                isRange = true;
            }
        }
        rangeStart = Math.Max(0, Math.Min(rangeStart, fileLength - 1));
        rangeEnd   = Math.Max(rangeStart, Math.Min(rangeEnd, fileLength - 1));
        long contentLength = rangeEnd - rangeStart + 1;

        try
        {
            ctx.Response.StatusCode = isRange ? 206 : 200;
            ctx.Response.ContentType = mime;
            ctx.Response.ContentLength64 = contentLength;
            ctx.Response.Headers["Accept-Ranges"] = "bytes";
            ctx.Response.Headers["Content-Range"] = $"bytes {rangeStart}-{rangeEnd}/{fileLength}";
            ctx.Response.Headers["Cache-Control"] = "no-store";

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(rangeStart, SeekOrigin.Begin);
            var buf = new byte[81920];
            long remaining = contentLength;
            while (remaining > 0)
            {
                int read = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (read == 0) break;
                ctx.Response.OutputStream.Write(buf, 0, read);
                remaining -= read;
            }
            ctx.Response.OutputStream.Close();
        }
        catch (HttpListenerException) { /* client disconnected */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MusicStream] {ex.Message}");
        }
    }

    // -- Lyrics HTTP client (singleton to avoid socket exhaustion) ---------------
    private static readonly System.Net.Http.HttpClient _lyricsHttpClient = CreateLyricsHttpClient();
    private static System.Net.Http.HttpClient CreateLyricsHttpClient()
    {
        var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        // Timeout is Infinite here; each call site passes a CancellationToken with its own deadline.
        var client  = new System.Net.Http.HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("Lrclib-Client", "RemotePlay/1.0 (https://github.com/ebrprive-lgtm/RemotePlay)");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    // -- In-memory negative-result cache (path hash -> UTC expiry) ---------------
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>
        _lyricsNegativeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fetches lyrics for a track — tries LRCLib (synced) first, falls back to Genius.
    /// Results are disk-cached; negative results are memory-cached for 24 h.</summary>
    private async Task HandleMusicLyricsAsync(HttpListenerContext ctx)
    {
        var artist = ctx.Request.QueryString["artist"] ?? string.Empty;
        var title  = ctx.Request.QueryString["title"]  ?? string.Empty;
        Logger.Detail("Lyrics", $"Request: artist='{artist}' title='{title}'");
        if (string.IsNullOrWhiteSpace(title))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"missing title\"}");
            return;
        }

        // If tag metadata didn't provide an artist, try to derive it from the file path.
        // Strategy 1: filename contains "Artist - Title" pattern.
        // Strategy 2: grandparent folder is typically the artist name in a standard
        //             Music\Artist\Album\Track.mp3 library layout.
        if (string.IsNullOrWhiteSpace(artist))
        {
            var rawPath = ctx.Request.QueryString["path"] ?? string.Empty;

            // Resolve the actual filesystem path so we can inspect directory names
            string? fsPath = null;
            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                try { fsPath = WebPathHelpers.DecodePath(rawPath); } catch { /* ignore */ }
                if (string.IsNullOrWhiteSpace(fsPath))
                {
                    // Fallback: simple URL-decode
                    try { fsPath = Uri.UnescapeDataString(rawPath.Replace('+', ' ')); } catch { /* ignore */ }
                }
            }

            if (!string.IsNullOrWhiteSpace(fsPath))
            {
                // Strategy 1: "Artist - Title" in filename
                var stem = Path.GetFileNameWithoutExtension(fsPath);
                // Strip leading track numbers (e.g. "01 ", "1. ", "01 - ")
                var stemNoNum = System.Text.RegularExpressions.Regex.Replace(
                    stem, @"^\d{1,3}[\s._\-]+", string.Empty).Trim();
                var dashIdx = stemNoNum.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIdx > 0)
                {
                    var candidate = stemNoNum[..dashIdx].Trim();
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        // Sanity: reject if what's left looks like another track number
                        !System.Text.RegularExpressions.Regex.IsMatch(candidate, @"^\d+$"))
                    {
                        artist = candidate;
                        Logger.Detail("Lyrics", $"Artist inferred from filename: '{artist}'");
                    }
                }

                // Strategy 2: grandparent folder = artist (Music\Artist\Album\track.mp3)
                if (string.IsNullOrWhiteSpace(artist))
                {
                    var albumDir  = Path.GetDirectoryName(fsPath);
                    var artistDir = albumDir is not null ? Path.GetDirectoryName(albumDir) : null;
                    var artistName = artistDir is not null ? Path.GetFileName(artistDir) : null;
                    // Reject obviously non-artist folder names
                    if (!string.IsNullOrWhiteSpace(artistName) &&
                        !string.Equals(artistName, "Music", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(artistName, "music", StringComparison.OrdinalIgnoreCase) &&
                        artistName.Length >= 2)
                    {
                        artist = artistName;
                        Logger.Detail("Lyrics", $"Artist inferred from folder structure: '{artist}'");
                    }
                }
            }
        }

        // Normalise artist+title the same way for cache keys and queries
        var cleanTitle  = NormalizeLyricsTitle(title);
        var cleanArtist = NormalizeLyricsArtist(artist);

        // Disk-cache key is a short SHA256 of "artist|title" (lowercase)
        var cacheKey  = ComputeLyricsCacheKey(cleanArtist, cleanTitle);
        var cacheFile = Path.Combine(AppPaths.LyricsCacheDirectory, cacheKey + ".json");

        // --- Serve from disk cache if present ---
        if (File.Exists(cacheFile))
        {
            try
            {
                var cached = await File.ReadAllTextAsync(cacheFile).ConfigureAwait(false);
                using var cdoc = JsonDocument.Parse(cached);
                var croot = cdoc.RootElement;
                // Respect TTL: negative results expire after 24 h, positive results after 30 days
                if (croot.TryGetProperty("cachedUtc", out var tsEl) &&
                    DateTimeOffset.TryParse(tsEl.GetString(), out var cachedAt))
                {
                    bool isPositive = croot.TryGetProperty("found", out var fEl) && fEl.GetBoolean();
                    var ttl = isPositive ? TimeSpan.FromDays(30) : TimeSpan.FromHours(2);
                    if (DateTimeOffset.UtcNow - cachedAt < ttl)
                    {
                        Logger.Detail("Lyrics", $"Disk cache hit for '{cleanArtist} - {cleanTitle}'");
                        // Strip internal cache fields before sending
                        var stripped = StripCacheMetaFromJson(cached);
                        TrySendResponse(ctx, 200, "application/json", stripped);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Detail("Lyrics", $"Disk cache read error (will re-fetch): {ex.Message}");
            }
        }

        // --- Memory-cached negative result (fast path, avoids file I/O) ---
        if (_lyricsNegativeCache.TryGetValue(cacheKey, out var expiry) && DateTimeOffset.UtcNow < expiry)
        {
            Logger.Detail("Lyrics", $"Negative memory cache hit for '{cleanArtist} - {cleanTitle}'");
            TrySendResponse(ctx, 200, "application/json", "{\"found\":false}");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            // --- Primary: LRCLib /get and /search run concurrently to minimise latency ---
            Logger.Detail("Lyrics", $"LRCLib fetch start for '{cleanArtist} - {cleanTitle}'");
            var getTask    = FetchLrcLibLyricsAsync(cleanArtist, cleanTitle, cts.Token);
            var searchTask = FetchLrcLibSearchAsync(cleanArtist, cleanTitle, cts.Token);
            await Task.WhenAll(getTask, searchTask).ConfigureAwait(false);

            // Prefer /get result (exact match); fall back to /search
            var lrcResult = getTask.Result ?? searchTask.Result;

            if (lrcResult.HasValue)
            {
                var (plain, synced) = lrcResult.Value;
                Logger.Detail("Lyrics", $"LRCLib hit for '{cleanArtist} - {cleanTitle}' (plain={plain?.Length ?? 0} synced={synced?.Length ?? 0})");
                var payload = JsonSerializer.Serialize(new
                {
                    found        = true,
                    lyrics       = plain ?? string.Empty,
                    syncedLyrics = synced,
                    source       = "lrclib"
                });
                WriteLyricsCache(cacheFile, payload, positive: true);
                TrySendResponse(ctx, 200, "application/json", payload);
                return;
            }

            // --- Fallback: Genius ---
            Logger.Detail("Lyrics", $"LRCLib miss — falling back to Genius for '{cleanArtist} - {cleanTitle}'");
            var (gLyrics, geniusUrl) = await FetchGeniusLyricsAsync(cleanArtist, cleanTitle, cts.Token).ConfigureAwait(false);
            if (gLyrics is null)
            {
                Logger.Detail("Lyrics", $"No lyrics found for '{cleanArtist} - {cleanTitle}'");
                // Cache the negative result in memory (2 h) and on disk
                _lyricsNegativeCache[cacheKey] = DateTimeOffset.UtcNow.AddHours(2);
                WriteLyricsCache(cacheFile, "{\"found\":false}", positive: false);
                TrySendResponse(ctx, 200, "application/json", "{\"found\":false}");
                return;
            }
            Logger.Detail("Lyrics", $"Genius hit for '{cleanArtist} - {cleanTitle}' ({gLyrics.Length} chars) url={geniusUrl}");
            var gPayload = JsonSerializer.Serialize(new { found = true, lyrics = gLyrics, geniusUrl, source = "genius" });
            WriteLyricsCache(cacheFile, gPayload, positive: true);
            TrySendResponse(ctx, 200, "application/json", gPayload);
        }
        catch (OperationCanceledException)
        {
            Logger.Detail("Lyrics", $"Lyrics fetch timed out for '{cleanArtist} - {cleanTitle}'");
            TrySendResponse(ctx, 200, "application/json", "{\"found\":false}");
        }
        catch (Exception ex)
        {
            Logger.Detail("Lyrics", $"Lyrics fetch exception for '{cleanArtist} - {cleanTitle}': {ex.Message}");
            TrySendResponse(ctx, 200, "application/json", "{\"found\":false}");
        }
    }

    /// <summary>Clears all lyrics cache: both the in-memory negative cache and all disk cache files.</summary>
    private Task HandleMusicLyricsClearCacheAsync(HttpListenerContext ctx)
    {
        try
        {
            // Clear in-memory negative cache
            _lyricsNegativeCache.Clear();

            // Delete all disk cache files
            var dir = AppPaths.LyricsCacheDirectory;
            if (Directory.Exists(dir))
            {
                int deleted = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try { File.Delete(f); deleted++; }
                    catch { /* best-effort */ }
                }
                Logger.Detail("Lyrics", $"Cache cleared: {deleted} file(s) removed.");
            }

            TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
        }
        catch (Exception ex)
        {
            Logger.Detail("Lyrics", $"Clear cache error: {ex.Message}");
            TrySendResponse(ctx, 500, "application/json", "{\"ok\":false}");
        }
        return Task.CompletedTask;
    }

    // -- Lyrics helpers ----------------------------------------------------------

    /// <summary>Strips feat./ft./with... parenthetical and leading track-number from a title.</summary>
    private static string NormalizeLyricsTitle(string title)
    {
        var s = title.Trim();
        // Normalise typographic apostrophes/quotes to plain ASCII so LRCLib exact-match works
        s = s.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u02BC', '\'') // right/left single quotation mark, modifier letter apostrophe
             .Replace('\u201C', '"').Replace('\u201D', '"');                           // left/right double quotation mark
        // Strip leading track number: "01 ", "01. ", "01 - ", etc.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^\d{1,3}[\s._\-]+", string.Empty).Trim();
        // Strip (feat. ...) / (ft. ...) / (with ...) / (featuring ...)
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"\s*[\(\[](feat|ft|with|featuring)[^\)\]]*[\)\]]",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        // Strip trailing " - Remastered", " (Radio Edit)", etc.
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"\s*[\(\[](remaster(ed)?|radio edit|single version|album version|live)[^\)\]]*[\)\]]",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return s;
    }

    /// <summary>Strips feat./ft. from the artist string (e.g. "Artist feat. Other").</summary>
    private static string NormalizeLyricsArtist(string artist)
    {
        var s = artist.Trim();
        // Normalise typographic apostrophes/quotes to plain ASCII
        s = s.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u02BC', '\'');
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"\s+(feat|ft|featuring|with)\s+.*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return s;
    }

    /// <summary>Returns a short hex string that uniquely identifies artist+title for cache keying.</summary>
    private static string ComputeLyricsCacheKey(string artist, string title)
    {
        var raw = $"{artist.ToLowerInvariant()}|{title.ToLowerInvariant()}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Writes a lyrics result to disk cache, embedding a timestamp so TTL can be enforced on re-read.</summary>
    private static void WriteLyricsCache(string cacheFile, string jsonPayload, bool positive)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LyricsCacheDirectory);
            // Inject a cachedUtc field into the JSON object
            var ts   = DateTimeOffset.UtcNow.ToString("O");
            var meta = $",\"cachedUtc\":\"{ts}\"";
            var json = jsonPayload.TrimEnd();
            json = json.EndsWith('}') ? json[..^1] + meta + "}" : json;
            File.WriteAllText(cacheFile, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            Logger.Detail("Lyrics", $"Disk cache write error: {ex.Message}");
        }
    }

    /// <summary>Removes internal cache metadata fields before forwarding to the browser.</summary>
    private static string StripCacheMetaFromJson(string json)
    {
        // Remove "cachedUtc":"..." including any leading comma+whitespace
        return System.Text.RegularExpressions.Regex.Replace(json,
            @",?\s*""cachedUtc""\s*:\s*""[^""]*""", string.Empty);
    }

    /// <summary>
    /// Tries to resolve the canonical recording title and artist from MusicBrainz (best-effort, 5 s timeout).
    /// Returns (null, null) if not found or on any error so callers always have a fallback.
    /// </summary>
    private static async Task<(string? title, string? artist)> TryGetMusicBrainzCanonicalAsync(
        string artist, string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null, null);
        try
        {
            using var mbCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            var q   = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}");
            var url = $"https://musicbrainz.org/ws/2/recording/?query={q}&limit=1&fmt=json";
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "RemotePlay/1.0 (https://github.com/ebrprive-lgtm/RemotePlay)");
            var resp = await _lyricsHttpClient.SendAsync(req, mbCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, null);

            var json = await resp.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            using var doc  = JsonDocument.Parse(json);
            var recordings = doc.RootElement.TryGetProperty("recordings", out var rEl) ? rEl : default;
            if (recordings.ValueKind != JsonValueKind.Array || recordings.GetArrayLength() == 0)
                return (null, null);

            var rec = recordings[0];
            var mbTitle = rec.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString() : null;
            string? mbArtist = null;
            if (rec.TryGetProperty("artist-credit", out var acEl) && acEl.ValueKind == JsonValueKind.Array
                && acEl.GetArrayLength() > 0)
            {
                var first = acEl[0];
                if (first.TryGetProperty("artist", out var aEl) && aEl.TryGetProperty("name", out var anEl))
                    mbArtist = anEl.GetString();
            }
            if (!string.IsNullOrWhiteSpace(mbTitle))
            {
                Logger.Detail("Lyrics", $"MusicBrainz canonical: '{artist} - {title}' -> '{mbArtist} - {mbTitle}'");
                return (mbTitle, mbArtist);
            }
        }
        catch (Exception ex)
        {
            Logger.Detail("Lyrics", $"MusicBrainz lookup skipped: {ex.Message}");
        }
        return (null, null);
    }

    /// <summary>
    /// Queries LRCLib for plain and synced (LRC) lyrics. Returns null when not found.
    /// API: GET https://lrclib.net/api/get?artist_name=X&track_name=Y
    /// </summary>
    private static async Task<(string? plain, string? synced)?> FetchLrcLibLyricsAsync(
        string artist, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var query = new System.Collections.Specialized.NameValueCollection();
        query["track_name"] = title.Trim();
        if (!string.IsNullOrWhiteSpace(artist))
            query["artist_name"] = artist.Trim();

        var qs = string.Join("&", Array.ConvertAll(query.AllKeys!, k =>
            Uri.EscapeDataString(k!) + "=" + Uri.EscapeDataString(query[k!] ?? string.Empty)));

        var url = "https://lrclib.net/api/get?" + qs;
        Logger.Detail("Lyrics", $"LRCLib request: {url}");

        try
        {
            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            callCts.CancelAfter(TimeSpan.FromSeconds(8));
            var response = await _lyricsHttpClient.GetAsync(url, callCts.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                Logger.Detail("Lyrics", $"LRCLib: {(int)response.StatusCode} — treating as miss");
                return null;
            }
            response.EnsureSuccessStatusCode();
            var json    = await response.Content.ReadAsStringAsync(callCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root    = doc.RootElement;

            var plain  = root.TryGetProperty("plainLyrics",  out var pEl) && pEl.ValueKind == JsonValueKind.String ? pEl.GetString() : null;
            var synced = root.TryGetProperty("syncedLyrics", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(plain) && string.IsNullOrWhiteSpace(synced))
            {
                Logger.Detail("Lyrics", "LRCLib: response has no lyric content");
                return null;
            }

            // Validate that the returned artist matches what we asked for to prevent
            // LRCLib returning a completely different artist's track (e.g. wrong homonym song).
            if (!string.IsNullOrWhiteSpace(artist))
            {
                var returnedArtist = root.TryGetProperty("artistName", out var raEl) && raEl.ValueKind == JsonValueKind.String
                    ? NormalizeLyricsArtist(raEl.GetString() ?? string.Empty)
                    : string.Empty;
                var wantArtist = NormalizeLyricsArtist(artist);
                if (!string.IsNullOrWhiteSpace(returnedArtist) && !LrcLibArtistMatches(wantArtist, returnedArtist))
                {
                    Logger.Detail("Lyrics", $"LRCLib /get: artist mismatch — got '{returnedArtist}', wanted '{wantArtist}'; discarding");
                    return null;
                }
            }

            return (plain, synced);
        }
        catch (OperationCanceledException oce)
        {
            // Only rethrow if the outer budget was cancelled; per-call timeout = treat as a miss
            if (oce.CancellationToken == ct && ct.IsCancellationRequested) throw;
            Logger.Detail("Lyrics", $"LRCLib request timed out for '{title}'");
            return null;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            Logger.Detail("Lyrics", $"LRCLib HTTP error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Uses LRCLib /api/search for fuzzy matching when the direct /api/get endpoint returns 404.
    /// Only returns a result when the returned artist name loosely matches the requested artist
    /// to prevent accepting lyrics for a completely different artist (e.g. Helloween instead of Billy Joel).
    /// </summary>
    private static async Task<(string? plain, string? synced)?> FetchLrcLibSearchAsync(
        string artist, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var q   = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}");
        var url = $"https://lrclib.net/api/search?q={q}";
        Logger.Detail("Lyrics", $"LRCLib search: {url}");
        try
        {
            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            callCts.CancelAfter(TimeSpan.FromSeconds(8));
            var response = await _lyricsHttpClient.GetAsync(url, callCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(callCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            // When we have an artist name, only accept results whose artist loosely matches.
            // This prevents fuzzy-title matches from the wrong artist (e.g. Helloween for a Billy Joel query).
            var artistNorm = NormalizeLyricsArtist(artist);
            var hasArtist  = !string.IsNullOrWhiteSpace(artistNorm);
            int itemIndex  = 0;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                itemIndex++;
                var returnedArtist = item.TryGetProperty("artistName", out var aEl) && aEl.ValueKind == JsonValueKind.String
                    ? NormalizeLyricsArtist(aEl.GetString() ?? string.Empty)
                    : string.Empty;
                var returnedTitle = item.TryGetProperty("trackName", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? string.Empty
                    : string.Empty;

                var plain  = item.TryGetProperty("plainLyrics",  out var pEl) && pEl.ValueKind == JsonValueKind.String ? pEl.GetString() : null;
                var synced = item.TryGetProperty("syncedLyrics", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
                bool hasLyrics = !string.IsNullOrWhiteSpace(plain) || !string.IsNullOrWhiteSpace(synced);

                Logger.Detail("Lyrics", $"LRCLib search [{itemIndex}]: artist='{returnedArtist}' title='{returnedTitle}' hasLyrics={hasLyrics}");

                if (!hasLyrics)
                    continue;

                if (hasArtist)
                {
                    if (string.IsNullOrWhiteSpace(returnedArtist))
                    {
                        Logger.Detail("Lyrics", $"LRCLib search [{itemIndex}]: skip — returned artistName is empty");
                        continue;
                    }

                    bool artistOk = LrcLibArtistMatches(artistNorm, returnedArtist);
                    Logger.Detail("Lyrics", $"LRCLib search [{itemIndex}]: artist match '{artistNorm}' vs '{returnedArtist}' => {artistOk}");
                    if (!artistOk)
                        continue;
                }
                else
                {
                    // No artist available — guard by title similarity to avoid accepting a completely
                    // different song that happens to contain the search words (e.g. Helloween's
                    // "Livin' Ain't No Crime" when searching for "Ain't No Crime").
                    var titleNorm     = NormalizeLyricsTitle(title);
                    var retTitleNorm  = NormalizeLyricsTitle(returnedTitle);
                    bool titleOk = LrcLibTitleMatches(titleNorm, retTitleNorm);
                    Logger.Detail("Lyrics", $"LRCLib search [{itemIndex}]: title match '{titleNorm}' vs '{retTitleNorm}' => {titleOk}");
                    if (!titleOk)
                        continue;
                }

                Logger.Detail("Lyrics", $"LRCLib search [{itemIndex}]: ACCEPTED artist='{returnedArtist}' title='{returnedTitle}'");
                return (plain, synced);
            }
            Logger.Detail("Lyrics", "LRCLib search: no hit with matching artist and lyric content");
            return null;
        }
        catch (OperationCanceledException oce)
        {
            if (oce.CancellationToken == ct && ct.IsCancellationRequested) throw;
            Logger.Detail("Lyrics", $"LRCLib search timed out for '{title}'");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Detail("Lyrics", $"LRCLib search error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns true when the artist name returned by LRCLib is considered a match for the
    /// requested artist. Uses token overlap so "Billy Joel" matches "billy joel" and minor
    /// variations, while completely different artists like "Helloween" are rejected.
    /// </summary>
    private static bool LrcLibArtistMatches(string want, string got)
    {
        // Exact normalised match
        if (string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) return true;

        // One contains the other (handles "The Beatles" vs "Beatles")
        if (want.Contains(got, StringComparison.OrdinalIgnoreCase) ||
            got.Contains(want, StringComparison.OrdinalIgnoreCase))
            return true;

        // Token overlap: require at least half the want-tokens to appear in got
        var wantTokens = want.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wantTokens.Length == 0) return true;
        int matches = wantTokens.Count(t => got.Contains(t, StringComparison.OrdinalIgnoreCase));
        return matches >= (wantTokens.Length + 1) / 2; // ceiling of half
    }

    /// <summary>
    /// Title match used when no artist metadata is available.
    /// Requires the returned title to be essentially the same as the searched title —
    /// rejects results where extra leading words were prepended (e.g. "It Ain't No Crime"
    /// or "Livin' Ain't No Crime" when searching for "Ain't No Crime").
    /// </summary>
    private static bool LrcLibTitleMatches(string want, string got)
    {
        if (string.IsNullOrWhiteSpace(want)) return true;

        // Exact match
        if (string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) return true;

        // The returned title must end with the wanted title (handles "Live: Ain't No Crime"
        // style suffixes only if the suffix IS the full wanted phrase and the prefix is short).
        // We do NOT allow the want to appear anywhere in a longer title, only at the end.
        if (got.EndsWith(want, StringComparison.OrdinalIgnoreCase))
        {
            // Prefix (the extra part before want) must be trivially short: e.g. "(Live) " or a
            // year prefix. If the prefix adds more than 8 characters, reject.
            int prefixLen = got.Length - want.Length;
            if (prefixLen <= 8) return true;
        }

        // High bidirectional token overlap: both sets must be ≥ 90 % covered.
        var wantTokens = want.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var gotTokens  = got.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wantTokens.Length == 0) return true;
        int wantInGot = wantTokens.Count(t => got.Contains(t, StringComparison.OrdinalIgnoreCase));
        int gotInWant = gotTokens.Count(t => want.Contains(t, StringComparison.OrdinalIgnoreCase));
        double precision = (double)wantInGot / wantTokens.Length;
        double recall    = gotTokens.Length == 0 ? 1.0 : (double)gotInWant / gotTokens.Length;
        return precision >= 0.9 && recall >= 0.9;
    }

    private static async Task<(string? lyrics, string? geniusUrl)> FetchGeniusLyricsAsync(
        string artist, string title, CancellationToken ct = default)
    {
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
        Logger.Detail("Lyrics", $"Searching Genius for: '{query}'");

        // The shared _lyricsHttpClient already has the right User-Agent / Accept-Language headers.
        // ── Step 1: try the direct slug URL ──────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            var slug      = BuildGeniusSlug(artist, title);
            var directUrl = $"https://genius.com/{slug}-lyrics";
            Logger.Detail("Lyrics", $"Trying direct URL: {directUrl}");
            try
            {
                var directHtml = await _lyricsHttpClient.GetStringAsync(directUrl, ct).ConfigureAwait(false);
                if (directHtml.Contains("data-lyrics-container", StringComparison.Ordinal))
                {
                    var directLyrics = ExtractGeniusLyricsText(directHtml);
                    if (directLyrics is not null)
                    {
                        Logger.Detail("Lyrics", $"Direct URL hit — extracted {directLyrics.Length} chars");
                        return (directLyrics, directUrl);
                    }
                }
                Logger.Detail("Lyrics", "Direct URL returned no lyrics container — falling back to search");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Detail("Lyrics", $"Direct URL fetch failed ({ex.Message}) — falling back to search");
            }
        }

        // ── Step 2: Genius internal JSON search API ───────────────────────────────────────────────
        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
            "https://genius.com/api/search/song?q=" + Uri.EscapeDataString(query));
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        var searchResp = await _lyricsHttpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!searchResp.IsSuccessStatusCode) return (null, null);
        var searchJson = await searchResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Logger.Detail("Lyrics", $"Search URL: {req.RequestUri}");

        var songUrl = ExtractFirstGeniusSongUrlFromJson(searchJson, artist, title);
        if (songUrl is null)
        {
            Logger.Detail("Lyrics", $"No song URL found in Genius API response (len={searchJson.Length})");
            return (null, null);
        }
        Logger.Detail("Lyrics", $"Song URL from search: {songUrl}");

        // ── Step 3: fetch lyrics page ─────────────────────────────────────────────────────────────
        var lyricsHtml = await _lyricsHttpClient.GetStringAsync(songUrl, ct).ConfigureAwait(false);
        Logger.Detail("Lyrics", $"Lyrics page fetched ({lyricsHtml.Length} chars)");
        var lyrics = ExtractGeniusLyricsText(lyricsHtml);
        Logger.Detail("Lyrics", lyrics is null ? "Lyrics extraction returned null" : $"Extracted {lyrics.Length} chars");
        return lyrics is null ? (null, null) : (lyrics, songUrl);
    }


    /// <summary>
    /// Builds the Genius URL slug from artist and title.
    /// Spaces become hyphens; only alphanumerics and hyphens are kept; runs of hyphens are collapsed.
    /// Example: "Billy Joel" + "Christie Lee" → "Billy-Joel-Christie-Lee"
    /// </summary>
    private static string BuildGeniusSlug(string artist, string title)
    {
        static string Slugify(string s)
        {
            // Replace any non-alphanumeric character with a hyphen, then collapse runs
            var sb = new System.Text.StringBuilder();
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }
        return Slugify(artist) + "-" + Slugify(title);
    }

    private static string? ExtractFirstGeniusSongUrlFromJson(string json, string artist, string title)
    {
        // The Genius /api/search/song response contains hits with "url":"https://genius.com/...-lyrics"
        // Slashes may be escaped as \/ in the JSON. We take the first hit URL ending in -lyrics.
        const string urlKey = "\"url\":\"";
        var pos = 0;
        while (pos < json.Length)
        {
            var keyPos = json.IndexOf(urlKey, pos, StringComparison.Ordinal);
            if (keyPos < 0) break;
            var valStart = keyPos + urlKey.Length;
            var valEnd   = json.IndexOf('"', valStart);
            if (valEnd < 0) break;
            var url = json[valStart..valEnd].Replace("\\/", "/");
            if (url.Contains("genius.com/", StringComparison.OrdinalIgnoreCase) &&
                url.EndsWith("-lyrics", StringComparison.OrdinalIgnoreCase))
                return url;
            pos = valEnd;
        }
        return null;
    }

    private static string? ExtractGeniusLyricsText(string html)
    {
        // Genius wraps lyrics in <div data-lyrics-container="true">...</div>
        const string attr = "data-lyrics-container=\"true\"";
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        while (true)
        {
            var divStart = html.IndexOf(attr, pos, StringComparison.Ordinal);
            if (divStart < 0) break;
            // Walk back to the opening <
            var tagOpen = html.LastIndexOf('<', divStart);
            if (tagOpen < 0) break;
            // Find the end of the opening tag >
            var tagClose = html.IndexOf('>', tagOpen);
            if (tagClose < 0) break;
            var innerStart = tagClose + 1;
            // Find matching closing </div> (account for nesting)
            var depth = 1;
            var cursor = innerStart;
            while (depth > 0 && cursor < html.Length)
            {
                var nextOpen  = html.IndexOf("<div",  cursor, StringComparison.OrdinalIgnoreCase);
                var nextClose = html.IndexOf("</div>", cursor, StringComparison.OrdinalIgnoreCase);
                if (nextClose < 0) break;
                if (nextOpen >= 0 && nextOpen < nextClose) { depth++; cursor = nextOpen + 4; }
                else { depth--; cursor = nextClose + 6; }
            }
            var innerHtml = html[innerStart..(cursor - 6 < innerStart ? innerStart : cursor - 6)];
            // Convert <br> and </div> to newlines, strip remaining tags
            innerHtml = System.Text.RegularExpressions.Regex.Replace(innerHtml, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            innerHtml = System.Text.RegularExpressions.Regex.Replace(innerHtml, @"</div>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            innerHtml = System.Text.RegularExpressions.Regex.Replace(innerHtml, @"<[^>]+>", string.Empty);
            innerHtml = System.Net.WebUtility.HtmlDecode(innerHtml);
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(innerHtml.Trim());
            pos = cursor;
        }
        var result = sb.ToString().Trim();
        if (result.Length == 0) return null;

        // Find the real start of the lyrics by searching for section markers in priority order:
        // 1) [Intro]  2) [Verse 1]  3) any line ending with "Lyrics" (Genius title header)
        var lines = result.Split('\n');
        int startLine = 0;
        bool found = false;

        // Priority 1: [Intro]
        for (int i = 0; i < lines.Length && !found; i++)
        {
            if (lines[i].TrimStart().StartsWith("[Intro]", StringComparison.OrdinalIgnoreCase))
            { startLine = i; found = true; }
        }

        // Priority 2: [Verse 1]
        if (!found)
        {
            for (int i = 0; i < lines.Length && !found; i++)
            {
                if (lines[i].TrimStart().StartsWith("[Verse 1]", StringComparison.OrdinalIgnoreCase))
                { startLine = i; found = true; }
            }
        }

        // Priority 3: any line whose trimmed content ends with "Lyrics" (Genius title header)
        if (!found)
        {
            for (int i = 0; i < lines.Length && !found; i++)
            {
                if (lines[i].TrimEnd().EndsWith("Lyrics", StringComparison.OrdinalIgnoreCase))
                {
                    startLine = i + 1;
                    // Skip blank lines immediately after the header
                    while (startLine < lines.Length && string.IsNullOrWhiteSpace(lines[startLine]))
                        startLine++;
                    found = true;
                }
            }
        }

        result = string.Join('\n', lines.Skip(startLine)).Trim();
        return result.Length > 0 ? result : null;
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

    private void HandleMusicBoost(HttpListenerContext ctx)
    {
        if (!double.TryParse(ctx.Request.QueryString["v"],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var boost))
        {
            TrySendResponse(ctx, 400, "text/plain", "Missing v");
            return;
        }
        _callbacks.SetMusicBoost(Math.Clamp(boost, 0, 4));
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
    }

    private void HandleSearch(HttpListenerContext ctx)
    {
        var query = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        if (_libraryIndex.Length == 0)
            StartLibraryIndexRefresh(force: false);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var history = GetHistoryForIp(GetClientIp(ctx));
        var resumeMap = GetMergedProgressMap(history);
        var watchedSet = GetMergedWatchedSet(history);
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

    private void HandleMusicRecent(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMusicPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            TrySendResponse(ctx, 200, "application/json", "{\"files\":[]}");
            return;
        }

        var musicExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".wav", ".opus", ".ape", ".alac" };

        var indexLookup = _musicIndex.ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);

        var files = GetHistoryForIp(GetClientIp(ctx)).GetRecent(_config.PlaybackHistoryLimit + 14)
            .Where(item => WebPathHelpers.IsUnderRoot(item.FilePath, root)
                        && (musicExts.Contains(Path.GetExtension(item.FilePath))
                            || string.Equals(Path.GetExtension(item.FilePath), ".m3u", StringComparison.OrdinalIgnoreCase)))
            .Take(7)
            .Select(item =>
            {
                var ext = Path.GetExtension(item.FilePath);
                if (string.Equals(ext, ".m3u", StringComparison.OrdinalIgnoreCase))
                    return BuildPlaylistRecentObj(item.FilePath);
                if (!indexLookup.TryGetValue(item.FilePath, out var musicFile))
                    musicFile = new MusicFile(Path.GetFileName(item.FilePath), item.FilePath);
                return BuildMusicRecentObj(musicFile, item.PositionSeconds, item.DurationSeconds);
            })
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { files }));
    }

    private static object BuildMusicRecentObj(MusicFile f, double positionSeconds, double durationSeconds)
    {
        var dir     = Path.GetDirectoryName(f.FullPath) ?? string.Empty;
        var ext     = Path.GetExtension(f.FullPath).TrimStart('.').ToLowerInvariant();
        string album    = Path.GetFileName(dir);
        string artist   = Path.GetFileName(Path.GetDirectoryName(dir)) ?? string.Empty;
        string tagTitle = string.Empty;
        int?   durationSec = durationSeconds > 0 ? (int)Math.Round(durationSeconds) : null;
        string genre    = string.Empty;
        int?   year     = null;
        bool   hasCover = false;

        try
        {
            using var tfile = TagLib.File.Create(f.FullPath);
            var tag = tfile.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title))  tagTitle = tag.Title;
            if (!string.IsNullOrWhiteSpace(tag.Album))  album    = tag.Album;
            var performers = tag.AlbumArtists.Length > 0 ? tag.AlbumArtists : tag.Performers;
            if (performers.Length > 0 && !string.IsNullOrWhiteSpace(performers[0]))
                artist = performers[0];
            if (tag.Year > 0)   year = (int)tag.Year;
            if (tag.Genres.Length > 0 && !string.IsNullOrWhiteSpace(tag.Genres[0]))
                genre = tag.Genres[0];
            hasCover = tfile.Tag.Pictures.Length > 0 || FolderHasCoverImage(dir);
            durationSec = (int)Math.Round(tfile.Properties.Duration.TotalSeconds);
        }
        catch { /* fall back to path-based values */ }

        return new
        {
            name        = f.Name,
            tagTitle,
            path        = WebPathHelpers.EncodePath(f.FullPath),
            folder      = Path.GetFileName(dir),
            ext,
            album,
            artist,
            genre,
            year,
            durationSec,
            hasCover,
            position    = Math.Round(positionSeconds, 1),
            duration    = Math.Round(durationSeconds, 1),
            progress    = durationSeconds > 0 ? Math.Round(positionSeconds / durationSeconds, 3) : 0
        };
    }

    private static object BuildPlaylistRecentObj(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var dir  = Path.GetDirectoryName(filePath) ?? string.Empty;
        string artist = string.Empty;
        try
        {
            var lines = File.ReadLines(filePath)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
                .Take(1).ToList();
            if (lines.Count > 0)
            {
                var entry = lines[0].Trim();
                var firstPath = Path.IsPathRooted(entry)
                    ? entry
                    : Path.GetFullPath(Path.Combine(dir, entry));
                if (File.Exists(firstPath))
                {
                    using var tagFile = TagLib.File.Create(firstPath);
                    var performers = tagFile.Tag.AlbumArtists.Length > 0
                        ? tagFile.Tag.AlbumArtists
                        : tagFile.Tag.Performers;
                    artist = performers.Length > 0 ? performers[0] : string.Empty;
                }
            }
        }
        catch { /* leave artist empty */ }

        return new
        {
            name,
            tagTitle    = name,
            path        = WebPathHelpers.EncodePath(filePath),
            folder      = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty,
            ext         = "m3u",
            album       = name,
            artist,
            genre       = string.Empty,
            year        = (int?)null,
            durationSec = (int?)null,
            hasCover    = false,
            position    = 0.0,
            duration    = 0.0,
            progress    = 0.0,
            isPlaylist  = true
        };
    }

    private void HandleFavorites(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMoviesPath;
        var history = GetHistoryForIp(GetClientIp(ctx));
        var resumeMap = GetMergedProgressMap(history);
        var watchedSet = GetMergedWatchedSet(history);
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

    private void HandleRecentClear(HttpListenerContext ctx)
    {
        GetHistoryForIp(GetClientIp(ctx)).ClearAll();
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    private void HandleMusicRecentClear(HttpListenerContext ctx)
    {
        var root = _config.ResolvedMusicPath;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var musicExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp3", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".wav", ".opus", ".ape", ".alac", ".m3u" };
            var history = GetHistoryForIp(GetClientIp(ctx));
            var toClear = history.GetRecent(int.MaxValue)
                .Where(item => WebPathHelpers.IsUnderRoot(item.FilePath, root)
                            && musicExts.Contains(Path.GetExtension(item.FilePath)))
                .Select(item => item.FilePath)
                .ToArray();
            foreach (var path in toClear)
                history.Clear(path);
        }
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
        var resumeMap = GetMergedProgressMap(history);
        var watchedSet = GetMergedWatchedSet(history);
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
        Logger.Info("Playback", $"Playing Video: '{Path.GetFileName(filePath)}' on Server");
        TrySendResponse(ctx, 200, "text/plain", "OK");
    }

    // Sets the browser-local-playing flag so the WPF window can update its idle overlay.
    private void HandleLocalPlayingAsync(HttpListenerContext ctx)
    {
        using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
        var body = reader.ReadToEnd().Trim();
        _browserLocalPlaying = body.Equals("true", StringComparison.OrdinalIgnoreCase);
        TrySendResponse(ctx, 200, "text/plain", "ok");
    }

    // Accepts a log message posted from the browser-side JavaScript (e.g. local playback events).
    private void HandleClientLog(HttpListenerContext ctx)
    {
        using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
        var body = reader.ReadToEnd().Trim();
        if (!string.IsNullOrWhiteSpace(body))
        {
            // Diagnostic trace prefixes are Detail-level; everything else is Info
            if (body.StartsWith("[LOCAL-", StringComparison.Ordinal) ||
                body.StartsWith("[RADIO-LOCAL]", StringComparison.Ordinal) ||
                body.StartsWith("[RADIO-SERVER]", StringComparison.Ordinal) ||
                body.StartsWith("[PROXY]", StringComparison.Ordinal))
                Logger.Detail("Playback", body);
            else
                Logger.Info("Playback", body);
        }
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
                NetworkShareHelper.EnsureConnected(_config.AllResolvedMoviesPaths, _config.NetworkShareCredentials);

                var allFiles = new List<LibraryFile>();

                foreach (var root in _config.AllResolvedMoviesPaths)
                {
                    if (!Directory.Exists(root))
                    {
                        Logger.Info($"Movie library root not found, skipping: {root}");
                        continue;
                    }

                    var rootFiles = EnumerateLibraryVideoFiles(root, _hiddenFolderNames, () => Interlocked.Increment(ref _scannedFolders))
                        .Select(f =>
                        {
                            Interlocked.Increment(ref _scannedFiles);
                            return f;
                        })
                        .ToArray();

                    var videoFiles = rootFiles
                        .Where(f => WebPathHelpers.IsVideoFile(f, _videoExtensions))
                        .Select(f => BuildLibraryFile(root, f));

                    var linkFiles = rootFiles
                        .Where(RplinkHelper.IsRplinkFile)
                        .Select(f => BuildLibraryFileForLink(root, f))
                        .Where(f => f is not null)
                        .Select(f => f!);

                    var folderLinkFiles = rootFiles
                        .Where(RplinkHelper.IsRplinkFile)
                        .SelectMany(f =>
                        {
                            var items = BuildLibraryFilesForFolderLink(root, f).ToList();
                            if (items.Count > 0)
                                Interlocked.Add(ref _scannedFiles, items.Count);
                            return items;
                        });

                    allFiles.AddRange(videoFiles.Concat(linkFiles).Concat(folderLinkFiles));
                }

                if (allFiles.Count == 0 && _config.AllResolvedMoviesPaths.Length == 0)
                {
                    _libraryIndex = [];
                    _lastIndexRefreshUtc = DateTimeOffset.UtcNow;
                    return;
                }

                var files = allFiles
                    // deduplicate: prefer the link entry over a plain entry for the same real path
                    .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(f => f.IsLink).First())
                    .OrderBy(f => f.SearchText, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                _libraryIndex = files;
                _lastIndexRefreshUtc = DateTimeOffset.UtcNow;
                SaveLibraryIndexCache();
                Logger.Detail($"Library index refreshed: {files.Length} videos across {_config.AllResolvedMoviesPaths.Length} root(s)");
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

    // How long a persisted music index is considered fresh before a background re-scan is run.
    private static readonly TimeSpan MusicIndexMaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Called on startup. If a recent cache was loaded, defers the full re-scan by a short
    /// warm-up period so the app is immediately usable; the background scan still runs to pick
    /// up any files added since the cache was written. If no cache (or it is stale), scans now.
    /// </summary>
    private void StartMusicIndexRefreshIfNeeded()
    {
        DateTimeOffset? lastRefresh;
        int cachedCount;
        lock (_musicIndexGate)
        {
            lastRefresh = _lastMusicIndexRefreshUtc;
            cachedCount = _musicIndex.Length;
        }

        if (lastRefresh.HasValue && cachedCount > 0 && DateTimeOffset.UtcNow - lastRefresh.Value < MusicIndexMaxAge)
        {
            Logger.Info($"Music index cache is fresh ({cachedCount} tracks, age {(DateTimeOffset.UtcNow - lastRefresh.Value).TotalMinutes:F0} min). " +
                        "Background re-scan deferred by 60 s.");
            // Give the app 60 seconds to fully start before doing the verify scan in the background.
            Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => StartMusicIndexRefresh());
        }
        else
        {
            Logger.Info(cachedCount == 0
                ? "No music index cache found — starting full scan."
                : $"Music index cache is stale ({cachedCount} tracks) — starting full scan.");
            StartMusicIndexRefresh();
        }
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
                NetworkShareHelper.EnsureConnected(_config.AllResolvedMusicPaths, _config.NetworkShareCredentials);

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

                // Scan the primary root with the live-prioritisable job, then scan
                // any additional roots with simple independent jobs.
                MusicScanner.Scan(job, _musicExtensions, onFolderComplete, onFolder, progress, cts.Token, _hiddenFolderNames);

                foreach (var additionalRoot in _config.ResolvedAdditionalMusicPaths)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (!Directory.Exists(additionalRoot)) continue;

                    Logger.Info($"Music scan: starting additional root '{additionalRoot}'");
                    var additionalJob = new MusicScanJob(additionalRoot);
                    MusicScanner.Scan(additionalJob, _musicExtensions, onFolderComplete, onFolder, progress, cts.Token, _hiddenFolderNames);
                }

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

    /// <summary>
    /// POST /api/durations — accepts a JSON array of Base64-encoded file paths,
    /// returns a JSON object mapping each encoded path to its cached duration in seconds.
    /// Only paths already present in the in-memory cache are returned (never probes disk).
    /// </summary>
    private void HandleDurations(HttpListenerContext ctx)
    {
        using var reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8);
        var body = reader.ReadToEnd();
        string[] encodedPaths;
        try
        {
            encodedPaths = JsonSerializer.Deserialize<string[]>(body) ?? [];
        }
        catch
        {
            TrySendResponse(ctx, 400, "text/plain", "Expected JSON array of encoded paths");
            return;
        }

        var result = new Dictionary<string, double>();
        foreach (var encoded in encodedPaths)
        {
            if (string.IsNullOrWhiteSpace(encoded)) continue;
            var filePath = WebPathHelpers.DecodePath(encoded);
            if (_videoDurationCache.TryGetValue(filePath, out var d) && d > 0)
                result[encoded] = d;
        }
        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(result));
    }

    private object BuildCardFile(string name, string filePath, IReadOnlyDictionary<string, RecentPlaybackItem> resumeMap, string folder = "", bool isFavorite = false, bool isLink = false, string? linkSourcePath = null, IReadOnlySet<string>? watchedSet = null)
    {
        resumeMap.TryGetValue(filePath, out var resume);
        var progress = resume is null || resume.DurationSeconds <= 0 ? 0 : Math.Round(resume.PositionSeconds / resume.DurationSeconds, 3);
        var watched = (watchedSet?.Contains(filePath) ?? false) || progress >= 0.95;
        long sizeBytes = 0;
        try { sizeBytes = new FileInfo(filePath).Length; } catch { }

        // Prefer duration from resume map (already known from playback).
        // Fall back to the background-populated cache.
        // If neither has a value yet, queue a background probe so future requests will have it.
        double durationSec = 0;
        if (resume is not null && resume.DurationSeconds > 0)
        {
            durationSec = Math.Round(resume.DurationSeconds, 1);
            _videoDurationCache[filePath] = durationSec; // keep cache warm
        }
        else if (_videoDurationCache.TryGetValue(filePath, out var cached) && cached > 0)
        {
            durationSec = cached;
        }
        else
        {
            // Schedule a one-shot background probe; result lands in cache for next request.
            var pathCapture = filePath;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var d = ReadFileDurationSeconds(pathCapture);
                if (d > 0)
                    _videoDurationCache[pathCapture] = d;
            });
        }

        return new
        {
            name,
            displayName = CleanDisplayTitle(name),
            path = WebPathHelpers.EncodePath(filePath),
            folder,
            favorite = isFavorite,
            position = resume is null ? 0 : Math.Round(resume.PositionSeconds, 1),
            duration = durationSec,
            progress,
            resume = resume is null ? string.Empty : FormatTime(resume.PositionSeconds),
            watched,
            isLink,
            linkPath = isLink && linkSourcePath is not null ? WebPathHelpers.EncodePath(linkSourcePath) : null,
            ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
            sizeBytes
        };
    }

    private static double ReadFileDurationSeconds(string filePath)
    {
        try
        {
            using var tfile = TagLib.File.Create(filePath);
            var secs = tfile.Properties.Duration.TotalSeconds;
            return secs > 0 ? Math.Round(secs, 1) : 0;
        }
        catch { return 0; }
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
        Logger.Info("Playback", $"Playing Radio: '{name}' on Server");
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

    private void HandleRadioBoost(HttpListenerContext ctx)
    {
        var raw = ctx.Request.QueryString["v"] ?? "1";
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var boost))
            boost = 1.0;
        _callbacks.RadioSetBoost(Math.Clamp(boost, 1.0, 3.0));
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
            var isFav = _callbacks.RadioIsFavorite(station.Uuid)
                || _callbacks.RadioIsFavoriteByUrl(station.StreamUrl)
                || _callbacks.RadioIsFavoriteByName(station.Name, station.Country);
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

    /// <summary>
    /// Proxies a radio stream to the browser so the same-origin &lt;audio&gt; element
    /// can play it without CORS restrictions. Query param: <c>url</c> (encoded stream URL).
    /// Streams bytes indefinitely; the browser disconnects when it stops listening.
    /// </summary>
    private static async Task HandleRadioStreamProxyAsync(HttpListenerContext ctx)
    {
        var streamUrl = ctx.Request.QueryString["url"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            TrySendResponse(ctx, 400, "text/plain", "missing url");
            return;
        }

        Logger.Detail("Playback", $"[PROXY] Request received for: {streamUrl}");

        using var http = new System.Net.Http.HttpClient();
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 RemotePlay/1.0");

        System.Net.Http.HttpResponseMessage? upstream = null;
        try
        {
            upstream = await http.GetAsync(streamUrl,
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            Logger.Detail("Playback", $"[PROXY] Upstream responded {(int)upstream.StatusCode}, content-type={upstream.Content.Headers.ContentType}");
        }
        catch (Exception ex)
        {
            Logger.Detail("Playback", $"[PROXY] Failed to connect to upstream: {ex.Message}");
            TrySendResponse(ctx, 502, "text/plain", "upstream error");
            return;
        }

        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.SendChunked = true;

        long bytesSent = 0;
        try
        {
            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buf = new byte[16 * 1024];
            int read;
            while ((read = await upstreamStream.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                await ctx.Response.OutputStream.WriteAsync(buf.AsMemory(0, read)).ConfigureAwait(false);
                bytesSent += read;
            }
            Logger.Detail("Playback", $"[PROXY] Stream ended normally after {bytesSent} bytes");
        }
        catch (Exception ex)
        {
            Logger.Detail("Playback", $"[PROXY] Pipe closed after {bytesSent} bytes — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            upstream.Dispose();
            try { ctx.Response.OutputStream.Close(); } catch { /* ignored */ }
        }
    }

}

