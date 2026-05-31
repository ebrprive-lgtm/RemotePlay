using System.IO;
using System.Net;
using System.Text.Json;

namespace RemotePlay;

// Music playback control handlers: play, queue, stream, lyrics, seek, volume, boost, reverb.

internal sealed partial class WebServer
{
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

    /// <summary>Fetches lyrics for a track â€” tries LRCLib (synced) first, falls back to Genius.
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
            Logger.Detail("Lyrics", $"LRCLib miss â€” falling back to Genius for '{cleanArtist} - {cleanTitle}'");
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
                Logger.Detail("Lyrics", $"LRCLib: {(int)response.StatusCode} â€” treating as miss");
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
                    Logger.Detail("Lyrics", $"LRCLib /get: artist mismatch â€” got '{returnedArtist}', wanted '{wantArtist}'; discarding");
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
                        Logger.Detail("Lyrics", $"LRCLib search [{itemIndex}]: skip â€” returned artistName is empty");
                        continue;
                    }

                    bool artistOk = LrcLibArtistMatches(artistNorm, returnedArtist);
                    Logger.Detail("Lyrics", $"LRCLib search [{itemIndex}]: artist match '{artistNorm}' vs '{returnedArtist}' => {artistOk}");
                    if (!artistOk)
                        continue;
                }
                else
                {
                    // No artist available â€” guard by title similarity to avoid accepting a completely
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
    /// Requires the returned title to be essentially the same as the searched title â€”
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

        // High bidirectional token overlap: both sets must be â‰¥ 90 % covered.
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
        // â”€â”€ Step 1: try the direct slug URL â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
                        Logger.Detail("Lyrics", $"Direct URL hit â€” extracted {directLyrics.Length} chars");
                        return (directLyrics, directUrl);
                    }
                }
                Logger.Detail("Lyrics", "Direct URL returned no lyrics container â€” falling back to search");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Detail("Lyrics", $"Direct URL fetch failed ({ex.Message}) â€” falling back to search");
            }
        }

        // â”€â”€ Step 2: Genius internal JSON search API â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

        // â”€â”€ Step 3: fetch lyrics page â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var lyricsHtml = await _lyricsHttpClient.GetStringAsync(songUrl, ct).ConfigureAwait(false);
        Logger.Detail("Lyrics", $"Lyrics page fetched ({lyricsHtml.Length} chars)");
        var lyrics = ExtractGeniusLyricsText(lyricsHtml);
        Logger.Detail("Lyrics", lyrics is null ? "Lyrics extraction returned null" : $"Extracted {lyrics.Length} chars");
        return lyrics is null ? (null, null) : (lyrics, songUrl);
    }


    /// <summary>
    /// Builds the Genius URL slug from artist and title.
    /// Spaces become hyphens; only alphanumerics and hyphens are kept; runs of hyphens are collapsed.
    /// Example: "Billy Joel" + "Christie Lee" â†’ "Billy-Joel-Christie-Lee"
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

}