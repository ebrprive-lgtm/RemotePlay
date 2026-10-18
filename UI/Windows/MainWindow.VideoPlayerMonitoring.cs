using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RemotePlay.Services;

namespace RemotePlay;

/// <summary>
/// Isolated Video Player Integration - Health Monitoring
/// </summary>
public partial class MainWindow
{
    private VideoPlayerProcessManager? _videoPlayerManager;
    private DispatcherTimer? _videoPlayerHealthTimer;
    private int _consecutivePingFailures;
    private int _isRestarting;
    private const int MaxPingFailuresBeforeRestart = 3; // 3 failures × 2s = 6 seconds

    private void InitializeVideoPlayerMonitoring()
    {
        _videoPlayerHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _videoPlayerHealthTimer.Tick += OnVideoPlayerHealthCheck;
    }

    private async void OnVideoPlayerHealthCheck(object? sender, EventArgs e)
    {
        var manager = _videoPlayerManager;
        if (manager is null || Volatile.Read(ref _isRestarting) != 0)
            return;

        try
        {
            // Check if process is still running
            if (!manager.IsProcessRunning)
            {
                if (!ReferenceEquals(_videoPlayerManager, manager))
                    return;

                Logger.Error($"[Video] Player process exited (code: {manager.ExitCode?.ToString() ?? "unknown"})", null);
                await RestartVideoPlayerAsync();
                return;
            }

            // Send ping
            var pingSuccessful = await manager.PingAsync();
            if (!ReferenceEquals(_videoPlayerManager, manager))
                return;

            if (pingSuccessful)
            {
                _consecutivePingFailures = 0;
            }
            else
            {
                _consecutivePingFailures++;
                AppendLog($"[Video] Ping failed ({_consecutivePingFailures}/{MaxPingFailuresBeforeRestart})");

                if (_consecutivePingFailures >= MaxPingFailuresBeforeRestart)
                {
                    AppendLog("[Video] Process unresponsive - force restarting...");
                    Logger.Warning("VideoPlayerMonitoring", $"Process unresponsive after {_consecutivePingFailures} ping failures");
                    await RestartVideoPlayerAsync();
                }
            }

            // Also get status update to keep position synchronized
            if (pingSuccessful && _consecutivePingFailures == 0)
            {
                var status = await manager.GetStatusAsync();
                if (!ReferenceEquals(_videoPlayerManager, manager))
                    return;

                if (status is not null)
                {
                    if (string.IsNullOrWhiteSpace(status.CurrentFilePath)
                        && !string.IsNullOrWhiteSpace(_currentFilePath)
                        && _videoPlaybackStartLock.CurrentCount > 0)
                    {
                        _currentFilePath = null;
                        _isPaused = false;
                        _positionMs = 0;
                        _duration = TimeSpan.Zero;
                        StopPositionClock(0);
                        _historyTimer.Stop();
                        AppendLog("[Video] Playback stopped from the fullscreen player");
                    }

                    // Update position tracking
                    _positionMs = (long)(status.PositionSeconds * 1000);
                    _duration = TimeSpan.FromSeconds(Math.Max(0, status.DurationSeconds));
                }
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_videoPlayerManager, manager))
                return;

            Logger.Error("[Video] Health check error", ex);
        }
    }

    private async System.Threading.Tasks.Task RestartVideoPlayerAsync()
    {
        if (Interlocked.Exchange(ref _isRestarting, 1) != 0)
            return;

        try
        {
            var manager = _videoPlayerManager;
            if (manager is null)
                return;

            await _videoPlaybackStartLock.WaitAsync();
            try
            {
                if (!ReferenceEquals(_videoPlayerManager, manager))
                    return;

                _consecutivePingFailures = 0;
                var restarted = await manager.RestartAndResumeAsync();
                if (restarted)
                {
                    AppendLog("[Video] Process restarted and playback resumed");
                    Logger.Info("[Video] Process successfully restarted");
                    UpdateIsolatedPlayerNavigationTitles();
                }
                else
                {
                    Logger.Error("[Video] Failed to restart process", null);
                }
            }
            finally
            {
                _videoPlaybackStartLock.Release();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[Video] Restart error", ex);
        }
        finally
        {
            Volatile.Write(ref _isRestarting, 0);
        }
    }

    private void StartVideoPlayerHealthMonitoring()
    {
        if (_videoPlayerHealthTimer is not null && !_videoPlayerHealthTimer.IsEnabled)
        {
            _videoPlayerHealthTimer.Start();
            Logger.Info("[Video] Health monitoring started");
        }
    }

    private void StopVideoPlayerHealthMonitoring()
    {
        if (_videoPlayerHealthTimer is not null && _videoPlayerHealthTimer.IsEnabled)
        {
            _videoPlayerHealthTimer.Stop();
            Logger.Info("[Video] Health monitoring stopped");
        }
    }
}
