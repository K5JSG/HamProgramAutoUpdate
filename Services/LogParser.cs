using System.Globalization;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Models;

namespace HamProgramAutoUpdate.Services;

/// <summary>
/// Reads an updater's log file and works out what happened: when it last ran,
/// whether it succeeded, and whether it actually installed anything.
///
/// The rules here were arrived at against real logs and are deliberately
/// conservative. In particular:
///  - A run's CLOSING line decides success or failure. Errors earlier in a
///    run may have been recovered from (CHIRP is refused by Cloudflare, then
///    succeeds through its browser fallback).
///  - "No update needed" must never count as an update. Negation phrases are
///    checked first and veto any update match.
/// </summary>
public static class LogParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    /// <summary>A run with no closing line whose log has been idle this long
    /// is treated as failed rather than still running.</summary>
    public static readonly TimeSpan StalledRunThreshold = TimeSpan.FromMinutes(15);

    // ---------------------------------------------------------------- headers

    private static readonly Regex[] Headers =
    {
        new(@"=+\r?\n(?<name>.+?)\s*:?\s*(?<date>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})\r?\n=+", Opts),
        new(@"=+\r?\n(?<name>.+?)\s*:?\s*(?<date>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})", Opts),
    };

    private static readonly Regex LogLine = new(@"\[(\d{2}:\d{2}:\d{2})\]", Opts);

    // ---------------------------------------------------------------- closers

    private static readonly Regex[] CloserFailure =
    {
        new(@"completed with error", Opts),
        new(@"\bFAILED\b", Opts),
        new(@"FAILURE:", Opts),
    };

    private static readonly Regex[] CloserSuccess =
    {
        new(@"completed successfully", Opts),
        new(@"Update Check Finished", Opts),
        new(@"Global update run completed at:", Opts),
    };

    private static readonly Regex[] Failures =
    {
        new(@"(FAILURE|completed with error|ERROR:|Failed|Crash)", Opts),
        new(@"FAILED TO RUN", Opts),
    };

    // ---------------------------------------------------------------- updates

    /// <summary>
    /// Phrases meaning no update happened. Checked FIRST: if a line matches
    /// any of these it can never count as an update. This exists because
    /// "No update needed." used to match the substring "Update needed".
    /// </summary>
    private static readonly Regex[] NoUpdate =
    {
        new(@"\bno\s+updates?\b", Opts),
        new(@"already up to date", Opts),
        new(@"\bup to date\b", Opts),
        new(@"nothing to do", Opts),
        new(@"no files changed", Opts),
        new(@"skipping", Opts),
        new(@"not installed", Opts),
        new(@"unchanged", Opts),
        new(@"repair only", Opts),
        new(@"deferred", Opts),
        new(@"checkonly", Opts),
        new(@"no newer version", Opts),
        new(@"fallback reference", Opts),
        new(@"matches remote", Opts),
    };

    /// <summary>Phrases meaning a new version really was installed.</summary>
    private static readonly Regex[] Updates =
    {
        new(@"(Updated to|upgraded|upgrade applied|SUCCESS: Updated)", Opts),
        new(@"GridTracker upgraded cleanly", Opts),
        new(@"Upgrade applied successfully", Opts),
        new(@"UPDATED \(Files modified:", Opts),
        new(@"Installing version", Opts),
        new(@"Successfully installed", Opts),
        new(@"Installation completed", Opts),
        new(@"Version changed .* -> ", Opts),
        new(@"SUCCESS: now on ", Opts),   // CHIRP: "SUCCESS: now on next-20260828"
    };

    /// <summary>True only when the line reports an actual version change.</summary>
    public static bool IsRealUpdate(string line)
    {
        foreach (var p in NoUpdate)
            if (p.IsMatch(line)) return false;

        foreach (var p in Updates)
            if (p.IsMatch(line)) return true;

        return false;
    }

    // ------------------------------------------------------------------ entry

    public sealed class ParseResult
    {
        public bool Exists { get; init; }
        public List<RunInfo> Runs { get; init; } = new();
        public RunStatus LatestStatus { get; init; } = RunStatus.Unknown;
        public DateTime? LatestRunTime { get; init; }
        public DateTime? LastUpdate { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public static ParseResult ParseFile(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return new ParseResult
            {
                Exists = false,
                LatestStatus = RunStatus.Unknown,
                ErrorMessage = "Log file not found",
            };
        }

        string content;
        DateTime mtime;
        try
        {
            content = ReadShared(logPath);
            mtime = File.GetLastWriteTime(logPath);
        }
        catch (Exception ex)
        {
            return new ParseResult
            {
                Exists = true,
                LatestStatus = RunStatus.Failed,
                ErrorMessage = $"Failed to read log: {ex.Message}",
            };
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new ParseResult
            {
                Exists = true,
                LatestStatus = RunStatus.Empty,
                ErrorMessage = "Log file is empty",
            };
        }

        var runs = ParseRuns(content, DateOnly.FromDateTime(mtime));
        var latest = runs.Count > 0 ? runs[^1] : null;

        // A run with no closing line reports Running. That is right while the
        // updater is working, but a crashed run would stay Running forever.
        // If the log stopped changing a while ago, the run died.
        if (latest is { Status: RunStatus.Running } &&
            DateTime.Now - mtime > StalledRunThreshold)
        {
            latest.Status = RunStatus.Failed;
            latest.Error ??= "Run ended without completing.";
        }

        var status = latest?.Status ?? RunStatus.Unknown;

        return new ParseResult
        {
            Exists = true,
            Runs = runs,
            LatestStatus = status,
            LatestRunTime = latest?.Timestamp,
            LastUpdate = NewestUpdate(runs),
            ErrorMessage = status == RunStatus.Failed ? latest?.Error : null,
        };
    }

    /// <summary>Read a log that an updater may currently have open for writing.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static DateTime? NewestUpdate(IEnumerable<RunInfo> runs)
    {
        DateTime? newest = null;
        foreach (var run in runs)
        {
            if (run.UpdateTime is { } t && (newest is null || t > newest))
                newest = t;
        }
        return newest;
    }

    // ------------------------------------------------------------------- runs

    private static List<RunInfo> ParseRuns(string content, DateOnly fallbackDate)
    {
        var runs = new List<RunInfo>();

        foreach (var header in Headers)
        {
            var matches = header.Matches(content);
            if (matches.Count == 0) continue;

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
                var run = ParseRun(content[start..end], matches[i].Groups["date"].Value);
                if (run is not null) runs.Add(run);
            }
            break;
        }

        if (runs.Count == 0)
        {
            // No parseable headers. Use the file's modification date rather
            // than today's: guessing "now" fabricates update timestamps that
            // are simply wrong, and those then get written into history.
            var run = ParseRun(content, fallbackDate.ToString("yyyy-MM-dd"));
            if (run is not null) runs.Add(run);
        }

        return runs;
    }

    private static RunInfo? ParseRun(string block, string headerDate)
    {
        var lines = block.Trim().Split('\n');

        var datePart = headerDate.Split(' ', 'T').FirstOrDefault();

        var timestamps = new List<string>();
        RunStatus? closer = null;
        string? error = null;
        string? firstError = null;
        DateTime? updateTime = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            var ts = LogLine.Match(line);
            if (ts.Success) timestamps.Add(ts.Groups[1].Value);

            // The closing line decides the outcome; a later closer overrides
            // an earlier one.
            if (CloserFailure.Any(p => p.IsMatch(line)))
            {
                closer = RunStatus.Failed;
                error = line.Trim();
            }
            else if (CloserSuccess.Any(p => p.IsMatch(line)))
            {
                closer = RunStatus.Success;
                error = null;
            }

            // In-run errors only matter if the run never closes.
            if (firstError is null && Failures.Any(p => p.IsMatch(line)))
                firstError = line.Trim();

            if (IsRealUpdate(line))
            {
                var stamp = timestamps.Count > 0 ? timestamps[^1] : null;
                updateTime = Combine(datePart, stamp) ?? ParseFull(headerDate);
            }
        }

        RunStatus status;
        if (closer is { } c)
        {
            status = c;
        }
        else
        {
            // Still running, or died partway. ParseFile settles which using
            // the log's modification time.
            status = RunStatus.Running;
            error = firstError;
        }

        var runTime = Combine(datePart, timestamps.FirstOrDefault()) ?? ParseFull(headerDate);

        return new RunInfo
        {
            Timestamp = runTime,
            Status = status,
            UpdateTime = updateTime,
            Error = error,
            LineCount = lines.Length,
        };
    }

    // ------------------------------------------------------------------ dates

    /// <summary>Combine a yyyy-MM-dd date with an HH:mm:ss time.</summary>
    private static DateTime? Combine(string? datePart, string? timePart)
    {
        if (string.IsNullOrEmpty(datePart) || string.IsNullOrEmpty(timePart)) return null;

        if (DateTime.TryParseExact($"{datePart} {timePart}", "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;

        return null;
    }

    /// <summary>Parse a full 'yyyy-MM-dd HH:mm:ss' (or ISO 'T') timestamp.</summary>
    private static DateTime? ParseFull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd" };
        if (DateTime.TryParseExact(value.Trim(), formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;

        return null;
    }
}
