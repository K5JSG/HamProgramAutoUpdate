using System.Text.Json;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Small per-user settings a couple of updaters need. NetLogger's download
/// gate requires registering a callsign/email with each request - the
/// original Python script had these hardcoded in source. Kept as an
/// overridable default here instead, so a real value ships out of the box
/// but nothing forces a rebuild to change it.
/// </summary>
public sealed class UpdaterSettings
{
    public string NetLoggerCallsign { get; set; } = "K5JSG";
    public string NetLoggerEmail { get; set; } = "k5jsg@arrl.net";

    /// <summary>Folder containing L4ONG.exe for a portable Log4OM install
    /// (the one holding the "config" subfolder next to it) - only needed on
    /// a PC that runs the portable flavor rather than the normal installed
    /// one. Unlike NetLogger's callsign/email, there is no sane out-of-the-
    /// box default: a portable copy has no installer to register itself
    /// anywhere, so it can only live wherever its owner chose to unzip it.
    /// Left blank, Log4omUpdater simply never finds a portable install (the
    /// normal installed flavor is still detected independently via the
    /// registry either way).</summary>
    public string Log4omPortablePath { get; set; } = "";

    private static string FilePath => Path.Combine(HistoryStore.StateDir, "updater_settings.json");

    public static UpdaterSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<UpdaterSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception)
        {
            // Fall through to defaults - a bad settings file should never
            // block an update run.
        }

        return new UpdaterSettings();
    }
}
