using System.Text.Json;

namespace HamProgramAutoUpdate.Services;

/// <summary>
/// Per-PC "snooze this program until a date and time" list, kept in
/// %ProgramData%\HamProgramAutoUpdate\snooze.json, e.g.
/// {"n1mm": "2026-10-18T12:00:00"} (local time). Every scheduled, dashboard
/// and catch-up run skips a snoozed program until its time passes, then
/// resumes by itself; --force-update ignores it. A missing or unreadable
/// file means nothing is snoozed. The dashboard's per-card Snooze button
/// writes this file; it can also be edited by hand.
///
/// Every method takes an optional <c>filePath</c> so tests can point it at a
/// scratch file instead of the real one.
/// </summary>
public static class UpdateSnooze
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HamProgramAutoUpdate", "snooze.json");

    /// <summary>The local time this program is snoozed until, or null if it
    /// isn't snoozed (no entry, expired, or the file can't be read).</summary>
    public static DateTime? SnoozedUntil(string key, DateTime? now = null, string? filePath = null)
    {
        var map = Load(filePath ?? FilePath);
        if (!map.TryGetValue(key, out var until)) return null;
        return until > (now ?? DateTime.Now) ? until : null;
    }

    /// <summary>Snooze <paramref name="key"/> until <paramref name="until"/>
    /// (local time). Other programs' snoozes are kept; expired ones are
    /// dropped while the file is rewritten. Returns an error message on
    /// failure, null on success.</summary>
    public static string? Set(string key, DateTime until, DateTime? now = null, string? filePath = null) =>
        Save(filePath ?? FilePath, key, until, now ?? DateTime.Now);

    /// <summary>Remove <paramref name="key"/>'s snooze, if any. Returns an
    /// error message on failure, null on success.</summary>
    public static string? Clear(string key, DateTime? now = null, string? filePath = null) =>
        Save(filePath ?? FilePath, key, null, now ?? DateTime.Now);

    private static Dictionary<string, DateTime> Load(string path)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return result;

            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (raw is null) return result;

            foreach (var pair in raw)
            {
                if (DateTime.TryParse(pair.Value, out var until)) result[pair.Key] = until;
            }
        }
        catch (Exception) { }

        return result;
    }

    private static string? Save(string path, string key, DateTime? until, DateTime now)
    {
        try
        {
            var map = Load(path);
            map.Remove(key);
            if (until is { } u) map[key] = u;

            // Expired entries are just noise; drop them whenever we rewrite.
            var live = map.Where(p => p.Value > now)
                .ToDictionary(p => p.Key, p => p.Value.ToString("yyyy-MM-ddTHH:mm:ss"),
                    StringComparer.OrdinalIgnoreCase);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(live, new JsonSerializerOptions { WriteIndented = true });

            // Write-then-replace so a headless run never reads a half-written file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
