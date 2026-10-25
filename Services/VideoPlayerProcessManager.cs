using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemotePlay.VideoPlayer.Ipc;

namespace RemotePlay.Services;

/// <summary>
/// Manages the isolated video player process, IPC communication, and health monitoring
/// </summary>
internal sealed class VideoPlayerProcessManager : IDisposable
{
    private const string VideoPlayerExecutableName = "RemotePlay.VideoPlayer.exe";
    private const int ReconnectTimeoutMs = 2_000;
    private static readonly TimeSpan CommandResponseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PingResponseTimeout = TimeSpan.FromSeconds(5);
    private readonly string _instanceId;
    private readonly Action<string> _logger;
    private Process? _process;
    private NamedPipeClientStream? _pipeClient;
    private StreamWriter? _pipeWriter;
    private StreamReader? _pipeReader;
    private bool _disposed;
    private bool _isConnected;  // explicit flag — NamedPipeClientStream.IsConnected is unreliable before first I/O
    private readonly SemaphoreSlim _startLock = new(1, 1);  // prevents concurrent StartAsync / process launches
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private DateTime _lastPongReceived = DateTime.MinValue;
    private PlaybackState _lastKnownState = new();
    private bool _fullscreenControlsEnabled = FullscreenControlsSettings.EnabledByDefault;
    private double _fullscreenControlsAutoHideDelaySeconds = FullscreenControlsSettings.DefaultAutoHideDelaySeconds;
    private StatusResponse? _lastStatus;

    public bool IsProcessRunning
    {
        get
        {
            var process = Volatile.Read(ref _process);
            if (process is null)
                return false;

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
    public bool IsConnected => _isConnected;
    public TimeSpan TimeSinceLastPong => DateTime.UtcNow - _lastPongReceived;
    public StatusResponse? LastStatus => Volatile.Read(ref _lastStatus);

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process is { HasExited: true } ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public VideoPlayerProcessManager(Action<string> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _instanceId = Guid.NewGuid().ToString();
        _logger = logger;
    }

    /// <summary>
    /// Starts the video player process and connects IPC
    /// </summary>
    public async Task<bool> StartAsync(int displayIndex = 0, CancellationToken ct = default)
    {
        // Prevent two callers from launching a second process concurrently
        await _startLock.WaitAsync(ct);
        try
        {
            return StartCore(displayIndex, ct);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private bool StartCore(int displayIndex, CancellationToken ct)
    {
        try
        {
            if (IsProcessRunning)
            {
                _logger("[VideoPlayerManager] Process already running");
                return true;
            }

            // Find the video player executable
            var exePath = FindVideoPlayerExecutable();
            if (exePath is null)
            {
                _logger("[VideoPlayerManager] ERROR: Could not find RemotePlay.VideoPlayer.exe");
                return false;
            }

            _logger($"[VideoPlayerManager] Launching: {exePath}");
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{_instanceId} {displayIndex}",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    // Ensure the child inherits a proper environment for WPF startup
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
                }
            };

            // Pipe the player's console output directly into the main app log
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _logger($"[VideoPlayer] {e.Data}");
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _logger($"[VideoPlayer ERR] {e.Data}");
            };

            if (!_process.Start())
            {
                _logger("[VideoPlayerManager] ERROR: Failed to start process");
                return false;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _logger($"[VideoPlayerManager] Process started (PID: {_process.Id})");

            // Connect to IPC — poll until the player's named-pipe server is ready
            // (WPF + LibVLC init can take 2-4 seconds)
            var connected = ConnectIpc(ct);
            if (!connected)
            {
                _logger("[VideoPlayerManager] ERROR: Failed to connect IPC — player may have crashed at startup");
                KillProcess();
                return false;
            }

            _logger("[VideoPlayerManager] IPC connected");
            _lastPongReceived = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            _logger($"[VideoPlayerManager] Start error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restarts the video player process and resumes playback at the last known position
    /// </summary>
    public async Task<bool> RestartAndResumeAsync(CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            _logger("[VideoPlayerManager] Restarting player process...");

            var lastKnownState = _lastKnownState with { };
            var lastFilePath = lastKnownState.FilePath;
            var lastPosition = lastKnownState.PositionSeconds;

            // Kill the old process
            KillProcess();
            await Task.Delay(500, ct);

            // Start the replacement process off the UI thread; connecting to its pipe can take several seconds.
            var started = await Task.Factory.StartNew(
                () => StartCore(lastKnownState.DisplayIndex, ct),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!started)
                return false;

            if (!await SendSetFullscreenControlsSettingsAsync(
                    _fullscreenControlsEnabled,
                    _fullscreenControlsAutoHideDelaySeconds,
                    ct))
            {
                _logger("[VideoPlayerManager] Fullscreen controls settings were not acknowledged after player restart.");
            }

            // Resume playback if we had a local file or an HTTP(S) stream playing.
            if (lastFilePath is not null && CanResumePlaybackPath(lastFilePath))
            {
                _logger($"[VideoPlayerManager] Resuming playback at {lastPosition:0.#}s");
                await Task.Delay(1000, ct); // Give process time to fully initialize

                var resumed = await SendPlayCommandAsync(new PlayCommand
                {
                    MessageType = nameof(PlayCommand),
                    FilePath = lastFilePath,
                    StartPositionSeconds = lastPosition,
                    IsLiveTv = lastKnownState.IsLiveTv,
                    Volume = lastKnownState.Volume,
                    Brightness = lastKnownState.Brightness,
                    Saturation = lastKnownState.Saturation,
                    Zoom = lastKnownState.Zoom,
                    AudioBoost = lastKnownState.AudioBoost,
                    PlaybackSpeed = lastKnownState.PlaybackSpeed,
                    SubtitlesEnabled = lastKnownState.SubtitlesEnabled,
                    TargetDisplayIndex = lastKnownState.DisplayIndex
                }, ct);

                return resumed;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger($"[VideoPlayerManager] Restart error: {ex.Message}");
            return false;
        }
        finally
        {
            _startLock.Release();
        }
    }

    internal static bool CanResumePlaybackPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (File.Exists(filePath))
            return true;

        return IsHttpStreamPath(filePath);
    }

    internal static bool IsHttpStreamPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        return Uri.TryCreate(filePath, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    internal static bool ShouldRecycleForHttpStreamSwitch(string? currentFilePath, string? nextFilePath) =>
        IsHttpStreamPath(currentFilePath) && IsHttpStreamPath(nextFilePath);

    private bool ConnectIpc(CancellationToken ct, int maxWaitMs = 30_000)
    {
        var pipeName = $"RemotePlay.VideoPlayer.{_instanceId}";
        _isConnected = false;

        // StartAsync runs off the UI thread. Keep startup pipe setup fully synchronous so
        // no continuation can be delayed between a successful connection and the first Ping.
        const int connectTimeoutMs = 2_000;
        const int retryDelayMs     = 250;
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            if (ct.IsCancellationRequested)
                return false;

            if (_process is not null && _process.HasExited)
            {
                _logger($"[VideoPlayerManager] Player process exited (code {_process.ExitCode}) before IPC connected");
                return false;
            }

            DisposePipeResources();
            _pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipeReader = new StreamReader(_pipeClient, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _pipeWriter = new StreamWriter(_pipeClient, new UTF8Encoding(false), leaveOpen: true);

            try
            {
                _pipeClient.Connect(connectTimeoutMs);
            }
            catch (TimeoutException)
            {
                Thread.Sleep(retryDelayMs);
                continue;
            }
            catch (Exception)
            {
                Thread.Sleep(retryDelayMs);
                continue;
            }

            _isConnected = true;
            _lastPongReceived = DateTime.UtcNow;
            return true;
        }

        _logger($"[VideoPlayerManager] IPC connect timed out after {sw.ElapsedMilliseconds}ms");
        DisposePipeResources();
        return false;
    }

    /// <summary>
    /// Sends a ping and waits for pong response
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var ping = new PingCommand { MessageType = nameof(PingCommand) };
            var response = await SendCommandAsync<PongResponse>(ping, ct, PingResponseTimeout);

            if (response?.MessageType == nameof(PongResponse))
            {
                _lastPongReceived = DateTime.UtcNow;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets current playback status
    /// </summary>
    public async Task<StatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var request = new StatusRequestCommand { MessageType = nameof(StatusRequestCommand) };
            var response = await SendCommandAsync<StatusResponse>(request, ct);

            if (response is not null)
            {
                Volatile.Write(ref _lastStatus, response);

                // Update last known state
                _lastKnownState = new PlaybackState
                {
                    FilePath = response.CurrentFilePath,
                    PositionSeconds = response.PositionSeconds,
                    IsPlaying = response.IsPlaying,
                    IsLiveTv = response.IsLiveTv,
                    DisplayIndex = _lastKnownState.DisplayIndex, // Preserve display
                    Volume = _lastKnownState.Volume,
                    Brightness = _lastKnownState.Brightness,
                    Saturation = _lastKnownState.Saturation,
                    Zoom = _lastKnownState.Zoom,
                    AudioBoost = _lastKnownState.AudioBoost,
                    PlaybackSpeed = _lastKnownState.PlaybackSpeed,
                    SubtitlesEnabled = _lastKnownState.SubtitlesEnabled
                };
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger($"[VideoPlayerManager] GetStatus error: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SendPlayCommandAsync(PlayCommand cmd, CancellationToken ct = default)
    {
        Volatile.Write(ref _lastStatus, new StatusResponse
        {
            MessageType = nameof(StatusResponse),
            CurrentFilePath = cmd.FilePath,
            PositionSeconds = cmd.StartPositionSeconds,
            IsLiveTv = cmd.IsLiveTv,
            PlayerState = "Opening"
        });

        // Update last known state
        _lastKnownState = new PlaybackState
        {
            FilePath = cmd.FilePath,
            PositionSeconds = cmd.StartPositionSeconds,
            IsPlaying = true,
            IsLiveTv = cmd.IsLiveTv,
            DisplayIndex = cmd.TargetDisplayIndex,
            Volume = cmd.Volume,
            Brightness = cmd.Brightness,
            Saturation = cmd.Saturation,
            Zoom = cmd.Zoom,
            AudioBoost = cmd.AudioBoost,
            PlaybackSpeed = cmd.PlaybackSpeed,
            SubtitlesEnabled = cmd.SubtitlesEnabled
        };

        var response = await SendCommandAsync<AcknowledgmentResponse>(cmd, ct);
        if (response?.Success == true)
            return true;

        if (response is null && IsProcessRunning && await ReconnectIpcAsync(ct).ConfigureAwait(false))
        {
            _logger("[VideoPlayerManager] Retrying PlayCommand after reconnecting IPC");
            response = await SendCommandAsync<AcknowledgmentResponse>(cmd, ct);
            if (response?.Success == true)
                return true;
        }

        _logger($"[VideoPlayerManager] Play command was not acknowledged: {response?.ErrorMessage ?? "no response"}");
        return false;
    }

    public async Task<bool> SendSetFullscreenControlsSettingsAsync(
        bool enabled,
        double autoHideDelaySeconds,
        CancellationToken ct = default)
    {
        if (!FullscreenControlsSettings.IsValidAutoHideDelaySeconds(autoHideDelaySeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(autoHideDelaySeconds),
                autoHideDelaySeconds,
                $"Fullscreen controls auto-hide delay must be between {FullscreenControlsSettings.MinimumAutoHideDelaySeconds} and {FullscreenControlsSettings.MaximumAutoHideDelaySeconds} seconds.");
        }

        _fullscreenControlsEnabled = enabled;
        _fullscreenControlsAutoHideDelaySeconds = autoHideDelaySeconds;
        var response = await SendCommandAsync<AcknowledgmentResponse>(new SetFullscreenControlsSettingsCommand
        {
            MessageType = nameof(SetFullscreenControlsSettingsCommand),
            Enabled = enabled,
            AutoHideDelaySeconds = autoHideDelaySeconds
        }, ct);

        return response?.Success == true;
    }

    public async Task<bool> SendSetAdjacentVideoTitlesAsync(
        string filePath,
        string? previousTitle,
        string? nextTitle,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var response = await SendCommandAsync<AcknowledgmentResponse>(new SetAdjacentVideoTitlesCommand
        {
            MessageType = nameof(SetAdjacentVideoTitlesCommand),
            FilePath = filePath,
            PreviousTitle = previousTitle,
            NextTitle = nextTitle
        }, ct);

        return response?.Success == true;
    }

    public Task SendPauseAsync(CancellationToken ct = default)
        => SendCommandAsync<AcknowledgmentResponse>(new PauseCommand { MessageType = nameof(PauseCommand) }, ct);

    public Task SendResumeAsync(CancellationToken ct = default)
        => SendCommandAsync<AcknowledgmentResponse>(new ResumeCommand { MessageType = nameof(ResumeCommand) }, ct);

    public Task SendStopAsync(CancellationToken ct = default)
    {
        _lastKnownState = new PlaybackState();
        return SendCommandAsync<AcknowledgmentResponse>(new StopCommand { MessageType = nameof(StopCommand) }, ct);
    }

    public Task SendSeekAsync(double positionSeconds, CancellationToken ct = default)
        => SendCommandAsync<AcknowledgmentResponse>(new SeekCommand
        {
            MessageType = nameof(SeekCommand),
            PositionSeconds = positionSeconds
        }, ct);

    public Task SendSetVolumeAsync(double volume, CancellationToken ct = default)
    {
        _lastKnownState.Volume = volume;
        return SendCommandAsync<AcknowledgmentResponse>(new SetVolumeCommand
        {
            MessageType = nameof(SetVolumeCommand),
            Volume = volume
        }, ct);
    }

    public Task SendSetBrightnessAsync(double brightness, CancellationToken ct = default)
    {
        _lastKnownState.Brightness = brightness;
        return SendCommandAsync<AcknowledgmentResponse>(new SetBrightnessCommand
        {
            MessageType = nameof(SetBrightnessCommand),
            Brightness = brightness
        }, ct);
    }

    public Task SendSetSaturationAsync(double saturation, CancellationToken ct = default)
    {
        _lastKnownState.Saturation = saturation;
        return SendCommandAsync<AcknowledgmentResponse>(new SetSaturationCommand
        {
            MessageType = nameof(SetSaturationCommand),
            Saturation = saturation
        }, ct);
    }

    public Task SendSetSharpnessAsync(double sharpness, CancellationToken ct = default)
        => SendCommandAsync<AcknowledgmentResponse>(new SetSharpnessCommand
        {
            MessageType = nameof(SetSharpnessCommand),
            Sharpness = sharpness
        }, ct);

    public Task SendSetZoomAsync(double zoom, CancellationToken ct = default)
    {
        _lastKnownState.Zoom = zoom;
        return SendCommandAsync<AcknowledgmentResponse>(new SetZoomCommand
        {
            MessageType = nameof(SetZoomCommand),
            Zoom = zoom
        }, ct);
    }

    public Task SendSetPlaybackSpeedAsync(double speed, CancellationToken ct = default)
    {
        _lastKnownState.PlaybackSpeed = speed;
        return SendCommandAsync<AcknowledgmentResponse>(new SetPlaybackSpeedCommand
        {
            MessageType = nameof(SetPlaybackSpeedCommand),
            PlaybackSpeed = speed
        }, ct);
    }

    public Task SendShutdownAsync(CancellationToken ct = default)
        => SendCommandAsync<AcknowledgmentResponse>(new ShutdownCommand { MessageType = nameof(ShutdownCommand) }, ct);

    public async Task<bool> BringToFrontAsync(int targetDisplayIndex, CancellationToken ct = default)
    {
        var response = await SendCommandAsync<AcknowledgmentResponse>(new BringToFrontCommand
        {
            MessageType = nameof(BringToFrontCommand),
            TargetDisplayIndex = targetDisplayIndex
        }, ct).ConfigureAwait(false);

        return response?.Success == true;
    }

    private async Task<T?> SendCommandAsync<T>(
        IpcMessage command,
        CancellationToken ct,
        TimeSpan? responseTimeout = null) where T : IpcMessage
    {
        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        StreamWriter? pipeWriter = null;
        StreamReader? pipeReader = null;
        NamedPipeClientStream? pipeClient = null;
        try
        {
            pipeWriter = _pipeWriter;
            pipeReader = _pipeReader;
            pipeClient = _pipeClient;
            if (pipeWriter is null || pipeReader is null || pipeClient is null || !IsConnected)
            {
                _logger($"[VideoPlayerManager] Cannot send {command.MessageType}: IPC is not connected");
                return null;
            }

            var requestWriter = pipeWriter;
            var requestReader = pipeReader;
            var json = IpcMessageJson.Serialize(command, GetJsonOptions());
            if (command is not PingCommand and not StatusRequestCommand)
                _logger($"[VideoPlayerManager] Sending {command.MessageType}: {json}");

            var responseTask = Task.Factory.StartNew(
                () =>
                {
                    requestWriter.WriteLine(json);
                    requestWriter.Flush();
                    return requestReader.ReadLine();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var timeout = responseTimeout ?? CommandResponseTimeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
            var completedTask = await Task.WhenAny(responseTask, timeoutTask).ConfigureAwait(false);
            if (completedTask != responseTask)
            {
                ct.ThrowIfCancellationRequested();
                _logger($"[VideoPlayerManager] {command.MessageType} response timed out after {timeout.TotalSeconds:0.#} seconds");
                DisposePipeResources(pipeWriter, pipeReader, pipeClient);
                _ = responseTask.ContinueWith(
                    task => { _ = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return null;
            }

            timeoutCts.Cancel();
            var responseLine = await responseTask.ConfigureAwait(false);
            if (responseLine is null)
            {
                _logger($"[VideoPlayerManager] {command.MessageType} failed: the player closed the IPC connection without responding");
                DisposePipeResources(pipeWriter, pipeReader, pipeClient);
                return null;
            }

            var response = JsonSerializer.Deserialize<T>(responseLine, GetJsonOptions());
            if (response is null)
                _logger($"[VideoPlayerManager] {command.MessageType} received an unrecognized response: {responseLine}");

            return response;
        }
        catch (Exception ex)
        {
            _logger($"[VideoPlayerManager] SendCommand: {command.MessageType} failed: {ex.GetType().Name}: {ex.Message}");
            if (pipeClient is not null)
                DisposePipeResources(pipeWriter, pipeReader, pipeClient);
            else
                _isConnected = false;
            return null;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<bool> ReconnectIpcAsync(CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsProcessRunning)
                return false;

            _logger("[VideoPlayerManager] Reconnecting IPC after a missing response");
            return ConnectIpc(ct, ReconnectTimeoutMs);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    private string? FindVideoPlayerExecutable()
    {
        var mainDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new List<string>();

        // 1. Same directory as main app (production deployment)
        candidates.Add(Path.Combine(mainDir, VideoPlayerExecutableName));

        // 2. RemotePlay.VideoPlayer subdirectory next to main exe
        candidates.Add(Path.Combine(mainDir, "RemotePlay.VideoPlayer", VideoPlayerExecutableName));

        // 3. Walk up from baseDirectory to find the solution root and probe VideoPlayer build dirs.
        //    Handles Debug/Release and any bin\<config>\<tfm>\ nesting depth.
        var dir = new DirectoryInfo(mainDir);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var playerBin = Path.Combine(dir.FullName, "RemotePlay.VideoPlayer", "bin");
            if (!Directory.Exists(playerBin))
                continue;

            foreach (var config in new[] { "Debug", "Release" })
            {
                // win-x64 (self-contained) MUST come before the bare tfm folder —
                // the bare folder may still contain a stale framework-dependent exe from
                // before the project was switched to SelfContained=true.
                candidates.Add(Path.Combine(playerBin, config, "net8.0-windows", "win-x64", VideoPlayerExecutableName));
                candidates.Add(Path.Combine(playerBin, config, "net8.0-windows", VideoPlayerExecutableName));
            }

            // Publish output
            candidates.Add(Path.Combine(dir.FullName, "RemotePlay.VideoPlayer", "publish", VideoPlayerExecutableName));
            break; // Found the player folder — no need to go higher
        }

        _logger("[VideoPlayerManager] Searching for video player executable...");
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                _logger($"[VideoPlayerManager] Found: {path}");
                return path;
            }
        }

        // Log all tried paths so failures are easy to diagnose
        _logger("[VideoPlayerManager] ERROR: Could not find RemotePlay.VideoPlayer.exe. Tried:");
        foreach (var path in candidates)
            _logger($"  - {path}");

        return null;
    }

    public void KillProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        try
        {
            if (process is not null && !process.HasExited)
            {
                _logger($"[VideoPlayerManager] Killing process (PID: {process.Id})");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            _logger($"[VideoPlayerManager] Kill process error: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
            Volatile.Write(ref _lastStatus, null);

            DisposePipeResources();
        }
    }

    private void DisposePipeResources()
    {
        _isConnected = false;

        DisposePipeResource(_pipeWriter);
        DisposePipeResource(_pipeReader);
        DisposePipeResource(_pipeClient);
        _pipeWriter = null;
        _pipeReader = null;
        _pipeClient = null;
    }

    private void DisposePipeResources(
        StreamWriter? pipeWriter,
        StreamReader? pipeReader,
        NamedPipeClientStream? pipeClient)
    {
        if (ReferenceEquals(_pipeClient, pipeClient))
        {
            _isConnected = false;
            _pipeWriter = null;
            _pipeReader = null;
            _pipeClient = null;
        }

        DisposePipeResource(pipeClient);
        DisposePipeResource(pipeWriter);
        DisposePipeResource(pipeReader);
    }

    private static void DisposePipeResource(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (IOException)
        {
            // The process has already exited and the pipe cannot be flushed.
        }
        catch (ObjectDisposedException)
        {
            // The pipe may already have been closed by a concurrent process restart.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            SendShutdownAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore
        }

        KillProcess();
    }

    private sealed record PlaybackState
    {
        public string? FilePath { get; set; }
        public double PositionSeconds { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsLiveTv { get; set; }
        public int DisplayIndex { get; set; }
        public double Volume { get; set; } = 1.0;
        public double Brightness { get; set; } = 0.5;
        public double Saturation { get; set; } = 1.0;
        public double Zoom { get; set; } = 1.0;
        public double AudioBoost { get; set; } = 1.0;
        public double PlaybackSpeed { get; set; } = 1.0;
        public bool SubtitlesEnabled { get; set; } = true;
    }
}
