namespace RemotePlay.Models;

internal sealed record MediaCodecInfo
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContainerFormat { get; init; } = string.Empty;
    public int TotalTracks { get; init; }
    public string CapturedAtUtc { get; init; } = string.Empty;
    public VideoTrackInfo[] VideoTracks { get; init; } = [];
    public AudioTrackInfo[] AudioTracks { get; init; } = [];
    public SubtitleTrackInfo[] SubtitleTracks { get; init; } = [];
}

internal sealed record VideoTrackInfo
{
    public int Id { get; init; }
    public string Codec { get; init; } = string.Empty;
    public string CodecDescription { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public string FrameRate { get; init; } = string.Empty;
    public string AspectRatio { get; init; } = string.Empty;
    public string Orientation { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

internal sealed record AudioTrackInfo
{
    public int Id { get; init; }
    public string Codec { get; init; } = string.Empty;
    public string CodecDescription { get; init; } = string.Empty;
    public int Channels { get; init; }
    public string ChannelLayout { get; init; } = string.Empty;
    public int SampleRate { get; init; }
    public string Language { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

internal sealed record SubtitleTrackInfo
{
    public int Id { get; init; }
    public string Codec { get; init; } = string.Empty;
    public string CodecDescription { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Encoding { get; init; } = string.Empty;
}
