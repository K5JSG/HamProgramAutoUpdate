using System.Diagnostics;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Was copy-pasted (with the same explanatory comment) across PotaUpdater,
/// HrdUpdater, N1mmUpdater and Log4omUpdater before being pulled out here.
/// Only the "safely find matching processes" part is shared - what each
/// caller then does with them (just check, close gracefully then kill,
/// kill immediately) still genuinely differs per program and stays there.
/// </summary>
public static class ProcessFinder
{
    /// <summary>Processes currently running with this exact name (no ".exe").
    /// Empty on any failure, including an empty/unresolvable name -
    /// GetProcessesByName("") is not a reliable "match nothing" query on
    /// .NET 8/Windows (observed matching something unrelated), so an
    /// empty/whitespace name must never be passed through to it.</summary>
    public static Process[] FindByName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return Array.Empty<Process>();

        try
        {
            return Process.GetProcessesByName(processName);
        }
        catch (Exception)
        {
            return Array.Empty<Process>();
        }
    }

    /// <summary>Processes currently running from this exe path, matched by
    /// filename only (Windows doesn't expose a running process's original
    /// launch path cheaply).</summary>
    public static Process[] FindByExePath(string? exePath) =>
        exePath is null ? Array.Empty<Process>() : FindByName(Path.GetFileNameWithoutExtension(exePath));
}
