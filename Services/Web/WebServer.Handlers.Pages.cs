using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace RemotePlay;

// Page, UI, status, health, settings, and setup handlers.

internal sealed partial class WebServer
{

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
            preferForcedSubtitles = _config.PreferForcedSubtitles,
            expertMode = _expertMode,
            debugMode  = _debugMode
        });
        TrySendResponse(ctx, 200, "application/json", json);
    }

    private void HandleExpertMode(HttpListenerContext ctx)
    {
        if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var body = reader.ReadToEnd();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("expertMode", out var ep) &&
                    (ep.ValueKind == JsonValueKind.True || ep.ValueKind == JsonValueKind.False))
                {
                    var on = ep.GetBoolean();
                    _expertMode = on;
                    _callbacks.SaveExpertMode(on);
                }
            }
            catch { }
        }
        var json = JsonSerializer.Serialize(new { expertMode = _expertMode });
        TrySendResponse(ctx, 200, "application/json", json);
    }

    private void HandleDebugMode(HttpListenerContext ctx)
    {
        if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                var body = reader.ReadToEnd();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("debugMode", out var dp) &&
                    (dp.ValueKind == JsonValueKind.True || dp.ValueKind == JsonValueKind.False))
                {
                    var on = dp.GetBoolean();
                    _debugMode = on;
                    _callbacks.SaveDebugMode(on);
                }
            }
            catch { }
        }
        var debugJson = JsonSerializer.Serialize(new { debugMode = _debugMode });
        TrySendResponse(ctx, 200, "application/json", debugJson);
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
                $"<div class=\"fav-row\"><span class=\"fav-flag\">{HtmlEncode(string.IsNullOrWhiteSpace(f.CountryCode) ? "ðŸŒ" : f.CountryCode)}</span><span class=\"fav-name\">{HtmlEncode(f.Name)}</span><span class=\"fav-meta\">{HtmlEncode(f.Country)}{(f.Bitrate > 0 ? $" &middot; {f.Bitrate} kbps" : "")}{(string.IsNullOrWhiteSpace(f.Codec) ? "" : $" &middot; {HtmlEncode(f.Codec.ToUpperInvariant())}")}</span></div>"));

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
            /* â”€â”€ Hero â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */
            .hero{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:1.2rem;align-items:center;padding:1.1rem 1.4rem;border:1px solid rgba(255,255,255,.1);border-radius:24px;background:linear-gradient(135deg,rgba(24,32,68,.92) 0%,rgba(10,14,30,.94) 100%);box-shadow:var(--shadow),inset 0 1px 0 rgba(255,255,255,.06);position:sticky;top:10px;z-index:4;backdrop-filter:blur(18px)}
            .eyebrow{color:var(--muted);font-weight:800;font-size:.72rem;text-transform:uppercase;letter-spacing:.12em}
            .hero h1{margin:.12rem 0 .2rem;font-size:clamp(1.5rem,3.5vw,2.6rem);line-height:1;background:linear-gradient(135deg,#fff 40%,var(--muted));-webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text}
            .subtitle{color:var(--muted);font-size:.9rem;line-height:1.4}
            .actions{display:flex;gap:.5rem;flex-wrap:wrap;justify-content:flex-end}
            .button,button{display:inline-flex;align-items:center;justify-content:center;gap:.35rem;border:1px solid rgba(255,255,255,.1);border-radius:999px;background:rgba(30,40,80,.9);color:#dce8ff;padding:.6rem .95rem;font-size:.88rem;font-weight:800;cursor:pointer;min-height:40px;transition:filter .15s,background .15s}
            .button.primary,button.primary{background:linear-gradient(135deg,var(--accent),#c4294a);border-color:transparent;color:#fff}
            .button:hover,button:hover{filter:brightness(1.15);text-decoration:none}
            /* â”€â”€ Overview tiles â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */
            .overview{display:grid;grid-template-columns:repeat(8,minmax(0,1fr));gap:.7rem;margin:1rem 0}
            .tile{background:var(--card);border:1px solid var(--line);border-radius:18px;padding:.85rem .9rem;display:flex;flex-direction:column;gap:.35rem;position:relative;overflow:hidden;box-shadow:0 8px 24px rgba(0,0,0,.22)}
            .tile::after{content:'';position:absolute;inset:0;border-radius:inherit;background:linear-gradient(135deg,rgba(255,255,255,.04),transparent 60%);pointer-events:none}
            .tile-icon{font-size:1.4rem;line-height:1}
            .tile-label{color:var(--muted);font-size:.68rem;font-weight:800;text-transform:uppercase;letter-spacing:.08em}
            .tile-value{font-size:1.05rem;font-weight:900;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:#fff}
            .tile-sub{color:var(--dim);font-size:.72rem;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            /* â”€â”€ Pills â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */
            .pill{display:inline-flex;align-items:center;gap:.3rem;border-radius:999px;padding:.22rem .5rem;font-size:.7rem;font-weight:900;background:rgba(148,163,184,.1);color:var(--muted)}
            .pill::before{content:'';width:.48rem;height:.48rem;border-radius:50%;background:var(--dim)}
            .pill.ok{color:#9fffe8;background:rgba(0,212,170,.1)}.pill.ok::before{background:var(--cyan);box-shadow:0 0 6px var(--cyan)}
            .pill.warn{color:#ffd480;background:rgba(255,170,0,.1)}.pill.warn::before{background:var(--warn);box-shadow:0 0 6px var(--warn)}
            .pill.live{color:#ff9fba;background:rgba(233,69,96,.12)}.pill.live::before{background:var(--accent);box-shadow:0 0 6px var(--accent)}
            /* â”€â”€ Dashboard grid â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */
            .dashboard{display:grid;grid-template-columns:1.15fr .85fr;gap:.9rem;align-items:start;margin-top:.2rem}
            .stack{display:grid;gap:.9rem}
            /* â”€â”€ Cards â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */
            .card{background:var(--card);border:1px solid var(--line);border-radius:20px;padding:1rem 1.1rem;box-shadow:0 10px 28px rgba(0,0,0,.22);overflow:hidden;position:relative}
            .card::before{content:'';position:absolute;inset:0;border-radius:inherit;background:linear-gradient(160deg,rgba(255,255,255,.03),transparent 50%);pointer-events:none}
            .card h2{display:flex;align-items:center;justify-content:space-between;gap:.6rem;margin:0 0 .75rem;font-size:.95rem;font-weight:800;color:#d8e8ff}
            .card h3{font-size:.8rem;font-weight:800;text-transform:uppercase;letter-spacing:.05em;color:var(--accent);margin:.75rem 0 .3rem}
            /* Accent cards for live state */
            .accent-card{border-top:none;padding-top:0}
            .accent-bar{height:3px;margin:-1px -1.1rem .85rem;border-radius:20px 20px 0 0}
            .accent-music .accent-bar{background:linear-gradient(90deg,var(--purple),var(--cyan))}
            .accent-radio .accent-bar{background:linear-gradient(90deg,var(--accent),var(--warn))}
            /* â”€â”€ Metric grid â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */
            .grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.5rem .7rem}
            .metric{background:rgba(255,255,255,.03);border:1px solid rgba(255,255,255,.05);border-radius:12px;padding:.6rem .65rem;min-width:0}
            .metric dt{color:var(--muted);font-size:.67rem;font-weight:800;text-transform:uppercase;letter-spacing:.05em;margin:0 0 .25rem}
            .metric dd{margin:0;color:#e8f0ff;word-break:break-word;line-height:1.35;font-size:.92rem}
            .metric.wide{grid-column:1/-1}
            .mono{font-family:Consolas,ui-monospace,monospace;font-size:.82rem}
            /* â”€â”€ Radio favorites â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */
            .fav-list{display:grid;gap:.35rem;margin-top:.5rem;max-height:260px;overflow-y:auto;scrollbar-width:thin}
            .fav-row{display:grid;grid-template-columns:2rem 1fr auto;gap:.4rem .55rem;align-items:baseline;padding:.4rem .55rem;border-radius:10px;background:rgba(255,255,255,.03);border:1px solid rgba(255,255,255,.05)}
            .fav-flag{font-size:.92rem;text-align:center}
            .fav-name{font-size:.88rem;font-weight:700;color:#d8e8ff;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
            .fav-meta{font-size:.72rem;color:var(--dim);white-space:nowrap}
            /* â”€â”€ Other â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */
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
              </div>
            </section>

            <section class="overview" aria-label="Health summary">
              <article class="tile"><div class="tile-icon">&#128421;</div><div class="tile-label">Server</div><div class="tile-value">{{{{HtmlEncode(_activeScheme.ToUpperInvariant())}}}}&thinsp;:&thinsp;{{{{_config.Port}}}}</div><span class="pill {{{{serverState}}}}">{{{{serverState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#127902;</div><div class="tile-label">Video</div><div class="tile-value">{{{{HtmlEncode(playbackLabel)}}}}</div><span class="pill {{{{playbackState}}}}">{{{{playbackState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#127925;</div><div class="tile-label">Music</div><div class="tile-value">{{{{HtmlEncode(musicLabel)}}}}</div><div class="tile-sub">{{{{_musicIndex.Length}}}} tracks indexed</div><span class="pill {{{{musicState}}}}">{{{{musicState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#128251;</div><div class="tile-label">Radio</div><div class="tile-value">{{{{HtmlEncode(radioLabel)}}}}</div><div class="tile-sub">{{{{radioFavorites.Count}}}} favorites saved</div><span class="pill {{{{(radioStatus.IsPlaying ? (radioStatus.IsStalled ? "warn" : "live") : radioState)}}}}}">{{{{(radioStatus.IsPlaying ? (radioStatus.IsStalled ? "STALLED" : "LIVE") : radioState.ToUpperInvariant())}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#128250;</div><div class="tile-label">Video lib</div><div class="tile-value">{{{{_libraryIndex.Length}}}}</div><div class="tile-sub">{{{{HtmlEncode(libraryLabel)}}}}</div><span class="pill {{{{libraryState}}}}">{{{{libraryState.ToUpperInvariant()}}}}</span></article>
              <article class="tile"><div class="tile-icon">&#127926;</div><div class="tile-label">Music lib</div><div class="tile-value">{{{{_musicIndex.Length}}}}</div><div class="tile-sub">{{{{(_isMusicIndexing ? "Indexingâ€¦" : "Ready")}}}}}</div><span class="pill {{{{(_isMusicIndexing ? "warn" : "ok")}}}}">{{{{(_isMusicIndexing ? "SCANNING" : "OK")}}}}</span></article>
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
                  <div class="metric"><dt>Volume</dt><dd>{{{{(radioStatus.IsPlaying ? $"{Math.Round(radioStatus.Volume * 100)}%" : "â€”")}}}}</dd></div>
                  <div class="metric"><dt>Boost</dt><dd>{{{{(radioStatus.IsPlaying ? $"{radioStatus.Boost:F1}Ã—" : "â€”")}}}}</dd></div>
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

            <div class="footer-note">RemotePlay health page &mdash; generated locally by this media computer &middot; <a href="/api/health" target="_blank">API JSON</a></div>
            </main>
            <script>
            (async()=>{
              try{
                const h=await fetch('/api/health');
                const hj=await h.json();
                const el=document.getElementById('runtime-json');
                if(el)el.textContent=JSON.stringify(hj.runtime,null,2);
              }catch(_){}finally{}
            })();
            </script>

            </body>
            </html>
            """;
        certificate?.Dispose();
        TrySendResponse(ctx, 200, "text/html; charset=utf-8", html);
    }

    private void HandleSettingsPage(HttpListenerContext ctx)
    {
        var (artFiles, artBytes) = BuildAlbumArtCacheHealth();
        var artSizeLabel = artBytes >= 1024 * 1024
            ? $"{artBytes / (1024.0 * 1024):F1} MB"
            : $"{artBytes / 1024.0:F0} KB";

        ctx.Response.AddHeader("Cache-Control", "no-store");
        var html = $$$$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width,initial-scale=1"/>
            <title>RemotePlay &#x2014; Settings</title>
            <style>
            *,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
            body{font-family:system-ui,sans-serif;background:#0d1117;color:#c9d1d9;min-height:100vh;padding:1.5rem 1rem 3rem}
            .page{width:min(860px,100%);margin:0 auto}
            h1{font-size:1.45rem;font-weight:700;margin-bottom:1.2rem;display:flex;align-items:center;gap:.6rem}
            h2{font-size:1rem;font-weight:600;margin-bottom:.8rem;color:#58a6ff}
            .card{background:#161b22;border:1px solid #30363d;border-radius:12px;padding:1.1rem 1.3rem;margin-bottom:1rem}
            .actions{display:flex;flex-wrap:wrap;gap:.5rem;margin-top:.6rem}
            button{cursor:pointer;border:1px solid #30363d;border-radius:8px;padding:.4rem .9rem;background:#21262d;color:#c9d1d9;font-size:.88rem;transition:background .15s}
            button:hover{background:#30363d}
            button.primary{background:#1f6feb;border-color:#1f6feb;color:#fff;font-weight:600}
            button.primary:hover{background:#388bfd}
            button:disabled{opacity:.45;cursor:not-allowed}
            .admin-result{min-height:1.1rem;color:#ffd48a;font-weight:700;font-size:.88rem;margin-top:.5rem}
            .muted{color:#8b949e;font-size:.82rem}
            .divider{border:none;border-top:1px solid #30363d;margin:.8rem 0}
            .sync-log{display:none;margin-top:.75rem;background:rgba(0,0,0,.35);border:1px solid #30363d;border-radius:8px;padding:.6rem .8rem;max-height:220px;overflow-y:auto;font-size:.8rem;line-height:1.6}
            .sync-log .ok{color:#4ade80}.sync-log .warn{color:#fbbf24}.sync-log .info{color:#7dd3fc}.sync-log .dim{color:#6e7681}
            .sync-bar-wrap{display:none;height:5px;border-radius:3px;background:rgba(255,255,255,.1);margin-top:.6rem}
            .sync-bar-fill{height:100%;border-radius:3px;background:#22d3ee;width:0%;transition:width .4s}
            .sync-status-pill{display:none;font-size:.75rem;font-weight:700;letter-spacing:.07em;text-transform:uppercase;color:#22d3ee;background:rgba(34,211,238,.12);border:1px solid rgba(34,211,238,.28);border-radius:4px;padding:.15rem .5rem;animation:syncpulse 1.4s ease-in-out infinite;align-self:center}
            @keyframes syncpulse{0%,100%{opacity:.6}50%{opacity:1}}
            .footer-note{font-size:.78rem;color:#484f58;text-align:center;margin-top:2rem}
            .footer-note a{color:#58a6ff;text-decoration:none}
            </style>
            </head>
            <body><div class="page">
            <h1>&#9881;&#65039; RemotePlay &mdash; Settings</h1>

            <section class="card">
              <h2>&#9881; Admin actions</h2>
              <div class="actions">
                <button class="primary" onclick="rescanLibrary()">&#8635; Rescan video lib</button>
                <button onclick="location.href='/remoteplay.log'">&#128196; Download log</button>
                <button onclick="refreshRuntime()">&#8635; Refresh runtime</button>
              </div>
              <p id="admin-result" class="admin-result"></p>
            </section>

            <section class="card">
              <h2>&#8679; Sync data to peers</h2>
              <dl style="display:grid;grid-template-columns:1fr 1fr;gap:.3rem .8rem;margin-bottom:.8rem">
                <div><dt class="muted">Cached covers</dt><dd>{{{{artFiles}}}} images</dd></div>
                <div><dt class="muted">Cover cache size</dt><dd>{{{{artSizeLabel}}}}</dd></div>
              </dl>
              <hr class="divider"/>
              <p class="muted" style="margin-bottom:.75rem">Pushes covers, lyrics and offsets to every RemotePlay instance found on the network. Only changed data is transferred.</p>
              <div class="actions" style="display:flex;align-items:center;gap:.75rem">
                <button class="primary" id="sync-btn" onclick="startSyncAll()">&#8679; Sync Data</button>
                <span class="sync-status-pill" id="sync-status-pill">&#8679; Syncing…</span>
              </div>
              <div class="sync-bar-wrap" id="sync-bar-wrap"><div class="sync-bar-fill" id="sync-bar-fill"></div></div>
              <div class="sync-log" id="sync-log"></div>
            </section>

            </div><div class="footer-note">RemotePlay settings &mdash; <a href="/">Open remote</a> &middot; <a href="/health" target="_blank">Health</a></div>
            <script>SETTINGS_JS_PLACEHOLDER</script>
            </body>
            </html>
            """
        .Replace("SETTINGS_JS_PLACEHOLDER", SettingsPageScript);
        TrySendResponse(ctx, 200, "text/html; charset=utf-8", html);
    }

    private const string SettingsPageScript = """
        async function refreshRuntime() {
          const result = document.getElementById('admin-result');
          result.textContent = 'Refreshing\u2026';
          try {
            const r = await fetch('/api/health');
            result.textContent = r.ok ? '\u2714 Runtime refreshed.' : 'Refresh failed: ' + r.status;
          } catch(e) { result.textContent = 'Refresh failed: ' + e; }
        }
        async function rescanLibrary() {
          const result = document.getElementById('admin-result');
          result.textContent = 'Starting rescan\u2026';
          try { await fetch('/api/rescan'); result.textContent = 'Video library rescan started.'; }
          catch(e) { result.textContent = 'Rescan failed: ' + e; }
        }
        function _log(msg, cls) {
          const box = document.getElementById('sync-log');
          box.style.display = 'block';
          const line = document.createElement('div');
          if (cls) line.className = cls;
          line.textContent = msg;
          box.appendChild(line);
          box.scrollTop = box.scrollHeight;
        }
        async function startSyncAll() {
          const btn = document.getElementById('sync-btn');
          const barWrap = document.getElementById('sync-bar-wrap');
          const fill = document.getElementById('sync-bar-fill');
          const log = document.getElementById('sync-log');
          btn.disabled = true;
          log.innerHTML = '';
          log.style.display = 'block';
          barWrap.style.display = 'block';
          fill.style.width = '5%';
          const pill = document.getElementById('sync-status-pill');
          if (pill) pill.style.display = 'inline-block';
          _log('Collecting local offsets\u2026', 'dim');
          let _syncCh = null;
          try { _syncCh = new BroadcastChannel('remoteplay-sync'); _syncCh.postMessage('sync-start'); } catch(_) {}
          let offsets = {};
          try { const raw = localStorage.getItem('remotePlayLyricOffsets'); if (raw) offsets = JSON.parse(raw); } catch(_) {}
          const body = JSON.stringify({ offsets });
          try {
            const resp = await fetch('/api/sync/all', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body
            });
            if (!resp.ok || !resp.body) { _log('Server error: ' + resp.status, 'warn'); btn.disabled = false; return; }
            const reader = resp.body.getReader();
            const decoder = new TextDecoder();
            let buf = '';
            let peerCount = 0;
            while (true) {
              const { done, value } = await reader.read();
              if (done) break;
              buf += decoder.decode(value, { stream: true });
              const parts = buf.split('\n\n');
              buf = parts.pop();
              for (const part of parts) {
                const line = part.trim();
                if (!line.startsWith('data:')) continue;
                let ev;
                try { ev = JSON.parse(line.slice(5).trim()); } catch { continue; }
                if (ev.type === 'peer') { peerCount++; fill.style.width = Math.min(10 + peerCount * 15, 85) + '%'; _log('\u2192 ' + ev.message, 'info'); }
                else if (ev.type === 'step') { _log('  ' + ev.message, 'dim'); }
                else if (ev.type === 'step_done') {
                  const s = ev.step.charAt(0).toUpperCase() + ev.step.slice(1);
                  _log('  \u2714 ' + s + ': sent ' + ev.sent + ', skipped ' + ev.skipped + (ev.failed > 0 ? ' \u26a0 failed ' + ev.failed : ''), 'ok');
                }
                else if (ev.type === 'step_error') { _log('  \u26a0 ' + ev.step + ': ' + ev.message, 'warn'); }
                else if (ev.type === 'done') {
                  fill.style.width = '100%';
                  if (ev.peers === 0) { _log(ev.message || 'No peers found.', 'warn'); }
                  else { _log('\u2714 Done \u2014 peers: ' + ev.peers + ' | sent: ' + ev.sent + ' | skipped: ' + ev.skipped + (ev.failed > 0 ? ' | failed: ' + ev.failed : ''), 'ok'); }
                }
              }
            }
          } catch(e) { _log('Sync failed: ' + e, 'warn'); }
          finally {
            btn.disabled = false;
            if (_syncCh) {
              try { _syncCh.postMessage('sync-done'); } catch(_) {}
              try { _syncCh.close(); } catch(_) {}
            }
            setTimeout(() => {
              barWrap.style.display = 'none';
              log.style.display = 'none';
              fill.style.width = '0%';
              if (pill) pill.style.display = 'none';
            }, 5000);
          }
        }
        """;

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

}
