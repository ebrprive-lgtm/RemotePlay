using System.Diagnostics.CodeAnalysis;

namespace RemotePlay;

[ExcludeFromCodeCoverage]
internal sealed record PlaybackStatus
{
    public bool IsPlaying { get; init; }
    public bool IsPaused  { get; init; }
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public string Title { get; init; } = string.Empty;
    public double Volume { get; init; } = 1;
    public bool IsMuted { get; init; }
    public string LastError { get; init; } = string.Empty;
    public bool CanResume { get; init; }
    public double ResumePositionSeconds { get; init; }
    public bool SmartResumeApplied { get; init; }
    public double Brightness { get; init; }
    public double Saturation { get; init; } = 1;
    public double Zoom { get; init; } = 1;
    public double AudioBoost { get; init; } = 1;
    public double PlaybackSpeed { get; init; } = 1;
    public bool SubtitlesEnabled { get; init; }
    public bool HasSubtitles { get; init; }
    public TrackOption[] AudioTracks { get; init; } = [];
    public TrackOption[] SubtitleTracks { get; init; } = [];
    public int CurrentAudioTrackId { get; init; }
    public int CurrentSubtitleTrackId { get; init; } = -1;
    public string? PreviousTitle { get; init; }
    public string? NextTitle { get; init; }
    public string? FilePath { get; init; }
    public PlaybackQueueItem[] Queue { get; init; } = [];
    public int QueueCount { get; init; }
    public ChapterInfo[] Chapters { get; init; } = [];
    public int CurrentChapter { get; init; } = -1;
    public int EqPreset { get; init; } = -1;
    public int ReverbPreset { get; init; } = 0;
}

[ExcludeFromCodeCoverage]
internal sealed record TrackOption(int Id, string Name, string Language = "", bool IsForced = false, bool IsDefault = false);

[ExcludeFromCodeCoverage]
internal sealed record ChapterInfo(int Id, string Name, double StartSeconds, double DurationSeconds);

[ExcludeFromCodeCoverage]
internal sealed record PlaybackQueueItem(string Path, string Title);

[ExcludeFromCodeCoverage]
internal sealed record LibraryScanStatus
{
    public bool IsScanning { get; init; }
    public int IndexedFiles { get; init; }
    public int IndexedMovies { get; init; }
    public int IndexedLinks { get; init; }
    public int ScannedFiles { get; init; }
    public int ScannedFolders { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string LastError { get; init; } = string.Empty;
    /// <summary>Number of .rplink files found to have missing targets during the last stale-link background check.</summary>
    public int StaleLinkCount { get; init; }
    /// <summary>True when every configured video library path (primary + additional) is missing or empty.</summary>
    public bool AllPathsInvalid { get; init; }
    /// <summary>True when every configured music library path (primary + additional) is missing or empty.</summary>
    public bool AllMusicPathsInvalid { get; init; }
}

[ExcludeFromCodeCoverage]
internal sealed record MusicIndexStatus
{
    public bool IsIndexing { get; init; }
    public bool IsEnriching { get; init; }
    public int IndexedTracks { get; init; }
    public string LastError { get; init; } = string.Empty;
    public DateTimeOffset? CompletedUtc { get; init; }
}

[ExcludeFromCodeCoverage]
internal sealed record ThumbnailQueueStatus
{
    public bool IsRunning { get; init; }
    public int Total { get; init; }
    public int Processed { get; init; }
    public int Generated { get; init; }
    public int Cached { get; init; }
    public string CurrentTitle { get; init; } = string.Empty;
    public string LastError { get; init; } = string.Empty;
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
}

[ExcludeFromCodeCoverage]
internal sealed record LibraryIndexCache
{
    public string RootPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public LibraryFile[] Files { get; init; } = [];
}

[ExcludeFromCodeCoverage]
internal sealed record MusicIndexCache
{
    public string RootPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public MusicFile[] Files { get; init; } = [];
}

internal sealed record M3uIndexCache
{
    public string RootPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public M3uEntry[] Entries { get; init; } = [];
}

[ExcludeFromCodeCoverage]
internal sealed record LibraryFile(
    string Name,
    string FilePath,
    string EncodedPath,
    string FolderName,
    string SearchText,
    long SizeBytes = 0,
    DateTime LastWriteUtc = default,
    bool IsLink = false,
    string? LinkSourcePath = null,
    bool IsFolderLink = false);
