using System.IO;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using RemotePlay.Helpers;
using RemotePlay.Models;

namespace RemotePlay;

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
    private WebServer? _webServer;
    private readonly DispatcherTimer _bannerTimer;
    private readonly DispatcherTimer _overlayTimer;
    private readonly DispatcherTimer _historyTimer;
    private readonly DispatcherTimer _fullscreenWatchdogTimer;
    private bool _isPaused;
    private bool _isVideoMode;
    private string? _currentFilePath;
    private TimeSpan? _pendingResumePosition;
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
    private TimeSpan _duration = TimeSpan.Zero;
    private bool _isApplyingMoviePreferences;
    private readonly List<string> _playbackQueue = [];
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

            _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _bannerTimer.Tick += (_, _) => HideBanner();

            _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _overlayTimer.Tick += (_, _) => HideVideoOverlay();

            _historyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _historyTimer.Tick += (_, _) => SaveCurrentPlaybackPosition();

            _fullscreenWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _fullscreenWatchdogTimer.Tick += (_, _) => EnsureFullscreenWindowBounds();

            _mediaPlayer.Playing += OnMediaPlaying;
            _mediaPlayer.EndReached += OnMediaEnded;
            _mediaPlayer.EncounteredError += OnMediaFailed;
            _mediaPlayer.LengthChanged += OnMediaLengthChanged;
            ApplyAudioLevel();
            ApplyBrightnessOverlay();

            Loaded += OnLoaded;
            Closing += (_, _) =>
            {
                SaveCurrentPlaybackPosition();
                _webServer?.Stop();
                _mediaPlayer.Dispose();
                _libVlc.Dispose();
            };

            Logger.Info("MainWindow constructor completed");
        }
        catch (Exception ex)
        {
            Logger.Error("MainWindow constructor failed", ex);
            throw;
        }

        RefreshCurrentDisplayText();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var ip = GetLocalIp();
        MoviesPathText.Text = $"Movies folder: {_config.ResolvedMoviesPath}";

        MoviesFolderBox.Text = _config.ResolvedMoviesPath;
        PortBox.Text = _config.Port.ToString();
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
        if (FindName("PreferredDisplayCombo") is System.Windows.Controls.ComboBox displayCombo)
        {
            displayCombo.Items.Clear();
            displayCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Primary screen (default)", Tag = -1 });
            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                var label = $"Screen {i + 1}  ({s.Bounds.Width}×{s.Bounds.Height}){(s.Primary ? "  [Primary]" : "")}";
                displayCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = i });
            }
            var savedIdx = _config.PreferredDisplayIndex;
            foreach (System.Windows.Controls.ComboBoxItem item in displayCombo.Items)
                if (item.Tag is int tag2 && tag2 == savedIdx)
                    displayCombo.SelectedItem = item;
            if (displayCombo.SelectedItem is null)
                displayCombo.SelectedIndex = 0;
        }

        // Pre-initialize the VLC HwndHost so its native window is created and
        // painted black before the user ever enters fullscreen.  Without this the
        // very first fullscreen entry shows a white surface because the HWND didn't
        // exist yet and VLC hasn't had a chance to paint it.
        VideoPanel.Visibility = Visibility.Visible;
        Dispatcher.InvokeAsync(() =>
        {
            VideoPanel.Visibility = Visibility.Collapsed;
        }, System.Windows.Threading.DispatcherPriority.Render);

        AppendLog($"RemotePlay started");
        AppendLog($"Requested URL: {_config.Scheme}://{ip}:{_config.Port}");
        AppendLog($"Movies: {_config.ResolvedMoviesPath}");

        EnsureFirewallRule(_config.Port);

        _webServer = CreateWebServer(_config);
        try
        {
            _webServer.Start();
            UpdateServerUrlDisplay(ip, _webServer.ActiveScheme, _config.Port, _webServer.StartupWarning);
            ServerStatusText.Text = $"? Server running on {_webServer.ActiveScheme}://*:{_config.Port}";
            AppendLog($"Web server listening on {_webServer.ActiveScheme}://*:{_config.Port}");
            if (!string.IsNullOrWhiteSpace(_webServer.StartupWarning))
                ShowDiag(_webServer.StartupWarning);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start web server", ex);
            ServerStatusText.Text = $"? Server failed on {_config.Scheme}://*:{_config.Port}";
            AppendLog($"ERROR starting server: {ex.Message}");
            ShowDiag($"Server failed: {ex.Message}");
        }
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

    private void OnToggleView(object sender, RoutedEventArgs e)
    {
        _isVideoMode = !_isVideoMode;

        if (_isVideoMode)
        {
            MainTabs.Visibility = Visibility.Collapsed;
            AppToolbar.Visibility = Visibility.Collapsed;
            ToggleViewBtn.Content = "\uD83E\uDEB5  Switch to Log";
            EnsureFullscreenWindowBounds(force: true);
            _fullscreenWatchdogTimer.Start();
            VideoPanel.Visibility = Visibility.Visible;
            HideVideoOverlay(immediate: true);
            if (_currentFilePath is null)
                ShowIdleOverlay();
        }
        else
        {
            _fullscreenWatchdogTimer.Stop();
            _overlayTimer.Stop();
            HideVideoOverlay(immediate: true);
            VideoPanel.Visibility = Visibility.Collapsed;
            AppToolbar.Visibility = Visibility.Visible;
            MainTabs.Visibility = Visibility.Visible;
            ToggleViewBtn.Content = "\u25B6  Switch to Video";
            Topmost = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
        }
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
            var label = $"Screen {i + 1}  ({screen.Bounds.Width}×{screen.Bounds.Height}){(screen.Primary ? "  [Primary]" : string.Empty)}";
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
            currentDisplayText.Text = $"Current fullscreen target: {screenName} ({screen.Bounds.Width}×{screen.Bounds.Height}){(screen.Primary ? " [Primary]" : string.Empty)}. Saved preference: {savedDisplay}.";
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
        var bounds = targetScreen.Bounds;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        WindowState = WindowState.Maximized;

        if (!force)
        {
            Logger.Info($"Fullscreen watchdog repaired bounds. Target={bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}; Current={diagnostics.WindowLeft},{diagnostics.WindowTop},{diagnostics.WindowWidth}x{diagnostics.WindowHeight}");
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
            }).ToArray()
        };
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _isVideoMode)
        {
            OnToggleView(this, new RoutedEventArgs());
            e.Handled = true;
        }
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
            var logFile = Path.Combine(AppContext.BaseDirectory, "remoteplay.log");
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

    private void OnTestPort(object sender, RoutedEventArgs e)
    {
        AppendLog($"--- Network Diagnostics ---");
        AppendLog($"Local IP  : {GetLocalIp()}");
        AppendLog($"Scheme    : {_config.Scheme}");
        AppendLog($"Port      : {_config.Port}");

        // Check if port is actually listening
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners();
            var listening = listeners.Any(l => l.Port == _config.Port);
            AppendLog($"Port {_config.Port} listening: {(listening ? "YES ?" : "NO ?")}");
        }
        catch (Exception ex)
        {
            AppendLog($"Port check failed: {ex.Message}");
        }

        // Firewall rule status
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=\"RemotePlay\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            var hasRule = output.Contains("RemotePlay");
            AppendLog($"Firewall rule: {(hasRule ? "EXISTS ?" : "MISSING ? — click 'Test Port' again as Admin")}");
        }
        catch (Exception ex)
        {
            AppendLog($"Firewall check failed: {ex.Message}");
        }

        AppendLog($"--- End Diagnostics ---");
        LogScroller.ScrollToBottom();
    }


    private void OnSettingsCat(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        var tag = btn.Tag?.ToString() ?? "";

        // Hide all panels
        foreach (var name in new[] { "PanelLibrary", "PanelServer", "PanelPlayback", "PanelTracks", "PanelDisplay" })
            if (FindName(name) is System.Windows.UIElement el)
                el.Visibility = Visibility.Collapsed;

        // Show selected panel
        if (FindName("Panel" + tag) is System.Windows.UIElement panel)
            panel.Visibility = Visibility.Visible;

        // Update sidebar highlight
        foreach (var name in new[] { "SettingsCatLibrary", "SettingsCatServer", "SettingsCatPlayback", "SettingsCatTracks", "SettingsCatDisplay" })
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
        var portText = PortBox.Text.Trim();
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

        var preferredDisplayIndex = _config.PreferredDisplayIndex;
        if (FindName("PreferredDisplayCombo") is System.Windows.Controls.ComboBox displayCombo2
            && displayCombo2.SelectedItem is System.Windows.Controls.ComboBoxItem displayItem
            && displayItem.Tag is int displayTag)
            preferredDisplayIndex = displayTag;

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
                preferredDisplayIndex);

            _config = _settingsApplyService.ApplyAndReload(updatedConfig);
            _playbackHistory.Trim(_config.PlaybackHistoryLimit);

            // Restart the web server with new config
            _webServer?.Stop();
            _webServer = CreateWebServer(_config);
            _webServer.Start();

            MoviesPathText.Text = $"Movies folder: {_config.ResolvedMoviesPath}";
            RefreshDisplaySettings();
            var ip = GetLocalIp();
            UpdateServerUrlDisplay(ip, _webServer.ActiveScheme, _config.Port, _webServer.StartupWarning);
            ServerStatusText.Text = $"? Server running on {_webServer.ActiveScheme}://*:{_config.Port}";
            AppendLog($"Settings applied — folder: {folder}, requested scheme: {_config.Scheme}, active scheme: {_webServer.ActiveScheme}, port: {port}");
            Logger.Info($"Settings updated — MoviesPath: {folder}, RequestedScheme: {_config.Scheme}, ActiveScheme: {_webServer.ActiveScheme}, Port: {port}");
            if (string.IsNullOrWhiteSpace(_webServer.StartupWarning))
                ShowSettingsFeedback("? Settings saved and server restarted.", isError: false);
            else
                ShowSettingsFeedback("?? Settings saved, but HTTPS failed and HTTP fallback is active. See Status tab for details.", isError: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply settings", ex);
            ShowSettingsFeedback($"? Error: {ex.Message}", isError: true);
        }
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
            var qrUri = new Uri($"{activeUrl}/setup-code.png?url={Uri.EscapeDataString(activeUrl)}");
            if (FindName("SetupQrImage") is System.Windows.Controls.Image setupQrImage)
                setupQrImage.Source = new BitmapImage(qrUri);
            if (FindName("FullscreenQrImage") is System.Windows.Controls.Image fullscreenQrImage)
                fullscreenQrImage.Source = new BitmapImage(qrUri);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to update setup QR image", ex);
        }
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
}
