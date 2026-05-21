using RemotePlay.Models;
using RemotePlay.Services;
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

    // ── Music (WPF MediaPlayer, not VLC) ─────────────────────────────────────
    public required Action<string>        PlayMusic      { get; init; }
    public required Action                PauseMusic     { get; init; }
    public required Action                StopMusic      { get; init; }
    public required Func<MusicStatus>     GetMusicStatus { get; init; }
    public required Action<double>        SeekMusic      { get; init; }
    public required Action<double>        SetMusicVolume { get; init; }

    // ── Radio ─────────────────────────────────────────────────────────────────
    public required Func<string, string, string, int, int, Task<List<RadioStation>>> RadioSearch        { get; init; }
    public required Func<int, int, Task<List<RadioStation>>>                          RadioTopStations   { get; init; }
    public required Func<string, Task<List<string>>>                           RadioGetTags       { get; init; }
    public required Func<Task<List<(string Code, string Name)>>>                RadioGetCountries  { get; init; }
    public required Action<string, string>                                      RadioPlay          { get; init; }
    public required Action                                                      RadioStop          { get; init; }
    public required Action<double>                                              RadioSetVolume     { get; init; }
    public required Action<double>                                              RadioSetBoost      { get; init; }
    public required Func<RadioStatus>                                           RadioGetStatus     { get; init; }
    public required Func<List<RadioStation>>                                    RadioGetFavorites  { get; init; }
    public required Action<RadioStation>                                        RadioToggleFavorite{ get; init; }
    public required Func<string, bool>                                          RadioIsFavorite    { get; init; }
    public required Action                                                      RadioNotifyAlive   { get; init; }
    public required Func<string, string, Task<string>>                          RadioResolveUrl    { get; init; }
}
