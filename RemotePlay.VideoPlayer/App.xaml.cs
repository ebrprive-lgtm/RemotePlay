using System;
using System.Windows;

namespace RemotePlay.VideoPlayer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        PlayerDiagnostics.Info($"Player process started. Diagnostic log: {PlayerDiagnostics.Path}");

        // Catch exceptions thrown inside Dispatcher.BeginInvoke callbacks (e.g. LibVLC event
        // callbacks). Without this, WPF terminates the process with exit code 0 and the
        // manager sees a clean exit before IPC is established.
        DispatcherUnhandledException += (_, args) =>
        {
            PlayerDiagnostics.Error("Unhandled dispatcher exception", args.Exception);
            Console.WriteLine($"[VideoPlayer] FATAL dispatcher exception: {args.Exception}");
            args.Handled = true;
            Shutdown(2);
        };

        // Catch exceptions on background threads (e.g. LibVLC internal threads).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                PlayerDiagnostics.Error($"Unhandled domain exception (isTerminating={args.IsTerminating})", exception);
            else
                PlayerDiagnostics.Info($"Unhandled domain exception (isTerminating={args.IsTerminating}): {args.ExceptionObject}");

            Console.WriteLine($"[VideoPlayer] FATAL domain exception (isTerminating={args.IsTerminating}): {args.ExceptionObject}");
        };

        // Log every shutdown so we know which code path triggered it.
        Exit += (_, exitArgs) =>
            Console.WriteLine($"[VideoPlayer] Application.Exit fired — exit code {exitArgs.ApplicationExitCode}");

    }
}

