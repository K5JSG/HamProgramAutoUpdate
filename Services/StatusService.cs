using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HamProgramAutoUpdate.Models;
using HamProgramAutoUpdate.Services.Updaters;

namespace HamProgramAutoUpdate.Services;

/// <summary>
/// Pulls together the log parser, the update history and the running-process
/// tracker into the list of cards the window shows.
/// </summary>
public sealed class StatusService
{
    private readonly HistoryStore _history = new();
    private readonly UpdaterRunner _runner;

    public StatusService(UpdaterRunner runner) => _runner = runner;

    public HistoryStore History => _history;

    /// <summary>
    /// Status for every program available on this PC.
    /// </summary>
    public List<ProgramStatus> GetAll(bool includeUnavailable = false)
    {
        _history.Load();

        var historyChanged = false;
        var results = new List<ProgramStatus>();

        foreach (var entry in UpdaterCatalog.Entries)
        {
            var logPath = UpdaterCatalog.LogPath(entry);
            var logExists = File.Exists(logPath);
            var target = UpdaterRegistry.Find(entry.Key)?.DetectTarget() ?? DetectedTarget.NotFound;

            if (!includeUnavailable && !target.IsInstalled && !logExists) continue;

            var parsed = LogParser.ParseFile(logPath);

            // Record anything newer than what we already knew about
            if (parsed.LastUpdate is { } fromLog && _history.RecordIfNewer(entry.Key, fromLog))
                historyChanged = true;

            var remembered = _history.GetLastUpdate(entry.Key);

            results.Add(new ProgramStatus
            {
                Key = entry.Key,
                Name = entry.DisplayName,
                LogPath = logPath,
                TargetInstallPath = target.InstallPath,
                TargetInstalled = target.IsInstalled,
                TargetVersion = target.Version,
                LogExists = logExists,
                Runs = parsed.Runs,
                LatestStatus = parsed.LatestStatus,
                LatestRunTime = parsed.LatestRunTime,
                LastUpdate = remembered,
                LastUpdateInLog = parsed.LastUpdate,
                LastUpdateRemembered = remembered is not null && parsed.LastUpdate is null,
                ErrorMessage = parsed.ErrorMessage,
                IsRunning = _runner.IsRunning(entry.Key),
            });
        }

        if (historyChanged) _history.Save();

        return results
            .OrderByDescending(r => r.LatestRunTime ?? DateTime.MinValue)
            .ToList();
    }

    /// <summary>
    /// Empty one program's log, keeping its update history.
    /// </summary>
    public (bool ok, string? error) ClearLog(string key)
    {
        GetAll(includeUnavailable: true);

        var entry = UpdaterCatalog.Find(key);
        if (entry is null) return (false, "Unknown program");

        var path = UpdaterCatalog.LogPath(entry);
        if (!File.Exists(path)) return (false, "Log file not found");

        try
        {
            using var _ = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Empty every tracked log, keeping all update history.</summary>
    public (int cleared, List<string> failed) ClearAllLogs()
    {
        GetAll(includeUnavailable: true);

        var cleared = 0;
        var failed = new List<string>();

        foreach (var entry in UpdaterCatalog.Entries)
        {
            var path = UpdaterCatalog.LogPath(entry);
            if (!File.Exists(path)) continue;

            try
            {
                using var _ = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite);
                cleared++;
            }
            catch (Exception)
            {
                failed.Add(entry.DisplayName);
            }
        }

        return (cleared, failed);
    }
}
