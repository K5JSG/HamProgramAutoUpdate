namespace HamProgramAutoUpdate.Services;

/// <summary>One tracked program: where its update log lives. The update
/// logic itself lives in Services/Updaters/Programs - see UpdaterRegistry.</summary>
public sealed record UpdaterEntry(
    string Key,
    string DisplayName,
    string RelativeLogPath);

/// <summary>
/// The programs this dashboard tracks.
/// </summary>
public static class UpdaterCatalog
{
    /// <summary>
    /// Where each program's OWN installer/updater historically wrote its log,
    /// back when every updater was a separate standalone script. Still used
    /// to detect a pre-existing install (see PotaUpdater) and as the
    /// one-time migration source in <see cref="LogPath"/> - not where the
    /// dashboard itself writes anymore.
    /// </summary>
    public static string HamRadioDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "Ham Radio");

    /// <summary>
    /// All logs live together under the installation's shared ProgramData
    /// folder rather than scattered across per-program folders in the
    /// user's Documents, so everything the dashboard manages stays in one
    /// place. Machine-wide (not per-user) because the app itself runs
    /// elevated and the scheduled tasks may run under a different session.
    /// </summary>
    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HamProgramAutoUpdate", "Logs");

    public static readonly IReadOnlyList<UpdaterEntry> Entries = new List<UpdaterEntry>
    {
        new("bkttimesync", "BktTimeSync",
            @"BktTimeSync Updater\bkttimesync_updater.log"),

        new("chirp", "CHIRP",
            @"Chirp Update Script\chirp_updater.log"),

        new("gridtracker", "GridTracker",
            @"Gridtracker Update Script\gridtracker_updater.log"),

        new("hrd", "Ham Radio Deluxe",
            @"HRD Update Script\HRD_Update_Script.log"),

        new("log4om", "Log4OM",
            @"Log4OM Update Script\log4om_updater.log"),

        new("n1mm", "N1MM Logger+",
            @"N1MM Logger+\N1MM Updater Script\N1MM_Updater.log"),

        new("netlogger", "NetLogger",
            @"Netlogger Update Script\netlogger_updater.log"),

        new("pota", "POTA Activator",
            @"POTA Activator Parks Activations Updater\POTA_Activator_Parks_Activation.log"),

        new("rt_systems", "RT Systems",
            @"RT Systems Update Script\rt_update_log.log"),

        new("tqsl", "TQSL",
            @"TQSL Updater\tqsl_updater.log"),

        new("wsjtx", "WSJT-X",
            @"WSJTX Update Script\wsjtx_updater.log"),

        new("wsjtx_improved", "WSJT-X Improved",
            @"WSJT-X Improved Update Script\wsjtx_improved_updater.log"),
    };

    /// <summary>
    /// The consolidated path the dashboard reads/writes this program's log
    /// at. On first access after upgrading from a version that still used
    /// per-program folders under Documents\Ham Radio, migrates that old file
    /// in so recent run history isn't lost.
    /// </summary>
    public static string LogPath(UpdaterEntry entry)
    {
        var consolidated = Path.Combine(LogDir, Path.GetFileName(entry.RelativeLogPath));
        MigrateLegacyLog(entry, consolidated);
        return consolidated;
    }

    /// <summary>
    /// One-time move of a pre-existing per-program folder's log into the
    /// consolidated path. Every program's updater (including CHIRP now - see
    /// ChirpUpdater's own migration of its other files) writes straight to
    /// the consolidated path afterward, so this never needs to run twice.
    /// </summary>
    private static void MigrateLegacyLog(UpdaterEntry entry, string consolidatedPath)
    {
        if (File.Exists(consolidatedPath)) return;

        var legacyPath = Path.Combine(HamRadioDir, entry.RelativeLogPath);
        if (!File.Exists(legacyPath)) return;

        try
        {
            Directory.CreateDirectory(LogDir);
            File.Move(legacyPath, consolidatedPath);
        }
        catch (Exception)
        {
            // Best-effort: starting the program with a fresh, empty log is
            // not worth failing over.
        }
    }

    public static UpdaterEntry? Find(string key) =>
        Entries.FirstOrDefault(e => e.Key == key);
}
