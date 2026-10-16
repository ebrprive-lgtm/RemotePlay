using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RemotePlay.VideoPlayer.Ipc;

namespace RemotePlay;

/// <summary>
/// Video Playback via Isolated Process
/// </summary>
public partial class MainWindow
{
    private readonly SemaphoreSlim _videoPlaybackStartLock = new(1, 1);

    private bool IsIsolatedVideoActive
    {
        get
        {
            if (_videoPlayerManager is not { IsProcessRunning: true } manager
                || string.IsNullOrWhiteSpace(_currentFilePath))
            {
                return false;
            }

            var status = manager.LastStatus;
            if (status is null)
                return true;

            if (string.IsNullOrWhiteSpace(status.CurrentFilePath))
                return false;

            if (!string.Equals(status.CurrentFilePath, _currentFilePath, StringComparison.OrdinalIgnoreCase))
                return true;

            return status.IsPlaying
                || status.IsPaused
                || status.PlayerState is "Opening" or "Buffering" or "Playing" or "Paused";
        }
    }

    private async Task BringIsolatedVideoToFrontAsync()
    {
        if (_videoPlayerManager is not { IsProcessRunning: true, IsConnected: true } manager)
        {
            Logger.Warning("VideoPlayback", "The isolated video player is not available to bring forward.");
            return;
        }

        if (!await manager.BringToFrontAsync(GetTargetDisplayIndex()))
            Logger.Warning("VideoPlayback", "Could not bring the isolated video player to the front.");
    }

    private async Task ApplyFullscreenControlsSettingsToPlayerAsync(bool enabled, double autoHideDelaySeconds)
    {
        if (_videoPlayerManager is not { IsProcessRunning: true } manager)
            return;

        try
        {
            if (!await manager.SendSetFullscreenControlsSettingsAsync(enabled, autoHideDelaySeconds))
            {
                Logger.Warning(
                    "VideoPlayback",
                    "The isolated player did not acknowledge the fullscreen controls settings; they will be retried before the next playback.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("VideoPlayback", "Failed to apply fullscreen controls settings to the isolated player.", ex);
        }
    }

    private void UpdateIsolatedPlayerNavigationTitles()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateIsolatedPlayerNavigationTitles);
            return;
        }

        var currentPath = _currentFilePath;
        if (string.IsNullOrWhiteSpace(currentPath)
            || _videoPlayerManager is not { IsProcessRunning: true, IsConnected: true } manager)
        {
            return;
        }

        var hasCurrentAdjacentCache = string.Equals(
            _adjacentVideoCacheCurrentPath,
            currentPath,
            StringComparison.OrdinalIgnoreCase);
        if (!hasCurrentAdjacentCache)
            RefreshAdjacentVideoCache(currentPath);

        var previousTitle = hasCurrentAdjacentCache && !string.IsNullOrWhiteSpace(_previousAdjacentVideoPath)
            ? Path.GetFileNameWithoutExtension(_previousAdjacentVideoPath)
            : null;
        var nextTitle = _playbackQueue.Count > 0
            ? Path.GetFileNameWithoutExtension(_playbackQueue[0])
            : hasCurrentAdjacentCache && !string.IsNullOrWhiteSpace(_nextAdjacentVideoPath)
                ? Path.GetFileNameWithoutExtension(_nextAdjacentVideoPath)
                : null;

        _ = SendAdjacentVideoTitlesToPlayerAsync(manager, currentPath, previousTitle, nextTitle);
    }

    private async Task SendAdjacentVideoTitlesToPlayerAsync(
        Services.VideoPlayerProcessManager manager,
        string currentPath,
        string? previousTitle,
        string? nextTitle)
    {
        try
        {
            if (!await manager.SendSetAdjacentVideoTitlesAsync(currentPath, previousTitle, nextTitle))
            {
                Logger.Warning(
                    "VideoPlayback",
                    "The isolated player did not acknowledge the adjacent video titles.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("VideoPlayback", "Failed to update adjacent video titles in the isolated player.", ex);
        }
    }

    private async void PlayMovieIsolated(string filePath)
    {
        await _videoPlaybackStartLock.WaitAsync();
        try
        {
            AppendLog($"[Video] PlayMovieIsolated: {System.IO.Path.GetFileName(filePath)}");
            StopCompetingPlaybackBeforeVideoStart();
            RestoreWindowForPlayback();
            if (_isVideoMode)
                OnToggleView(this, new RoutedEventArgs());

            // Ensure video player process is running (semaphore inside StartAsync prevents double-launch)
            if (_videoPlayerManager is null || !_videoPlayerManager.IsProcessRunning || !_videoPlayerManager.IsConnected)
            {
                AppendLog($"[Video] Manager state: null={_videoPlayerManager is null} running={_videoPlayerManager?.IsProcessRunning} connected={_videoPlayerManager?.IsConnected}");
                _videoPlayerManager?.Dispose();
                _videoPlayerManager = new Services.VideoPlayerProcessManager(AppendLog);

                var displayIndex = GetTargetDisplayIndex();
                AppendLog($"[Video] Launching VideoPlayer process (display={displayIndex})...");

                // Keep synchronous process and pipe startup away from the UI and normal
                // thread pool, which LibVLC may occupy while its native components load.
                var started = await Task.Factory.StartNew(
                    () => _videoPlayerManager.StartAsync(displayIndex).GetAwaiter().GetResult(),
                    System.Threading.CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                AppendLog($"[Video] StartAsync returned: {started}");

                if (!started)
                {
                    AppendLog("[Video] ERROR: Failed to start video player process");
                    _currentFilePath = null;
                    _lastPlaybackError = "The isolated video player could not be started. Check the application log for details.";
                    ShowIdleOverlay();
                    return;
                }

                StartVideoPlayerHealthMonitoring();
            }
            else
            {
                AppendLog("[Video] Reusing existing VideoPlayer process");
            }

            if (!await _videoPlayerManager.SendSetFullscreenControlsSettingsAsync(
                    _config.FullscreenControlsEnabled,
                    _config.FullscreenControlsAutoHideDelaySeconds))
            {
                Logger.Warning("VideoPlayback", "The isolated player did not acknowledge its fullscreen controls settings.");
            }

            // Prepare playback state
            _isPaused = false;
            _lastPlaybackError = string.Empty;
            _currentFilePath = filePath;
            RefreshAdjacentVideoCache(filePath);
            _pendingResumePosition = null;
            _smartResumeApplied = false;
            _duration = TimeSpan.Zero;
            _positionMs = 0;
            _positionBaseMs = 0;
            _positionClockStartedUtc = null;
            _lastSubtitleTrackId = null;
            _lastAudioTrackId = null;
            _preferredSubtitleApplied = false;
            _zoom = 1;
            _forceSwAudio = false;

            ApplyStoredMoviePreferences(filePath);

            // Find subtitle file
            string? subtitlePath = FindSubtitleFile(filePath);

            // Build play command
            var playCmd = new PlayCommand
            {
                MessageType = nameof(PlayCommand),
                FilePath = filePath,
                StartPositionSeconds = 0,
                Volume = _volume,
                Brightness = _brightness,
                Saturation = _saturation,
                Sharpness = _sharpness,
                Zoom = _zoom,
                AudioBoost = _audioBoost,
                PlaybackSpeed = _playbackSpeed,
                SubtitlesEnabled = _subtitlesEnabled,
                PreferredAudioTrack = _lastAudioTrackId,
                PreferredSubtitleTrack = _lastSubtitleTrackId,
                TargetDisplayIndex = GetTargetDisplayIndex(),
                SubtitleFilePath = subtitlePath,
                EqPreset = _currentEqPreset,
                ReverbPreset = _currentReverbPreset,
                ForceSwAudio = _forceSwAudio
            };

            // Send play command off the UI thread (pipe write should not block UI)
            AppendLog($"[Video] Sending PlayCommand: {System.IO.Path.GetFileName(filePath)} pos=0");
            var playAccepted = await Task.Run(() => _videoPlayerManager.SendPlayCommandAsync(playCmd));
            if (!playAccepted)
                throw new InvalidOperationException("The isolated video player did not acknowledge the playback request.");

            AppendLog("[Video] PlayCommand acknowledged");
            await BringIsolatedVideoToFrontAsync();
            UpdateIsolatedPlayerNavigationTitles();

            StopPositionClock(0);
            _historyTimer.Start();
            AppendLog($"Playing: {Path.GetFileName(filePath)}");
            Logger.Info($"[Video Isolated] Playing: {filePath}");
        }
        catch (Exception ex)
        {
            Logger.Error("[Video Isolated] Error starting playback", ex);
            AppendLog($"[Video] ERROR: {ex.Message}");
            _lastPlaybackError = $"Video playback failed to start: {ex.Message}";
            ShowIdleOverlay();
        }
        finally
        {
            _videoPlaybackStartLock.Release();
        }
    }

    private string? FindSubtitleFile(string videoPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(videoPath);
            if (string.IsNullOrEmpty(dir))
                return null;

            var nameWithoutExt = Path.GetFileNameWithoutExtension(videoPath);

            foreach (var ext in SubtitleExtensions)
            {
                var subtitlePath = Path.Combine(dir, nameWithoutExt + ext);
                if (File.Exists(subtitlePath))
                {
                    Logger.Info($"[Video] Found subtitle: {Path.GetFileName(subtitlePath)}");
                    return subtitlePath;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Subtitle search error", ex);
        }

        return null;
    }

    private int GetTargetDisplayIndex()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0)
            throw new InvalidOperationException("No display detected.");

        var preferredIndex = _config.PreferredDisplayIndex;
        if (preferredIndex >= 0 && preferredIndex < screens.Length)
            return preferredIndex;

        var primaryDeviceName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        for (var index = 0; index < screens.Length; index++)
        {
            if (string.Equals(screens[index].DeviceName, primaryDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning("VideoPlayback", $"Preferred display index {preferredIndex} is unavailable; using primary display {index}.");
                return index;
            }
        }

        Logger.Warning("VideoPlayback", $"Preferred display index {preferredIndex} is unavailable; using display 0.");
        return 0;
    }

    private async void PauseVideoIsolated()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendPauseAsync();
            _isPaused = true;
            AppendLog("[Video] Paused");
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Pause error", ex);
        }
    }

    private async void ResumeVideoIsolated()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendResumeAsync();
            _isPaused = false;
            AppendLog("[Video] Resumed");
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Resume error", ex);
        }
    }

    private async Task StopVideoIsolatedAsync()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendStopAsync();
            _currentFilePath = null;
            _isPaused = false;
            _historyTimer.Stop();
            StopVideoPlayerHealthMonitoring();
            AppendLog("[Video] Stopped");
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Stop error", ex);
        }
    }

    private async Task ReturnToFullscreenQrAfterIsolatedPlayerEscapeAsync()
    {
        try
        {
            await StopVideoIsolatedAsync();
            StopVideoPlayerHealthMonitoring();
            ResetPlaybackState();
            RestoreWindowForPlayback();

            if (_isVideoMode)
                ShowIdleOverlay();
            else
                await EnterFullscreenCleanAsync();

            Activate();
        }
        catch (Exception ex)
        {
            Logger.Error("VideoPlayback", "Failed to return to the fullscreen QR screen after Escape.", ex);
        }
    }

    private async void SeekVideoIsolated(double positionSeconds)
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendSeekAsync(positionSeconds);
            _positionMs = (long)(positionSeconds * 1000);
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Seek error", ex);
        }
    }

    private async void UpdateVideoVolumeIsolated()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendSetVolumeAsync(_volume);
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Volume update error", ex);
        }
    }

    private async void UpdateVideoBrightnessIsolated()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendSetBrightnessAsync(_brightness);
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Brightness update error", ex);
        }
    }

    private async void UpdateVideoSaturationIsolated()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendSetSaturationAsync(_saturation);
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Saturation update error", ex);
        }
    }

    private async void UpdateVideoSharpnessIsolated()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendSetSharpnessAsync(_sharpness);
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Sharpness update error", ex);
        }
    }

    private async void UpdateVideoZoomIsolated()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendSetZoomAsync(_zoom);
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Zoom update error", ex);
        }
    }

    private async void UpdateVideoPlaybackSpeedIsolated()
    {
        if (_videoPlayerManager is null)
            return;

        try
        {
            await _videoPlayerManager.SendSetPlaybackSpeedAsync(_playbackSpeed);
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Playback speed update error", ex);
        }
    }

    private void CompleteVideoTransition()
    {
        // Called after video starts playing to complete UI transition
        Dispatcher.InvokeAsync(() =>
        {
            // Show video is active
            if (FindName("IdleOverlay") is UIElement idleOverlay)
                idleOverlay.Visibility = Visibility.Collapsed;
        });
    }
}
