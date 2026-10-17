using RemotePlay.Models;
using Xunit;

namespace RemotePlay.Tests;

public sealed class VideoAudioCompatibilitySelectorTests
{
    [Fact]
    public void SupportedStereoAacInMp4UsesOriginalSource()
    {
        var mediaInfo = CreateMediaInfo(new AudioTrackInfo
        {
            Codec = "aac",
            Profile = "LC",
            Channels = 2,
        });

        var mode = VideoAudioCompatibilitySelector.Select(
            @"C:\Movies\source.mp4",
            mediaInfo,
            new HashSet<string>(["aac-lc"], StringComparer.Ordinal));

        Assert.Equal(VideoAudioCompatibilityMode.Original, mode);
    }

    [Fact]
    public void SupportedStereoAacInMkvCopiesAudioIntoMp4WithoutEncoding()
    {
        var mediaInfo = CreateMediaInfo(new AudioTrackInfo
        {
            Codec = "aac",
            Profile = "LC",
            Channels = 2,
        });

        var mode = VideoAudioCompatibilitySelector.Select(
            @"C:\Movies\source.mkv",
            mediaInfo,
            new HashSet<string>(["aac-lc"], StringComparer.Ordinal));

        Assert.Equal(VideoAudioCompatibilityMode.CopyAudioToMp4, mode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    public void UnknownOrUnsupportedAudioFallsBackToAac(string supportedToken)
    {
        var mediaInfo = CreateMediaInfo(new AudioTrackInfo
        {
            Codec = "aac",
            Profile = "LC",
            Channels = 2,
        });

        var mode = VideoAudioCompatibilitySelector.Select(
            @"C:\Movies\source.mp4",
            mediaInfo,
            new HashSet<string>([supportedToken], StringComparer.Ordinal));

        Assert.Equal(VideoAudioCompatibilityMode.EncodeAac, mode);
    }

    [Fact]
    public void MultichannelAacIsDownmixedToStereoAac()
    {
        var mediaInfo = CreateMediaInfo(new AudioTrackInfo
        {
            Codec = "aac",
            Profile = "LC",
            Channels = 6,
        });

        var mode = VideoAudioCompatibilitySelector.Select(
            @"C:\Movies\source.mp4",
            mediaInfo,
            new HashSet<string>(["aac-lc"], StringComparer.Ordinal));

        Assert.Equal(VideoAudioCompatibilityMode.EncodeAac, mode);
    }

    [Fact]
    public void UnsupportedAacProfileFallsBackToAac()
    {
        var mediaInfo = CreateMediaInfo(new AudioTrackInfo
        {
            Codec = "aac",
            Profile = "unknown profile",
            Channels = 2,
        });

        var mode = VideoAudioCompatibilitySelector.Select(
            @"C:\Movies\source.mp4",
            mediaInfo,
            new HashSet<string>(["aac-lc"], StringComparer.Ordinal));

        Assert.Equal(VideoAudioCompatibilityMode.EncodeAac, mode);
    }

    [Fact]
    public void SupportedNonAacCodecCanBeCopiedIntoMp4()
    {
        var mediaInfo = CreateMediaInfo(new AudioTrackInfo
        {
            Codec = "mp3",
            Channels = 2,
        });

        var mode = VideoAudioCompatibilitySelector.Select(
            @"C:\Movies\source.avi",
            mediaInfo,
            new HashSet<string>(["mp3"], StringComparer.Ordinal));

        Assert.Equal(VideoAudioCompatibilityMode.CopyAudioToMp4, mode);
    }

    [Fact]
    public void SupportedTokensIgnoreUnknownValuesAndCreateStableSignature()
    {
        var supported = VideoAudioCompatibilitySelector.ParseSupportedCodecs("aac-lc, unknown, mp3,aac-lc");

        Assert.Equal(["aac-lc", "mp3"], supported.OrderBy(token => token, StringComparer.Ordinal));
        Assert.Equal(
            "aac-lc,mp3",
            VideoAudioCompatibilitySelector.CreateCapabilitySignature(supported));
    }

    private static MediaCodecInfo CreateMediaInfo(params AudioTrackInfo[] audioTracks) =>
        new()
        {
            AudioTracks = audioTracks,
        };
}
