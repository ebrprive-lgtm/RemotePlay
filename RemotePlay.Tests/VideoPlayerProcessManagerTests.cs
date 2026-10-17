using RemotePlay.Services;
using Xunit;

namespace RemotePlay.Tests;

public sealed class VideoPlayerProcessManagerTests
{
    [Theory]
    [InlineData("http://tv.example/live.m3u8", true)]
    [InlineData("https://tv.example/live.m3u8", true)]
    [InlineData("rtsp://tv.example/live", false)]
    [InlineData("file:///missing/live.m3u8", false)]
    [InlineData("not a uri", false)]
    [InlineData(null, false)]
    public void CanResumePlaybackPathRecognizesHttpStreams(string? filePath, bool expected)
    {
        Assert.Equal(expected, VideoPlayerProcessManager.CanResumePlaybackPath(filePath));
    }

    [Fact]
    public void CanResumePlaybackPathRecognizesExistingLocalFiles()
    {
        Assert.True(VideoPlayerProcessManager.CanResumePlaybackPath(typeof(VideoPlayerProcessManagerTests).Assembly.Location));
    }

    [Theory]
    [InlineData("https://tv.example/live.m3u8", true)]
    [InlineData("http://tv.example/live.m3u8", true)]
    [InlineData("C:\\Videos\\movie.mkv", false)]
    [InlineData("rtsp://tv.example/live", false)]
    [InlineData(null, false)]
    public void IsHttpStreamPathRecognizesOnlyHttpStreams(string? filePath, bool expected)
    {
        Assert.Equal(expected, VideoPlayerProcessManager.IsHttpStreamPath(filePath));
    }

    [Theory]
    [InlineData("http://tv.example/one.m3u8", "https://tv.example/two.m3u8", true)]
    [InlineData("C:\\Videos\\movie.mkv", "https://tv.example/live.m3u8", false)]
    [InlineData(null, "https://tv.example/live.m3u8", false)]
    [InlineData("https://tv.example/live.m3u8", "C:\\Videos\\movie.mkv", false)]
    public void ShouldRecycleForHttpStreamSwitchOnlyWhenReplacingAnHttpStream(
        string? currentFilePath,
        string? nextFilePath,
        bool expected)
    {
        Assert.Equal(
            expected,
            VideoPlayerProcessManager.ShouldRecycleForHttpStreamSwitch(currentFilePath, nextFilePath));
    }
}
