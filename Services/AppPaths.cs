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
    /// Best-effort delete of everything under TempDir, called when the app
    /// (interactive dashboard or a headless --run-updates/--check-updates/
    /// --force-update invocation) is closing. Each updater already cleans up
    /// its own per-run subfolder in its own try/finally as soon as that run
    /// finishes, so under normal conditions this only ever catches stragglers
    /// - a run that crashed instead of returning normally, or a self-update
    /// download/install folder that is still genuinely in use because this
    /// process is exiting specifically to hand off to the installer it just
    /// launched. Deleting a still-open file/folder throws; that's caught and
    /// skipped per-entry rather than aborting the whole cleanup, so one busy
    /// folder can never block the rest from being removed, and whatever is
    /// skipped just gets picked up by the next cleanup instead.
    /// </summary>
    public static void CleanupBestEffort()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(TempDir))
            {
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
