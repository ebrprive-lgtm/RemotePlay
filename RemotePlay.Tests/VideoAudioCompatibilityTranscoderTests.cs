using System.Text;
using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class VideoAudioCompatibilityTranscoderTests
{
    [Fact]
    public void CreateStartInfoCopiesVideoAndCreatesStereoAacMp4()
    {
        var startInfo = VideoAudioCompatibilityTranscoder.CreateStartInfo(
            "ffmpeg",
            @"\\media-server\Movies\source.mkv",
            @"C:\Temp\output.partial");

        Assert.Equal("ffmpeg", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-nostats",
                "-stats_period", "0.5",
                "-progress", "pipe:1",
                "-y",
                "-i", @"\\media-server\Movies\source.mkv",
                "-map", "0:v:0",
                "-map", "0:a?",
                "-map_metadata", "0",
                "-map_chapters", "0",
                "-c:v", "copy",
                "-c:a", "aac",
                "-ac", "2",
                "-b:a", "192k",
                "-sn",
                "-dn",
                "-movflags", "+faststart",
                "-f", "mp4",
                @"C:\Temp\output.partial",
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfoCopiesSupportedAudioWithoutEncodingIt()
    {
        var startInfo = VideoAudioCompatibilityTranscoder.CreateStartInfo(
            "ffmpeg",
            @"\\media-server\Movies\source.mkv",
            @"C:\Temp\output.partial",
            VideoAudioCompatibilityMode.CopyAudioToMp4);

        Assert.Contains("-c:a", startInfo.ArgumentList);
        Assert.Contains("copy", startInfo.ArgumentList);
        Assert.DoesNotContain("aac", startInfo.ArgumentList);
        Assert.DoesNotContain("-ac", startInfo.ArgumentList);
        Assert.DoesNotContain("-b:a", startInfo.ArgumentList);
        Assert.Equal(
            "+faststart",
            startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("-movflags") + 1]);
    }

    [Fact]
    public void CreateProgressiveStartInfoSeparatesMediaAndProgressAndFragmentsTheMp4()
    {
        var startInfo = VideoAudioCompatibilityTranscoder.CreateProgressiveStartInfo(
            "ffmpeg",
            @"C:\Movies\source.mkv");

        Assert.Equal("ffmpeg", startInfo.FileName);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal("pipe:2", startInfo.ArgumentList[
            startInfo.ArgumentList.IndexOf("-progress") + 1]);
        Assert.Contains("+frag_keyframe+empty_moov+default_base_moof", startInfo.ArgumentList);
        Assert.Contains("1000000", startInfo.ArgumentList);
        Assert.Contains("-flush_packets", startInfo.ArgumentList);
        Assert.Equal("pipe:1", startInfo.ArgumentList[^1]);
        Assert.Contains("-c:v", startInfo.ArgumentList);
        Assert.Contains("copy", startInfo.ArgumentList);
        Assert.Contains("-c:a", startInfo.ArgumentList);
        Assert.Contains("aac", startInfo.ArgumentList);
    }

    [Fact]
    public void ProgressValuesProduceAnEstimatedPercentageAndTimeRemaining()
    {
        Assert.True(VideoAudioCompatibilityTranscoder.TryParseOutputTimeMicroseconds("30000000", out var processedSeconds));
        Assert.True(VideoAudioCompatibilityTranscoder.TryParseSpeedMultiplier(" 2.0x ", out var speedMultiplier));

        var progress = VideoAudioCompatibilityTranscoder.CalculateProgress(120, processedSeconds, speedMultiplier);

        Assert.True(progress.IsTranscoding);
        Assert.False(progress.IsComplete);
        Assert.True(progress.ProgressPercent.HasValue);
        Assert.True(progress.EstimatedRemainingSeconds.HasValue);
        Assert.Equal(25d, progress.ProgressPercent.Value, 6);
        Assert.Equal(45d, progress.EstimatedRemainingSeconds.Value, 6);
    }

    [Fact]
    public void ProgressWithoutDurationDoesNotInventAnEstimate()
    {
        var progress = VideoAudioCompatibilityTranscoder.CalculateProgress(null, 30, 1.5);

        Assert.Null(progress.ProgressPercent);
        Assert.Null(progress.EstimatedRemainingSeconds);
        Assert.False(VideoAudioCompatibilityTranscoder.TryParseOutputTimeMicroseconds("-1", out _));
        Assert.False(VideoAudioCompatibilityTranscoder.TryParseSpeedMultiplier("N/A", out _));
    }

    [Fact]
    public async Task CancelOperationCancelsItsTranscodeTask()
    {
        using var operation = new VideoAudioCompatibilityTranscoder.TranscodeOperation<string>(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "unreachable";
            },
            CancellationToken.None);
        var task = operation.Task;

        Assert.True(operation.RequestCancellation());
        Assert.True(operation.IsCancellationRequested);
        Assert.True(operation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(operation.IsCompleted);
        Assert.False(operation.Cancel());
    }

    [Fact]
    public void CreateCacheKeyChangesWhenSourceMetadataChanges()
    {
        var writeTime = DateTime.UtcNow;

        var initial = VideoAudioCompatibilityTranscoder.CreateCacheKey(@"C:\Movies\source.mkv", 100, writeTime);
        var differentLength = VideoAudioCompatibilityTranscoder.CreateCacheKey(@"C:\Movies\source.mkv", 101, writeTime);
        var differentWriteTime = VideoAudioCompatibilityTranscoder.CreateCacheKey(
            @"C:\Movies\source.mkv",
            100,
            writeTime.AddTicks(1));

        Assert.NotEqual(initial, differentLength);
        Assert.NotEqual(initial, differentWriteTime);
        Assert.Matches("^[a-f0-9]{64}$", initial);
    }

    [Fact]
    public void GetProgressReportsACompletedCacheForCodecSpecificRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(directory, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var sourcePath = Path.Combine(directory, "source.mkv");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);

        try
        {
            var source = new FileInfo(sourcePath);
            var cacheKey = VideoAudioCompatibilityTranscoder.CreateCacheKey(
                source.FullName,
                source.Length,
                source.LastWriteTimeUtc);
            var cachePath = Path.Combine(cacheDirectory, cacheKey + ".mp4");
            File.WriteAllBytes(cachePath, [1, 2, 3]);
            File.WriteAllBytes(
                VideoAudioCompatibilityTranscoder.GetReadyMarkerPath(cachePath),
                Encoding.ASCII.GetBytes(VideoAudioCompatibilityTranscoder.ReadyMarkerContents));
            var transcoder = new VideoAudioCompatibilityTranscoder(cacheDirectory);

            var progress = transcoder.GetProgress(sourcePath, ["aac-lc"]);

            Assert.False(progress.IsTranscoding);
            Assert.True(progress.IsComplete);
            Assert.Equal(100d, progress.ProgressPercent!.Value);
            Assert.Equal("aac", progress.Mode);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetProgressDoesNotReportAnUnmarkedOrLegacyCacheFileAsComplete(bool writeLegacyMarker)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(directory, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var sourcePath = Path.Combine(directory, "source.mkv");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);

        try
        {
            var source = new FileInfo(sourcePath);
            var cacheKey = VideoAudioCompatibilityTranscoder.CreateCacheKey(
                source.FullName,
                source.Length,
                source.LastWriteTimeUtc);
            var cachePath = Path.Combine(cacheDirectory, cacheKey + ".mp4");
            File.WriteAllBytes(cachePath, [1, 2, 3]);
            if (writeLegacyMarker)
            {
                File.WriteAllBytes(
                    VideoAudioCompatibilityTranscoder.GetReadyMarkerPath(cachePath),
                    Array.Empty<byte>());
            }
            var transcoder = new VideoAudioCompatibilityTranscoder(cacheDirectory);

            var progress = transcoder.GetProgress(sourcePath, ["aac-lc"]);

            Assert.False(progress.IsTranscoding);
            Assert.False(progress.IsComplete);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
