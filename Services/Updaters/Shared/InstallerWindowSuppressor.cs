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
    private readonly string[] _visibleOnlyButtonLabelSubstrings;
    private readonly IntPtr _desktop;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="titleSubstrings">Windows whose title contains any of
    /// these (case-insensitive) are handled.</param>
    /// <param name="buttonLabelSubstrings">When set, look for a child button
    /// whose label contains one of these and click it (BM_CLICK) instead of
    /// just hiding the window - RT Systems' "OK" dialogs need this.</param>
    /// <param name="visibleOnlyButtonLabelSubstrings">Like
    /// <paramref name="buttonLabelSubstrings"/>, but only clicked while the
    /// window is on screen. For a button that also sits on a window the
    /// program keeps hidden on purpose: RTUpdater_V5 /silent creates its main
    /// "RT Updater V5" dialog hidden, with a real "Update" button on it, and
    /// clicking that while there's nothing to update makes it show "An error
    /// must have occured" and open its UpdateLog.Txt (seen live 2026-10-03).</param>
    /// <param name="desktop">A hidden desktop (see HiddenDesktopAutomation) to
    /// watch instead of the caller's own. Plain EnumWindows only sees the
    /// calling thread's desktop.</param>
    public InstallerWindowSuppressor(string[] titleSubstrings, string[]? buttonLabelSubstrings = null,
        string[]? visibleOnlyButtonLabelSubstrings = null, IntPtr desktop = default)
    {
        _titleSubstrings = titleSubstrings;
        _buttonLabelSubstrings = buttonLabelSubstrings;
        _visibleOnlyButtonLabelSubstrings = visibleOnlyButtonLabelSubstrings ?? Array.Empty<string>();
        _desktop = desktop;
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
        // Guards against the double-call every caller's using-block makes
        // (an explicit Stop() in a finally, then Dispose()'s own Stop()) -
        // without this early-return, the second call's Cancel() would throw
        // ObjectDisposedException on an already-disposed _cts.
        if (_cts is null) return;

        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        // Safe even if Wait() above timed out: PollOnce's loop only reads
        // IsCancellationRequested on its own locally-captured token, never
        // _cts itself, so disposing here can't throw into a still-running
        // loop iteration.
        _cts.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private void PollOnce()
    {
        var clickMode = _buttonLabelSubstrings is { Length: > 0 };
        var matches = new List<(IntPtr hWnd, bool visible)>();
        NativeMethods.EnumWindowsProc visit = (hWnd, _) =>
        {
            var visible = NativeMethods.IsWindowVisible(hWnd);
            // Hide mode has nothing to do for a window that's already hidden.
            // Click mode must still find it: run by the SYSTEM nightly task,
            // a program starts hidden (Task Scheduler's SW_HIDE carries into
            // its first dialog), so RTUpdater_V5's "All files are up to
            // date. [OK]" sits invisible and waits forever (seen live
            // 2026-10-03).
            if (!visible && !clickMode) return true;
            var title = GetWindowText(hWnd);
            if (title.Length > 0 && _titleSubstrings.Any(s => title.Contains(s, StringComparison.OrdinalIgnoreCase)))
                matches.Add((hWnd, visible));
            return true;
        };
        if (_desktop != IntPtr.Zero) NativeMethods.EnumDesktopWindows(_desktop, visit, IntPtr.Zero);
        else NativeMethods.EnumWindows(visit, IntPtr.Zero);

        foreach (var (hWnd, visible) in matches)
        {
            if (clickMode)
            {
                var labels = visible ? _buttonLabelSubstrings!.Concat(_visibleOnlyButtonLabelSubstrings).ToArray() : _buttonLabelSubstrings!;
                // BM_CLICK fakes a mouse click, which nothing on a non-input
                // desktop reliably acts on - use the WM_COMMAND route there.
                if (TryClickChildButton(hWnd, labels, simulateMouse: visible && _desktop == IntPtr.Zero))
                    continue;
            }

            if (visible) NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
        }
    }

    private static bool TryClickChildButton(IntPtr parent, string[] labelSubstrings, bool simulateMouse)
    {
        var clicked = false;
        NativeMethods.EnumChildWindows(parent, (hWnd, _) =>
        {
            // Buttons only: a static label like "Update not needed" matches
            // the same substrings, and its ID posted as a click is meaningless.
            if (!string.Equals(GetClassName(hWnd), "Button", StringComparison.OrdinalIgnoreCase)) return true;
            var text = GetWindowText(hWnd);
            if (text.Length > 0 && labelSubstrings.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                if (simulateMouse)
                {
                    NativeMethods.PostMessage(hWnd, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                }
                else
                {
                    // BM_CLICK fakes a mouse click, which a hidden, inactive
                    // dialog can ignore. WM_COMMAND/BN_CLICKED to the dialog
                    // is exactly what the button itself would send when
                    // clicked, and works whether or not it's on screen.
                    var id = NativeMethods.GetDlgCtrlID(hWnd);
                    var wParam = (IntPtr)((NativeMethods.BN_CLICKED << 16) | (id & 0xFFFF));
                    NativeMethods.PostMessage(parent, NativeMethods.WM_COMMAND, wParam, hWnd);
                }
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

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static class NativeMethods
    {
        public const int SW_HIDE = 0;
        public const uint BM_CLICK = 0x00F5;
        public const uint WM_COMMAND = 0x0111;
        public const int BN_CLICKED = 0;

        [DllImport("user32.dll")]
        public static extern int GetDlgCtrlID(IntPtr hWnd);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

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

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
