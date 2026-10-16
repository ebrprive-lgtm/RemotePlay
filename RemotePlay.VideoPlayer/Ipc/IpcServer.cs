using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemotePlay.VideoPlayer;

namespace RemotePlay.VideoPlayer.Ipc;

/// <summary>
/// Named pipe server for receiving commands from the main RemotePlay process
/// </summary>
internal sealed class IpcServer : IDisposable
{
    private const string PipeNamePrefix = "RemotePlay.VideoPlayer.";
    private readonly string _pipeName;
    private readonly Func<IpcMessage, Task<IpcMessage?>> _messageHandler;
    private NamedPipeServerStream? _pipeServer;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public string PipeName => _pipeName;
    public bool IsListening { get; private set; }

    public IpcServer(string instanceId, Func<IpcMessage, Task<IpcMessage?>> messageHandler)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(messageHandler);

        _pipeName = PipeNamePrefix + instanceId;
        _messageHandler = messageHandler;
    }

    public void Start()
    {
        if (IsListening)
            throw new InvalidOperationException("IPC server is already listening");

        _cts = new CancellationTokenSource();
        _listenTask = Task.Factory.StartNew(
            () => Listen(_cts.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        IsListening = true;
    }

    private void Listen(CancellationToken ct)
    {
        PlayerDiagnostics.Info($"IPC listener started: {_pipeName}");
        Console.WriteLine($"[IPC Server] Listening on pipe: {_pipeName}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipeServer = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, // allow reconnect after failed attempt
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);

                Console.WriteLine("[IPC Server] Waiting for client connection...");
                _pipeServer.WaitForConnection();
                PlayerDiagnostics.Info("IPC client connected");
                Console.WriteLine("[IPC Server] Client connected — entering message loop");

                HandleConnection(_pipeServer, ct);

                Console.WriteLine("[IPC Server] Client disconnected — ready for next connection");
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    break;

                PlayerDiagnostics.Error("IPC listener error", ex);
                Console.WriteLine($"[IPC Server] Error: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[IPC Server] Stack: {ex.StackTrace}");
                Thread.Sleep(1000);
            }
            finally
            {
                _pipeServer?.Dispose();
                _pipeServer = null;
            }
        }

        PlayerDiagnostics.Info("IPC listener stopped");
        Console.WriteLine("[IPC Server] Stopped");
    }

    private void HandleConnection(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);

            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var message = DeserializeMessage(line);
                    if (message is null)
                    {
                        PlayerDiagnostics.Info($"IPC ignored unrecognized payload: {GetDiagnosticPayload(line)}");
                        Console.WriteLine($"[IPC Server] Ignored unrecognized message: {line}");
                        continue;
                    }

                    Console.WriteLine($"[IPC Server] Received {message.MessageType} ({message.MessageId})");
                    var response = _messageHandler(message).GetAwaiter().GetResult();
                    if (response is not null)
                    {
                        var responseJson = IpcMessageJson.Serialize(response, GetJsonOptions());
                        writer.WriteLine(responseJson);
                        writer.Flush();
                        PlayerDiagnostics.Info($"IPC responded {response.MessageType} to {message.MessageType} ({message.MessageId})");
                        Console.WriteLine($"[IPC Server] Sent {response.MessageType} for {message.MessageType}");
                    }
                }
                catch (Exception ex)
                {
                    PlayerDiagnostics.Error("IPC message handling error", ex);
                    Console.WriteLine($"[IPC Server] Message handling error: {ex.Message}");
                }
            }
        }
        catch (IOException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error("IPC connection error", ex);
            Console.WriteLine($"[IPC Server] Connection error: {ex.Message}");
        }
    }

    private static IpcMessage? DeserializeMessage(string json)
    {
        try
        {
            var options = GetJsonOptions();

            // First, deserialize as base to get MessageType
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("MessageType", out var typeProperty))
                return null;

            var messageType = typeProperty.GetString();
            if (string.IsNullOrEmpty(messageType))
                return null;

            var type = GetCommandType(messageType);
            if (type is null)
            {
                PlayerDiagnostics.Info($"IPC ignored unsupported message type: {messageType}");
                return null;
            }

            return JsonSerializer.Deserialize(json, type, options) as IpcMessage;
        }
        catch (Exception ex)
        {
            PlayerDiagnostics.Error("IPC deserialization error", ex);
            Console.WriteLine($"[IPC Server] Deserialization error: {ex.Message}");
            return null;
        }
    }

    private static Type? GetCommandType(string messageType) => messageType switch
    {
        nameof(PlayCommand) => typeof(PlayCommand),
        nameof(BringToFrontCommand) => typeof(BringToFrontCommand),
        nameof(PauseCommand) => typeof(PauseCommand),
        nameof(ResumeCommand) => typeof(ResumeCommand),
        nameof(StopCommand) => typeof(StopCommand),
        nameof(SeekCommand) => typeof(SeekCommand),
        nameof(SetVolumeCommand) => typeof(SetVolumeCommand),
        nameof(SetBrightnessCommand) => typeof(SetBrightnessCommand),
        nameof(SetSaturationCommand) => typeof(SetSaturationCommand),
        nameof(SetSharpnessCommand) => typeof(SetSharpnessCommand),
        nameof(SetZoomCommand) => typeof(SetZoomCommand),
        nameof(SetAudioBoostCommand) => typeof(SetAudioBoostCommand),
        nameof(SetPlaybackSpeedCommand) => typeof(SetPlaybackSpeedCommand),
        nameof(SetSubtitlesEnabledCommand) => typeof(SetSubtitlesEnabledCommand),
        nameof(SetAudioTrackCommand) => typeof(SetAudioTrackCommand),
        nameof(SetSubtitleTrackCommand) => typeof(SetSubtitleTrackCommand),
        nameof(SetEqualizerCommand) => typeof(SetEqualizerCommand),
        nameof(SetReverbCommand) => typeof(SetReverbCommand),
        nameof(PingCommand) => typeof(PingCommand),
        nameof(StatusRequestCommand) => typeof(StatusRequestCommand),
        nameof(ShutdownCommand) => typeof(ShutdownCommand),
        _ => null
    };

    private static string GetDiagnosticPayload(string payload)
        => payload.Length <= 2_048 ? payload : $"{payload[..2_048]}... (truncated)";

    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsListening = false;

        _cts?.Cancel();
        _pipeServer?.Dispose();
        _cts?.Dispose();

        try
        {
            _listenTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore
        }
    }
}
