using System.Diagnostics;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Locks a file holding a secret down to Administrators and SYSTEM. The
/// shared data folder under ProgramData lets every user on the PC read its
/// files by default, and a machine-scoped DPAPI value (see DpapiProtector)
/// can be decrypted by any account on the PC - so the file's own ACL is what
/// keeps a standard user from reading the token. Uses icacls rather than the
/// ACL APIs, keeping the app free of extra package dependencies like the rest
/// of this project. Best effort; never throws.
/// </summary>
public static class SecureFile
{
    private const string AdministratorsSid = "*S-1-5-32-544";
    private const string SystemSid = "*S-1-5-18";

    public static void RestrictToAdmins(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var psi = new ProcessStartInfo("icacls.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/inheritance:r");
            psi.ArgumentList.Add("/grant:r");
            psi.ArgumentList.Add($"{AdministratorsSid}:F");
            psi.ArgumentList.Add("/grant:r");
            psi.ArgumentList.Add($"{SystemSid}:F");

            using var proc = Process.Start(psi);
            if (proc is null) return;
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15_000))
            {
                try { proc.Kill(); } catch (Exception) { }
            }
        }
        catch (Exception)
        {
        }
    }
}
