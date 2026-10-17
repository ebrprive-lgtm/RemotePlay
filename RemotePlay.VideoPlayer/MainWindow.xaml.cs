using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Forms;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using RemotePlay.Helpers;
using RemotePlay.VideoPlayer.Ipc;

namespace RemotePlay.VideoPlayer;

/// <summary>
/// Fullscreen video player window that runs in isolation from the main RemotePlay app
/// </summary>
public partial class MainWindow : Window
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const double KeyboardSeekStepSeconds = 10;
    private const double PlaybackControlsMaxWidth = 1400;
    private static readonly nint HwndTopmost = new(-1);

    private readonly string _instanceId;
    private readonly int? _startupDisplayIndex;
    private IpcServer? _ipcServer;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private VideoView? _videoView;           // created in code to avoid XAML parse-time VLC crash
    private PlayCommand? _pendingPlayCommand;
    private PlayCommand? _activePlayCommand;
    private bool _isPaused;
    private bool _playbackStopRequested;
    private string? _currentFilePath;
    private string _lastPlaybackError = string.Empty;
    private string _lastVlcErrorMessage = string.Empty;
    private string? _previousVideoTitle;
    private string? _nextVideoTitle;
    private double _volume = 1.0;
    private double _lastNonZeroVolume = 1.0;
    private double _brightness = 0.5;
    private double _saturation = 1.0;
    private double _sharpness;
    private double _zoom = 1.0;
    private double _audioBoost = 1.0;
    private double _playbackSpeed = 1.0;
    private bool _subtitlesEnabled = true;
    private int? _lastSubtitleTrackId;
    private int? _lastAudioTrackId;
    private bool _hasSubtitles;
    private bool _isSeeking;
    private double? _pendingSeekSeconds;
    private long _pendingSeekStartedAt;
    private long _pendingSeekExpiresAt;
    private long _pendingSeekFallbackAt;
    private long _pendingSeekConfirmAfter;
    private long _pendingSeekLastConfirmSampleAt;
    private int _pendingSeekConfirmSamples;
    private bool _pendingSeekPositionFallbackAttempted;
    private bool _isTrackClickSeek;
    private bool _isUpdatingVolumeSlider;
    private bool _showRemainingTime = true;
    private bool _fullscreenControlsEnabled = FullscreenControlsSettings.EnabledByDefault;
    private double _fullscreenControlsAutoHideDelaySeconds = FullscreenControlsSettings.DefaultAutoHideDelaySeconds;
    private bool _isFadingOutControls;
    private bool _controlsInitialized;
    private int _controlsVisibilityVersion;
    private bool _hasLastPointerPosition;
    private NativePoint _lastPointerPosition;
    private readonly DispatcherTimer _pointerTrackingTimer;
    private readonly DispatcherTimer _controlsAutoHideTimer;
    private readonly DispatcherTimer _playbackProgressTimer;

    public MainWindow()
    {
        InitializeComponent();
        _controlsInitialized = true;
        PlaybackControlsPopup.PlacementTarget = VideoHost;
        PlaybackControlsPopup.CustomPopupPlacementCallback = PlacePlaybackControlsPopup;
        PlaybackControls.PreviewKeyDown += PlaybackControls_PreviewKeyDown;
        ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
        SeekSlider.AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(SeekSlider_PreviewMouseLeftButtonDown),
            handledEventsToo: true);
        SeekSlider.AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(SeekSlider_PreviewMouseLeftButtonUp),
            handledEventsToo: true);
        UpdateVolumeControls();
        UpdatePlaybackToggleButton();

        _pointerTrackingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pointerTrackingTimer.Tick += PointerTrackingTimer_Tick;
        _controlsAutoHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_fullscreenControlsAutoHideDelaySeconds)
        };
        _controlsAutoHideTimer.Tick += ControlsAutoHideTimer_Tick;
        _playbackProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _playbackProgressTimer.Tick += PlaybackProgressTimer_Tick;

        // Get instance ID from command line arguments
        var args = Environment.GetCommandLineArgs();
        _instanceId = args.Length > 1 ? args[1] : Guid.NewGuid().ToString();
        _startupDisplayIndex = args.Length > 2 && int.TryParse(args[2], out var displayIndex)
            ? displayIndex
            : null;
        SourceInitialized += Window_SourceInitialized;

        PlayerDiagnostics.Info($"Main window created for instance {_instanceId}");
        Console.WriteLine($"[VideoPlayer] Starting with instance ID: {_instanceId}");
    }

    private static CustomPopupPlacement[] PlacePlaybackControlsPopup(
        System.Windows.Size popupSize,
        System.Windows.Size targetSize,
        System.Windows.Point offset)
    {
        var x = Math.Max(0, (targetSize.Width - popupSize.Width) / 2);
        var y = Math.Max(0, targetSize.Height - popupSize.Height - 64);
        return new[]
        {
            new CustomPopupPlacement(new System.Windows.Point(x, y), PopupPrimaryAxis.None)
        };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (_startupDisplayIndex is not { } displayIndex)
        {
            Console.WriteLine("[VideoPlayer] [1/7] No display arg — using default position");
            return;
        }

        Console.WriteLine($"[VideoPlayer] [1/7] Positioning on display {displayIndex} before first presentation");
        PositionOnDisplay(displayIndex);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GetCursorPos(out var initialPointerPosition))
            {
                _lastPointerPosition = initialPointerPosition;
                _hasLastPointerPosition = true;
            }
            _pointerTrackingTimer.Start();

            PlayerDiagnostics.Info("Window loaded; starting IPC server and LibVLC initialization");
            Console.WriteLine($"[VideoPlayer] [2/7] Window_Loaded — instance: {_instanceId}");

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
                vlc.Log += OnLibVlcLog;
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
            _mediaPlayer.Paused           += OnMediaPaused;

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
                QueuePlayCommand(pending);
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
            if (screens.Length == 0)
                throw new InvalidOperationException("No display detected.");

            var targetIndex = displayIndex;
            if (targetIndex < 0 || targetIndex >= screens.Length)
            {
                var primaryDeviceName = Screen.PrimaryScreen?.DeviceName;
                targetIndex = Array.FindIndex(screens, screen =>
                    string.Equals(screen.DeviceName, primaryDeviceName, StringComparison.OrdinalIgnoreCase));
                if (targetIndex < 0)
                    targetIndex = 0;
                PlayerDiagnostics.Info($"Requested display {displayIndex} is unavailable; using display {targetIndex}");
            }

            var bounds = screens[targetIndex].Bounds;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowState = WindowState.Normal;

            if (!SetWindowPos(
                    new WindowInteropHelper(this).Handle,
                    HwndTopmost,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    SwpNoActivate | SwpFrameChanged))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            PlayerDiagnostics.Info($"Positioned fullscreen on display {targetIndex}: {bounds}");
            Console.WriteLine($"[VideoPlayer] Positioned on display {targetIndex}: {bounds}");
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error("Failed to position fullscreen player window", ex);
            Console.WriteLine($"[VideoPlayer] Failed to position on display: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        HandlePlaybackKeyDown(e);
    }

    private void PlaybackControls_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        ShowPlaybackControls();
        HandlePlaybackKeyDown(e);
    }

    private void HandlePlaybackKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Handled)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = HandlePlayerShortcut(key, e.IsRepeat);
    }

    private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled
            || (msg.message != WmKeyDown && msg.message != WmSysKeyDown)
            || !IsActivePlayerMessage(msg.hwnd))
        {
            return;
        }

        var key = KeyInterop.KeyFromVirtualKey(msg.wParam.ToInt32());
        var isRepeat = (msg.lParam.ToInt64() & 0x40000000L) != 0;
        handled = HandlePlayerShortcut(key, isRepeat);
    }

    private bool IsActivePlayerMessage(nint messageWindow)
    {
        if (messageWindow == nint.Zero)
            return false;

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == nint.Zero)
            return false;

        _ = GetWindowThreadProcessId(messageWindow, out var messageProcessId);
        _ = GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        var playerProcessId = (uint)Environment.ProcessId;
        return messageProcessId == playerProcessId && foregroundProcessId == playerProcessId;
    }

    private bool HandlePlayerShortcut(Key key, bool isRepeat)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return false;

        if (key == Key.Escape)
        {
            PlayerDiagnostics.Info("Escape pressed; stopping playback and returning to the QR screen");
            StopPlayback();
            HidePlaybackControls(animate: false);
            SetPlayerCursorVisible(true);
            WindowState = WindowState.Minimized;
            _ = SendMainApplicationCommandAsync(
                RemotePlayCommandPipe.FullscreenPlayerEscapeArgument,
                "return to the fullscreen QR screen");
            return true;
        }

        if (key == Key.Space)
        {
            if (IsPlaybackControlButtonFocused())
                return false;

            return isRepeat || TogglePlaybackPause();
        }

        if (VolumeSlider.IsKeyboardFocusWithin)
            return false;

        return key switch
        {
            Key.Left => SeekBySeconds(-KeyboardSeekStepSeconds),
            Key.Right => SeekBySeconds(KeyboardSeekStepSeconds),
            _ => false
        };
    }

    private bool IsPlaybackControlButtonFocused() =>
        PreviousVideoButton.IsKeyboardFocusWithin
        || PausePlaybackButton.IsKeyboardFocusWithin
        || NextVideoButton.IsKeyboardFocusWithin
        || StopPlaybackButton.IsKeyboardFocusWithin
        || MuteVolumeButton.IsKeyboardFocusWithin
        || PlaybackTimeButton.IsKeyboardFocusWithin;

    private bool TogglePlaybackPause()
    {
        if (_mediaPlayer is null || string.IsNullOrWhiteSpace(_currentFilePath))
            return false;

        SetPlaybackPaused(!_isPaused);
        return true;
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (GetCursorPos(out var position))
            HandlePointerPosition(position);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        HidePlaybackControls();
        SetPlayerCursorVisible(true);
    }

    private void PointerTrackingTimer_Tick(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HidePlaybackControls();
            SetPlayerCursorVisible(true);
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero
            && GetCursorPos(out var position)
            && GetWindowRect(handle, out var bounds))
        {
            HandlePointerPosition(position, bounds);
        }
    }

    private void HandlePointerPosition(NativePoint position)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !GetWindowRect(handle, out var bounds))
            return;

        HandlePointerPosition(position, bounds);
    }

    private void HandlePointerPosition(NativePoint position, NativeRect bounds)
    {
        // VLC renders in a child HWND, so WPF mouse events alone miss some pointer movement.
        var moved = !_hasLastPointerPosition
            || position.X != _lastPointerPosition.X
            || position.Y != _lastPointerPosition.Y;
        _lastPointerPosition = position;
        _hasLastPointerPosition = true;

        var insideWindow = position.X >= bounds.Left
            && position.X < bounds.Right
            && position.Y >= bounds.Top
            && position.Y < bounds.Bottom;

        if (!insideWindow && !_isSeeking)
        {
            HidePlaybackControls();
            SetPlayerCursorVisible(true);
            return;
        }

        if (insideWindow && moved)
            ShowPlaybackControls();
    }

    private void ShowPlaybackControls()
    {
        SetPlayerCursorVisible(true);
        if (string.IsNullOrWhiteSpace(_currentFilePath) || _mediaPlayer is null)
            return;

        if (!_fullscreenControlsEnabled)
        {
            HidePlaybackControls(animate: false);
            ResetControlsAutoHideTimer();
            return;
        }

        PlaybackControlsPopup.Width = Math.Min(PlaybackControlsMaxWidth, Math.Max(0, VideoHost.ActualWidth - 64));
        VolumeSlider.Width = Math.Clamp(PlaybackControlsPopup.Width * 0.12, 100, 180);
        var wasOpen = PlaybackControlsPopup.IsOpen && PlaybackControls.Visibility == Visibility.Visible;
        var shouldFadeIn = !wasOpen || _isFadingOutControls;
        var startingOpacity = wasOpen ? PlaybackControls.Opacity : 0;
        if (shouldFadeIn)
        {
            _controlsVisibilityVersion++;
            _isFadingOutControls = false;
            PlaybackControls.BeginAnimation(UIElement.OpacityProperty, null);
            PlaybackControls.Opacity = startingOpacity;
        }

        PlaybackControls.Visibility = Visibility.Visible;
        PlaybackControlsPopup.IsOpen = true;
        if (shouldFadeIn)
        {
            PlaybackControls.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(startingOpacity, 1, TimeSpan.FromMilliseconds(180)));
        }

        UpdateVolumeControls();
        UpdatePlaybackControlsProgress();
        ResetControlsAutoHideTimer();
    }

    private void ResetControlsAutoHideTimer()
    {
        _controlsAutoHideTimer.Stop();
        if (!_isSeeking)
            _controlsAutoHideTimer.Start();
    }

    private void HidePlaybackControls(bool animate = true)
    {
        _controlsAutoHideTimer.Stop();
        if (PlaybackControls.IsKeyboardFocusWithin)
            Keyboard.ClearFocus();

        if (!animate)
        {
            _controlsVisibilityVersion++;
            _isFadingOutControls = false;
            PlaybackControls.BeginAnimation(UIElement.OpacityProperty, null);
            PlaybackControls.Opacity = 1;
            PlaybackControlsPopup.IsOpen = false;
            PlaybackControls.Visibility = Visibility.Collapsed;
            return;
        }

        if (!PlaybackControlsPopup.IsOpen || _isFadingOutControls)
            return;

        _isFadingOutControls = true;
        var transitionVersion = ++_controlsVisibilityVersion;
        var fadeOut = new DoubleAnimation(
            PlaybackControls.Opacity,
            0,
            TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) =>
        {
            if (transitionVersion != _controlsVisibilityVersion)
                return;

            _isFadingOutControls = false;
            PlaybackControlsPopup.IsOpen = false;
            PlaybackControls.Visibility = Visibility.Collapsed;
            PlaybackControls.BeginAnimation(UIElement.OpacityProperty, null);
            PlaybackControls.Opacity = 1;
        };
        PlaybackControls.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void ControlsAutoHideTimer_Tick(object? sender, EventArgs e)
    {
        if (_isSeeking)
        {
            _controlsAutoHideTimer.Start();
            return;
        }

        HidePlaybackControls();
        SetPlayerCursorVisible(WindowState == WindowState.Minimized || !IsPointerInsideWindow());
    }

    private bool IsPointerInsideWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle != nint.Zero
            && GetCursorPos(out var position)
            && GetWindowRect(handle, out var bounds)
            && position.X >= bounds.Left
            && position.X < bounds.Right
            && position.Y >= bounds.Top
            && position.Y < bounds.Bottom;
    }

    private void SetPlayerCursorVisible(bool visible)
    {
        Cursor = visible
            ? System.Windows.Input.Cursors.Arrow
            : System.Windows.Input.Cursors.None;
        if (_videoView is not null)
            _videoView.Cursor = Cursor;
    }

    private void PlaybackProgressTimer_Tick(object? sender, EventArgs e) =>
        UpdatePlaybackControlsProgress();

    private void UpdatePlaybackControlsProgress()
    {
        var duration = GetDurationSeconds();
        if (duration <= 0)
        {
            SeekSlider.IsEnabled = false;
            SeekSlider.Maximum = 1;
            if (!_isSeeking)
                SeekSlider.Value = 0;
            PlaybackTimeText.Text = FormatPlaybackTimePair(0, duration);
            UpdatePlaybackToggleButton();
            return;
        }

        SeekSlider.Maximum = duration;
        SeekSlider.IsEnabled = !string.IsNullOrWhiteSpace(_currentFilePath);

        var position = Math.Clamp(GetPositionSeconds(), 0, duration);
        var displayPosition = position;
        if (!_isSeeking && _pendingSeekSeconds is double pendingSeek)
        {
            var now = Environment.TickCount64;
            var expectedPosition = pendingSeek;
            if (!_isPaused && _mediaPlayer?.IsPlaying == true)
            {
                var elapsedSinceSeekSeconds = Math.Max(0, now - _pendingSeekStartedAt) / 1000.0;
                expectedPosition = Math.Clamp(
                    pendingSeek + elapsedSinceSeekSeconds * _playbackSpeed,
                    0,
                    duration);
            }

            var positionIsNearExpected = Math.Abs(position - expectedPosition) <= 1.5;
            if (now < _pendingSeekConfirmAfter || !positionIsNearExpected)
            {
                _pendingSeekConfirmSamples = 0;
                _pendingSeekLastConfirmSampleAt = 0;
            }
            else if (now - _pendingSeekLastConfirmSampleAt >= 150)
            {
                _pendingSeekConfirmSamples++;
                _pendingSeekLastConfirmSampleAt = now;
            }

            var seekReached = _pendingSeekConfirmSamples >= 2;
            var seekTimedOut = now >= _pendingSeekExpiresAt;
            if (seekReached || seekTimedOut)
            {
                if (seekReached)
                    PlayerDiagnostics.Info($"Seek confirmed at {position:0.##}s for target {pendingSeek:0.##}s");
                else
                    PlayerDiagnostics.Info($"Seek to {pendingSeek:0.##}s was not confirmed; player remains at {position:0.##}s");
                _pendingSeekSeconds = null;
                _pendingSeekStartedAt = 0;
                _pendingSeekPositionFallbackAttempted = false;
                _pendingSeekConfirmSamples = 0;
                _pendingSeekLastConfirmSampleAt = 0;
            }
            else
            {
                if (!_pendingSeekPositionFallbackAttempted
                    && now >= _pendingSeekFallbackAt
                    && !positionIsNearExpected)
                {
                    _pendingSeekPositionFallbackAttempted = true;
                    _pendingSeekStartedAt = now;
                    _pendingSeekConfirmAfter = now + 600;
                    _pendingSeekConfirmSamples = 0;
                    _pendingSeekLastConfirmSampleAt = 0;
                    if (_mediaPlayer is not null)
                    {
                        try
                        {
                            _mediaPlayer.Position = (float)Math.Clamp(pendingSeek / duration, 0, 1);
                            PlayerDiagnostics.Info(
                                $"Retrying seek to {pendingSeek:0.##}s through LibVLC normalized position; observed={position:0.##}s, expected={expectedPosition:0.##}s");
                        }
                        catch (Exception ex)
                        {
                            PlayerDiagnostics.Error("Normalized-position seek failed", ex);
                        }
                    }
                }

                displayPosition = pendingSeek;
            }
        }

        if (!_isSeeking)
            SeekSlider.Value = displayPosition;

        PlaybackTimeText.Text = FormatPlaybackTimePair(_isSeeking ? SeekSlider.Value : displayPosition, duration);
        UpdatePlaybackToggleButton();
    }

    private static string FormatPlaybackTime(double seconds)
    {
        var totalSeconds = Math.Max(0, (long)Math.Floor(seconds));
        var hours = totalSeconds / 3600;
        return hours > 0
            ? $"{hours}:{totalSeconds % 3600 / 60:00}:{totalSeconds % 60:00}"
            : $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private string FormatPlaybackTimePair(double position, double duration)
    {
        if (duration <= 0)
            return "00:00 / --:--";

        var clampedPosition = Math.Clamp(position, 0, duration);
        var otherTime = _showRemainingTime
            ? $"-{FormatPlaybackTime(Math.Max(0, duration - clampedPosition))}"
            : FormatPlaybackTime(duration);
        return $"{FormatPlaybackTime(clampedPosition)} / {otherTime}";
    }

    private void PlaybackTimeButton_Click(object sender, RoutedEventArgs e)
    {
        _showRemainingTime = !_showRemainingTime;
        PlaybackTimeButton.ToolTip = _showRemainingTime
            ? "Click to show total duration"
            : "Click to show time remaining";
        AutomationProperties.SetName(
            PlaybackTimeButton,
            _showRemainingTime ? "Show total duration" : "Show time remaining");
        UpdatePlaybackControlsProgress();
        ShowPlaybackControls();
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (GetDurationSeconds() <= 0 || string.IsNullOrWhiteSpace(_currentFilePath))
            return;

        _isSeeking = true;
        _controlsAutoHideTimer.Stop();

        _isTrackClickSeek = false;
        if (SeekSlider.Template.FindName("PART_Track", SeekSlider) is Track track
            && track.Thumb is { IsMouseOver: false })
        {
            var point = e.GetPosition(track);
            var thumbWidth = track.Thumb.ActualWidth;
            var usableWidth = track.ActualWidth - thumbWidth;
            var fraction = usableWidth > 0
                ? (point.X - thumbWidth / 2) / usableWidth
                : 0;
            if (track.IsDirectionReversed)
                fraction = 1 - fraction;
            fraction = Math.Clamp(fraction, 0, 1);
            SeekSlider.Value = SeekSlider.Minimum + fraction * (SeekSlider.Maximum - SeekSlider.Minimum);
            _isTrackClickSeek = true;
            PlayerDiagnostics.Info($"Seek track click: x={point.X:0.##}/{track.ActualWidth:0.##}, thumb={thumbWidth:0.##}, fraction={fraction:0.####}, value={SeekSlider.Value:0.##}s");
            e.Handled = true;
            QueueSeekCommit();
        }

        ShowPlaybackControls();
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeeking)
            return;

        QueueSeekCommit();
    }

    private void QueueSeekCommit()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isSeeking)
                return;

            var targetSeconds = SeekSlider.Value;
            var source = _isTrackClickSeek ? "track click" : "thumb drag";
            CommitSeek(targetSeconds, source);
        }, DispatcherPriority.Input);
    }

    private bool SeekBySeconds(double offsetSeconds)
    {
        var duration = GetDurationSeconds();
        if (duration <= 0 || string.IsNullOrWhiteSpace(_currentFilePath))
            return false;

        var currentPosition = _pendingSeekSeconds ?? GetPositionSeconds();
        CommitSeek(Math.Clamp(currentPosition + offsetSeconds, 0, duration), "keyboard");
        return true;
    }

    private void CommitSeek(double targetSeconds, string source)
    {
        var duration = GetDurationSeconds();
        if (duration <= 0 || string.IsNullOrWhiteSpace(_currentFilePath))
            return;

        targetSeconds = Math.Clamp(targetSeconds, 0, duration);
        _isSeeking = false;
        PlayerDiagnostics.Info($"Committing {source} seek at {targetSeconds:0.##}s");
        _isTrackClickSeek = false;
        _pendingSeekSeconds = targetSeconds;
        var now = Environment.TickCount64;
        _pendingSeekStartedAt = now;
        _pendingSeekFallbackAt = now + 750;
        _pendingSeekConfirmAfter = now + 600;
        _pendingSeekExpiresAt = now + 7000;
        _pendingSeekConfirmSamples = 0;
        _pendingSeekLastConfirmSampleAt = 0;
        _pendingSeekPositionFallbackAttempted = false;
        SeekToSeconds(targetSeconds);
        UpdatePlaybackControlsProgress();
        ShowPlaybackControls();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
            PlaybackTimeText.Text = FormatPlaybackTimePair(SeekSlider.Value, GetDurationSeconds());
    }

    private void PausePlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlaybackPause();
    }

    private async void PreviousVideoButton_Click(object sender, RoutedEventArgs e)
    {
        await RequestAdjacentVideoAsync(RemotePlayCommandPipe.FullscreenPlayerPreviousArgument);
    }

    private async void NextVideoButton_Click(object sender, RoutedEventArgs e)
    {
        await RequestAdjacentVideoAsync(RemotePlayCommandPipe.FullscreenPlayerNextArgument);
    }

    private Task RequestAdjacentVideoAsync(string commandArgument)
    {
        ShowPlaybackControls();
        return SendMainApplicationCommandAsync(commandArgument, "adjacent video playback");
    }

    private async Task SendMainApplicationCommandAsync(string commandArgument, string requestDescription)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                RemotePlayCommandPipe.Name,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(1000);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(commandArgument);
        }
        catch (Exception ex) when (
            ex is IOException
            or TimeoutException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            PlayerDiagnostics.Error($"Could not request {requestDescription} from the main app", ex);
        }
    }

    private void UpdateAdjacentVideoButtons(string? previousTitle, string? nextTitle)
    {
        _previousVideoTitle = string.IsNullOrWhiteSpace(previousTitle) ? null : previousTitle;
        _nextVideoTitle = string.IsNullOrWhiteSpace(nextTitle) ? null : nextTitle;

        PreviousVideoButton.IsEnabled = _previousVideoTitle is not null;
        PreviousVideoButton.ToolTip = _previousVideoTitle ?? "No previous title";
        AutomationProperties.SetName(
            PreviousVideoButton,
            _previousVideoTitle is null ? "Previous video" : $"Previous video: {_previousVideoTitle}");

        NextVideoButton.IsEnabled = _nextVideoTitle is not null;
        NextVideoButton.ToolTip = _nextVideoTitle ?? "No next title";
        AutomationProperties.SetName(
            NextVideoButton,
            _nextVideoTitle is null ? "Next video" : $"Next video: {_nextVideoTitle}");
    }

    private void SetPlaybackPaused(bool paused)
    {
        if (_mediaPlayer is null || string.IsNullOrWhiteSpace(_currentFilePath))
            return;

        if (_isPaused == paused)
        {
            UpdatePlaybackToggleButton();
            return;
        }

        try
        {
            if (paused)
                _mediaPlayer.Pause();
            else
                _mediaPlayer.Play();

            _isPaused = paused;
            PlayerDiagnostics.Info(paused ? "Playback paused from controls" : "Playback resumed from controls");
            UpdatePlaybackToggleButton();
            ShowPlaybackControls();
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error(paused ? "Playback pause failed" : "Playback resume failed", ex);
        }
    }

    private void UpdatePlaybackToggleButton()
    {
        if (!_controlsInitialized)
            return;

        PausePlaybackButton.IsEnabled = _mediaPlayer is not null && !string.IsNullOrWhiteSpace(_currentFilePath);
        PausePlaybackButton.Content = _isPaused ? "\uE768" : "\uE769";
        PausePlaybackButton.ToolTip = _isPaused ? "Resume playback (Space)" : "Pause playback (Space)";
        AutomationProperties.SetName(PausePlaybackButton, _isPaused ? "Resume playback" : "Pause playback");
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUpdatingVolumeSlider)
            SetPlaybackVolume(e.NewValue);
    }

    private void MuteVolumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_volume <= 0)
            SetPlaybackVolume(_lastNonZeroVolume > 0 ? _lastNonZeroVolume : 1);
        else
            SetPlaybackVolume(0);

        ShowPlaybackControls();
    }

    private void SetPlaybackVolume(double volume)
    {
        _volume = volume;
        if (_volume > 0)
            _lastNonZeroVolume = _volume;

        try
        {
            ApplyAudioLevel();
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error("Setting playback volume failed", ex);
        }

        UpdateVolumeControls();
    }

    private void UpdateVolumeControls()
    {
        if (!_controlsInitialized)
            return;

        _isUpdatingVolumeSlider = true;
        try
        {
            VolumeSlider.Value = Math.Clamp(_volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
            var isMuted = _volume <= 0;
            MuteVolumeButton.Content = isMuted ? "\uE74F" : "\uE767";
            MuteVolumeButton.ToolTip = isMuted ? "Unmute audio" : "Mute audio";
            AutomationProperties.SetName(MuteVolumeButton, isMuted ? "Unmute audio" : "Mute audio");
        }
        finally
        {
            _isUpdatingVolumeSlider = false;
        }
    }

    private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StopPlayback();
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error("Stop button failed", ex);
        }
    }

    private void StopPlayback()
    {
        System.Threading.Volatile.Write(ref _playbackStopRequested, true);
        System.Threading.Volatile.Write(ref _lastPlaybackError, string.Empty);
        System.Threading.Volatile.Write(ref _lastVlcErrorMessage, string.Empty);
        _mediaPlayer?.Stop();
        _currentFilePath = null;
        _activePlayCommand = null;
        _pendingPlayCommand = null;
        _isPaused = false;
        UpdateAdjacentVideoButtons(null, null);
        _hasSubtitles = false;
        _isSeeking = false;
        _pendingSeekSeconds = null;
        _pendingSeekStartedAt = 0;
        _pendingSeekPositionFallbackAttempted = false;
        _pendingSeekConfirmSamples = 0;
        _pendingSeekLastConfirmSampleAt = 0;
        _playbackProgressTimer.Stop();
        HidePlaybackControls(animate: false);
        SetPlayerCursorVisible(true);
        UpdatePlaybackControlsProgress();
    }

    private void RestoreWindowForPlayback()
    {
        if (WindowState != WindowState.Minimized)
            return;

        PlayerDiagnostics.Info("Restoring minimized player window for playback");
        WindowState = WindowState.Maximized;
        Activate();
    }

    private void QueuePlayCommand(PlayCommand cmd)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RestoreWindowForPlayback();
            PositionOnDisplay(cmd.TargetDisplayIndex);
            UpdateLayout();
            VideoHost.UpdateLayout();
            _videoView?.UpdateLayout();
            UpdateVolumeControls();
            UpdatePlaybackToggleButton();
            ExecutePlay(cmd);
        }, DispatcherPriority.ApplicationIdle);
    }

    private async Task<IpcMessage?> HandleIpcMessageAsync(IpcMessage message)
    {
        try
        {
            PlayerDiagnostics.Info($"IPC received {message.MessageType} ({message.MessageId})");
            Console.WriteLine($"[VideoPlayer] Received: {message.MessageType}");

            if (message is PingCommand ping)
            {
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send).Task.ConfigureAwait(false);
                return new PongResponse
                {
                    MessageType = nameof(PongResponse),
                    RequestId = ping.MessageId
                };
            }

            // Status reads the current playback fields; playback commands dispatch work to the
            // UI thread via BeginInvoke and return an ACK immediately so the pipe loop stays unblocked.
            IpcMessage response = message switch
            {
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
                    PlayerState = _mediaPlayer?.State.ToString() ?? "Unknown",
                    LastError = System.Threading.Volatile.Read(ref _lastPlaybackError)
                },

                PlayCommand play => DispatchPlay(play),
                BringToFrontCommand bring => DispatchAction(() =>
                {
                    RestoreWindowForPlayback();
                    PositionOnDisplay(bring.TargetDisplayIndex);
                    Activate();
                }, message.MessageId),
                PauseCommand => DispatchAction(() => SetPlaybackPaused(true), message.MessageId),
                ResumeCommand => DispatchAction(() => SetPlaybackPaused(false), message.MessageId),
                StopCommand => DispatchAction(StopPlayback, message.MessageId),
                SeekCommand seek => DispatchAction(() => ApplySeek(seek), message.MessageId),
                SetVolumeCommand vol => DispatchAction(() => SetPlaybackVolume(vol.Volume), message.MessageId),
                SetFullscreenControlsSettingsCommand controls => DispatchFullscreenControlsSettings(controls),
                SetAdjacentVideoTitlesCommand titles => DispatchAdjacentVideoTitles(titles),
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

            return response;
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error($"IPC command {message.MessageType} failed", ex);
            Console.WriteLine($"[VideoPlayer] Command error: {ex}");
            return new AcknowledgmentResponse
            {
                MessageType = nameof(AcknowledgmentResponse),
                RequestId = message.MessageId,
                Success = false,
                ErrorMessage = ex.Message
            };
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

    private AcknowledgmentResponse DispatchFullscreenControlsSettings(SetFullscreenControlsSettingsCommand command)
    {
        if (!FullscreenControlsSettings.IsValidAutoHideDelaySeconds(command.AutoHideDelaySeconds))
        {
            var errorMessage =
                $"Fullscreen controls auto-hide delay must be between {FullscreenControlsSettings.MinimumAutoHideDelaySeconds} and {FullscreenControlsSettings.MaximumAutoHideDelaySeconds} seconds.";
            PlayerDiagnostics.Error(
                "Rejected fullscreen controls settings",
                new ArgumentOutOfRangeException(nameof(command.AutoHideDelaySeconds), command.AutoHideDelaySeconds, errorMessage));
            return new AcknowledgmentResponse
            {
                MessageType = nameof(AcknowledgmentResponse),
                RequestId = command.MessageId,
                Success = false,
                ErrorMessage = errorMessage
            };
        }

        return DispatchAction(
            () => ApplyFullscreenControlsSettings(command.Enabled, command.AutoHideDelaySeconds),
            command.MessageId);
    }

    private AcknowledgmentResponse DispatchAdjacentVideoTitles(SetAdjacentVideoTitlesCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.FilePath))
        {
            const string errorMessage = "Adjacent video titles require the current file path.";
            PlayerDiagnostics.Error(errorMessage, new ArgumentException(errorMessage, nameof(command.FilePath)));
            return new AcknowledgmentResponse
            {
                MessageType = nameof(AcknowledgmentResponse),
                RequestId = command.MessageId,
                Success = false,
                ErrorMessage = errorMessage
            };
        }

        return DispatchAction(() =>
        {
            if (!string.Equals(_currentFilePath, command.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                PlayerDiagnostics.Info($"Ignored stale adjacent video titles for {command.FilePath}");
                return;
            }

            UpdateAdjacentVideoButtons(command.PreviousTitle, command.NextTitle);
        }, command.MessageId);
    }

    private void ApplyFullscreenControlsSettings(bool enabled, double autoHideDelaySeconds)
    {
        _fullscreenControlsEnabled = enabled;
        _fullscreenControlsAutoHideDelaySeconds = autoHideDelaySeconds;
        _controlsAutoHideTimer.Interval = TimeSpan.FromSeconds(autoHideDelaySeconds);
        PlayerDiagnostics.Info(
            $"Fullscreen controls settings updated: enabled={enabled}; autoHide={autoHideDelaySeconds:0.##}s");

        if (_mediaPlayer is not null && !string.IsNullOrWhiteSpace(_currentFilePath))
            ShowPlaybackControls();
        else if (!enabled)
            HidePlaybackControls(animate: false);
    }

    private AcknowledgmentResponse DispatchPlay(PlayCommand cmd)
    {
        PlayerDiagnostics.Info($"Play requested: {cmd.FilePath}; fileExists={File.Exists(cmd.FilePath)}; mediaPlayerReady={_mediaPlayer is not null}");
        Console.WriteLine($"[VideoPlayer] DispatchPlay: {Path.GetFileName(cmd.FilePath)}; mediaPlayerReady={_mediaPlayer is not null}");

        // Store state fields immediately (thread-safe value types / strings)
        System.Threading.Volatile.Write(ref _playbackStopRequested, false);
        _currentFilePath = cmd.FilePath;
        System.Threading.Volatile.Write(ref _lastPlaybackError, string.Empty);
        System.Threading.Volatile.Write(ref _lastVlcErrorMessage, string.Empty);
        Dispatcher.BeginInvoke(
            () => UpdateAdjacentVideoButtons(null, null),
            DispatcherPriority.Send);
        _volume = cmd.Volume;
        if (_volume > 0)
            _lastNonZeroVolume = _volume;
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
        _pendingSeekSeconds = null;
        _pendingSeekStartedAt = 0;
        _pendingSeekPositionFallbackAttempted = false;
        _pendingSeekConfirmSamples = 0;
        _pendingSeekLastConfirmSampleAt = 0;
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
            QueuePlayCommand(cmd);
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
        if (System.Threading.Volatile.Read(ref _playbackStopRequested))
        {
            PlayerDiagnostics.Info("Ignoring queued playback after an explicit stop");
            return;
        }

        try
        {
            PlayerDiagnostics.Info($"Executing play: {cmd.FilePath}; fileExists={File.Exists(cmd.FilePath)}; videoViewLoaded={_videoView?.IsLoaded}");
            Console.WriteLine($"[VideoPlayer] ExecutePlay: {Path.GetFileName(cmd.FilePath)} pos={cmd.StartPositionSeconds:0.#}s");
            Console.WriteLine($"[VideoPlayer] VideoView HWND ready: {_videoView?.IsLoaded}");

            System.Threading.Volatile.Write(ref _lastPlaybackError, string.Empty);
            System.Threading.Volatile.Write(ref _lastVlcErrorMessage, string.Empty);
            var mediaPlayer = _mediaPlayer ?? throw new InvalidOperationException("LibVLC MediaPlayer is not initialized.");
            var libVlc = _libVlc ?? throw new InvalidOperationException("LibVLC is not initialized.");
            PlayerDiagnostics.Info("Stopping previous media before playback");
            mediaPlayer.Stop();
            PlayerDiagnostics.Info("Previous media stopped");

            using var media = new Media(libVlc, new Uri(cmd.FilePath, UriKind.Absolute));
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

            // Set scale before VLC renders its first frame to avoid a native-size top-left flash.
            ApplyVideoZoom();
            PlayerDiagnostics.Info("Calling LibVLC Play");
            if (!mediaPlayer.Play(media))
                throw new InvalidOperationException("LibVLC rejected the media for playback.");
            _playbackProgressTimer.Start();
            _controlsAutoHideTimer.Stop();
            _controlsAutoHideTimer.Start();
            PlayerDiagnostics.Info("LibVLC Play call completed; awaiting playback events");
            _mediaPlayer?.SetRate((float)_playbackSpeed);
            ApplyAudioLevel();
            ApplyVideoAdjustments();

            if (cmd.StartPositionSeconds > 0)
            {
                // Seek after a short delay so VLC is already playing when the target time is applied.
                Task.Delay(800).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                {
                    SeekToSeconds(cmd.StartPositionSeconds);
                }));
            }

            Console.WriteLine($"[VideoPlayer] Playing: {Path.GetFileName(cmd.FilePath)}");
        }
        catch (Exception ex)
        {
            var detail = $"Playback start failed ({ex.GetType().Name}): {ex.Message}";
            System.Threading.Volatile.Write(ref _lastPlaybackError, detail);
            PlayerDiagnostics.Error($"Play execution failed for {cmd.FilePath}", ex);
            Console.WriteLine($"[VideoPlayer] ExecutePlay error: {detail}");
        }
    }

    private void ApplySeek(SeekCommand cmd)
    {
        SeekToSeconds(cmd.PositionSeconds);
    }

    private void SeekToSeconds(double positionSeconds)
    {
        var duration = GetDurationSeconds();
        if (_mediaPlayer is not null && duration > 0 && double.IsFinite(positionSeconds))
        {
            var targetMilliseconds = (long)(Math.Clamp(positionSeconds, 0, duration) * 1000);
            PlayerDiagnostics.Info($"Seek request: target={targetMilliseconds}ms, current={_mediaPlayer.Time}ms, duration={_mediaPlayer.Length}ms, seekable={_mediaPlayer.IsSeekable}");
            try
            {
                _mediaPlayer.Time = targetMilliseconds;
            }
            catch (Exception ex)
            {
                PlayerDiagnostics.Error("Timestamp seek failed", ex);
            }
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

        var adjustments = VideoAdjustmentMapper.Map(_brightness, _saturation);
        var requiresAdjustment = VideoAdjustmentMapper.RequiresAdjustment(adjustments);
        _mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, requiresAdjustment ? 1 : 0);
        if (!requiresAdjustment)
            return;

        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, adjustments.Brightness);
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Contrast, adjustments.Contrast);
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Gamma, adjustments.Gamma);
        _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Saturation, adjustments.Saturation);
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

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        if (e.Level != LogLevel.Error) return;

        var message = e.Message?.Trim();
        if (string.IsNullOrWhiteSpace(message)) return;

        var module = string.IsNullOrWhiteSpace(e.Module) ? "unknown" : e.Module.Trim();
        var detail = $"LibVLC [{module}]: {message}";
        if (!System.Threading.Volatile.Read(ref _playbackStopRequested))
            System.Threading.Volatile.Write(ref _lastVlcErrorMessage, detail);
        PlayerDiagnostics.Error(detail);
        Console.WriteLine($"[VideoPlayer] {detail}");
    }

    private void OnMediaPlaying(object? sender, EventArgs e)
    {
        if (System.Threading.Volatile.Read(ref _playbackStopRequested))
        {
            PlayerDiagnostics.Info("Ignoring late Playing event after an explicit stop");
            return;
        }

        System.Threading.Volatile.Write(ref _lastPlaybackError, string.Empty);
        System.Threading.Volatile.Write(ref _lastVlcErrorMessage, string.Empty);
        PlayerDiagnostics.Info("LibVLC event: Playing");
        Console.WriteLine("[VideoPlayer] Media playing");
        Dispatcher.BeginInvoke(() =>
        {
            _isPaused = false;
            UpdatePlaybackToggleButton();
            if (_activePlayCommand is { } command)
                PositionOnDisplay(command.TargetDisplayIndex);
            ApplyVideoAdjustments();
            ApplyVideoZoom();
            UpdatePlaybackControlsProgress();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        PlayerDiagnostics.Info("LibVLC event: EndReached");
        Console.WriteLine("[VideoPlayer] Media ended");
        Dispatcher.BeginInvoke(() =>
        {
            _isPaused = true;
            _playbackProgressTimer.Stop();
            UpdatePlaybackToggleButton();
            HidePlaybackControls();
            SetPlayerCursorVisible(true);
        });
    }

    private void OnMediaFailed(object? sender, EventArgs e)
    {
        if (System.Threading.Volatile.Read(ref _playbackStopRequested))
        {
            PlayerDiagnostics.Info("Ignoring playback error reported after an explicit stop");
            return;
        }

        var detail = System.Threading.Volatile.Read(ref _lastVlcErrorMessage);
        if (string.IsNullOrWhiteSpace(detail))
            detail = $"LibVLC reported EncounteredError (state={_mediaPlayer?.State}); no native error detail was emitted.";

        System.Threading.Volatile.Write(ref _lastPlaybackError, detail);
        PlayerDiagnostics.Error($"Media playback failed: {detail}");
        Console.WriteLine($"[VideoPlayer] Media FAILED: {detail}");
        Dispatcher.BeginInvoke(() =>
        {
            _isPaused = true;
            _playbackProgressTimer.Stop();
            UpdatePlaybackToggleButton();
            HidePlaybackControls();
            SetPlayerCursorVisible(true);
        });
    }

    private void OnMediaPaused(object? sender, EventArgs e)
    {
        PlayerDiagnostics.Info("LibVLC event: Paused");
        Dispatcher.BeginInvoke(() =>
        {
            _isPaused = true;
            UpdatePlaybackToggleButton();
        });
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

        ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;
        _pointerTrackingTimer.Stop();
        _controlsAutoHideTimer.Stop();
        _playbackProgressTimer.Stop();
        base.OnClosed(e);

        _ipcServer?.Dispose();
        _mediaPlayer?.Dispose();
        _videoView?.Dispose();
        _libVlc?.Dispose();

        Console.WriteLine("[VideoPlayer] Shutdown complete");
    }
}
