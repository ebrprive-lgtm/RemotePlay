using System.Configuration;
using System.Data;
using System.Windows;

namespace RemotePlay
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
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
