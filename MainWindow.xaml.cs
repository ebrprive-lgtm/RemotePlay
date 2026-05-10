using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RemotePlay;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private WebServer? _webServer;
    private readonly DispatcherTimer _bannerTimer;
    private bool _isPaused;
    private bool _isVideoMode;

    public MainWindow()
    {
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Error("Unhandled exception", e.ExceptionObject as Exception);
            AppendLog($"[CRASH] {e.ExceptionObject}");
        };

        _config = AppConfig.Load();

        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _bannerTimer.Tick += (_, _) => HideBanner();

        VideoPlayer.MediaEnded += OnMediaEnded;
        VideoPlayer.MediaFailed += OnMediaFailed;

        Loaded += OnLoaded;
        Closing += (_, _) => _webServer?.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var ip = GetLocalIp();
        IpAddressText.Text = $"http://{ip}:{_config.Port}";
        IdleIpText.Text = $"Open  http://{ip}:{_config.Port}  on your phone or tablet";
        MoviesPathText.Text = $"Movies folder: {_config.ResolvedMoviesPath}";

        MoviesFolderBox.Text = _config.ResolvedMoviesPath;
        PortBox.Text = _config.Port.ToString();

        AppendLog($"RemotePlay started");
        AppendLog($"URL  : http://{ip}:{_config.Port}");
        AppendLog($"Movies: {_config.ResolvedMoviesPath}");

        EnsureFirewallRule(_config.Port);

        _webServer = new WebServer(_config, PlayMovie, StopMovie, TogglePause);
        try
        {
            _webServer.Start();
            ServerStatusText.Text = $"✅ Server running on :{_config.Port}";
            AppendLog($"Web server listening on port {_config.Port}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start web server", ex);
            ServerStatusText.Text = $"❌ Server failed on :{_config.Port}";
            AppendLog($"ERROR starting server: {ex.Message}");
            ShowDiag($"Server failed: {ex.Message}");
        }
    }

    // ── View toggle ─────────────────────────────────────────────────────────

    private void OnToggleView(object sender, RoutedEventArgs e)
    {
        _isVideoMode = !_isVideoMode;

        if (_isVideoMode)
        {
            MainTabs.Visibility = Visibility.Collapsed;
            VideoPanel.Visibility = Visibility.Visible;
            ToggleViewBtn.Content = "🪵  Switch to Log";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
        }
        else
        {
            VideoPanel.Visibility = Visibility.Collapsed;
            MainTabs.Visibility = Visibility.Visible;
            ToggleViewBtn.Content = "▶  Switch to Video";
            Topmost = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
        }
    }

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
        AppendLog($"Port      : {_config.Port}");

        // Check if port is actually listening
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners();
            var listening = listeners.Any(l => l.Port == _config.Port);
            AppendLog($"Port {_config.Port} listening: {(listening ? "YES ✅" : "NO ❌")}");
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
            AppendLog($"Firewall rule: {(hasRule ? "EXISTS ✅" : "MISSING ❌ — click 'Test Port' again as Admin")}");
        }
        catch (Exception ex)
        {
            AppendLog($"Firewall check failed: {ex.Message}");
        }

        AppendLog($"--- End Diagnostics ---");
        LogScroller.ScrollToBottom();
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    private void PlayMovie(string filePath)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                _isPaused = false;
                VideoPlayer.Source = new Uri(filePath, UriKind.Absolute);
                VideoPlayer.Play();
                IdleOverlay.Visibility = Visibility.Collapsed;
                NowPlayingText.Text = "▶  " + Path.GetFileNameWithoutExtension(filePath);
                ShowBanner();
                AppendLog($"Playing: {Path.GetFileName(filePath)}");
                Logger.Info($"Playing: {filePath}");

                // Auto-switch to video mode when a movie starts
                if (!_isVideoMode) OnToggleView(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                Logger.Error("Error starting playback", ex);
                AppendLog($"ERROR playing: {ex.Message}");
            }
        });
    }

    private void StopMovie()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                VideoPlayer.Stop();
                VideoPlayer.Source = null;
                _isPaused = false;
                IdleOverlay.Visibility = Visibility.Visible;
                HideBanner();
                AppendLog("Playback stopped");
                Logger.Info("Playback stopped");
            }
            catch (Exception ex)
            {
                Logger.Error("Error stopping playback", ex);
                AppendLog($"ERROR stopping: {ex.Message}");
            }
        });
    }

    private void TogglePause()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_isPaused)
                {
                    VideoPlayer.Play();
                    _isPaused = false;
                    NowPlayingText.Text = NowPlayingText.Text.Replace("⏸", "▶");
                }
                else
                {
                    VideoPlayer.Pause();
                    _isPaused = true;
                    NowPlayingText.Text = NowPlayingText.Text.Replace("▶", "⏸");
                    ShowBanner();
                    _bannerTimer.Stop();
                }
                AppendLog(_isPaused ? "Paused" : "Resumed");
            }
            catch (Exception ex)
            {
                Logger.Error("Error toggling pause", ex);
                AppendLog($"ERROR pause/resume: {ex.Message}");
            }
        });
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        IdleOverlay.Visibility = Visibility.Visible;
        HideBanner();
        AppendLog("Playback finished");
        Logger.Info("Playback finished");
    }

    private void OnMediaFailed(object sender, System.Windows.ExceptionRoutedEventArgs e)
    {
        Logger.Error("Media playback failed", e.ErrorException);
        IdleOverlay.Visibility = Visibility.Visible;
        AppendLog($"MEDIA ERROR: {e.ErrorException?.Message ?? "unknown"}");
        ShowDiag("Playback error — unsupported format or missing codec");
    }

    // ── Settings ─────────────────────────────────────────────────────────────

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

    private void OnApplySettings(object sender, RoutedEventArgs e)
    {
        var folder = MoviesFolderBox.Text.Trim();
        var portText = PortBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            ShowSettingsFeedback("⚠️ Folder does not exist. Please choose a valid path.", isError: true);
            return;
        }

        if (!int.TryParse(portText, out var port) || port < 1024 || port > 65535)
        {
            ShowSettingsFeedback("⚠️ Port must be a number between 1024 and 65535.", isError: true);
            return;
        }

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                new { Port = port, MoviesPath = folder },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var configFile = Path.Combine(AppContext.BaseDirectory, "remoteplay.json");
            File.WriteAllText(configFile, json);

            // Restart the web server with new config
            _webServer?.Stop();
            var newConfig = AppConfig.Load();
            _webServer = new WebServer(newConfig, PlayMovie, StopMovie, TogglePause);
            _webServer.Start();

            MoviesPathText.Text = $"Movies folder: {newConfig.ResolvedMoviesPath}";
            ServerStatusText.Text = $"✅ Server running on :{newConfig.Port}";
            AppendLog($"Settings applied — folder: {folder}, port: {port}");
            Logger.Info($"Settings updated — MoviesPath: {folder}, Port: {port}");
            ShowSettingsFeedback("✅ Settings saved and server restarted.", isError: false);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply settings", ex);
            ShowSettingsFeedback($"❌ Error: {ex.Message}", isError: true);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private void ShowBanner()
    {
        _bannerTimer.Stop();
        NowPlayingBanner.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        if (!_isPaused)
            _bannerTimer.Start();
    }

    private void HideBanner()
    {
        _bannerTimer.Stop();
        NowPlayingBanner.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
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
