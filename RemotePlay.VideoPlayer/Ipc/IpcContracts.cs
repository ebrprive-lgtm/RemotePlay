using System;
using System.Text.Json;

namespace RemotePlay.VideoPlayer.Ipc;

public static class IpcMessageJson
{
    public static string Serialize(IpcMessage message, JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(message, message.GetType(), options);
}

// ══════════════════════════════════════════════════════════════════════════════
// IPC Message Base
// ══════════════════════════════════════════════════════════════════════════════

public abstract record IpcMessage
{
    public required string MessageType { get; init; }
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public static class FullscreenControlsSettings
{
    public const bool EnabledByDefault = true;
    public const double DefaultAutoHideDelaySeconds = 2.5;
    public const double MinimumAutoHideDelaySeconds = 0.5;
    public const double MaximumAutoHideDelaySeconds = 60;

    public static bool IsValidAutoHideDelaySeconds(double seconds) =>
        double.IsFinite(seconds)
        && seconds >= MinimumAutoHideDelaySeconds
        && seconds <= MaximumAutoHideDelaySeconds;
}

public static class RemotePlayCommandPipe
{
    public const string Name = "RemotePlay_CommandPipe";
    public const char ArgumentSeparator = '\u001f';
    public const string FullscreenPlayerNextArgument = "--fullscreen-player-next";
    public const string FullscreenPlayerPreviousArgument = "--fullscreen-player-previous";
    public const string FullscreenPlayerEscapeArgument = "--fullscreen-player-escape";
}

// ══════════════════════════════════════════════════════════════════════════════
// Commands (Main Window → Video Player)
// ══════════════════════════════════════════════════════════════════════════════

public sealed record PlayCommand : IpcMessage
{
    public required string FilePath { get; init; }
    public bool IsLiveTv { get; init; }
    public double StartPositionSeconds { get; init; }
    public double Volume { get; init; } = 1.0;
    public double Brightness { get; init; } = 0.5;
    public double Saturation { get; init; } = 1.0;
    public double Sharpness { get; init; } = 0.0;
    public double Zoom { get; init; } = 1.0;
    public double AudioBoost { get; init; } = 1.0;
    public double PlaybackSpeed { get; init; } = 1.0;
    public bool SubtitlesEnabled { get; init; } = true;
    public int? PreferredAudioTrack { get; init; }
    public int? PreferredSubtitleTrack { get; init; }
    public int TargetDisplayIndex { get; init; }
    public string? SubtitleFilePath { get; init; }
    public int EqPreset { get; init; } = -1;
    public int ReverbPreset { get; init; }
    public bool ForceSwAudio { get; init; }
}

public sealed record PauseCommand : IpcMessage
{
}

public sealed record ResumeCommand : IpcMessage
{
}

public sealed record StopCommand : IpcMessage
{
}

public sealed record BringToFrontCommand : IpcMessage
{
    public int TargetDisplayIndex { get; init; }
}

public sealed record SeekCommand : IpcMessage
{
    public required double PositionSeconds { get; init; }
}

public sealed record SetVolumeCommand : IpcMessage
{
    public required double Volume { get; init; }
}

public sealed record SetFullscreenControlsSettingsCommand : IpcMessage
{
    public required bool Enabled { get; init; }
    public required double AutoHideDelaySeconds { get; init; }
}

public sealed record SetAdjacentVideoTitlesCommand : IpcMessage
{
    public required string FilePath { get; init; }
    public string? PreviousTitle { get; init; }
    public string? NextTitle { get; init; }
}

public sealed record SetBrightnessCommand : IpcMessage
{
    public required double Brightness { get; init; }
}

public sealed record SetSaturationCommand : IpcMessage
{
    public required double Saturation { get; init; }
}

public sealed record SetSharpnessCommand : IpcMessage
{
    public required double Sharpness { get; init; }
}

public sealed record SetZoomCommand : IpcMessage
{
    public required double Zoom { get; init; }
}

public sealed record SetAudioBoostCommand : IpcMessage
{
    public required double AudioBoost { get; init; }
}

public sealed record SetPlaybackSpeedCommand : IpcMessage
{
    public required double PlaybackSpeed { get; init; }
}

public sealed record SetSubtitlesEnabledCommand : IpcMessage
{
    public required bool Enabled { get; init; }
}

public sealed record SetAudioTrackCommand : IpcMessage
{
    public required int TrackId { get; init; }
}

public sealed record SetSubtitleTrackCommand : IpcMessage
{
    public required int TrackId { get; init; }
}

public sealed record SetEqualizerCommand : IpcMessage
{
    public required int Preset { get; init; }
}

public sealed record SetReverbCommand : IpcMessage
{
    public required int Preset { get; init; }
}

public sealed record PingCommand : IpcMessage
{
}

public sealed record StatusRequestCommand : IpcMessage
{
}

public sealed record ShutdownCommand : IpcMessage
{
}

// ══════════════════════════════════════════════════════════════════════════════
// Responses (Video Player → Main Window)
// ══════════════════════════════════════════════════════════════════════════════

public sealed record PongResponse : IpcMessage
{
    public Guid RequestId { get; init; }
}

public sealed record StatusResponse : IpcMessage
{
    public Guid RequestId { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsPaused { get; init; }
    public bool IsLiveTv { get; init; }
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public string? CurrentFilePath { get; init; }
    public bool HasSubtitles { get; init; }
    public int? CurrentAudioTrack { get; init; }
    public int? CurrentSubtitleTrack { get; init; }
    public string PlayerState { get; init; } = "Unknown";
    public string LastError { get; init; } = string.Empty;
}

public sealed record AcknowledgmentResponse : IpcMessage
{
    public Guid RequestId { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
