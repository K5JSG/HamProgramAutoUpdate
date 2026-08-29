using System.Diagnostics;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>Standard Windows Installer exit codes, worth explaining to a user
/// or a log reader instead of just printing the number. POTA's Python script
/// had the most complete table of the three MSI-based updaters; this is that
/// table, kept as the one shared copy.</summary>
public static class MsiExitCodes
{
    private static readonly Dictionary<int, string> Messages = new()
    {
        [1602] = "The user cancelled installation.",
        [1603] = "A fatal error occurred during installation.",
        [1618] = "Another installation is already in progress.",
        [1619] = "The installation package could not be opened.",
        [1625] = "The installation is forbidden by system policy.",
        [1638] = "A newer or equal version is already installed.",
        [1641] = "Installation succeeded and a restart has been initiated.",
    };

    /// <summary>0 and 3010 both mean success (3010 = reboot required).</summary>
    public static bool IsSuccess(int exitCode) => exitCode is 0 or 3010 or 1641;

    public static string Describe(int exitCode) =>
        Messages.TryGetValue(exitCode, out var message)
            ? message
            : $"msiexec returned exit code {exitCode}.";
}

public static class MsiInstaller
{
    /// <summary>Runs `msiexec /i &lt;path&gt; /qn /norestart /l*v &lt;logPath&gt;` and
    /// waits for it to finish. Requires the caller to already be elevated.</summary>
    public static async Task<(bool ok, int exitCode, string message)> InstallAsync(
        string msiPath, string logPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/i");
        psi.ArgumentList.Add(msiPath);
        psi.ArgumentList.Add("/qn");
        psi.ArgumentList.Add("/norestart");
        psi.ArgumentList.Add("/l*v");
        psi.ArgumentList.Add(logPath);

        using var proc = Process.Start(psi);
        if (proc is null) return (false, -1, "msiexec did not start.");

        await proc.WaitForExitAsync(ct);

        var ok = MsiExitCodes.IsSuccess(proc.ExitCode);
        return (ok, proc.ExitCode, MsiExitCodes.Describe(proc.ExitCode));
    }
}
