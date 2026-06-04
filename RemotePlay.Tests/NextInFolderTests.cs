using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RemotePlay.Models;
using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

/// <summary>
/// Integration tests for <c>/api/next-in-folder</c> — the endpoint that powers
/// the browser "Up Next" card by returning the adjacent video in the same directory.
/// </summary>
public sealed class NextInFolderTests : IDisposable
{
    private readonly WebServer _server;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public NextInFolderTests()
    {
        var port = FindFreePort();
        _server = new WebServer(new AppConfig { Port = port }, BuildStubCallbacks());
        _server.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };

        // Create a unique temp directory for this test class instance
        _tempDir = Path.Combine(Path.GetTempPath(), "RemotePlayTests_NextInFolder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _server.Stop();
        _client.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ── /api/next-in-folder — missing path parameter ─────────────────────────

    [Fact]
    public async Task NextInFolder_WhenPathParamMissing_Returns400()
    {
        var response = await _client.GetAsync("/api/next-in-folder");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NextInFolder_WhenPathParamMissing_ResponseContainsMissingPathError()
    {
        var response = await _client.GetAsync("/api/next-in-folder");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("found", out var found));
        Assert.False(found.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ── /api/next-in-folder — file does not exist ────────────────────────────

    [Fact]
    public async Task NextInFolder_WhenFileDoesNotExist_ReturnsOkWithFoundFalse()
    {
        var fakePath = Path.Combine(_tempDir, "does_not_exist.mp4");
        var encoded = EncodePath(fakePath);

        var response = await _client.GetAsync($"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
    }

    // ── /api/next-in-folder — only one file in directory ────────────────────

    [Fact]
    public async Task NextInFolder_WhenOnlyOneVideoInDirectory_ReturnsFoundFalse()
    {
        var dir = CreateSubDir();
        var only = CreateFile(dir, "only_video.mp4");

        var encoded = EncodePath(only);
        var response = await _client.GetAsync($"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
    }

    // ── /api/next-in-folder — next file exists ───────────────────────────────

    [Fact]
    public async Task NextInFolder_WhenNextExists_ReturnsFoundTrueWithCorrectTitle()
    {
        var dir = CreateSubDir();
        var first  = CreateFile(dir, "Episode 01.mp4");
        var second = CreateFile(dir, "Episode 02.mp4");

        var encoded = EncodePath(first);
        var response = await _client.GetAsync($"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        var title = doc.RootElement.GetProperty("title").GetString();
        Assert.Equal(Path.GetFileNameWithoutExtension(second), title);
    }

    [Fact]
    public async Task NextInFolder_WhenNextExists_ReturnedPathDecodesToNextFile()
    {
        var dir = CreateSubDir();
        var first  = CreateFile(dir, "Movie A.mkv");
        var second = CreateFile(dir, "Movie B.mkv");

        var encoded = EncodePath(first);
        var response = await _client.GetAsync($"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        var returnedEncoded = doc.RootElement.GetProperty("path").GetString()!;
        var decoded = DecodePath(returnedEncoded);
        Assert.Equal(second, decoded, StringComparer.OrdinalIgnoreCase);
    }

    // ── /api/next-in-folder — last file has no next ──────────────────────────

    [Fact]
    public async Task NextInFolder_WhenCurrentIsLastFile_ReturnsFoundFalse()
    {
        var dir = CreateSubDir();
        CreateFile(dir, "Episode 01.mp4");
        var last = CreateFile(dir, "Episode 02.mp4");

        var encoded = EncodePath(last);
        var response = await _client.GetAsync($"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
    }

    // ── /api/next-in-folder — natural sort ordering ──────────────────────────

    [Fact]
    public async Task NextInFolder_UsesNaturalSort_Episode2ComesBeforeEpisode10()
    {
        var dir = CreateSubDir();
        CreateFile(dir, "Show - Episode 1.mp4");
        var ep2  = CreateFile(dir, "Show - Episode 2.mp4");
        CreateFile(dir, "Show - Episode 10.mp4");

        // After natural sort: ep1 < ep2 < ep10.  Next after ep2 should be ep10.
        var encoded = EncodePath(ep2);
        var response = await _client.GetAsync($"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        var title = doc.RootElement.GetProperty("title").GetString();
        Assert.Equal("Show - Episode 10", title);
    }

    // ── /api/next-in-folder — direction=previous ─────────────────────────────

    [Fact]
    public async Task NextInFolder_DirectionPrevious_ReturnsPreviousFile()
    {
        var dir = CreateSubDir();
        var first  = CreateFile(dir, "Alpha.mp4");
        var second = CreateFile(dir, "Beta.mp4");

        var encoded = EncodePath(second);
        var response = await _client.GetAsync(
            $"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}&direction=previous");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        var title = doc.RootElement.GetProperty("title").GetString();
        Assert.Equal(Path.GetFileNameWithoutExtension(first), title);
    }

    [Fact]
    public async Task NextInFolder_DirectionPrevious_WhenFirstFile_ReturnsFoundFalse()
    {
        var dir = CreateSubDir();
        var first = CreateFile(dir, "Alpha.mp4");
        CreateFile(dir, "Beta.mp4");

        var encoded = EncodePath(first);
        var response = await _client.GetAsync(
            $"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}&direction=previous");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("found").GetBoolean());
    }

    // ── /api/next-in-folder — non-video files are ignored ────────────────────

    [Fact]
    public async Task NextInFolder_NonVideoFilesAreIgnored()
    {
        var dir = CreateSubDir();
        var mp4    = CreateFile(dir, "Video A.mp4");
        CreateFile(dir, "subtitle.srt");  // should be excluded
        CreateFile(dir, "readme.txt");    // should be excluded
        var second = CreateFile(dir, "Video B.mp4");

        var encoded = EncodePath(mp4);
        var response = await _client.GetAsync($"/api/next-in-folder?path={Uri.EscapeDataString(encoded)}");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        var title = doc.RootElement.GetProperty("title").GetString();
        Assert.Equal(Path.GetFileNameWithoutExtension(second), title);
    }

    // ── /api/status — chapters field is present ──────────────────────────────

    [Fact]
    public async Task Status_ResponseContainsChaptersField()
    {
        var response = await _client.GetAsync("/api/status");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("chapters", out var chaptersEl));
        Assert.Equal(JsonValueKind.Array, chaptersEl.ValueKind);
    }

    [Fact]
    public async Task Status_ResponseContainsCurrentChapterField()
    {
        var response = await _client.GetAsync("/api/status");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("currentChapter", out _));
    }

    // ── /api/chapter — seek to chapter ───────────────────────────────────────

    [Fact]
    public async Task Chapter_WhenValidId_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/chapter?id=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Chapter_WhenMissingId_Returns400()
    {
        var response = await _client.GetAsync("/api/chapter");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Chapter_WhenIdIsNotANumber_Returns400()
    {
        var response = await _client.GetAsync("/api/chapter?id=notanumber");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string CreateSubDir()
    {
        var dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static string EncodePath(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

    private static string DecodePath(string encoded)
    {
        var normalized = encoded.Replace(' ', '+').Replace('-', '+').Replace('_', '/');
        int pad = normalized.Length % 4;
        if (pad == 2) normalized += "==";
        else if (pad == 3) normalized += "=";
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
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
