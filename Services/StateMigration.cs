using System.Text.Json;
using System.Text.Json.Nodes;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services;

/// <summary>
/// One-time move of this Windows user's pre-1.9.0 data from
/// %LOCALAPPDATA%\HamProgramAutoUpdate to the shared
/// HistoryStore.StateDir under ProgramData, so the nightly task (now SYSTEM)
/// sees the same update history, markers, settings and tokens.
///
/// Runs at every app start (dashboard and CLI) but only does anything for a
/// user whose old folder hasn't been migrated yet; the installer's own
/// post-install --install-updates-task call covers the installing user
/// before the first nightly run. It has to happen in that user's context,
/// not SYSTEM's: the old tokens are encrypted for that user, so only they
/// can decrypt them to re-encrypt them for the machine.
///
/// The old files are left in place as a backup (a marker file stops this
/// re-running). Several Windows accounts each migrate when they next run the
/// app; their histories merge, newest entry per program wins.
/// </summary>
public static class StateMigration
{
    private const string DoneMarker = "moved_to_programdata.txt";

    /// <summary>Copied only if the shared folder doesn't have one yet.</summary>
    private static readonly string[] CopyIfMissing =
    {
        "updater_settings.json",
        "wsjtx_improved_build.txt",
    };

    /// <summary>Files holding a GitHubToken: copied if missing, token
    /// re-encrypted for the machine, file locked to Administrators/SYSTEM.</summary>
    private static readonly string[] TokenFiles =
    {
        "pota_updater_config.json",
        "vspe_switcher_config.json",
        "qsl_submissions_config.json",
    };

    /// <summary>Never throws.</summary>
    public static void Run()
    {
        try
        {
            var oldDir = HistoryStore.LegacyStateDir;
            var newDir = HistoryStore.StateDir;
            if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(oldDir) || File.Exists(Path.Combine(oldDir, DoneMarker))) return;

            Directory.CreateDirectory(newDir);

            if (!HistoryStore.MergeFrom(Path.Combine(oldDir, "update_history.json")))
                return; // retried next start rather than marking it done

            MoveLastFullRun(oldDir, newDir);

            foreach (var name in CopyIfMissing)
                CopyIfAbsent(Path.Combine(oldDir, name), Path.Combine(newDir, name));

            foreach (var name in TokenFiles)
                MigrateTokenFile(Path.Combine(oldDir, name), Path.Combine(newDir, name));

            File.WriteAllText(Path.Combine(oldDir, DoneMarker),
                $"Moved to {newDir} on {DateTime.Now:yyyy-MM-dd HH:mm}. These files are kept only as a backup.{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Retried at the next start.
        }
    }

    /// <summary>Keeps whichever last-full-run time is newer.</summary>
    private static void MoveLastFullRun(string oldDir, string newDir)
    {
        var oldPath = Path.Combine(oldDir, "last_full_run.txt");
        var newPath = Path.Combine(newDir, "last_full_run.txt");
        if (!File.Exists(oldPath)) return;

        if (!File.Exists(newPath) ||
            (ReadTime(oldPath) is { } o && (ReadTime(newPath) is not { } n || o > n)))
        {
            File.Copy(oldPath, newPath, overwrite: true);
        }

        static DateTime? ReadTime(string path) =>
            DateTime.TryParse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;
    }

    private static void CopyIfAbsent(string from, string to)
    {
        if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
    }

    /// <summary>Copies a token file across, re-encrypting a user-scoped
    /// GitHubToken for the machine while we're still running as the user who
    /// encrypted it. A plaintext token is left for the updater's own first
    /// read to encrypt. Accepts the bare-token form the VSPE file allows.</summary>
    private static void MigrateTokenFile(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;

        var text = File.ReadAllText(from).Trim();
        if (text.StartsWith('{'))
        {
            var node = JsonNode.Parse(text) as JsonObject;
            if (node?["GitHubToken"]?.GetValue<string>() is { } token && DpapiProtector.NeedsUpgrade(token))
            {
                try { node["GitHubToken"] = DpapiProtector.Protect(DpapiProtector.Unprotect(token)); }
                catch (Exception) { /* not this user's - copied as-is; the updater logs it */ }
            }
            text = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? text;
        }
        else if (DpapiProtector.NeedsUpgrade(text))
        {
            try { text = JsonSerializer.Serialize(new { GitHubToken = DpapiProtector.Protect(DpapiProtector.Unprotect(text)) }); }
            catch (Exception) { }
        }

        File.WriteAllText(to, text);
        SecureFile.RestrictToAdmins(to);
    }
}
