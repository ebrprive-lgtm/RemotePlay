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

    public WebServerApiTests()
    {
        var port = FindFreePort();
        _server = new WebServer(new AppConfig { Port = port }, BuildStubCallbacks());
        _server.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
    }

    public void Dispose()
    {
        _server.Stop();
        _client.Dispose();
    }

    // ── /api/status ──────────────────────────────────────────────────────────

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

    // ── /api/library-status ─────────────────────────────────────────────────

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

    // ── /api/version ────────────────────────────────────────────────────────

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

    // ── /health ─────────────────────────────────────────────────────────────

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

    // ── 404 for unknown paths ────────────────────────────────────────────────

    [Fact]
    public async Task UnknownPath_Returns404()
    {
        var response = await _client.GetAsync("/api/this-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Rate limiting ────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimiting_Returns429_WhenLimitExceeded()
    {
        var port = FindFreePort();
        // Allow 2 requests per 60-second window so the third is rate-limited.
        var config = new AppConfig { Port = port, MaxRequestsPerIpPerWindow = 2, RateLimitWindowSeconds = 60 };
        var tightServer = new WebServer(config, BuildStubCallbacks());
        tightServer.Start();
        using var tightClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };

        await tightClient.GetAsync("/api/version");
        await tightClient.GetAsync("/api/version");
        var limited = await tightClient.GetAsync("/api/version");

        tightServer.Stop();

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task RateLimiting_Disabled_WhenLimitIsZero()
    {
        var port = FindFreePort();
        var config = new AppConfig { Port = port, MaxRequestsPerIpPerWindow = 0 };
        var openServer = new WebServer(config, BuildStubCallbacks());
        openServer.Start();
        using var openClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };

        for (var i = 0; i < 10; i++)
        {
            var response = await openClient.GetAsync("/api/version");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        openServer.Stop();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static WebServerCallbacks BuildStubCallbacks() => new()
    {
        Play                  = _ => { },
        Stop                  = () => { },
        Pause                 = () => { },
        GetStatus             = () => new PlaybackStatus(),
        Seek                  = _ => { },
        Skip                  = _ => { },
        SetVolume             = _ => { },
        ToggleMute            = () => { },
        SetBrightness         = _ => { },
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
        GetMusicStatus        = () => new MusicStatus(false, false, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, string.Empty, -1, 0),
        SeekMusic             = _ => { },
        SetMusicVolume        = _ => { },
        SetMusicBoost         = _ => { },
        SetMusicNextTrack     = _ => { },
        RadioSearch           = (_, _, _, _, _) => Task.FromResult(new List<RadioStation>()),
        RadioTopStations      = (_, _) => Task.FromResult(new List<RadioStation>()),
        RadioGetTags          = _ => Task.FromResult(new List<string>()),
        RadioGetCountries     = () => Task.FromResult(new List<(string Code, string Name)>()),
        RadioPlay             = (_, _) => { },
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
    };
}
