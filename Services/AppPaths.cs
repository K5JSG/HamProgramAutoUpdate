namespace HamProgramAutoUpdate.Services;

/// <summary>
/// A permanent, single subfolder of the shared Windows Temp directory that
/// every temp file this app ever creates - each updater's own download/
/// staging folder, the scheduled-task XML files handed to schtasks, the
/// self-update's downloaded setup exe, and the folder Inno/NSIS installers
/// self-extract into while running silently - is routed into, instead of
/// scattering directly under %TEMP%. Lets a user add one antivirus/Norton
/// 360 folder exclusion (this folder) instead of excluding the whole shared
/// system temp directory.
/// </summary>
public static class AppPaths
{
    public static string TempDir
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "HamProgramAutoUpdate");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// SelfUpdateService's DownloadDir/InstallTempDir subfolder names -
    /// shared here so CleanupBestEffort can exempt them (see there for why)
    /// without SelfUpdateService and AppPaths each hardcoding the same
    /// string literal twice.
    /// </summary>
    public const string SelfUpdateDownloadSubfolder = "Updates";
    public const string SelfUpdateInstallTempSubfolder = "InstallTemp";

    /// <summary>
    /// Best-effort delete of everything under TempDir except the self-update
    /// download/install-temp subfolders, called when the app (interactive
    /// dashboard or a headless --run-updates/--check-updates/--force-update
    /// invocation) is closing. Each updater already cleans up its own
    /// per-run subfolder in its own try/finally as soon as that run
    /// finishes, so under normal conditions this only ever catches
    /// stragglers from a run that crashed instead of returning normally.
    ///
    /// The two self-update subfolders are skipped outright rather than just
    /// relying on delete-of-a-locked-folder throwing: when this process is
    /// exiting specifically to hand off to the installer it just launched
    /// (SelfUpdateService.DownloadAndLaunchInstallerAsync), Process.Start
    /// returns as soon as the child process object exists, not once Setup.exe
    /// has actually opened anything inside InstallTempDir - so at the moment
    /// OnExit runs here, that folder can still be completely empty and its
    /// delete succeeds instead of throwing, out from under the installer's
    /// own self-extraction a moment later (confirmed live: "Setup was unable
    /// to create the directory ...\InstallTemp\is-XXXXXXXX.tmp, Error 3").
    /// Both subfolders are already cleaned up the next time the app starts,
    /// by SelfUpdateService.CleanupOldDownloads, so leaving them for that
    /// instead of racing them here now costs nothing.
    /// </summary>
    public static void CleanupBestEffort()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(TempDir))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, SelfUpdateDownloadSubfolder, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, SelfUpdateInstallTempSubfolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
