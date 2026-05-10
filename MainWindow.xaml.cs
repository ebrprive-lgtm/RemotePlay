using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace RemotePlay;

public partial class MainWindow : Window
{
    private AppConfig _config;
    private readonly PlaybackHistory _playbackHistory = new();
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private WebServer? _webServer;
    private readonly DispatcherTimer _bannerTimer;
    private readonly DispatcherTimer _overlayTimer;
    private readonly DispatcherTimer _historyTimer;
    private bool _isPaused;
    private bool _isVideoMode;
    private string? _currentFilePath;
    private TimeSpan? _pendingResumePosition;
    private string _lastPlaybackError = string.Empty;
    private double _brightness;
    private double _volume = 1;
    private double _audioBoost = 1;
    private double _playbackSpeed = 1;
    private bool _subtitlesEnabled = true;
    private int? _lastSubtitleTrackId;
    private bool _hasSubtitles;
    private TimeSpan _duration = TimeSpan.Zero;
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub"];

    public MainWindow()
    {
        try
        {
            Logger.Info("MainWindow constructor started");
            InitializeComponent();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Logger.Error("Unhandled exception", e.ExceptionObject as Exception);
                AppendLog($"[CRASH] {e.ExceptionObject}");
            };

            _config = AppConfig.Load();
            _volume = Math.Clamp(_config.Volume, 0, 1);
            _brightness = Math.Clamp(_config.Brightness, 0, 1);
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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var ip = GetLocalIp();
        MoviesPathText.Text = $"Movies folder: {_config.ResolvedMoviesPath}";

        MoviesFolderBox.Text = _config.ResolvedMoviesPath;
        PortBox.Text = _config.Port.ToString();
        if (FindName("UseHttpsBox") is System.Windows.Controls.CheckBox useHttpsBox)
            useHttpsBox.IsChecked = _config.UseHttps;

        AppendLog($"RemotePlay started");
        AppendLog($"Requested URL: {_config.Scheme}://{ip}:{_config.Port}");
        AppendLog($"Movies: {_config.ResolvedMoviesPath}");

        EnsureFirewallRule(_config.Port);

        _webServer = CreateWebServer(_config);
        try
        {
            _webServer.Start();
            UpdateServerUrlDisplay(ip, _webServer.ActiveScheme, _config.Port, _webServer.StartupWarning);
            ServerStatusText.Text = $"✅ Server running on {_webServer.ActiveScheme}://*:{_config.Port}";
            AppendLog($"Web server listening on {_webServer.ActiveScheme}://*:{_config.Port}");
            if (!string.IsNullOrWhiteSpace(_webServer.StartupWarning))
                ShowDiag(_webServer.StartupWarning);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start web server", ex);
            ServerStatusText.Text = $"❌ Server failed on {_config.Scheme}://*:{_config.Port}";
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
            AppToolbar.Visibility = Visibility.Collapsed;
            VideoPanel.Visibility = Visibility.Visible;
            HideVideoOverlay(immediate: true);
            ToggleViewBtn.Content = "\uD83E\uDEB5  Switch to Log";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
        }
        else
        {
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

    // ── Overlay auto-hide ────────────────────────────────────────────────────

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
        if (!_isVideoMode)
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

    // ── Log / diagnostics

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
                _lastPlaybackError = string.Empty;
                _currentFilePath = filePath;
                _pendingResumePosition = null;
                _duration = TimeSpan.Zero;
                _lastSubtitleTrackId = null;
                HideIdleOverlay();
                using var media = new Media(_libVlc, new Uri(filePath, UriKind.Absolute));
                media.AddOption(":avcodec-hw=none");
                _hasSubtitles = TryAttachSubtitle(media, filePath);
                _mediaPlayer.Play(media);
                _mediaPlayer.SetRate((float)_playbackSpeed);
                ApplyAudioLevel();
                ApplyBrightnessOverlay();
                _historyTimer.Start();
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
                SaveCurrentPlaybackPosition();
                _historyTimer.Stop();
                _mediaPlayer.Stop();
                _currentFilePath = null;
                _pendingResumePosition = null;
                _isPaused = false;
                _duration = TimeSpan.Zero;
                _hasSubtitles = false;
                ShowIdleOverlay();
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
                    _mediaPlayer.Play();
                    _isPaused = false;
                    NowPlayingText.Text = NowPlayingText.Text.Replace("⏸", "▶");
                }
                else
                {
                    _mediaPlayer.Pause();
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

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!string.IsNullOrWhiteSpace(_currentFilePath))
                _playbackHistory.Clear(_currentFilePath);

            _historyTimer.Stop();
            _currentFilePath = null;
            _pendingResumePosition = null;
            _isPaused = false;
            _duration = TimeSpan.Zero;
            _hasSubtitles = false;
            ShowIdleOverlay();
            HideBanner();
            AppendLog("Playback finished");
            Logger.Info("Playback finished");
        });
    }

    private void OnMediaPlaying(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            HideIdleOverlay();

            var preferredSubtitleTrack = GetPreferredSubtitleTrackId();
            _hasSubtitles = preferredSubtitleTrack is not null;
            if (_hasSubtitles)
            {
                if (_subtitlesEnabled)
                {
                    _mediaPlayer.SetSpu(preferredSubtitleTrack!.Value);
                    _lastSubtitleTrackId = preferredSubtitleTrack.Value;
                }
                else
                {
                    _mediaPlayer.SetSpu(-1);
                }
            }

            if (string.IsNullOrWhiteSpace(_currentFilePath) || _duration <= TimeSpan.Zero)
                return;

            var resumePosition = _playbackHistory.GetResumePosition(_currentFilePath, _duration);
            if (resumePosition is null)
                return;

            _pendingResumePosition = resumePosition;
            _mediaPlayer.Time = (long)resumePosition.Value.TotalMilliseconds;
            AppendLog($"Resumed from {resumePosition.Value:mm\\:ss}");
        });
    }

    private void OnMediaLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => _duration = TimeSpan.FromMilliseconds(Math.Max(0, e.Length)));
    }

    private void OnMediaFailed(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _lastPlaybackError = "VLC playback failed";
            _historyTimer.Stop();
            Logger.Error("Media playback failed");
            ShowIdleOverlay();
            AppendLog("MEDIA ERROR: VLC playback failed");
            ShowDiag("Playback error — VLC could not play this file");
        });
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

    private void OnExportCertificate(object sender, RoutedEventArgs e)
    {
        try
        {
            using var certificate = WebServer.TryGetHttpsCertificate();
            if (certificate is null)
            {
                ShowSettingsFeedback("⚠️ No HTTPS certificate exists yet. Enable HTTPS once first.", isError: true);
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
            ShowSettingsFeedback($"✅ Certificate exported: {dialog.FileName}", isError: false);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to export HTTPS certificate", ex);
            ShowSettingsFeedback($"❌ Certificate export failed: {ex.Message}", isError: true);
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
            ShowSettingsFeedback($"❌ Could not open health page: {ex.Message}", isError: true);
        }
    }

    private void OnApplySettings(object sender, RoutedEventArgs e)
    {
        var folder = MoviesFolderBox.Text.Trim();
        var portText = PortBox.Text.Trim();
        var useHttps = FindName("UseHttpsBox") is System.Windows.Controls.CheckBox useHttpsBox && useHttpsBox.IsChecked == true;

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
            AppConfig.Save(new AppConfig
            {
                Port = port,
                UseHttps = useHttps,
                MoviesPath = folder,
                Volume = _volume,
                Brightness = _brightness,
                AudioBoost = _audioBoost,
                PlaybackSpeed = _playbackSpeed,
                SubtitlesEnabled = _subtitlesEnabled
            });

            // Restart the web server with new config
            _webServer?.Stop();
            _config = AppConfig.Load();
            _webServer = CreateWebServer(_config);
            _webServer.Start();

            MoviesPathText.Text = $"Movies folder: {_config.ResolvedMoviesPath}";
            var ip = GetLocalIp();
            UpdateServerUrlDisplay(ip, _webServer.ActiveScheme, _config.Port, _webServer.StartupWarning);
            ServerStatusText.Text = $"✅ Server running on {_webServer.ActiveScheme}://*:{_config.Port}";
            AppendLog($"Settings applied — folder: {folder}, requested scheme: {_config.Scheme}, active scheme: {_webServer.ActiveScheme}, port: {port}");
            Logger.Info($"Settings updated — MoviesPath: {folder}, RequestedScheme: {_config.Scheme}, ActiveScheme: {_webServer.ActiveScheme}, Port: {port}");
            if (string.IsNullOrWhiteSpace(_webServer.StartupWarning))
                ShowSettingsFeedback("✅ Settings saved and server restarted.", isError: false);
            else
                ShowSettingsFeedback("⚠️ Settings saved, but HTTPS failed and HTTP fallback is active. See Status tab for details.", isError: true);
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

    private void HideIdleOverlay()
    {
        IdleOverlay.Visibility = Visibility.Collapsed;
        IdleOverlay.IsHitTestVisible = false;

        if (VideoPanel.FindName("VideoBrightnessOverlay") is UIElement brightnessOverlay)
            brightnessOverlay.Visibility = Visibility.Visible;

        ApplyBrightnessOverlay();
    }

    private void ShowIdleOverlay()
    {
        IdleOverlay.Visibility = Visibility.Visible;
        IdleOverlay.IsHitTestVisible = true;

        // Keep idle screen dark even when brightness overlay had been raised during playback.
        if (VideoPanel.FindName("VideoBrightnessOverlay") is UIElement brightnessOverlay)
        {
            brightnessOverlay.Opacity = 0;
            brightnessOverlay.Visibility = Visibility.Visible;
        }
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

    // Returns current playback state for the web interface
    private PlaybackStatus GetPlaybackStatus()
    {
        var result = new PlaybackStatus();
        Dispatcher.Invoke(() =>
        {
            var hasSubtitles = _hasSubtitles;
            var subtitlesEnabled = _subtitlesEnabled;
            try
            {
                var subtitleTracks = _mediaPlayer.SpuDescription;
                if (subtitleTracks is { Length: > 0 } && subtitleTracks.Any(t => t.Id >= 0))
                    hasSubtitles = true;

                var currentSpu = _mediaPlayer.Spu;
                subtitlesEnabled = currentSpu != -1;
                if (currentSpu >= 0)
                    _lastSubtitleTrackId = currentSpu;
            }
            catch (Exception ex)
            {
                Logger.Error("Could not inspect subtitle tracks", ex);
            }

            result = new PlaybackStatus
            {
                IsPlaying = _mediaPlayer.Media is not null && _mediaPlayer.State is not VLCState.Stopped and not VLCState.NothingSpecial and not VLCState.Ended,
                IsPaused = _isPaused,
                PositionSeconds = Math.Max(0, _mediaPlayer.Time / 1000d),
                DurationSeconds = Math.Max(0, _duration.TotalSeconds),
                Title = NowPlayingText.Text,
                Volume = _volume,
                IsMuted = _mediaPlayer.Mute,
                LastError = _lastPlaybackError,
                CanResume = _pendingResumePosition is not null,
                Brightness = _brightness,
                AudioBoost = _audioBoost,
                PlaybackSpeed = _playbackSpeed,
                SubtitlesEnabled = subtitlesEnabled,
                HasSubtitles = hasSubtitles
            };
        });
        return result;
    }

    private void SeekTo(double seconds)
    {
        Dispatcher.Invoke(() =>
        {
            try { _mediaPlayer.Time = (long)Math.Max(0, seconds * 1000); }
            catch (Exception ex) { Logger.Error("Seek failed", ex); }
        });
    }

    private void SkipBy(double seconds)
    {
        Dispatcher.Invoke(() =>
        {
            if (_mediaPlayer.Media is null)
                return;

            var target = TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)) + TimeSpan.FromSeconds(seconds);
            if (target < TimeSpan.Zero)
                target = TimeSpan.Zero;

            if (_duration > TimeSpan.Zero && target > _duration)
                target = _duration;

            _mediaPlayer.Time = (long)target.TotalMilliseconds;
        });
    }

    private void SetVolume(double volume)
    {
        Dispatcher.Invoke(() =>
        {
            _volume = Math.Clamp(volume, 0, 1);
            ApplyAudioLevel();
            SavePlaybackPreferences();
        });
    }

    private void ToggleMute()
    {
        Dispatcher.Invoke(() => _mediaPlayer.Mute = !_mediaPlayer.Mute);
    }

    private void SetBrightness(double brightness)
    {
        Dispatcher.Invoke(() =>
        {
            _brightness = Math.Clamp(brightness, 0, 1);
            ApplyBrightnessOverlay();
            SavePlaybackPreferences();
        });
    }

    private void SetAudioBoost(double audioBoost)
    {
        Dispatcher.Invoke(() =>
        {
            _audioBoost = Math.Clamp(audioBoost, 1, 2);
            ApplyAudioLevel();
            SavePlaybackPreferences();
        });
    }

    private void SetPlaybackSpeed(double playbackSpeed)
    {
        Dispatcher.Invoke(() =>
        {
            _playbackSpeed = Math.Clamp(playbackSpeed, 0.5, 2);
            _mediaPlayer.SetRate((float)_playbackSpeed);
            SavePlaybackPreferences();
        });
    }

    private void ToggleSubtitles()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mediaPlayer.Media is null)
            {
                _subtitlesEnabled = !_subtitlesEnabled;
                SavePlaybackPreferences();
                return;
            }

            var currentSpu = _mediaPlayer.Spu;
            var currentlyEnabled = currentSpu != -1;
            if (currentlyEnabled && currentSpu >= 0)
                _lastSubtitleTrackId = currentSpu;

            if (currentlyEnabled)
            {
                _mediaPlayer.SetSpu(-1);
                _subtitlesEnabled = false;
            }
            else
            {
                var restoreTrack = GetPreferredSubtitleTrackId();
                if (restoreTrack is null)
                {
                    _hasSubtitles = false;
                    _subtitlesEnabled = false;
                }
                else
                {
                    _mediaPlayer.SetSpu(restoreTrack.Value);
                    _lastSubtitleTrackId = restoreTrack.Value;
                    _subtitlesEnabled = true;
                }
            }

            SavePlaybackPreferences();
        });
    }

    private void SaveCurrentPlaybackPosition()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath) || _mediaPlayer.Media is null)
            return;

        _playbackHistory.SavePosition(_currentFilePath, TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)), _duration);
    }

    private WebServer CreateWebServer(AppConfig config) =>
        new(config, PlayMovie, StopMovie, TogglePause, GetPlaybackStatus, SeekTo, SkipBy, SetVolume, ToggleMute, SetBrightness,
            SetAudioBoost, SetPlaybackSpeed, ToggleSubtitles);

    private void ApplyAudioLevel()
    {
        try
        {
            _mediaPlayer.Volume = (int)Math.Round(_volume * _audioBoost * 100);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply audio level", ex);
        }
    }

    private void ApplyBrightnessOverlay()
    {
        var nativeApplied = false;

        // Native VLC brightness uses 1.0 as normal.
        // Map slider 0..1 to VLC 0..2 so 50% = normal.
        try
        {
            _mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, 1);
            _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, (float)Math.Clamp(_brightness * 2, 0, 2));
            nativeApplied = true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply native VLC brightness", ex);
        }

        if (VideoPanel.FindName("VideoBrightnessOverlay") is UIElement brightnessOverlay)
        {
            // Only add extra highlight above 50% when native path is active.
            // Otherwise keep legacy overlay behavior as fallback.
            brightnessOverlay.Opacity = nativeApplied
                ? Math.Max(0, _brightness - 0.5) * 0.35
                : _brightness * 0.45;
        }
    }

    private void SavePlaybackPreferences()
    {
        AppConfig.Save(new AppConfig
        {
            Port = _config.Port,
            UseHttps = _config.UseHttps,
            MoviesPath = _config.MoviesPath,
            Volume = _volume,
            Brightness = _brightness,
            AudioBoost = _audioBoost,
            PlaybackSpeed = _playbackSpeed,
            SubtitlesEnabled = _subtitlesEnabled
        });
    }

    private bool TryAttachSubtitle(Media media, string filePath)
    {
        var subtitlePath = FindSubtitlePath(filePath);
        if (subtitlePath is null)
            return false;

        media.AddOption($":sub-file={subtitlePath}");
        media.AddOption(_subtitlesEnabled ? ":sub-track=0" : ":sub-track=-1");
        AppendLog($"Subtitle detected: {Path.GetFileName(subtitlePath)}");
        return true;
    }

    private int? GetPreferredSubtitleTrackId()
    {
        try
        {
            var subtitleTracks = _mediaPlayer.SpuDescription;
            if (subtitleTracks is null || subtitleTracks.Length == 0)
                return null;

            if (_lastSubtitleTrackId is int lastTrack && subtitleTracks.Any(t => t.Id == lastTrack))
                return lastTrack;

            var firstTrack = subtitleTracks
                .Where(t => t.Id >= 0)
                .OrderBy(t => t.Id)
                .FirstOrDefault();

            return firstTrack.Id >= 0 ? firstTrack.Id : null;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not resolve subtitle track", ex);
            return null;
        }
    }

    private static string? FindSubtitlePath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (var extension in SubtitleExtensions)
        {
            var exactPath = Path.Combine(directory, fileName + extension);
            if (File.Exists(exactPath))
                return exactPath;
        }

        foreach (var extension in SubtitleExtensions)
        {
            var match = Directory.EnumerateFiles(directory, fileName + ".*" + extension)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (match is not null)
                return match;
        }

        return null;
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
