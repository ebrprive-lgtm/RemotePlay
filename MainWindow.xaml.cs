using System.IO;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using RemotePlay.Helpers;
using RemotePlay.Models;
using RemotePlay.Services.Discovery;
using System.Diagnostics.CodeAnalysis;

namespace RemotePlay;

[ExcludeFromCodeCoverage]
public partial class MainWindow : Window
{
    private readonly Abstractions.Services.IAppConfigService _appConfigService;
    private readonly Abstractions.Services.IAppConfigFactory _appConfigFactory;
    private readonly Abstractions.Services.ISettingsValidationService _settingsValidationService;
    private readonly Abstractions.Services.ISettingsApplyService _settingsApplyService;
    private AppConfig _config;
    private readonly PlaybackHistory _playbackHistory = new();
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly RemotePlay.Services.MusicPlayer _musicPlayer;
    private readonly RemotePlay.Services.RadioPlayer _radioPlayer;
    private readonly RemotePlay.Services.RadioBrowserClient _radioBrowser;
    private WebServer? _webServer;
    private PresenceBroadcaster? _broadcaster;
    private RemotePlay.Services.AppUpdater? _appUpdater;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;
    private double _windowedLeft = double.NaN;
    private double _windowedTop  = double.NaN;
    private double _windowedWidth  = double.NaN;
    private double _windowedHeight = double.NaN;
    private readonly DispatcherTimer _bannerTimer;
    private readonly DispatcherTimer _overlayTimer;
    private readonly DispatcherTimer _historyTimer;
    private readonly DispatcherTimer _fullscreenWatchdogTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _miniPreviewTimer;
    private readonly string _miniPreviewSnapshotPath =
        Path.Combine(Path.GetTempPath(), "remoteplay_preview.png");
    private bool _isPaused;
    private bool _isVideoMode;
    private string? _currentFilePath;
    private TimeSpan? _pendingResumePosition;
    private bool _smartResumeApplied;
    private string _lastPlaybackError = string.Empty;
    private double _brightness;
    private double _saturation = 1;
    private double _zoom = 1;
    private double _volume = 1;
    private double _audioBoost = 1;
    private double _playbackSpeed = 1;
    private bool _subtitlesEnabled = true;
    private int? _lastSubtitleTrackId;
    private int? _lastAudioTrackId;
    private bool _hasSubtitles;
    private bool _preferredSubtitleApplied;
    private bool _forceSwAudio;
    private TimeSpan _duration = TimeSpan.Zero;
    private bool _isApplyingMoviePreferences;
    private CancellationTokenSource? _videoTransitionCts;
    private MediaCodecInfo? _codecInfo;
    private bool _serverReady;
    private readonly List<string> _playbackQueue = [];
    private string? _serverUrl;
    private static readonly LanguageOption[] LanguageOptions =
    [
        new("", "Default / none"),
        new("eng", "English"),
        new("nld", "Dutch"),
        new("fra", "French"),
        new("deu", "German"),
        new("spa", "Spanish"),
        new("ita", "Italian"),
        new("por", "Portuguese"),
        new("jpn", "Japanese"),
        new("kor", "Korean"),
        new("zho", "Chinese"),
        new("rus", "Russian"),
        new("ara", "Arabic"),
        new("hin", "Hindi"),
        new("tur", "Turkish"),
        new("pol", "Polish"),
        new("swe", "Swedish"),
        new("nor", "Norwegian"),
        new("dan", "Danish"),
        new("fin", "Finnish"),
        new("ell", "Greek"),
        new("heb", "Hebrew"),
        new("ces", "Czech"),
        new("hun", "Hungarian"),
        new("ron", "Romanian"),
        new("ukr", "Ukrainian"),
        new("tha", "Thai"),
        new("vie", "Vietnamese"),
        new("ind", "Indonesian"),
        new("msa", "Malay")
    ];
    private sealed record LanguageOption(string Code, string Name);

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv" };
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub"];

    internal MainWindow(
        Abstractions.Services.IAppConfigService appConfigService,
        Abstractions.Services.IAppConfigFactory appConfigFactory,
        Abstractions.Services.ISettingsValidationService settingsValidationService,
        Abstractions.Services.ISettingsApplyService settingsApplyService,
        ViewModels.MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(appConfigService);
        ArgumentNullException.ThrowIfNull(appConfigFactory);
        ArgumentNullException.ThrowIfNull(settingsValidationService);
        ArgumentNullException.ThrowIfNull(settingsApplyService);
        ArgumentNullException.ThrowIfNull(viewModel);

        _appConfigService = appConfigService;
        _appConfigFactory = appConfigFactory;
        _settingsValidationService = settingsValidationService;
        _settingsApplyService = settingsApplyService;

        try
        {
            Logger.Info("MainWindow constructor started");
            InitializeComponent();
            DataContext = viewModel;

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Logger.Error("Unhandled exception", e.ExceptionObject as Exception);
                AppendLog($"[CRASH] {e.ExceptionObject}");
            };

            _config = _appConfigService.Load();
            _volume = Math.Clamp(_config.Volume, 0, 1);
            _brightness = 0.5;
            _saturation = 1;
            _zoom = Math.Clamp(_config.Zoom, 1, 2);
            _audioBoost = Math.Clamp(_config.AudioBoost, 1, 2);
            _playbackSpeed = Math.Clamp(_config.PlaybackSpeed, 0.5, 2);
            _subtitlesEnabled = _config.SubtitlesEnabled;

            Core.Initialize();
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc);
            VideoPlayer.MediaPlayer = _mediaPlayer;
            _musicPlayer = new RemotePlay.Services.MusicPlayer();
            _musicPlayer.SetDevice(_config.MusicAudioDeviceId);
            _radioPlayer  = new RemotePlay.Services.RadioPlayer();
            _radioPlayer.SetDevice(_config.MusicAudioDeviceId);
            _radioBrowser = new RemotePlay.Services.RadioBrowserClient(AppPaths.RadioFavoritesFile);
            _ = _radioBrowser.ResolveServerAsync();

            _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _bannerTimer.Tick += (_, _) => HideBanner();

            _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _overlayTimer.Tick += (_, _) => HideVideoOverlay();

            _historyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _historyTimer.Tick += (_, _) => SaveCurrentPlaybackPosition();

            _fullscreenWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _fullscreenWatchdogTimer.Tick += (_, _) => EnsureFullscreenWindowBounds();

            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _statsTimer.Tick += (_, _) => RefreshStatusStats();

            _miniPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.1) };
            _miniPreviewTimer.Tick += (_, _) => UpdateMiniPreview();

            _mediaPlayer.Playing += OnMediaPlaying;
            _mediaPlayer.EndReached += OnMediaEnded;
            _mediaPlayer.EncounteredError += OnMediaFailed;
            _mediaPlayer.LengthChanged += OnMediaLengthChanged;
            _mediaPlayer.ESAdded += OnESAdded;
            ApplyAudioLevel();
            ApplyBrightnessOverlay();

            Loaded += OnLoaded;
            Closing += OnClosing;
            Activated += OnWindowActivated;

            Logger.Info("MainWindow constructor completed");
        }
        catch (Exception ex)
        {
            Logger.Error("MainWindow constructor failed", ex);
            throw;
        }

        RefreshCurrentDisplayText();
    }

    private static void PopulateDisplayCombo(System.Windows.Controls.ComboBox combo, int savedIdx)
    {
        combo.Items.Clear();
        combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Primary screen (default)", Tag = -1 });
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var label = $"Screen {i + 1}  ({s.Bounds.Width}x{s.Bounds.Height}){(s.Primary ? "  [Primary]" : "")}";
            combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = i });
        }
        foreach (System.Windows.Controls.ComboBoxItem item in combo.Items)
            if (item.Tag is int tag && tag == savedIdx)
                combo.SelectedItem = item;
        if (combo.SelectedItem is null)
            combo.SelectedIndex = 0;
    }

    private void PopulateMusicAudioDeviceCombo(string savedDeviceId)
    {
        MusicAudioDeviceCombo.Items.Clear();
        foreach (var (num, name) in Services.MusicPlayer.EnumerateDevices())
            MusicAudioDeviceCombo.Items.Add(new MusicAudioDevice(num, name));

        // Select saved device
        foreach (MusicAudioDevice item in MusicAudioDeviceCombo.Items)
        {
            if (item.DeviceNumber == -1 && string.IsNullOrWhiteSpace(savedDeviceId)) { MusicAudioDeviceCombo.SelectedItem = item; break; }
            if (!string.IsNullOrWhiteSpace(savedDeviceId) && item.Name.Contains(savedDeviceId, StringComparison.OrdinalIgnoreCase)) { MusicAudioDeviceCombo.SelectedItem = item; break; }
        }
        if (MusicAudioDeviceCombo.SelectedItem is null && MusicAudioDeviceCombo.Items.Count > 0)
            MusicAudioDeviceCombo.SelectedIndex = 0;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshCurrentDisplayText();
            if (FindName("PreferredDisplayCombo") is System.Windows.Controls.ComboBox combo)
                PopulateDisplayCombo(combo, _config.PreferredDisplayIndex);
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version;
        Title = version is not null
            ? $"RemotePlay v{version.Major}.{version.Minor}.{version.Build}"
            : "RemotePlay";

        Logger.Clear();
        LogBox.Clear();
        InitializeTrayIcon();
        ApplyWindowsAutostart(_config.StartWithWindows);

        UpdateUpdateReadiness();

        MoviesPathText.Text = $"Movies folder: {_config.ResolvedMoviesPath}";

        MoviesFolderBox.Text = _config.ResolvedMoviesPath;
        MusicFolderBox.Text = _config.ResolvedMusicPath;
        PortBox.Text = _config.Port.ToString();
        if (FindName("InstanceNameBox") is System.Windows.Controls.TextBox instanceNameBox)
            instanceNameBox.Text = _config.InstanceName;
        if (FindName("UseHttpsBox") is System.Windows.Controls.CheckBox useHttpsBox)
            useHttpsBox.IsChecked = _config.UseHttps;
        PopulateLanguageCombo("PreferredAudioLangCombo", _config.PreferredAudioLanguage);
        PopulateLanguageCombo("PreferredSubtitleLangCombo", _config.PreferredSubtitleLanguage);
        PopulateLanguageCombo("SecondarySubtitleLangCombo", _config.SecondarySubtitleLanguage);
        if (FindName("PreferForcedSubtitlesBox") is System.Windows.Controls.CheckBox forcedBox)
            forcedBox.IsChecked = _config.PreferForcedSubtitles;
        if (FindName("PlaybackEndBehaviorCombo") is System.Windows.Controls.ComboBox endCombo)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in endCombo.Items)
                if (item.Tag?.ToString() == _config.PlaybackEndBehavior.ToString())
                    endCombo.SelectedItem = item;
            if (endCombo.SelectedItem is null && endCombo.Items.Count > 0)
                endCombo.SelectedIndex = 0;
        }
        if (FindName("PlaybackHistoryLimitBox") is System.Windows.Controls.TextBox historyLimitBox)
            historyLimitBox.Text = _config.PlaybackHistoryLimit.ToString();
        if (FindName("AutoRescanIntervalMinutesBox") is System.Windows.Controls.TextBox rescanIntervalBox)
            rescanIntervalBox.Text = _config.LibraryRescanDelayMinutes.ToString();
        if (FindName("UpdateSourcePathBox") is System.Windows.Controls.TextBox updateSourcePathBox)
            updateSourcePathBox.Text = _config.UpdateSourcePath;
        if (FindName("AutoUpdateIntervalMinutesBox") is System.Windows.Controls.TextBox autoUpdateIntervalBox)
            autoUpdateIntervalBox.Text = _config.AutoUpdateIntervalMinutes.ToString();
        if (FindName("StartWithWindowsBox") is System.Windows.Controls.CheckBox startWithWindowsBox)
            startWithWindowsBox.IsChecked = _config.StartWithWindows;
        if (FindName("UseTrayIconBox") is System.Windows.Controls.CheckBox useTrayIconBox)
            useTrayIconBox.IsChecked = _config.UseTrayIcon;
        if (FindName("PreferredDisplayCombo") is System.Windows.Controls.ComboBox displayCombo)
            PopulateDisplayCombo(displayCombo, _config.PreferredDisplayIndex);
        PopulateMusicAudioDeviceCombo(_config.MusicAudioDeviceId);

        AppendLog($"RemotePlay started");
        AppendLog($"Movies: {_config.ResolvedMoviesPath}");
        UpdateServerReadiness(isReady: false, "Server: starting\u2026");
        UpdateLibraryReadiness("Library: waiting for server\u2026", isReady: false, isBusy: true);
        ServerStatusText.Text = $"Server starting on {_config.Scheme}://*:{_config.Port}\u2026";

        // Animate the startup indicator dot
        if (FindResource("PulseAnim") is System.Windows.Media.Animation.Storyboard pulse)
        {
            System.Windows.Media.Animation.Storyboard.SetTarget(pulse, StartupPulseDot);
            pulse.Begin();
        }

        // GetLocalIp() and all server startup work runs on a background thread to keep the UI responsive.
        _ = StartServerPipelineAsync(_config, isRestart: false);

        // Restore persisted window geometry.
        RestoreWindowLayout();

        // Handle --create-link <path> launched from Explorer context menu.
        var args = Environment.GetCommandLineArgs();
        var linkArgIndex = Array.IndexOf(args, "--create-link");
        if (linkArgIndex >= 0 && linkArgIndex + 1 < args.Length)
        {
            var targetPath = args[linkArgIndex + 1];
            NavigateToLinksTab(targetPath);
        }
    }

    private void RestoreWindowLayout()
    {
        // Restore window geometry only when values look sane (non-default / on-screen).
        if (_config.WindowWidth > 100 && _config.WindowHeight > 100)
        {
            Width  = _config.WindowWidth;
            Height = _config.WindowHeight;

            // Only restore position when it falls within a visible screen area.
            var screenBounds = System.Windows.Forms.Screen.AllScreens
                .Select(s => s.WorkingArea)
                .ToArray();
            var windowRect = new System.Drawing.Rectangle(
                (int)_config.WindowLeft, (int)_config.WindowTop,
                (int)_config.WindowWidth, (int)_config.WindowHeight);
            if (screenBounds.Any(b => b.IntersectsWith(windowRect)))
            {
                Left = _config.WindowLeft;
                Top  = _config.WindowTop;
            }
        }

        if (_config.WindowMaximized)
            WindowState = WindowState.Maximized;

        // Restore browser column widths (applied after layout pass via Dispatcher).
        if (_config.BrowserColNameWidth > 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                LeftColName.Width   = _config.BrowserColNameWidth;
                LeftColType.Width   = _config.BrowserColTypeWidth;
                LeftColTarget.Width = _config.BrowserColTargetWidth;
                RightColName.Width   = _config.BrowserColNameWidth;
                RightColType.Width   = _config.BrowserColTypeWidth;
                RightColTarget.Width = _config.BrowserColTargetWidth;
            }));
        }
    }

    private void SaveWindowLayout()
    {
        // Don't persist geometry when maximized — we already track that flag separately.
        var isMax = WindowState == WindowState.Maximized;
        var updated = AppConfig.WithWindowLayout(
            _config,
            width:     isMax ? _config.WindowWidth  : Width,
            height:    isMax ? _config.WindowHeight : Height,
            left:      isMax ? _config.WindowLeft   : Left,
            top:       isMax ? _config.WindowTop    : Top,
            maximized: isMax,
            colName:   LeftColName.Width,
            colType:   LeftColType.Width,
            colTarget: LeftColTarget.Width);
        _config = updated;
        _appConfigService.Save(updated);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => SaveWindowLayout();

    private void OnWindowStateChanged(object? sender, EventArgs e) => SaveWindowLayout();

    // When re-activating the window (e.g. alt-tab back from another app) while in
    // fullscreen, the LibVLC child HWND may have absorbed Win32 keyboard focus.
    // Explicitly re-focus the WPF window so PreviewKeyDown (ESC) keeps working.
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_isVideoMode)
            Focus();
    }

    private async Task StartServerPipelineAsync(AppConfig config, bool isRestart)
    {
        try
        {
            var actionName = isRestart ? "Restart" : "Startup";
            await Dispatcher.InvokeAsync(() =>
            {
                _serverReady = false;
                UpdateServerReadiness(isReady: false, $"Server: {(isRestart ? "restarting" : "starting")}…");
                UpdateLibraryReadiness("Library: waiting for server…", isReady: false, isBusy: true);
            });
            Logger.Info($"{actionName}: resolving local IP");
            await Dispatcher.InvokeAsync(() => AppendLog($"{actionName}: resolving local IP…"));
            var ip = GetLocalIp();
            Logger.Info($"{actionName}: local IP resolved as {ip}");
            await CheckPortConflictAsync(config.Port, actionName, isRestart);

            // Fire-and-forget: firewall rule setup must not block the server startup path
            _ = Task.Run(() =>
            {
                Logger.Info($"{actionName}: checking firewall rule in background");
                EnsureFirewallRule(config.Port);
            });

            Logger.Info($"{actionName}: starting presence broadcaster");
            await Dispatcher.InvokeAsync(() => AppendLog($"{actionName}: starting discovery broadcaster…"));
            _webServer?.Stop();
            // Ensure all playback is stopped before the server restarts so the new
            // server instance always starts in a clean, idle state.
            _musicPlayer.Stop();
            _radioPlayer.Stop();
            await Dispatcher.InvokeAsync(() => StopMovie());
            _broadcaster?.Stop();
            _broadcaster?.Dispose();
            _broadcaster = new PresenceBroadcaster(config);
            _broadcaster.Start();

            Logger.Info($"{actionName}: creating web server");
            await Dispatcher.InvokeAsync(() => AppendLog($"{actionName}: creating web server…"));
            _webServer = await RunStartupStepWithTimeoutAsync(() => CreateWebServer(config), "creating web server", TimeSpan.FromSeconds(15));
            Logger.Info($"{actionName}: starting web listener");
            await Dispatcher.InvokeAsync(() => AppendLog($"{actionName}: starting web listener on {config.Scheme}://*:{config.Port}…"));
            await RunStartupStepWithTimeoutAsync(() => _webServer.Start(), "starting web listener", TimeSpan.FromSeconds(15));
            Logger.Info($"{actionName}: web listener started");

            await Dispatcher.InvokeAsync(() =>
            {
                _serverReady = true;
                UpdateServerUrlDisplay(ip, _webServer.ActiveScheme, config.Port, _webServer.StartupWarning);
                ServerStatusText.Text = $"Server running on {_webServer.ActiveScheme}://*:{config.Port}";
                UpdateServerReadiness(isReady: true, $"Server: ready on {_webServer.ActiveScheme}://*:{config.Port}");
                UpdateLibraryReadinessFromStatus();
                UpdateUpdateReadiness();
                // Poll until the async check completes, then show final result
                _ = Task.Run(async () =>
                {
                    for (var i = 0; i < 30; i++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        if (_appUpdater is null) break;
                        await Dispatcher.InvokeAsync(UpdateUpdateReadiness);
                        if (_appUpdater.HasChecked) break;
                    }
                });
                AppendLog($"Web server listening on {_webServer.ActiveScheme}://*:{config.Port}");
                if (!string.IsNullOrWhiteSpace(_webServer.StartupWarning))
                    ShowDiag(_webServer.StartupWarning);
                if (!isRestart)
                {
                    _ = Task.Run(() => { Console.Beep(880, 120); Console.Beep(1108, 200); });
                    DismissStartupOverlay(success: true, onComplete: () =>
                        Dispatcher.InvokeAsync(EnterFullscreenCleanAsync,
                            System.Windows.Threading.DispatcherPriority.Loaded));
                }
                RefreshStatusStats();
                _statsTimer.Start();
            });

            _ = RunServerSelfTestAsync(ip, _webServer.ActiveScheme, config.Port);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start web server", ex);
            await Dispatcher.InvokeAsync(() =>
            {
                _serverReady = false;
                ServerStatusText.Text = $"Server failed on {config.Scheme}://*:{config.Port}";
                UpdateServerReadiness(isReady: false, "Server: failed");
                UpdateLibraryReadiness("Library: unavailable until server starts", isReady: false, isBusy: false);
                AppendLog($"ERROR starting server: {ex.Message}");
                ShowDiag($"Server failed: {ex.Message}");
                if (!isRestart)
                    DismissStartupOverlay(success: false);
                else
                    ShowSettingsFeedback($"Server restart failed: {ex.Message}", isError: true);
            });
        }
    }

    private void DismissStartupOverlay(bool success, Action? onComplete = null)
    {
        if (StartupOverlay.Visibility != Visibility.Visible)
        {
            onComplete?.Invoke();
            return;
        }

        if (!success)
        {
            // Briefly flash red so the user sees the failure before it fades
            StartupPulseDot.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x59, 0x59));
            StartupOverlayText.Text = "Server failed to start";
            StartupOverlayText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x59, 0x59));
        }

        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
            new Duration(TimeSpan.FromMilliseconds(success ? 600 : 1800)));
        fade.Completed += (_, _) =>
        {
            StartupOverlay.Visibility = Visibility.Collapsed;
            onComplete?.Invoke();
        };
        StartupOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void InitializeTrayIcon()
    {
        if (!_config.UseTrayIcon)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            return;
        }

        if (_trayIcon is not null)
            return;

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location),
            Text = "RemotePlay",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add("Open RemotePlay", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SaveWindowLayout();
        if (!_isExiting && _config.UseTrayIcon)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            _trayIcon?.ShowBalloonTip(1500, "RemotePlay", "RemotePlay is still running in the tray.", System.Windows.Forms.ToolTipIcon.Info);
            return;
        }

        SaveCurrentPlaybackPosition();
        _webServer?.Stop();
        _broadcaster?.Stop();
        _broadcaster?.Dispose();
        _trayIcon?.Dispose();
        _mediaPlayer.Dispose();
        _musicPlayer.Dispose();
        _radioPlayer.Dispose();
        _radioBrowser.Dispose();
        _libVlc.Dispose();
    }

    private static void ApplyWindowsAutostart(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
                return;

            const string valueName = "RemotePlay";
            if (enabled)
            {
                var executable = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                key.SetValue(valueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply Windows autostart setting", ex);
        }
    }

    private static async Task RunStartupStepWithTimeoutAsync(Action action, string description, TimeSpan timeout)
    {
        var startupTask = Task.Run(action);
        var completedTask = await Task.WhenAny(startupTask, Task.Delay(timeout));
        if (completedTask != startupTask)
            throw new TimeoutException($"Timed out while {description} after {timeout.TotalSeconds:0} seconds.");

        await startupTask;
    }

    private static async Task<T> RunStartupStepWithTimeoutAsync<T>(Func<T> action, string description, TimeSpan timeout)
    {
        var startupTask = Task.Run(action);
        var completedTask = await Task.WhenAny(startupTask, Task.Delay(timeout));
        if (completedTask != startupTask)
            throw new TimeoutException($"Timed out while {description} after {timeout.TotalSeconds:0} seconds.");

        return await startupTask;
    }

    private async Task CheckPortConflictAsync(int port, string actionName, bool isRestart)
    {
        // On restart our own server still holds the port — not a conflict.
        if (isRestart)
            return;

        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners();
            var isListening = listeners.Any(listener => listener.Port == port);
            if (!isListening)
                return;

            var message = $"{actionName}: port {port} is already listening before RemotePlay starts. Startup may fail if another process owns it.";
            Logger.Info(message);
            await Dispatcher.InvokeAsync(() =>
            {
                AppendLog(message);
                ShowDiag($"Port {port} is already in use. If startup fails, choose another port in Settings.");
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Port conflict check failed", ex);
            await Dispatcher.InvokeAsync(() => AppendLog($"Port conflict check failed: {ex.Message}"));
        }
    }

    private async Task RunServerSelfTestAsync(string ip, string activeScheme, int port)
    {
        try
        {
            await Dispatcher.InvokeAsync(() => AppendLog("Self-test: checking server endpoints…"));
            var baseUrl = $"{activeScheme}://{ip}:{port}";
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

            await CheckEndpointAsync(client, baseUrl, "/health");
            await CheckEndpointAsync(client, baseUrl, "/api/status");
            await CheckEndpointAsync(client, baseUrl, "/api/library-status");

            Logger.Info("Self-test: server endpoints OK");
            await Dispatcher.InvokeAsync(() => AppendLog("Self-test: server endpoints OK"));
        }
        catch (Exception ex)
        {
            Logger.Error("Self-test failed", ex);
            await Dispatcher.InvokeAsync(() =>
            {
                AppendLog($"Self-test failed: {ex.Message}");
                ShowDiag($"Server started, but self-test failed: {ex.Message}");
            });
        }
    }

    private static async Task CheckEndpointAsync(HttpClient client, string baseUrl, string path)
    {
        using var response = await client.GetAsync(baseUrl + path).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private void RefreshStatusStats()
    {
        if (_webServer is null) return;

        int videoCount = _webServer.LibraryVideoCount;
        int peerCount = _broadcaster?.GetPeers().Length ?? 0;

        if (FindName("LibraryStatText") is System.Windows.Controls.TextBlock libText)
            libText.Text = $"{videoCount} videos";

        if (FindName("PeersStatText") is System.Windows.Controls.TextBlock peersText)
            peersText.Text = $"{peerCount} instance{(peerCount != 1 ? "s" : "")} online";

        UpdateLibraryReadinessFromStatus();
    }

    private void OnRescanLibrary(object sender, RoutedEventArgs e)
    {
        if (_webServer is null)
        {
            AppendLog("Library rescan unavailable until the server starts.");
            return;
        }

        _webServer.RequestLibraryRescan();
        AppendLog("Library rescan requested.");
        UpdateLibraryReadiness("Library: rescan requested…", isReady: false, isBusy: true);
        RefreshStatusStats();
    }

    private void UpdateServerReadiness(bool isReady, string text)
    {
        ServerReadyText.Text = text;
        ServerReadyDot.Background = new System.Windows.Media.SolidColorBrush(isReady
            ? System.Windows.Media.Color.FromRgb(0x00, 0xD4, 0xAA)
            : System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x00));
    }

    private void UpdateLibraryReadinessFromStatus()
    {
        if (!_serverReady || _webServer is null)
            return;

        var status = _webServer.LibraryStatus;
        if (status.IsScanning)
        {
            var scanned = status.ScannedFiles > 0 ? $" ({status.ScannedFiles} files checked)" : string.Empty;
            UpdateLibraryReadiness($"Library: scanning in background{scanned}", isReady: false, isBusy: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            UpdateLibraryReadiness($"Library: scan warning — {status.LastError}", isReady: false, isBusy: false);
            return;
        }

        UpdateLibraryReadiness($"Library: ready ({status.IndexedFiles} videos indexed)", isReady: true, isBusy: false);
    }

    private void UpdateLibraryReadiness(string text, bool isReady, bool isBusy)
    {
        LibraryReadyText.Text = text;
        LibraryReadyDot.Background = new System.Windows.Media.SolidColorBrush(isReady
            ? System.Windows.Media.Color.FromRgb(0x00, 0xD4, 0xAA)
            : isBusy
                ? System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x00)
                : System.Windows.Media.Color.FromRgb(0xFF, 0x59, 0x59));
    }

    private void UpdateUpdateReadiness()
    {
        if (_appUpdater is null || string.IsNullOrWhiteSpace(_config.UpdateSourcePath))
        {
            UpdateReadyText.Text = "Auto-update: not configured";
            UpdateReadyDot.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));
            return;
        }

        if (_appUpdater.IsUpdating)
        {
            UpdateReadyText.Text = "Auto-update: applying update…";
            UpdateReadyDot.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x00));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_appUpdater.LastUpdateError))
        {
            UpdateReadyText.Text = $"Auto-update: error — {_appUpdater.LastUpdateError}";
            UpdateReadyDot.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x59, 0x59));
            return;
        }

        if (!_appUpdater.HasChecked)
        {
            UpdateReadyText.Text = $"Auto-update: checking… (current {_appUpdater.CurrentVersion})";
            UpdateReadyDot.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x00));
            return;
        }

        var available = _appUpdater.AvailableVersion;
        var current = _appUpdater.CurrentVersion;

        if (!string.IsNullOrWhiteSpace(available) &&
            !string.Equals(available, current, StringComparison.OrdinalIgnoreCase))
        {
            UpdateReadyText.Text = $"Auto-update: update available! {current} → {available}";
            UpdateReadyDot.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x00));
            return;
        }

        UpdateReadyText.Text = $"Auto-update: up to date ({current})";
        UpdateReadyDot.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x00, 0xD4, 0xAA));
    }

    private void PopulateLanguageCombo(string comboName, string selectedCode)
    {
        if (FindName(comboName) is not System.Windows.Controls.ComboBox combo)
            return;

        combo.Items.Clear();
        foreach (var option in LanguageOptions)
            combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = option.Name, Tag = option.Code });

        foreach (System.Windows.Controls.ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), selectedCode, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private string GetSelectedLanguageCode(string comboName, string fallback)
    {
        if (FindName(comboName) is System.Windows.Controls.ComboBox combo
            && combo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            return item.Tag?.ToString() ?? string.Empty;

        return fallback;
    }

    // -- View toggle ---------------------------------------------------------

    private void OnIdleOverlayClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isVideoMode)
            OnToggleView(this, new RoutedEventArgs());
    }

    private void OnToggleView(object sender, RoutedEventArgs e)
    {
        if (!_isVideoMode)
        {
            _ = EnterFullscreenCleanAsync();
            return;
        }

        // Exiting fullscreen.
        _isVideoMode = false;
        _fullscreenWatchdogTimer.Stop();
        _overlayTimer.Stop();
        HideVideoOverlay(immediate: true);
        IdleOverlay.Visibility = Visibility.Collapsed;
        IdleOverlay.IsHitTestVisible = false;
        VideoPanel.Visibility = Visibility.Collapsed;
        AppToolbar.Visibility = Visibility.Visible;
        MainTabs.Visibility = Visibility.Visible;
        CornerWidget.Visibility = Visibility.Visible;
        ToggleViewBtn.Content = "\u25B6  Switch to Video";
        Topmost = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.CanResize;

        // Move the main window back to where it was before fullscreen.
        if (!double.IsNaN(_windowedLeft))
        {
            Left   = _windowedLeft;
            Top    = _windowedTop;
            Width  = _windowedWidth;
            Height = _windowedHeight;
        }
        if (_currentFilePath is not null && _mediaPlayer.IsPlaying)
            _miniPreviewTimer.Start();
    }

    private async Task EnterFullscreenCleanAsync()
    {
        // Remember windowed position so we can restore it when exiting fullscreen.
        _windowedLeft   = Left;
        _windowedTop    = Top;
        _windowedWidth  = Width;
        _windowedHeight = Height;

        _isVideoMode = true;
        _miniPreviewTimer.Stop();
        MiniPreviewBorder.Visibility = Visibility.Collapsed;
        CornerWidget.Visibility = Visibility.Collapsed;
        MainTabs.Visibility = Visibility.Collapsed;
        AppToolbar.Visibility = Visibility.Collapsed;
        ToggleViewBtn.Content = "\uD83E\uDEB5  Switch to Log";

        // Black cover hides any resize/render flashes for the duration of the
        // fullscreen transition. It is faded out smoothly at the end.
        FlashCover.Opacity = 1;
        FlashCover.Visibility = Visibility.Visible;

        // Show idle QR — VideoPanel stays Collapsed during resize so the LibVLC
        // HWND doesn't exist yet and cannot paint white during the window resize.
        HideVideoOverlay(immediate: true);
        ShowIdleOverlay();

        // Let WPF render the idle overlay before resizing.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Resize to fullscreen — VideoPanel is still Collapsed, no HWND yet.
        EnsureFullscreenWindowBounds(force: true);
        _fullscreenWatchdogTimer.Start();

        // Flush so the window is fully painted at its new fullscreen size.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Create the LibVLC HWND at fullscreen size using Visibility.Hidden:
        // the HWND exists and is measured at the correct dimensions but SW_HIDE
        // keeps it invisible — no white WM_PAINT flash.  Switching to Visible
        // later is just SW_SHOW with no resize, so no flash on first Play either.
        //
        // Guard: if OnMediaPlaying fired during the awaits above and already
        // called HideIdleOverlay() → VideoPanel.Visibility = Visible, do not
        // clobber it back to Hidden or the video will never appear.
        if (VideoPanel.Visibility == Visibility.Visible)
        {
            FadeOutFlashCover();
            return;
        }

        // If media is already playing (e.g. re-entering fullscreen from mini-preview
        // click while a movie is running), OnMediaPlaying won't fire again, so
        // CompleteVideoTransitionAsync will never be called. Reveal the video directly.
        if (_mediaPlayer.IsPlaying)
            HideIdleOverlay();
        else
            VideoPanel.Visibility = Visibility.Hidden;

        // Ensure WPF keyboard focus stays on this window, not on the LibVLC child HWND.
        // Without this, PreviewKeyDown (ESC) and overlay click events stop firing after
        // switching away from the window and back into fullscreen.
        Focus();

        FadeOutFlashCover();
    }

    private void FadeOutFlashCover()
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => FlashCover.Visibility = Visibility.Collapsed;
        FlashCover.BeginAnimation(OpacityProperty, fade);
    }

    private System.Windows.Forms.Screen GetPreferredFullscreenScreen()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screenIndex = _config.PreferredDisplayIndex;
        if (screens.Length == 0)
            throw new InvalidOperationException("No display detected.");

        return screenIndex >= 0 && screenIndex < screens.Length
            ? screens[screenIndex]
            : System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }

    private void RefreshDisplaySettings()
    {
        if (FindName("PreferredDisplayCombo") is not System.Windows.Controls.ComboBox displayCombo)
            return;

        displayCombo.Items.Clear();
        displayCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Primary screen (default)", Tag = -1 });
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var label = $"Screen {i + 1}  ({screen.Bounds.Width}�{screen.Bounds.Height}){(screen.Primary ? "  [Primary]" : string.Empty)}";
            displayCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = i });
        }

        var savedDisplayIndex = _config.PreferredDisplayIndex;
        System.Windows.Controls.ComboBoxItem? savedItem = null;
        foreach (System.Windows.Controls.ComboBoxItem item in displayCombo.Items)
        {
            if (item.Tag is int tag && tag == savedDisplayIndex)
            {
                savedItem = item;
                break;
            }
        }

        if (savedItem is null && savedDisplayIndex >= 0)
        {
            savedItem = new System.Windows.Controls.ComboBoxItem
            {
                Content = $"Saved screen {savedDisplayIndex + 1} (not currently connected)",
                Tag = savedDisplayIndex
            };
            displayCombo.Items.Add(savedItem);
        }

        displayCombo.SelectedItem = savedItem ?? displayCombo.Items[0];
        RefreshCurrentDisplayText();
    }

    private void RefreshCurrentDisplayText()
    {
        if (FindName("CurrentDisplayText") is not System.Windows.Controls.TextBlock currentDisplayText)
            return;

        try
        {
            var screen = GetPreferredFullscreenScreen();
            var screens = System.Windows.Forms.Screen.AllScreens;
            var index = Array.IndexOf(screens, screen);
            var screenName = index >= 0 ? $"Screen {index + 1}" : "Primary screen";
            var savedDisplay = _config.PreferredDisplayIndex < 0
                ? "Primary screen (default)"
                : $"Screen {_config.PreferredDisplayIndex + 1}";
            currentDisplayText.Text = $"Current fullscreen target: {screenName} ({screen.Bounds.Width}�{screen.Bounds.Height}){(screen.Primary ? " [Primary]" : string.Empty)}. Saved preference: {savedDisplay}.";
        }
        catch (Exception ex)
        {
            currentDisplayText.Text = $"Current fullscreen target unavailable: {ex.Message}";
        }
    }

    private bool EnsureFullscreenWindowBounds(bool force = false)
    {
        if (!_isVideoMode)
            return false;

        var diagnostics = GetDisplayDiagnosticsCore();
        var needsRepair = force || diagnostics.NeedsFullscreenRepair;

        if (!needsRepair)
            return false;

        var targetScreen = GetPreferredFullscreenScreen();
        var physBounds = targetScreen.Bounds;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;

        // Set position and size atomically in physical pixels via Win32 SetWindowPos.
        // Setting WPF Left/Top then Width/Height separately triggers a PerMonitorV2
        // DPI-change event between the two writes, causing WPF to rescale the old
        // dimensions before the new ones are applied — resulting in a visible oversized
        // flash for ~3 seconds until the watchdog corrects it.  A single SetWindowPos
        // call moves and resizes in one message with no intermediate state.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowBoundsPhysical(
            hwnd,
            physBounds.Left,
            physBounds.Top,
            physBounds.Width,
            physBounds.Height);

        if (!force)
        {
            Logger.Info($"Fullscreen watchdog repaired bounds. Target={physBounds.Left},{physBounds.Top},{physBounds.Width}x{physBounds.Height}px; Current={diagnostics.WindowLeft},{diagnostics.WindowTop},{diagnostics.WindowWidth}x{diagnostics.WindowHeight}px");
            AppendLog("Fullscreen watchdog repaired window bounds.");
        }

        return true;
    }

    private DisplayDiagnostics GetDisplayDiagnostics()
    {
        DisplayDiagnostics diagnostics = new();
        Dispatcher.Invoke(() => diagnostics = GetDisplayDiagnosticsCore());
        return diagnostics;
    }

    private DisplayDiagnostics GetDisplayDiagnosticsCore()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var selectedIndex = _config.PreferredDisplayIndex;
        var targetScreen = GetPreferredFullscreenScreen();
        var targetIndex = Array.IndexOf(screens, targetScreen);
        var targetBounds = targetScreen.Bounds;
        var source = PresentationSource.FromVisual(this);
        var dpiScaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var dpiScaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;

        var topLeftPx = PointToScreen(new System.Windows.Point(0, 0));
        var bottomRightPx = PointToScreen(new System.Windows.Point(Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));
        var currentWidthPx = Math.Max(0, bottomRightPx.X - topLeftPx.X);
        var currentHeightPx = Math.Max(0, bottomRightPx.Y - topLeftPx.Y);

        var windowLeftPx = (int)Math.Round(topLeftPx.X);
        var windowTopPx = (int)Math.Round(topLeftPx.Y);
        var windowWidthPx = (int)Math.Round(currentWidthPx);
        var windowHeightPx = (int)Math.Round(currentHeightPx);

        var needsRepair = FullscreenBoundsEvaluator.NeedsRepair(
            _isVideoMode,
            WindowStyle.ToString(),
            ResizeMode.ToString(),
            WindowState.ToString(),
            Topmost,
            windowLeftPx,
            windowTopPx,
            windowWidthPx,
            windowHeightPx,
            targetBounds.Left,
            targetBounds.Top,
            targetBounds.Width,
            targetBounds.Height);

        return new DisplayDiagnostics
        {
            IsVideoMode = _isVideoMode,
            PreferredDisplayIndex = selectedIndex,
            TargetDisplayIndex = targetIndex,
            TargetDisplayName = targetScreen.DeviceName,
            TargetLeft = targetBounds.Left,
            TargetTop = targetBounds.Top,
            TargetWidth = targetBounds.Width,
            TargetHeight = targetBounds.Height,
            WindowState = WindowState.ToString(),
            WindowStyle = WindowStyle.ToString(),
            ResizeMode = ResizeMode.ToString(),
            Topmost = Topmost,
            WindowLeft = windowLeftPx,
            WindowTop = windowTopPx,
            WindowWidth = windowWidthPx,
            WindowHeight = windowHeightPx,
            CurrentFilePath = _currentFilePath ?? string.Empty,
            CurrentTitle = NowPlayingText.Text,
            Zoom = Math.Round(_zoom, 3),
            Brightness = Math.Round(_brightness, 3),
            Saturation = Math.Round(_saturation, 3),
            VideoSurfaceWidth = Math.Round(VideoPanel.ActualWidth, 1),
            VideoSurfaceHeight = Math.Round(VideoPanel.ActualHeight, 1),
            VideoPlayerActualWidth = Math.Round(VideoPlayer.ActualWidth, 1),
            VideoPlayerActualHeight = Math.Round(VideoPlayer.ActualHeight, 1),
            DpiScaleX = Math.Round(dpiScaleX, 3),
            DpiScaleY = Math.Round(dpiScaleY, 3),
            NeedsFullscreenRepair = needsRepair,
            Displays = screens.Select((screen, index) => new DisplayInfo
            {
                Index = index,
                Name = screen.DeviceName,
                IsPrimary = screen.Primary,
                Left = screen.Bounds.Left,
                Top = screen.Bounds.Top,
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                WorkingLeft = screen.WorkingArea.Left,
                WorkingTop = screen.WorkingArea.Top,
                WorkingWidth = screen.WorkingArea.Width,
                WorkingHeight = screen.WorkingArea.Height
            }).ToArray(),
            CodecInfo = _codecInfo,
            ForceSwAudio = _forceSwAudio
        };
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _isVideoMode)
        {
            OnToggleView(this, new RoutedEventArgs());
            e.Handled = true;
        }
        if (e.Key == System.Windows.Input.Key.LeftShift || e.Key == System.Windows.Input.Key.RightShift)
            if (FindName("ShowExtendedLogBtn") is System.Windows.Controls.Button logBtn)
                logBtn.Visibility = Visibility.Visible;
    }

    private void OnWindowPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.LeftShift || e.Key == System.Windows.Input.Key.RightShift)
            if (FindName("ShowExtendedLogBtn") is System.Windows.Controls.Button logBtn)
                logBtn.Visibility = Visibility.Collapsed;
    }
    // -- Overlay auto-hide ----------------------------------------------------

    private void OnVideoMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(VideoPanel);
        if (pos.Y <= 80)
            ShowVideoOverlay();
    }

    private void OnVideoTopHotZoneMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        ShowVideoOverlay();

    private void ShowVideoOverlay()
    {
        if (!_isVideoMode || _currentFilePath is null)
            return;

        VideoOverlayToolbar.Visibility = Visibility.Visible;
        VideoOverlayToolbar.IsHitTestVisible = true;
        VideoOverlayToolbar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        _overlayTimer.Stop();
        _overlayTimer.Start();
    }

    private void HideVideoOverlay(bool immediate = false)
    {
        _overlayTimer.Stop();
        VideoOverlayToolbar.IsHitTestVisible = false;

        if (immediate)
        {
            VideoOverlayToolbar.BeginAnimation(OpacityProperty, null);
            VideoOverlayToolbar.Opacity = 0;
            VideoOverlayToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
        animation.Completed += (_, _) => VideoOverlayToolbar.Visibility = Visibility.Collapsed;
        VideoOverlayToolbar.BeginAnimation(OpacityProperty, animation);
    }

    // -- Log / diagnostics

    private void OnRefreshLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var logFile = AppPaths.LogFile;
            if (File.Exists(logFile))
            {
                var lines = File.ReadAllLines(logFile);
                LogBox.Text = string.Join(Environment.NewLine, lines);
                LogScroller.ScrollToBottom();
            }
            else
            {
                AppendLog("(log file not found yet)");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not read log: {ex.Message}");
        }
    }

    private void OnMiniPreviewClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isVideoMode)
            OnToggleView(this, new RoutedEventArgs());
    }

    private void UpdateMiniPreview()
    {
        if (_isVideoMode || !_mediaPlayer.IsPlaying)
        {
            _miniPreviewTimer.Stop();
            MiniPreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            if (!_mediaPlayer.TakeSnapshot(0, _miniPreviewSnapshotPath, 0, 0))
                return;

            if (!File.Exists(_miniPreviewSnapshotPath))
                return;

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(_miniPreviewSnapshotPath, UriKind.Absolute);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();

            MiniPreviewImage.Source = bitmap;
            MiniPreviewBorder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Logger.Error("Mini preview update failed", ex);
        }
    }

    private void OnStopAndExit(object sender, RoutedEventArgs e)
    {
        _webServer?.Stop();
        System.Windows.Application.Current.Shutdown();
    }


    private void OnSettingsCat(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        var tag = btn.Tag?.ToString() ?? "";

        // Hide all panels
        foreach (var name in new[] { "PanelLibrary", "PanelServer", "PanelPlayback", "PanelTracks", "PanelDisplay", "PanelStartup" })
            if (FindName(name) is System.Windows.UIElement el)
                el.Visibility = Visibility.Collapsed;

        // Show selected panel
        if (FindName("Panel" + tag) is System.Windows.UIElement panel)
            panel.Visibility = Visibility.Visible;

        // Update sidebar highlight
        foreach (var name in new[] { "SettingsCatLibrary", "SettingsCatServer", "SettingsCatPlayback", "SettingsCatTracks", "SettingsCatDisplay", "SettingsCatStartup" })
        {
            if (FindName(name) is System.Windows.Controls.Button catBtn)
            {
                var isActive = catBtn == btn;
                catBtn.Background = isActive
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1a, 0x2a, 0x3a))
                    : System.Windows.Media.Brushes.Transparent;
                catBtn.Foreground = isActive
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0x45, 0x60))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA));
            }
        }

    }


    // -- Settings -------------------------------------------------------------

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the folder containing your video files",
            InitialDirectory = MoviesFolderBox.Text
        };

        if (dialog.ShowDialog() == true)
            MoviesFolderBox.Text = dialog.FolderName;
    }

    private void OnBrowseMusicFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the root folder for your music library",
            InitialDirectory = MusicFolderBox.Text
        };

        if (dialog.ShowDialog() == true)
            MusicFolderBox.Text = dialog.FolderName;
    }

    private void OnBrowseUpdateSourceFolder(object sender, RoutedEventArgs e)
    {
        var currentPath = FindName("UpdateSourcePathBox") is System.Windows.Controls.TextBox box ? box.Text : string.Empty;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the folder containing update files",
            InitialDirectory = currentPath
        };

        if (dialog.ShowDialog() == true && FindName("UpdateSourcePathBox") is System.Windows.Controls.TextBox pathBox)
            pathBox.Text = dialog.FolderName;
    }

    private void OnExportCertificate(object sender, RoutedEventArgs e)
    {
        try
        {
            using var certificate = WebServer.TryGetHttpsCertificate();
            if (certificate is null)
            {
                ShowSettingsFeedback("?? No HTTPS certificate exists yet. Enable HTTPS once first.", isError: true);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export RemotePlay HTTPS certificate",
                FileName = "RemotePlay-Local-HTTPS.cer",
                Filter = "Certificate (*.cer)|*.cer"
            };

            if (dialog.ShowDialog() != true)
                return;

            File.WriteAllBytes(dialog.FileName, certificate.Export(X509ContentType.Cert));
            ShowSettingsFeedback($"? Certificate exported: {dialog.FileName}", isError: false);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to export HTTPS certificate", ex);
            ShowSettingsFeedback($"? Certificate export failed: {ex.Message}", isError: true);
        }
    }

    private void OnOpenHealthPage(object sender, RoutedEventArgs e)
    {
        try
        {
            var scheme = _webServer?.ActiveScheme ?? _config.Scheme;
            var url = $"{scheme}://{GetLocalIp()}:{_config.Port}/health";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to open health page", ex);
            ShowSettingsFeedback($"? Could not open health page: {ex.Message}", isError: true);
        }
    }

    private void OnApplySettings(object sender, RoutedEventArgs e)
    {
        var folder = MoviesFolderBox.Text.Trim();
        var musicFolder = MusicFolderBox.Text.Trim();
        var portText = PortBox.Text.Trim();
        var instanceName = FindName("InstanceNameBox") is System.Windows.Controls.TextBox instanceNameBox2
            ? instanceNameBox2.Text.Trim()
            : _config.InstanceName;
        var useHttps = FindName("UseHttpsBox") is System.Windows.Controls.CheckBox useHttpsBox && useHttpsBox.IsChecked == true;

        var preferredAudioLang = GetSelectedLanguageCode("PreferredAudioLangCombo", _config.PreferredAudioLanguage);
        var preferredSubtitleLang = GetSelectedLanguageCode("PreferredSubtitleLangCombo", _config.PreferredSubtitleLanguage);
        var secondarySubtitleLang = GetSelectedLanguageCode("SecondarySubtitleLangCombo", _config.SecondarySubtitleLanguage);

        var preferForced = _config.PreferForcedSubtitles;
        if (FindName("PreferForcedSubtitlesBox") is System.Windows.Controls.CheckBox forcedBox2)
            preferForced = forcedBox2.IsChecked == true;

        var playbackEndBehavior = _config.PlaybackEndBehavior;
        if (FindName("PlaybackEndBehaviorCombo") is System.Windows.Controls.ComboBox endCombo2
            && endCombo2.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem
            && Enum.TryParse<PlaybackEndMode>(selectedItem.Tag?.ToString(), out var parsedMode))
            playbackEndBehavior = parsedMode;

        var playbackHistoryLimit = _config.PlaybackHistoryLimit;
        if (FindName("PlaybackHistoryLimitBox") is System.Windows.Controls.TextBox historyLimitBox2)
        {
            if (!int.TryParse(historyLimitBox2.Text.Trim(), out playbackHistoryLimit) || playbackHistoryLimit < 1)
            {
                ShowSettingsFeedback("Playback history limit must be a whole number of at least 1.", isError: true);
                return;
            }
        }

        var libraryRescanDelayMinutes = _config.LibraryRescanDelayMinutes;
        if (FindName("AutoRescanIntervalMinutesBox") is System.Windows.Controls.TextBox rescanIntervalBox2)
        {
            if (!int.TryParse(rescanIntervalBox2.Text.Trim(), out libraryRescanDelayMinutes) || libraryRescanDelayMinutes < 0)
            {
                ShowSettingsFeedback("Auto-rescan interval must be a whole number of 0 or more minutes.", isError: true);
                return;
            }
        }

        var updateSourcePath = _config.UpdateSourcePath;
        if (FindName("UpdateSourcePathBox") is System.Windows.Controls.TextBox updateSourcePathBox2)
            updateSourcePath = updateSourcePathBox2.Text.Trim();

        var autoUpdateIntervalMinutes = _config.AutoUpdateIntervalMinutes;
        if (FindName("AutoUpdateIntervalMinutesBox") is System.Windows.Controls.TextBox autoUpdateIntervalBox2)
        {
            if (!int.TryParse(autoUpdateIntervalBox2.Text.Trim(), out autoUpdateIntervalMinutes) || autoUpdateIntervalMinutes < 0)
            {
                ShowSettingsFeedback("Auto-update interval must be a whole number of 0 or more minutes.", isError: true);
                return;
            }
        }

        var preferredDisplayIndex = _config.PreferredDisplayIndex;
        if (FindName("PreferredDisplayCombo") is System.Windows.Controls.ComboBox displayCombo2
            && displayCombo2.SelectedItem is System.Windows.Controls.ComboBoxItem displayItem
            && displayItem.Tag is int displayTag)
            preferredDisplayIndex = displayTag;

        var startWithWindows = FindName("StartWithWindowsBox") is System.Windows.Controls.CheckBox startWithWindowsBox2
            ? startWithWindowsBox2.IsChecked == true
            : _config.StartWithWindows;
        var useTrayIcon = FindName("UseTrayIconBox") is System.Windows.Controls.CheckBox useTrayIconBox2
            ? useTrayIconBox2.IsChecked == true
            : _config.UseTrayIcon;

        var musicAudioDeviceId = _config.MusicAudioDeviceId;
        if (MusicAudioDeviceCombo.SelectedItem is MusicAudioDevice selectedDev)
            musicAudioDeviceId = selectedDev.DeviceNumber == -1 ? string.Empty : selectedDev.Name;

        var validation = _settingsValidationService.Validate(folder, portText);
        if (!validation.IsValid)
        {
            ShowSettingsFeedback(validation.ErrorMessage ?? "?? Invalid settings.", isError: true);
            return;
        }

        var port = validation.ParsedPort ?? _config.Port;

        try
        {
            var updatedConfig = _appConfigFactory.CreateForSettingsApply(
                _config,
                port,
                useHttps,
                folder,
                musicFolder,
                instanceName,
                _volume,
                _zoom,
                _audioBoost,
                _playbackSpeed,
                _subtitlesEnabled,
                preferredAudioLang,
                preferredSubtitleLang,
                secondarySubtitleLang,
                preferForced,
                playbackEndBehavior,
                playbackHistoryLimit,
                libraryRescanDelayMinutes,
                preferredDisplayIndex,
                startWithWindows,
                useTrayIcon,
                updateSourcePath,
                autoUpdateIntervalMinutes,
                musicAudioDeviceId);

            _config = _settingsApplyService.ApplyAndReload(updatedConfig);
            ApplyWindowsAutostart(_config.StartWithWindows);
            InitializeTrayIcon();
            if (!_config.UseTrayIcon)
                ShowInTaskbar = true;
            _playbackHistory.Trim(_config.PlaybackHistoryLimit);
            _musicPlayer.SetDevice(_config.MusicAudioDeviceId);

            MoviesPathText.Text = $"Movies folder: {_config.ResolvedMoviesPath}";
            RefreshDisplaySettings();
            ShowSettingsFeedback("Saving\u2026 restarting server.", isError: false);

            var configSnapshot = _config;
            _ = RestartServerAfterSettingsApplyAsync(configSnapshot, folder);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply settings", ex);
            ShowSettingsFeedback($"Error: {ex.Message}", isError: true);
        }
    }

    private async Task RestartServerAfterSettingsApplyAsync(AppConfig configSnapshot, string folder)
    {
        await StartServerPipelineAsync(configSnapshot, isRestart: true);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_webServer is null)
                return;

            AppendLog($"Settings applied \u2014 folder: {folder}, scheme: {_webServer.ActiveScheme}, port: {configSnapshot.Port}");
            Logger.Info($"Settings updated \u2014 MoviesPath: {folder}, Scheme: {_webServer.ActiveScheme}, Port: {configSnapshot.Port}");
            if (string.IsNullOrWhiteSpace(_webServer.StartupWarning))
                ShowSettingsFeedback("Settings saved and server restarted.", isError: false);
            else
                ShowSettingsFeedback("Settings saved, but HTTPS failed and HTTP fallback is active. See Status tab for details.", isError: true);
        });
    }

    private void ShowSettingsFeedback(string message, bool isError)
    {
        SettingsFeedback.Text = message;
        SettingsFeedback.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 80, 80))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 212, 170));
        SettingsFeedback.Visibility = Visibility.Visible;
    }

    private void UpdateServerUrlDisplay(string ip, string activeScheme, int port, string? warning)
    {
        var activeUrl = $"{activeScheme}://{ip}:{port}";
        var alternateScheme = activeScheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
        IpAddressText.Text = activeUrl;
        if (FindName("AlternateUrlText") is System.Windows.Controls.TextBlock alternateUrlText)
            alternateUrlText.Text = $"Alternate: {alternateScheme}://{ip}:{port}    Health: {activeUrl}/health";
        IdleIpText.Text = $"Open  {activeUrl}  on your phone or tablet";

        if (!string.IsNullOrWhiteSpace(warning))
        {
            DiagText.Text = warning;
            DiagText.Visibility = Visibility.Visible;
        }

        try
        {
            _serverUrl = activeUrl;
            var qrSource = CreateQrBitmapSource(activeUrl);
            if (FindName("FullscreenQrImage") is System.Windows.Controls.Image fullscreenQrImage)
                fullscreenQrImage.Source = qrSource;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to update setup QR image", ex);
        }
    }

    private static BitmapImage CreateQrBitmapSource(string url)
    {
        var pngBytes = WebServer.GenerateQrCodePng(url);
        using var ms = new System.IO.MemoryStream(pngBytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // -- Helpers --------------------------------------------------------------

    private void AppendLog(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogScroller.ScrollToBottom();
        });
    }

    private void ShowDiag(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            DiagText.Text = message;
            DiagText.Visibility = Visibility.Visible;
        });
    }


    private static void EnsureFirewallRule(int port)
    {
        try
        {
            // Check whether the rule already exists to avoid a slow redundant netsh add
            var checkPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"RemotePlay\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var checkProc = System.Diagnostics.Process.Start(checkPsi);
            string checkOutput = checkProc?.StandardOutput.ReadToEnd() ?? string.Empty;
            checkProc?.WaitForExit(2000);

            if (checkOutput.Contains($"LocalPort", StringComparison.OrdinalIgnoreCase)
                && checkOutput.Contains(port.ToString(), StringComparison.Ordinal))
            {
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"RemotePlay\" dir=in action=allow protocol=TCP localport={port} profile=private,domain",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
            Logger.Info($"Firewall rule ensured for port {port}");
        }
        catch (Exception ex)
        {
            Logger.Error("Could not set firewall rule (app may need admin rights)", ex);
        }
    }

    private static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return "localhost";
        }
    }

    // ── Library Links tab ────────────────────────────────────────────────────


    // ══════════════════════════════════════════════════════════════════
    //  LINKS TAB — dual file browser
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Represents one row in either file browser.</summary>
    private enum BrowserIndexState { Normal, InIndex, NotInIndex }

    private sealed record BrowserEntry(
        string  DisplayName,    // shown in the list
        string  FullPath,       // absolute path on disk
        bool    IsGoUp,         // true for the ".." entry
        bool    IsDirectory,    // true for folder rows
        bool    IsLink,         // true for .rplink files (right browser)
        string  TargetFolder,   // right browser: directory of the resolved target (empty for folders/go-up)
        bool    IsBroken,       // right browser: true when the target file no longer exists
        bool    IsFolderLink = false,     // right browser: link that targets a directory
        string  FullTargetPath = "",      // right browser: fully resolved target path (raw, may not exist)
        BrowserIndexState IndexState = BrowserIndexState.Normal)   // set by Check Index command
    {
        public string TypeLabel    => IsGoUp ? "" : IsFolderLink ? "folder link" : IsDirectory ? "folder" : IsLink ? "link" : "Movie";
        // Exposed as string so XAML DataTriggers can bind without needing the private enum type.
        public string IndexStateLabel => IndexState.ToString();
        /// <summary>The display path for the Target column: for file links shows full target path including filename;
        /// for folder links shows the target folder path.</summary>
        public string TargetDisplay => !IsLink ? string.Empty
            : IsFolderLink ? FullTargetPath
            : FullTargetPath;  // includes filename since FullTargetPath is the raw resolved target
        // Tooltip shown on hover for link rows: shows resolved path with existence indicator
        public string TargetTooltip => !IsLink ? string.Empty
            : IsBroken
                ? $"❌  Target not found:\n{FullTargetPath}"
                : $"✅  {FullTargetPath}";
    }

    private string _leftDir  = string.Empty;
    private string _rightDir = string.Empty;

    /// <summary>Switches to the Links tab and pre-fills the left browser at the folder
    /// that contains <paramref name="targetPath"/>, with that file highlighted.</summary>
    private void NavigateToLinksTab(string targetPath)
    {
        if (MainTabs.Items.Count > 2)
            MainTabs.SelectedIndex = 2;

        var dir = Path.GetDirectoryName(targetPath);
        if (dir is not null && Directory.Exists(dir))
            PopulateLeftBrowser(dir);
    }

    private void OnMainTabsSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 2)
            RefreshBothBrowsers();
    }

    private void RefreshBothBrowsers()
    {
        var root = _config.ResolvedMoviesPath;

        // Seed from persisted config on first open; fall back to library root.
        if (string.IsNullOrWhiteSpace(_leftDir))
            _leftDir  = Directory.Exists(_config.LinkBrowserLeftDir)  ? _config.LinkBrowserLeftDir  : root;
        if (string.IsNullOrWhiteSpace(_rightDir))
            _rightDir = Directory.Exists(_config.LinkBrowserRightDir) ? _config.LinkBrowserRightDir : root;

        if (!Directory.Exists(_leftDir))  _leftDir  = root;
        if (!Directory.Exists(_rightDir)) _rightDir = root;

        if (!Directory.Exists(_leftDir) || !Directory.Exists(_rightDir))
        {
            LeftBrowser.ItemsSource  = Array.Empty<BrowserEntry>();
            RightBrowser.ItemsSource = Array.Empty<BrowserEntry>();
            return;
        }

        PopulateLeftBrowser(_leftDir);
        PopulateRightBrowser(_rightDir);
        UpdateStaleLinkBadge();
    }

    /// <summary>Updates the stale-link warning badge in the toolbar based on the background scan result.</summary>
    private void UpdateStaleLinkBadge()
    {
        var count = _webServer?.LibraryStatus.StaleLinkCount ?? 0;
        if (count > 0)
        {
            StaleLinkBadge.Text       = $"⚠️  {count} broken link{(count == 1 ? "" : "s")} detected";
            StaleLinkBadge.Visibility = Visibility.Visible;
        }
        else
        {
            StaleLinkBadge.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Persists both browser directories into config so they survive app restart.</summary>
    private void SaveBrowserDirs()
    {
        var updated = AppConfig.WithBrowserDirs(_config, _leftDir, _rightDir);
        _config = updated;
        _appConfigService.Save(updated);
    }

    /// <summary>Breadcrumb data item — bound to the breadcrumb <c>ItemsControl</c> in XAML.</summary>
    private sealed record BreadcrumbItem(string Name, string Dir);

    /// <summary>Updates the breadcrumb <c>ItemsControl</c> for a browser pane to reflect <paramref name="dir"/>.</summary>
    private void UpdateBreadcrumbs(System.Windows.Controls.ItemsControl control, string dir)
    {
        var root = _config.ResolvedMoviesPath;
        if (!Directory.Exists(dir) || !Directory.Exists(root))
        {
            control.ItemsSource = Array.Empty<BreadcrumbItem>();
            return;
        }

        // Build ordered list of (name, absolutePath) segments from root down to dir.
        var normRoot   = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normTarget = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var crumbs = new List<BreadcrumbItem>
        {
            new(Path.GetFileName(normRoot), normRoot)
        };

        var relative = Path.GetRelativePath(normRoot, normTarget);
        if (relative != ".")
        {
            var current = normRoot;
            foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                crumbs.Add(new(part, current));
            }
        }

        control.ItemsSource = crumbs;
    }

    private void PopulateLeftBrowser(string dir)
    {
        _leftDir = dir;
        UpdateBreadcrumbs(LeftBreadcrumbs, dir);
        SaveBrowserDirs();

        if (!Directory.Exists(dir))
        {
            LeftBrowser.ItemsSource = Array.Empty<BrowserEntry>();
            return;
        }

        var root = _config.ResolvedMoviesPath;
        var exts = _config.EffectiveVideoFileExtensions
            .Select(e => e.ToLowerInvariant()).ToHashSet();

        var entries = new List<BrowserEntry>();

        // ".." entry — always shown unless we are at root
        if (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            entries.Add(new BrowserEntry("..", Path.GetDirectoryName(dir) ?? root,
                IsGoUp: true, IsDirectory: true, IsLink: false, TargetFolder: "", IsBroken: false));

        // Sub-folders
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x))
            entries.Add(new BrowserEntry(Path.GetFileName(d), d,
                IsGoUp: false, IsDirectory: true, IsLink: false, TargetFolder: "", IsBroken: false));

        // .rplink files — resolve target to get folder and broken status
        foreach (var f in Directory.EnumerateFiles(dir, "*" + RplinkHelper.Extension)
            .OrderBy(x => x))
        {
            var target        = RplinkHelper.TryReadTarget(f);
            var rawTarget     = RplinkHelper.TryReadTargetRaw(f) ?? string.Empty;
            var isFolderLink  = RplinkHelper.IsTargetFolder(f);
            var isBroken      = target is null;
            var targetFolder  = target is not null
                ? (isFolderLink ? target : Path.GetDirectoryName(target) ?? string.Empty)
                : string.Empty;

            entries.Add(new BrowserEntry(Path.GetFileNameWithoutExtension(f), f,
                IsGoUp: false, IsDirectory: isFolderLink, IsLink: true,
                TargetFolder: targetFolder, IsBroken: isBroken, IsFolderLink: isFolderLink,
                FullTargetPath: rawTarget));
        }

        // Video files
        foreach (var f in Directory.EnumerateFiles(dir)
            .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(x => x))
        {
            entries.Add(new BrowserEntry(Path.GetFileName(f), f,
                IsGoUp: false, IsDirectory: false, IsLink: false, TargetFolder: "", IsBroken: false));
        }

        LeftBrowser.ItemsSource = entries;
    }

    private void PopulateRightBrowser(string dir)
    {
        _rightDir = dir;
        UpdateBreadcrumbs(RightBreadcrumbs, dir);
        SaveBrowserDirs();

        if (!Directory.Exists(dir))
        {
            RightBrowser.ItemsSource = Array.Empty<BrowserEntry>();
            return;
        }

        var root = _config.ResolvedMoviesPath;
        var exts = _config.EffectiveVideoFileExtensions
            .Select(e => e.ToLowerInvariant()).ToHashSet();

        var entries = new List<BrowserEntry>();

        // ".." entry
        if (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            entries.Add(new BrowserEntry("..", Path.GetDirectoryName(dir) ?? root,
                IsGoUp: true, IsDirectory: true, IsLink: false, TargetFolder: "", IsBroken: false));

        // Sub-folders
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x))
            entries.Add(new BrowserEntry(Path.GetFileName(d), d,
                IsGoUp: false, IsDirectory: true, IsLink: false, TargetFolder: "", IsBroken: false));

        // .rplink files — resolve target to get folder and broken status
        foreach (var f in Directory.EnumerateFiles(dir, "*" + RplinkHelper.Extension)
            .OrderBy(x => x))
        {
            var target        = RplinkHelper.TryReadTarget(f);
            var rawTarget     = RplinkHelper.TryReadTargetRaw(f) ?? string.Empty;
            var isFolderLink  = RplinkHelper.IsTargetFolder(f);
            var isBroken      = target is null;
            var targetFolder  = target is not null
                ? (isFolderLink ? target : Path.GetDirectoryName(target) ?? string.Empty)
                : string.Empty;

            entries.Add(new BrowserEntry(Path.GetFileNameWithoutExtension(f), f,
                IsGoUp: false, IsDirectory: isFolderLink, IsLink: true,
                TargetFolder: targetFolder, IsBroken: isBroken, IsFolderLink: isFolderLink,
                FullTargetPath: rawTarget));
        }

        // Video files (same as left browser)
        foreach (var f in Directory.EnumerateFiles(dir)
            .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(x => x))
        {
            entries.Add(new BrowserEntry(Path.GetFileName(f), f,
                IsGoUp: false, IsDirectory: false, IsLink: false, TargetFolder: "", IsBroken: false));
        }

        RightBrowser.ItemsSource = entries;
    }

    // ── Sync left → right ────────────────────────────────────────────

    private void OnSyncLeftToRight(object sender, RoutedEventArgs e)
        => PopulateRightBrowser(_leftDir);

    // ── Left browser navigation ──────────────────────────────────────

    private void OnLeftBrowserDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LeftBrowser.SelectedItem is not BrowserEntry entry) return;
        if (entry.IsDirectory)
        {
            // Folder-links: navigate into the target folder rather than treating as a link.
            if (entry.IsFolderLink && !string.IsNullOrEmpty(entry.TargetFolder) && Directory.Exists(entry.TargetFolder))
                PopulateLeftBrowser(entry.TargetFolder);
            else if (!entry.IsFolderLink)
                PopulateLeftBrowser(entry.FullPath);
        }
    }

    private void OnLeftBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string dir && Directory.Exists(dir))
            PopulateLeftBrowser(dir);
    }

    // ── Right browser navigation ─────────────────────────────────────

    private void OnRightBrowserDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RightBrowser.SelectedItem is not BrowserEntry entry) return;
        // Folder-link rows: navigate into the target directory.
        if (entry.IsDirectory)
        {
            if (entry.IsFolderLink && !string.IsNullOrEmpty(entry.TargetFolder) && Directory.Exists(entry.TargetFolder))
                PopulateRightBrowser(entry.TargetFolder);
            else if (!entry.IsFolderLink)
                PopulateRightBrowser(entry.FullPath);
        }
    }

    private void OnRightBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string dir && Directory.Exists(dir))
            PopulateRightBrowser(dir);
    }

    // ══════════════════════════════════════════════════════════════════
    // Browser context menus — unified handler + helpers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves which browser triggered the menu and returns its ListView,
    /// current directory, opposite directory, and which side it is.
    /// </summary>
    private (System.Windows.Controls.ListView Browser, string CurrentDir, string OppositeDir, bool IsLeft)
        GetBrowserContext(object menuSender)
    {
        var cm = menuSender as ContextMenu
              ?? (menuSender as MenuItem)?.Parent as ContextMenu;
        var lv = cm?.PlacementTarget as System.Windows.Controls.ListView;
        bool isLeft = lv == LeftBrowser;
        return (lv ?? LeftBrowser, isLeft ? _leftDir : _rightDir,
                isLeft ? _rightDir : _leftDir, isLeft);
    }

    private void RefreshBrowser(bool isLeft)
    {
        if (isLeft) PopulateLeftBrowser(_leftDir);
        else        PopulateRightBrowser(_rightDir);
    }

    private static string OppositeSide(bool isLeft) => isLeft ? "right" : "left";

    private void OnBrowserContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;

        bool isLeft      = cm.Tag?.ToString() == "Left";
        var  lv          = isLeft ? LeftBrowser : RightBrowser;
        var  currentDir  = isLeft ? _leftDir : _rightDir;
        var  oppositeDir = isLeft ? _rightDir : _leftDir;
        var  oppSide     = OppositeSide(isLeft);

        var selected = lv.SelectedItems.OfType<BrowserEntry>().Where(x => !x.IsGoUp).ToList();

        // Reset any previous index-check colouring when the menu re-opens.
        // Rebuild items with Normal IndexState so rows revert to default white.
        if (lv.ItemsSource is IEnumerable<BrowserEntry> currentItems
            && currentItems.Any(x => x.IndexState != BrowserIndexState.Normal))
        {
            var reset = currentItems
                .Select(x => x.IndexState == BrowserIndexState.Normal ? x : x with { IndexState = BrowserIndexState.Normal })
                .ToList();
            lv.ItemsSource = null;
            lv.ItemsSource = reset;
        }

        bool hasAny       = selected.Count > 0;
        bool singleItem   = selected.Count == 1;
        bool anyFiles     = selected.Any(x => !x.IsDirectory && !x.IsLink);
        bool anyLinks     = selected.Any(x => x.IsLink);
        bool anyFolders   = selected.Any(x => x.IsDirectory && !x.IsLink);
        bool singleFolder = singleItem && selected[0].IsDirectory && !selected[0].IsLink;
        bool singleFile   = singleItem && !selected[0].IsDirectory && !selected[0].IsLink;
        bool singleLink   = singleItem && selected[0].IsLink;
        bool onlyFiles    = hasAny && !anyLinks && !anyFolders;
        bool onlyLinks    = hasAny && !anyFiles && !anyFolders;
        bool onlyFolders  = hasAny && !anyFiles && !anyLinks;

        var indexStatus       = _webServer?.LibraryStatus;
        bool indexReady       = indexStatus is { IsScanning: false } && indexStatus.CompletedUtc is not null;
        bool currentDirExists = Directory.Exists(currentDir);
        bool oppDirExists     = Directory.Exists(oppositeDir);
        bool dirsAreDifferent = !string.Equals(currentDir, oppositeDir, StringComparison.OrdinalIgnoreCase);

        // Show only the group that matches the selection type.
        // When mixed or empty — show everything disabled so the menu isn't blank.
        bool showFiles   = onlyFiles   || (!hasAny && !anyLinks && !anyFolders);
        bool showLinks   = onlyLinks;
        bool showFolders = onlyFolders;

        if (!showFiles && !showLinks && !showFolders)
            showFiles = showLinks = showFolders = true;

        int fileCount = selected.Count(x => !x.IsDirectory && !x.IsLink);
        int linkCount = selected.Count(x => x.IsLink);

        foreach (var item in cm.Items)
        {
            string tag = item switch
            {
                MenuItem mi => mi.Tag?.ToString() ?? string.Empty,
                Separator s => s.Tag?.ToString() ?? string.Empty,
                _           => string.Empty
            };

            if (item is Separator sep)
            {
                sep.Visibility = tag switch
                {
                    "SepFileFolder"  => showFiles && (showLinks || showFolders) ? Visibility.Visible : Visibility.Collapsed,
                    "SepLinkFolder"  => showLinks && showFolders                ? Visibility.Visible : Visibility.Collapsed,
                    "SepCheckIndex"  => Visibility.Visible,
                    _                => Visibility.Visible
                };
                continue;
            }

            if (item is not MenuItem mi2) continue;

            switch (tag)
            {
                // ── File group ──────────────────────────────────────────
                case "CreateLinks":
                    mi2.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = $"🔗  Create link(s) to {oppSide}";
                    mi2.IsEnabled  = onlyFiles && oppDirExists;
                    break;

                case "FindLinks":
                    mi2.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = "🔎  Find links for this file";
                    mi2.IsEnabled  = singleFile;
                    break;

                case "RenameFile":
                    mi2.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = "✏️  Rename file";
                    mi2.IsEnabled  = singleFile;
                    break;

                case "DeleteFiles":
                    mi2.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = fileCount > 1 ? $"🗑️  Delete {fileCount} files" : "🗑️  Delete file";
                    mi2.IsEnabled  = onlyFiles;
                    break;

                case "MoveFiles":
                    mi2.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = fileCount > 1 ? $"📦  Move {fileCount} files to {oppSide}" : $"📦  Move file to {oppSide}";
                    mi2.IsEnabled  = onlyFiles && oppDirExists && dirsAreDifferent;
                    break;

                // ── Link group ──────────────────────────────────────────
                case "RenameLink":
                    mi2.Visibility = showLinks ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = "✏️  Rename link";
                    mi2.IsEnabled  = singleLink;
                    break;

                case "MoveLink":
                    mi2.Visibility = showLinks ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = $"📦  Move link to {oppSide}";
                    mi2.IsEnabled  = onlyLinks && oppDirExists && dirsAreDifferent;
                    break;

                case "BulkRetarget":
                    mi2.Visibility = showLinks ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = linkCount > 1 ? $"🎯  Retarget {linkCount} broken links…" : "🎯  Retarget broken link…";
                    mi2.IsEnabled  = onlyLinks && selected.Any(x => x.IsLink && x.IsBroken);
                    break;

                case "AutoHeal":
                    mi2.Visibility = showLinks ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = "🩹  Auto-heal broken link";
                    mi2.IsEnabled  = singleLink && selected[0].IsBroken;
                    break;

                case "DeleteLinks":
                    mi2.Visibility = showLinks ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = linkCount > 1 ? $"🗑️  Delete {linkCount} links" : "🗑️  Delete link";
                    mi2.IsEnabled  = onlyLinks;
                    break;

                // ── Folder group ────────────────────────────────────────
                case "CreateFolderLink":
                    mi2.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = $"🔗  Create folder link to {oppSide}";
                    mi2.IsEnabled  = singleFolder && oppDirExists;
                    break;

                case "MoveFolder":
                    mi2.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = $"📂  Move folder to {oppSide}";
                    mi2.IsEnabled  = singleFolder && indexReady && oppDirExists && dirsAreDifferent;
                    break;

                case "CreateFolder":
                    mi2.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = "📁  Create folder here";
                    mi2.IsEnabled  = currentDirExists;
                    break;

                case "RenameFolder":
                    mi2.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = "✏️  Rename folder";
                    mi2.IsEnabled  = singleFolder;
                    break;

                case "DeleteFolder":
                    mi2.Visibility = showFolders ? Visibility.Visible : Visibility.Collapsed;
                    mi2.Header     = "🗑️  Delete folder";
                    mi2.IsEnabled  = singleFolder;
                    break;

                case "CheckIndex":
                    mi2.Header    = "🔍  Check Index";
                    mi2.IsEnabled = indexReady && currentDirExists;
                    break;
            }
        }
    }

    // ── Create Link(s) ────────────────────────────────────────────────

    private void OnBrowserCreateLinks(object sender, RoutedEventArgs e)
    {
        var (browser, _, oppositeDir, isLeft) = GetBrowserContext(sender);

        var selected = browser.SelectedItems
            .OfType<BrowserEntry>()
            .Where(x => !x.IsGoUp && !x.IsDirectory && !x.IsLink)
            .ToList();

        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            try { _webServer?.CreateRplink(item.FullPath, oppositeDir); }
            catch { /* silently skip failed items */ }
        }

        // Refresh the opposite side which received the new links
        if (isLeft) PopulateRightBrowser(_rightDir);
        else        PopulateLeftBrowser(_leftDir);
    }

    // ── Create folder link in opposite browser ───────────────────────

    private void OnBrowserCreateFolderLink(object sender, RoutedEventArgs e)
    {
        var (browser, _, oppositeDir, isLeft) = GetBrowserContext(sender);
        if (browser.SelectedItem is not BrowserEntry entry || !entry.IsDirectory || entry.IsGoUp) return;

        var folderName = Path.GetFileName(entry.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var linkPath   = Path.Combine(oppositeDir, folderName + RplinkHelper.Extension);

        if (File.Exists(linkPath))
        {
            DarkMessageBox.Show(
                $"A link named \"{folderName}\" already exists in the destination:\n{linkPath}",
                "Create Folder Link", this);
            return;
        }

        try
        {
            var stored = RplinkHelper.MakeRelativeIfPossible(linkPath, entry.FullPath);
            RplinkHelper.Create(linkPath, stored);
            _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
            _webServer?.IndexAddOrUpdateFile(linkPath);
            // Refresh the opposite browser where the link was created
            if (isLeft) PopulateRightBrowser(_rightDir);
            else        PopulateLeftBrowser(_leftDir);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show("Could not create folder link: " + ex.Message, "Error", this);
        }
    }

    // ── Move folder to opposite browser location ──────────────────────

    private void OnBrowserMoveFolder(object sender, RoutedEventArgs e)
    {
        var (browser, currentDir, oppositeDir, isLeft) = GetBrowserContext(sender);

        if (browser.SelectedItem is not BrowserEntry entry || !entry.IsDirectory || entry.IsGoUp)
            return;

        var sourceDir   = entry.FullPath;
        var folderName  = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destDir     = Path.Combine(oppositeDir, folderName);
        var libraryRoot = _config.ResolvedMoviesPath;

        if (Directory.Exists(destDir))
        {
            DarkMessageBox.Show(
                $"A folder named \"{folderName}\" already exists in the destination:\n{destDir}",
                "Move Folder", this);
            return;
        }

        if (destDir.StartsWith(sourceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceDir, destDir, StringComparison.OrdinalIgnoreCase))
        {
            DarkMessageBox.Show(
                "Cannot move a folder into itself or one of its subfolders.",
                "Move Folder", this);
            return;
        }

        List<string> affectedLinks;
        try
        {
            affectedLinks = FolderOperationsHelper.FindLinksPointingIntoFolder(libraryRoot, sourceDir).ToList();
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not scan for affected links:\n{ex.Message}",
                "Move Folder", this);
            return;
        }

        var linkWord    = affectedLinks.Count == 1 ? "link" : "links";
        var linkSummary = affectedLinks.Count == 0
            ? "No links point into this folder — only the folder will be moved."
            : $"{affectedLinks.Count} {linkWord} point into this folder and will be retargeted automatically.";

        if (!DarkMessageBox.Confirm(
                $"Move folder:\n  {sourceDir}\n\nTo:\n  {destDir}\n\n{linkSummary}\n\nContinue?",
                "Move Folder", this)) return;

        try { Directory.Move(sourceDir, destDir); }
        catch (Exception ex)
        {
            DarkMessageBox.Show($"Move failed:\n{ex.Message}", "Move Folder", this);
            return;
        }

        int rewroteOk = 0, rewroteFailed = 0;
        foreach (var linkFile in affectedLinks)
        {
            try
            {
                var raw = RplinkHelper.TryReadTargetRaw(linkFile);
                if (raw is null) continue;

                var newTarget = string.Equals(raw, sourceDir, StringComparison.OrdinalIgnoreCase)
                    ? destDir
                    : destDir + raw[sourceDir.Length..];

                var stored = RplinkHelper.MakeRelativeIfPossible(linkFile, newTarget);
                RplinkHelper.Create(linkFile, stored);
                rewroteOk++;
            }
            catch { rewroteFailed++; }
        }

        _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
        _webServer?.IndexRenamePrefix(sourceDir, destDir);
        PopulateLeftBrowser(_leftDir);
        PopulateRightBrowser(_rightDir);

        if (rewroteFailed > 0)
        {
            DarkMessageBox.Show(
                $"Folder moved. {rewroteOk} link(s) updated, {rewroteFailed} could not be rewritten.",
                "Move Folder", this);
        }
    }

    // ── Create folder ─────────────────────────────────────────────────

    private void OnBrowserCreateFolder(object sender, RoutedEventArgs e)
    {
        var (_, currentDir, _, isLeft) = GetBrowserContext(sender);
        CreateFolderIn(currentDir, onDone: () => RefreshBrowser(isLeft));
    }

    private void CreateFolderIn(string parentDir, Action onDone)
    {
        if (!Directory.Exists(parentDir)) return;

        var dlg = new RenameDialog("New Folder") { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var name = dlg.NewName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var newPath = Path.Combine(parentDir, name);
        try
        {
            Directory.CreateDirectory(newPath);
            onDone();
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show("Could not create folder: " + ex.Message, "Error", this);
        }
    }

    // ── Delete folder ─────────────────────────────────────────────────

    private void OnBrowserDeleteFolder(object sender, RoutedEventArgs e)
    {
        var (browser, _, _, isLeft) = GetBrowserContext(sender);
        if (browser.SelectedItem is not BrowserEntry entry || !entry.IsDirectory || entry.IsGoUp) return;
        DeleteFolder(entry.FullPath, onDone: () => RefreshBrowser(isLeft));
    }

    private void DeleteFolder(string folderPath, Action onDone)
    {
        var (totalFiles, linksInsideFolder) = FolderOperationsHelper.CountFolderContents(folderPath);

        var libraryRoot = _config.ResolvedMoviesPath;
        var indexStatus = _webServer?.LibraryStatus;
        bool indexReady = indexStatus is { IsScanning: false } && indexStatus.CompletedUtc is not null;

        int externalBrokenLinks;
        if (indexReady && _webServer is not null)
            externalBrokenLinks = _webServer.CountIndexedLinksPointingIntoFolder(folderPath);
        else if (Directory.Exists(libraryRoot))
            externalBrokenLinks = FolderOperationsHelper.FindLinksPointingIntoFolder(libraryRoot, folderPath).Count;
        else
            externalBrokenLinks = 0;

        var msg = $"Delete folder \"{Path.GetFileName(folderPath)}\"?\n\n" +
                  $"  Files inside this folder:            {totalFiles}\n" +
                  $"  Links (.rplink) inside this folder:  {linksInsideFolder}\n" +
                  $"  External links that will break:      {externalBrokenLinks}\n\n" +
                  "This cannot be undone.";

        if (!DarkMessageBox.Confirm(msg, "Confirm Delete Folder", this)) return;

        try
        {
            Directory.Delete(folderPath, recursive: true);
            _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
            _webServer?.IndexRemoveUnderPath(folderPath);
            onDone();
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show("Delete failed: " + ex.Message, "Error", this);
        }
    }

    // ── Move link(s) to opposite side (#11)

    private void OnBrowserMoveLink(object sender, RoutedEventArgs e)
    {
        var (browser, _, oppositeDir, isLeft) = GetBrowserContext(sender);

        var links = browser.SelectedItems
            .OfType<BrowserEntry>()
            .Where(x => x.IsLink)
            .ToList();

        if (links.Count == 0) return;
        if (!Directory.Exists(oppositeDir)) return;

        int moved = 0, failed = 0;
        string lastError = string.Empty;

        _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
        foreach (var link in links)
        {
            var dest = Path.Combine(oppositeDir, Path.GetFileName(link.FullPath));
            if (File.Exists(dest))
            {
                failed++;
                lastError = $"A link named '{Path.GetFileName(link.FullPath)}' already exists at the destination.";
                continue;
            }

            try
            {
                // Rewrite the stored target so it stays valid from the new location, then move.
                var raw = RplinkHelper.TryReadTargetRaw(link.FullPath);
                if (raw is not null)
                {
                    // Resolve to absolute before moving so relative paths can be recomputed.
                    var absolute = Path.IsPathRooted(raw) ? raw
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.FullPath)!, raw));
                    var stored = RplinkHelper.MakeRelativeIfPossible(dest, absolute);
                    File.Copy(link.FullPath, dest, overwrite: false);
                    RplinkHelper.Create(dest, stored);
                    File.Delete(link.FullPath);
                }
                else
                {
                    File.Move(link.FullPath, dest);
                }

                _webServer?.IndexRemoveUnderPath(link.FullPath);
                _webServer?.IndexAddOrUpdateFile(dest);
                moved++;
            }
            catch (Exception ex) { failed++; lastError = ex.Message; }
        }

        RefreshBrowser(isLeft);
        if (isLeft) PopulateRightBrowser(_rightDir);
        else        PopulateLeftBrowser(_leftDir);

        if (failed > 0)
        {
            DarkMessageBox.Show(
                $"{moved} link(s) moved, {failed} failed.\nLast error: {lastError}",
                "Move Link", this);
        }
    }

    // ── Bulk retarget broken link(s) (#10)

    private void OnBrowserBulkRetarget(object sender, RoutedEventArgs e)
    {
        var (browser, _, _, isLeft) = GetBrowserContext(sender);

        var links = browser.SelectedItems
            .OfType<BrowserEntry>()
            .Where(x => x.IsLink && x.IsBroken)
            .ToList();

        if (links.Count == 0) return;

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Select the new target folder for {links.Count} broken link(s)"
        };

        if (dlg.ShowDialog(this) != true) return;
        var newTargetDir = dlg.FolderName;

        int rewrote = 0, failed = 0;
        string lastError = string.Empty;

        _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
        foreach (var link in links)
        {
            try
            {
                // Build a new target: find a matching filename in the chosen folder, else use the folder itself.
                var currentRaw  = RplinkHelper.TryReadTargetRaw(link.FullPath) ?? string.Empty;
                var fileName    = Path.GetFileName(currentRaw);
                var candidate   = string.IsNullOrEmpty(fileName)
                    ? newTargetDir
                    : Path.Combine(newTargetDir, fileName);

                var newTarget = File.Exists(candidate) ? candidate : newTargetDir;
                var stored    = RplinkHelper.MakeRelativeIfPossible(link.FullPath, newTarget);
                RplinkHelper.Create(link.FullPath, stored);
                _webServer?.IndexAddOrUpdateFile(link.FullPath);
                rewrote++;
            }
            catch (Exception ex) { failed++; lastError = ex.Message; }
        }

        RefreshBrowser(isLeft);

        var msg = rewrote > 0
            ? $"Retargeted {rewrote} link(s) to:\n{newTargetDir}"
            : string.Empty;
        if (failed > 0)
            msg += $"\n{failed} link(s) could not be rewritten.\nLast error: {lastError}";

        if (!string.IsNullOrEmpty(msg))
            DarkMessageBox.Show(msg.Trim(), "Bulk Retarget", this);
    }

    // ── Auto-heal broken link (#2) ────────────────────────────────────

    private void OnBrowserAutoHeal(object sender, RoutedEventArgs e)
    {
        var (browser, _, _, isLeft) = GetBrowserContext(sender);
        if (browser.SelectedItem is not BrowserEntry entry || !entry.IsLink || !entry.IsBroken) return;

        var root = _config.ResolvedMoviesPath;
        if (!Directory.Exists(root)) return;

        var targetRaw  = RplinkHelper.TryReadTargetRaw(entry.FullPath) ?? string.Empty;
        var searchName = Path.GetFileName(targetRaw);

        if (string.IsNullOrEmpty(searchName))
        {
            DarkMessageBox.Show(
                "Cannot auto-heal: the stored target has no file name to search for.",
                "Auto-Heal", this);
            return;
        }

        // Find the first file anywhere under the library root that matches the name.
        string? found = null;
        try
        {
            found = Directory
                .EnumerateFiles(root, searchName, SearchOption.AllDirectories)
                .FirstOrDefault(f => !RplinkHelper.IsRplinkFile(f));
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Search failed: {ex.Message}", "Auto-Heal", this);
            return;
        }

        if (found is null)
        {
            DarkMessageBox.Show(
                $"No file named \"{searchName}\" was found anywhere under:\n{root}",
                "Auto-Heal", this);
            return;
        }

        var stored = RplinkHelper.MakeRelativeIfPossible(entry.FullPath, found);
        try
        {
            RplinkHelper.Create(entry.FullPath, stored);
            _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
            _webServer?.IndexAddOrUpdateFile(entry.FullPath);
            RefreshBrowser(isLeft);
            DarkMessageBox.Show(
                $"✅  Link retargeted to:\n{found}",
                "Auto-Heal", this);
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show(
                $"Could not rewrite link: {ex.Message}", "Auto-Heal", this);
        }
    }

    // ── Delete link(s) ────────────────────────────────────────────────

    private void OnBrowserDeleteLinks(object sender, RoutedEventArgs e)
    {
        var (browser, _, _, isLeft) = GetBrowserContext(sender);

        var links = browser.SelectedItems
            .OfType<BrowserEntry>()
            .Where(x => x.IsLink)
            .ToList();

        if (links.Count == 0) return;

        int failed = 0;
        string lastError = string.Empty;

        _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
        foreach (var link in links)
        {
            try
            {
                File.Delete(link.FullPath);
                _webServer?.IndexRemoveUnderPath(link.FullPath);
            }
            catch (Exception ex) { failed++; lastError = ex.Message; }
        }
        RefreshBrowser(isLeft);

        if (failed > 0)
        {
            DarkMessageBox.Show(
                $"{links.Count - failed} link(s) deleted, {failed} failed.\nLast error: {lastError}",
                "Delete Links", this);
        }
    }

    // ── Delete video file(s)

    private void OnBrowserDeleteFiles(object sender, RoutedEventArgs e)
    {
        var (browser, _, _, isLeft) = GetBrowserContext(sender);

        var files = browser.SelectedItems
            .OfType<BrowserEntry>()
            .Where(x => !x.IsDirectory && !x.IsLink && !x.IsGoUp)
            .ToList();

        if (files.Count == 0) return;

        var msg = files.Count == 1
            ? $"Permanently delete \"{files[0].DisplayName}\"?\n\nThis cannot be undone."
            : $"Permanently delete {files.Count} file(s)?\n\nThis cannot be undone.";

        if (!DarkMessageBox.Confirm(msg, "Confirm Delete", this)) return;

        int failed = 0;
        string lastError = string.Empty;

        _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
        foreach (var file in files)
        {
            try
            {
                File.Delete(file.FullPath);
                _webServer?.IndexRemoveUnderPath(file.FullPath);
            }
            catch (Exception ex) { failed++; lastError = ex.Message; }
        }
        RefreshBrowser(isLeft);

        if (failed > 0)
        {
            DarkMessageBox.Show(
                $"{files.Count - failed} file(s) deleted, {failed} failed.\nLast error: {lastError}",
                "Delete Files", this);
        }
    }

    // ── Move video file(s) to opposite panel ──────────────────────────

    private void OnBrowserMoveFiles(object sender, RoutedEventArgs e)
    {
        var (browser, _, oppositeDir, isLeft) = GetBrowserContext(sender);

        var files = browser.SelectedItems
            .OfType<BrowserEntry>()
            .Where(x => !x.IsDirectory && !x.IsLink && !x.IsGoUp)
            .ToList();

        if (files.Count == 0) return;
        if (!Directory.Exists(oppositeDir)) return;

        var fileWord = files.Count == 1 ? $"\"{files[0].DisplayName}\"" : $"{files.Count} files";
        var msg = $"Move {fileWord} to:\n{oppositeDir}\n\nAny links pointing to the moved file(s) will be retargeted automatically.";
        if (!DarkMessageBox.Confirm(msg, "Move Files", this)) return;

        int moved = 0, failed = 0;
        string lastError = string.Empty;

        _webServer?.SuppressWatcher(TimeSpan.FromSeconds(30));
        foreach (var file in files)
        {
            var dest = Path.Combine(oppositeDir, Path.GetFileName(file.FullPath));
            if (File.Exists(dest))
            {
                failed++;
                lastError = $"A file named \"{Path.GetFileName(file.FullPath)}\" already exists at the destination.";
                continue;
            }
            try
            {
                File.Move(file.FullPath, dest);
                RetargetLinksAfterFileRename(file.FullPath, dest);
                _webServer?.IndexRenameFile(file.FullPath, dest);
                moved++;
            }
            catch (Exception ex) { failed++; lastError = ex.Message; }
        }

        RefreshBrowser(isLeft);
        RefreshBrowser(!isLeft);

        if (failed > 0)
        {
            DarkMessageBox.Show(
                $"{moved} file(s) moved, {failed} failed.\nLast error: {lastError}",
                "Move Files", this);
        }
    }

    // ── Find links for file

    private void OnBrowserFindLinks(object sender, RoutedEventArgs e)
    {
        var (browser, _, _, _) = GetBrowserContext(sender);
        if (browser.SelectedItem is not BrowserEntry entry || entry.IsGoUp || entry.IsDirectory) return;
        Func<string, string[]?>? indexLookup = _webServer is { } ws ? ws.GetIndexedLinkSourcesForFile : null;
        var dlg = new FindLinksDialog(entry.FullPath, _config.ResolvedMoviesPath, indexLookup) { Owner = this };
        dlg.ShowDialog();
    }

    // ── Check Index

    private void OnBrowserCheckIndex(object sender, RoutedEventArgs e)
    {
        if (_webServer is null) return;

        var (lv, _, _, _) = GetBrowserContext(sender);

        var status = _webServer.LibraryStatus;

        // Guard: index not ready yet
        if (status.IsScanning)
        {
            DarkMessageBox.Show(
                "The library index is still scanning. Please wait for it to finish and then try again.",
                "Check Index", this);
            return;
        }

        if (status.CompletedUtc is null)
        {
            DarkMessageBox.Show(
                "The library index has not been built yet. A scan will start automatically — please try again once it completes.",
                "Check Index", this);
            _webServer.RequestLibraryRescan();
            return;
        }

        var indexed        = _webServer.GetIndexedPathSet();
        var ignoredFolders = _webServer.GetIgnoredFolderNames();

        if (indexed.Count == 0)
        {
            DarkMessageBox.Show(
                "The index appears to be empty. A library rescan has been triggered — re-run Check Index after the scan completes.",
                "Check Index", this);
            _webServer.RequestLibraryRescan();
            return;
        }

        if (lv.ItemsSource is not IEnumerable<BrowserEntry> current) return;

        // Classify each indexable entry
        int inIndex      = 0;
        int notInIndex   = 0;
        int ignoredCount = 0;

        var updated = current.Select(entry =>
        {
            // Only check entries that should be in the index:
            //   - regular video files (not a directory, not a link)
            //   - .rplink files that target a video file (IsLink=true, IsFolderLink=false)
            // Skip go-up entries, plain folders, and folder links — they are never indexed.
            bool shouldBeIndexed = !entry.IsGoUp
                && (!entry.IsDirectory || entry.IsLink)
                && !(entry.IsLink && entry.IsFolderLink);

            if (!shouldBeIndexed) return entry;

            // Check whether this entry lives inside an ignored folder — if so, it is
            // intentionally excluded from the index; don't flag it as missing.
            bool isInIgnoredFolder = IsPathUnderIgnoredFolder(entry.FullPath, ignoredFolders);
            if (isInIgnoredFolder)
            {
                ignoredCount++;
                return entry; // leave Normal (white) — excluded by design
            }

            if (indexed.Contains(Path.GetFullPath(entry.FullPath)))
            {
                inIndex++;
                return entry with { IndexState = BrowserIndexState.InIndex };
            }

            notInIndex++;
            return entry with { IndexState = BrowserIndexState.NotInIndex };
        }).ToList();

        lv.ItemsSource = null;
        lv.ItemsSource = updated;

        if (notInIndex > 0)
        {
            _webServer.RequestLibraryRescan();
            var ignored = ignoredCount > 0 ? $"\n⬜  {ignoredCount} file(s) are in ignored folders (excluded by design)." : string.Empty;
            DarkMessageBox.Show(
                $"⚠️  {notInIndex} file(s) are NOT in the index (shown in red).\n" +
                $"✅  {inIndex} file(s) are in the index (shown in green).{ignored}\n\n" +
                "A library rescan has been triggered — re-run Check Index after the scan completes.",
                "Check Index", this);
        }
        else if (inIndex > 0)
        {
            var ignored = ignoredCount > 0 ? $"\n⬜  {ignoredCount} file(s) are in ignored folders and are excluded by design." : string.Empty;
            DarkMessageBox.Show(
                $"✅  All {inIndex} file(s) in this folder are in the index.{ignored}",
                "Check Index", this);
        }
        else if (ignoredCount > 0)
        {
            DarkMessageBox.Show(
                $"⬜  All {ignoredCount} file(s) here are inside ignored folders (e.g. Subs, Alt) and are intentionally excluded from the index.",
                "Check Index", this);
        }
        else
        {
            DarkMessageBox.Show(
                "No indexable files (movies or links) found in this folder.",
                "Check Index", this);
        }
    }

    /// <summary>Returns true when any segment of <paramref name="path"/> matches an ignored folder name.</summary>
    private static bool IsPathUnderIgnoredFolder(string path, IReadOnlySet<string> ignoredFolderNames)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (ignoredFolderNames.Contains(Path.GetFileName(dir)))
                return true;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break; // reached root
            dir = parent;
        }
        return false;
    }

    // ── Rename ────────────────────────────────────────────────────────

    private void OnBrowserRename(object sender, RoutedEventArgs e)
    {
        var (browser, _, _, isLeft) = GetBrowserContext(sender);
        if (browser.SelectedItem is not BrowserEntry entry || entry.IsGoUp) return;
        RenameEntry(entry, isLink: entry.IsLink, onDone: () => RefreshBrowser(isLeft));
    }

    /// <summary>Shows a rename dialog and renames the file/folder/link on disk.
    /// When renaming a video file, any .rplink files pointing to the old path are retargeted automatically.</summary>
    private void RenameEntry(BrowserEntry entry, bool isLink, Action onDone)
    {
        // For files and links we show the name without extension so the user
        // doesn't accidentally double the extension (e.g. "movie.mp4" -> "movie.mp4.mp4").
        var currentName = (isLink || (!entry.IsDirectory))
            ? Path.GetFileNameWithoutExtension(entry.FullPath)
            : Path.GetFileName(entry.FullPath);

        var dlg = new RenameDialog(currentName) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var newBaseName = dlg.NewName.Trim();
        if (string.IsNullOrWhiteSpace(newBaseName) || newBaseName == currentName) return;

        try
        {
            string dir  = Path.GetDirectoryName(entry.FullPath)!;
            string dest = isLink
                ? Path.Combine(dir, newBaseName + RplinkHelper.Extension)
                : entry.IsDirectory
                    ? Path.Combine(dir, newBaseName)
                    : Path.Combine(dir, newBaseName + Path.GetExtension(entry.FullPath));

            if (entry.IsDirectory)
                Directory.Move(entry.FullPath, dest);
            else
                File.Move(entry.FullPath, dest);

            // When a video file is renamed, retarget any .rplink files that pointed to the old path.
            if (!isLink && !entry.IsDirectory)
                RetargetLinksAfterFileRename(entry.FullPath, dest);

            // When a folder is renamed, retarget any .rplink files whose targets live inside it.
            if (entry.IsDirectory)
                RetargetLinksAfterFolderRename(entry.FullPath, dest);

            _webServer?.SuppressWatcher(TimeSpan.FromSeconds(10));
            if (entry.IsDirectory)
                _webServer?.IndexRenamePrefix(entry.FullPath, dest);
            else
                _webServer?.IndexRenameFile(entry.FullPath, dest);
            onDone();
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show("Rename failed: " + ex.Message, "Error", this);
        }
    }

    /// <summary>Scans the library root for .rplink files pointing to <paramref name="oldPath"/> and
    /// rewrites them to point to <paramref name="newPath"/>.</summary>
    private void RetargetLinksAfterFileRename(string oldPath, string newPath)
    {
        // Prefer the in-memory index (no disk I/O) when it contains link source paths.
        var indexedSources = _webServer?.GetIndexedLinkSourcesForFile(oldPath);
        IEnumerable<string> linkFiles;

        if (indexedSources is { Length: > 0 })
        {
            linkFiles = indexedSources;
        }
        else
        {
            var libraryRoot = _config.ResolvedMoviesPath;
            if (!Directory.Exists(libraryRoot)) return;
            linkFiles = Directory.EnumerateFiles(libraryRoot, "*" + RplinkHelper.Extension, SearchOption.AllDirectories);
        }

        try
        {
            foreach (var linkFile in linkFiles)
            {
                var raw = RplinkHelper.TryReadTargetRaw(linkFile);
                if (raw is null) continue;
                if (!string.Equals(raw, oldPath, StringComparison.OrdinalIgnoreCase)) continue;

                var stored = RplinkHelper.MakeRelativeIfPossible(linkFile, newPath);
                RplinkHelper.Create(linkFile, stored);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"RetargetLinksAfterFileRename failed", ex);
        }
    }

    /// <summary>Finds every .rplink file whose resolved target falls inside <paramref name="oldFolderPath"/>
    /// (or one of its sub-folders) and rewrites the target so it points into <paramref name="newFolderPath"/> instead.</summary>
    private void RetargetLinksAfterFolderRename(string oldFolderPath, string newFolderPath)
    {
        var libraryRoot = _config.ResolvedMoviesPath;
        if (!Directory.Exists(libraryRoot)) return;

        // Normalise the old folder path so prefix comparisons are reliable.
        var oldPrefix = Path.GetFullPath(oldFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
        var newPrefix = Path.GetFullPath(newFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;

        // Prefer the in-memory index to avoid a full disk scan when possible.
        IEnumerable<string> linkFiles;
        var allSources = _webServer?.GetIndexedPathSet()
            .Where(p => p.EndsWith(RplinkHelper.Extension, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        linkFiles = (allSources is { Length: > 0 })
            ? allSources
            : Directory.EnumerateFiles(libraryRoot, "*" + RplinkHelper.Extension, SearchOption.AllDirectories);

        var retargeted = 0;
        try
        {
            foreach (var linkFile in linkFiles)
            {
                var raw = RplinkHelper.TryReadTargetRaw(linkFile);
                if (raw is null) continue;

                // Resolve to an absolute path so relative links work correctly.
                var resolved = Path.IsPathRooted(raw)
                    ? raw
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkFile)!, raw));

                if (!resolved.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = resolved.Substring(oldPrefix.Length);
                var updatedTarget = Path.Combine(newPrefix, relative);
                var stored = RplinkHelper.MakeRelativeIfPossible(linkFile, updatedTarget);
                RplinkHelper.Create(linkFile, stored);
                retargeted++;
            }

            if (retargeted > 0)
                Logger.Info($"RetargetLinksAfterFolderRename: retargeted {retargeted} link(s) from '{oldFolderPath}' -> '{newFolderPath}'");
        }
        catch (Exception ex)
        {
            Logger.Error("RetargetLinksAfterFolderRename failed", ex);
        }
    }

    // ── Explorer context menu registration ───────────────────────────

    private void OnRegisterContextMenu(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

            const string keyPath = @"Software\Classes\*\shell\RemotePlay.CreateLink";
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue(null, "Create RemotePlay link here…");
                key.SetValue("Icon", $"\"{exePath}\",0");
            }
            using (var cmd = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath + @"\command"))
            {
                cmd.SetValue(null, $"\"{exePath}\" --create-link \"%1\"");
            }

            RegisterContextMenuBtn.Content = "✓  Registered — right-click a video in Explorer";
            RegisterContextMenuBtn.Background = System.Windows.Media.Brushes.DarkGreen;
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show("Could not register context menu: " + ex.Message, "Error", this);
        }
    }

    private string BuildVideoFilter()
    {
        var exts = _config.EffectiveVideoFileExtensions;
        var patterns = string.Join(";", exts.Select(ex => "*" + ex));
        return $"Video files ({patterns})|{patterns}|All files (*.*)|*.*";
    }

    // ── Broken link scanner (#1) ──────────────────────────────────────

    /// <summary>One row in the broken-link scan results list.</summary>
    private sealed record BrokenScanEntry(
        string LinkFile,        // full path to the .rplink file
        string StoredTarget,    // the raw path stored inside the file
        string ParentDir)       // folder that contains the .rplink file
    {
        public string DisplayName => Path.GetFileNameWithoutExtension(LinkFile);
        public string Tooltip     => $"Link file: {LinkFile}\nStored target: {StoredTarget}";
    }

    private void OnScanBrokenLinks(object sender, RoutedEventArgs e)
    {
        var root = _config.ResolvedMoviesPath;
        if (!Directory.Exists(root))
        {
            BrokenLinksPanel.Visibility = Visibility.Visible;
            BrokenLinksStatus.Text      = "Library root does not exist.";
            BrokenLinksList.ItemsSource = null;
            return;
        }

        var broken = Directory
            .EnumerateFiles(root, "*" + RplinkHelper.Extension, SearchOption.AllDirectories)
            .Select(f =>
            {
                var raw = RplinkHelper.TryReadTargetRaw(f) ?? string.Empty;
                return new BrokenScanEntry(f, raw, Path.GetDirectoryName(f) ?? root);
            })
            .Where(e => RplinkHelper.TryReadTarget(e.LinkFile) is null)
            .OrderBy(e => e.LinkFile)
            .ToList();

        BrokenLinksPanel.Visibility = Visibility.Visible;
        BrokenLinksStatus.Text = broken.Count == 0
            ? "✅  No broken links found."
            : $"⚠️  {broken.Count} broken link{(broken.Count == 1 ? "" : "s")} found:";
        BrokenLinksList.ItemsSource = broken;
    }

    private void OnHideBrokenLinksPanel(object sender, RoutedEventArgs e)
        => BrokenLinksPanel.Visibility = Visibility.Collapsed;

    private void OnDeleteBrokenLink(object sender, RoutedEventArgs e)
    {
        if (BrokenLinksList.SelectedItem is not BrokenScanEntry entry) return;
        try
        {
            File.Delete(entry.LinkFile);
            PopulateRightBrowser(_rightDir);
            OnScanBrokenLinks(sender, e); // refresh list
        }
        catch (Exception ex)
        {
            DarkMessageBox.Show("Delete failed: " + ex.Message, "Error", this);
        }
    }

    // ── Reverse lookup: find all links pointing at a file (#5) ───────

    private void OnFindLinksForFile(object sender, RoutedEventArgs e)
    {
        if (LeftBrowser.SelectedItem is not BrowserEntry entry || entry.IsGoUp || entry.IsDirectory) return;

        Func<string, string[]?>? indexLookup = _webServer is { } ws2 ? ws2.GetIndexedLinkSourcesForFile : null;
        var dlg = new FindLinksDialog(entry.FullPath, _config.ResolvedMoviesPath, indexLookup) { Owner = this };
        dlg.ShowDialog();
    }
}

/// <summary>Item model for the music audio output device ComboBox.</summary>
internal sealed record MusicAudioDevice(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}
