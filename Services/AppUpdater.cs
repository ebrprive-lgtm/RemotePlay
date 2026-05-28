using System.Diagnostics;
using System.IO;

namespace RemotePlay.Services;

/// <summary>
/// Checks a configured source folder for updated application files and applies them
/// by copying only changed files, then restarting the process.
/// </summary>
internal sealed class AppUpdater
{
    private volatile bool _isUpdating;
    private volatile bool _hasChecked;
    private string _currentVersion = string.Empty;
    private string _availableVersion = string.Empty;
    private string _lastUpdateError = string.Empty;
    private readonly object _gate = new();
    private System.Threading.Timer? _intervalTimer;

    public bool IsUpdating => _isUpdating;
    public bool HasChecked => _hasChecked;
    public string CurrentVersion => _currentVersion;
    public string AvailableVersion => _availableVersion;
    public string LastUpdateError => _lastUpdateError;

    /// <summary>
    /// Invoked on the UI thread just before the process shuts down for an update.
    /// Hook this up to the main window's clean-exit path so the tray icon is disposed.
    /// </summary>
    public Action? ShutdownRequested { get; set; }

    public AppUpdater()
    {
        _currentVersion = UpdateFileHelper.ReadVersionFile(AppContext.BaseDirectory)
            ?? UpdateFileHelper.GetAssemblyVersion();
    }

    /// <summary>Starts the periodic update check based on the provided config.</summary>
    public void Start(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.UpdateSourcePath))
            return;

        // Check immediately on startup.
        _ = Task.Run(() => CheckAndApplyAsync(config));

        if (config.AutoUpdateIntervalMinutes > 0)
        {
            var interval = TimeSpan.FromMinutes(config.AutoUpdateIntervalMinutes);
            _intervalTimer = new System.Threading.Timer(
                _ => _ = Task.Run(() => CheckAndApplyAsync(config)),
                null,
                interval,
                interval);
        }
    }

    public void Stop() => _intervalTimer?.Dispose();

    private async Task CheckAndApplyAsync(AppConfig config)
    {
        if (_isUpdating)
            return;

        lock (_gate)
        {
            if (_isUpdating)
                return;
        }

        var sourcePath = config.UpdateSourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
        {
            _lastUpdateError = Directory.Exists(sourcePath) ? string.Empty : $"Update source folder not found: {sourcePath}";
            _hasChecked = true;
            return;
        }

        try
        {
            var sourceVersion = UpdateFileHelper.ReadVersionFile(sourcePath);
            if (sourceVersion is null)
            {
                Logger.Info("AppUpdater: No version.txt found in update source, skipping check.");
                _lastUpdateError = "No version.txt found in update source folder.";
                _hasChecked = true;
                return;
            }

            _availableVersion = sourceVersion;

            if (string.Equals(sourceVersion, _currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                _hasChecked = true;
                return;
            }

            Logger.Info($"AppUpdater: Update available — current={_currentVersion}, available={sourceVersion}. Starting update.");

            _hasChecked = true;
            lock (_gate)
                _isUpdating = true;

            _lastUpdateError = string.Empty;

            await ApplyUpdateAsync(sourcePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastUpdateError = ex.Message;
            _hasChecked = true;
            Logger.Error("AppUpdater: Update failed", ex);
            lock (_gate)
                _isUpdating = false;
        }
    }

    private async Task ApplyUpdateAsync(string sourcePath)
    {
        var targetPath = AppContext.BaseDirectory;
        var exeName = Path.GetFileName(Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
        var tempBatchPath = Path.Combine(Path.GetTempPath(), "remoteplay_update.bat");

        var changedFiles = await Task.Run(() => UpdateFileHelper.CollectChangedFiles(sourcePath, targetPath)).ConfigureAwait(false);

        if (changedFiles.Count == 0)
        {
            Logger.Info("AppUpdater: No changed files found; update skipped.");
            lock (_gate)
                _isUpdating = false;
            return;
        }

        Logger.Info($"AppUpdater: Copying {changedFiles.Count} changed file(s).");

        // Build a batch file that waits for the process to exit, copies files, then restarts.
        var processId = Environment.ProcessId;
        var exePath = Environment.ProcessPath ?? Path.Combine(targetPath, exeName);
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("@echo off");
        lines.AppendLine($":waitloop");
        lines.AppendLine($"tasklist /FI \"PID eq {processId}\" 2>NUL | find \"{processId}\" >NUL");
        lines.AppendLine("if not errorlevel 1 (");
        lines.AppendLine("  timeout /t 1 /nobreak >NUL");
        lines.AppendLine("  goto waitloop");
        lines.AppendLine(")");

        foreach (var (src, dst) in changedFiles)
        {
            var dstDir = Path.GetDirectoryName(dst)!;
            lines.AppendLine($"if not exist \"{dstDir}\\\" mkdir \"{dstDir}\"");
            lines.AppendLine($"copy /Y \"{src}\" \"{dst}\" >NUL");
        }

        lines.AppendLine($"start \"\" \"{exePath}\"");
        lines.AppendLine("del \"%~f0\"");

        await File.WriteAllTextAsync(tempBatchPath, lines.ToString()).ConfigureAwait(false);

        Logger.Info($"AppUpdater: Launching update batch at {tempBatchPath}. App will restart.");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{tempBatchPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (ShutdownRequested is { } shutdown)
                shutdown();
            else
                System.Windows.Application.Current.Shutdown();
        });
    }

    }
