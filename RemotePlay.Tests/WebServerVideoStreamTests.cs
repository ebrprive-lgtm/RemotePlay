using System.Net;
using System.Net.Http;
using System.Linq;
using RemotePlay;
using RemotePlay.Models;
using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

public sealed class WebServerVideoStreamTests : IClassFixture<VideoStreamTestHost>
{
    private readonly VideoStreamTestHost _host;

    public WebServerVideoStreamTests(VideoStreamTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task GetWithoutRange_ReturnsCompleteFileAndStreamingHeaders()
    {
        var bytes = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        var filePath = _host.WriteVideo(_host.PrimaryRoot, "full.mp4", bytes);

        using var response = await _host.Client.GetAsync(_host.GetStreamUrl(filePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("bytes", Assert.Single(response.Headers.GetValues("Accept-Ranges")));
        Assert.False(response.Content.Headers.Contains("Content-Range"));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetEmptyFile_ReturnsEmptyFullResponseAndUnsatisfiableRange()
    {
        var filePath = _host.WriteVideo(_host.PrimaryRoot, "empty.mp4", []);

        using var full = await _host.Client.GetAsync(_host.GetStreamUrl(filePath));
        using var request = new HttpRequestMessage(HttpMethod.Get, _host.GetStreamUrl(filePath));
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-");
        using var ranged = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(0, full.Content.Headers.ContentLength);
        Assert.Empty(await full.Content.ReadAsByteArrayAsync());
        Assert.Equal((HttpStatusCode)416, ranged.StatusCode);
        Assert.Equal("bytes */0", ranged.Content.Headers.ContentRange?.ToString());
    }

    [Theory]
    [InlineData("bytes=2-5", 2, 5)]
    [InlineData("bytes=4-", 4, 9)]
    [InlineData("bytes=-3", 7, 9)]
    [InlineData("bytes=0-99", 0, 9)]
    public async Task GetWithRange_ReturnsExactPartialContent(
        string rangeHeader,
        int expectedStart,
        int expectedEnd)
    {
        var bytes = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        var filePath = _host.WriteVideo(_host.PrimaryRoot, $"range-{Guid.NewGuid():N}.mp4", bytes);
        using var request = new HttpRequestMessage(HttpMethod.Get, _host.GetStreamUrl(filePath));
        request.Headers.TryAddWithoutValidation("Range", rangeHeader);

        using var response = await _host.Client.SendAsync(request);

        var expectedBytes = bytes.Skip(expectedStart).Take(expectedEnd - expectedStart + 1).ToArray();
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(expectedBytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal($"bytes {expectedStart}-{expectedEnd}/10", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(expectedBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetWithUnsatisfiableRange_Returns416AndTotalLength()
    {
        var filePath = _host.WriteVideo(_host.PrimaryRoot, "unsatisfiable.mp4", [0, 1, 2]);
        using var request = new HttpRequestMessage(HttpMethod.Get, _host.GetStreamUrl(filePath));
        request.Headers.TryAddWithoutValidation("Range", "bytes=3-");

        using var response = await _host.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)416, response.StatusCode);
        Assert.Equal("bytes */3", response.Content.Headers.ContentRange?.ToString());
    }

    [Theory]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("items=0-1")]
    [InlineData("bytes=5-4")]
    public async Task GetWithUnsupportedOrMultiRange_ReturnsFullContent(string rangeHeader)
    {
        var bytes = new byte[] { 0, 1, 2, 3 };
        var filePath = _host.WriteVideo(_host.PrimaryRoot, $"ignored-range-{Guid.NewGuid():N}.mp4", bytes);
        using var request = new HttpRequestMessage(HttpMethod.Get, _host.GetStreamUrl(filePath));
        request.Headers.TryAddWithoutValidation("Range", rangeHeader);

        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Content.Headers.Contains("Content-Range"));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetWithoutPathOrWithInvalidToken_ReturnsBadRequest()
    {
        using var missing = await _host.Client.GetAsync("/api/video/stream");
        using var invalid = await _host.Client.GetAsync("/api/video/stream?path=!!!!");

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task GetMissingFileOrUnsupportedExtension_ReturnsExpectedError()
    {
        var missingPath = Path.Combine(_host.PrimaryRoot, "missing.mp4");
        var unsupportedPath = _host.WriteVideo(_host.PrimaryRoot, "not-video.txt", [1, 2, 3]);

        using var missing = await _host.Client.GetAsync(_host.GetStreamUrl(missingPath));
        using var unsupported = await _host.Client.GetAsync(_host.GetStreamUrl(unsupportedPath));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
    }

    [Fact]
    public async Task GetOutsideConfiguredRoots_IsForbidden()
    {
        var filePath = _host.WriteVideo(_host.OutsideRoot, "outside.mp4", [1, 2, 3]);

        using var response = await _host.Client.GetAsync(_host.GetStreamUrl(filePath));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetFromAdditionalConfiguredRoot_IsAllowed()
    {
        var filePath = _host.WriteVideo(_host.AdditionalRoot, "additional.mp4", [4, 5, 6]);

        using var response = await _host.Client.GetAsync(_host.GetStreamUrl(filePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 4, 5, 6 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetThroughFileLink_IsAllowedOnlyForItsTarget()
    {
        var targetPath = _host.WriteVideo(_host.OutsideRoot, "linked.mp4", [7, 8, 9]);
        var otherTargetPath = _host.WriteVideo(_host.OutsideRoot, "other.mp4", [1, 2, 3]);
        var linkPath = Path.Combine(_host.PrimaryRoot, "linked.rplink");
        RplinkHelper.Create(linkPath, targetPath);

        using var linked = await _host.Client.GetAsync(_host.GetStreamUrl(targetPath, linkPath));
        using var mismatched = await _host.Client.GetAsync(_host.GetStreamUrl(otherTargetPath, linkPath));

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(new byte[] { 7, 8, 9 }, await linked.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Forbidden, mismatched.StatusCode);
    }

    [Fact]
    public async Task GetThroughLinkOutsideConfiguredRoots_IsForbidden()
    {
        var targetPath = _host.WriteVideo(_host.OutsideRoot, "external-target.mp4", [1, 2, 3]);
        var linkPath = Path.Combine(_host.OutsideRoot, "external-link.rplink");
        RplinkHelper.Create(linkPath, targetPath);

        using var response = await _host.Client.GetAsync(_host.GetStreamUrl(targetPath, linkPath));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetThroughDirectoryLink_IsAllowedOnlyForFilesWithinTarget()
    {
        var linkedDirectory = Path.Combine(_host.OutsideRoot, "linked-directory");
        Directory.CreateDirectory(linkedDirectory);
        var targetPath = _host.WriteVideo(linkedDirectory, "linked-folder-video.mp4", [7, 8, 9]);
        var otherPath = _host.WriteVideo(_host.OutsideRoot, "not-in-linked-directory.mp4", [1, 2, 3]);
        var linkPath = Path.Combine(_host.PrimaryRoot, "linked-directory.rplink");
        RplinkHelper.Create(linkPath, linkedDirectory);

        using var linked = await _host.Client.GetAsync(_host.GetStreamUrl(targetPath, linkPath));
        using var outsideTarget = await _host.Client.GetAsync(_host.GetStreamUrl(otherPath, linkPath));

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outsideTarget.StatusCode);
    }

    [Fact]
    public async Task PostToStreamRoute_IsMethodNotAllowed()
    {
        using var response = await _host.Client.PostAsync("/api/video/stream", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET", Assert.Single(response.Content.Headers.GetValues("Allow")));
    }
}

public sealed class VideoStreamTestHost : IDisposable
{
    private readonly WebServer _server;
    private readonly string _temporaryDirectory;

    public VideoStreamTestHost()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "RemotePlayVideoStreamTests",
            Guid.NewGuid().ToString("N"));
        PrimaryRoot = Path.Combine(_temporaryDirectory, "primary");
        AdditionalRoot = Path.Combine(_temporaryDirectory, "additional");
        OutsideRoot = Path.Combine(_temporaryDirectory, "outside");
        Directory.CreateDirectory(PrimaryRoot);
        Directory.CreateDirectory(AdditionalRoot);
        Directory.CreateDirectory(OutsideRoot);

        var cacheDirectory = Path.Combine(_temporaryDirectory, "cache");
        var cacheFilePath = Path.Combine(cacheDirectory, "podcast-top-pool.json");
        Port = WebServerApiTests.FindFreePort();
        _server = new WebServer(
            new AppConfig
            {
                Port = Port,
                AdditionalMoviesPaths = [PrimaryRoot, AdditionalRoot],
                MaxRequestsPerIpPerWindow = 0,
            },
            WebServerApiTests.BuildStubCallbacks(),
            podcastTopPoolCacheFilePath: cacheFilePath);
        _server.Start();
        Client = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}") };
    }

    public string PrimaryRoot { get; }

    public string AdditionalRoot { get; }

    public string OutsideRoot { get; }

    public int Port { get; }

    public HttpClient Client { get; }

    public string WriteVideo(string root, string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string GetStreamUrl(string filePath, string? linkPath = null)
    {
        var url = $"/api/video/stream?path={Uri.EscapeDataString(WebPathHelpers.EncodePath(filePath))}";
        if (linkPath is not null)
            url += $"&linkPath={Uri.EscapeDataString(WebPathHelpers.EncodePath(linkPath))}";
        return url;
    }

    public void Dispose()
    {
        _server.Stop();
        Client.Dispose();
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
