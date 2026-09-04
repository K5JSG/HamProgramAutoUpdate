using System.Diagnostics;
using System.Windows;
using HamProgramAutoUpdate.Services;
using Forms = System.Windows.Forms;

namespace HamProgramAutoUpdate;

public partial class App : Application
{
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private Mutex? _singleInstance;

    public static IStatusService Status { get; private set; } = null!;
    public static IUpdaterRunner Runner { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- command line, used by the installer and uninstaller ----------
        var args = e.Args;
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--install-task":
                    {
                        var (ok, error) = TaskSchedulerService.InstallDashboardTask();
                        if (!ok) Console.Error.WriteLine(error);
                        Shutdown(ok ? 0 : 1);
                        return;
                    }
                case "--remove-task":
                    {
                        var (ok, error) = TaskSchedulerService.RemoveDashboardTask();
                        if (!ok) Console.Error.WriteLine(error);
                        Shutdown(ok ? 0 : 1);
                        return;
                    }
                case "--install-updates-task":
                    {
                        var (ok, error) = TaskSchedulerService.InstallUpdaterTask();
                        if (!ok) Console.Error.WriteLine(error);
                        Shutdown(ok ? 0 : 1);
                        return;
                    }
                case "--remove-updates-task":
                    {
                        var (ok, error) = TaskSchedulerService.RemoveUpdaterTask();
                        if (!ok) Console.Error.WriteLine(error);
                        Shutdown(ok ? 0 : 1);
                        return;
                    }
                case "--version":
                    {
                        Console.WriteLine(AppInfo.Version);
                        Shutdown(0);
                        return;
                    }
                case "--run-updates":
                    {
                        // Run on a threadpool thread, not directly: OnStartup runs on
                        // the UI thread before the Dispatcher message loop has started,
                        // so an async call awaited here would capture a
                        // DispatcherSynchronizationContext with nothing pumping it yet.
                        // Blocking on it with GetResult() would then deadlock the moment
                        // any continuation tried to marshal back to this same thread.
                        var exitCode = Task.Run(() => Services.Updaters.HeadlessUpdateRunner.RunAllAsync()).GetAwaiter().GetResult();
                        Shutdown(exitCode);
                        return;
                    }
                case "--check-updates":
                    {
                        // Same as --run-updates but never downloads or installs anything -
                        // just checks each detected program's real version against what
                        // each updater finds live, and logs the result.
                        var exitCode = Task.Run(() => Services.Updaters.HeadlessUpdateRunner.RunAllAsync(dryRun: true)).GetAwaiter().GetResult();
                        Shutdown(exitCode);
                        return;
                    }
                case "--force-update":
                    {
                        if (args.Length < 2)
                        {
                            Console.Error.WriteLine("Usage: --force-update <key>  (e.g. --force-update tqsl)");
                            Shutdown(1);
                            return;
                        }
                        var exitCode = Task.Run(() => Services.Updaters.HeadlessUpdateRunner.RunOneAsync(args[1], force: true)).GetAwaiter().GetResult();
                        Shutdown(exitCode);
                        return;
                    }
                case "--check-update":
                    {
                        // Single-program counterpart to --check-updates: dry
                        // run, live network/detection included, nothing
                        // downloaded or installed.
                        if (args.Length < 2)
                        {
                            Console.Error.WriteLine("Usage: --check-update <key>  (e.g. --check-update chirp)");
                            Shutdown(1);
                            return;
                        }
                        var exitCode = Task.Run(() => Services.Updaters.HeadlessUpdateRunner.CheckOneAsync(args[1])).GetAwaiter().GetResult();
                        Shutdown(exitCode);
                        return;
                    }
            }
        }

        // ---- only one dashboard at a time --------------------------------
        _singleInstance = new Mutex(true, @"Local\HamProgramAutoUpdate_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show(
                "The Ham Program Auto Update is already running.\n\n" +
                "Look for the teal icon in the notification area, next to the clock.",
                "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // Best-effort: removes any setup exe left behind by a previous
        // self-update run (see SelfUpdateService.DownloadAndLaunchInstallerAsync -
        // the app shuts down right after launching that installer, so this is
        // the first opportunity to clean it up).
        SelfUpdateService.CleanupOldDownloads();

        Runner = new UpdaterRunner();
        Status = new StatusService(Runner);

        _window = new MainWindow();
        BuildTrayIcon();

        // Always start minimized to the tray, regardless of how the exe was
        // launched (logon task, desktop shortcut, Start menu, ...) - the
        // dashboard only opens when the user asks for it via the tray icon.
    }

    private void BuildTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowWindow());
        menu.Items.Add("Run All Updates", null, (_, _) => RunAllUpdates());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Ham Program Auto Update",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/app.ico");
            using var stream = GetResourceStream(uri)?.Stream;
            if (stream is not null) return new System.Drawing.Icon(stream);
        }
        catch (Exception)
        {
            // fall through to the stock icon
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void ShowWindow()
    {
        _window ??= new MainWindow();

        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Refresh();
    }

    private void RunAllUpdates()
    {
        var task = TaskSchedulerService.ResolveUpdaterTask();
        if (task is null)
        {
            _tray?.ShowBalloonTip(5000, "Ham Program Auto Update",
                $"No scheduled task named \"{TaskSchedulerService.UpdaterTaskName}\" was found.",
                Forms.ToolTipIcon.Warning);
            return;
        }

        if (TaskSchedulerService.IsRunning(task))
        {
            _tray?.ShowBalloonTip(3000, "Ham Program Auto Update",
                "The update task is already running.", Forms.ToolTipIcon.Info);
            return;
        }

        var error = TaskSchedulerService.RunTask(task);
        if (error is not null)
        {
            _tray?.ShowBalloonTip(5000, "Ham Program Auto Update",
                error, Forms.ToolTipIcon.Error);
        }
        else
        {
            _tray?.ShowBalloonTip(3000, "Ham Program Auto Update",
                "Update scripts started.", Forms.ToolTipIcon.Info);
        }
    }

    private void ExitApp()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        Runner?.Dispose();
        AppPaths.CleanupBestEffort();
        base.OnExit(e);
    }
}

public static class AppInfo
{
    public static string Version
    {
        get
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").FileVersion ?? "1.0.0.0";
            }
            catch (Exception)
            {
                return "1.0.0.0";
            }
        }
    }

    /// <summary>Parsed form of <see cref="Version"/>, for comparing against a
    /// GitHub release tag in SelfUpdateService.</summary>
    public static Version VersionValue =>
        System.Version.TryParse(Version, out var v) ? v : new Version(0, 0, 0, 0);

    /// <summary>Major.Minor.Build only - the Revision field is always 0 in
    /// this project's own builds (see build.ps1) and just clutters a
    /// human-facing display like the window title.</summary>
    public static string ShortVersion => $"{VersionValue.Major}.{VersionValue.Minor}.{VersionValue.Build}";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
