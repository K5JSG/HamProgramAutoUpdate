using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Starts a program as the signed-in Windows user, from the nightly task
/// running as SYSTEM. For the rare tool that only works as a real user (its
/// own login and data live in that user's profile) - everything else runs
/// fine as SYSTEM. Picks an active session first, then a disconnected one
/// (e.g. a closed Remote Desktop window, still signed in); returns null if
/// nobody is signed in at all.
///
/// The child gets the user's own environment (so %LOCALAPPDATA% etc. point
/// into their profile), its stdout+stderr come back line by line, and it runs
/// inside a job object so a cancel can end it together with anything it
/// started. SYSTEM is required: WTSQueryUserToken needs SeTcbPrivilege.
/// </summary>
public sealed class UserSessionLauncher : IDisposable
{
    public static bool IsRunningAsSystem
    {
        get
        {
            try { using var id = WindowsIdentity.GetCurrent(); return id.IsSystem; }
            catch (Exception) { return false; }
        }
    }

    private readonly IntPtr _process;
    private readonly IntPtr _job;
    private readonly Task _reader;

    public string UserName { get; }

    private UserSessionLauncher(IntPtr process, IntPtr job, Task reader, string userName)
    {
        _process = process;
        _job = job;
        _reader = reader;
        UserName = userName;
    }

    /// <summary>Starts <paramref name="exePath"/> in a signed-in user's
    /// session, or returns null if nobody is signed in. Each output line goes
    /// to <paramref name="onLine"/> (on a background thread). Throws
    /// Win32Exception if a signed-in user was found but the start failed.</summary>
    public static UserSessionLauncher? Start(string exePath, IEnumerable<string> args, string workingDir, Action<string> onLine)
    {
        var (sessionId, userName) = FindSignedInSession();
        if (sessionId is null) return null;

        IntPtr token = IntPtr.Zero, env = IntPtr.Zero, readPipe = IntPtr.Zero, writePipe = IntPtr.Zero, job = IntPtr.Zero;
        var pi = new PROCESS_INFORMATION();
        try
        {
            if (!WTSQueryUserToken(sessionId.Value, out token)) throw new Win32Exception();
            if (!CreateEnvironmentBlock(out env, token, false)) throw new Win32Exception();

            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            if (!CreatePipe(out readPipe, out writePipe, ref sa, 0)) throw new Win32Exception();
            if (!SetHandleInformation(readPipe, HANDLE_FLAG_INHERIT, 0)) throw new Win32Exception();

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW,
                wShowWindow = SW_HIDE,
                hStdOutput = writePipe,
                hStdError = writePipe,
            };

            var cmd = new StringBuilder(Quote(exePath));
            foreach (var a in args) cmd.Append(' ').Append(Quote(a));

            if (!CreateProcessAsUser(token, null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                    CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                    env, workingDir, ref si, out pi))
            {
                throw new Win32Exception();
            }

            // Job object so Kill() also ends anything the tool started.
            job = CreateJobObject(IntPtr.Zero, null);
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, pi.hProcess);
            ResumeThread(pi.hThread);

            // Only the child may hold the write end now, or the reader never
            // sees end-of-file.
            CloseHandle(writePipe);
            writePipe = IntPtr.Zero;

            var readHandle = new SafeFileHandle(readPipe, ownsHandle: true);
            readPipe = IntPtr.Zero;
            var reader = Task.Run(() =>
            {
                try
                {
                    using var stream = new FileStream(readHandle, FileAccess.Read);
                    using var sr = new StreamReader(stream, Encoding.UTF8);
                    string? line;
                    while ((line = sr.ReadLine()) is not null) onLine(line);
                }
                catch (Exception) { }
            });

            var launched = new UserSessionLauncher(pi.hProcess, job, reader, userName ?? "?");
            pi.hProcess = IntPtr.Zero;
            job = IntPtr.Zero;
            return launched;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (job != IntPtr.Zero) CloseHandle(job);
            if (writePipe != IntPtr.Zero) CloseHandle(writePipe);
            if (readPipe != IntPtr.Zero) CloseHandle(readPipe);
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>Waits for the program to exit (and its output to be fully
    /// read), returning its exit code. On cancellation the caller should
    /// Kill().</summary>
    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        while (WaitForSingleObject(_process, 0) == WAIT_TIMEOUT)
            await Task.Delay(250, ct);
        await _reader;
        return GetExitCodeProcess(_process, out var code) ? (int)code : -1;
    }

    public void Kill()
    {
        try
        {
            if (_job != IntPtr.Zero) TerminateJobObject(_job, 1);
            else TerminateProcess(_process, 1);
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (_job != IntPtr.Zero) CloseHandle(_job);
        if (_process != IntPtr.Zero) CloseHandle(_process);
    }

    private static (int? sessionId, string? userName) FindSignedInSession()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count)) return (null, null);
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            var sessions = Enumerable.Range(0, count)
                .Select(i => Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size))
                .Where(s => s.SessionID != 0 && (s.State == WTSActive || s.State == WTSDisconnected))
                .OrderBy(s => s.State == WTSActive ? 0 : 1)
                .ToList();

            foreach (var s in sessions)
            {
                var user = QuerySessionString(s.SessionID, WTSUserName);
                if (string.IsNullOrEmpty(user)) continue;
                var domain = QuerySessionString(s.SessionID, WTSDomainName);
                return (s.SessionID, string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}");
            }
            return (null, null);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? QuerySessionString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    // ------------------------------------------------------------- native

    private const int WTSActive = 0, WTSDisconnected = 4;
    private const int WTSUserName = 5, WTSDomainName = 7;
    private const uint HANDLE_FLAG_INHERIT = 1;
    private const int STARTF_USESHOWWINDOW = 0x1, STARTF_USESTDHANDLES = 0x100;
    private const short SW_HIDE = 0;
    private const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
    private const uint WAIT_TIMEOUT = 0x102;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionID;
        public IntPtr pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES sa, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? app, StringBuilder cmdLine,
        IntPtr procAttrs, IntPtr threadAttrs, bool inheritHandles, uint flags, IntPtr env,
        string? currentDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
