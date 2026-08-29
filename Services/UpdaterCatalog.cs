namespace HamProgramAutoUpdate.Services;

/// <summary>One tracked program: where its update log lives. The update
/// logic itself lives in Services/Updaters/Programs - see UpdaterRegistry.</summary>
public sealed record UpdaterEntry(
    string Key,
    string DisplayName,
    string RelativeLogPath);

/// <summary>
/// The programs this dashboard tracks. Every path is relative to
/// %USERPROFILE%\Documents\Ham Radio, so the same build works for any
/// Windows account with no per-machine configuration. Log paths are kept
/// identical to what each program's old standalone updater script wrote, so
/// existing history/log files carry over with no migration.
/// </summary>
public static class UpdaterCatalog
{
    public static string HamRadioDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "Ham Radio");

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
    };

    public static string LogPath(UpdaterEntry entry) =>
        Path.Combine(HamRadioDir, entry.RelativeLogPath);

    public static UpdaterEntry? Find(string key) =>
        Entries.FirstOrDefault(e => e.Key == key);
}
