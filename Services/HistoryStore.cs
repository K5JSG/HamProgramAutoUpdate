using System;
using System.Collections.Generic;
using System.IO;
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

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, JsonOpts));

            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public DateTime? GetLastUpdate(string key)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry.LastUpdate is null) return null;
        return DateTime.TryParse(entry.LastUpdate, out var dt) ? dt : null;
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
