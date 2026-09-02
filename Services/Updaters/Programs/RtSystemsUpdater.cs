using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from rt_all_updater.py. Shaped differently from every other
/// updater here: RT Systems' own programs each ship a proprietary
/// RTUpdater_V5.exe (one per installed radio module), so this isn't a
/// version-check-and-download pipeline - it discovers every such exe under
/// C:\RT Systems V5, runs each with /silent, and dismisses whatever
/// confirmation dialogs still pop up despite that flag. "Success" per module
/// is inferred from RTUpdater_V5.exe's own UpdateLog.Txt (see
/// WasUpdatedPerLog), since it doesn't report a version itself.
/// </summary>
public sealed class RtSystemsUpdater : UpdaterBase
{
    private const string BaseDir = @"C:\RT Systems V5";
    private static readonly string[] SkipFolderNames = { "dist", "build", "update script" };

    private static readonly Regex CompareVersionRegex = new(
        @"Compare version (?<a>[\d.]+)\s+and\s+(?<b>[\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RtSystemsUpdater() : base("rt_systems", "RT Systems", DetectRtSystems)
    {
    }

    /// <summary>
    /// Deliberately deviates from IProgramUpdater.DetectTarget's documented
    /// "cheap enough for every dashboard refresh - registry reads and
    /// File.Exists checks only" contract: RT Systems has no registry
    /// uninstall entries at all (see the class doc comment), so a real
    /// filesystem walk is the only way to detect it. Also re-walked
    /// independently by RunAsync below rather than reused, since DetectTarget
    /// and RunAsync are called at genuinely different times (this is a
    /// stateless static detector, not an instance with something to cache
    /// between them). Accepted as-is: RT Systems module folders are
    /// typically few and shallow in practice, so the real-world cost of the
    /// extra walk(s) is low.
    /// </summary>
    private static DetectedTarget DetectRtSystems() =>
        Directory.Exists(BaseDir) && FindUpdaters().Any()
            ? DetectedTarget.Found(BaseDir)
            : DetectedTarget.NotFound;

    private static IEnumerable<string> FindUpdaters()
    {
        if (!Directory.Exists(BaseDir)) yield break;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(BaseDir, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (SkipFolderNames.Any(skip => string.Equals(name, skip, StringComparison.OrdinalIgnoreCase)))
                continue;

            var updater = Path.Combine(dir, "RTUpdater_V5.exe");
            if (File.Exists(updater)) yield return updater;
        }
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        // Exclude an updater sitting directly in the base folder - that one
        // is the launcher, not a per-module updater.
        var updaters = FindUpdaters()
            .Where(u => !string.Equals(Path.GetDirectoryName(u), BaseDir, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (updaters.Count == 0)
        {
            ctx.Log.Line("No RT Systems module updaters found - skipping.");
            ctx.Log.Line("RT Systems Updater completed successfully");
            return UpdateResult.Skipped("No modules found");
        }

        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would run {updaters.Count} module updater(s).");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        using var suppressor = new InstallerWindowSuppressor(
            new[] { "Installed Programmers", "RT Updater", "RTUpdater" },
            buttonLabelSubstrings: new[] { "OK", "Update" });
        suppressor.Start();

        var updatedCount = 0;
        var failedCount = 0;

        try
        {
            for (var i = 0; i < updaters.Count; i++)
            {
                var updaterPath = updaters[i];
                var folder = Path.GetDirectoryName(updaterPath)!;
                var beforeMtime = DirectoryMaxWriteTime(folder);

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = updaterPath,
                        WorkingDirectory = folder,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("/silent");

                    using var proc = Process.Start(psi);
                    if (proc is not null)
                        await proc.WaitForExitAsync(ctx.CancellationToken);

                    var updated = WasUpdatedPerLog(folder) ?? DirectoryMaxWriteTime(folder) > beforeMtime;
                    if (updated)
                    {
                        ctx.Log.Line($"[{i + 1}/{updaters.Count}] Checking updates for: {folder} -> UPDATED (Files modified: {DirectoryMaxWriteTime(folder):yyyy-MM-dd HH:mm:ss})");
                        updatedCount++;
                    }
                    else
                    {
                        ctx.Log.Line($"[{i + 1}/{updaters.Count}] Checking updates for: {folder} -> No updates found.");
                    }
                }
                catch (Exception ex)
                {
                    ctx.Log.Line($"[{i + 1}/{updaters.Count}] Checking updates for: {folder} -> FAILED TO RUN (Error: {ex.Message})");
                    failedCount++;
                }
            }
        }
        finally
        {
            suppressor.Stop();
        }

        if (failedCount > 0 && updatedCount == 0)
        {
            ctx.Log.Line($"RT Systems Updater FAILED: {failedCount} module(s) failed to run");
            return UpdateResult.Failed($"{failedCount} module(s) failed to run");
        }

        ctx.Log.Line("RT Systems Updater completed successfully");
        // NewVersion is left null here - RT Systems' per-module updaters don't
        // report a version at all (see the class doc comment), so there is no
        // real version string to put in that field. The module count belongs
        // in Message only.
        return updatedCount > 0
            ? UpdateResult.Updated(null, $"{updatedCount} module(s) updated, {failedCount} failed")
            : UpdateResult.UpToDate($"{failedCount} failed");
    }

    /// <summary>Excludes UpdateLog.Txt - see WasUpdatedPerLog for why: RTUpdater_V5.exe
    /// rewrites it on every run regardless of outcome, so including it here
    /// made every run look like an update even when nothing changed.</summary>
    private static DateTime DirectoryMaxWriteTime(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "UpdateLog.Txt", StringComparison.OrdinalIgnoreCase))
                .Select(File.GetLastWriteTime)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// RTUpdater_V5.exe writes its own UpdateLog.Txt into the module's
    /// folder on every run, regardless of whether anything was actually
    /// updated - confirmed directly against a real run: all three modules
    /// showed as "updated" purely because that log file was rewritten, while
    /// the log's own content showed every "Compare version A and B" line as
    /// A == B (nothing really changed). That content is a reliable signal
    /// though, so use it instead: a genuine version difference on any
    /// comparison line means that component really was updated. Numeric
    /// comparison, not string equality - RTUpdater pads inconsistently
    /// ("5.00.1.0" vs "5.0.1.0" for the same version).
    /// Returns null (not "no update") if there is no log to read or it has
    /// no comparison lines, so the caller can fall back to the mtime check
    /// for whatever module shape produced that.
    /// </summary>
    private static bool? WasUpdatedPerLog(string folder)
    {
        var logPath = Path.Combine(folder, "UpdateLog.Txt");
        if (!File.Exists(logPath)) return null;

        string content;
        try { content = File.ReadAllText(logPath, Encoding.Unicode); }
        catch (Exception) { return null; }

        var matches = CompareVersionRegex.Matches(content);
        if (matches.Count == 0) return null;

        foreach (Match m in matches)
        {
            var a = m.Groups["a"].Value;
            var b = m.Groups["b"].Value;
            if (FileVersionHelper.IsNewer(a, b) || FileVersionHelper.IsNewer(b, a))
                return true;
        }
        return false;
    }
}
