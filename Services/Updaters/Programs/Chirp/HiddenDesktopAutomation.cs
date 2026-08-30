using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HamProgramAutoUpdate.Services.Updaters.Programs.Chirp;

/// <summary>
/// Win32 plumbing for running Chrome on a non-input ("hidden") desktop and
/// clicking into it without ever touching the real mouse - a line-for-line
/// port of the same technique Chirp Update Script.py used successfully
/// (CreateDesktopW + PostMessageW to a RenderWidget HWND; see that file's
/// history in ChirpUpdaterSource\ for the full story of what was tried and
/// ruled out to arrive at this).
///
/// One deliberate difference from the Python version: that script relaunched
/// its own process onto the hidden desktop first (so plain EnumWindows, which
/// only sees the calling thread's current desktop, would work), then spawned
/// Chrome as its child. This C# port instead stays on the normal thread/
/// desktop throughout and uses EnumDesktopWindows (which explicitly takes a
/// target desktop handle) to look into the hidden desktop from outside it.
/// PostMessageW/EnumChildWindows/GetClientRect all operate on a window handle
/// directly and are not restricted to the calling thread's own desktop, so
/// this should be equivalent - but it is the one part of this port that is
/// genuinely new versus the proven Python structure, worth knowing about if
/// window discovery ever comes up empty where the Python version found
/// windows fine.
/// </summary>
public static class HiddenDesktopAutomation
{
    private const uint GENERIC_ALL = 0x10000000;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const int SW_SHOWNORMAL = 1;

    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const int MK_LBUTTON = 0x0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars;
        public int dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, int dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Creates a fresh, non-input desktop with full access for this
    /// process. Chrome (and anything else) launched with STARTUPINFO.lpDesktop
    /// set to <paramref name="name"/> renders there instead of the real one -
    /// nothing is ever visible, and the real mouse/keyboard are untouched.</summary>
    public static IntPtr CreateHiddenDesktop(string name)
    {
        var handle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDesktopW failed (error {Marshal.GetLastWin32Error()}).");
        return handle;
    }

    public static void DestroyDesktop(IntPtr hDesktop)
    {
        if (hDesktop != IntPtr.Zero) CloseDesktop(hDesktop);
    }

    /// <summary>Launches <paramref name="exePath"/> onto the named hidden
    /// desktop and hands back a normal managed Process (obtained by PID, not
    /// by the raw handle CreateProcessW returns) so the caller can use
    /// ordinary WaitForExitAsync/Kill from here on - only the launch itself
    /// needs the raw Win32 call, since ProcessStartInfo has no way to target
    /// a specific desktop.</summary>
    public static Process StartOnDesktop(string exePath, string arguments, string desktopName)
    {
        var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");
        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpDesktop = desktopName,
            dwFlags = STARTF_USESHOWWINDOW,
            wShowWindow = SW_SHOWNORMAL,
        };

        var ok = CreateProcessW(
            null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
            Path.GetDirectoryName(exePath), ref si, out var pi);

        if (!ok)
            throw new InvalidOperationException($"CreateProcessW failed to launch '{exePath}' (error {Marshal.GetLastWin32Error()}).");

        try
        {
            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
    }

    /// <summary>(hwnd, title) for every Chrome_WidgetWin_1 top-level window on
    /// the given desktop.</summary>
    public static List<(IntPtr Hwnd, string Title)> FindChromeWindows(IntPtr hDesktop)
    {
        var found = new List<(IntPtr, string)>();
        EnumDesktopWindows(hDesktop, (hWnd, _) =>
        {
            var cls = new StringBuilder(256);
            GetClassNameW(hWnd, cls, cls.Capacity);
            if (cls.ToString() == "Chrome_WidgetWin_1")
            {
                var title = new StringBuilder(512);
                GetWindowTextW(hWnd, title, title.Capacity);
                found.Add((hWnd, title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Every RenderWidget child of the given top-level Chrome window,
    /// with its client size. Chrome creates one per out-of-process iframe -
    /// Cloudflare's Turnstile challenge lives in one of these.</summary>
    public static List<(IntPtr Hwnd, int Width, int Height)> RenderWidgets(IntPtr hwndParent)
    {
        var found = new List<(IntPtr, int, int)>();
        EnumChildWindows(hwndParent, (hWnd, _) =>
        {
            var cls = new StringBuilder(256);
            GetClassNameW(hWnd, cls, cls.Capacity);
            if (cls.ToString().Contains("RenderWidget"))
            {
                GetClientRect(hWnd, out var rect);
                found.Add((hWnd, rect.Right - rect.Left, rect.Bottom - rect.Top));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void PostMove(IntPtr hwnd, int x, int y) =>
        PostMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(x, y));

    /// <summary>Approach, press, release - paced so it does not look
    /// instantaneous, same timing as the Python version. Posted messages land
    /// in the target window's real message queue and Chrome reports the
    /// resulting event as isTrusted=true, unlike a JS-dispatched synthetic
    /// click.</summary>
    public static async Task PostClickAsync(IntPtr hwnd, int x, int y, CancellationToken ct)
    {
        foreach (var (dx, dy) in new[] { (-25, -18), (-12, -8), (-4, -2), (0, 0) })
        {
            PostMessageW(hwnd, WM_MOUSEMOVE, IntPtr.Zero, MakeLParam(x + dx, y + dy));
            await Task.Delay(40, ct);
        }
        await Task.Delay(120, ct);
        PostMessageW(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, MakeLParam(x, y));
        await Task.Delay(90, ct);
        PostMessageW(hwnd, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(x, y));
    }

    private static IntPtr MakeLParam(int x, int y) => (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
}
