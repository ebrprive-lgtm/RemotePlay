using RemotePlay.Models;
using System.Diagnostics.CodeAnalysis;

namespace RemotePlay;

[ExcludeFromCodeCoverage]
internal sealed class WebServerCallbacks
{
    public required Action<string> Play { get; init; }
    public required Action Stop { get; init; }
    public required Action Pause { get; init; }
    public required Func<PlaybackStatus> GetStatus { get; init; }
    public required Action<double> Seek { get; init; }
    public required Action<double> Skip { get; init; }
    public required Action<double> SetVolume { get; init; }
    public required Action ToggleMute { get; init; }
    public required Action<double> SetBrightness { get; init; }
    public required Action<double> SetSaturation { get; init; }
    public required Action<double> SetZoom { get; init; }
    public required Action<double> SetAudioBoost { get; init; }
    public required Action<double> SetPlaybackSpeed { get; init; }
    public required Action ToggleSubtitles { get; init; }
    public required Action<int> SetAudioTrack { get; init; }
    public required Action<int> SetSubtitleTrack { get; init; }
    public required Action<int> PlayAdjacent { get; init; }
    public required Action<string> Enqueue { get; init; }
    public required Action<string> RemoveFromQueue { get; init; }
    public required Action<string, int> MoveQueueItem { get; init; }
    public required Action ClearQueue { get; init; }
    public required Action<string> ClearPlaybackHistory { get; init; }
    public required Action<string, bool> MarkWatchedHistory { get; init; }
    public required Func<DisplayDiagnostics> GetDisplayDiagnostics { get; init; }
    public required Action FixAudio { get; init; }
}
