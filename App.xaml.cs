using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemotePlay.DependencyInjection;
using WpfApplication = System.Windows.Application;

namespace RemotePlay
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : WpfApplication
    {
        public ServiceProvider? Services { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Logger.Error("Unhandled exception in AppDomain", args.ExceptionObject as Exception);

            DispatcherUnhandledException += (_, args) =>
            {
                Logger.Error("Unhandled dispatcher exception", args.Exception);
                args.Handled = false;
            };

            try
            {
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
