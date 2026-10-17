using System.IO;
using System.Linq;
using RemotePlay.Models;

namespace RemotePlay;

internal enum VideoAudioCompatibilityMode
{
    Original,
    CopyAudioToMp4,
    EncodeAac,
}

internal static class VideoAudioCompatibilitySelector
{
    private static readonly HashSet<string> KnownCodecTokens = new(StringComparer.Ordinal)
    {
        "aac-lc",
        "aac-main",
        "aac-ssr",
        "aac-ltp",
        "aac-he",
        "aac-hev2",
        "mp3",
        "ac3",
        "eac3",
        "alac",
        "flac",
        "opus",
        "vorbis",
    };

    internal static HashSet<string> ParseSupportedCodecs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new HashSet<string>(StringComparer.Ordinal);

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(KnownCodecTokens.Contains)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal static VideoAudioCompatibilityMode Select(
        string sourcePath,
        MediaCodecInfo mediaInfo,
        IReadOnlySet<string> supportedCodecs)
    {
        ArgumentNullException.ThrowIfNull(mediaInfo);
        ArgumentNullException.ThrowIfNull(supportedCodecs);

        var audioTracks = mediaInfo.AudioTracks;
        var allAudioTracksSupported = audioTracks.All(track =>
            track.Channels is > 0 and <= 2
            && GetCodecToken(track) is { } token
            && supportedCodecs.Contains(token));
        if (!allAudioTracksSupported)
            return VideoAudioCompatibilityMode.EncodeAac;

        var extension = Path.GetExtension(sourcePath);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                ? VideoAudioCompatibilityMode.Original
                : VideoAudioCompatibilityMode.CopyAudioToMp4;
    }

    internal static string CreateCapabilitySignature(IEnumerable<string> supportedCodecs) =>
        string.Join(
            ",",
            supportedCodecs
                .Where(KnownCodecTokens.Contains)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(token => token, StringComparer.Ordinal));

    private static string? GetCodecToken(AudioTrackInfo track)
    {
        var codec = track.Codec.Trim().ToLowerInvariant();
        if (codec != "aac")
        {
            return codec switch
            {
                "mp3" => "mp3",
                "ac3" => "ac3",
                "eac3" => "eac3",
                "alac" => "alac",
                "flac" => "flac",
                "opus" => "opus",
                "vorbis" => "vorbis",
                _ => null,
            };
        }

        var profile = track.Profile
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return profile switch
        {
            "lc" => "aac-lc",
            "main" => "aac-main",
            "ssr" => "aac-ssr",
            "ltp" => "aac-ltp",
            "heaac" or "heaacv1" => "aac-he",
            "heaacv2" => "aac-hev2",
            _ => null,
        };
    }
}

internal sealed record VideoAudioCompatibilityStream(
    string Path,
    VideoAudioCompatibilityMode Mode);
