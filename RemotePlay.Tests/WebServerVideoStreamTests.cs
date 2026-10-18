using System.Net;
using System.Net.Http;
using System.Linq;
using System.Globalization;
using System.Text.Json;
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
    public async Task Browse_ExposesMasterLibraryFoldersAndVideosInsideDecades()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var movieName = $"1942 - Movie {suffix}";
        var movieDecade = Path.Combine(_host.PrimaryRoot, "@Master.Movies", "1940s");
        var seriesName = $"1942 - Series {suffix}";
        var seriesFolder = Path.Combine(_host.PrimaryRoot, "@Master.Series", "1940s", seriesName);
        var seasonFolder = Path.Combine(seriesFolder, "Season 1");

        Directory.CreateDirectory(movieDecade);
        Directory.CreateDirectory(seasonFolder);
        Directory.CreateDirectory(Path.Combine(_host.PrimaryRoot, "@Private"));
        _host.WriteVideo(movieDecade, movieName + ".mp4", [1, 2, 3]);
        var episodeName = seriesName + " S01E01";
        _host.WriteVideo(seasonFolder, episodeName + ".mkv", [4, 5, 6]);

        using var rootBrowse = await BrowseAsync(null);
        var rootFolders = GetNames(rootBrowse, "folders");
        Assert.Contains("@Master.Movies", rootFolders);
        Assert.Contains("@Master.Series", rootFolders);
        Assert.DoesNotContain("@Private", rootFolders);

        using var moviesBrowse = await BrowseAsync(Path.Combine(_host.PrimaryRoot, "@Master.Movies"));
        Assert.Contains("1940s", GetNames(moviesBrowse, "folders"));
        using var movieDecadeBrowse = await BrowseAsync(movieDecade);
        Assert.Contains(movieName, GetNames(movieDecadeBrowse, "files"));

        using var seriesBrowse = await BrowseAsync(Path.Combine(_host.PrimaryRoot, "@Master.Series"));
        Assert.Contains("1940s", GetNames(seriesBrowse, "folders"));
        using var seriesDecadeBrowse = await BrowseAsync(Path.GetDirectoryName(seriesFolder)!);
        Assert.Contains(seriesName, GetNames(seriesDecadeBrowse, "folders"));
        using var seriesFolderBrowse = await BrowseAsync(seriesFolder);
        Assert.Contains("Season 1", GetNames(seriesFolderBrowse, "folders"));
        using var seasonBrowse = await BrowseAsync(seasonFolder);
        Assert.Contains(episodeName, GetNames(seasonBrowse, "files"));
    }

    private async Task<JsonDocument> BrowseAsync(string? path)
    {
        var url = path is null
            ? "/api/browse"
            : $"/api/browse?dir={Uri.EscapeDataString(WebPathHelpers.EncodePath(path))}";
        using var response = await _host.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string[] GetNames(JsonDocument document, string propertyName) =>
        document.RootElement.GetProperty(propertyName)
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!)
            .ToArray();

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

    [Theory]
    [InlineData(".mp4", "video/mp4")]
    [InlineData(".m4v", "video/mp4")]
    [InlineData(".mov", "video/quicktime")]
    [InlineData(".mkv", "video/x-matroska")]
    [InlineData(".avi", "video/x-msvideo")]
    [InlineData(".wmv", "video/x-ms-wmv")]
    [InlineData(".ts", "video/mp2t")]
    [InlineData(".flv", "video/x-flv")]
    public async Task GetVideoStream_UsesContainerMimeTypeAndPreservesSourceBytes(
        string extension,
        string expectedMimeType)
    {
        var sourceBytes = new byte[] { 0x00, 0x52, 0x50, 0x01, 0xFF };
        var filePath = _host.WriteVideo(
            _host.PrimaryRoot,
            $"mime-{Guid.NewGuid():N}{extension}",
            sourceBytes);

        using var response = await _host.Client.GetAsync(_host.GetStreamUrl(filePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMimeType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(sourceBytes, await response.Content.ReadAsByteArrayAsync());
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
    public async Task AudioCompatibleStream_AuthorizesSourceBeforeTranscoding()
    {
        var filePath = _host.WriteVideo(_host.OutsideRoot, "outside-audio-compat.mkv", [1, 2, 3]);

        using var response = await _host.Client.GetAsync(
            _host.GetStreamUrl(filePath, audioCompatible: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AutoAudioCompatibleStream_AuthorizesSourceBeforeProbingCodecs()
    {
        var filePath = _host.WriteVideo(_host.OutsideRoot, "outside-auto-audio-compat.mkv", [1, 2, 3]);
        var url = _host.GetStreamUrl(filePath) + "&audioCompat=auto&audioCodecs=aac-lc";

        using var response = await _host.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CodecInfoRequest_AuthorizesSourceBeforeStartingProbe()
    {
        var filePath = _host.WriteVideo(_host.OutsideRoot, "outside-codec-info.mkv", [1, 2, 3]);

        using var response = await _host.Client.GetAsync(_host.GetCodecInfoUrl(filePath));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TranscodeStatus_ReturnsProgressForAuthorizedVideo()
    {
        var filePath = _host.WriteVideo(_host.PrimaryRoot, "transcode-status.mkv", [1, 2, 3]);

        using var response = await _host.Client.GetAsync(
            _host.GetTranscodeStatusUrl(filePath, audioCodecs: "aac-lc"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var progress = document.RootElement;
        Assert.False(progress.GetProperty("isTranscoding").GetBoolean());
        Assert.False(progress.GetProperty("isComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("progressPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("estimatedRemainingSeconds").ValueKind);
    }

    [Fact]
    public async Task TranscodeStatus_RejectsWrongMethodAndUnauthorizedPaths()
    {
        var outsidePath = _host.WriteVideo(_host.OutsideRoot, "outside-transcode-status.mkv", [1, 2, 3]);
        var allowedPath = _host.WriteVideo(_host.PrimaryRoot, "method-transcode-status.mkv", [1, 2, 3]);

        using var wrongMethod = await _host.Client.PostAsync(
            _host.GetTranscodeStatusUrl(allowedPath),
            content: null);
        using var outside = await _host.Client.GetAsync(_host.GetTranscodeStatusUrl(outsidePath));
        using var missingPath = await _host.Client.GetAsync("/api/video/transcode-status");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("GET", Assert.Single(wrongMethod.Content.Headers.GetValues("Allow")));
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingPath.StatusCode);
    }

    [Fact]
    public async Task TranscodeCancel_AuthorizesSourceAndOnlyAcceptsPost()
    {
        var allowedPath = _host.WriteVideo(_host.PrimaryRoot, "cancel-transcode.mkv", [1, 2, 3]);
        var outsidePath = _host.WriteVideo(_host.OutsideRoot, "outside-cancel-transcode.mkv", [1, 2, 3]);

        using var wrongMethod = await _host.Client.GetAsync(_host.GetTranscodeCancelUrl(allowedPath));
        using var idle = await _host.Client.PostAsync(_host.GetTranscodeCancelUrl(allowedPath), content: null);
        using var outside = await _host.Client.PostAsync(_host.GetTranscodeCancelUrl(outsidePath), content: null);
        using var missingPath = await _host.Client.PostAsync("/api/video/transcode-cancel", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("POST", Assert.Single(wrongMethod.Content.Headers.GetValues("Allow")));
        Assert.Equal(HttpStatusCode.OK, idle.StatusCode);
        using var document = JsonDocument.Parse(await idle.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingPath.StatusCode);
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
    public async Task GetThroughMovieDirectiveLink_StreamsMovieResolvedByFilename()
    {
        var masterMovies = Path.Combine(_host.PrimaryRoot, "@Master.Movies");
        Directory.CreateDirectory(masterMovies);
        var movieName = $"directive-movie-{Guid.NewGuid():N}";
        var bytes = new byte[] { 7, 8, 9 };
        var targetPath = _host.WriteVideo(masterMovies, movieName + ".mkv", bytes);
        var linkPath = Path.Combine(_host.PrimaryRoot, movieName + ".rplink");
        File.WriteAllText(linkPath, "@Movie");

        using var response = await _host.Client.GetAsync(_host.GetStreamUrl(targetPath, linkPath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetThroughEmptyLink_StreamsMatchingMovieFromMasterMovies()
    {
        var masterMovies = Path.Combine(_host.PrimaryRoot, "@Master.Movies");
        var decadeMovies = Path.Combine(masterMovies, "2020s");
        Directory.CreateDirectory(decadeMovies);
        var movieName = $"2024 - empty-link-movie-{Guid.NewGuid():N}";
        var bytes = new byte[] { 7, 8, 9 };
        var targetPath = _host.WriteVideo(decadeMovies, movieName + ".flv", bytes);
        var linkPath = Path.Combine(_host.PrimaryRoot, movieName + ".rplink");
        File.WriteAllText(linkPath, string.Empty);

        using var response = await _host.Client.GetAsync(_host.GetStreamUrl(targetPath, linkPath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
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

    [Fact]
    public async Task LocalVideoProgress_SavesPositionAndAddsFileToRecent()
    {
        var filePath = _host.WriteVideo(_host.AdditionalRoot, "local-progress.mp4", [1, 2, 3]);

        using var response = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(filePath, 42.5, 1800),
            content: null);
        using var recentResponse = await _host.Client.GetAsync("/api/recent");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recentResponse.StatusCode);
        var entry = Assert.Single(_host.History.GetRecent(100), item => item.FilePath == filePath);
        Assert.Equal(42.5, entry.PositionSeconds);
        Assert.Equal(1800, entry.DurationSeconds);
        Assert.DoesNotContain(filePath, _host.History.GetWatchedSet());

        using var recentDocument = JsonDocument.Parse(await recentResponse.Content.ReadAsStringAsync());
        var encodedPath = WebPathHelpers.EncodePath(filePath);
        Assert.Contains(
            recentDocument.RootElement.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("path").GetString() == encodedPath);
    }

    [Fact]
    public async Task LocalVideoProgress_RecordsPlaybackBeforeDurationIsKnown()
    {
        var filePath = _host.WriteVideo(_host.PrimaryRoot, "duration-pending.mp4", [1, 2, 3]);

        using var response = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(filePath, 0, 0),
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(_host.History.GetRecent(100), item => item.FilePath == filePath);
        Assert.Equal(0, entry.PositionSeconds);
        Assert.Equal(0, entry.DurationSeconds);
        Assert.DoesNotContain(filePath, _host.History.GetWatchedSet());
    }

    [Fact]
    public async Task LocalVideoProgress_MarksWatchedOnCompletion()
    {
        var filePath = _host.WriteVideo(_host.PrimaryRoot, "completed.mp4", [1, 2, 3]);

        using var response = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(filePath, 120, 120, completed: true),
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(filePath, _host.History.GetWatchedSet());
    }

    [Fact]
    public async Task LocalVideoProgress_RejectsWrongMethodInvalidBoundsAndUnauthorizedPaths()
    {
        var filePath = _host.WriteVideo(_host.PrimaryRoot, "invalid-progress.mp4", [1, 2, 3]);
        var outsidePath = _host.WriteVideo(_host.OutsideRoot, "outside-progress.mp4", [1, 2, 3]);
        var unsupportedPath = _host.WriteVideo(_host.PrimaryRoot, "unsupported.txt", [1, 2, 3]);
        var missingPath = Path.Combine(_host.PrimaryRoot, "missing-progress.mp4");

        using var wrongMethod = await _host.Client.GetAsync(_host.GetLocalProgressUrl(filePath, 15, 60));
        using var missing = await _host.Client.PostAsync("/api/video/local-progress", content: null);
        using var invalidToken = await _host.Client.PostAsync(
            "/api/video/local-progress?path=!!!!&pos=15&duration=60",
            content: null);
        using var negative = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(filePath, "-1", "60"),
            content: null);
        using var nonFinite = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(filePath, "NaN", "60"),
            content: null);
        using var missingDuration = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(filePath, 15, 0),
            content: null);
        using var beyondDuration = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(filePath, 61, 60),
            content: null);
        using var outside = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(outsidePath, 15, 60),
            content: null);
        using var unsupported = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(unsupportedPath, 15, 60),
            content: null);
        using var notFound = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(missingPath, 15, 60),
            content: null);
        using var prematureCompletion = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(filePath, 0, 60, completed: true),
            content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("POST", Assert.Single(wrongMethod.Content.Headers.GetValues("Allow")));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nonFinite.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingDuration.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, beyondDuration.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, prematureCompletion.StatusCode);
        Assert.DoesNotContain(filePath, _host.History.GetProgressMap().Keys);
    }

    [Fact]
    public async Task HistoryClearAndWatchedSet_AuthorizeLinkedVideoContext()
    {
        var targetPath = _host.WriteVideo(_host.OutsideRoot, "linked-history.mp4", [1, 2, 3]);
        var linkPath = Path.Combine(_host.PrimaryRoot, "linked-history.rplink");
        RplinkHelper.Create(linkPath, targetPath);

        using var progress = await _host.Client.PostAsync(
            _host.GetLocalProgressUrl(targetPath, 45, 600, linkPath),
            content: null);
        var watchedUrl = "/api/history/watched/set?path="
            + Uri.EscapeDataString(WebPathHelpers.EncodePath(targetPath))
            + "&linkPath="
            + Uri.EscapeDataString(WebPathHelpers.EncodePath(linkPath))
            + "&watched=true";
        using var watched = await _host.Client.GetAsync(watchedUrl);
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        Assert.Equal(HttpStatusCode.OK, watched.StatusCode);
        Assert.Contains(targetPath, _host.History.GetWatchedSet());

        var clearUrl = "/api/history/clear?path="
            + Uri.EscapeDataString(WebPathHelpers.EncodePath(targetPath))
            + "&linkPath="
            + Uri.EscapeDataString(WebPathHelpers.EncodePath(linkPath));
        using var clear = await _host.Client.GetAsync(clearUrl);

        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.DoesNotContain(targetPath, _host.History.GetRecent(100).Select(item => item.FilePath));
    }
}

public sealed class WebServerMusicStreamTests : IClassFixture<VideoStreamTestHost>
{
    private readonly VideoStreamTestHost _host;

    public WebServerMusicStreamTests(VideoStreamTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task GetWithoutRange_ReturnsCompleteAudioWithoutContentRange()
    {
        var bytes = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        var filePath = _host.WriteVideo(
            _host.PrimaryRoot,
            $"music-full-{Guid.NewGuid():N}.mp3",
            bytes);

        using var response = await _host.Client.GetAsync(GetMusicStreamUrl(filePath));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("bytes", Assert.Single(response.Headers.GetValues("Accept-Ranges")));
        Assert.False(response.Content.Headers.Contains("Content-Range"));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("bytes=2-5", 2, 5)]
    [InlineData("bytes=4-", 4, 9)]
    [InlineData("bytes=-3", 7, 9)]
    public async Task GetWithRange_ReturnsExactPartialAudio(
        string rangeHeader,
        int expectedStart,
        int expectedEnd)
    {
        var bytes = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        var filePath = _host.WriteVideo(
            _host.PrimaryRoot,
            $"music-range-{Guid.NewGuid():N}.mp3",
            bytes);
        using var request = new HttpRequestMessage(HttpMethod.Get, GetMusicStreamUrl(filePath));
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
        var filePath = _host.WriteVideo(
            _host.PrimaryRoot,
            $"music-unsatisfiable-{Guid.NewGuid():N}.mp3",
            [0, 1, 2]);
        using var request = new HttpRequestMessage(HttpMethod.Get, GetMusicStreamUrl(filePath));
        request.Headers.TryAddWithoutValidation("Range", "bytes=3-");

        using var response = await _host.Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)416, response.StatusCode);
        Assert.Equal("bytes */3", response.Content.Headers.ContentRange?.ToString());
    }

    private static string GetMusicStreamUrl(string filePath) =>
        "/api/music/stream?path=" + Uri.EscapeDataString(WebPathHelpers.EncodePath(filePath));
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
        History = new PlaybackHistory(Path.Combine(_temporaryDirectory, "history.json"));
        Port = WebServerApiTests.FindFreePort();
        _server = new WebServer(
            new AppConfig
            {
                Port = Port,
                AdditionalMoviesPaths = [PrimaryRoot, AdditionalRoot],
                MaxRequestsPerIpPerWindow = 0,
            },
            WebServerApiTests.BuildStubCallbacks(),
            playbackHistory: History,
            podcastTopPoolCacheFilePath: cacheFilePath);
        _server.Start();
        Client = new HttpClient { BaseAddress = new Uri($"http://localhost:{Port}") };
    }

    public string PrimaryRoot { get; }

    public string AdditionalRoot { get; }

    public string OutsideRoot { get; }

    internal PlaybackHistory History { get; }

    public int Port { get; }

    public HttpClient Client { get; }

    public string WriteVideo(string root, string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string GetStreamUrl(string filePath, string? linkPath = null, bool audioCompatible = false)
    {
        var url = $"/api/video/stream?path={Uri.EscapeDataString(WebPathHelpers.EncodePath(filePath))}";
        if (linkPath is not null)
            url += $"&linkPath={Uri.EscapeDataString(WebPathHelpers.EncodePath(linkPath))}";
        if (audioCompatible)
            url += "&audioCompat=aac";
        return url;
    }

    public string GetCodecInfoUrl(string filePath, string? linkPath = null)
    {
        var url = $"/api/video/codec-info?path={Uri.EscapeDataString(WebPathHelpers.EncodePath(filePath))}";
        if (linkPath is not null)
            url += $"&linkPath={Uri.EscapeDataString(WebPathHelpers.EncodePath(linkPath))}";
        return url;
    }

    public string GetTranscodeStatusUrl(
        string filePath,
        string? linkPath = null,
        string? audioCodecs = null)
    {
        var url = $"/api/video/transcode-status?path={Uri.EscapeDataString(WebPathHelpers.EncodePath(filePath))}";
        if (linkPath is not null)
            url += $"&linkPath={Uri.EscapeDataString(WebPathHelpers.EncodePath(linkPath))}";
        if (audioCodecs is not null)
            url += $"&audioCodecs={Uri.EscapeDataString(audioCodecs)}";
        return url;
    }

    public string GetTranscodeCancelUrl(string filePath, string? linkPath = null)
    {
        var url = $"/api/video/transcode-cancel?path={Uri.EscapeDataString(WebPathHelpers.EncodePath(filePath))}";
        if (linkPath is not null)
            url += $"&linkPath={Uri.EscapeDataString(WebPathHelpers.EncodePath(linkPath))}";
        return url;
    }

    public string GetLocalProgressUrl(
        string filePath,
        double position,
        double duration,
        string? linkPath = null,
        bool completed = false) =>
        GetLocalProgressUrl(
            filePath,
            position.ToString("R", CultureInfo.InvariantCulture),
            duration.ToString("R", CultureInfo.InvariantCulture),
            linkPath,
            completed);

    public string GetLocalProgressUrl(
        string filePath,
        string position,
        string duration,
        string? linkPath = null,
        bool completed = false)
    {
        var url = $"/api/video/local-progress?path={Uri.EscapeDataString(WebPathHelpers.EncodePath(filePath))}"
            + $"&pos={Uri.EscapeDataString(position)}&duration={Uri.EscapeDataString(duration)}";
        if (linkPath is not null)
            url += $"&linkPath={Uri.EscapeDataString(WebPathHelpers.EncodePath(linkPath))}";
        if (completed)
            url += "&completed=true";
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
