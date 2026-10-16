using System.Windows;
using System.Windows.Controls;

namespace HamProgramAutoUpdate;

/// <summary>
/// Date + time picker for a card's Snooze button. It only collects the
/// choice - the caller writes it (UpdateSnooze) so a failed write can be
/// reported. After ShowDialog() returns true, read <see cref="SnoozeUntil"/>
/// (a time to snooze until) or <see cref="Cleared"/> (remove the snooze).
/// </summary>
public partial class SnoozeWindow : Window
{
    private bool _loading = true;

    public DateTime? SnoozeUntil { get; private set; }
    public bool Cleared { get; private set; }

    public SnoozeWindow(string displayName, DateTime? currentlySnoozedUntil)
    {
        InitializeComponent();

        TitleText.Text = $"Snooze {displayName} updates";

        for (var h = 1; h <= 12; h++) HourBox.Items.Add(h.ToString());
        for (var m = 0; m < 60; m++) MinuteBox.Items.Add(m.ToString("00"));
        AmPmBox.Items.Add("AM");
        AmPmBox.Items.Add("PM");

        DateBox.DisplayDateStart = DateTime.Today;

        // Open on the current snooze if there is one, otherwise tomorrow at noon.
        var start = currentlySnoozedUntil ?? DateTime.Today.AddDays(1).AddHours(12);
        DateBox.SelectedDate = start.Date;
        HourBox.SelectedIndex = (start.Hour % 12 == 0 ? 12 : start.Hour % 12) - 1;
        MinuteBox.SelectedIndex = start.Minute;
        AmPmBox.SelectedIndex = start.Hour >= 12 ? 1 : 0;

        ClearButton.Visibility = currentlySnoozedUntil is null ? Visibility.Collapsed : Visibility.Visible;

        _loading = false;
        UpdateSummary();
    }

    /// <summary>The picked date and time, or null if any part is missing.</summary>
    private DateTime? Picked()
    {
        if (DateBox.SelectedDate is not { } date) return null;
        if (HourBox.SelectedIndex < 0 || MinuteBox.SelectedIndex < 0 || AmPmBox.SelectedIndex < 0) return null;

        var hour = HourBox.SelectedIndex + 1;          // 1..12
        if (hour == 12) hour = 0;
        if (AmPmBox.SelectedIndex == 1) hour += 12;

        return date.Date.AddHours(hour).AddMinutes(MinuteBox.SelectedIndex);
    }

    private void UpdateSummary()
    {
        var picked = Picked();
        if (picked is null)
        {
            SummaryText.Text = "Pick a date and a time.";
            SnoozeButton.IsEnabled = false;
        }
        else if (picked <= DateTime.Now)
        {
            SummaryText.Text = "That time has already passed - pick a time in the future.";
            SnoozeButton.IsEnabled = false;
        }
        else
        {
            SummaryText.Text = $"Updates resume after {picked:dddd, MMMM d, yyyy 'at' h:mm tt}.";
            SnoozeButton.IsEnabled = true;
        }
    }

    private void Input_Changed(object sender, EventArgs e)
    {
        if (!_loading) UpdateSummary();
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked is null || picked <= DateTime.Now) return;

        SnoozeUntil = picked;
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Cleared = true;
        DialogResult = true;
    }
}
