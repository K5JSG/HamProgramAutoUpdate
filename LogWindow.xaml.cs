using System.Diagnostics;
using System.Windows;

namespace HamProgramAutoUpdate;

public partial class LogWindow : Window
{
    private readonly string _path;

    public LogWindow(string displayName, string logPath)
    {
        InitializeComponent();

        _path = logPath;
        Title = $"{displayName} - Log";
        TitleText.Text = $"{displayName} - Log";
        PathText.Text = logPath;

        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                LogText.Text = "(the log file does not exist yet)";
                return;
            }

            // Share the file: an updater may have it open for writing
            using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var content = reader.ReadToEnd();
            LogText.Text = string.IsNullOrWhiteSpace(content) ? "(empty)" : content;
        }
        catch (Exception ex)
        {
            LogText.Text = $"Could not read the log:\n\n{ex.Message}";
        }

        // Newest run is at the bottom of these logs
        Dispatcher.BeginInvoke(new Action(() => Scroller.ScrollToEnd()));
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Load();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(_path);
            if (folder is null || !Directory.Exists(folder)) return;

            // Select the log in Explorer when it exists, otherwise just open
            // the folder.
            if (File.Exists(_path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the folder.\n\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
