using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using RemotePlay.Models;
using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

/// <summary>
/// Integration tests that start a real <see cref="WebServer"/> on a random local port
/// and exercise the most critical API routes over HTTP.
/// </summary>
public sealed class WebServerApiTests : IDisposable
{
    private readonly WebServer _server;
    private readonly HttpClient _client;
    private readonly HttpClient _podcastUpstreamClient;
    private readonly HttpClient _podcastCatalogHttpClient;
    private readonly TestPodcastHttpMessageHandler _podcastHttpHandler;
    private readonly TestPodcastCatalogHttpMessageHandler _podcastCatalogHttpHandler;
    private readonly TestPodcastHostResolver _podcastHostResolver;
    private readonly TestPodcastCacheTimeProvider _podcastCacheTimeProvider;
    private readonly string _podcastTopPoolCacheDirectory;
    private readonly string _podcastTopPoolCacheFile;
    private readonly List<(string Url, string Title)> _radioPlayRequests = [];
    private readonly List<(string Path, double Position)> _videoPositionPlayRequests = [];
    private PlaybackStatus _playbackStatus = new();

    public WebServerApiTests()
    {
        var port = FindFreePort();
        _podcastTopPoolCacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "RemotePlayPodcastCacheTests",
            Guid.NewGuid().ToString("N"));
        _podcastTopPoolCacheFile = Path.Combine(_podcastTopPoolCacheDirectory, "podcast-top-pool.json");
        _podcastHttpHandler = new TestPodcastHttpMessageHandler();
        _podcastCatalogHttpHandler = new TestPodcastCatalogHttpMessageHandler();
        _podcastHostResolver = new TestPodcastHostResolver();
        _podcastCacheTimeProvider = new TestPodcastCacheTimeProvider();
        var playbackHistory = new PlaybackHistory(Path.Combine(_podcastTopPoolCacheDirectory, "history.json"));
        _podcastUpstreamClient = new HttpClient(_podcastHttpHandler);
        _podcastCatalogHttpClient = new HttpClient(_podcastCatalogHttpHandler);
        var podcastFeedHttpClient = new PodcastFeedHttpClient(_podcastUpstreamClient, _podcastHostResolver);
        _server = new WebServer(
            new AppConfig { Port = port },
            BuildStubCallbacks(
                (url, title) => _radioPlayRequests.Add((url, title)),
                (path, position) => _videoPositionPlayRequests.Add((path, position)),
                () => _playbackStatus),
            playbackHistory: playbackHistory,
            podcastFeedHttpClient: podcastFeedHttpClient,
            podcastFeedCacheTimeProvider: _podcastCacheTimeProvider,
            podcastCatalogHttpClient: _podcastCatalogHttpClient,
            podcastTopPoolCacheFilePath: _podcastTopPoolCacheFile);
        _server.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
    }

    public void Dispose()
    {
        _server.Stop();
        _client.Dispose();
        _podcastUpstreamClient.Dispose();
        _podcastCatalogHttpClient.Dispose();
        if (Directory.Exists(_podcastTopPoolCacheDirectory))
            Directory.Delete(_podcastTopPoolCacheDirectory, recursive: true);
    }

    // â”€â”€ /api/status â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Status_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Status_ResponseContainsIsPlayingField()
    {
        var response = await _client.GetAsync("/api/status");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("isPlaying", out _));
    }

    [Fact]
    public async Task Status_ResponseContainsVolumeField()
    {
        var response = await _client.GetAsync("/api/status");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("volume", out _));
    }

    [Fact]
    public async Task VideoPlay_PassesStartPositionToPositionAwareCallback()
    {
        Directory.CreateDirectory(_podcastTopPoolCacheDirectory);
        var filePath = Path.Combine(_podcastTopPoolCacheDirectory, "position-handoff.mp4");
        await File.WriteAllBytesAsync(filePath, [0x00]);
        var encodedPath = WebPathHelpers.EncodePath(filePath);

        var response = await _client.GetAsync(
            "/api/video/play?path=" + Uri.EscapeDataString(encodedPath) + "&pos=123.45");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((filePath, 123.45), Assert.Single(_videoPositionPlayRequests));
    }

    [Fact]
    public async Task VideoPlay_RejectsNegativeStartPosition()
    {
        Directory.CreateDirectory(_podcastTopPoolCacheDirectory);
        var filePath = Path.Combine(_podcastTopPoolCacheDirectory, "invalid-position.mp4");
        await File.WriteAllBytesAsync(filePath, [0x00]);
        var encodedPath = WebPathHelpers.EncodePath(filePath);

        var response = await _client.GetAsync(
            "/api/video/play?path=" + Uri.EscapeDataString(encodedPath) + "&pos=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_videoPositionPlayRequests);
    }

    [Fact]
    public async Task VideoStatus_SavesProgressForTheClientThatStartedServerPlayback()
    {
        Directory.CreateDirectory(_podcastTopPoolCacheDirectory);
        var filePath = Path.Combine(_podcastTopPoolCacheDirectory, "server-progress.mp4");
        await File.WriteAllBytesAsync(filePath, [0x00]);
        var encodedPath = WebPathHelpers.EncodePath(filePath);

        using var play = await _client.GetAsync("/api/video/play?path=" + Uri.EscapeDataString(encodedPath));
        Assert.Equal(HttpStatusCode.OK, play.StatusCode);

        _playbackStatus = new PlaybackStatus
        {
            IsPlaying = true,
            FilePath = filePath,
            PositionSeconds = 42.5,
            DurationSeconds = 1800,
        };
        using var activeStatus = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, activeStatus.StatusCode);

        _playbackStatus = _playbackStatus with
        {
            IsPlaying = false,
            PositionSeconds = 1790,
        };
        using var stoppedStatus = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, stoppedStatus.StatusCode);

        var history = new PlaybackHistory(Path.Combine(_podcastTopPoolCacheDirectory, "history.json"));
        var entry = Assert.Single(history.GetRecent(10), item => item.FilePath == filePath);
        Assert.Equal(1800, entry.PositionSeconds);
        Assert.Contains(filePath, history.GetWatchedSet());
    }

    [Fact]
    public async Task VideoStop_SavesLatestProgressForTheClientThatStartedServerPlayback()
    {
        Directory.CreateDirectory(_podcastTopPoolCacheDirectory);
        var filePath = Path.Combine(_podcastTopPoolCacheDirectory, "server-stop-progress.mp4");
        await File.WriteAllBytesAsync(filePath, [0x00]);
        var encodedPath = WebPathHelpers.EncodePath(filePath);

        using var play = await _client.GetAsync("/api/video/play?path=" + Uri.EscapeDataString(encodedPath));
        Assert.Equal(HttpStatusCode.OK, play.StatusCode);

        _playbackStatus = new PlaybackStatus
        {
            IsPlaying = true,
            FilePath = filePath,
            PositionSeconds = 60,
            DurationSeconds = 1800,
        };
        using var activeStatus = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, activeStatus.StatusCode);

        _playbackStatus = _playbackStatus with { PositionSeconds = 120 };
        using var stop = await _client.GetAsync("/api/stop");
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);

        var history = new PlaybackHistory(Path.Combine(_podcastTopPoolCacheDirectory, "history.json"));
        var entry = Assert.Single(history.GetRecent(10), item => item.FilePath == filePath);
        Assert.Equal(120, entry.PositionSeconds);
    }

    // â”€â”€ /api/library-status â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task LibraryStatus_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/library-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LibraryStatus_ResponseContainsIsScanning()
    {
        var response = await _client.GetAsync("/api/library-status");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        // The endpoint serializes the LibraryScanStatus record with default (PascalCase) naming.
        Assert.True(
            doc.RootElement.TryGetProperty("IsScanning", out _) ||
            doc.RootElement.TryGetProperty("isScanning", out _));
    }

    // â”€â”€ /api/version â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Version_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Version_ResponseContainsVersionField()
    {
        var response = await _client.GetAsync("/api/version");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("version", out _));
    }

    // â”€â”€ /health â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ResponseContainsHtmlDoctype()
    {
        var response = await _client.GetAsync("/health");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("<!DOCTYPE html>", html, StringComparison.OrdinalIgnoreCase);
    }

    // â”€â”€ 404 for unknown paths â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task UnknownPath_Returns404()
    {
        var response = await _client.GetAsync("/api/this-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // â”€â”€ Rate limiting â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task RateLimiting_BypassesLocalAddresses()
    {
        var port = FindFreePort();
        var config = new AppConfig { Port = port, MaxRequestsPerIpPerWindow = 2, RateLimitWindowSeconds = 60 };
        var tightServer = new WebServer(config, BuildStubCallbacks());
        using var tightClient = new HttpClient();
        tightClient.BaseAddress = new Uri($"http://localhost:{port}");
        tightServer.Start();

        try
        {
            for (var i = 0; i < 3; i++)
            {
                using var response = await tightClient.GetAsync("/api/version");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        finally
        {
            tightServer.Stop();
        }
    }

    [Fact]
    public void RateLimiting_LimitsNonLocalAddressesAfterConfiguredQuota()
    {
        var config = new AppConfig { MaxRequestsPerIpPerWindow = 2, RateLimitWindowSeconds = 60 };
        var server = new WebServer(config, BuildStubCallbacks());

        try
        {
            Assert.False(server.IsRateLimited("203.0.113.42"));
            Assert.False(server.IsRateLimited("203.0.113.42"));
            Assert.True(server.IsRateLimited("203.0.113.42"));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task RateLimiting_Disabled_WhenLimitIsZero()
    {
        var port = FindFreePort();
        var config = new AppConfig { Port = port, MaxRequestsPerIpPerWindow = 0 };
        var openServer = new WebServer(config, BuildStubCallbacks());
        using var openClient = new HttpClient();
        openClient.BaseAddress = new Uri($"http://localhost:{port}");
        openServer.Start();

        for (var i = 0; i < 10; i++)
        {
            var response = await openClient.GetAsync("/api/version");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        openServer.Stop();
    }

    [Fact]
    public async Task LiveTvReindex_Get_ReturnsMethodNotAllowed()
    {
        var response = await _client.GetAsync("/api/live-tv/reindex");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task LiveTvPlay_WhenTargetIsLocalhost_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/live-tv/play?url=http%3A%2F%2Flocalhost%2Flive.m3u8&name=Test");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // -- /api/dlna/renderers ---------------------------------------------------

    [Fact]
    public async Task DlnaRenderers_WhenNoDlnaDiscovery_ReturnsOkWithEmptyArray()
    {
        var response = await _client.GetAsync("/api/dlna/renderers");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task DlnaRenderers_ResponseContentTypeIsJson()
    {
        var response = await _client.GetAsync("/api/dlna/renderers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    // -- /api/dlna/play --------------------------------------------------------

    [Fact]
    public async Task DlnaPlay_WhenNoDlnaDiscovery_Returns503()
    {
        using var content = new StringContent(
            """{"controlUrl":"http://192.168.1.1/ctl","mediaUrl":"http://localhost/video.mp4"}""",
            System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/dlna/play", content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(doc.RootElement.TryGetProperty("ok", out var okProp));
        Assert.False(okProp.GetBoolean());
    }

    [Fact]
    public async Task DlnaPlay_WhenBodyIsInvalidJson_Returns400OrServiceUnavailable()
    {
        // When DlnaDiscovery is not injected the server returns 503 before parsing the body.
        // When it is injected and the body is malformed, it returns 400.
        using var content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/dlna/play", content);

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task DlnaPlay_WhenControlUrlMissingFromBody_Returns503OrBadRequest()
    {
        using var content = new StringContent(
            """{"mediaUrl":"http://localhost/video.mp4"}""",
            System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/dlna/play", content);

        // 503 = no DLNA running; 400 = missing required field.
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    // -- /api/dlna unknown sub-path --------------------------------------------

    [Fact]
    public async Task DlnaUnknownSubPath_Returns404()
    {
        var response = await _client.GetAsync("/api/dlna/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RadioPlay_Get_ReturnsMethodNotAllowed()
    {
        var response = await _client.GetAsync("/api/radio/play?url=https%3A%2F%2Fexample.com%2Fstream&name=Test");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task RadioPlay_PostLocalhost_ReturnsBadRequest()
    {
        using var content = new StringContent(string.Empty);
        var response = await _client.PostAsync("/api/radio/play?url=http%3A%2F%2Flocalhost%2Fstream&name=Test", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RadioStop_Get_ReturnsMethodNotAllowed()
    {
        var response = await _client.GetAsync("/api/radio/stop");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task SettingsTestNetworkShare_RejectsNonUncPathWithoutExposingPassword()
    {
        const string password = "must-not-be-returned";
        using var content = new StringContent(
            """{"path":"not-a-unc-path","username":"test-user","password":"must-not-be-returned"}""",
            System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/settings/test-network-share", content);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("UNC", doc.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(password, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsPost_RetainsNetworkShareCredentialsForSubsequentGet()
    {
        using var content = new StringContent(
            """{"networkShareCredentials":[{"path":"\\\\fse\\Music","username":"test-user","password":"test-password"}]}""",
            System.Text.Encoding.UTF8, "application/json");

        var saveResponse = await _client.PostAsync("/api/settings", content);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        var getResponse = await _client.GetAsync("/api/settings");
        var json = await getResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var credential = Assert.Single(doc.RootElement.GetProperty("networkShareCredentials").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(@"\\fse\Music", credential.GetProperty("path").GetString());
        Assert.Equal("test-user", credential.GetProperty("username").GetString());
        Assert.Equal("test-password", credential.GetProperty("password").GetString());
    }

    [Fact]
    public async Task PodcastsCountries_ReturnsCountryDirectory()
    {
        var response = await _client.GetAsync("/api/podcasts/countries");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var hasUnitedStates = false;

        foreach (var country in doc.RootElement.EnumerateArray())
        {
            if (country.GetProperty("code").GetString() == "US")
            {
                hasUnitedStates = true;
                break;
            }
        }

        Assert.True(hasUnitedStates);
    }

    [Fact]
    public async Task PodcastsSearch_GlobalFallbackBalancesCountriesAndCachesStableDeduplicatedOrder()
    {
        var countries = new[] { "US", "GB", "CA", "AU", "DE", "FR", "ES", "IT", "NL", "BE" };
        _podcastCatalogHttpHandler.ResponseFactory = request =>
        {
            var country = request.RequestUri!.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)[2]
                .ToUpperInvariant();
            var sharedFeedUrl = country == "GB"
                ? "https://FEEDS.test/shared.xml"
                : "https://feeds.test/shared.xml";
            var results = Enumerable.Range(0, 25)
                .Select(index => new
                {
                    name = index == 1
                        ? "Shared Top"
                        : $"{country} Top {(index == 0 ? 1 : index)}",
                    artistName = index == 1 ? "Shared Author" : $"{country} Author",
                    url = index == 1
                        ? sharedFeedUrl
                        : $"https://feeds.test/{country.ToLowerInvariant()}-{index}.xml",
                    artworkUrl100 = string.Empty,
                    genres = new[] { new { name = "News" } },
                })
                .ToArray();
            if (country == "BE")
                results = results[..1];

            var json = JsonSerializer.Serialize(new { feed = new { results } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
        };

        using var firstResponse = await _client.GetAsync("/api/podcasts/search?limit=50");
        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        using var firstDocument = JsonDocument.Parse(firstJson);
        var firstRoot = firstDocument.RootElement;
        var firstTitles = firstRoot.GetProperty("shows")
            .EnumerateArray()
            .Select(show => show.GetProperty("title").GetString())
            .ToArray();

        using var secondResponse = await _client.GetAsync("/api/podcasts/search?limit=5&offset=5");
        var secondJson = await secondResponse.Content.ReadAsStringAsync();
        using var secondDocument = JsonDocument.Parse(secondJson);
        var secondRoot = secondDocument.RootElement;
        var secondTitles = secondDocument.RootElement.GetProperty("shows")
            .EnumerateArray()
            .Select(show => show.GetProperty("title").GetString())
            .ToArray();
        using var stableResponse = await _client.GetAsync("/api/podcasts/search?limit=49");
        var stableJson = await stableResponse.Content.ReadAsStringAsync();
        using var stableDocument = JsonDocument.Parse(stableJson);
        var stableTitles = stableDocument.RootElement.GetProperty("shows")
            .EnumerateArray()
            .Select(show => show.GetProperty("title").GetString())
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stableResponse.StatusCode);
        Assert.False(firstRoot.GetProperty("refreshing").GetBoolean());
        Assert.Equal(countries.Select(country => $"{country} Top 1"), firstTitles.Take(countries.Length));
        Assert.Equal(50, firstTitles.Length);
        Assert.Equal(1, firstTitles.Count(title => title == "Shared Top"));
        Assert.Equal(firstTitles.Skip(5).Take(5), secondTitles);
        Assert.Equal(firstTitles.Take(49), stableTitles);
        Assert.True(firstRoot.GetProperty("hasMore").GetBoolean());
        Assert.Equal(200, firstRoot.GetProperty("total").GetInt32());
        Assert.True(secondRoot.GetProperty("hasMore").GetBoolean());
        Assert.Equal(200, secondRoot.GetProperty("total").GetInt32());
        Assert.Equal(countries.Length, _podcastCatalogHttpHandler.RequestedUris.Count);
        Assert.All(
            _podcastCatalogHttpHandler.RequestedUris,
            uri =>
            {
                Assert.Equal("rss.marketingtools.apple.com", uri.Host);
                Assert.Contains("/top/100/", uri.AbsolutePath, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task PodcastsSearch_ColdGlobalPoolReturnsProgressBeforeBalancedRefreshCompletes()
    {
        var secondCountryGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _podcastCatalogHttpHandler.AsyncResponseFactory = async (request, cancellationToken) =>
        {
            var country = GetPodcastChartCountry(request);
            if (country == "GB")
                await secondCountryGate.Task.WaitAsync(cancellationToken);

            return BuildPodcastTopChartResponse(country, "Progress");
        };

        try
        {
            var initialResponseTask = _client.GetAsync("/api/podcasts/search?limit=12");
            await WaitForPodcastCatalogRequestsAsync(2, TimeSpan.FromSeconds(5));

            using var initialResponse = await initialResponseTask.WaitAsync(TimeSpan.FromSeconds(5));
            var initialJson = await initialResponse.Content.ReadAsStringAsync();
            using var initialDocument = JsonDocument.Parse(initialJson);
            var initialRoot = initialDocument.RootElement;

            Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
            Assert.Equal(12, initialRoot.GetProperty("shows").GetArrayLength());
            Assert.Equal("Progress US Top 1", initialRoot.GetProperty("shows")[0].GetProperty("title").GetString());
            Assert.True(initialRoot.GetProperty("refreshing").GetBoolean());
            Assert.True(initialRoot.GetProperty("hasMore").GetBoolean());
            Assert.Equal(JsonValueKind.Null, initialRoot.GetProperty("total").ValueKind);

            using var progressResponse = await _client.GetAsync("/api/podcasts/search?limit=12")
                .WaitAsync(TimeSpan.FromSeconds(5));
            var progressJson = await progressResponse.Content.ReadAsStringAsync();
            using var progressDocument = JsonDocument.Parse(progressJson);
            var progressRoot = progressDocument.RootElement;

            Assert.Equal(HttpStatusCode.OK, progressResponse.StatusCode);
            Assert.True(progressRoot.GetProperty("refreshing").GetBoolean());
            Assert.Equal("Progress US Top 1", progressRoot.GetProperty("shows")[0].GetProperty("title").GetString());
            Assert.Equal(2, _podcastCatalogHttpHandler.RequestedCount);

            secondCountryGate.TrySetResult(true);
            await WaitForPodcastCatalogRequestsAsync(10, TimeSpan.FromSeconds(5));
            await WaitForPodcastTopPoolCacheAsync(_podcastTopPoolCacheFile, TimeSpan.FromSeconds(5));

            using var completedResponse = await _client.GetAsync("/api/podcasts/search?limit=12");
            var completedJson = await completedResponse.Content.ReadAsStringAsync();
            using var completedDocument = JsonDocument.Parse(completedJson);
            var completedRoot = completedDocument.RootElement;
            var completedTitles = completedRoot.GetProperty("shows")
                .EnumerateArray()
                .Select(show => show.GetProperty("title").GetString())
                .ToArray();

            Assert.Equal(HttpStatusCode.OK, completedResponse.StatusCode);
            Assert.False(completedRoot.GetProperty("refreshing").GetBoolean());
            Assert.Equal(
                new[] { "Progress US Top 1", "Progress GB Top 1", "Progress CA Top 1" },
                completedTitles.Take(3));
            Assert.Equal(200, completedRoot.GetProperty("total").GetInt32());
            Assert.Equal(10, _podcastCatalogHttpHandler.RequestedCount);
        }
        finally
        {
            secondCountryGate.TrySetResult(true);
            if (_podcastCatalogHttpHandler.RequestedCount > 0)
                await WaitForPodcastCatalogRequestsAsync(10, TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task PodcastsSearch_DoesNotPersistEmptyPoolAndRecoversAfterCatalogFailure()
    {
        _podcastCatalogHttpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        using var failedResponse = await _client.GetAsync("/api/podcasts/search?limit=12");
        var failedJson = await failedResponse.Content.ReadAsStringAsync();
        using var failedDocument = JsonDocument.Parse(failedJson);

        Assert.Equal(HttpStatusCode.OK, failedResponse.StatusCode);
        Assert.Empty(failedDocument.RootElement.GetProperty("shows").EnumerateArray());
        Assert.False(File.Exists(_podcastTopPoolCacheFile));
        Assert.Equal(10, _podcastCatalogHttpHandler.RequestedCount);

        _podcastCatalogHttpHandler.ResponseFactory = request =>
        {
            var country = GetPodcastChartCountry(request);
            return BuildPodcastTopChartResponse(country, "Recovered");
        };

        using var recoveredResponse = await _client.GetAsync("/api/podcasts/search?limit=12");
        var recoveredJson = await recoveredResponse.Content.ReadAsStringAsync();
        using var recoveredDocument = JsonDocument.Parse(recoveredJson);

        Assert.Equal(HttpStatusCode.OK, recoveredResponse.StatusCode);
        Assert.Equal(12, recoveredDocument.RootElement.GetProperty("shows").GetArrayLength());
        Assert.Equal("Recovered US Top 1", recoveredDocument.RootElement.GetProperty("shows")[0].GetProperty("title").GetString());
        Assert.True(recoveredDocument.RootElement.GetProperty("refreshing").GetBoolean());
        await WaitForPodcastCatalogRequestsAsync(20, TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(_podcastTopPoolCacheFile));
        Assert.Equal(20, _podcastCatalogHttpHandler.RequestedCount);
    }

    [Fact]
    public async Task PodcastsSearch_ServesPersistedPoolDuringSingleFlightPartialRefresh()
    {
        _podcastCatalogHttpHandler.ResponseFactory = request =>
        {
            var country = GetPodcastChartCountry(request);
            return BuildPodcastTopChartResponse(country, "Baseline");
        };

        using var initialResponse = await _client.GetAsync("/api/podcasts/search?limit=50");
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        Assert.True(File.Exists(_podcastTopPoolCacheFile));

        var staleSavedAt = DateTimeOffset.UtcNow.AddDays(-2);
        using (var savedDocument = JsonDocument.Parse(File.ReadAllText(_podcastTopPoolCacheFile)))
        {
            var savedShows = savedDocument.RootElement.GetProperty("Shows").Clone();
            var staleCache = JsonSerializer.Serialize(new
            {
                Version = 1,
                SavedAtUtc = staleSavedAt,
                IsPartial = false,
                SuccessfulSources = 10,
                AttemptedSources = 10,
                Shows = savedShows,
            });
            File.WriteAllText(_podcastTopPoolCacheFile, staleCache);
        }

        var refreshGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _podcastCatalogHttpHandler.AsyncResponseFactory = async (request, cancellationToken) =>
        {
            await refreshGate.Task.WaitAsync(cancellationToken);
            var country = GetPodcastChartCountry(request);
            return country == "US"
                ? BuildPodcastTopChartResponse(country, "Partial")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        var restartedPort = FindFreePort();
        var restartedServer = new WebServer(
            new AppConfig { Port = restartedPort },
            BuildStubCallbacks(),
            podcastFeedHttpClient: new PodcastFeedHttpClient(_podcastUpstreamClient, _podcastHostResolver),
            podcastFeedCacheTimeProvider: _podcastCacheTimeProvider,
            podcastCatalogHttpClient: _podcastCatalogHttpClient,
            podcastTopPoolCacheFilePath: _podcastTopPoolCacheFile);
        restartedServer.Start();
        using var restartedClient = new HttpClient();
        restartedClient.BaseAddress = new Uri($"http://localhost:{restartedPort}");

        try
        {
            using var firstStaleResponse = await restartedClient.GetAsync("/api/podcasts/search?limit=5")
                .WaitAsync(TimeSpan.FromSeconds(5));
            var firstStaleJson = await firstStaleResponse.Content.ReadAsStringAsync();
            using var firstStaleDocument = JsonDocument.Parse(firstStaleJson);

            Assert.Equal(HttpStatusCode.OK, firstStaleResponse.StatusCode);
            Assert.Equal("Baseline US Top 1", firstStaleDocument.RootElement.GetProperty("shows")[0].GetProperty("title").GetString());
            await WaitForPodcastCatalogRequestsAsync(11, TimeSpan.FromSeconds(5));

            using var secondStaleResponse = await restartedClient.GetAsync("/api/podcasts/search?limit=5")
                .WaitAsync(TimeSpan.FromSeconds(5));
            var secondStaleJson = await secondStaleResponse.Content.ReadAsStringAsync();
            using var secondStaleDocument = JsonDocument.Parse(secondStaleJson);

            Assert.Equal(HttpStatusCode.OK, secondStaleResponse.StatusCode);
            Assert.Equal("Baseline US Top 1", secondStaleDocument.RootElement.GetProperty("shows")[0].GetProperty("title").GetString());
            Assert.Equal(11, _podcastCatalogHttpHandler.RequestedCount);

            refreshGate.TrySetResult(true);
            await WaitForPodcastCatalogRequestsAsync(20, TimeSpan.FromSeconds(5));

            using var refreshedCache = JsonDocument.Parse(File.ReadAllText(_podcastTopPoolCacheFile));
            Assert.Equal(
                staleSavedAt,
                refreshedCache.RootElement.GetProperty("SavedAtUtc").GetDateTimeOffset());
            Assert.Equal(200, refreshedCache.RootElement.GetProperty("Shows").GetArrayLength());

            using var staleAfterFailureDocument = await WaitForPodcastRefreshStateAsync(
                restartedClient,
                "/api/podcasts/search?limit=5",
                refreshing: false,
                TimeSpan.FromSeconds(5));
            Assert.False(staleAfterFailureDocument.RootElement.GetProperty("refreshing").GetBoolean());
            Assert.Equal(
                "Baseline US Top 1",
                staleAfterFailureDocument.RootElement.GetProperty("shows")[0].GetProperty("title").GetString());
            Assert.Equal(20, _podcastCatalogHttpHandler.RequestedCount);
        }
        finally
        {
            refreshGate.TrySetResult(true);
            if (_podcastCatalogHttpHandler.RequestedCount > 10)
                await WaitForPodcastCatalogRequestsAsync(20, TimeSpan.FromSeconds(5));
            restartedServer.Stop();
        }
    }

    [Fact]
    public async Task PodcastsSearch_CountryOnlyUsesStorefrontChart()
    {
        _podcastCatalogHttpHandler.ResponseFactory = request =>
        {
            if (!request.RequestUri!.Host.Equals("rss.marketingtools.apple.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return BuildPodcastTopChartResponse(GetPodcastChartCountry(request), "Storefront");
        };

        using var response = await _client.GetAsync("/api/podcasts/search?country=CO&limit=2");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Storefront CO Top 1", root.GetProperty("shows")[0].GetProperty("title").GetString());
        Assert.Equal("CO", root.GetProperty("shows")[0].GetProperty("country").GetString());
        Assert.Equal(2, root.GetProperty("shows").GetArrayLength());
        Assert.True(root.GetProperty("hasMore").GetBoolean());
        Assert.Equal(25, root.GetProperty("total").GetInt32());
        Assert.Single(_podcastCatalogHttpHandler.RequestedUris);
        Assert.Equal("rss.marketingtools.apple.com", _podcastCatalogHttpHandler.RequestedUris[0].Host);
        Assert.Contains(
            "/api/v2/co/podcasts/top/100/podcasts.json",
            _podcastCatalogHttpHandler.RequestedUris[0].AbsolutePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PodcastsSearch_CountryChartFailureFallsBackToCountrySearch()
    {
        _podcastCatalogHttpHandler.ResponseFactory = request =>
        {
            if (request.RequestUri!.Host.Equals("rss.marketingtools.apple.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            if (!request.RequestUri.Host.Equals("itunes.apple.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            var results = Enumerable.Range(1, 200)
                .Select(index => new
                {
                    collectionName = $"Country Search Result {index}",
                    artistName = "Country Search Author",
                    feedUrl = $"https://feeds.test/country-search-{index}.xml",
                    artworkUrl100 = string.Empty,
                    primaryGenreName = "Comedy",
                    country = "COL",
                })
                .ToArray();
            var json = JsonSerializer.Serialize(new { resultCount = results.Length, results });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
        };

        using var response = await _client.GetAsync("/api/podcasts/search?country=CO&limit=1");
        var responseJson = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Country Search Result 1", root.GetProperty("shows")[0].GetProperty("title").GetString());
        Assert.Equal("CO", root.GetProperty("shows")[0].GetProperty("country").GetString());
        Assert.Equal(2, _podcastCatalogHttpHandler.RequestedUris.Count);
        Assert.Equal("rss.marketingtools.apple.com", _podcastCatalogHttpHandler.RequestedUris[0].Host);
        Assert.Equal("itunes.apple.com", _podcastCatalogHttpHandler.RequestedUris[1].Host);
        Assert.Contains("country=CO", _podcastCatalogHttpHandler.RequestedUris[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PodcastsSearch_ExplicitCountryRemainsCountrySpecific()
    {
        _podcastCatalogHttpHandler.ResponseFactory = request =>
        {
            if (!request.RequestUri!.Host.Equals("itunes.apple.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            var json = JsonSerializer.Serialize(new
            {
                resultCount = 2,
                results = new[]
                {
                    new
                    {
                        collectionName = "Country Result 1",
                        artistName = "Country Author",
                        feedUrl = "https://feeds.test/country-1.xml",
                        artworkUrl100 = string.Empty,
                        primaryGenreName = "News",
                        country = "US",
                    },
                    new
                    {
                        collectionName = "Country Result 2",
                        artistName = "Country Author",
                        feedUrl = "https://feeds.test/country-2.xml",
                        artworkUrl100 = string.Empty,
                        primaryGenreName = "News",
                        country = "US",
                    },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
        };

        using var firstResponse = await _client.GetAsync(
            "/api/podcasts/search?country=US&q=phase-three-country-specific&limit=1&offset=0");
        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        using var firstDocument = JsonDocument.Parse(firstJson);
        using var secondResponse = await _client.GetAsync(
            "/api/podcasts/search?country=US&q=phase-three-country-specific&limit=1&offset=1");
        var secondJson = await secondResponse.Content.ReadAsStringAsync();
        using var secondDocument = JsonDocument.Parse(secondJson);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(
            "Country Result 1",
            firstDocument.RootElement.GetProperty("shows")[0].GetProperty("title").GetString());
        Assert.Equal(
            "US",
            firstDocument.RootElement.GetProperty("shows")[0].GetProperty("country").GetString());
        Assert.True(firstDocument.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, firstDocument.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(
            "Country Result 2",
            secondDocument.RootElement.GetProperty("shows")[0].GetProperty("title").GetString());
        Assert.False(secondDocument.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, secondDocument.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, _podcastCatalogHttpHandler.RequestedUris.Count);
        Assert.All(
            _podcastCatalogHttpHandler.RequestedUris,
            uri =>
            {
                Assert.Equal("itunes.apple.com", uri.Host);
                Assert.Contains("country=US", uri.Query, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task PodcastsFeed_RejectsPrivateHost()
    {
        var response = await _client.GetAsync("/api/podcasts/feed?url=http%3A%2F%2F127.0.0.1%2Ffeed.xml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsPlay_RejectsPrivateHost()
    {
        using var content = new StringContent(string.Empty);
        var response = await _client.PostAsync("/api/podcasts/play?url=http%3A%2F%2F127.0.0.1%2Fepisode.mp3", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_radioPlayRequests);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsUserInfoWithoutMakingAnUpstreamRequest()
    {
        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://user:secret@feed.test/feed.xml"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_podcastHttpHandler.RequestedUris);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsHostnameResolvingToPrivateAddressBeforeRequest()
    {
        _podcastHostResolver.Set("private.test", IPAddress.Parse("10.20.30.40"));

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://private.test/feed.xml"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsMixedPublicAndPrivateDnsResults()
    {
        _podcastHostResolver.Set(
            "mixed.test",
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Parse("10.20.30.40"));

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://mixed.test/feed.xml"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsFeed_ReturnsBadGatewayWhenDnsCannotBeValidated()
    {
        _podcastHostResolver.Set("unresolved.test");

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://unresolved.test/feed.xml"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsFeed_DoesNotFollowUnsupportedOrCredentialedRedirects()
    {
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("ftp://user:password@feed.test/file.xml");
            return redirect;
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/start"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(_podcastHttpHandler.RequestedUris);
        Assert.DoesNotContain("password", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsRedirectToPrivateAddressBeforeFollowing()
    {
        _podcastHostResolver.Set("private.test", IPAddress.Parse("192.168.1.10"));
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://private.test/feed.xml");
            return response;
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/start"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsFeed_FollowsRelativePublicRedirect()
    {
        _podcastHttpHandler.ResponseFactory = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/series/start")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("../feed.xml", UriKind.Relative);
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss><channel><item><title>Episode</title><enclosure url=\"https://audio.test/episode.mp3\" /></item></channel></rss>"),
            };
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/series/start"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _podcastHttpHandler.RequestedUris.Count);
        Assert.Equal("/feed.xml", _podcastHttpHandler.RequestedUris[1].AbsolutePath);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Episode", document.RootElement.GetProperty("episodes")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task PodcastsFeed_RejectsHttpsDowngrade()
    {
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("http://feed.test/insecure");
            return redirect;
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/start"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsRedirectLoop()
    {
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("/start", UriKind.Relative);
            return redirect;
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/start"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsMoreThanFiveRedirects()
    {
        _podcastHttpHandler.ResponseFactory = request =>
        {
            var currentNumber = int.Parse(request.RequestUri!.AbsolutePath.TrimStart('/'));
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri($"/{currentNumber + 1}", UriKind.Relative);
            return redirect;
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(6, _podcastHttpHandler.RequestedUris.Count);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsOversizedResponse()
    {
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            response.Content.Headers.ContentLength = PodcastFeedHttpClient.MaxResponseBytes + 1;
            return response;
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/feed.xml"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task PodcastsFeed_CachesParsedFeedAcrossOffsetPages()
    {
        _podcastHttpHandler.ResponseFactory = _ => PodcastFeedResponse(
            "<rss><channel>" +
            "<item><title>First</title><enclosure url=\"https://audio.test/first.mp3\" /></item>" +
            "<item><title>Second</title><enclosure url=\"https://audio.test/second.mp3\" /></item>" +
            "</channel></rss>");

        var feedUrl = Uri.EscapeDataString("https://feed.test/feed.xml");
        using var firstResponse = await _client.GetAsync(
            $"/api/podcasts/feed?url={feedUrl}&limit=1&offset=0");
        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        using var secondResponse = await _client.GetAsync(
            $"/api/podcasts/feed?url={feedUrl}&limit=1&offset=1");
        var secondJson = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using var firstDocument = JsonDocument.Parse(firstJson);
        using var secondDocument = JsonDocument.Parse(secondJson);
        Assert.Equal("First", firstDocument.RootElement.GetProperty("episodes")[0].GetProperty("title").GetString());
        Assert.Equal("Second", secondDocument.RootElement.GetProperty("episodes")[0].GetProperty("title").GetString());
        Assert.True(firstDocument.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.False(secondDocument.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, secondDocument.RootElement.GetProperty("total").GetInt32());
        Assert.Single(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsFeed_CacheKeyNormalizesHostAndPortButKeepsQueryParameters()
    {
        _podcastHttpHandler.ResponseFactory = request =>
        {
            var title = request.RequestUri!.Query.Contains("edition=b", StringComparison.Ordinal)
                ? "Edition B"
                : "Edition A";
            return PodcastFeedResponse(
                $"<rss><channel><item><title>{title}</title><enclosure url=\"https://audio.test/{title.Replace(' ', '-').ToLowerInvariant()}.mp3\" /></item></channel></rss>");
        };

        using var firstResponse = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://FEED.test:443/feed.xml?edition=a"));
        using var normalizedResponse = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/feed.xml?edition=a"));
        using var differentQueryResponse = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/feed.xml?edition=b"));
        var normalizedJson = await normalizedResponse.Content.ReadAsStringAsync();
        var differentQueryJson = await differentQueryResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, normalizedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, differentQueryResponse.StatusCode);
        using var normalizedDocument = JsonDocument.Parse(normalizedJson);
        using var differentQueryDocument = JsonDocument.Parse(differentQueryJson);
        Assert.Equal("Edition A", normalizedDocument.RootElement.GetProperty("episodes")[0].GetProperty("title").GetString());
        Assert.Equal("Edition B", differentQueryDocument.RootElement.GetProperty("episodes")[0].GetProperty("title").GetString());
        Assert.Equal(2, _podcastHttpHandler.RequestedUris.Count);
    }

    [Fact]
    public async Task PodcastsFeed_RevalidatesExpiredCacheWithEntityTagAndLastModified()
    {
        var lastModified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var responseDate = _podcastCacheTimeProvider.GetUtcNow().AddSeconds(-10);
        var requestCount = 0;
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            requestCount++;
            if (requestCount == 1)
                return PodcastFeedResponse(
                    "<rss><channel><item><title>Cached</title><enclosure url=\"https://audio.test/cached.mp3\" /></item></channel></rss>",
                    cacheControl: "max-age=30",
                    etag: "\"revision-1\"",
                    lastModified: lastModified,
                    responseDate: responseDate,
                    age: TimeSpan.FromSeconds(15));

            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.TryAddWithoutValidation("Cache-Control", "max-age=30");
            return notModified;
        };

        var feedUrl = Uri.EscapeDataString("https://feed.test/conditional.xml");
        using var firstResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        _podcastCacheTimeProvider.Advance(TimeSpan.FromSeconds(16));
        using var secondResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        var secondJson = await secondResponse.Content.ReadAsStringAsync();
        using var thirdResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, thirdResponse.StatusCode);
        Assert.Equal("\"revision-1\"", _podcastHttpHandler.RequestedRequests[1].IfNoneMatch);
        Assert.Equal(lastModified, _podcastHttpHandler.RequestedRequests[1].IfModifiedSince);
        using var document = JsonDocument.Parse(secondJson);
        Assert.Equal("Cached", document.RootElement.GetProperty("episodes")[0].GetProperty("title").GetString());
        Assert.Equal(2, _podcastHttpHandler.RequestedUris.Count);
    }

    [Fact]
    public async Task PodcastsFeed_ReplacesExpiredEntryWhenFeedChanges()
    {
        var requestCount = 0;
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            requestCount++;
            return requestCount == 1
                ? PodcastFeedResponse(
                    "<rss><channel><item><title>Old</title><enclosure url=\"https://audio.test/old.mp3\" /></item></channel></rss>",
                    cacheControl: "max-age=1",
                    etag: "\"revision-1\"")
                : PodcastFeedResponse(
                    "<rss><channel><item><title>New</title><enclosure url=\"https://audio.test/new.mp3\" /></item></channel></rss>",
                    cacheControl: "max-age=30",
                    etag: "\"revision-2\"");
        };

        var feedUrl = Uri.EscapeDataString("https://feed.test/changed.xml");
        using var firstResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        _podcastCacheTimeProvider.Advance(TimeSpan.FromSeconds(2));
        using var secondResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        using var thirdResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        var secondJson = await secondResponse.Content.ReadAsStringAsync();
        var thirdJson = await thirdResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, thirdResponse.StatusCode);
        Assert.Equal("\"revision-1\"", _podcastHttpHandler.RequestedRequests[1].IfNoneMatch);
        Assert.Equal(2, _podcastHttpHandler.RequestedUris.Count);
        using var secondDocument = JsonDocument.Parse(secondJson);
        using var thirdDocument = JsonDocument.Parse(thirdJson);
        Assert.Equal("New", secondDocument.RootElement.GetProperty("episodes")[0].GetProperty("title").GetString());
        Assert.Equal("New", thirdDocument.RootElement.GetProperty("episodes")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task PodcastsFeed_RevalidatesRedirectedFeedAtValidatorUriOnly()
    {
        var finalResponseCount = 0;
        _podcastHttpHandler.ResponseFactory = request =>
        {
            if (request.RequestUri!.Host.Equals("feed.test", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://cdn.test/final.xml?edition=1") },
                };
            }

            finalResponseCount++;
            return finalResponseCount == 1
                ? PodcastFeedResponse(
                    "<rss><channel><item><title>Redirected</title><enclosure url=\"https://audio.test/redirected.mp3\" /></item></channel></rss>",
                    cacheControl: "max-age=1",
                    etag: "\"redirect-tag\"")
                : new HttpResponseMessage(HttpStatusCode.NotModified);
        };

        var feedUrl = Uri.EscapeDataString("https://feed.test/redirect.xml");
        using var firstResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        _podcastCacheTimeProvider.Advance(TimeSpan.FromSeconds(2));
        using var secondResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(4, _podcastHttpHandler.RequestedRequests.Count);
        Assert.Null(_podcastHttpHandler.RequestedRequests[0].IfNoneMatch);
        Assert.Null(_podcastHttpHandler.RequestedRequests[1].IfNoneMatch);
        Assert.Null(_podcastHttpHandler.RequestedRequests[2].IfNoneMatch);
        Assert.Equal("\"redirect-tag\"", _podcastHttpHandler.RequestedRequests[3].IfNoneMatch);
        Assert.Equal("cdn.test", _podcastHttpHandler.RequestedRequests[3].Uri.Host);
    }

    [Fact]
    public async Task PodcastsFeed_UsesExpiresHeaderWhenNoMaxAgeIsPresent()
    {
        var expiresAt = _podcastCacheTimeProvider.GetUtcNow().AddSeconds(10);
        var requestCount = 0;
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            requestCount++;
            return requestCount == 1
                ? PodcastFeedResponse(
                    "<rss><channel><item><title>Expires</title><enclosure url=\"https://audio.test/expires.mp3\" /></item></channel></rss>",
                    etag: "\"expires-tag\"",
                    expires: expiresAt)
                : new HttpResponseMessage(HttpStatusCode.NotModified);
        };

        var feedUrl = Uri.EscapeDataString("https://feed.test/expires.xml");
        using var firstResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        _podcastCacheTimeProvider.Advance(TimeSpan.FromSeconds(11));
        using var secondResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, _podcastHttpHandler.RequestedUris.Count);
        Assert.Equal("\"expires-tag\"", _podcastHttpHandler.RequestedRequests[1].IfNoneMatch);
    }

    [Fact]
    public async Task PodcastsFeed_RechecksFeedHostBeforeServingCachedContent()
    {
        _podcastHttpHandler.ResponseFactory = _ => PodcastFeedResponse(
            "<rss><channel><item><title>Cached</title><enclosure url=\"https://audio.test/cached.mp3\" /></item></channel></rss>");
        var feedUrl = Uri.EscapeDataString("https://feed.test/rebound.xml");

        using var firstResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        _podcastHostResolver.Set("feed.test", IPAddress.Parse("192.168.1.20"));
        using var secondResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        var secondJson = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, secondResponse.StatusCode);
        Assert.Contains("destination is not allowed", secondJson, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsFeed_NoCacheDirectiveRevalidatesOnEveryRequest()
    {
        var requestCount = 0;
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            requestCount++;
            if (requestCount == 1)
                return PodcastFeedResponse(
                    "<rss><channel><item><title>Revalidated</title><enclosure url=\"https://audio.test/revalidated.mp3\" /></item></channel></rss>",
                    cacheControl: "no-cache",
                    etag: "\"no-cache-tag\"");

            return new HttpResponseMessage(HttpStatusCode.NotModified);
        };

        var feedUrl = Uri.EscapeDataString("https://feed.test/no-cache.xml");
        using var firstResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        using var secondResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        using var thirdResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, thirdResponse.StatusCode);
        Assert.Null(_podcastHttpHandler.RequestedRequests[0].IfNoneMatch);
        Assert.Equal("\"no-cache-tag\"", _podcastHttpHandler.RequestedRequests[1].IfNoneMatch);
        Assert.Equal("\"no-cache-tag\"", _podcastHttpHandler.RequestedRequests[2].IfNoneMatch);
        Assert.Equal(3, _podcastHttpHandler.RequestedUris.Count);
    }

    [Fact]
    public async Task PodcastsFeed_NoStoreResponseEvictsPriorEntryAndIsNotCached()
    {
        var requestCount = 0;
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => PodcastFeedResponse(
                    "<rss><channel><item><title>First</title><enclosure url=\"https://audio.test/first.mp3\" /></item></channel></rss>",
                    cacheControl: "max-age=1",
                    etag: "\"revision-1\""),
                2 => PodcastFeedResponse(
                    "<rss><channel><item><title>NoStore</title><enclosure url=\"https://audio.test/no-store.mp3\" /></item></channel></rss>",
                    cacheControl: "no-store",
                    etag: "\"revision-2\""),
                _ => PodcastFeedResponse(
                    "<rss><channel><item><title>Fresh</title><enclosure url=\"https://audio.test/fresh.mp3\" /></item></channel></rss>"),
            };
        };

        var feedUrl = Uri.EscapeDataString("https://feed.test/no-store.xml");
        using var firstResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        _podcastCacheTimeProvider.Advance(TimeSpan.FromSeconds(2));
        using var secondResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        using var thirdResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, thirdResponse.StatusCode);
        Assert.Equal("\"revision-1\"", _podcastHttpHandler.RequestedRequests[1].IfNoneMatch);
        Assert.Null(_podcastHttpHandler.RequestedRequests[2].IfNoneMatch);
        Assert.Equal(3, _podcastHttpHandler.RequestedUris.Count);
    }

    [Fact]
    public async Task PodcastsFeed_DoesNotServeStaleCacheWhenUpstreamRefreshFails()
    {
        var requestCount = 0;
        _podcastHttpHandler.ResponseFactory = _ =>
        {
            requestCount++;
            return requestCount == 1
                ? PodcastFeedResponse(
                    "<rss><channel><item><title>Stale item</title><enclosure url=\"https://audio.test/stale.mp3\" /></item></channel></rss>",
                    cacheControl: "max-age=1",
                    etag: "\"stale-tag\"")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        var feedUrl = Uri.EscapeDataString("https://feed.test/failing-refresh.xml");
        using var firstResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        _podcastCacheTimeProvider.Advance(TimeSpan.FromSeconds(2));
        using var failedResponse = await _client.GetAsync($"/api/podcasts/feed?url={feedUrl}");
        var failedJson = await failedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, failedResponse.StatusCode);
        Assert.DoesNotContain("Stale item", failedJson, StringComparison.Ordinal);
        Assert.Equal(2, _podcastHttpHandler.RequestedUris.Count);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsFeedWithOnlyPrivateResolvedAudioHosts()
    {
        _podcastHostResolver.Set("private-audio.test", IPAddress.Parse("10.10.10.10"));
        _podcastHttpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<rss><channel><item><title>Unsafe</title><enclosure url=\"https://private-audio.test/episode.mp3\" /></item></channel></rss>"),
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/feed.xml"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("private-audio.test", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PodcastsFeed_RejectsFeedWithOnlyPrivateLiteralAudioUrls()
    {
        _podcastHttpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<rss><channel><item><title>Unsafe</title><enclosure url=\"http://127.0.0.1/episode.mp3\" /></item></channel></rss>"),
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/feed.xml"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PodcastsFeed_OmitsUnsafeEpisodeButKeepsSafeEpisodes()
    {
        _podcastHostResolver.Set("private-audio.test", IPAddress.Parse("10.10.10.10"));
        _podcastHttpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<rss><channel>" +
                "<item><title>Safe</title><enclosure url=\"https://audio.test/safe.mp3\" /></item>" +
                "<item><title>Unsafe</title><enclosure url=\"https://private-audio.test/unsafe.mp3\" /></item>" +
                "</channel></rss>"),
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/feed.xml"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        var episodes = document.RootElement.GetProperty("episodes");
        Assert.Equal("Safe", episodes[0].GetProperty("title").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task PodcastsFeed_RejectsTooManyDistinctAudioHosts()
    {
        var items = string.Join(
            string.Empty,
            Enumerable.Range(0, 65).Select(index =>
                $"<item><title>Episode {index}</title><enclosure url=\"https://audio{index}.test/{index}.mp3\" /></item>"));
        _podcastHttpHandler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<rss><channel>{items}</channel></rss>"),
        };

        var response = await _client.GetAsync(
            "/api/podcasts/feed?url=" + Uri.EscapeDataString("https://feed.test/feed.xml"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("too many audio hosts", json, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_podcastHttpHandler.RequestedUris);
    }

    [Fact]
    public async Task PodcastsPlay_RejectsHostnameResolvingToPrivateAddress()
    {
        _podcastHostResolver.Set("private.test", IPAddress.Parse("172.16.0.10"));
        using var content = new StringContent(string.Empty);

        var response = await _client.PostAsync(
            "/api/podcasts/play?url=" + Uri.EscapeDataString("https://private.test/episode.mp3"),
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_radioPlayRequests);
    }

    [Fact]
    public async Task PodcastsPlay_AllowsPublicHostname()
    {
        using var content = new StringContent(string.Empty);

        var response = await _client.PostAsync(
            "/api/podcasts/play?url=" + Uri.EscapeDataString("https://feed.test/episode.mp3"),
            content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://feed.test/episode.mp3", Assert.Single(_radioPlayRequests).Url);
    }

    private static HttpResponseMessage PodcastFeedResponse(
        string xml,
        string? cacheControl = null,
        string? etag = null,
        DateTimeOffset? lastModified = null,
        DateTimeOffset? responseDate = null,
        TimeSpan? age = null,
        DateTimeOffset? expires = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml),
        };
        if (cacheControl is not null)
            response.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
        if (etag is not null)
            response.Headers.TryAddWithoutValidation("ETag", etag);
        if (lastModified.HasValue)
            response.Content.Headers.LastModified = lastModified;
        if (responseDate.HasValue)
            response.Headers.Date = responseDate;
        if (age.HasValue)
            response.Headers.Age = age;
        if (expires.HasValue)
            response.Content.Headers.Expires = expires;

        return response;
    }

    private static async Task WaitForPodcastTopPoolCacheAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(File.Exists(path), $"Expected podcast top-pool cache at '{path}'.");
    }

    private static async Task<JsonDocument> WaitForPodcastRefreshStateAsync(
        HttpClient client,
        string requestUri,
        bool refreshing,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync(requestUri).WaitAsync(timeout);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (document.RootElement.GetProperty("refreshing").GetBoolean() == refreshing)
                return document;

            document.Dispose();
            await Task.Delay(10);
        }

        throw new TimeoutException($"Podcast refresh state did not become refreshing={refreshing}.");
    }

    private async Task WaitForPodcastCatalogRequestsAsync(int expectedCount, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (_podcastCatalogHttpHandler.RequestedCount < expectedCount && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(
            _podcastCatalogHttpHandler.RequestedCount >= expectedCount,
            $"Expected at least {expectedCount} catalog requests, received {_podcastCatalogHttpHandler.RequestedCount}.");
    }

    private static string GetPodcastChartCountry(HttpRequestMessage request)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
            throw new InvalidOperationException($"Unexpected podcast chart URL: {request.RequestUri}");

        return segments[2].ToUpperInvariant();
    }

    private static HttpResponseMessage BuildPodcastTopChartResponse(string country, string titlePrefix)
    {
        var safePrefix = titlePrefix.Replace(' ', '-').ToLowerInvariant();
        var results = Enumerable.Range(1, 25)
            .Select(index => new
            {
                name = $"{titlePrefix} {country} Top {index}",
                artistName = $"{titlePrefix} {country} Author",
                url = $"https://feeds.test/{safePrefix}-{country.ToLowerInvariant()}-{index}.xml",
                artworkUrl100 = string.Empty,
                genres = new[] { new { name = "News" } },
            })
            .ToArray();
        var json = JsonSerializer.Serialize(new { feed = new { results } });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };
    }

    internal static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static WebServerCallbacks BuildStubCallbacks(
        Action<string, string>? radioPlay = null,
        Action<string, double>? videoPlayAtPosition = null,
        Func<PlaybackStatus>? getStatus = null) => new()
    {
        Play                  = _ => { },
        PlayAtPosition        = videoPlayAtPosition,
        Stop                  = () => { },
        Pause                 = () => { },
        GetStatus             = getStatus ?? (() => new PlaybackStatus()),
        Seek                  = _ => { },
        Skip                  = _ => { },
        SetVolume             = _ => { },
        ToggleMute            = () => { },
        SetBrightness         = _ => { },
        SetSharpness          = _ => { },
        SetSaturation         = _ => { },
        SetZoom               = _ => { },
        SetAudioBoost         = _ => { },
        SetPlaybackSpeed      = _ => { },
        ToggleSubtitles       = () => { },
        SetAudioTrack         = _ => { },
        SetSubtitleTrack      = _ => { },
        SeekToChapter         = _ => { },
        SetEqPreset           = _ => { },
        SetReverbPreset       = _ => { },
        SetMusicReverbPreset  = _ => { },
        SetMusicReverbStrength = _ => { },
        SetMusicReverbDryLevel = _ => { },
        SetMusicReverbPreDelayMs = _ => { },
        SetMusicReverbBypass = _ => { },
        SetMusicVocalRemovalMode = _ => { },
        PlayAdjacent          = _ => { },
        Enqueue               = _ => { },
        RemoveFromQueue       = _ => { },
        MoveQueueItem         = (_, _) => { },
        ClearQueue            = () => { },
        ClearPlaybackHistory  = _ => { },
        MarkWatchedHistory    = (_, _) => { },
        GetDisplayDiagnostics = () => new DisplayDiagnostics(),
        FixAudio              = () => { },
        PlayMusic             = (_, _) => { },
        PauseMusic            = () => { },
        StopMusic             = () => { },
        GetMusicStatus        = () => new MusicStatus(false, false, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, string.Empty, -1, 0, 1.0, 1.0, 0.0, false, false),
        SeekMusic             = _ => { },
        SetMusicVolume        = _ => { },
        SetMusicBoost         = _ => { },
        SetMusicNextTrack     = _ => { },
        RadioSearch           = (_, _, _, _, _) => Task.FromResult(new List<RadioStation>()),
        RadioTopStations      = (_, _) => Task.FromResult(new List<RadioStation>()),
        RadioGetTags          = _ => Task.FromResult(new List<string>()),
        RadioGetCountries     = () => Task.FromResult(new List<(string Code, string Name)>()),
        RadioPlay             = radioPlay ?? ((_, _) => { }),
        RadioStop             = () => { },
        RadioSetVolume        = _ => { },
        RadioSetBoost         = _ => { },
        RadioGetStatus        = () => new RadioStatus(false, string.Empty, string.Empty, string.Empty, 1, 1, 0, false, string.Empty, -1, 0),
        RadioGetFavorites     = () => new List<RadioStation>(),
        RadioToggleFavorite   = _ => { },
        RadioIsFavorite       = _ => false,
        RadioIsFavoriteByUrl  = _ => false,
        RadioIsFavoriteByName = (_, _) => false,
        RadioNotifyAlive      = () => { },
        RadioResolveUrl       = (_, _) => Task.FromResult(string.Empty),
        RadioSetReverbPreset  = _ => { },
        SetMusicEqPreset      = _ => { },
        RadioSetEqPreset      = _ => { },
        SaveExpertMode        = _ => { },
        SaveDebugMode         = _ => { },
        SaveSettings          = _ => { },
        RestartApp            = () => { },
                RestartServer         = () => { },
    };

    private sealed class TestPodcastHttpMessageHandler : HttpMessageHandler
    {
        private readonly object _requestGate = new();

        internal List<Uri> RequestedUris { get; } = [];
        internal List<(Uri Uri, string? IfNoneMatch, DateTimeOffset? IfModifiedSince)> RequestedRequests { get; } = [];

        internal Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss><channel /></rss>"),
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_requestGate)
            {
                RequestedUris.Add(request.RequestUri!);
                RequestedRequests.Add((
                    request.RequestUri!,
                    request.Headers.IfNoneMatch.FirstOrDefault()?.ToString(),
                    request.Headers.IfModifiedSince));
            }

            return Task.FromResult(ResponseFactory(request));
        }
    }

    private sealed class TestPodcastCatalogHttpMessageHandler : HttpMessageHandler
    {
        private readonly object _requestGate = new();

        internal List<Uri> RequestedUris { get; } = [];
        internal int RequestedCount
        {
            get
            {
                lock (_requestGate)
                    return RequestedUris.Count;
            }
        }

        internal Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"resultCount\":0,\"results\":[]}"),
            };
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncResponseFactory { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_requestGate)
                RequestedUris.Add(request.RequestUri!);

            var asyncFactory = AsyncResponseFactory;
            if (asyncFactory is not null)
                return await asyncFactory(request, cancellationToken).ConfigureAwait(false);

            return ResponseFactory(request);
        }
    }

    private sealed class TestPodcastHostResolver : IPodcastHostResolver
    {
        private readonly Dictionary<string, IPAddress[]> _addresses = new(StringComparer.OrdinalIgnoreCase);

        internal void Set(string host, params IPAddress[] addresses)
            => _addresses[host] = addresses;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var addresses = _addresses.TryGetValue(host, out var configured)
                ? configured
                : [IPAddress.Parse("93.184.216.34")];
            return Task.FromResult(addresses);
        }
    }

    private sealed class TestPodcastCacheTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        internal void Advance(TimeSpan amount)
        {
            lock (_gate)
                _utcNow += amount;
        }
    }
}
