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

        // Held for the lifetime of the process — must not be disposed until Exit.
        private static Mutex? _singleInstanceMutex;

        public ServiceProvider? Services { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // ── Single-instance guard ────────────────────────────────────────────
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                // Signal the existing instance to restore its window, then exit silently.
                try
                {
                    using var evt = EventWaitHandle.OpenExisting(BringToFrontEventName);
                    evt.Set();
                }
                catch { /* existing instance may not have created the event yet — ignore */ }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown(0);
                return;
            }

            // Create the bring-to-front event so the existing instance can react.
            using var bringToFrontEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: BringToFrontEventName);

            // Watch for a second instance signalling us in a background thread.
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            Exit += (_, _) => cts.Cancel();

            _ = Task.Run(() =>
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
            }, token);
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
                splash.FadeOutAndClose();

                Logger.Info("Application startup complete");
            }
            catch (Exception ex)
            {
                Logger.Error("Application startup failed", ex);
                throw;
            }
        }
    }

}
