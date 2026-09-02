using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HamProgramAutoUpdate.Services;

public sealed class HistoryEntry
{
    [JsonPropertyName("last_update")]
    public string? LastUpdate { get; set; }

    [JsonPropertyName("recorded_at")]
    public string? RecordedAt { get; set; }

    [JsonPropertyName("manual")]
    public bool? Manual { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HamProgramAutoUpdate");

    public static string FilePath => Path.Combine(StateDir, "update_history.json");

    private Dictionary<string, HistoryEntry> _entries = new();

    public IReadOnlyDictionary<string, HistoryEntry> Entries => _entries;

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                _entries = new Dictionary<string, HistoryEntry>();
                return;
            }

            var json = File.ReadAllText(FilePath);
            _entries = JsonSerializer.Deserialize<Dictionary<string, HistoryEntry>>(json)
                       ?? new Dictionary<string, HistoryEntry>();
        }
        catch (Exception)
        {
            _entries = new Dictionary<string, HistoryEntry>();
        }
    }

    /// <summary>Persists the in-memory entries, merged with whatever is
    /// currently on disk (preferring, per key, whichever side has the newer
    /// RecordedAt) rather than blindly overwriting the file - the dashboard
    /// process and the separate headless --run-updates process each keep
    /// their own long-lived in-memory copy, so a plain overwrite would let
    /// whichever process calls Save() last silently discard an update the
    /// other one recorded in between. A named Mutex serializes the merge
    /// against a concurrent Save() from the other process; if it can't be
    /// acquired promptly this still proceeds rather than risk hanging a
    /// real update over it.</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(StateDir);

            using var mutex = new Mutex(false, @"Global\HamProgramAutoUpdate_HistoryStore");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }

                var onDisk = ReadFromDisk();
                foreach (var (key, entry) in _entries)
                {
                    if (!onDisk.TryGetValue(key, out var existing) || IsNewerOrEqual(entry, existing))
                        onDisk[key] = entry;
                }
                _entries = onDisk;

                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, JsonOpts));

                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Dictionary<string, HistoryEntry> ReadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, HistoryEntry>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, HistoryEntry>>(json)
                   ?? new Dictionary<string, HistoryEntry>();
        }
        catch (Exception)
        {
            return new Dictionary<string, HistoryEntry>();
        }
    }

    /// <summary>Parses a timestamp written by <see cref="RecordIfNewer"/> or
    /// <see cref="SetLastUpdate"/> ("o"/"s" format, both culture-invariant).
    /// Must parse with InvariantCulture to match - a plain culture-sensitive
    /// DateTime.TryParse would misread the stored ISO-shaped digits under a
    /// non-Gregorian default calendar (e.g. th-TH).</summary>
    private static bool TryParseStoredTimestamp(string? s, out DateTime result) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);

    private static bool IsNewerOrEqual(HistoryEntry a, HistoryEntry b)
    {
        var aTime = TryParseStoredTimestamp(a?.RecordedAt, out var at) ? at : DateTime.MinValue;
        var bTime = TryParseStoredTimestamp(b?.RecordedAt, out var bt) ? bt : DateTime.MinValue;
        return aTime >= bTime;
    }

    public DateTime? GetLastUpdate(string key)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry?.LastUpdate is null) return null;
        return TryParseStoredTimestamp(entry.LastUpdate, out var dt) ? dt : null;
    }

    public bool RecordIfNewer(string key, DateTime candidate)
    {
        var existing = GetLastUpdate(key);
        if (existing is not null && candidate <= existing) return false;

        _entries[key] = new HistoryEntry
        {
            LastUpdate = candidate.ToString("s"),
            RecordedAt = DateTime.Now.ToString("o"),
        };
        return true;
    }

    public bool SetLastUpdate(string key, DateTime value, string? source = null)
    {
        _entries[key] = new HistoryEntry
        {
            LastUpdate = value.ToString("s"),
            RecordedAt = DateTime.Now.ToString("o"),
            Manual = true,
            Source = source,
        };
        return Save();
    }
}
