using System.Linq;
using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class VideoMediaInfoProbeTests
{
    [Fact]
    public void CreateStartInfoPassesSourcePathAsOneArgument()
    {
        var sourcePath = @"\\media-server\Movies\Good Morning Vietnam.mkv";
        var startInfo = VideoMediaInfoProbe.CreateStartInfo("ffprobe", sourcePath);

        Assert.Equal("ffprobe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", sourcePath },
            startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void ParseReturnsContainerAndAudioVideoSubtitleTrackDetails()
    {
        const string json = """
            {
              "format": { "format_name": "matroska,webm" },
              "streams": [
                {
                  "index": 0,
                  "codec_type": "video",
                  "codec_name": "h264",
                  "codec_long_name": "H.264 / AVC / MPEG-4 AVC",
                  "width": 1920,
                  "height": 1080,
                  "avg_frame_rate": "24000/1001",
                  "sample_aspect_ratio": "1:1",
                  "tags": { "language": "eng", "title": "Main video" }
                },
                {
                  "index": 1,
                  "codec_type": "audio",
                  "codec_name": "ac3",
                  "codec_long_name": "ATSC A/52A (AC-3)",
                  "profile": "Dolby Digital",
                  "channels": 6,
                  "channel_layout": "5.1(side)",
                  "sample_rate": "48000",
                  "tags": { "language": "eng", "title": "Surround" }
                },
                {
                  "index": 2,
                  "codec_type": "subtitle",
                  "codec_name": "subrip",
                  "codec_long_name": "SubRip subtitle",
                  "tags": { "language": "dut", "title": "Dutch" }
                }
              ]
            }
            """;

        var mediaInfo = VideoMediaInfoProbe.Parse(@"C:\Movies\Good Morning Vietnam.mkv", json);

        Assert.Equal("matroska,webm", mediaInfo.ContainerFormat);
        Assert.Equal("Good Morning Vietnam.mkv", mediaInfo.FileName);
        Assert.Equal(3, mediaInfo.TotalTracks);
        var video = Assert.Single(mediaInfo.VideoTracks);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(1920, video.Width);
        Assert.Equal("23.976 fps", video.FrameRate);
        Assert.Equal("Main video", video.Description);
        var audio = Assert.Single(mediaInfo.AudioTracks);
        Assert.Equal("ac3", audio.Codec);
        Assert.Equal("Dolby Digital", audio.Profile);
        Assert.Equal(6, audio.Channels);
        Assert.Equal("5.1(side)", audio.ChannelLayout);
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal("Surround", audio.Description);
        var subtitle = Assert.Single(mediaInfo.SubtitleTracks);
        Assert.Equal("subrip", subtitle.Codec);
        Assert.Equal("dut", subtitle.Language);
    }
}
