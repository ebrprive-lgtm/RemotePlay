using System;
using System.Text.Json;
using RemotePlay.VideoPlayer.Ipc;
using Xunit;

namespace RemotePlay.Tests;

public sealed class IpcMessageJsonTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Serialize_AcknowledgmentResponse_PreservesSuccessAndRequestId()
    {
        var requestId = Guid.NewGuid();
        IpcMessage response = new AcknowledgmentResponse
        {
            MessageType = nameof(AcknowledgmentResponse),
            RequestId = requestId,
            Success = true
        };

        var json = IpcMessageJson.Serialize(response, Options);
        var deserialized = JsonSerializer.Deserialize<AcknowledgmentResponse>(json, Options);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Equal(requestId, deserialized.RequestId);
    }

    [Fact]
    public void Serialize_StatusResponse_PreservesPlaybackState()
    {
        IpcMessage response = new StatusResponse
        {
            MessageType = nameof(StatusResponse),
            IsPlaying = true,
            IsLiveTv = true,
            CurrentFilePath = @"C:\Videos\movie.mkv",
            DurationSeconds = 120
        };

        var json = IpcMessageJson.Serialize(response, Options);
        var deserialized = JsonSerializer.Deserialize<StatusResponse>(json, Options);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.IsPlaying);
        Assert.True(deserialized.IsLiveTv);
        Assert.Equal(@"C:\Videos\movie.mkv", deserialized.CurrentFilePath);
        Assert.Equal(120, deserialized.DurationSeconds);
    }

    [Fact]
    public void Serialize_PlayCommand_PreservesLiveTvMode()
    {
        IpcMessage command = new PlayCommand
        {
            MessageType = nameof(PlayCommand),
            FilePath = "https://example.com/live/stream.m3u8",
            IsLiveTv = true
        };

        var json = IpcMessageJson.Serialize(command, Options);
        var deserialized = JsonSerializer.Deserialize<PlayCommand>(json, Options);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.IsLiveTv);
    }

    [Fact]
    public void Serialize_BringToFrontCommand_PreservesTargetDisplay()
    {
        IpcMessage command = new BringToFrontCommand
        {
            MessageType = nameof(BringToFrontCommand),
            TargetDisplayIndex = 2
        };

        var json = IpcMessageJson.Serialize(command, Options);
        var deserialized = JsonSerializer.Deserialize<BringToFrontCommand>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.TargetDisplayIndex);
    }

    [Fact]
    public void Serialize_SetFullscreenControlsSettingsCommand_PreservesSettings()
    {
        IpcMessage command = new SetFullscreenControlsSettingsCommand
        {
            MessageType = nameof(SetFullscreenControlsSettingsCommand),
            Enabled = false,
            AutoHideDelaySeconds = 5
        };

        var json = IpcMessageJson.Serialize(command, Options);
        var deserialized = JsonSerializer.Deserialize<SetFullscreenControlsSettingsCommand>(json, Options);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.Enabled);
        Assert.Equal(5, deserialized.AutoHideDelaySeconds);
    }

    [Fact]
    public void Serialize_SetAdjacentVideoTitlesCommand_PreservesCurrentPathAndTitles()
    {
        IpcMessage command = new SetAdjacentVideoTitlesCommand
        {
            MessageType = nameof(SetAdjacentVideoTitlesCommand),
            FilePath = @"C:\Videos\current.mkv",
            PreviousTitle = "Previous title",
            NextTitle = "Next title"
        };

        var json = IpcMessageJson.Serialize(command, Options);
        var deserialized = JsonSerializer.Deserialize<SetAdjacentVideoTitlesCommand>(json, Options);

        Assert.NotNull(deserialized);
        Assert.Equal(@"C:\Videos\current.mkv", deserialized.FilePath);
        Assert.Equal("Previous title", deserialized.PreviousTitle);
        Assert.Equal("Next title", deserialized.NextTitle);
    }
}
