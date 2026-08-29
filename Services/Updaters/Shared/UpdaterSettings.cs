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

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HamProgramAutoUpdate", "updater_settings.json");

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
