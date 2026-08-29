using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HamProgramAutoUpdate.Services;

namespace HamProgramAutoUpdate;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _pollingFast;

    public MainWindow()
    {
        InitializeComponent();

        EmptyPath.Text = UpdaterCatalog.HamRadioDir;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Loaded += (_, _) => Refresh();
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
    /// </summary>
    private void UpdatePollingRate(List<CardViewModel> items)
    {
        var shouldPollFast = items.Any(i => i.Model.IsRunning) || App.Runner.AnyRunning();

        if (shouldPollFast && !_pollingFast)
        {
            _timer.Interval = TimeSpan.FromSeconds(3);
            _pollingFast = true;
        }
        else if (!shouldPollFast && _pollingFast)
        {
            _timer.Interval = TimeSpan.FromSeconds(30);
            _pollingFast = false;
        }
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
