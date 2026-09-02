using System.Windows;
using System.Windows.Media;
using HamProgramAutoUpdate.Models;

namespace HamProgramAutoUpdate;

/// <summary>
/// Presentation wrapper: turns a ProgramStatus into the strings, brushes and
/// visibilities the card template binds to. Keeping the formatting here means
/// the XAML needs no converters.
/// </summary>
public sealed class CardViewModel
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xAA));
    private static readonly Brush AccentBright = new SolidColorBrush(Color.FromRgb(0x00, 0xE8, 0xBB));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xFF, 0x47, 0x57));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC3, 0x00));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA));

    private static readonly Brush PanelFill = new SolidColorBrush(Color.FromRgb(0x12, 0x18, 0x2F));
    private static readonly Brush PanelBorder = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x4A));
    private static readonly Brush RecentFill = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0xD4, 0xAA));
    private static readonly Brush RecentBorder = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0xD4, 0xAA));

    // Same colors as Accent/Danger/Warn/Muted above, just with a translucent
    // fill alpha - cached the same way so StatusFill doesn't allocate a fresh
    // SolidColorBrush on every single binding evaluation (every card, every
    // 3-30s refresh tick).
    private static readonly Brush SuccessFill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xD4, 0xAA));
    private static readonly Brush FailedFill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x47, 0x57));
    private static readonly Brush RunningFill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xC3, 0x00));
    private static readonly Brush UnknownFill = new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88));

    /// <summary>An update within this many calendar days is highlighted.</summary>
    public const int RecentDays = 3;

    private readonly ProgramStatus _s;

    public CardViewModel(ProgramStatus status)
    {
        _s = status;
    }

    static CardViewModel()
    {
        AllBrushesFrozen();
    }

    private static void AllBrushesFrozen()
    {
        // Freezing shared brushes lets WPF reuse them across cards
        foreach (var b in new[] { Accent, AccentBright, Danger, Warn, Muted, Dim, Text,
                                  PanelFill, PanelBorder, RecentFill, RecentBorder,
                                  SuccessFill, FailedFill, RunningFill, UnknownFill })
        {
            if (b.CanFreeze && !b.IsFrozen) b.Freeze();
        }
    }

    public string Key => _s.Key;
    public string Name => _s.Name;
    public ProgramStatus Model => _s;

    // ------------------------------------------------------------- status

    public string StatusLabel => _s.LatestStatus switch
    {
        RunStatus.Success => "SUCCESS",
        RunStatus.Failed => "FAILED",
        RunStatus.Running => "RUNNING",
        RunStatus.Empty => "EMPTY",
        _ => "UNKNOWN",
    };

    public Brush StatusColor => _s.LatestStatus switch
    {
        RunStatus.Success => Accent,
        RunStatus.Failed => Danger,
        RunStatus.Running => Warn,
        _ => Muted,
    };

    public Brush StatusFill => _s.LatestStatus switch
    {
        RunStatus.Success => SuccessFill,
        RunStatus.Failed => FailedFill,
        RunStatus.Running => RunningFill,
        _ => UnknownFill,
    };

    // -------------------------------------------------------------- dates

    public string LastRunText => _s.LatestRunTime?.ToString("g") ?? "Never";

    public string LastUpdateText => FormatUpdate(_s.LastUpdate);

    public string LastUpdateTooltip => _s.LastUpdate?.ToString("F") ?? "";

    public Brush UpdateTextColor =>
        _s.LastUpdate is null ? Dim : (IsRecent ? AccentBright : Text);

    public bool IsRecent => DaysAgo(_s.LastUpdate) is { } d && d >= 0 && d <= RecentDays;

    public Brush UpdateFill => IsRecent ? RecentFill : PanelFill;
    public Brush UpdateBorder => IsRecent ? RecentBorder : PanelBorder;

    public Visibility RememberedVisibility =>
        _s.LastUpdateRemembered ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Calendar days between then and now, compared at midnight so the count
    /// matches what a person means by "days ago". An update at 11pm last
    /// night is yesterday, not today.
    /// </summary>
    private static int? DaysAgo(DateTime? value)
    {
        if (value is null) return null;
        var then = value.Value.Date;
        var today = DateTime.Now.Date;
        return (int)Math.Round((today - then).TotalDays);
    }

    private static string FormatUpdate(DateTime? value)
    {
        if (value is null) return "None recorded";

        var days = DaysAgo(value) ?? 0;
        string relative = days switch
        {
            < 0 => "in the future",
            0 => "today",
            1 => "yesterday",
            < 30 => $"{days} days ago",
            < 60 => "last month",
            < 365 => $"{days / 30} months ago",
            < 730 => "last year",
            _ => $"{days / 365} years ago",
        };

        return $"{value.Value:d} ({relative})";
    }

    // ------------------------------------------------------------ buttons

    public bool CanRun => _s.TargetInstalled && !_s.IsRunning;

    public string RunButtonText => _s.IsRunning ? "Running..." : "Run";

    public string RunTooltip => !_s.TargetInstalled
        ? $"{_s.Name} was not detected on this PC - nothing to update."
        : _s.IsRunning
            ? "Already running"
            : "Run this program's updater now";

    // ------------------------------------------------------------- errors

    public string ErrorMessage => _s.ErrorMessage ?? "";

    public Visibility ErrorVisibility =>
        string.IsNullOrWhiteSpace(_s.ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;
}
