using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemotePlay.DependencyInjection;
using WpfApplication = System.Windows.Application;
using System.Diagnostics.CodeAnalysis;

namespace RemotePlay
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    [ExcludeFromCodeCoverage]
    public partial class App : WpfApplication
    {
        private const string SingleInstanceMutexName = "Global\\RemotePlay_SingleInstance_Mutex";
        private const string BringToFrontEventName   = "Global\\RemotePlay_BringToFront_Event";
        private const string CommandPipeName         = "RemotePlay_CommandPipe";

        // Held for the lifetime of the process — must not be disposed until Exit.
        private static Mutex? _singleInstanceMutex;
        private readonly ConcurrentQueue<string[]> _pendingExternalCommands = [];

        public ServiceProvider? Services { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // ── Single-instance guard ────────────────────────────────────────────
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                var forwarded = TryForwardExternalCommand(e.Args);

                // Signal the existing instance to restore its window, then exit silently.
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(BringToFrontEventName);
                    evt.Set();
                }
                catch { /* existing instance may not have created the event yet — ignore */ }

                // Fallback: if this is an external command and forwarding failed, continue in this process
                // so the command still executes instead of being dropped.
                if (!forwarded && e.Args.Length > 0)
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                    Logger.Warning("Startup", "External command forwarding failed; processing command in a secondary instance.");
                }
                else
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                    Shutdown(0);
                    return;
                }
            }

            // Create the bring-to-front event so the existing instance can react.
            var bringToFrontEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: BringToFrontEventName);

            // Watch for a second instance signalling us in a background thread.
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            Exit += (_, _) => cts.Cancel();
            _ = StartCommandPipeListenerAsync(token);

            _ = Task.Run(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        bool signalled = bringToFrontEvent.WaitOne(TimeSpan.FromSeconds(1));
                        if (signalled && !token.IsCancellationRequested)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                var win = MainWindow;
                                if (win is null) return;
                                if (win.WindowState == WindowState.Minimized)
                                    win.WindowState = WindowState.Normal;
                                win.Activate();
                                win.Topmost = true;
                                win.Topmost = false;
                                win.Focus();
                            });
                        }
                    }
                }
                finally
                {
                    bringToFrontEvent.Dispose();
                }
            });
            // ── End single-instance guard ────────────────────────────────────────

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Logger.Error("Unhandled exception in AppDomain", args.ExceptionObject as Exception);

            DispatcherUnhandledException += (_, args) =>
            {
                Logger.Error("Unhandled dispatcher exception", args.Exception);
                args.Handled = false;
            };

            try
            {
                // Show splash as early as possible — before DI setup and base startup.
                var splash = new SplashWindow();
                splash.Show();

                Logger.Clear();
                ClearVideoPlayerDiagnosticLog();
                Logger.Info("Application startup begin");
                base.OnStartup(e);

                var serviceCollection = new ServiceCollection();
                serviceCollection.AddRemotePlayApplication();
                var registeredServices = serviceCollection
                    .Select(descriptor => $"{descriptor.Lifetime}: {descriptor.ServiceType.FullName} => {descriptor.ImplementationType?.FullName ?? descriptor.ImplementationInstance?.GetType().FullName ?? "<factory>"}")
                    .ToArray();

                Services = serviceCollection.BuildServiceProvider();

                MainWindow mainWindow;
                try
                {
                    mainWindow = Services.GetRequiredService<MainWindow>();
                }
                catch (InvalidOperationException ex)
                {
                    Logger.Error("Failed to resolve MainWindow from the DI container. Registered services:" + Environment.NewLine + string.Join(Environment.NewLine, registeredServices), ex);
                    throw;
                }

                MainWindow = mainWindow;
                mainWindow.Show();
                if (e.Args.Length > 0)
                    mainWindow.HandleExternalCommandArgs(e.Args);

                while (_pendingExternalCommands.TryDequeue(out var pendingArgs))
                    mainWindow.HandleExternalCommandArgs(pendingArgs);

                splash.FadeOutAndClose();

                Logger.Info("Application startup complete");
            }
            catch (Exception ex)
            {
                Logger.Error("Application startup failed", ex);
                throw;
            }
        }

        private static void ClearVideoPlayerDiagnosticLog()
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemotePlay",
                "Logs",
                "RemotePlay.VideoPlayer.log");

            try
            {
                File.Delete(logPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static bool TryForwardExternalCommand(string[] args)
        {
            if (args.Length == 0)
                return true;

            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", CommandPipeName, PipeDirection.Out);
                    client.Connect(timeout: 750);
                    using var writer = new StreamWriter(client) { AutoFlush = true };
                    writer.WriteLine(string.Join('\u001f', args));
                    return true;
                }
                catch
                {
                    // Primary instance may still be starting up; retry briefly.
                    Thread.Sleep(150);
                }
            }

            return false;
        }

        private async Task StartCommandPipeListenerAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        CommandPipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cancellationToken);
                    using var reader = new StreamReader(server);
                    var payload = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(payload))
                        continue;

                    var args = payload.Split('\u001f', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (args.Length == 0)
                        continue;

                    Dispatcher.Invoke(() => DispatchExternalCommand(args));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Listener must stay alive for future activations.
                }
            }
        }

        private void DispatchExternalCommand(string[] args)
        {
            if (MainWindow is MainWindow window)
            {
                window.HandleExternalCommandArgs(args);
                return;
            }

            _pendingExternalCommands.Enqueue(args);
        }
    }

}
