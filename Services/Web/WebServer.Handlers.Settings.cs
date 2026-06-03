using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemotePlay.Services;

namespace RemotePlay;

// Handlers for /api/settings (GET + POST) and test-path / test-url helpers.
internal sealed partial class WebServer
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNameCaseInsensitive  = true,
        PropertyNamingPolicy         = JsonNamingPolicy.CamelCase,
        WriteIndented                = true,
        DefaultIgnoreCondition       = JsonIgnoreCondition.Never,
        NumberHandling               = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // ── GET /api/settings ───────────────────────────────────────────────────────
    private void HandleApiSettingsGet(HttpListenerContext ctx)
    {
        var c = _config;
        var dto = BuildSettingsDto(c);
        var json = JsonSerializer.Serialize(dto, SettingsJsonOptions);
        ctx.Response.AddHeader("Cache-Control", "no-store");
        TrySendResponse(ctx, 200, "application/json; charset=utf-8", json);
    }

    // ── POST /api/settings ──────────────────────────────────────────────────────
    private void HandleApiSettingsPost(HttpListenerContext ctx)
    {
        string body;
        try
        {
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            body = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            TrySendResponse(ctx, 400, "application/json", $"{{\"ok\":false,\"error\":\"Could not read body: {ex.Message}\"}}");
            return;
        }

        WebSettingsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WebSettingsDto>(body, SettingsJsonOptions);
        }
        catch (Exception ex)
        {
            TrySendResponse(ctx, 400, "application/json", $"{{\"ok\":false,\"error\":\"Invalid JSON: {ex.Message}\"}}");
            return;
        }

        if (dto is null)
        {
            TrySendResponse(ctx, 400, "application/json", "{\"ok\":false,\"error\":\"Empty settings payload.\"}");
            return;
        }

        // Map DTO → updated AppConfig (Port / UseHttps / WPF-only fields are never touched here)
        var updated = _config with
        {
            InstanceName                = string.IsNullOrWhiteSpace(dto.InstanceName) ? _config.InstanceName : dto.InstanceName.Trim(),
            AdditionalMoviesPaths       = dto.AdditionalMoviesPaths ?? _config.AdditionalMoviesPaths,
            AdditionalMusicPaths        = dto.AdditionalMusicPaths  ?? _config.AdditionalMusicPaths,
            NetworkShareCredentials     = dto.NetworkShareCredentials != null
                                            ? dto.NetworkShareCredentials
                                                  .Where(c2 => !string.IsNullOrWhiteSpace(c2.Path))
                                                  .Select(c2 => new NetworkShareCredential { Path = c2.Path.Trim(), Username = c2.Username?.Trim() ?? string.Empty, Password = c2.Password ?? string.Empty })
                                                  .ToArray()
                                            : _config.NetworkShareCredentials,
            LibraryRescanDelayMinutes   = dto.LibraryRescanDelayMinutes   ?? _config.LibraryRescanDelayMinutes,
            EnableThumbnailGeneration   = dto.EnableThumbnailGeneration   ?? _config.EnableThumbnailGeneration,
            LibraryPageSize             = dto.LibraryPageSize             ?? _config.LibraryPageSize,
            IgnoredLibraryFolders       = ParseCommaSeparated(dto.IgnoredLibraryFolders, _config.IgnoredLibraryFolders),
            VideoFileExtensions         = ParseCommaSeparated(dto.VideoFileExtensions,   _config.VideoFileExtensions),
            MusicFileExtensions         = ParseCommaSeparated(dto.MusicFileExtensions,   _config.MusicFileExtensions),
            UpdateSourcePath            = dto.UpdateSourcePath?.Trim()                  ?? _config.UpdateSourcePath,
            AutoUpdateIntervalMinutes   = dto.AutoUpdateIntervalMinutes   ?? _config.AutoUpdateIntervalMinutes,
            MusicAudioDeviceId          = dto.MusicAudioDeviceId?.Trim()               ?? _config.MusicAudioDeviceId,
            PreferredAudioLanguage      = dto.PreferredAudioLanguage?.Trim()            ?? _config.PreferredAudioLanguage,
            PreferredSubtitleLanguage   = dto.PreferredSubtitleLanguage?.Trim()         ?? _config.PreferredSubtitleLanguage,
            SecondarySubtitleLanguage   = dto.SecondarySubtitleLanguage?.Trim()         ?? _config.SecondarySubtitleLanguage,
            PreferForcedSubtitles       = dto.PreferForcedSubtitles     ?? _config.PreferForcedSubtitles,
            PlaybackEndBehavior         = Enum.TryParse<PlaybackEndMode>(dto.PlaybackEndBehavior, out var pem) ? pem : _config.PlaybackEndBehavior,
            PlaybackHistoryLimit        = dto.PlaybackHistoryLimit       ?? _config.PlaybackHistoryLimit,
            MaxRequestsPerIpPerWindow   = dto.MaxRequestsPerIpPerWindow   ?? _config.MaxRequestsPerIpPerWindow,
            RateLimitWindowSeconds      = dto.RateLimitWindowSeconds      ?? _config.RateLimitWindowSeconds,
            ExpertMode                  = dto.ExpertMode                  ?? _config.ExpertMode,
            DebugMode                   = dto.DebugMode                   ?? _config.DebugMode,
            SyncIntervalHours           = dto.SyncIntervalHours           ?? _config.SyncIntervalHours,
            SyncAtStartup               = dto.SyncAtStartup               ?? _config.SyncAtStartup,
            PreferredDisplayIndex       = dto.PreferredDisplayIndex       ?? _config.PreferredDisplayIndex,
            StartWithWindows            = dto.StartWithWindows            ?? _config.StartWithWindows,
            UseTrayIcon                 = dto.UseTrayIcon                 ?? _config.UseTrayIcon,
        };

        try
        {
            _callbacks.SaveSettings(updated);
            _config = updated;  // keep WebServer's own reference in sync
            // Rescan the library immediately so the video domain reflects the new path.
            StartLibraryIndexRefresh(force: true);
        }
        catch (Exception ex)
        {
            Logger.Error("HandleApiSettingsPost: SaveSettings failed", ex);
            TrySendResponse(ctx, 500, "application/json", $"{{\"ok\":false,\"error\":\"{JsonEncodedString(ex.Message)}\"}}");
            return;
        }

        // Return the newly saved config so the UI reflects the persisted values
        var responseDto = BuildSettingsDto(updated);
        var json = JsonSerializer.Serialize(new { ok = true, settings = responseDto }, SettingsJsonOptions);
        TrySendResponse(ctx, 200, "application/json; charset=utf-8", json);
    }

    // ── GET /api/audio-devices ────────────────────────────────────────────────
    private static void HandleApiAudioDevices(HttpListenerContext ctx)
    {
        var devices = MusicPlayer.EnumerateDevices();
        var json = JsonSerializer.Serialize(
            devices.Select(d => new { id = d.DeviceNumber < 0 ? "" : d.Name, name = d.Name }),
            SettingsJsonOptions);
        TrySendResponse(ctx, 200, "application/json; charset=utf-8", json);
    }

    // ── GET /api/displays ─────────────────────────────────────────────────────
    private static void HandleApiDisplays(HttpListenerContext ctx)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var list = screens.Select((s, i) => new
        {
            index = i,
            name  = $"Screen {i + 1}{(s.Primary ? " (primary)" : "")} — {s.Bounds.Width}×{s.Bounds.Height}",
        });
        var json = JsonSerializer.Serialize(list, SettingsJsonOptions);
        TrySendResponse(ctx, 200, "application/json; charset=utf-8", json);
    }

    // ── GET /api/settings/test-path
    private static void HandleApiSettingsTestPath(HttpListenerContext ctx)
    {
        var encoded = ctx.Request.QueryString["path"] ?? string.Empty;
        var path    = Uri.UnescapeDataString(encoded).Trim();

        if (string.IsNullOrWhiteSpace(path))
        {
            TrySendResponse(ctx, 200, "application/json", "{\"ok\":false,\"exists\":false,\"error\":\"No path provided.\"}");
            return;
        }

        try
        {
            var resolved = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

            var exists    = Directory.Exists(resolved) || File.Exists(resolved);
            var json      = JsonSerializer.Serialize(new { ok = true, exists, resolved });
            TrySendResponse(ctx, 200, "application/json; charset=utf-8", json);
        }
        catch (Exception ex)
        {
            TrySendResponse(ctx, 200, "application/json",
                $"{{\"ok\":false,\"exists\":false,\"error\":\"{JsonEncodedString(ex.Message)}\"}}");
        }
    }

    // ── GET /api/settings/test-url?url=<encoded> ───────────────────────────────
    private static async void HandleApiSettingsTestUrl(HttpListenerContext ctx)
    {
        var encoded = ctx.Request.QueryString["url"] ?? string.Empty;
        var url     = Uri.UnescapeDataString(encoded).Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            TrySendResponse(ctx, 200, "application/json", "{\"ok\":false,\"reachable\":false,\"error\":\"No URL provided.\"}");
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var req    = new HttpRequestMessage(HttpMethod.Head, url);
            var resp         = await client.SendAsync(req).ConfigureAwait(false);
            var json         = JsonSerializer.Serialize(new { ok = true, reachable = true, status = (int)resp.StatusCode });
            TrySendResponse(ctx, 200, "application/json; charset=utf-8", json);
        }
        catch (Exception ex)
        {
            var msg  = ex.InnerException?.Message ?? ex.Message;
            TrySendResponse(ctx, 200, "application/json",
                $"{{\"ok\":false,\"reachable\":false,\"error\":\"{JsonEncodedString(msg)}\"}}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private WebSettingsDto BuildSettingsDto(AppConfig c) => new()
    {
        InstanceName                = c.InstanceName,
        AdditionalMoviesPaths       = c.AdditionalMoviesPaths,
        AdditionalMusicPaths        = c.AdditionalMusicPaths,
        NetworkShareCredentials     = c.NetworkShareCredentials
                                        .Select(sc => new WebNetworkShareCredentialDto { Path = sc.Path, Username = sc.Username, Password = sc.Password })
                                        .ToArray(),
        LibraryRescanDelayMinutes   = c.LibraryRescanDelayMinutes,
        EnableThumbnailGeneration   = c.EnableThumbnailGeneration,
        LibraryPageSize             = c.LibraryPageSize,
        IgnoredLibraryFolders       = string.Join(", ", c.IgnoredLibraryFolders),
        VideoFileExtensions         = string.Join(", ", c.VideoFileExtensions),
        MusicFileExtensions         = string.Join(", ", c.MusicFileExtensions),
        UpdateSourcePath            = c.UpdateSourcePath,
        AutoUpdateIntervalMinutes   = c.AutoUpdateIntervalMinutes,
        MusicAudioDeviceId          = c.MusicAudioDeviceId,
        PreferredAudioLanguage      = c.PreferredAudioLanguage,
        PreferredSubtitleLanguage   = c.PreferredSubtitleLanguage,
        SecondarySubtitleLanguage   = c.SecondarySubtitleLanguage,
        PreferForcedSubtitles       = c.PreferForcedSubtitles,
        PlaybackEndBehavior         = c.PlaybackEndBehavior.ToString(),
        PlaybackHistoryLimit        = c.PlaybackHistoryLimit,
        MaxRequestsPerIpPerWindow   = c.MaxRequestsPerIpPerWindow,
        RateLimitWindowSeconds      = c.RateLimitWindowSeconds,
        ExpertMode                  = c.ExpertMode,
        DebugMode                   = c.DebugMode,
        SyncIntervalHours           = c.SyncIntervalHours,
        SyncAtStartup               = c.SyncAtStartup,
        PreferredDisplayIndex       = c.PreferredDisplayIndex,
        StartWithWindows            = c.StartWithWindows,
        UseTrayIcon                 = c.UseTrayIcon,
    };

    private static string[] ParseCommaSeparated(string? value, string[] fallback)
    {
        if (value is null)
            return fallback;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .ToArray();
        return parts.Length > 0 ? parts : fallback;
    }

    private static string JsonEncodedString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    // ── DTO types ────────────────────────────────────────────────────────────────
    private sealed class WebSettingsDto
    {
        public string?  InstanceName                { get; set; }
        public string[]? AdditionalMoviesPaths      { get; set; }
        public string[]? AdditionalMusicPaths       { get; set; }
        public WebNetworkShareCredentialDto[]? NetworkShareCredentials { get; set; }
        public int?     LibraryRescanDelayMinutes   { get; set; }
        public bool?    EnableThumbnailGeneration   { get; set; }
        public int?     LibraryPageSize             { get; set; }
        public string?  IgnoredLibraryFolders       { get; set; }
        public string?  VideoFileExtensions         { get; set; }
        public string?  MusicFileExtensions         { get; set; }
        public string?  UpdateSourcePath            { get; set; }
        public int?     AutoUpdateIntervalMinutes   { get; set; }
        public string?  MusicAudioDeviceId          { get; set; }
        public string?  PreferredAudioLanguage      { get; set; }
        public string?  PreferredSubtitleLanguage   { get; set; }
        public string?  SecondarySubtitleLanguage   { get; set; }
        public bool?    PreferForcedSubtitles       { get; set; }
        public string?  PlaybackEndBehavior         { get; set; }
        public int?     PlaybackHistoryLimit        { get; set; }
        public int?     MaxRequestsPerIpPerWindow   { get; set; }
        public int?     RateLimitWindowSeconds      { get; set; }
        public bool?    ExpertMode                  { get; set; }
        public bool?    DebugMode                   { get; set; }
        public int?     SyncIntervalHours           { get; set; }
        public bool?    SyncAtStartup               { get; set; }
        public int?     PreferredDisplayIndex       { get; set; }
        public bool?    StartWithWindows            { get; set; }
        public bool?    UseTrayIcon                 { get; set; }
    }

    private sealed class WebNetworkShareCredentialDto
    {
        public string Path     { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
