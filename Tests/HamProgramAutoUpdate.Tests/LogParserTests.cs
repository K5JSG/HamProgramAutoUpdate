using HamProgramAutoUpdate.Models;
using HamProgramAutoUpdate.Services;

namespace HamProgramAutoUpdate.Tests;

public class LogParserTests
{
    private static string Log(params string[] lines) => string.Join("\n", lines) + "\n";

    private static string WriteTempLog(string content, DateTime? lastWriteTime = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"logparsertest_{Guid.NewGuid():N}.log");
        File.WriteAllText(path, content);
        if (lastWriteTime is { } t) File.SetLastWriteTime(path, t);
        return path;
    }

    // ---------------------------------------------------------- IsRealUpdate

    [Theory]
    [InlineData("[14:04:30] Updated to 1.3.0.", true)]
    [InlineData("[14:12:38] Success: Upgrade applied successfully to version 1.3.1!", true)]
    [InlineData("[10:00:00] SUCCESS: now on next-20260828", true)]
    [InlineData("[10:00:00] GridTracker upgraded cleanly", true)]
    [InlineData("[10:00:00] No update needed.", false)]
    [InlineData("[10:00:00] Already up to date (installed 1.3.1.0, latest 1.3.1).", false)]
    [InlineData("[10:00:00] No updates found.", false)]
    [InlineData("[10:00:00] Update Check Finished (dry run).", false)]
    [InlineData("[10:00:00] New version available: 1.3.1 (installed: 1.0.0.0)", false)]
    [InlineData("[10:00:00] Checking GitHub releases for K5JSG/POTA-Activator-Park-Activations...", false)]
    public void IsRealUpdate_MatchesExpected(string line, bool expected)
    {
        Assert.Equal(expected, LogParser.IsRealUpdate(line));
    }

    [Fact]
    public void IsRealUpdate_NegationWinsEvenThoughItContainsUpdateSubstring()
    {
        // The exact bug the negation-checked-first design exists to prevent:
        // "No update needed" contains the substring "Update" and must never
        // be treated as a real update.
        Assert.False(LogParser.IsRealUpdate("[10:00:00] No update needed."));
    }

    // ------------------------------------------------------------ ParseFile

    [Fact]
    public void ParseFile_MissingFile_ReportsNotExists()
    {
        var result = LogParser.ParseFile(Path.Combine(Path.GetTempPath(), $"does-not-exist_{Guid.NewGuid():N}.log"));

        Assert.False(result.Exists);
        Assert.Equal(RunStatus.Unknown, result.LatestStatus);
    }

    [Fact]
    public void ParseFile_EmptyFile_ReportsEmpty()
    {
        var path = WriteTempLog("");
        try
        {
            var result = LogParser.ParseFile(path);

            Assert.True(result.Exists);
            Assert.Equal(RunStatus.Empty, result.LatestStatus);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseFile_SuccessfulRunWithNoUpdate_ReportsSuccessAndNoUpdateTime()
    {
        var content = Log(
            "========================================",
            "POTA ACTIVATOR UPDATER 2026-08-30 14:42:07",
            "========================================",
            "[14:42:07] Checking GitHub releases for K5JSG/POTA-Activator-Park-Activations...",
            "[14:42:09] Already up to date (installed 1.3.1.0, latest 1.3.1).",
            "[14:42:09] POTA Updater completed successfully");
        var path = WriteTempLog(content);
        try
        {
            var result = LogParser.ParseFile(path);

            Assert.Equal(RunStatus.Success, result.LatestStatus);
            Assert.Null(result.LastUpdate);
            Assert.Equal(new DateTime(2026, 8, 30, 14, 42, 7), result.LatestRunTime);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseFile_SuccessfulRunWithRealUpdate_RecordsUpdateTime()
    {
        var content = Log(
            "========================================",
            "POTA ACTIVATOR UPDATER 2026-08-30 14:12:36",
            "========================================",
            "[14:12:36] Checking GitHub releases for K5JSG/POTA-Activator-Park-Activations...",
            "[14:12:36] New version available: 1.3.1 (installed: 1.0.0.0)",
            "[14:12:37] Installing via msiexec...",
            "[14:12:38] Updated to 1.3.1.",
            "[14:12:38] POTA Updater completed successfully");
        var path = WriteTempLog(content);
        try
        {
            var result = LogParser.ParseFile(path);

            Assert.Equal(RunStatus.Success, result.LatestStatus);
            Assert.Equal(new DateTime(2026, 8, 30, 14, 12, 38), result.LastUpdate);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseFile_FailedRun_ReportsFailedWithErrorMessage()
    {
        var content = Log(
            "========================================",
            "TQSL UPDATER 2026-08-30 09:00:00",
            "========================================",
            "[09:00:00] Checking https://www.arrl.org/tqsl-download for the latest version...",
            "[09:00:01] TQSL Updater FAILED: could not reach the download page (timeout)");
        var path = WriteTempLog(content);
        try
        {
            var result = LogParser.ParseFile(path);

            Assert.Equal(RunStatus.Failed, result.LatestStatus);
            Assert.Contains("FAILED", result.ErrorMessage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseFile_MultipleRuns_LatestRunTimeWinsButUpdateIsSurfacedFromEither()
    {
        var content = Log(
            "========================================",
            "POTA ACTIVATOR UPDATER 2026-08-30 14:04:26",
            "========================================",
            "[14:04:26] Checking GitHub releases for K5JSG/POTA-Activator-Park-Activations...",
            "[14:04:27] Updated to 1.3.0.",
            "[14:04:30] POTA Updater completed successfully",
            "",
            "========================================",
            "POTA ACTIVATOR UPDATER 2026-08-30 14:42:07",
            "========================================",
            "[14:42:07] Checking GitHub releases for K5JSG/POTA-Activator-Park-Activations...",
            "[14:42:09] Already up to date (installed 1.3.1.0, latest 1.3.1).",
            "[14:42:09] POTA Updater completed successfully");
        var path = WriteTempLog(content);
        try
        {
            var result = LogParser.ParseFile(path);

            Assert.Equal(2, result.Runs.Count);
            // The latest run is the one that reports current status/timestamp...
            Assert.Equal(new DateTime(2026, 8, 30, 14, 42, 7), result.LatestRunTime);
            // ...but NewestUpdate scans every run block, so the earlier
            // real update is still the reported "last update" even though
            // the most recent run itself found nothing new.
            Assert.Equal(new DateTime(2026, 8, 30, 14, 4, 27), result.LastUpdate);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseFile_UnclosedRecentRun_ReportsRunning()
    {
        var content = Log(
            "========================================",
            "WSJT-X UPDATER 2026-08-30 09:00:00",
            "========================================",
            "[09:00:00] Checking https://sourceforge.net/projects/wsjt/rss?path=/ for the latest final release...");
        var path = WriteTempLog(content, DateTime.Now);
        try
        {
            var result = LogParser.ParseFile(path);

            Assert.Equal(RunStatus.Running, result.LatestStatus);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseFile_UnclosedStaleRun_ReportsFailedAsStalled()
    {
        var content = Log(
            "========================================",
            "WSJT-X UPDATER 2026-08-30 09:00:00",
            "========================================",
            "[09:00:00] Checking https://sourceforge.net/projects/wsjt/rss?path=/ for the latest final release...");
        // Older than LogParser.StalledRunThreshold (15 minutes), so a run
        // that never closed is treated as dead rather than still running.
        var path = WriteTempLog(content, DateTime.Now - LogParser.StalledRunThreshold - TimeSpan.FromMinutes(1));
        try
        {
            var result = LogParser.ParseFile(path);

            Assert.Equal(RunStatus.Failed, result.LatestStatus);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseFile_RtSystemsCloser_ReportsSuccess()
    {
        var content = Log(
            "========================================",
            "RT SYSTEMS UPDATER 2026-08-30 09:00:00",
            "========================================",
            @"[09:00:00] [1/3] Checking updates for: C:\RT Systems V5\Module1 -> No updates found.",
            "[09:00:05] Global update run completed at: 2026-08-30 09:00:05");
        var path = WriteTempLog(content);
        try
        {
            var result = LogParser.ParseFile(path);

            Assert.Equal(RunStatus.Success, result.LatestStatus);
        }
        finally { File.Delete(path); }
    }
}
