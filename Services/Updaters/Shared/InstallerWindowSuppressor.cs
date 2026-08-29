using System.Runtime.InteropServices;
using System.Text;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Some silent installers still pop up windows despite a "/S"-style flag.
/// GridTracker and WSJT-X's Python updaters ran a 30ms-poll background thread
/// that hides any window whose title matches a known substring; RT Systems'
/// per-module RTUpdater_V5.exe goes further and needs its confirmation
/// dialogs actually dismissed (button-clicked), not just hidden, or the
/// update never proceeds. Both behaviors share the same EnumWindows poll loop.
/// </summary>
public sealed class InstallerWindowSuppressor : IDisposable
{
    private readonly string[] _titleSubstrings;
    private readonly string[]? _buttonLabelSubstrings;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="titleSubstrings">Windows whose title contains any of
    /// these (case-insensitive) are handled.</param>
    /// <param name="buttonLabelSubstrings">When set, look for a child button
    /// whose label contains one of these and click it (BM_CLICK) instead of
    /// just hiding the window - RT Systems' "OK"/"Update" dialogs need this.</param>
    public InstallerWindowSuppressor(string[] titleSubstrings, string[]? buttonLabelSubstrings = null)
    {
        _titleSubstrings = titleSubstrings;
        _buttonLabelSubstrings = buttonLabelSubstrings;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try { PollOnce(); } catch (Exception) { }
                Thread.Sleep(30);
            }
        }, token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }

    public void Dispose() => Stop();

    private void PollOnce()
    {
        var matches = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            var title = GetWindowText(hWnd);
            if (title.Length > 0 && _titleSubstrings.Any(s => title.Contains(s, StringComparison.OrdinalIgnoreCase)))
                matches.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (var hWnd in matches)
        {
            if (_buttonLabelSubstrings is { Length: > 0 } && TryClickChildButton(hWnd, _buttonLabelSubstrings))
                continue;

            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
        }
    }

    private static bool TryClickChildButton(IntPtr parent, string[] labelSubstrings)
    {
        var clicked = false;
        NativeMethods.EnumChildWindows(parent, (hWnd, _) =>
        {
            var text = GetWindowText(hWnd);
            if (text.Length > 0 && labelSubstrings.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                NativeMethods.PostMessage(hWnd, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                clicked = true;
                return false; // stop enumerating this window's children
            }
            return true;
        }, IntPtr.Zero);
        return clicked;
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        var length = NativeMethods.GetWindowTextLength(hWnd);
        if (length == 0) return "";
        var sb = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static class NativeMethods
    {
        public const int SW_HIDE = 0;
        public const uint BM_CLICK = 0x00F5;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
