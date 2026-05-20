using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemotePlay.Services;

/// <summary>Radio station record from the Radio Browser API.</summary>
internal sealed class RadioStation
{
    [JsonPropertyName("stationuuid")]  public string Uuid        { get; set; } = "";
    [JsonPropertyName("name")]         public string Name        { get; set; } = "";
    [JsonPropertyName("url_resolved")] public string StreamUrl   { get; set; } = "";
    [JsonPropertyName("homepage")]     public string Homepage    { get; set; } = "";
    [JsonPropertyName("country")]      public string Country     { get; set; } = "";
    [JsonPropertyName("countrycode")]  public string CountryCode { get; set; } = "";
    [JsonPropertyName("state")]        public string State       { get; set; } = "";
    [JsonPropertyName("language")]     public string Language    { get; set; } = "";
    [JsonPropertyName("tags")]         public string Tags        { get; set; } = "";
    [JsonPropertyName("codec")]        public string Codec       { get; set; } = "";
    [JsonPropertyName("bitrate")]      public int    Bitrate     { get; set; }
    [JsonPropertyName("votes")]        public int    Votes       { get; set; }
    [JsonPropertyName("clickcount")]   public int    ClickCount  { get; set; }
    [JsonPropertyName("hls")]          public int    Hls         { get; set; }
    [JsonPropertyName("favicon")]      public string Favicon     { get; set; } = "";
}

/// <summary>
/// Resolves Radio Browser API servers and performs station searches.
/// Results are cached in memory to avoid hammering the API on every keystroke.
/// Favorites are persisted to a local JSON file keyed by stationuuid.
/// </summary>
internal sealed class RadioBrowserClient : IDisposable
{
    private static readonly string[] FallbackHosts =
    [
        "de1.api.radio-browser.info",
        "nl1.api.radio-browser.info",
        "at1.api.radio-browser.info",
    ];

    private readonly HttpClient _http;
    private readonly string _favoritesFile;
    private readonly object _favGate = new();

    private string _baseUrl = "";
    private List<RadioStation> _favorites = [];
    private bool _favLoaded;

    // Simple in-memory cache: query key → (timestamp, results)
    private readonly Dictionary<string, (DateTime At, List<RadioStation> Results)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public RadioBrowserClient(string favoritesFile)
    {
        _favoritesFile = favoritesFile;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0");
    }

    // ── Server resolution ────────────────────────────────────────────────────

    public async Task ResolveServerAsync(CancellationToken ct = default)
    {
        // Try DNS-based discovery first, then fall back to known hosts.
        try
        {
            var entries = await System.Net.Dns.GetHostAddressesAsync(
                "all.api.radio-browser.info", ct).ConfigureAwait(false);
            if (entries.Length > 0)
            {
                // Pick a host that is reachable.
                foreach (var host in FallbackHosts)
                {
                    try
                    {
                        var probe = await _http.GetAsync(
                            $"https://{host}/json/stats", ct).ConfigureAwait(false);
                        if (probe.IsSuccessStatusCode) { _baseUrl = $"https://{host}"; return; }
                    }
                    catch { /* try next */ }
                }
            }
        }
        catch { /* DNS failed, fall through */ }

        // Last-resort: pick any reachable host from the known list.
        foreach (var host in FallbackHosts)
        {
            try
            {
                var probe = await _http.GetAsync(
                    $"https://{host}/json/stats", ct).ConfigureAwait(false);
                if (probe.IsSuccessStatusCode) { _baseUrl = $"https://{host}"; return; }
            }
            catch { /* try next */ }
        }

        _baseUrl = $"https://{FallbackHosts[0]}"; // use de1 as last resort
    }

    private string Base => string.IsNullOrEmpty(_baseUrl) ? $"https://{FallbackHosts[0]}" : _baseUrl;

    // ── Search / browse ──────────────────────────────────────────────────────

    public async Task<List<RadioStation>> SearchAsync(
        string query, string country = "", string tag = "",
        int limit = 40, int offset = 0, CancellationToken ct = default)
    {
        var key = $"q:{query}|c:{country}|t:{tag}|l:{limit}|o:{offset}";
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Results;

        var ub = new System.Text.StringBuilder(Base);
        ub.Append("/json/stations/search?hidebroken=true&order=votes&reverse=true");
        ub.Append("&limit=").Append(limit);
        if (offset > 0) ub.Append("&offset=").Append(offset);
        if (!string.IsNullOrWhiteSpace(query))   ub.Append("&name=").Append(Uri.EscapeDataString(query));
        if (!string.IsNullOrWhiteSpace(country)) ub.Append("&countrycode=").Append(Uri.EscapeDataString(country));
        if (!string.IsNullOrWhiteSpace(tag))     ub.Append("&tagExact=true&tag=").Append(Uri.EscapeDataString(tag));

        try
        {
            var results = await _http.GetFromJsonAsync<List<RadioStation>>(
                ub.ToString(), ct).ConfigureAwait(false) ?? [];
            _cache[key] = (DateTime.UtcNow, results);
            return results;
        }
        catch { return []; }
    }

    public async Task<List<RadioStation>> TopStationsAsync(int limit = 40, int offset = 0, CancellationToken ct = default)
    {
        var key = $"top:{limit}:{offset}";
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Results;

        try
        {
            var url = offset > 0
                ? $"{Base}/json/stations/search?hidebroken=true&order=votes&reverse=true&limit={limit}&offset={offset}"
                : $"{Base}/json/stations/topvote/{limit}";
            var results = await _http.GetFromJsonAsync<List<RadioStation>>(
                url, ct).ConfigureAwait(false) ?? [];
            _cache[key] = (DateTime.UtcNow, results);
            return results;
        }
        catch { return []; }
    }

    public async Task<List<string>> GetTagsAsync(string countryCode = "", int limit = 60, CancellationToken ct = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                // The /json/tags endpoint ignores countrycode. Instead, sample stations for
                // that country and derive the tag list from their actual tag fields.
                var cc = Uri.EscapeDataString(countryCode.ToUpperInvariant());
                var stations = await _http.GetFromJsonAsync<List<RadioStation>>(
                    $"{Base}/json/stations/search?hidebroken=true&countrycode={cc}&limit=200&offset=0", ct)
                    .ConfigureAwait(false) ?? [];
                return stations
                    .SelectMany(s => s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .Take(limit)
                    .ToList();
            }

            var raw = await _http.GetFromJsonAsync<List<JsonElement>>(
                $"{Base}/json/tags?order=stationcount&reverse=true&hidebroken=true&limit={limit}", ct)
                .ConfigureAwait(false) ?? [];
            return raw.Where(e => e.TryGetProperty("name", out _))
                      .Select(e => e.GetProperty("name").GetString() ?? "")
                      .Where(n => !string.IsNullOrWhiteSpace(n))
                      .ToList();
        }
        catch { return []; }
    }

    public async Task<List<(string Code, string Name)>> GetCountriesAsync(int limit = 80, CancellationToken ct = default)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<List<JsonElement>>(
                $"{Base}/json/countries?order=stationcount&reverse=true&hidebroken=true&limit={limit}", ct)
                .ConfigureAwait(false) ?? [];
            return raw.Where(e => e.TryGetProperty("iso_3166_1", out _) && e.TryGetProperty("name", out _))
                      .Select(e => (e.GetProperty("iso_3166_1").GetString() ?? "",
                                    e.GetProperty("name").GetString() ?? ""))
                      .Where(t => !string.IsNullOrWhiteSpace(t.Item1))
                      .ToList();
        }
        catch { return []; }
    }

    // ── Favorites ────────────────────────────────────────────────────────────

    private void EnsureFavoritesLoaded()
    {
        if (_favLoaded) return;
        lock (_favGate)
        {
            if (_favLoaded) return;
            try
            {
                if (File.Exists(_favoritesFile))
                {
                    var json = File.ReadAllText(_favoritesFile);
                    _favorites = JsonSerializer.Deserialize<List<RadioStation>>(json) ?? [];
                }
            }
            catch { _favorites = []; }
            _favLoaded = true;
        }
    }

    public List<RadioStation> GetFavorites()
    {
        EnsureFavoritesLoaded();
        lock (_favGate) return [.. _favorites];
    }

    public bool IsFavorite(string uuid)
    {
        EnsureFavoritesLoaded();
        lock (_favGate) return _favorites.Any(f => f.Uuid == uuid);
    }

    public void ToggleFavorite(RadioStation station)
    {
        EnsureFavoritesLoaded();
        lock (_favGate)
        {
            var existing = _favorites.FindIndex(f => f.Uuid == station.Uuid);
            if (existing >= 0) _favorites.RemoveAt(existing);
            else _favorites.Add(station);
            SaveFavoritesLocked();
        }
    }

    private void SaveFavoritesLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_favoritesFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_favoritesFile,
                JsonSerializer.Serialize(_favorites, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    public void Dispose() => _http.Dispose();
}
