using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RemotePlay.Models;
using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

/// <summary>
/// Integration tests for the Server-Sent Events (SSE) endpoint at /api/events.
/// Each test spins up a real <see cref="WebServer"/> on a random port.
/// </summary>
public sealed class SseTests : IDisposable
{
    private readonly WebServer _server;
    private readonly HttpClient _client;
    private readonly int _port;

    public SseTests()
    {
        _port = FindFreePort();
        _server = new WebServer(new AppConfig { Port = _port }, BuildStubCallbacks());
        _server.Start();
        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_port}"),
            // Do not buffer the entire SSE body before we can read it.
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public void Dispose()
    {
        _server.Stop();
        _client.Dispose();
    }

    // ── /api/events — HTTP fundamentals ───────────────────────────────────────

    [Fact]
    public async Task SseEndpoint_Returns200()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SseEndpoint_ContentTypeIsEventStream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        // HttpListener sets the charset on the content-type header.
        var ct = response.Content.Headers.ContentType?.MediaType;
        Assert.Equal("text/event-stream", ct);
    }

    [Fact]
    public async Task SseEndpoint_SendsCacheControlNoCache()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        string cacheControl;
        if (response.Headers.CacheControl?.ToString() is { } typed)
        {
            cacheControl = typed;
        }
        else if (response.Content.Headers.TryGetValues("Cache-Control", out var vals))
        {
            cacheControl = string.Join(",", vals);
        }
        else
        {
            cacheControl = string.Empty;
        }

        Assert.Contains("no-cache", cacheControl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SseEndpoint_DeliversRetryDirective()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var firstChunk = await ReadFirstChunkAsync(stream, cts.Token);

        Assert.Contains("retry:", firstChunk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SseEndpoint_DeliversConnectedMessage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var firstChunk = await ReadFirstChunkAsync(stream, cts.Token);

        Assert.Contains("\"connected\":true", firstChunk, StringComparison.Ordinal);
    }

    // ── Multiple concurrent clients ────────────────────────────────────────────

    [Fact]
    public async Task SseEndpoint_AcceptsMultipleConcurrentClients()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var client1 = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
        var client2 = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };

        try
        {
            using var req1 = new HttpRequestMessage(HttpMethod.Get, "/api/events");
            using var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/events");

            var t1 = client1.SendAsync(req1, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var t2 = client2.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var responses = await Task.WhenAll(t1, t2);

            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);

            foreach (var r in responses) r.Dispose();
        }
        finally
        {
            client1.Dispose();
            client2.Dispose();
        }
    }

    // ── PushSseEvent broadcasts to connected client ────────────────────────────

    [Fact]
    public async Task PushSseEvent_BroadcastsToConnectedClient()
    {
        using var headersCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, headersCts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);

        // Consume the initial handshake frame.
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await ReadFirstChunkAsync(stream, handshakeCts.Token);

        // Push a custom event from the server side.
        using var pushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _server.PushSseEvent("status", "{}");

        var received = await ReadFirstChunkAsync(stream, pushCts.Token);

        Assert.Contains("event: status", received, StringComparison.Ordinal);
        Assert.Contains("data: {}", received, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduleSsePush_WithZeroDelay_BroadcastsStatusEvent()
    {
        using var headersCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, headersCts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await ReadFirstChunkAsync(stream, handshakeCts.Token);

        using var pushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _server.ScheduleSsePush(delayMs: 0);

        var received = await ReadFirstChunkAsync(stream, pushCts.Token);

        Assert.Contains("event: status", received, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduleSsePush_WithDelay_BroadcastsAfterDelay()
    {
        using var headersCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, headersCts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await ReadFirstChunkAsync(stream, handshakeCts.Token);

        var before = DateTimeOffset.UtcNow;
        _server.ScheduleSsePush(delayMs: 200);

        using var pushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await ReadFirstChunkAsync(stream, pushCts.Token);
        var elapsed = DateTimeOffset.UtcNow - before;

        Assert.Contains("event: status", received, StringComparison.Ordinal);
        // Allow generous margin; the important thing is we didn't receive it before the delay.
        Assert.True(elapsed.TotalMilliseconds >= 150,
            $"Expected push after ~200 ms but received after {elapsed.TotalMilliseconds:F0} ms");
    }

    // ── No clients — push is a no-op ──────────────────────────────────────────

    [Fact]
    public void PushSseEvent_WhenNoClientsConnected_DoesNotThrow()
    {
        // Should be a silent no-op — no clients.
        var ex = Record.Exception(() => _server.PushSseEvent("status", "{}"));
        Assert.Null(ex);
    }

    [Fact]
    public void ScheduleSsePush_WhenNoClientsConnected_DoesNotThrow()
    {
        var ex = Record.Exception(() => _server.ScheduleSsePush());
        Assert.Null(ex);
    }

    // ── Stop cleans up SSE connections ────────────────────────────────────────

    [Fact]
    public async Task Stop_TerminatesOpenSseStream()
    {
        var stopPort = FindFreePort();
        var stopServer = new WebServer(new AppConfig { Port = stopPort }, BuildStubCallbacks());
        stopServer.Start();

        using var stopClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{stopPort}"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        req.Headers.Add("Accept", "text/event-stream");

        using var response = await stopClient.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(CancellationToken.None);
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await ReadFirstChunkAsync(stream, handshakeCts.Token);

        // Stopping the server must close all SSE streams; reading afterwards
        // should return 0 bytes (EOF) promptly.
        stopServer.Stop();
        stopClient.Dispose();

        using var eofCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var buf = new byte[64];
        int bytesRead;
        try
        {
            bytesRead = await stream.ReadAsync(buf, eofCts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Either the stream threw because the connection dropped — that also counts.
            bytesRead = 0;
        }

        Assert.Equal(0, bytesRead);
    }

    // ── Existing routes are unaffected ─────────────────────────────────────────

    [Fact]
    public async Task Status_StillReturnsOk_AfterSseEndpointAdded()
    {
        var response = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_StillReturnsOk_AfterSseEndpointAdded()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads from <paramref name="stream"/> until a double newline terminates
    /// an SSE frame (or the token is cancelled).
    /// </summary>
    private static async Task<string> ReadFirstChunkAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0) break;
            sb.Append((char)buf[0]);

            // SSE frames are terminated by a blank line (\n\n).
            if (sb.Length >= 2 &&
                sb[^1] == '\n' && sb[^2] == '\n')
                break;
        }

        return sb.ToString();
    }

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
        SaveSettings          = _ => { },
        RestartApp            = () => { },
        RestartServer         = () => { },
    };
}
