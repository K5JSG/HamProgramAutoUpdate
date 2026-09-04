using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HamProgramAutoUpdate.Services;

namespace HamProgramAutoUpdate;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _updateCheckTimer;
    private bool _pollingFast;
    private int _notRunningTicksInARow;
    private ReleaseInfo? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();

        Title = $"Ham Program Auto Update v{AppInfo.ShortVersion}";
        EmptyPath.Text = UpdaterCatalog.LogDir;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        // The dashboard normally stays hidden in the tray for days at a
        // time (see App.OnStartup), so this timer keeps running whether or
        // not the window is actually visible.
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateCheckTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        _updateCheckTimer.Start();

        Loaded += (_, _) => Refresh();
        Loaded += (_, _) => _ = CheckForUpdateAsync();
    }

    // ------------------------------------------------------------ updates

    private async Task CheckForUpdateAsync()
    {
        var release = await SelfUpdateService.CheckForUpdateAsync();

        _pendingUpdate = release;
        if (release is null)
        {
            UpdateAvailableButton.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateAvailableButton.Content = $"Update to v{release.Version} available";
        UpdateAvailableButton.ToolTip = "Download and install the latest version from GitHub.";
        UpdateAvailableButton.IsEnabled = true;
        UpdateAvailableButton.Visibility = Visibility.Visible;
    }

    /// <summary>Manual counterpart to the automatic 6-hour CheckForUpdateAsync
    /// timer - unlike that background check, this one always tells the user
    /// something happened, even when there's nothing new.</summary>
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        var originalContent = CheckForUpdatesButton.Content;
        CheckForUpdatesButton.Content = "Checking...";

        try
        {
            await CheckForUpdateAsync();
            if (_pendingUpdate is null)
            {
                MessageBox.Show($"You're running the latest version (v{AppInfo.ShortVersion}).",
                    "Up to date", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            CheckForUpdatesButton.Content = originalContent;
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async void UpdateAvailable_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { } release) return;

        var confirm = MessageBox.Show(
            $"Download and install version {release.Version}?\n\n" +
            "The dashboard will close and the installer will open. " +
            "Your update history is kept.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        UpdateAvailableButton.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(text => UpdateAvailableButton.Content = text);

            var error = await SelfUpdateService.DownloadAndLaunchInstallerAsync(release, progress);
            if (error is not null)
            {
                MessageBox.Show($"Could not download the update.\n\n{error}", "Update failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateAvailableButton.Content = $"Update to v{release.Version} available";
                UpdateAvailableButton.IsEnabled = true;
                return;
            }

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not download the update.\n\n{ex.Message}", "Update failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateAvailableButton.Content = $"Update to v{release.Version} available";
            UpdateAvailableButton.IsEnabled = true;
        }
    }

    /// <summary>Closing the window hides it; the tray icon keeps the app alive.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- data

    public void Refresh()
    {
        var items = App.Status.GetAll()
            .Select(s => new CardViewModel(s))
            .ToList();

        CardList.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LastUpdatedText.Text = $"Last updated: {DateTime.Now:T}";

        UpdateElevationBadge();
        UpdatePollingRate(items);
    }

    private void UpdateElevationBadge()
    {
        if (AppInfo.IsElevated)
        {
            ElevationText.Text = "ADMIN";
            ElevationText.Foreground = (Brush)FindResource("AccentBrush");
            ElevationBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0xD4, 0xAA));
            ElevationBadge.ToolTip = "Running as administrator - updaters launch with no UAC prompt.";
        }
        else
        {
            ElevationText.Text = "NOT ADMIN";
            ElevationText.Foreground = (Brush)FindResource("WarnBrush");
            ElevationBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xC3, 0x00));
            ElevationBadge.ToolTip =
                "Not running as administrator. Updaters that install software may fail.\n" +
                "Start the dashboard from its scheduled task, or right-click it and " +
                "choose Run as administrator.";
        }
    }

    /// <summary>
    /// Poll every 3 seconds while something is running so cards update live,
    /// then drop back to the idle 30 second refresh.
    ///
    /// AnyRunning()/per-card IsRunning only ever reflect IN-PROCESS runs
    /// started via a card's own Run button (App.Runner) - neither can see the
    /// "Program Update Scripts" scheduled task, which runs as a completely
    /// separate process. Without also checking TaskSchedulerService.IsRunning,
    /// fast polling triggered by "Run All Updates" reverted to the slow
    /// interval on the very next tick even while that task was still
    /// actively running.
    /// </summary>
    private void UpdatePollingRate(List<CardViewModel> items)
    {
        var stillRunning = items.Any(i => i.Model.IsRunning) || App.Runner.AnyRunning()
            || TaskSchedulerService.IsRunning(TaskSchedulerService.UpdaterTaskPath);

        if (stillRunning)
        {
            _notRunningTicksInARow = 0;
            if (!_pollingFast)
            {
                _timer.Interval = TimeSpan.FromSeconds(3);
                _pollingFast = true;
            }
            return;
        }

        if (!_pollingFast) return;

        // schtasks /Query can briefly still report "Ready" for a moment
        // right after /Run fires it off - require two consecutive "nothing
        // running" ticks (a 3-6s grace window at the fast interval) before
        // actually dropping back to slow polling, so "Run All Updates"
        // starting the task doesn't get immediately undone by this same
        // check on the very next tick before Task Scheduler has caught up.
        if (++_notRunningTicksInARow < 2) return;

        _timer.Interval = TimeSpan.FromSeconds(30);
        _pollingFast = false;
        _notRunningTicksInARow = 0;
    }

    // ------------------------------------------------------------- header

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void RunAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Run all update scripts now?\n\n" +
            $"This starts the \"{TaskSchedulerService.UpdaterTaskName}\" scheduled task, " +
            "which checks every program for updates. It may take several minutes.",
            "Run updates", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        // Also check whether the dashboard app itself has an update, same as
        // the 6-hour _updateCheckTimer - fire-and-forget, and it only ever
        // shows UpdateAvailableButton (never downloads/installs on its own),
        // so it's safe to kick off regardless of whether the scheduled task
        // below is found/started successfully.
        _ = CheckForUpdateAsync();

        var task = TaskSchedulerService.ResolveUpdaterTask();
        if (task is null)
        {
            MessageBox.Show(
                $"No scheduled task named \"{TaskSchedulerService.UpdaterTaskName}\" was found.\n\n" +
                "Create it in Task Scheduler with one action per updater, or use the " +
                "Run button on each card instead.",
                "Task not found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (TaskSchedulerService.IsRunning(task))
        {
            MessageBox.Show("The update task is already running.", "Already running",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var error = TaskSchedulerService.RunTask(task);
        if (error is not null)
        {
            MessageBox.Show($"Could not start the task.\n\n{error}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _timer.Interval = TimeSpan.FromSeconds(3);
        _pollingFast = true;
        Refresh();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Empty all tracked log files?\n\n" +
            "The \"Last Update\" date for each program is saved separately and will be kept.\n\n" +
            "The log contents themselves cannot be recovered.",
            "Clear logs", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var (cleared, failed) = App.Status.ClearAllLogs();

        if (failed.Count > 0)
        {
            MessageBox.Show(
                $"Cleared {cleared} log(s).\n\nCould not clear: {string.Join(", ", failed)}\n\n" +
                "A file may be open or in use by a running updater.",
                "Partly done", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
    }

    // -------------------------------------------------------------- cards

    private static string? KeyOf(object sender) => (sender as Button)?.Tag as string;

    private void RunOne_Click(object sender, RoutedEventArgs e)
    {
        if (KeyOf(sender) is not { } key) return;

        var error = App.Runner.Run(key);
        if (error is not null)
        {
            var name = UpdaterCatalog.Find(key)?.DisplayName ?? key;
            MessageBox.Show($"Could not run the {name} updater.\n\n{error}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _timer.Interval = TimeSpan.FromSeconds(3);
        _pollingFast = true;
        Refresh();
    }

    private void ClearOne_Click(object sender, RoutedEventArgs e)
    {
        if (KeyOf(sender) is not { } key) return;

        var name = UpdaterCatalog.Find(key)?.DisplayName ?? key;
        var confirm = MessageBox.Show(
            $"Empty the log file for {name}?\n\n" +
            "Its \"Last Update\" date is saved separately and will be kept.\n\n" +
            "The log contents themselves cannot be recovered.",
            "Clear log", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var (ok, error) = App.Status.ClearLog(key);
        if (!ok)
        {
            MessageBox.Show(
                $"Could not clear the log for {name}.\n\n{error}\n\n" +
                "The file may be in use by a running updater.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (KeyOf(sender) is not { } key) return;

        var entry = UpdaterCatalog.Find(key);
        if (entry is null) return;

        var path = UpdaterCatalog.LogPath(entry);
        var window = new LogWindow(entry.DisplayName, path) { Owner = this };
        window.ShowDialog();
    }
}
