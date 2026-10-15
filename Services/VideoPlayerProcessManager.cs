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
            _logger("[VideoPlayerManager] Restarting crashed process...");

            var lastFilePath = _lastKnownState.FilePath;
            var lastPosition = _lastKnownState.PositionSeconds;

            // Kill the old process
            KillProcess();
            await Task.Delay(500, ct);

            // Start new process
            var started = StartCore(_lastKnownState.DisplayIndex, ct);
            if (!started)
                return false;

            // Resume playback if we had a file playing
            if (!string.IsNullOrEmpty(lastFilePath) && File.Exists(lastFilePath))
            {
                _logger($"[VideoPlayerManager] Resuming playback at {lastPosition:0.#}s");
                await Task.Delay(1000, ct); // Give process time to fully initialize

                var resumed = await SendPlayCommandAsync(new PlayCommand
                {
                    MessageType = nameof(PlayCommand),
                    FilePath = lastFilePath,
                    StartPositionSeconds = lastPosition,
                    Volume = _lastKnownState.Volume,
                    Brightness = _lastKnownState.Brightness,
                    Saturation = _lastKnownState.Saturation,
                    Zoom = _lastKnownState.Zoom,
                    AudioBoost = _lastKnownState.AudioBoost,
                    PlaybackSpeed = _lastKnownState.PlaybackSpeed,
                    SubtitlesEnabled = _lastKnownState.SubtitlesEnabled,
                    TargetDisplayIndex = _lastKnownState.DisplayIndex
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

    private bool ConnectIpc(CancellationToken ct)
    {
        var pipeName = $"RemotePlay.VideoPlayer.{_instanceId}";
        _isConnected = false;

        // StartAsync runs off the UI thread. Keep startup pipe setup fully synchronous so
        // no continuation can be delayed between a successful connection and the first Ping.
        const int connectTimeoutMs = 2_000;
        const int retryDelayMs     = 250;
        const int maxWaitMs        = 30_000;
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
            var response = await SendCommandAsync<PongResponse>(ping, ct);

            if (response is not null)
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
        // Update last known state
        _lastKnownState = new PlaybackState
        {
            FilePath = cmd.FilePath,
            PositionSeconds = cmd.StartPositionSeconds,
            IsPlaying = true,
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

    private async Task<T?> SendCommandAsync<T>(IpcMessage command, CancellationToken ct) where T : IpcMessage
    {
        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pipeWriter is null || _pipeReader is null || !IsConnected)
            {
                _logger($"[VideoPlayerManager] Cannot send {command.MessageType}: IPC is not connected");
                return null;
            }

            var json = JsonSerializer.Serialize(command, command.GetType(), GetJsonOptions());
            if (command is not PingCommand and not StatusRequestCommand)
                _logger($"[VideoPlayerManager] Sending {command.MessageType}: {json}");

            var responseLine = await Task.Factory.StartNew(
                () =>
                {
                    _pipeWriter.WriteLine(json);
                    _pipeWriter.Flush();
                    return _pipeReader.ReadLine();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);

            if (responseLine is null)
            {
                _logger($"[VideoPlayerManager] {command.MessageType} failed: the player closed the IPC connection without responding");
                _isConnected = false;
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
            _isConnected = false;  // assume pipe is broken
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
            return ConnectIpc(ct);
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
        public int DisplayIndex { get; set; }
        public double Volume { get; set; } = 1.0;
        public double Brightness { get; set; }
        public double Saturation { get; set; } = 1.0;
        public double Zoom { get; set; } = 1.0;
        public double AudioBoost { get; set; } = 1.0;
        public double PlaybackSpeed { get; set; } = 1.0;
        public bool SubtitlesEnabled { get; set; } = true;
    }
}
