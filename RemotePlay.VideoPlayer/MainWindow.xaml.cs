using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using RemotePlay.VideoPlayer.Ipc;

namespace RemotePlay.VideoPlayer;

/// <summary>
/// Fullscreen video player window that runs in isolation from the main RemotePlay app
/// </summary>
public partial class MainWindow : Window
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private readonly string _instanceId;
    private IpcServer? _ipcServer;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private VideoView? _videoView;           // created in code to avoid XAML parse-time VLC crash
    private PlayCommand? _pendingPlayCommand;
    private PlayCommand? _activePlayCommand;
    private bool _isPaused;
    private string? _currentFilePath;
    private double _volume = 1.0;
    private double _brightness;
    private double _saturation = 1.0;
    private double _sharpness;
    private double _zoom = 1.0;
    private double _audioBoost = 1.0;
    private double _playbackSpeed = 1.0;
    private bool _subtitlesEnabled = true;
    private int? _lastSubtitleTrackId;
    private int? _lastAudioTrackId;
    private bool _hasSubtitles;

    public MainWindow()
    {
        InitializeComponent();

        // Get instance ID from command line arguments
        var args = Environment.GetCommandLineArgs();
        _instanceId = args.Length > 1 ? args[1] : Guid.NewGuid().ToString();

        PlayerDiagnostics.Info($"Main window created for instance {_instanceId}");
        Console.WriteLine($"[VideoPlayer] Starting with instance ID: {_instanceId}");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            PlayerDiagnostics.Info("Window loaded; starting IPC server and LibVLC initialization");
            Console.WriteLine($"[VideoPlayer] [1/7] Window_Loaded — instance: {_instanceId}");

            // Position on the correct monitor
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 2 && int.TryParse(args[2], out var displayIndex))
            {
                Console.WriteLine($"[VideoPlayer] [2/7] Positioning on display {displayIndex}");
                PositionOnDisplay(displayIndex);
                Console.WriteLine($"[VideoPlayer] [2/7] Positioned — Left={Left:F0} Top={Top:F0} Width={Width:F0} Height={Height:F0}");
            }
            else
            {
                Console.WriteLine("[VideoPlayer] [2/7] No display arg — using default position");
            }

            // Start the IPC server FIRST so the main app can connect while LibVLC loads.
            Console.WriteLine("[VideoPlayer] [3/7] Starting IPC server...");
            _ipcServer = new IpcServer(_instanceId, HandleIpcMessageAsync);
            _ipcServer.Start();
            PlayerDiagnostics.Info($"IPC server started: {_ipcServer.PipeName}");
            Console.WriteLine($"[VideoPlayer] [3/7] IPC server listening on pipe: {_ipcServer.PipeName}");

            // Initialize LibVLC off the UI thread — loading native VLC DLLs can take
            // several seconds and would freeze the window if done synchronously.
            Console.WriteLine("[VideoPlayer] [4/7] Starting LibVLC init on background thread...");
            var (libVlc, mediaPlayer) = await Task.Run(() =>
            {
                Console.WriteLine("[VideoPlayer] [4a] Core.Initialize()...");
                Core.Initialize();
                Console.WriteLine("[VideoPlayer] [4b] new LibVLC()...");
                var vlc = new LibVLC();
                PlayerDiagnostics.Info($"LibVLC created. Version: {vlc.Version}");
                Console.WriteLine($"[VideoPlayer] [4c] LibVLC created — version: {vlc.Version}");
                var mp = new MediaPlayer(vlc);
                Console.WriteLine("[VideoPlayer] [4d] MediaPlayer created — background init complete");
                return (vlc, mp);
            });

            Console.WriteLine("[VideoPlayer] [5/7] Back on UI thread — creating VideoView...");
            _libVlc      = libVlc;
            _mediaPlayer = mediaPlayer;

            // Add to visual tree first, then assign MediaPlayer so the HWND exists when VLC attaches.
            _videoView = new VideoView
            {
                Background = System.Windows.Media.Brushes.Black,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch
            };
            Console.WriteLine("[VideoPlayer] [5/7] Adding VideoView to visual tree...");
            VideoHost.Children.Add(_videoView);
            Console.WriteLine("[VideoPlayer] [5/7] Assigning MediaPlayer to VideoView...");
            _videoView.MediaPlayer = _mediaPlayer;
            PlayerDiagnostics.Info($"VideoView ready. IsLoaded={_videoView.IsLoaded}");
            Console.WriteLine("[VideoPlayer] [5/7] VideoView ready");

            // Hook up VLC events
            Console.WriteLine("[VideoPlayer] [6/7] Hooking VLC events...");
            _mediaPlayer.Playing          += OnMediaPlaying;
            _mediaPlayer.EndReached       += OnMediaEnded;
            _mediaPlayer.EncounteredError += OnMediaFailed;
            _mediaPlayer.LengthChanged    += OnMediaLengthChanged;
            _mediaPlayer.TimeChanged      += OnMediaTimeChanged;
            _mediaPlayer.ESAdded          += OnESAdded;
            _mediaPlayer.Opening          += (_, _) => PlayerDiagnostics.Info("LibVLC event: Opening");
            _mediaPlayer.Buffering        += (_, e) => Console.WriteLine($"[VideoPlayer] VLC state: Buffering {e.Cache:0}%");
            _mediaPlayer.Stopped          += (_, _) => PlayerDiagnostics.Info("LibVLC event: Stopped");
            _mediaPlayer.Paused           += (_, _) => PlayerDiagnostics.Info("LibVLC event: Paused");

            PlayerDiagnostics.Info($"Player initialization complete. Pending playback={_pendingPlayCommand is not null}");
            Console.WriteLine($"[VideoPlayer] [7/7] Fully initialized and ready (pendingPlay={_pendingPlayCommand?.FilePath ?? "none"})");

            // If a PlayCommand arrived before LibVLC was ready, execute it now.
            // Use DispatcherPriority.Loaded (6) so the call is deferred until AFTER the
            // Render pass (priority 7) has created the VideoView's native HWND.
            // Calling ExecutePlay synchronously here (or at Normal priority) would race the
            // layout pass and leave VLC with no rendering surface, causing a black screen.
            if (_pendingPlayCommand is not null)
            {
                var pending = _pendingPlayCommand;
                _pendingPlayCommand = null;
                Console.WriteLine($"[VideoPlayer] Scheduling buffered PlayCommand (after HWND ready): {pending.FilePath}");
                Dispatcher.BeginInvoke(() => ExecutePlay(pending), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error("Player initialization failed", ex);
            Console.WriteLine($"[VideoPlayer] INIT FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[VideoPlayer] Stack trace: {ex.StackTrace}");
            if (ex.InnerException is not null)
                Console.WriteLine($"[VideoPlayer] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            System.Windows.Application.Current.Shutdown(1);
        }
    }

    private void PositionOnDisplay(int displayIndex)
    {
        try
        {
            var screens = Screen.AllScreens;
            if (displayIndex >= 0 && displayIndex < screens.Length)
            {
                var screen = screens[displayIndex];
                var bounds = screen.Bounds;

                WindowState = WindowState.Normal;
                SetWindowPos(
                    new WindowInteropHelper(this).Handle,
                    nint.Zero,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

                Console.WriteLine($"[VideoPlayer] Positioned on display {displayIndex}: {bounds}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VideoPlayer] Failed to position on display: {ex.Message}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            PlayerDiagnostics.Info("Escape pressed; minimizing player window");
            WindowState = WindowState.Minimized;
            e.Handled = true;
        }
    }

    private void RestoreWindowForPlayback()
    {
        if (WindowState != WindowState.Minimized)
            return;

        PlayerDiagnostics.Info("Restoring minimized player window for playback");
        WindowState = WindowState.Maximized;
        Activate();
    }

    private Task<IpcMessage?> HandleIpcMessageAsync(IpcMessage message)
    {
        try
        {
            PlayerDiagnostics.Info($"IPC received {message.MessageType} ({message.MessageId})");
            Console.WriteLine($"[VideoPlayer] Received: {message.MessageType}");

            // Ping and Status need to return data synchronously — they only read fields.
            // All playback control commands dispatch work to the UI thread via BeginInvoke
            // (fire-and-forget) and return an ACK immediately so the pipe loop stays unblocked.
            IpcMessage response = message switch
            {
                PingCommand ping => new PongResponse
                {
                    MessageType = nameof(PongResponse),
                    RequestId = ping.MessageId
                },

                StatusRequestCommand status => new StatusResponse
                {
                    MessageType = nameof(StatusResponse),
                    RequestId = status.MessageId,
                    IsPlaying = _mediaPlayer?.IsPlaying ?? false,
                    IsPaused = _isPaused,
                    PositionSeconds = GetPositionSeconds(),
                    DurationSeconds = GetDurationSeconds(),
                    CurrentFilePath = _currentFilePath,
                    HasSubtitles = _hasSubtitles,
                    CurrentAudioTrack = _lastAudioTrackId,
                    CurrentSubtitleTrack = _lastSubtitleTrackId,
                    PlayerState = _mediaPlayer?.State.ToString() ?? "Unknown"
                },

                PlayCommand play => DispatchPlay(play),
                PauseCommand => DispatchAction(() => { _mediaPlayer?.Pause(); _isPaused = true; }, message.MessageId),
                ResumeCommand => DispatchAction(() => { _mediaPlayer?.Play(); _isPaused = false; }, message.MessageId),
                StopCommand => DispatchAction(() => { _mediaPlayer?.Stop(); _currentFilePath = null; _isPaused = false; }, message.MessageId),
                SeekCommand seek => DispatchAction(() => ApplySeek(seek), message.MessageId),
                SetVolumeCommand vol => DispatchAction(() => { _volume = vol.Volume; ApplyAudioLevel(); }, message.MessageId),
                SetBrightnessCommand bright => DispatchAction(() => { _brightness = bright.Brightness; ApplyVideoAdjustments(); }, message.MessageId),
                SetSaturationCommand sat => DispatchAction(() => { _saturation = sat.Saturation; ApplyVideoAdjustments(); }, message.MessageId),
                SetSharpnessCommand sharp => DispatchAction(() =>
                {
                    _sharpness = sharp.Sharpness;
                    ReloadCurrentMediaForSharpness();
                }, message.MessageId),
                SetZoomCommand zoom => DispatchAction(() => { _zoom = zoom.Zoom; ApplyVideoZoom(); }, message.MessageId),
                SetAudioBoostCommand boost => DispatchAction(() => { _audioBoost = boost.AudioBoost; ApplyAudioLevel(); }, message.MessageId),
                SetPlaybackSpeedCommand speed => DispatchAction(() => { _playbackSpeed = speed.PlaybackSpeed; _mediaPlayer?.SetRate((float)_playbackSpeed); }, message.MessageId),
                SetSubtitlesEnabledCommand subs => DispatchAction(() => { _subtitlesEnabled = subs.Enabled; _mediaPlayer?.SetSpu(subs.Enabled ? 0 : -1); }, message.MessageId),
                SetAudioTrackCommand audio => DispatchAction(() => { _lastAudioTrackId = audio.TrackId; _mediaPlayer?.SetAudioTrack(audio.TrackId); }, message.MessageId),
                SetSubtitleTrackCommand subtitle => DispatchAction(() => { _lastSubtitleTrackId = subtitle.TrackId; _mediaPlayer?.SetSpu(subtitle.TrackId); }, message.MessageId),
                ShutdownCommand => DispatchAction(() => System.Windows.Application.Current.Shutdown(), message.MessageId),

                _ => new AcknowledgmentResponse
                {
                    MessageType = nameof(AcknowledgmentResponse),
                    RequestId = message.MessageId,
                    Success = false,
                    ErrorMessage = $"Unknown command: {message.MessageType}"
                }
            };

            return Task.FromResult<IpcMessage?>(response);
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error($"IPC command {message.MessageType} failed", ex);
            Console.WriteLine($"[VideoPlayer] Command error: {ex}");
            return Task.FromResult<IpcMessage?>(new AcknowledgmentResponse
            {
                MessageType = nameof(AcknowledgmentResponse),
                RequestId = message.MessageId,
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    /// <summary>
    /// Queues an action on the UI thread (fire-and-forget) and returns an ACK immediately.
    /// This keeps the IPC pipe loop responsive and prevents Dispatcher deadlocks.
    /// </summary>
    private AcknowledgmentResponse DispatchAction(Action action, Guid requestId = default)
    {
        Dispatcher.BeginInvoke(action);
        return new AcknowledgmentResponse
        {
            MessageType = nameof(AcknowledgmentResponse),
            RequestId = requestId,
            Success = true
        };
    }

    private AcknowledgmentResponse DispatchPlay(PlayCommand cmd)
    {
        PlayerDiagnostics.Info($"Play requested: {cmd.FilePath}; fileExists={File.Exists(cmd.FilePath)}; mediaPlayerReady={_mediaPlayer is not null}");
        Console.WriteLine($"[VideoPlayer] DispatchPlay: {Path.GetFileName(cmd.FilePath)}; mediaPlayerReady={_mediaPlayer is not null}");

        Dispatcher.BeginInvoke(RestoreWindowForPlayback);

        // Store state fields immediately (thread-safe value types / strings)
        _currentFilePath = cmd.FilePath;
        _volume = cmd.Volume;
        _brightness = cmd.Brightness;
        _saturation = cmd.Saturation;
        _sharpness = cmd.Sharpness;
        _zoom = cmd.Zoom;
        _audioBoost = cmd.AudioBoost;
        _playbackSpeed = cmd.PlaybackSpeed;
        _subtitlesEnabled = cmd.SubtitlesEnabled;
        _lastAudioTrackId = cmd.PreferredAudioTrack;
        _lastSubtitleTrackId = cmd.PreferredSubtitleTrack;
        _isPaused = false;
        _activePlayCommand = cmd;

        // All LibVLC operations must run on the UI thread.
        // BeginInvoke = fire-and-forget; ACK is returned to the main app immediately.
        // If LibVLC isn't ready yet (still initializing), buffer the command — it will
        // be executed by Window_Loaded once initialization completes.
        if (_mediaPlayer is null)
        {
            PlayerDiagnostics.Info("Play request buffered while LibVLC initializes");
            Console.WriteLine("[VideoPlayer] LibVLC not ready yet — buffering PlayCommand");
            _pendingPlayCommand = cmd;
        }
        else
        {
            PlayerDiagnostics.Info("Play request queued to the UI dispatcher");
            // Defer to DispatcherPriority.Loaded (6) so the call runs AFTER the Render pass
            // (priority 7) has created the VideoView HWND. Normal priority (9) would race it.
            Console.WriteLine($"[VideoPlayer] Dispatching ExecutePlay (HWND-safe): {Path.GetFileName(cmd.FilePath)}");
            Dispatcher.BeginInvoke(() => ExecutePlay(cmd), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        return new AcknowledgmentResponse
        {
            MessageType = nameof(AcknowledgmentResponse),
            RequestId = cmd.MessageId,
            Success = true
        };
    }

    private void ReloadCurrentMediaForSharpness()
    {
        if (_activePlayCommand is not { } command)
            return;

        var restartCommand = command with
        {
            Sharpness = _sharpness,
            StartPositionSeconds = GetPositionSeconds()
        };

        _activePlayCommand = restartCommand;
        ExecutePlay(restartCommand);
    }

    private void ExecutePlay(PlayCommand cmd)
    {
        try
        {
            PlayerDiagnostics.Info($"Executing play: {cmd.FilePath}; fileExists={File.Exists(cmd.FilePath)}; videoViewLoaded={_videoView?.IsLoaded}");
            Console.WriteLine($"[VideoPlayer] ExecutePlay: {Path.GetFileName(cmd.FilePath)} pos={cmd.StartPositionSeconds:0.#}s");
            Console.WriteLine($"[VideoPlayer] VideoView HWND ready: {_videoView?.IsLoaded}");

            _mediaPlayer?.Stop();

            using var media = new Media(_libVlc!, new Uri(cmd.FilePath, UriKind.Absolute));
            media.AddOption(":avcodec-hw=none");

            if (_sharpness > 0)
            {
                var sigma = 0.5 + (_sharpness * 5.0);
                media.AddOption(":video-filter=sharpen");
                media.AddOption($":sharpen-sigma={sigma.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            if (cmd.ForceSwAudio)
            {
                media.AddOption(":audio-resampler=soxr");
                media.AddOption(":no-spdif");
            }

            if (!string.IsNullOrEmpty(cmd.SubtitleFilePath) && File.Exists(cmd.SubtitleFilePath))
            {
                media.AddOption($":sub-file={cmd.SubtitleFilePath}");
                media.AddOption(_subtitlesEnabled ? ":sub-track=0" : ":sub-track=-1");
                _hasSubtitles = true;
            }
            else
            {
                _hasSubtitles = TryAttachSubtitle(media, cmd.FilePath);
            }

            _mediaPlayer?.Play(media);
            PlayerDiagnostics.Info("LibVLC Play call completed; awaiting playback events");
            _mediaPlayer?.SetRate((float)_playbackSpeed);
            ApplyAudioLevel();
            ApplyVideoAdjustments();

            if (cmd.StartPositionSeconds > 0)
            {
                // Seek after a short delay — media must be playing before Position is writable
                Task.Delay(800).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                {
                    var duration = GetDurationSeconds();
                    if (duration > 0)
                        _mediaPlayer!.Position = (float)Math.Clamp(cmd.StartPositionSeconds / duration, 0, 1);
                }));
            }

            Console.WriteLine($"[VideoPlayer] Playing: {Path.GetFileName(cmd.FilePath)}");
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error($"Play execution failed for {cmd.FilePath}", ex);
            Console.WriteLine($"[VideoPlayer] ExecutePlay error: {ex}");
        }
    }

    private void ApplySeek(SeekCommand cmd)
    {
        var duration = GetDurationSeconds();
        if (duration > 0)
        {
            var pos = (float)Math.Clamp(cmd.PositionSeconds / duration, 0, 1);
            _mediaPlayer!.Position = pos;
        }
    }

    private bool TryAttachSubtitle(Media media, string videoPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(videoPath);
            if (string.IsNullOrEmpty(dir))
                return false;

            var nameWithoutExt = Path.GetFileNameWithoutExtension(videoPath);
            var subtitleExtensions = new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" };

            foreach (var ext in subtitleExtensions)
            {
                var subtitlePath = Path.Combine(dir, nameWithoutExt + ext);
                if (File.Exists(subtitlePath))
                {
                    media.AddOption($":sub-file={subtitlePath}");
                    media.AddOption(_subtitlesEnabled ? ":sub-track=0" : ":sub-track=-1");
                    Console.WriteLine($"[VideoPlayer] Attached subtitle: {Path.GetFileName(subtitlePath)}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VideoPlayer] Subtitle attach error: {ex.Message}");
        }

        return false;
    }

    private void ApplyAudioLevel()
    {
        if (_mediaPlayer is null)
            return;

        var finalVolume = Math.Clamp(_volume * _audioBoost, 0, 2);
        _mediaPlayer.Volume = (int)(finalVolume * 100);
    }

    private void ApplyVideoAdjustments()
    {
        if (_mediaPlayer is null)
            return;

        _mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, 1);
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, (float)_brightness);
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Saturation, (float)_saturation);
    }

    private void ApplyVideoZoom()
    {
        if (_mediaPlayer is null)
            return;

        _mediaPlayer.Scale = Math.Abs(_zoom - 1) < 0.0001 ? 0 : (float)_zoom;
    }

    private double GetPositionSeconds()
    {
        if (_mediaPlayer is null)
            return 0;

        var time = _mediaPlayer.Time;
        return time >= 0 ? time / 1000.0 : 0;
    }

    private double GetDurationSeconds()
    {
        if (_mediaPlayer is null)
            return 0;

        var length = _mediaPlayer.Length;
        return length >= 0 ? length / 1000.0 : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LibVLC Event Handlers
    // ═══════════════════════════════════════════════════════════════════════════

    private void OnMediaPlaying(object? sender, EventArgs e)
    {
        PlayerDiagnostics.Info("LibVLC event: Playing");
        Console.WriteLine("[VideoPlayer] Media playing");
        Dispatcher.BeginInvoke(ApplyVideoZoom, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        PlayerDiagnostics.Info("LibVLC event: EndReached");
        Console.WriteLine("[VideoPlayer] Media ended");
    }

    private void OnMediaFailed(object? sender, EventArgs e)
    {
        PlayerDiagnostics.Info($"LibVLC event: EncounteredError; state={_mediaPlayer?.State}");
        Console.WriteLine($"[VideoPlayer] Media FAILED (VLC state={_mediaPlayer?.State})");
    }

    private void OnMediaLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        PlayerDiagnostics.Info($"LibVLC event: LengthChanged; duration={e.Length / 1000.0:0.##}s");
        Console.WriteLine($"[VideoPlayer] Duration: {e.Length / 1000.0:0.##}s");
    }

    private void OnMediaTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // Silent - too frequent
    }

    private void OnESAdded(object? sender, MediaPlayerESAddedEventArgs e)
    {
        if (e.Type == TrackType.Text)
        {
            _hasSubtitles = true;
            PlayerDiagnostics.Info("LibVLC event: text subtitle track detected");
            Console.WriteLine("[VideoPlayer] Subtitles detected");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Console.WriteLine("[VideoPlayer] OnClosed triggered");
        Console.WriteLine($"[VideoPlayer] OnClosed stack trace:\n{Environment.StackTrace}");

        base.OnClosed(e);

        _ipcServer?.Dispose();
        _mediaPlayer?.Dispose();
        _videoView?.Dispose();
        _libVlc?.Dispose();

        Console.WriteLine("[VideoPlayer] Shutdown complete");
    }
}
