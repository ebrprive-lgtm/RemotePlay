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

        var configJson = System.Text.Json.JsonSerializer.Serialize(BuildSettingsDto(_config), SettingsJsonOptions);

        var movieCount  = _libraryIndex.Length;
        var musicCount  = _musicIndex.Length;

        var ipAddresses = GetLocalIpAddresses()
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .ToArray();
        var primaryIp = ipAddresses.FirstOrDefault() ?? "localhost";
        var serverUrl = $"{_activeScheme}://{primaryIp}:{_config.Port}/";
        var allIpUrls = ipAddresses.Length > 1
            ? string.Join(", ", ipAddresses.Select(ip => $"{_activeScheme}://{ip}:{_config.Port}/"))
            : serverUrl;

        var lastIndexed = _lastIndexRefreshUtc.HasValue
            ? _lastIndexRefreshUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "Not yet";
        var lastMusicIndexed = _lastMusicIndexRefreshUtc.HasValue
            ? _lastMusicIndexRefreshUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "Not yet";

        ctx.Response.AddHeader("Cache-Control", "no-store");
        var html = SettingsPageHtml(artFiles, artSizeLabel, configJson,
            _config.InstanceName, _activeScheme.ToUpperInvariant(), _config.Port, serverUrl, allIpUrls,
            movieCount, musicCount, lastIndexed, lastMusicIndexed, _appVersion,
            _isIndexing, _isMusicIndexing);
        TrySendResponse(ctx, 200, "text/html; charset=utf-8", html);
    }

    private static string SettingsPageHtml(int artFiles, string artSizeLabel, string configJsonEscaped,
        string instanceName, string scheme, int port, string serverUrl, string allIpUrls,
        int movieCount, int musicCount, string lastIndexed, string lastMusicIndexed, string appVersion,
        bool isIndexing, bool isMusicIndexing)
    {
        var j = HtmlEncode(configJsonEscaped).Replace("&quot;", "\"").Replace("&#39;", "'");
        var overviewHtml = BuildOverviewPanelHtml(instanceName, scheme, port, serverUrl, allIpUrls,
            movieCount, musicCount, lastIndexed, lastMusicIndexed, appVersion, isIndexing, isMusicIndexing);
        return SettingsPageTemplate
            .Replace("{{artFiles}}", artFiles.ToString())
            .Replace("{{artSizeLabel}}", artSizeLabel)
            .Replace("SETTINGS_JSON_PLACEHOLDER", j)
            .Replace("{{overviewHtml}}", overviewHtml);
    }

    private static string BuildOverviewPanelHtml(
        string instanceName, string scheme, int port, string serverUrl, string allIpUrls,
        int movieCount, int musicCount, string lastIndexed, string lastMusicIndexed, string appVersion,
        bool isIndexing, bool isMusicIndexing)
    {
        var buildingBadge  = "<span class=\"ov-building\">&#9654; Building&hellip;</span>";
        var movieCountHtml = $"<span class=\"ov-count\" id=\"ov-video-count\">{movieCount:N0}</span>{(isIndexing ? " " + buildingBadge : "")}";
        var musicCountHtml = $"<span class=\"ov-count\" id=\"ov-music-count\">{musicCount:N0}</span>{(isMusicIndexing ? " " + buildingBadge : "")}";
        var videoRefreshedHtml = HtmlEncode(lastIndexed);
        var musicRefreshedHtml = HtmlEncode(lastMusicIndexed);
        return $"""
            <div class="card">
              <h2>&#127758; Server</h2>
              <div class="ov-grid">
                <div class="ov-item"><div class="ov-label">Instance name</div><div class="ov-value">{HtmlEncode(instanceName)}</div></div>
                <div class="ov-item"><div class="ov-label">Protocol</div><div class="ov-value">{HtmlEncode(scheme)}</div></div>
                <div class="ov-item"><div class="ov-label">Port</div><div class="ov-value">{port}</div></div>
                <div class="ov-item"><div class="ov-label">Version</div><div class="ov-value">{HtmlEncode(appVersion)}</div></div>
                <div class="ov-item ov-wide"><div class="ov-label">Server URL</div><div class="ov-value ov-mono"><a href="{HtmlEncode(serverUrl)}" target="_blank" rel="noopener">{HtmlEncode(serverUrl)}</a></div></div>
                {(allIpUrls != serverUrl ? $"""<div class="ov-item ov-wide"><div class="ov-label">All addresses</div><div class="ov-value ov-mono">{HtmlEncode(allIpUrls)}</div></div>""" : "")}
              </div>
            </div>
            <div class="card">
              <h2>&#128250; Video cache</h2>
              <div class="ov-grid" style="margin-bottom:.7rem">
                <div class="ov-item"><div class="ov-label">Movies in cache</div><div class="ov-value">{movieCountHtml}</div></div>
                <div class="ov-item"><div class="ov-label">Last indexed</div><div class="ov-value ov-mono" style="font-size:.83rem">{videoRefreshedHtml}</div></div>
              </div>
              <div class="actions">
                <button class="primary" onclick="ovReindex('video')">&#8635; Reindex video</button>
              </div>
            </div>
            <div class="card">
              <h2>&#127911; Music cache</h2>
              <div class="ov-grid" style="margin-bottom:.7rem">
                <div class="ov-item"><div class="ov-label">Tracks in cache</div><div class="ov-value">{musicCountHtml}</div></div>
                <div class="ov-item"><div class="ov-label">Last indexed</div><div class="ov-value ov-mono" style="font-size:.83rem">{musicRefreshedHtml}</div></div>
              </div>
              <div class="actions">
                <button class="primary" onclick="ovReindex('music')">&#8635; Reindex music</button>
              </div>
            </div>
            <div class="card" style="border-color:#21262d">
              <div class="actions" style="margin-top:0">
                <a href="/health" target="_blank" rel="noopener" class="secondary" style="text-decoration:none;display:inline-flex;align-items:center;gap:.3rem;cursor:pointer;border:1px solid #30363d;border-radius:8px;padding:.38rem .9rem;background:#21262d;color:#c9d1d9;font-size:.875rem">&#10084; Full health dashboard</a>
              </div>
            </div>
            """;
    }

    private const string SettingsPageTemplate = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8"/>
        <meta name="viewport" content="width=device-width,initial-scale=1"/>
        <title>RemotePlay &#x2014; Settings</title>
        <style>
        *,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
        html,body{height:100%}
        body{font-family:system-ui,sans-serif;background:#0d1117;color:#c9d1d9;display:flex;flex-direction:column;min-height:100vh}
        a{color:#58a6ff}
        /* ── top bar ── */
        .top-bar{background:#161b22;border-bottom:1px solid #30363d;padding:.65rem 1.2rem;display:flex;align-items:center;gap:.75rem;flex-wrap:wrap}
        .top-bar h1{font-size:1.05rem;font-weight:700;flex:1;display:flex;align-items:center;gap:.45rem}
        .top-bar .links{display:flex;gap:.5rem;font-size:.82rem;color:#6e7681}
        .top-bar .links a{color:#8b949e;text-decoration:none}
        .top-bar .links a:hover{color:#c9d1d9}
        /* ── layout ── */
        .layout{display:flex;flex:1;overflow:hidden}
        .sidebar{width:178px;min-width:148px;background:#161b22;border-right:1px solid #30363d;padding:.65rem .45rem;display:flex;flex-direction:column;gap:.15rem;overflow-y:auto}
        .sidebar-sep{margin:.4rem .5rem .25rem;font-size:.67rem;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#484f58}
        .cat-btn{width:100%;text-align:left;background:none;border:none;border-radius:7px;padding:.46rem .7rem;color:#8b949e;font-size:.86rem;cursor:pointer;transition:background .13s,color .13s;display:flex;align-items:center;gap:.45rem}
        .cat-btn:hover{background:#21262d;color:#c9d1d9}
        .cat-btn.active{background:#1f6feb1e;color:#58a6ff;font-weight:600}
        .cat-btn .ico{width:1.1em;text-align:center;flex-shrink:0}
        /* ── panels ── */
        .panels{flex:1;overflow-y:auto;padding:1.2rem 1.5rem 4rem}
        .panel{display:none;max-width:800px}
        .panel.active{display:block}
        #panel-log{max-width:1400px}
        /* ── cards ── */
        .card{background:#161b22;border:1px solid #30363d;border-radius:12px;padding:1rem 1.2rem;margin-bottom:.9rem}
        .card h2{font-size:.9rem;font-weight:700;color:#58a6ff;margin-bottom:.85rem;display:flex;align-items:center;gap:.4rem}
        .card h2 .badge{font-size:.68rem;font-weight:600;padding:.08rem .42rem;border-radius:4px;background:#1f6feb22;color:#58a6ff;border:1px solid #1f6feb44;letter-spacing:.02em}
        /* ── form rows ── */
        .row{display:flex;align-items:center;gap:.6rem;margin-bottom:.6rem;flex-wrap:wrap}
        .row label{width:210px;min-width:140px;font-size:.855rem;color:#8b949e;line-height:1.35}
        .row input[type=text],.row input[type=number],.row select,.row textarea{flex:1;min-width:0;background:#0d1117;border:1px solid #30363d;border-radius:6px;padding:.34rem .6rem;color:#c9d1d9;font-size:.86rem;transition:border-color .15s}
        .row input[type=text]:focus,.row input[type=number]:focus,.row select:focus,.row textarea:focus{outline:none;border-color:#388bfd}
        .row input[type=checkbox]{width:17px;height:17px;cursor:pointer;accent-color:#1f6feb;flex-shrink:0}
        .row textarea{resize:vertical;min-height:52px;font-family:monospace}
        .row .narrow{width:100px;flex:none}
        .hint{font-size:.77rem;color:#484f58;margin-top:-.38rem;margin-bottom:.52rem;padding-left:216px;line-height:1.4}
        /* ── path / test ── */
        .path-row{display:flex;align-items:center;gap:.4rem;flex:1;min-width:0}
        .path-row input{flex:1;min-width:0;background:#0d1117;border:1px solid #30363d;border-radius:6px;padding:.34rem .6rem;color:#c9d1d9;font-size:.86rem}
        .test-btn{cursor:pointer;border:1px solid #30363d;border-radius:6px;padding:.3rem .6rem;background:#21262d;color:#c9d1d9;font-size:.8rem;white-space:nowrap;transition:background .13s}
        .test-btn:hover{background:#30363d}
        .test-ok{color:#4ade80;font-size:.79rem}
        .test-err{color:#f87171;font-size:.79rem}
        /* ── buttons ── */
        .actions{display:flex;flex-wrap:wrap;gap:.5rem;margin-top:.75rem}
        button.primary{cursor:pointer;border:1px solid #1f6feb;border-radius:8px;padding:.38rem .95rem;background:#1f6feb;color:#fff;font-size:.875rem;font-weight:600;transition:background .13s}
        button.primary:hover{background:#388bfd}
        button.secondary{cursor:pointer;border:1px solid #30363d;border-radius:8px;padding:.38rem .9rem;background:#21262d;color:#c9d1d9;font-size:.875rem;transition:background .13s}
        button.secondary:hover{background:#30363d}
        button.danger{cursor:pointer;border:1px solid #6e2121;border-radius:8px;padding:.38rem .9rem;background:#2d1212;color:#f87171;font-size:.875rem;transition:background .13s}
        button.danger:hover{background:#3d1919}
        button:disabled{opacity:.42;cursor:not-allowed}
        /* ── save footer ── */
        .save-row{display:flex;align-items:center;gap:.85rem;margin-top:.75rem;flex-wrap:wrap}
        .save-msg{min-height:1.2rem;font-size:.84rem;font-weight:600}
        .save-msg.ok{color:#4ade80}.save-msg.err{color:#f87171}
        /* ── misc ── */
        .divider{border:none;border-top:1px solid #21262d;margin:.75rem 0}
        .muted{color:#8b949e;font-size:.82rem}
        .admin-result{min-height:1.1rem;color:#ffd48a;font-weight:700;font-size:.88rem;margin-top:.5rem}
        /* ── sync ── */
        .sync-log{display:none;margin-top:.75rem;background:rgba(0,0,0,.38);border:1px solid #30363d;border-radius:8px;padding:.6rem .8rem;max-height:220px;overflow-y:auto;font-size:.8rem;line-height:1.6}
        .sync-log .ok{color:#4ade80}.sync-log .warn{color:#fbbf24}.sync-log .info{color:#7dd3fc}.sync-log .dim{color:#6e7681}
        .sync-bar-wrap{display:none;height:4px;border-radius:3px;background:rgba(255,255,255,.08);margin-top:.6rem}
        .sync-bar-fill{height:100%;border-radius:3px;background:#22d3ee;width:0%;transition:width .4s}
        .sync-status-pill{display:none;font-size:.74rem;font-weight:700;letter-spacing:.07em;text-transform:uppercase;color:#22d3ee;background:rgba(34,211,238,.12);border:1px solid rgba(34,211,238,.28);border-radius:4px;padding:.15rem .5rem;animation:syncpulse 1.4s ease-in-out infinite;align-self:center}
        @keyframes syncpulse{0%,100%{opacity:.6}50%{opacity:1}}
        /* ── list entries ── */
        .list-entries{display:flex;flex-direction:column;gap:.32rem;margin-bottom:.4rem}
        .list-entry{display:flex;gap:.4rem;align-items:center}
        .list-entry input{flex:1;background:#0d1117;border:1px solid #30363d;border-radius:6px;padding:.3rem .52rem;color:#c9d1d9;font-size:.84rem}
        .list-entry button{cursor:pointer;border:1px solid #30363d;border-radius:6px;padding:.26rem .52rem;background:#21262d;color:#f87171;font-size:.8rem}
        .list-entry button:hover{background:#30363d}
        .add-entry-btn{cursor:pointer;border:1px dashed #30363d;border-radius:6px;padding:.28rem .65rem;background:none;color:#58a6ff;font-size:.81rem;transition:border-color .13s}
        .add-entry-btn:hover{border-color:#388bfd}
        /* ── credentials ── */
        .cred-row{display:grid;grid-template-columns:1fr 1fr 1fr auto;gap:.4rem;margin-bottom:.32rem}
        .cred-row input{background:#0d1117;border:1px solid #30363d;border-radius:6px;padding:.3rem .5rem;color:#c9d1d9;font-size:.82rem}
        /* ── overview ── */
        .ov-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.5rem .7rem;margin-top:.2rem}
        .ov-item{background:rgba(255,255,255,.025);border:1px solid #30363d;border-radius:10px;padding:.52rem .7rem}
        .ov-wide{grid-column:1/-1}
        .ov-label{color:#8b949e;font-size:.66rem;font-weight:700;text-transform:uppercase;letter-spacing:.05em;margin-bottom:.2rem}
        .ov-value{color:#e8f0ff;font-size:.91rem;font-weight:600;word-break:break-all}
        .ov-mono{font-family:Consolas,ui-monospace,monospace;font-size:.81rem}
        .ov-count{font-size:1.4rem;font-weight:900;color:#58a6ff}
        .ov-building{display:inline-flex;align-items:center;gap:.25rem;font-size:.71rem;font-weight:700;color:#fbbf24;background:rgba(251,191,36,.12);border:1px solid rgba(251,191,36,.3);border-radius:4px;padding:.1rem .4rem;vertical-align:middle;animation:ov-pulse 1.4s ease-in-out infinite}
        @keyframes ov-pulse{0%,100%{opacity:.55}50%{opacity:1}}
        /* ── info callout ── */
        .callout{display:flex;gap:.55rem;align-items:flex-start;background:rgba(31,111,235,.07);border:1px solid rgba(31,111,235,.22);border-radius:9px;padding:.65rem .85rem;margin-bottom:.75rem;font-size:.83rem;color:#7fbcff;line-height:1.45}
        .callout .ico{font-size:1rem;flex-shrink:0;margin-top:.05rem}
        .callout.warn{background:rgba(251,191,36,.07);border-color:rgba(251,191,36,.25);color:#fcd34d}
        .callout.ok{background:rgba(74,222,128,.06);border-color:rgba(74,222,128,.22);color:#86efac}
        /* -- log viewer -- */
        .log-toolbar{display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:.5rem;margin-bottom:.55rem}
        .log-filters{display:flex;gap:.4rem;align-items:center;flex-wrap:wrap}
        .log-filters select,.log-filters input{background:#0d1117;border:1px solid #30363d;border-radius:6px;padding:.28rem .5rem;color:#c9d1d9;font-size:.8rem}
        .log-actions{display:flex;gap:.4rem;align-items:center;flex-wrap:wrap}
        .log-viewer{background:#0d1117;border:1px solid #30363d;border-radius:8px;height:calc(100vh - 280px);min-height:300px;max-height:calc(100vh - 200px);overflow-y:auto;overflow-x:auto;font-family:Consolas,ui-monospace,monospace;font-size:.72rem;line-height:1.4;outline:none;user-select:none}
        .log-table{width:100%;border-collapse:collapse;table-layout:fixed}
        .log-table td{padding:.1rem .45rem;vertical-align:middle;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .log-table .lv-msg{white-space:pre-wrap;word-break:break-all}
        .log-table tr.lv-INFO .lv-badge{color:#7dd3fc}
        .log-table tr.lv-DETAIL .lv-badge{color:#8b949e}
        .log-table tr.lv-WARN .lv-badge{color:#fbbf24}
        .log-table tr.lv-ERROR .lv-badge{color:#f87171;font-weight:700}
        .log-table .lv-ts{color:#484f58;white-space:nowrap;width:135px}
        .log-table .lv-badge-col{white-space:nowrap;width:76px;text-align:center}
        .log-table .lv-src{color:#7c8590;white-space:nowrap;width:90px;overflow:hidden;text-overflow:ellipsis}
        .log-table .lv-msg{color:#c9d1d9;width:auto}
        .log-table tr.selected td{background:rgba(88,166,255,.12)}
        .log-table tr:hover td{background:rgba(255,255,255,.03)}
        .log-table tr.selected:hover td{background:rgba(88,166,255,.18)}
        /* ── footer ── */
        .footer{font-size:.74rem;color:#484f58;text-align:center;padding:.7rem 0;border-top:1px solid #21262d}
        /* ── responsive ── */
        @media(max-width:580px){
          .sidebar{width:46px;min-width:46px}.sidebar-sep,.cat-btn span.lbl{display:none}
          .cat-btn{justify-content:center;padding:.48rem}.cat-btn .ico{width:auto}
          .row label{width:100%}.hint{padding-left:0}
          .ov-grid{grid-template-columns:1fr}
        }
        </style>
        </head>
        <body>
        <div class="top-bar">
          <h1><span>&#9881;&#65039;</span> RemotePlay &mdash; Settings</h1>
          <div class="links">
            <a href="/">&#8592; Remote</a> &middot;
            <a href="/health" target="_blank" rel="noopener">Health</a> &middot;
            <a href="/setup">Setup</a>
          </div>
        </div>
        <div class="layout">
          <nav class="sidebar">
            <button class="cat-btn active" onclick="showCat('overview')" id="cat-overview"><span class="ico">&#9432;</span><span class="lbl">Overview</span></button>
            <div class="sidebar-sep">Library</div>
            <button class="cat-btn" onclick="showCat('library')" id="cat-library"><span class="ico">&#128214;</span><span class="lbl">Paths</span></button>
            <button class="cat-btn" onclick="showCat('scanning')" id="cat-scanning"><span class="ico">&#128269;</span><span class="lbl">Scanning</span></button>
            <div class="sidebar-sep">Playback</div>
            <button class="cat-btn" onclick="showCat('playback')" id="cat-playback"><span class="ico">&#9654;</span><span class="lbl">Video</span></button>
            <button class="cat-btn" onclick="showCat('audio')" id="cat-audio"><span class="ico">&#127911;</span><span class="lbl">Audio</span></button>
            <div class="sidebar-sep">System</div>
            <button class="cat-btn" onclick="showCat('server')" id="cat-server"><span class="ico">&#128187;</span><span class="lbl">Server</span></button>
            <button class="cat-btn" onclick="showCat('application')" id="cat-application"><span class="ico">&#8635;</span><span class="lbl">Application</span></button>
            <button class="cat-btn" onclick="showCat('appearance')" id="cat-appearance"><span class="ico">&#127912;</span><span class="lbl">Appearance</span></button>
            <button class="cat-btn" onclick="showCat('desktop')" id="cat-desktop"><span class="ico">&#128444;</span><span class="lbl">Desktop</span></button>
            <button class="cat-btn" onclick="showCat('tools')" id="cat-tools"><span class="ico">&#128295;</span><span class="lbl">Tools</span></button>
            <button class="cat-btn" onclick="showCat('log')" id="cat-log"><span class="ico">&#128221;</span><span class="lbl">Log</span></button>
          </nav>
          <div class="panels">

            <!-- ═══ OVERVIEW ═══ -->
            <div class="panel active" id="panel-overview">
              {{overviewHtml}}
            </div>

            <!-- ═══ LIBRARY PATHS ═══ -->
            <div class="panel" id="panel-library">
              <div class="card">
                <h2>&#128250; Video library paths</h2>
                <p class="muted" style="margin-bottom:.7rem">Add every folder root that contains movies or TV shows. Sub-folders are scanned recursively.</p>
                <div class="list-entries" id="extra-movies-list"></div>
                <button class="add-entry-btn" onclick="addListEntry('extra-movies-list','addlMovies',true)">+ Add video path</button>
              </div>
              <div class="card">
                <h2>&#127911; Music library paths</h2>
                <p class="muted" style="margin-bottom:.7rem">Add every folder root that contains music files.</p>
                <div class="list-entries" id="extra-music-list"></div>
                <button class="add-entry-btn" onclick="addListEntry('extra-music-list','addlMusic',true)">+ Add music path</button>
              </div>
              <div class="card">
                <h2>&#128101; Network share credentials</h2>
                <div class="callout"><span class="ico">&#128274;</span> Credentials are stored in plain text in the config file. Keep the config file private and accessible only to the service account.</div>
                <div style="display:grid;grid-template-columns:1fr 1fr 1fr auto;gap:.35rem .4rem;margin-bottom:.35rem;padding:0 .1rem">
                  <span class="muted" style="font-size:.75rem">UNC path prefix</span>
                  <span class="muted" style="font-size:.75rem">Username</span>
                  <span class="muted" style="font-size:.75rem">Password</span>
                  <span></span>
                </div>
                <div id="cred-list"></div>
                <button class="add-entry-btn" onclick="addCredRow()">+ Add credentials</button>
              </div>
              </div>

            <!-- ═══ SCANNING ═══ -->
            <div class="panel" id="panel-scanning">
              <div class="card">
                <h2>&#128269; Scan schedule</h2>
                <div class="row">
                  <label>Auto-rescan interval</label>
                  <input type="number" id="LibraryRescanDelayMinutes" min="0" class="narrow"/> <span class="muted" style="font-size:.82rem">minutes</span>
                </div>
                <div class="hint">How often the video library is rescanned while the app is running. Set to 0 to scan only at startup.</div>
                <div class="row">
                  <label>Results per page</label>
                  <input type="number" id="LibraryPageSize" min="25" class="narrow"/>
                </div>
                <div class="hint">Number of movies shown per page in the browser.</div>
              </div>
              <div class="card">
                <h2>&#128247; Thumbnails &amp; artwork</h2>
                <div class="row"><label>Generate thumbnails</label><input type="checkbox" id="EnableThumbnailGeneration"/></div>
                <div class="hint">Creates a JPEG preview for each video file on first access. Disable on slow storage.</div>
              </div>
              <div class="card">
                <h2>&#128193; File filters</h2>
                <div class="row">
                  <label>Ignored folder names</label>
                  <input type="text" id="IgnoredLibraryFolders" placeholder="Subs, Alt, Extras"/>
                </div>
                <div class="hint">Comma-separated folder names that will be skipped entirely during scanning (case-insensitive).</div>
                <div class="row">
                  <label>Video file extensions</label>
                  <input type="text" id="VideoFileExtensions" placeholder=".mp4, .mkv, .avi"/>
                </div>
                <div class="hint">Comma-separated. Only files with these extensions are indexed.</div>
                <div class="row">
                  <label>Music file extensions</label>
                  <input type="text" id="MusicFileExtensions" placeholder=".mp3, .flac, .aac"/>
                </div>
                <div class="hint">Comma-separated. Only files with these extensions are added to the music library.</div>
              </div>
              </div>

            <!-- ═══ PLAYBACK ═══ -->
            <div class="panel" id="panel-playback">
              <div class="card">
                <h2>&#127760; Language preferences</h2>
                <div class="row">
                  <label>Preferred audio language</label>
                  <select id="PreferredAudioLanguage">
                    <option value="eng">English</option>
                    <option value="fra">French</option>
                    <option value="deu">German</option>
                    <option value="nld">Dutch</option>
                    <option value="spa">Spanish</option>
                    <option value="por">Portuguese</option>
                  </select>
                </div>
                <div class="row">
                  <label>Subtitle language</label>
                  <select id="PreferredSubtitleLanguage">
                    <option value="eng">English</option>
                    <option value="fra">French</option>
                    <option value="deu">German</option>
                    <option value="nld">Dutch</option>
                    <option value="spa">Spanish</option>
                    <option value="por">Portuguese</option>
                  </select>
                </div>
                <div class="row">
                  <label>Secondary subtitle language</label>
                  <select id="SecondarySubtitleLanguage">
                    <option value="">None</option>
                    <option value="eng">English</option>
                    <option value="fra">French</option>
                    <option value="deu">German</option>
                    <option value="nld">Dutch</option>
                    <option value="spa">Spanish</option>
                    <option value="por">Portuguese</option>
                  </select>
                </div>
                <div class="row"><label>Prefer forced subtitles</label><input type="checkbox" id="PreferForcedSubtitles"/></div>
                <div class="hint">When available, always load the forced subtitle track for foreign-language inserts.</div>
              </div>
              <div class="card">
                <h2>&#9654; Playback behaviour</h2>
                <div class="row">
                  <label>When video ends</label>
                  <select id="PlaybackEndBehavior">
                    <option value="Stop">Stop</option>
                    <option value="PlayNext">Play next in folder</option>
                    <option value="Repeat">Repeat</option>
                  </select>
                </div>
                <div class="row">
                  <label>Playback history limit</label>
                  <input type="number" id="PlaybackHistoryLimit" min="1" class="narrow"/> <span class="muted" style="font-size:.82rem">items</span>
                </div>
                <div class="hint">Number of recently-played items kept in the history list.</div>
              </div>
              </div>

            <!-- ═══ AUDIO ═══ -->
            <div class="panel" id="panel-audio">
              <div class="card">
                <h2>&#127911; Audio output</h2>
                <div class="row">
                  <label>Music audio device</label>
                  <select id="MusicAudioDeviceId" style="flex:1"></select>
                </div>
                <div class="hint">Output device used for music playback. The first entry uses the Windows default output. Changes take effect on the next track.</div>
              </div>
              </div>

            <!-- ═══ SERVER ═══ -->
            <div class="panel" id="panel-server">
              <div class="callout" style="margin-bottom:.9rem"><span class="ico">&#128161;</span> Port and HTTPS changes take effect after restarting the application. Other server settings apply immediately.</div>
              <div class="card">
                <h2>&#128204; Identity</h2>
                <div class="row">
                  <label>Instance name</label>
                  <input type="text" id="ServerInstanceName" placeholder="My RemotePlay"/>
                </div>
                <div class="hint">Friendly name shown to peers on the network and on the Setup page.</div>
              </div>
              <div class="card">
                <h2>&#127760; Connection</h2>
                <div class="row">
                  <label>HTTP port</label>
                  <div class="path-row" style="align-items:center;gap:.5rem">
                    <input type="number" id="Port" min="1" max="65535" class="narrow" oninput="_portTested=false;document.getElementById('port-test-result').textContent='';document.getElementById('port-test-result').className='hint'"/>
                    <button class="test-btn" onclick="testPort()">Test</button>
                  </div>
                </div>
                <span id="port-test-result" class="hint"></span>
                <div class="hint">Enter a new port and press <strong>Test</strong> to verify it is available. The change takes effect after a reboot.</div>
                <div class="row">
                  <label>Use HTTPS</label>
                  <input type="checkbox" id="UseHttps" disabled title="Restart required to toggle HTTPS"/>
                </div>
                <div class="hint">Requires a restart. A self-signed certificate is created automatically on first use.</div>
                <div class="row">
                  <label>HTTPS certificate</label>
                  <a href="/cert.cer" class="secondary" style="font-size:.83rem;padding:.3rem .7rem;border:1px solid #30363d;border-radius:7px;text-decoration:none;color:#c9d1d9;display:inline-block" download>&#128196; Download .cer</a>
                </div>
                <div class="hint">Install this certificate as a Trusted Root on client devices to eliminate browser HTTPS warnings.</div>
              </div>
              <div class="card">
                <h2>&#128274; Security</h2>
                <p class="muted" style="margin-bottom:.7rem">Limits how many HTTP requests a single IP address can make within a sliding time window. Protects against accidental hammering and basic abuse.</p>
                <div class="row">
                  <label>Max requests per IP</label>
                  <input type="number" id="MaxRequestsPerIpPerWindow" min="0" class="narrow"/>
                  <span class="muted" style="font-size:.82rem">per window</span>
                </div>
                <div class="hint">Set to 0 to disable rate limiting entirely.</div>
                <div class="row">
                  <label>Window duration</label>
                  <input type="number" id="RateLimitWindowSeconds" min="1" class="narrow"/>
                  <span class="muted" style="font-size:.82rem">seconds</span>
                </div>
              </div>
              <div class="card" style="border-color:#da3633">
                <h2 style="color:#f85149">&#9888; Danger zone</h2>
                <p class="muted" style="margin-bottom:.85rem">Restart the RemotePlay application. Active playback will be interrupted.</p>
                <div class="actions">
                  <button style="background:#da3633;color:#fff;border:none;border-radius:7px;padding:.45rem 1.1rem;font-size:.88rem;cursor:pointer;font-weight:600" onclick="rebootServer()">&#9211; Reboot server</button>
                </div>
                <p id="reboot-result" class="admin-result"></p>
              </div>
              </div>

            <!-- ═══ APPLICATION ═══ -->
            <div class="panel" id="panel-application">
              <div class="card">
                <h2>&#8635; Auto-update source</h2>
                <div class="row">
                  <label>Source path&nbsp;/&nbsp;URL</label>
                  <div class="path-row">
                    <input type="text" id="UpdateSourcePath" placeholder="\\server\share\RemotePlay  or  https://..."/>
                    <button class="test-btn" onclick="testPathOrUrl('UpdateSourcePath','update-source-result')">Test</button>
                  </div>
                </div>
                <span id="update-source-result" class="hint"></span>
                <div class="hint">A local folder path or HTTP URL that contains updated application files. Leave blank to disable auto-update.</div>
                <div class="row">
                  <label>Check interval</label>
                  <input type="number" id="AutoUpdateIntervalMinutes" min="0" class="narrow"/>
                  <span class="muted" style="font-size:.82rem">minutes &nbsp;(0 = startup only)</span>
                </div>
              </div>
              <div class="card">
                <h2>&#9654; Startup</h2>
                <p class="muted" style="margin-bottom:.75rem">Control how RemotePlay behaves when Windows starts and when its window is closed.</p>
                <div class="row"><label>Start with Windows</label><input type="checkbox" id="StartWithWindows"/></div>
                <div class="hint" style="margin-bottom:.85rem">Adds or removes RemotePlay from your Windows startup programs.</div>
                <div class="row"><label>Keep in system tray when closed</label><input type="checkbox" id="UseTrayIcon"/></div>
                <div class="hint">When enabled, closing the window hides RemotePlay to the tray instead of exiting.</div>
              </div>
              </div>

            <!-- ═══ APPEARANCE ═══ -->
            <div class="panel" id="panel-appearance">
              <div class="card">
                <h2>&#127912; Developer &amp; diagnostics modes</h2>
                <div class="row"><label>Expert mode</label><input type="checkbox" id="ExpertMode"/></div>
                <div class="hint">Reveals advanced controls in the web UI (e.g. dynamic folder creation, codec hints).</div>
                <div class="row"><label>Debug mode</label><input type="checkbox" id="DebugMode"/></div>
                <div class="hint">Reveals debug-only controls (e.g. cache reset, raw index inspection). Not recommended for normal use.</div>
              </div>
              </div>

            <!-- ═══ DESKTOP ═══ -->
            <div class="panel" id="panel-desktop">
              <div class="card">
                <h2>&#128444; Display</h2>
                <p class="muted" style="margin-bottom:.75rem">Choose which monitor the fullscreen video window opens on.</p>
                <div class="row">
                  <label>Fullscreen display</label>
                  <select id="PreferredDisplayIndex" data-numeric="1" style="min-width:260px">
                    <option value="-1">Primary monitor</option>
                  </select>
                </div>
                <div class="hint">The list is populated from the monitors detected on the host machine.</div>
              </div>
              </div>

            <!-- ═══ TOOLS ═══ -->
            <div class="panel" id="panel-tools">
              <div class="card">
                <h2>&#8679; Peer sync</h2>
                <p class="muted" style="margin-bottom:.75rem">Pushes cached artwork, lyrics and playback offsets to every RemotePlay instance discovered on the local network.</p>
                <dl class="ov-grid" style="margin-bottom:.85rem">
                  <div class="ov-item"><div class="ov-label">Cached covers</div><div class="ov-value">{{artFiles}} images</div></div>
                  <div class="ov-item"><div class="ov-label">Cover cache size</div><div class="ov-value">{{artSizeLabel}}</div></div>
                </dl>
                <div class="row">
                  <label>Auto-sync interval</label>
                  <select id="SyncIntervalHours" data-numeric="1">
                    <option value="0">Off &mdash; manual only</option>
                    <option value="4">Every 4 hours</option>
                    <option value="12">Every 12 hours</option>
                    <option value="24">Every day</option>
                    <option value="168">Every week</option>
                  </select>
                </div>
                <div class="row"><label>Sync at startup</label><input type="checkbox" id="SyncAtStartup"/></div>
                <div class="hint">Trigger a full sync automatically each time the application starts.</div>
                <div class="actions" style="align-items:center;gap:.75rem">
                  <button class="primary" id="sync-btn" onclick="startSyncAll()">&#8679; Sync now</button>
                  <span class="sync-status-pill" id="sync-status-pill">&#8679; Syncing&#8230;</span>
                </div>
                <div class="sync-bar-wrap" id="sync-bar-wrap"><div class="sync-bar-fill" id="sync-bar-fill"></div></div>
                <div class="sync-log" id="sync-log"></div>
              </div>
              <div class="card">
                <h2>&#9881; Maintenance</h2>
                <p class="muted" style="margin-bottom:.7rem">One-time actions that affect the running application state.</p>
                <div class="actions">
                  <button class="primary" onclick="rescanLibrary()">&#8635; Rescan library</button>
                  <button class="secondary" onclick="refreshRuntime()">&#8635; Refresh runtime</button>
                </div>
                <p id="admin-result" class="admin-result"></p>
              </div>
              </div>

            <!-- ═══ LOG ═══ -->
            <div class="panel" id="panel-log">
              <div class="card">
                <h2>&#128221; Application log</h2>
                <p class="muted" style="margin-bottom:.75rem">Most-recent entries at the bottom. Click any row to select it, then copy to clipboard.</p>
                <div class="log-toolbar" id="log-toolbar">
                  <div class="log-filters">
                    <select id="log-filter-level" onchange="applyLogFilters()" title="Filter by severity">
                      <option value="">All severities</option>
                      <option value="INFO">Info</option>
                      <option value="DETAIL">Detail</option>
                      <option value="WARN">Warning</option>
                      <option value="ERROR">Error</option>
                    </select>
                    <input id="log-filter-source" type="text" placeholder="Filter source&hellip;" oninput="applyLogFilters()" title="Filter by source / category" style="width:140px"/>
                  </div>
                  <div class="log-actions">
                    <span id="log-sel-info" class="muted" style="font-size:.78rem"></span>
                    <button class="secondary" id="log-copy-btn" onclick="copyLogSelection()" disabled title="Copy selected rows to clipboard">&#128203; Copy</button>
                    <button class="secondary" onclick="clearLog()" title="Clear the log file">&#128465; Clear log</button>
                    <button class="secondary" onclick="loadLog()" title="Refresh">&#8635; Refresh</button>
                    <a href="/remoteplay.log" class="secondary" style="font-size:.83rem;padding:.3rem .7rem;border:1px solid #30363d;border-radius:7px;text-decoration:none;color:#c9d1d9;display:inline-block" download>&#128196; Download log</a>
                  </div>
                </div>
                <div class="log-viewer" id="log-viewer" tabindex="0">
                  <table class="log-table" id="log-table"><tbody id="log-tbody"></tbody></table>
                </div>
                <div style="display:flex;align-items:center;justify-content:space-between;margin-top:.45rem">
                  <span class="muted" style="font-size:.78rem" id="log-footer-info"></span>
                  <span class="muted" style="font-size:.78rem">Click a row to select &bull; Shift-click for range &bull; Ctrl-click to multi-select</span>
                </div>
              </div>
            </div>

          </div>
        </div>
        <div class="footer">RemotePlay settings</div>
        <script>
        const INITIAL_CFG = SETTINGS_JSON_PLACEHOLDER;
        function showCat(name) {
          document.querySelectorAll('.cat-btn').forEach(b=>b.classList.remove('active'));
          document.querySelectorAll('.panel').forEach(p=>p.classList.remove('active'));
          const btn=document.getElementById('cat-'+name), panel=document.getElementById('panel-'+name);
          if(btn)btn.classList.add('active');
          if(panel)panel.classList.add('active');
          history.replaceState(null,'','?cat='+name);
          if(name==='overview') startOverviewPoll();
          if(name==='log') loadLog();
          if(name==='desktop'){
            const sel=document.getElementById('PreferredDisplayIndex');
            const cur=sel?Number(sel.value):-1;
            loadDisplaySelect(cur);
          }
        }
        let _ovPollTimer=null;
        let _ovVideoFloor=0, _ovMusicFloor=0; // never let displayed count drop below last known value during a scan
        function startOverviewPoll(){
          if(_ovPollTimer)return; // already running
          _ovPollTimer=setInterval(async()=>{
            const panel=document.getElementById('panel-overview');
            if(!panel||!panel.classList.contains('active')){stopOverviewPoll();return;}
            try{
              const r=await fetch('/api/library-status');
              if(r.ok){
                const s=await r.json();
                const count=s.IndexedFiles??0;
                const el=document.getElementById('ov-video-count');
                if(el){
                  if(s.IsScanning){
                    // during scan: only advance the floor, never show a drop
                    if(count>_ovVideoFloor)_ovVideoFloor=count;
                    el.textContent=_ovVideoFloor.toLocaleString();
                  } else {
                    // scan finished: show real value and reset floor
                    _ovVideoFloor=count;
                    el.textContent=count.toLocaleString();
                  }
                }
              }
            }catch(_){}
            try{
              const r=await fetch('/api/music/status');
              if(r.ok){
                const s=await r.json();
                const count=s.indexedFiles??0;
                const isIdx=s.isScanning??false;
                const el=document.getElementById('ov-music-count');
                if(el){
                  if(isIdx){
                    if(count>_ovMusicFloor)_ovMusicFloor=count;
                    el.textContent=_ovMusicFloor.toLocaleString();
                  } else {
                    _ovMusicFloor=count;
                    el.textContent=count.toLocaleString();
                  }
                }
              }
            }catch(_){}
          },2000);
        }
        function stopOverviewPoll(){if(_ovPollTimer){clearInterval(_ovPollTimer);_ovPollTimer=null;}}
        function populate(cfg) {
          const set=(id,v)=>{const el=document.getElementById(id);if(!el)return;if(el.type==='checkbox')el.checked=!!v;else el.value=v??'';};
          // Library / Scanning
          set('ServerInstanceName',cfg.instanceName);
          if(!_portLoaded){set('Port',cfg.port);_portLoaded=true;}_portTested=false;
          set('LibraryRescanDelayMinutes',cfg.libraryRescanDelayMinutes);
          set('LibraryPageSize',cfg.libraryPageSize);
          set('EnableThumbnailGeneration',cfg.enableThumbnailGeneration);
          set('IgnoredLibraryFolders',cfg.ignoredLibraryFolders);
          set('VideoFileExtensions',cfg.videoFileExtensions);
          set('MusicFileExtensions',cfg.musicFileExtensions);
          // Playback
          set('PreferredAudioLanguage',cfg.preferredAudioLanguage);
          set('PreferredSubtitleLanguage',cfg.preferredSubtitleLanguage);
          set('SecondarySubtitleLanguage',cfg.secondarySubtitleLanguage);
          set('PreferForcedSubtitles',cfg.preferForcedSubtitles);
          set('PlaybackEndBehavior',cfg.playbackEndBehavior);
          set('PlaybackHistoryLimit',cfg.playbackHistoryLimit);
          // Audio
          populateAudioDeviceSelect(cfg.musicAudioDeviceId);
          // Security
          set('MaxRequestsPerIpPerWindow',cfg.maxRequestsPerIpPerWindow);
          set('RateLimitWindowSeconds',cfg.rateLimitWindowSeconds);
          // Updates
          set('UpdateSourcePath',cfg.updateSourcePath);
          set('AutoUpdateIntervalMinutes',cfg.autoUpdateIntervalMinutes);
          // Appearance
          set('ExpertMode',cfg.expertMode);
          set('DebugMode',cfg.debugMode);
          // Desktop
          set('StartWithWindows',cfg.startWithWindows);
          set('UseTrayIcon',cfg.useTrayIcon);
          populateDisplaySelect(cfg.preferredDisplayIndex??-1);
          // Tools / Sync
          set('SyncIntervalHours',cfg.syncIntervalHours??0);
          set('SyncAtStartup',cfg.syncAtStartup);
          // Lists
          populateList('extra-movies-list','addlMovies',cfg.additionalMoviesPaths||[],true);
          populateList('extra-music-list','addlMusic',cfg.additionalMusicPaths||[],true);
          populateCredentials(cfg.networkShareCredentials||[]);
        }
        function populateList(listId,key,items,isPath) {
          const el=document.getElementById(listId); el.innerHTML='';
          (items.length>0?items:['']).forEach(v=>addListEntry(listId,key,isPath,v));
        }
        function addListEntry(listId,key,isPath,value) {
          const el=document.getElementById(listId); const row=document.createElement('div'); row.className='list-entry';
          const rid='lr-'+Math.random().toString(36).slice(2);
          row.innerHTML=`<input type="text" data-key="${key}" value="${escAttr(value||'')}" placeholder="path..." />`
            +(isPath?`<button class="test-btn" onclick="testPathEntry(this,'${rid}')">Test</button><span id="${rid}" class="test-ok" style="font-size:.78rem"></span>`:'')
            +`<button onclick="this.closest('.list-entry').remove()" title="Remove">&#10005;</button>`;
          el.appendChild(row);
        }
        function populateCredentials(creds) {
          const el=document.getElementById('cred-list'); el.innerHTML='';
          creds.forEach(c=>addCredRow(c.path,c.username,c.password));
        }
        function addCredRow(path,user,pass) {
          const el=document.getElementById('cred-list'); const row=document.createElement('div'); row.className='cred-row';
          row.innerHTML=`<input type="text" data-cred="path" value="${escAttr(path||'')}" placeholder="\\\\server\\share" />`
            +`<input type="text" data-cred="username" value="${escAttr(user||'')}" placeholder="Username" />`
            +`<input type="password" data-cred="password" value="${escAttr(pass||'')}" placeholder="Password" />`
            +`<button onclick="this.closest('.cred-row').remove()" title="Remove">&#10005;</button>`;
          el.appendChild(row);
        }
        const CAT_FIELDS={
          library:[],   // handled entirely by list/cred collectors in collectCategory
          scanning:['LibraryRescanDelayMinutes','LibraryPageSize','EnableThumbnailGeneration','IgnoredLibraryFolders','VideoFileExtensions','MusicFileExtensions'],
          playback:['PreferredAudioLanguage','PreferredSubtitleLanguage','SecondarySubtitleLanguage','PreferForcedSubtitles','PlaybackEndBehavior','PlaybackHistoryLimit'],
          audio:['MusicAudioDeviceId'],
          server:[],    // ServerInstanceName, MaxRequestsPerIpPerWindow, RateLimitWindowSeconds handled in collectCategory
          application:['UpdateSourcePath','AutoUpdateIntervalMinutes','StartWithWindows','UseTrayIcon'],
          appearance:['ExpertMode','DebugMode'],
          desktop:['PreferredDisplayIndex'],
          tools:['SyncIntervalHours','SyncAtStartup'],
        };
        function readField(id){const el=document.getElementById(id);if(!el)return undefined;if(el.type==='checkbox')return el.checked;const v=el.value.trim();if(el.type==='number'||el.tagName==='SELECT'&&el.dataset.numeric)return v===''?null:Number(v);return v;}
        function collectCategory(cat){
          const p={};(CAT_FIELDS[cat]||[]).forEach(id=>{p[id]=readField(id);});
          if(cat==='library'){
            p['AdditionalMoviesPaths']=[...document.querySelectorAll('#extra-movies-list [data-key=addlMovies]')].map(i=>i.value.trim()).filter(Boolean);
            p['AdditionalMusicPaths']=[...document.querySelectorAll('#extra-music-list [data-key=addlMusic]')].map(i=>i.value.trim()).filter(Boolean);
            p['NetworkShareCredentials']=[...document.querySelectorAll('#cred-list .cred-row')].map(row=>({path:row.querySelector('[data-cred=path]')?.value.trim()||'',username:row.querySelector('[data-cred=username]')?.value.trim()||'',password:row.querySelector('[data-cred=password]')?.value||''})).filter(c=>c.path);
          }
          // ServerInstanceName is a display alias for InstanceName on the Server panel
          if(cat==='server'){const v=readField('ServerInstanceName');if(v!==undefined)p['InstanceName']=v;const r1=readField('MaxRequestsPerIpPerWindow');if(r1!==undefined)p['MaxRequestsPerIpPerWindow']=r1;const r2=readField('RateLimitWindowSeconds');if(r2!==undefined)p['RateLimitWindowSeconds']=r2;if(_portTested){const pv=readField('Port');if(pv!==undefined&&pv!==null)p['Port']=pv;}}
          return p;
        }
        let _autoSaveTimer=null;
        function scheduleAutoSave(cat){
          clearTimeout(_autoSaveTimer);
          _autoSaveTimer=setTimeout(()=>saveCategory(cat),600);
        }
        function bindCategoryAutoSave(cat){
          const panel=document.getElementById('panel-'+cat);
          if(!panel)return;
          panel.querySelectorAll('input,select,textarea').forEach(el=>{
            if(el.id==='Port')return; // Port requires Test before save, excluded from auto-save
            const evt=el.type==='range'?'change':(el.type==='text'||el.type==='number'||el.tagName==='TEXTAREA'?'change':'change');
            el.addEventListener(evt,()=>scheduleAutoSave(cat));
          });
        }
        async function saveCategory(cat){
          try{
            const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(collectCategory(cat))});
            const d=await r.json();
            if(d.ok&&d.settings)populate(d.settings);
          }catch(e){console.warn('Auto-save failed:',e);}
        }
        async function testPath(inputId,resultId){
          const val=document.getElementById(inputId)?.value.trim();
          const el=document.getElementById(resultId); if(!el)return;
          el.textContent='Testing\u2026'; el.className='hint';
          const r=await fetch('/api/settings/test-path?path='+encodeURIComponent(val));
          const d=await r.json();
          el.textContent=d.exists?'\u2714 Found: '+d.resolved:'\u26a0 Not found'+(d.error?': '+d.error:'');
          el.className=d.exists?'test-ok':'test-err';
        }
        async function testPathEntry(btn,resultId){
          const input=btn.closest('.list-entry').querySelector('input');
          const el=document.getElementById(resultId); if(!el)return;
          el.textContent='Testing\u2026';
          const r=await fetch('/api/settings/test-path?path='+encodeURIComponent(input?.value.trim()||''));
          const d=await r.json();
          el.textContent=d.exists?'\u2714':'\u26a0 Not found';
          el.className=d.exists?'test-ok':'test-err';
        }
        async function testPathOrUrl(inputId,resultId){
          const val=document.getElementById(inputId)?.value.trim()||'';
          const el=document.getElementById(resultId); if(!el)return;
          el.textContent='Testing\u2026'; el.className='hint';
          if(/^https?:\/\//i.test(val)){
            const r=await fetch('/api/settings/test-url?url='+encodeURIComponent(val));
            const d=await r.json();
            el.textContent=d.reachable?'\u2714 Reachable (HTTP '+d.status+')':'\u26a0 '+(d.error||'Unreachable');
            el.className=d.reachable?'test-ok':'test-err';
          }else{await testPath(inputId,resultId);}
        }
        async function refreshRuntime(){
          const result=document.getElementById('admin-result'); result.textContent='Refreshing\u2026';
          try{const r=await fetch('/api/health');result.textContent=r.ok?'\u2714 Runtime refreshed.':'Refresh failed: '+r.status;}
          catch(e){result.textContent='Refresh failed: '+e;}
        }
        let _portTested=false,_portLoaded=false;
        async function testPort(){
          const el=document.getElementById('port-test-result'); if(!el)return;
          const port=parseInt(document.getElementById('Port')?.value||'0',10);
          if(!port||port<1||port>65535){el.textContent='\u26a0 Enter a valid port (1-65535)';el.className='test-err';return;}
          el.textContent='Testing\u2026';el.className='hint';
          try{
            const r=await fetch('/api/settings/test-port?port='+port);
            const d=await r.json();
            if(d.available){_portTested=true;el.textContent='\u2714 Port '+port+' is available. Save settings to apply (reboot required).';el.className='test-ok';}
            else{_portTested=false;el.textContent='\u26a0 Port '+port+' is in use: '+(d.error||'unavailable');el.className='test-err';}
          }catch(e){_portTested=false;el.textContent='Test failed: '+e;el.className='test-err';}
        }
        async function rebootServer(){
          const result=document.getElementById('reboot-result');
          if(!confirm('Restart the RemotePlay server? Active playback will stop.'))return;
          result.textContent='Saving\u2026';
          try{
            // Always save the current server settings (including port if tested) before restarting
            const sr=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(collectCategory('server'))});
            if(!sr.ok){result.textContent='\u26a0 Failed to save settings. Restart cancelled.';return;}
            const savedPort=parseInt(document.getElementById('Port')?.value||'0',10);
            result.textContent='Restarting\u2026';
            const rr=await fetch('/api/restart',{method:'POST'});
            const rd=await rr.json();
            const newPort=rd.newPort||savedPort||location.port||80;
            result.textContent='Server restarting\u2026 reconnecting on port '+newPort;
            // Wait for the server to come back up on the (possibly new) port, then redirect
            const newOrigin=location.protocol+'//'+location.hostname+(newPort?':'+newPort:'');
            const pollUrl=newOrigin+'/api/health';
            let attempts=0;
            const maxAttempts=30;
            const poll=setInterval(async()=>{
              attempts++;
              try{
                const hr=await fetch(pollUrl,{cache:'no-store'});
                if(hr.ok){clearInterval(poll);result.textContent='Server ready \u2014 redirecting\u2026';location.href=newOrigin+'/';}
              }catch(_){}
              if(attempts>=maxAttempts){clearInterval(poll);result.textContent='\u26a0 Server did not respond after restart. Try navigating to '+newOrigin;}
            },1000);
          }
          catch(e){result.textContent='Restart request failed: '+e;}
        }
        async function rescanLibrary(){
          const result=document.getElementById('admin-result'); result.textContent='Starting rescan\u2026';
          try{
            await Promise.all([fetch('/api/rescan'),fetch('/api/rescan-music')]);
            result.textContent='Library rescan started (video + music).';
          }catch(e){result.textContent='Rescan failed: '+e;}
        }
        function _log(msg,cls){const box=document.getElementById('sync-log');box.style.display='block';const line=document.createElement('div');if(cls)line.className=cls;line.textContent=msg;box.appendChild(line);box.scrollTop=box.scrollHeight;}
        async function ovReindex(type){
          const isVideo=type==='video';
          const rescanUrl=isVideo?'/api/rescan':'/api/rescan-music';
          try{
            await fetch(rescanUrl,{method:'POST'});
            if(isVideo)_ovVideoFloor=0; else _ovMusicFloor=0;
            stopOverviewPoll(); // reset so startOverviewPoll re-arms fresh
            startOverviewPoll();
          }catch(e){alert('Reindex failed: '+e);}
        }
        async function startSyncAll(){
          const btn=document.getElementById('sync-btn'),barWrap=document.getElementById('sync-bar-wrap'),fill=document.getElementById('sync-bar-fill'),log=document.getElementById('sync-log');
          btn.disabled=true;log.innerHTML='';log.style.display='block';barWrap.style.display='block';fill.style.width='5%';
          const pill=document.getElementById('sync-status-pill');if(pill)pill.style.display='inline-block';
          _log('Collecting local offsets\u2026','dim');
          let offsets={};try{const raw=localStorage.getItem('remotePlayLyricOffsets');if(raw)offsets=JSON.parse(raw);}catch(_){}
          try{
            const resp=await fetch('/api/sync/all',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({offsets})});
            if(!resp.ok||!resp.body){_log('Server error: '+resp.status,'warn');btn.disabled=false;return;}
            const reader=resp.body.getReader();const decoder=new TextDecoder();let buf='';let peerCount=0;
            while(true){const{done,value}=await reader.read();if(done)break;buf+=decoder.decode(value,{stream:true});
              const parts=buf.split('\n\n');buf=parts.pop();
              for(const part of parts){const line=part.trim();if(!line.startsWith('data:'))continue;
                let ev;try{ev=JSON.parse(line.slice(5).trim());}catch{continue;}
                if(ev.type==='peer'){peerCount++;fill.style.width=Math.min(10+peerCount*15,85)+'%';_log('\u2192 '+ev.message,'info');}
                else if(ev.type==='step'){_log('  '+ev.message,'dim');}
                else if(ev.type==='step_done'){_log('  \u2714 '+ev.step+': sent '+ev.sent+', skipped '+ev.skipped+(ev.failed>0?' \u26a0 failed '+ev.failed:''),'ok');}
                else if(ev.type==='step_error'){_log('  \u26a0 '+ev.step+': '+ev.message,'warn');}
                else if(ev.type==='done'){fill.style.width='100%';if(ev.peers===0){_log(ev.message||'No peers found.','warn');}else{_log('\u2714 Done \u2014 peers: '+ev.peers+' | sent: '+ev.sent+' | skipped: '+ev.skipped+(ev.failed>0?' | failed: '+ev.failed:''),'ok');}}
              }
            }
          }catch(e){_log('Sync failed: '+e,'warn');}
          finally{btn.disabled=false;setTimeout(()=>{barWrap.style.display='none';log.style.display='none';fill.style.width='0%';if(pill)pill.style.display='none';},5000);}
        }
        function escAttr(s){return String(s).replace(/&/g,'&amp;').replace(/"/g,'&quot;').replace(/</g,'&lt;');}
        async function loadAudioDevices(selectedId){
          const sel=document.getElementById('MusicAudioDeviceId'); if(!sel)return;
          try{
            const r=await fetch('/api/audio-devices'); if(!r.ok)return;
            const devices=await r.json();
            sel.innerHTML='';
            devices.forEach(d=>{
              const opt=document.createElement('option');
              opt.value=d.id; opt.textContent=d.name;
              if(d.id===(selectedId||''))opt.selected=true;
              sel.appendChild(opt);
            });
          }catch(e){console.warn('Could not load audio devices',e);}
        }
        function populateAudioDeviceSelect(id){
          const sel=document.getElementById('MusicAudioDeviceId'); if(!sel)return;
          // Set immediately if options already loaded; will also be set by loadAudioDevices
          for(const o of sel.options){if(o.value===(id||'')){o.selected=true;return;}}
          sel.dataset.pendingValue=id||'';
        }
        async function loadDisplaySelect(selectedIndex){
          const sel=document.getElementById('PreferredDisplayIndex'); if(!sel)return;
          try{
            const r=await fetch('/api/displays');
            const screens=await r.json();
            // Keep the Primary option, then add each screen
            sel.innerHTML='<option value="-1">Primary monitor</option>';
            screens.forEach(s=>{
              const o=document.createElement('option');
              o.value=s.index; o.textContent=s.name; sel.appendChild(o);
            });
          }catch(e){ /* leave default option */ }
          // Select saved value
          for(const o of sel.options){if(Number(o.value)===(selectedIndex??-1)){o.selected=true;break;}}
        }
        function populateDisplaySelect(idx){
          // Try to set immediately; if options not yet loaded, loadDisplaySelect will set it
          const sel=document.getElementById('PreferredDisplayIndex'); if(!sel)return;
          for(const o of sel.options){if(Number(o.value)===idx){o.selected=true;return;}}
          sel.dataset.pendingDisplayIndex=idx;
        }
        populate(INITIAL_CFG);
        ['library','scanning','playback','audio','server','security','updates','appearance','desktop','tools'].forEach(bindCategoryAutoSave);
        loadAudioDevices(INITIAL_CFG.musicAudioDeviceId);
        loadDisplaySelect(INITIAL_CFG.preferredDisplayIndex??-1);
        const catFromUrl=new URLSearchParams(location.search).get('cat');
        if(catFromUrl)showCat(catFromUrl);
        startOverviewPoll();
        /* ---- LOG VIEWER ---- */
        let _logAllEntries=[]; // full set from last fetch
        let _logSelected=new Set(); // selected row indices (into _logAllEntries)
        let _logAnchor=-1; // shift-click anchor
        async function loadLog(){
          const level=document.getElementById('log-filter-level')?.value||'';
          const src=document.getElementById('log-filter-source')?.value||'';
          let url='/api/server-log?lines=2000';
          if(level)url+='&level='+encodeURIComponent(level);
          if(src)url+='&source='+encodeURIComponent(src);
          try{
            const r=await fetch(url);
            if(!r.ok){renderLogError('Failed to load log (HTTP '+r.status+')');return;}
            const entries=await r.json();
            _logAllEntries=entries;
            _logSelected.clear();
            _logAnchor=-1;
            renderLogTable(entries);
            updateLogSelInfo();
          }catch(e){renderLogError(String(e));}
        }
        function applyLogFilters(){ loadLog(); }
        function renderLogError(msg){
          const tb=document.getElementById('log-tbody');
          if(tb)tb.innerHTML='<tr><td colspan="4" style="color:#f87171;padding:.5rem .8rem">'+msg+'</td></tr>';
        }
        function renderLogTable(entries){
          const tb=document.getElementById('log-tbody');
          if(!tb)return;
          if(!entries.length){tb.innerHTML='<tr><td colspan="4" style="color:#484f58;padding:.5rem .8rem">No log entries.</td></tr>';return;}
          const rows=entries.map((e,i)=>{
            const lvClass='lv-'+e.level;
            return '<tr class="'+lvClass+'" data-idx="'+i+'" onclick="logRowClick(event,'+i+')">'
              +'<td class="lv-ts">'+esc(e.timestamp)+'</td>'
              +'<td class="lv-badge lv-badge-col">'+esc(e.level)+'</td>'
              +'<td class="lv-src" title="'+esc(e.source)+'">'+esc(e.source)+'</td>'
              +'<td class="lv-msg">'+esc(e.message)+'</td>'
              +'</tr>';
          });
          tb.innerHTML=rows.join('');
          // scroll to bottom (most recent)
          const viewer=document.getElementById('log-viewer');
          if(viewer)viewer.scrollTop=viewer.scrollHeight;
          document.getElementById('log-footer-info').textContent=entries.length.toLocaleString()+' entries shown';
        }
        function logRowClick(ev,idx){
          if(ev.ctrlKey||ev.metaKey){
            if(_logSelected.has(idx))_logSelected.delete(idx); else _logSelected.add(idx);
            _logAnchor=idx;
          } else if(ev.shiftKey&&_logAnchor>=0){
            const lo=Math.min(_logAnchor,idx), hi=Math.max(_logAnchor,idx);
            for(let i=lo;i<=hi;i++)_logSelected.add(i);
          } else {
            _logSelected.clear();
            _logSelected.add(idx);
            _logAnchor=idx;
          }
          highlightSelected();
          updateLogSelInfo();
        }
        function highlightSelected(){
          const rows=document.querySelectorAll('#log-tbody tr');
          rows.forEach(r=>{
            const i=parseInt(r.dataset.idx,10);
            r.classList.toggle('selected',_logSelected.has(i));
          });
        }
        function updateLogSelInfo(){
          const n=_logSelected.size;
          document.getElementById('log-sel-info').textContent=n?n+' row'+(n>1?'s':'')+' selected':'';
          const btn=document.getElementById('log-copy-btn');
          if(btn)btn.disabled=n===0;
        }
        function copyLogSelection(){
          const sel=[..._logSelected].sort((a,b)=>a-b);
          const lines=sel.map(i=>_logAllEntries[i]?.raw||'');
          navigator.clipboard.writeText(lines.join('\n')).catch(()=>{
            // fallback
            const ta=document.createElement('textarea');
            ta.value=lines.join('\n');
            document.body.appendChild(ta);ta.select();document.execCommand('copy');document.body.removeChild(ta);
          });
        }
        async function clearLog(){
          const r=await fetch('/api/server-log/clear',{method:'POST'});
          if(r.ok){_logAllEntries=[];_logSelected.clear();renderLogTable([]);}
        }
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}
        </script>
        </body>
        </html>
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

    /// <summary>Reads the last <paramref name="maxLines"/> lines from the log file and parses them into structured entries.</summary>
    internal static List<LogEntry> ReadLogEntries(string filePath, int maxLines, string? levelFilter, string? sourceFilter)
    {
        if (!File.Exists(filePath))
            return [];

        string[] rawLines;
        lock (typeof(Logger)) // best-effort; Logger uses its own lock but we just need a snapshot
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            rawLines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        // Parse lines — format: "yyyy-MM-dd HH:mm:ss [LEVEL] [Source] Message"
        var entries = new List<LogEntry>(Math.Min(rawLines.Length, maxLines));
        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = ParseLogLine(line);
            if (levelFilter is not null && !string.Equals(entry.Level, levelFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (sourceFilter is not null && !entry.Source.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            entries.Add(entry);
        }

        // Return only the last maxLines after filtering
        if (entries.Count > maxLines)
            entries.RemoveRange(0, entries.Count - maxLines);

        return entries;
    }

    private static readonly System.Text.RegularExpressions.Regex _logLineRegex =
        new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[(\w+)\] \[([^\]]+)\] (.*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static LogEntry ParseLogLine(string line)
    {
        var m = _logLineRegex.Match(line);
        if (!m.Success)
            return new LogEntry { Timestamp = "", Level = "INFO", Source = "General", Message = line, Raw = line };
        return new LogEntry
        {
            Timestamp = m.Groups[1].Value,
            Level     = m.Groups[2].Value,
            Source    = m.Groups[3].Value,
            Message   = m.Groups[4].Value,
            Raw       = line
        };
    }

    internal sealed record LogEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public string Timestamp { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("level")]
        public string Level     { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public string Source    { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message   { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("raw")]
        public string Raw       { get; init; } = "";
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
