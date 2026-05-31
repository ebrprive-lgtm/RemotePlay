using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using RemotePlay.Services;

namespace RemotePlay;

// Radio browsing, playback, and streaming proxy handlers.

internal sealed partial class WebServer
{

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
            Logger.Detail("Playback", $"[PROXY] Pipe closed after {bytesSent} bytes â€” {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            upstream.Dispose();
            try { ctx.Response.OutputStream.Close(); } catch { /* ignored */ }
        }
    }

}
