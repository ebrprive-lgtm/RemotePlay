using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using System.Collections.Concurrent;

namespace RemotePlay;

// Podcast discovery and feed parsing handlers.

internal sealed partial class WebServer
{
    private const long MaxPodcastFeedCharacters = 10_000_000;
    private const int MaxPodcastAudioHostsToValidate = 64;
    private const int PodcastAudioHostValidationConcurrency = 8;
    private static readonly TimeSpan PodcastFeedRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PodcastAudioHostValidationTimeout = TimeSpan.FromSeconds(10);
    private static readonly HttpClient _podcastHttpClient = CreatePodcastHttpClient();
    private static readonly PodcastFeedHttpClient _defaultPodcastFeedHttpClient = PodcastFeedHttpClient.CreateDefault();
    private static readonly ConcurrentDictionary<string, PodcastSearchCacheEntry> _podcastSearchCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, PodcastTopPoolCacheEntry> _podcastTopPoolCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan _podcastSearchCacheTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan _podcastTopPoolCacheTtl = TimeSpan.FromDays(1);
    private static readonly SemaphoreSlim _itunesRequestThrottleGate = new(1, 1);
    private static readonly TimeSpan _itunesMinRequestInterval = TimeSpan.FromMilliseconds(450);
    private static DateTimeOffset _lastItunesRequestUtc = DateTimeOffset.MinValue;
    private static readonly HashSet<string> _itunesSupportedPodcastCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "GB", "CA", "AU", "NZ", "IE", "ZA", "IN", "SG", "HK", "MY", "PH", "ID", "TH", "TW", "JP", "KR", "CN", "VN",
        "AE", "SA", "IL", "TR", "RU", "UA", "CZ", "SK", "PL", "HU", "RO", "BG", "HR", "SI", "LV", "LT", "EE", "GR", "CY", "MT",
        "IT", "ES", "PT", "FR", "DE", "AT", "CH", "BE", "NL", "LU", "DK", "SE", "NO", "FI", "IS",
        "MX", "BR", "AR", "CL", "CO", "PE", "VE", "EC", "CR", "GT", "HN", "NI", "PA", "PY", "UY", "DO", "SV",
    };
    private static readonly IReadOnlyDictionary<string, string> _podcastCountryCodeToAlpha2 = BuildPodcastCountryCodeToAlpha2();
    private static readonly IReadOnlyDictionary<string, string> _podcastCountryNameByAlpha2 = BuildPodcastCountryNameByAlpha2();

    private static HttpClient CreatePodcastHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePlay/1.0");
        return client;
    }

    private static bool IsItunesRequestUri(Uri uri)
        => string.Equals(uri.Host, "itunes.apple.com", StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> SendPodcastHttpGetAsync(string uri, HttpCompletionOption completionOption)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
            return await _podcastHttpClient.GetAsync(uri, completionOption).ConfigureAwait(false);

        if (!IsItunesRequestUri(parsedUri))
            return await _podcastHttpClient.GetAsync(parsedUri, completionOption).ConfigureAwait(false);

        await _itunesRequestThrottleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _lastItunesRequestUtc;
            if (elapsed < _itunesMinRequestInterval)
            {
                await Task.Delay(_itunesMinRequestInterval - elapsed).ConfigureAwait(false);
            }

            _lastItunesRequestUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _itunesRequestThrottleGate.Release();
        }

        return await _podcastHttpClient.GetAsync(parsedUri, completionOption).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, string> BuildPodcastCountryCodeToAlpha2()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                var alpha2 = region.TwoLetterISORegionName.ToUpperInvariant();
                if (alpha2.Length != 2 || !IsSupportedPodcastCountryCode(alpha2))
                    continue;

                map.TryAdd(alpha2, alpha2);

                var alpha3 = (region.ThreeLetterISORegionName ?? string.Empty).Trim().ToUpperInvariant();
                if (alpha3.Length == 3)
                    map.TryAdd(alpha3, alpha2);
            }
            catch (ArgumentException)
            {
                // Ignore cultures that cannot be converted to regions.
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> BuildPodcastCountryNameByAlpha2()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                var alpha2 = region.TwoLetterISORegionName.ToUpperInvariant();
                if (alpha2.Length != 2 || !IsSupportedPodcastCountryCode(alpha2))
                    continue;

                map.TryAdd(alpha2, region.EnglishName);
            }
            catch (ArgumentException)
            {
                // Ignore cultures that cannot be converted to regions.
            }
        }

        return map;
    }

    private async Task HandlePodcastSearchAsync(HttpListenerContext ctx)
    {
        var q = (ctx.Request.QueryString["q"] ?? string.Empty).Trim();
        var topic = (ctx.Request.QueryString["topic"] ?? string.Empty).Trim();
        var rawCountry = (ctx.Request.QueryString["country"] ?? string.Empty).Trim();
        var hasCountryFilter = rawCountry.Length == 2 && rawCountry.All(char.IsLetter);
        var requestedCountry = hasCountryFilter ? rawCountry.ToUpperInvariant() : string.Empty;
        var hasSupportedCountryFilter = hasCountryFilter && IsSupportedPodcastCountryCode(requestedCountry);
        var country = hasSupportedCountryFilter ? requestedCountry : string.Empty;
        _ = int.TryParse(ctx.Request.QueryString["limit"], out var limit);
        _ = int.TryParse(ctx.Request.QueryString["offset"], out var offset);
        limit = Math.Clamp(limit <= 0 ? 30 : limit, 1, 50);
        offset = Math.Max(offset, 0);

        var term = string.Join(' ', new[] { q, topic }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var hasExplicitSearch = !string.IsNullOrWhiteSpace(term);
        var effectiveTerm = hasExplicitSearch ? term : "podcast";
        var cacheKey = BuildPodcastSearchCacheKey(hasSupportedCountryFilter ? country : string.Empty, effectiveTerm, hasExplicitSearch, offset, limit);

        if (TryGetCachedPodcastSearchResponse(cacheKey, out var cachedResponseJson))
        {
            TrySendResponse(ctx, 200, "application/json", cachedResponseJson);
            return;
        }

        Logger.Info("Podcasts", $"Search request: q='{q}', topic='{topic}', country='{(hasSupportedCountryFilter ? country : "ALL")}', limit={limit}, offset={offset}, explicitSearch={hasExplicitSearch}");

        if (hasCountryFilter && !hasSupportedCountryFilter)
        {
            Logger.Warning("Podcasts", $"Country '{requestedCountry}' is not supported by iTunes podcast search. Falling back to multi-country discovery.");
        }

        async Task SendFallbackSearchPageAsync(string reason)
        {
            var fallbackPool = await GetFallbackPoolAsync(hasSupportedCountryFilter, country).ConfigureAwait(false);
            var filterTerm = hasExplicitSearch ? effectiveTerm : string.Empty;
            var filtered = fallbackPool
                .Where(s => string.IsNullOrWhiteSpace(filterTerm)
                    || (s.Title?.Contains(filterTerm, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (s.Author?.Contains(filterTerm, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (s.Genre?.Contains(filterTerm, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToArray();

            var page = filtered.Skip(offset).Take(limit).ToArray();
            var hasMore = offset + page.Length < filtered.Length;
            var total = filtered.Length;
            Logger.Warning("Podcasts", $"Using fallback search ({reason}). pool={fallbackPool.Length}, filtered={filtered.Length}, page={page.Length}, hasMore={hasMore}, total={total}");
            var payloadJson = JsonSerializer.Serialize(new { shows = page, hasMore, total });
            SetCachedPodcastSearchResponse(cacheKey, payloadJson);
            TrySendResponse(ctx, 200, "application/json", payloadJson);
        }

        if (!hasExplicitSearch)
        {
            Logger.Info("Podcasts", hasSupportedCountryFilter
                ? $"No explicit query/topic provided for country '{country}'. Using iTunes generic podcast search for broader discovery."
                : "No explicit query/topic provided with country=ALL. Using multi-country discovery search.");
        }

        if (!hasSupportedCountryFilter && !hasExplicitSearch)
        {
            Logger.Info("Podcasts", "Using fast all-country top-pool search path (cached daily) to reduce iTunes load.");
            await SendFallbackSearchPageAsync("fast all-country top-pool").ConfigureAwait(false);
            return;
        }

        try
        {
            var upstreamPage = hasSupportedCountryFilter
                ? (hasExplicitSearch
                    ? await FetchItunesPodcastShowsPageAsync(country, effectiveTerm, offset, limit).ConfigureAwait(false)
                    : await FetchItunesCountryDiscoveryPageAsync(country, offset, limit).ConfigureAwait(false))
                : await FetchItunesAllCountriesShowsPageAsync(effectiveTerm, offset, limit).ConfigureAwait(false);
            var shows = upstreamPage.Shows;
            var hasMore = upstreamPage.HasMore;
            var total = upstreamPage.Total;

            Logger.Info("Podcasts", $"Upstream search response: mappedShows={shows.Length}, hasMore={hasMore}, total={(total.HasValue ? total.Value.ToString() : "null")}, scanned={upstreamPage.ScannedCount}");

            if (shows.Length > 0)
            {
                var payloadJson = JsonSerializer.Serialize(new { shows, hasMore, total });
                SetCachedPodcastSearchResponse(cacheKey, payloadJson);
                TrySendResponse(ctx, 200, "application/json", payloadJson);
                return;
            }

            await SendFallbackSearchPageAsync("upstream returned zero usable shows").ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.Warning("Podcasts", $"Podcast search request failed: {ex.Message}");
            await SendFallbackSearchPageAsync("HttpRequestException").ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            Logger.Warning("Podcasts", "Podcast search request timed out. Falling back to top podcasts.");
            await SendFallbackSearchPageAsync("TaskCanceledException").ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            Logger.Warning("Podcasts", $"Podcast search JSON parse failed: {ex.Message}");
            await SendFallbackSearchPageAsync("JsonException").ConfigureAwait(false);
        }
    }

    private async Task<PodcastShowDto[]> GetFallbackPoolAsync(bool hasSupportedCountryFilter, string country)
    {
        var key = hasSupportedCountryFilter
            ? $"country:{(country ?? string.Empty).Trim().ToUpperInvariant()}"
            : "all";

        if (_podcastTopPoolCache.TryGetValue(key, out var cached)
            && cached.ExpiresUtc > DateTimeOffset.UtcNow)
        {
            return cached.Shows;
        }

        var shows = hasSupportedCountryFilter
            ? await FetchTopPodcastsAsync(country, 200).ConfigureAwait(false)
            : await FetchGlobalTopPodcastsAsync(200).ConfigureAwait(false);

        _podcastTopPoolCache[key] = new PodcastTopPoolCacheEntry(shows, DateTimeOffset.UtcNow.Add(_podcastTopPoolCacheTtl));
        return shows;
    }

    private static string BuildPodcastSearchCacheKey(string country, string term, bool hasExplicitSearch, int offset, int limit)
    {
        var normalizedCountry = (country ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedTerm = (term ?? string.Empty).Trim().ToLowerInvariant();
        return $"country={normalizedCountry}|term={normalizedTerm}|explicit={(hasExplicitSearch ? 1 : 0)}|offset={offset}|limit={limit}";
    }

    private static bool TryGetCachedPodcastSearchResponse(string cacheKey, out string responseJson)
    {
        responseJson = string.Empty;
        if (!_podcastSearchCache.TryGetValue(cacheKey, out var entry))
            return false;

        if (entry.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            _podcastSearchCache.TryRemove(cacheKey, out _);
            return false;
        }

        responseJson = entry.ResponseJson;
        return true;
    }

    private static void SetCachedPodcastSearchResponse(string cacheKey, string responseJson)
    {
        _podcastSearchCache[cacheKey] = new PodcastSearchCacheEntry(responseJson, DateTimeOffset.UtcNow.Add(_podcastSearchCacheTtl));

        if (_podcastSearchCache.Count <= 512)
            return;

        foreach (var kv in _podcastSearchCache)
        {
            if (kv.Value.ExpiresUtc <= DateTimeOffset.UtcNow)
            {
                _podcastSearchCache.TryRemove(kv.Key, out _);
            }
        }
    }

    private async Task<(PodcastShowDto[] Shows, bool HasMore, int? Total, int ScannedCount)> FetchItunesPodcastShowsPageAsync(string country, string term, int offset, int limit)
    {
        const int upstreamPageSize = 200;
        const int upstreamMaxScan = 2000;

        var uniqueShows = new List<PodcastShowDto>(offset + limit + 8);
        var seenFeedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upstreamOffset = 0;
        var reachedEnd = false;
        var scannedCount = 0;

        while (uniqueShows.Count < offset + limit + 1 && upstreamOffset < upstreamMaxScan)
        {
            var uri = $"https://itunes.apple.com/search?media=podcast&entity=podcast&country={Uri.EscapeDataString(country)}&limit={upstreamPageSize}&offset={upstreamOffset}&term={Uri.EscapeDataString(term)}";
            using var response = await SendPodcastHttpGetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"iTunes search failed with {(int)response.StatusCode} {response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<ItunesPodcastSearchResponse>(stream, CaseInsensitiveOptions).ConfigureAwait(false);
            var results = payload?.Results ?? [];
            scannedCount += results.Count;

            if (results.Count == 0)
            {
                reachedEnd = true;
                break;
            }

            foreach (var r in results)
            {
                if (!IsValidPublicPodcastFeedUrl(r.FeedUrl))
                    continue;

                var feedUrl = (r.FeedUrl ?? string.Empty).Trim();
                if (!seenFeedUrls.Add(feedUrl))
                    continue;

                var show = new PodcastShowDto
                {
                    Title = (r.CollectionName ?? string.Empty).Trim(),
                    Author = (r.ArtistName ?? string.Empty).Trim(),
                    FeedUrl = feedUrl,
                    ArtworkUrl = (r.ArtworkUrl600 ?? r.ArtworkUrl100 ?? string.Empty).Trim(),
                    Genre = (r.PrimaryGenreName ?? string.Empty).Trim(),
                    Country = NormalizePodcastShowCountryCode(r.Country),
                };

                if (string.IsNullOrWhiteSpace(show.Title))
                    continue;

                uniqueShows.Add(show);
            }

            if (results.Count < upstreamPageSize)
            {
                reachedEnd = true;
                break;
            }

            upstreamOffset += results.Count;
        }

        var page = uniqueShows.Skip(offset).Take(limit).ToArray();
        var hasMore = uniqueShows.Count > offset + page.Length || !reachedEnd;
        int? total = reachedEnd ? uniqueShows.Count : null;
        return (page, hasMore, total, scannedCount);
    }

    private async Task<(PodcastShowDto[] Shows, bool HasMore, int? Total, int ScannedCount)> FetchItunesCountryDiscoveryPageAsync(string country, int offset, int limit)
    {
        var seedTerms = new[]
        {
            "podcast", "news", "comedy", "business", "technology", "history", "science", "sports", "health", "society"
        };

        const int upstreamPageSize = 200;
        const int perSeedMaxScan = 400;

        var uniqueShows = new List<PodcastShowDto>(offset + limit + 8);
        var seenFeedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedCount = 0;
        var fullyExhausted = true;

        foreach (var seed in seedTerms)
        {
            var seedOffset = 0;
            while (seedOffset < perSeedMaxScan)
            {
                var uri = $"https://itunes.apple.com/search?media=podcast&entity=podcast&country={Uri.EscapeDataString(country)}&limit={upstreamPageSize}&offset={seedOffset}&term={Uri.EscapeDataString(seed)}";
                using var response = await SendPodcastHttpGetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"iTunes country discovery failed with {(int)response.StatusCode} {response.StatusCode}");

                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var payload = await JsonSerializer.DeserializeAsync<ItunesPodcastSearchResponse>(stream, CaseInsensitiveOptions).ConfigureAwait(false);
                var results = payload?.Results ?? [];
                scannedCount += results.Count;

                if (results.Count == 0)
                    break;

                foreach (var r in results)
                {
                    if (!IsValidPublicPodcastFeedUrl(r.FeedUrl))
                        continue;

                    var feedUrl = (r.FeedUrl ?? string.Empty).Trim();
                    if (!seenFeedUrls.Add(feedUrl))
                        continue;

                    var show = new PodcastShowDto
                    {
                        Title = (r.CollectionName ?? string.Empty).Trim(),
                        Author = (r.ArtistName ?? string.Empty).Trim(),
                        FeedUrl = feedUrl,
                        ArtworkUrl = (r.ArtworkUrl600 ?? r.ArtworkUrl100 ?? string.Empty).Trim(),
                        Genre = (r.PrimaryGenreName ?? string.Empty).Trim(),
                        Country = NormalizePodcastShowCountryCode(r.Country),
                    };

                    if (string.IsNullOrWhiteSpace(show.Title))
                        continue;

                    uniqueShows.Add(show);
                }

                if (results.Count < upstreamPageSize)
                    break;

                seedOffset += results.Count;

                if (uniqueShows.Count >= offset + limit + 1)
                {
                    fullyExhausted = false;
                    break;
                }
            }

            if (!fullyExhausted)
                break;
        }

        var page = uniqueShows.Skip(offset).Take(limit).ToArray();
        var hasMore = uniqueShows.Count > offset + page.Length || !fullyExhausted;
        int? total = fullyExhausted ? uniqueShows.Count : null;
        return (page, hasMore, total, scannedCount);
    }

    private async Task<(PodcastShowDto[] Shows, bool HasMore, int? Total, int ScannedCount)> FetchItunesAllCountriesShowsPageAsync(string term, int offset, int limit)
    {
        var countries = new[] { "US", "GB", "CA", "AU", "DE", "FR", "ES", "IT", "NL", "BE", "SE", "NO", "DK", "IE", "BR", "MX", "JP", "KR", "IN" };
        var targetCount = offset + limit + 1;
        var uniqueShows = new List<PodcastShowDto>(targetCount + 8);
        var seenFeedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedCount = 0;
        var hasPotentialMore = false;
        var countryOffsets = countries.ToDictionary(c => c, _ => 0, StringComparer.OrdinalIgnoreCase);
        var countryHasMore = countries.ToDictionary(c => c, _ => true, StringComparer.OrdinalIgnoreCase);
        var countryChunkSize = Math.Clamp(limit, 25, 50);

        while (uniqueShows.Count < targetCount)
        {
            var hadFetchableCountry = false;
            var beforeWaveCount = uniqueShows.Count;

            foreach (var c in countries)
            {
                if (!countryHasMore[c])
                    continue;

                hadFetchableCountry = true;
                var pageOffset = countryOffsets[c];
                var countryPage = await FetchItunesPodcastShowsPageAsync(c, term, pageOffset, countryChunkSize).ConfigureAwait(false);
                scannedCount += countryPage.ScannedCount;
                hasPotentialMore = hasPotentialMore || countryPage.HasMore;
                countryOffsets[c] = pageOffset + countryChunkSize;
                countryHasMore[c] = countryPage.HasMore;

                foreach (var show in countryPage.Shows)
                {
                    if (!seenFeedUrls.Add(show.FeedUrl))
                        continue;

                    uniqueShows.Add(show);
                }

                if (uniqueShows.Count >= targetCount)
                    break;
            }

            if (!hadFetchableCountry || uniqueShows.Count == beforeWaveCount)
                break;
        }

        var page = uniqueShows.Skip(offset).Take(limit).ToArray();
        var hasMore = uniqueShows.Count > offset + page.Length || hasPotentialMore;
        int? total = hasMore ? null : uniqueShows.Count;
        return (page, hasMore, total, scannedCount);
    }

    private async Task<PodcastShowDto[]> FetchGlobalTopPodcastsAsync(int limit)
    {
        var countries = new[] { "US", "GB", "CA", "AU", "DE", "FR", "ES", "IT", "NL", "BE" };
        var unique = new List<PodcastShowDto>(limit + 8);
        var seenFeedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in countries)
        {
            var top = await FetchTopPodcastsAsync(c, Math.Clamp(limit, 1, 200)).ConfigureAwait(false);
            foreach (var show in top)
            {
                if (!seenFeedUrls.Add(show.FeedUrl))
                    continue;

                unique.Add(show);
                if (unique.Count >= limit)
                    return unique.ToArray();
            }
        }

        return unique.ToArray();
    }

    private async Task HandlePodcastPlayAsync(HttpListenerContext ctx)
    {
        var rawUrl = (ctx.Request.QueryString["url"] ?? string.Empty).Trim();
        var title = (ctx.Request.QueryString["title"] ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"missing url\"}");
            return;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || !PodcastFeedHttpClient.IsValidHttpUri(uri))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid url\"}");
            return;
        }

        try
        {
            await _podcastFeedHttpClient.ValidatePublicUriAsync(uri).ConfigureAwait(false);
        }
        catch (PodcastDestinationRejectedException)
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"podcast URL destination is not allowed\"}");
            return;
        }
        catch (PodcastHostResolutionException)
        {
            Logger.Warning("Podcasts", "Podcast playback host could not be resolved or validated.");
            TrySendResponse(ctx, 502, "application/json", "{\"error\":\"podcast URL destination could not be validated\"}");
            return;
        }

        StopAllServerPlayers();
        _callbacks.RadioPlay(uri.ToString(), string.IsNullOrWhiteSpace(title) ? uri.Host : title);

        var displayTitle = string.IsNullOrWhiteSpace(title) ? uri.Host : title;
        Logger.Info("Playback", $"Playing Podcast: '{displayTitle}' on Server");
        TrySendResponse(ctx, 200, "application/json", "{\"ok\":true}");
        ScheduleSsePush(delayMs: 300);
    }

    private async Task<PodcastShowDto[]> FetchTopPodcastsAsync(string country, int limit)
    {
        var safeCountry = NormalizePodcastCountryCode(country);
        var safeLimit = Math.Clamp(limit <= 0 ? 30 : limit, 1, 200);
        var endpoint = $"https://rss.applemarketingtools.com/api/v2/{safeCountry.ToLowerInvariant()}/podcasts/top/{safeLimit}/podcasts.json";

        try
        {
            using var response = await _podcastHttpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return [];

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<AppleTopPodcastsResponse>(stream, CaseInsensitiveOptions).ConfigureAwait(false);
            var results = payload?.Feed?.Results ?? [];

            return results
                .Select(r => new PodcastShowDto
                {
                    Title = (r.Name ?? string.Empty).Trim(),
                    Author = (r.ArtistName ?? string.Empty).Trim(),
                    FeedUrl = (r.Url ?? string.Empty).Trim(),
                    ArtworkUrl = (r.ArtworkUrl100 ?? string.Empty).Trim(),
                    Genre = (r.Genres?.FirstOrDefault()?.Name ?? string.Empty).Trim(),
                    Country = safeCountry,
                })
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .Where(s => IsValidPublicPodcastFeedUrl(s.FeedUrl))
                .GroupBy(s => s.FeedUrl, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Logger.Warning("Podcasts", $"Top podcasts fallback failed for country '{safeCountry}': {ex.Message}");
            return [];
        }
    }

    private void HandlePodcastCountries(HttpListenerContext ctx)
    {
        var countries = _podcastCountryNameByAlpha2
            .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new { code = kv.Key, name = kv.Value })
            .ToArray();

        TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(countries));
    }

    private async Task HandlePodcastFeedAsync(HttpListenerContext ctx)
    {
        var url = (ctx.Request.QueryString["url"] ?? string.Empty).Trim();
        _ = int.TryParse(ctx.Request.QueryString["limit"], out var limit);
        _ = int.TryParse(ctx.Request.QueryString["offset"], out var offset);
        limit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 250);
        offset = Math.Max(offset, 0);

        if (string.IsNullOrWhiteSpace(url))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"missing url\"}");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestedUri)
            || !PodcastFeedHttpClient.IsValidHttpUri(requestedUri))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid url\"}");
            return;
        }

        var resolvedUrl = await ResolvePodcastFeedUrlIfNeededAsync(url).ConfigureAwait(false);

        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var feedUri)
            || !PodcastFeedHttpClient.IsValidHttpUri(feedUri))
        {
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"invalid url\"}");
            return;
        }

        using var requestTimeout = new CancellationTokenSource(PodcastFeedRequestTimeout);
        try
        {
            await _podcastFeedHttpClient
                .ValidatePublicUriAsync(feedUri, requestTimeout.Token)
                .ConfigureAwait(false);
            var parsedEpisodes = await _podcastFeedCache.GetOrRefreshAsync(
                feedUri,
                (validators, cancellationToken) => FetchPodcastFeedForCacheAsync(
                    feedUri,
                    validators,
                    cancellationToken),
                requestTimeout.Token).ConfigureAwait(false);
            var allEpisodes = await ValidatePodcastEpisodeAudioUrlsAsync(
                parsedEpisodes,
                requestTimeout.Token).ConfigureAwait(false);
            var episodes = allEpisodes.Skip(offset).Take(limit).ToArray();
            var hasMore = allEpisodes.Length > offset + episodes.Length;
            var total = allEpisodes.Length;
            TrySendResponse(ctx, 200, "application/json", JsonSerializer.Serialize(new { episodes, hasMore, total }));
        }
        catch (PodcastDestinationRejectedException)
        {
            Logger.Warning("Podcasts", "Podcast feed request was rejected by the URL destination policy.");
            TrySendResponse(ctx, 400, "application/json", "{\"error\":\"podcast URL destination is not allowed\"}");
        }
        catch (PodcastHostResolutionException)
        {
            Logger.Warning("Podcasts", "Podcast feed host could not be resolved or validated.");
            TrySendResponse(ctx, 502, "application/json", "{\"error\":\"podcast feed address unavailable\"}");
        }
        catch (PodcastFeedTooLargeException)
        {
            Logger.Warning("Podcasts", "Podcast feed exceeded the response size limit.");
            TrySendResponse(ctx, 502, "application/json", "{\"error\":\"podcast feed exceeds size limit\"}");
        }
        catch (PodcastFeedTooManyAudioHostsException)
        {
            Logger.Warning("Podcasts", "Podcast feed exceeded the audio host validation limit.");
            TrySendResponse(ctx, 502, "application/json", "{\"error\":\"podcast feed contains too many audio hosts\"}");
        }
        catch (PodcastFeedContentRejectedException)
        {
            Logger.Warning("Podcasts", "Podcast feed contained no safely reachable episode audio.");
            TrySendResponse(ctx, 502, "application/json", "{\"error\":\"podcast feed contains no safe episode audio\"}");
        }
        catch (OperationCanceledException)
        {
            TrySendResponse(ctx, 504, "application/json", "{\"error\":\"podcast feed timeout\"}");
        }
        catch (HttpRequestException ex)
        {
            Logger.Warning("Podcasts", $"Podcast feed request failed: {ex.Message}");
            TrySendResponse(ctx, 502, "application/json", "{\"error\":\"podcast feed unavailable\"}");
        }
        catch (System.Xml.XmlException ex)
        {
            Logger.Warning("Podcasts", $"Podcast feed XML parse failed: {ex.Message}");
            TrySendResponse(ctx, 502, "application/json", "{\"error\":\"podcast feed parse failure\"}");
        }
    }

    private async Task<PodcastFeedCacheRefreshResult> FetchPodcastFeedForCacheAsync(
        Uri feedUri,
        PodcastFeedValidators? validators,
        CancellationToken cancellationToken)
    {
        using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshTimeout.CancelAfter(PodcastFeedRequestTimeout);
        var refreshCancellationToken = refreshTimeout.Token;

        using var response = await _podcastFeedHttpClient
            .GetAsync(
                feedUri,
                HttpCompletionOption.ResponseHeadersRead,
                refreshCancellationToken,
                validators)
            .ConfigureAwait(false);
        var finalUri = PodcastFeedCache.NormalizeUri(
            response.RequestMessage?.RequestUri ?? feedUri);
        var noStore = response.Headers.CacheControl?.NoStore == true
                      || response.Headers.Vary.Any(value => string.Equals(value.Trim(), "*", StringComparison.Ordinal));
        var freshnessLifetime = GetPodcastFeedFreshnessLifetime(response);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (validators is null
                || !validators.HasValidators
                || !finalUri.Equals(PodcastFeedCache.NormalizeUri(validators.Uri)))
            {
                throw new HttpRequestException("Podcast feed returned 304 without matching cached validators.");
            }

            var refreshedValidators = new PodcastFeedValidators(
                finalUri,
                response.Headers.ETag ?? validators.ETag,
                response.Content.Headers.LastModified ?? validators.LastModified);
            return new PodcastFeedCacheRefreshResult(
                null,
                refreshedValidators,
                NotModified: true,
                noStore,
                freshnessLifetime);
        }

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Podcast feed returned HTTP {(int)response.StatusCode}.");

        using var stream = await PodcastFeedHttpClient
            .ReadBoundedContentAsync(
                response.Content,
                PodcastFeedHttpClient.MaxResponseBytes,
                refreshCancellationToken)
            .ConfigureAwait(false);
        var settings = new System.Xml.XmlReaderSettings
        {
            Async = true,
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPodcastFeedCharacters,
        };
        using var reader = System.Xml.XmlReader.Create(stream, settings);
        var xml = await XDocument.LoadAsync(reader, LoadOptions.None, refreshCancellationToken).ConfigureAwait(false);
        var episodes = ParsePodcastEpisodes(xml);
        var entityTag = response.Headers.ETag;
        var lastModified = response.Content.Headers.LastModified;
        var newValidators = entityTag is null && !lastModified.HasValue
            ? null
            : new PodcastFeedValidators(finalUri, entityTag, lastModified);

        return new PodcastFeedCacheRefreshResult(
            episodes,
            newValidators,
            NotModified: false,
            noStore,
            freshnessLifetime);
    }

    private TimeSpan? GetPodcastFeedFreshnessLifetime(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        if (cacheControl?.NoCache == true)
            return TimeSpan.Zero;

        var now = _podcastFeedCacheTimeProvider.GetUtcNow();
        var age = response.Headers.Age ?? TimeSpan.Zero;
        if (response.Headers.Date is { } responseDate)
        {
            var apparentAge = now - responseDate;
            if (apparentAge > age)
                age = apparentAge;
        }

        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        if (cacheControl?.MaxAge is { } maxAge)
            return age >= maxAge ? TimeSpan.Zero : maxAge - age;

        if (response.Content.Headers.Expires is { } expires)
        {
            var date = response.Headers.Date ?? now;
            var lifetime = expires - date;
            return age >= lifetime ? TimeSpan.Zero : lifetime - age;
        }

        return null;
    }

    private async Task<PodcastEpisodeDto[]> ValidatePodcastEpisodeAudioUrlsAsync(
        PodcastEpisodeDto[] episodes,
        CancellationToken cancellationToken)
    {
        var parsedEpisodes = episodes
            .Select(episode => Uri.TryCreate(episode.AudioUrl, UriKind.Absolute, out var uri)
                               && PodcastFeedHttpClient.IsSyntacticallyPublicUrl(episode.AudioUrl)
                ? (Episode: episode, Uri: uri)
                : ((PodcastEpisodeDto Episode, Uri Uri)?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        var hostUris = parsedEpisodes
            .GroupBy(item => item.Uri.DnsSafeHost, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Uri)
            .ToArray();

        if (hostUris.Length > MaxPodcastAudioHostsToValidate)
            throw new PodcastFeedTooManyAudioHostsException();

        var safeHosts = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PodcastAudioHostValidationTimeout);
        using var throttle = new SemaphoreSlim(PodcastAudioHostValidationConcurrency);
        await Task.WhenAll(hostUris.Select(async uri =>
        {
            var host = uri.DnsSafeHost;
            try
            {
                await throttle.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    await _podcastFeedHttpClient.ValidatePublicUriAsync(uri, timeout.Token).ConfigureAwait(false);
                    safeHosts[host] = true;
                }
                finally
                {
                    throttle.Release();
                }
            }
            catch (PodcastDestinationRejectedException)
            {
                safeHosts[host] = false;
            }
            catch (PodcastHostResolutionException)
            {
                safeHosts[host] = false;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                safeHosts[host] = false;
            }
        })).ConfigureAwait(false);

        var safeEpisodes = parsedEpisodes
            .Where(item => safeHosts.TryGetValue(item.Uri.DnsSafeHost, out var isSafe) && isSafe)
            .Select(item => item.Episode)
            .ToArray();
        var omittedCount = episodes.Length - safeEpisodes.Length;
        if (omittedCount > 0)
            Logger.Warning("Podcasts", $"Omitted {omittedCount} episodes with unsafe or unresolvable audio destinations.");

        if (episodes.Length > 0 && safeEpisodes.Length == 0)
            throw new PodcastFeedContentRejectedException();

        return safeEpisodes;
    }

    private async Task<string> ResolvePodcastFeedUrlIfNeededAsync(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return rawUrl;

        var host = uri.Host.Trim().ToLowerInvariant();
        var isApplePodcastUrl = host.Contains("podcasts.apple.com", StringComparison.Ordinal)
                             || host.Contains("itunes.apple.com", StringComparison.Ordinal);
        if (!isApplePodcastUrl)
            return rawUrl;

        var applePodcastId = TryExtractApplePodcastId(uri);
        if (string.IsNullOrWhiteSpace(applePodcastId))
            return rawUrl;

        var lookupUrl = $"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(applePodcastId)}&entity=podcast";

        try
        {
            using var response = await SendPodcastHttpGetAsync(lookupUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return rawUrl;

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<ItunesPodcastSearchResponse>(stream, CaseInsensitiveOptions).ConfigureAwait(false);
            var resolvedFeedUrl = payload?.Results
                ?.Select(r => (r.FeedUrl ?? string.Empty).Trim())
                .FirstOrDefault(IsValidPublicPodcastFeedUrl);

            return string.IsNullOrWhiteSpace(resolvedFeedUrl) ? rawUrl : resolvedFeedUrl;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Logger.Warning("Podcasts", $"Podcast feed URL resolve failed for Apple id '{applePodcastId}': {ex.Message}");
            return rawUrl;
        }
    }

    private static string TryExtractApplePodcastId(Uri uri)
    {
        if (uri is null)
            return string.Empty;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            if (!segment.StartsWith("id", StringComparison.OrdinalIgnoreCase) || segment.Length <= 2)
                continue;

            var digits = new string(segment[2..].TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0)
                return digits;
        }

        return string.Empty;
    }

    private static PodcastEpisodeDto[] ParsePodcastEpisodes(XDocument xml)
    {
        var itemNodes = xml
            .Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(e.Name.LocalName, "entry", StringComparison.OrdinalIgnoreCase));

        return itemNodes
            .Select(item =>
            {
                var audioUrl = GetPodcastAudioUrl(item);
                if (string.IsNullOrWhiteSpace(audioUrl))
                    return null;

                Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri);
                var title = GetPodcastElementValue(item, "title");
                var description = GetPodcastElementValue(item, "description")
                                  ?? GetPodcastElementValue(item, "summary")
                                  ?? GetPodcastElementValue(item, "encoded");
                var pubRaw = GetPodcastElementValue(item, "pubDate")
                             ?? GetPodcastElementValue(item, "published")
                             ?? GetPodcastElementValue(item, "updated");
                var durationRaw = GetPodcastElementValue(item, "duration");

                DateTimeOffset? publishedUtc = null;
                if (!string.IsNullOrWhiteSpace(pubRaw)
                    && DateTimeOffset.TryParse(pubRaw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                        out var published))
                {
                    publishedUtc = published.ToUniversalTime();
                }

                return new PodcastEpisodeDto
                {
                    Title = string.IsNullOrWhiteSpace(title)
                        ? Path.GetFileName(audioUri?.LocalPath ?? audioUrl)
                        : title.Trim(),
                    AudioUrl = audioUrl,
                    Description = (description ?? string.Empty).Trim(),
                    PublishedUtc = publishedUtc,
                    DurationSeconds = ParsePodcastDurationSeconds(durationRaw),
                };
            })
            .Where(e => e is not null)
            .Cast<PodcastEpisodeDto>()
            .OrderByDescending(e => e.PublishedUtc ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static string? GetPodcastAudioUrl(XElement item)
    {
        var enclosure = item
            .Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "enclosure", StringComparison.OrdinalIgnoreCase));
        var enclosureUrl = enclosure?.Attribute("url")?.Value;
        if (!string.IsNullOrWhiteSpace(enclosureUrl))
            return enclosureUrl.Trim();

        var atomLink = item
            .Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "link", StringComparison.OrdinalIgnoreCase)
                              && string.Equals((e.Attribute("rel")?.Value ?? string.Empty).Trim(), "enclosure", StringComparison.OrdinalIgnoreCase));
        var href = atomLink?.Attribute("href")?.Value;
        return string.IsNullOrWhiteSpace(href) ? null : href.Trim();
    }

    private static string? GetPodcastElementValue(XElement element, string localName)
    {
        var match = element
            .Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        return match?.Value;
    }

    private static int? ParsePodcastDurationSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asSeconds))
            return asSeconds >= 0 ? asSeconds : null;

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span))
            return span.TotalSeconds >= 0 ? (int)Math.Round(span.TotalSeconds) : null;

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3)
            return null;

        var total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p < 0)
                return null;
            total = (total * 60) + p;
        }

        return total;
    }

    private static string NormalizePodcastCountryCode(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length != 2 || !value.All(char.IsLetter))
            return "US";

        return value.ToUpperInvariant();
    }

    private static string NormalizePodcastShowCountryCode(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length == 0)
            return string.Empty;

        if (value.Length == 2 && value.All(char.IsLetter))
            return value;

        if (value.Length == 3 && value.All(char.IsLetter) && _podcastCountryCodeToAlpha2.TryGetValue(value, out var alpha2))
            return alpha2;

        return value;
    }

    private static bool IsSupportedPodcastCountryCode(string? raw)
    {
        var country = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return _itunesSupportedPodcastCountries.Contains(country);
    }

    private static bool IsValidPublicPodcastFeedUrl(string? url)
        => PodcastFeedHttpClient.IsSyntacticallyPublicUrl(url);

    private sealed class ItunesPodcastSearchResponse
    {
        [JsonPropertyName("resultCount")]
        public int ResultCount { get; set; }

        [JsonPropertyName("results")]
        public List<ItunesPodcastResult> Results { get; set; } = [];
    }

    private sealed class ItunesPodcastResult
    {
        [JsonPropertyName("collectionName")]
        public string CollectionName { get; set; } = string.Empty;

        [JsonPropertyName("artistName")]
        public string ArtistName { get; set; } = string.Empty;

        [JsonPropertyName("feedUrl")]
        public string FeedUrl { get; set; } = string.Empty;

        [JsonPropertyName("artworkUrl600")]
        public string ArtworkUrl600 { get; set; } = string.Empty;

        [JsonPropertyName("artworkUrl100")]
        public string ArtworkUrl100 { get; set; } = string.Empty;

        [JsonPropertyName("primaryGenreName")]
        public string PrimaryGenreName { get; set; } = string.Empty;

        [JsonPropertyName("country")]
        public string Country { get; set; } = string.Empty;
    }

    private sealed class AppleTopPodcastsResponse
    {
        [JsonPropertyName("feed")]
        public AppleTopPodcastsFeed? Feed { get; set; }
    }

    private sealed class AppleTopPodcastsFeed
    {
        [JsonPropertyName("results")]
        public List<AppleTopPodcastsItem> Results { get; set; } = [];
    }

    private sealed class AppleTopPodcastsItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("artistName")]
        public string ArtistName { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("artworkUrl100")]
        public string ArtworkUrl100 { get; set; } = string.Empty;

        [JsonPropertyName("genres")]
        public List<AppleTopPodcastsGenre>? Genres { get; set; }
    }

    private sealed class AppleTopPodcastsGenre
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class PodcastShowDto
    {
        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; init; } = string.Empty;

        [JsonPropertyName("feedUrl")]
        public string FeedUrl { get; init; } = string.Empty;

        [JsonPropertyName("artworkUrl")]
        public string ArtworkUrl { get; init; } = string.Empty;

        [JsonPropertyName("genre")]
        public string Genre { get; init; } = string.Empty;

        [JsonPropertyName("country")]
        public string Country { get; init; } = string.Empty;
    }

    private sealed record PodcastSearchCacheEntry(string ResponseJson, DateTimeOffset ExpiresUtc);
    private sealed record PodcastTopPoolCacheEntry(PodcastShowDto[] Shows, DateTimeOffset ExpiresUtc);
}
