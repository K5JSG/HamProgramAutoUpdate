using System.Diagnostics;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from rt_all_updater.py. Shaped differently from every other
/// updater here: RT Systems' own programs each ship a proprietary
/// RTUpdater_V5.exe (one per installed radio module), so this isn't a
/// version-check-and-download pipeline - it discovers every such exe under
/// C:\RT Systems V5, runs each with /silent, and dismisses whatever
/// confirmation dialogs still pop up despite that flag. "Success" per module
/// is inferred from whether any file under that module's folder changed,
/// since RTUpdater_V5.exe doesn't report a version itself.
/// </summary>
public sealed class RtSystemsUpdater : UpdaterBase
{
    private const string BaseDir = @"C:\RT Systems V5";
    private static readonly string[] SkipFolderNames = { "dist", "build", "update script" };

    public RtSystemsUpdater() : base("rt_systems", "RT Systems", DetectRtSystems)
    {
    }

    /// <summary>Emits the different 50-"=" / colon header LogParser.RtHeader
    /// expects, instead of the generic 40-"=" header every other program uses.</summary>
    public override UpdaterLog CreateLog(string logPath) => new RtSystemsLog(logPath);

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
            ctx.Log.Line($"Global update run completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            return UpdateResult.Skipped("No modules found");
        }

        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would run {updaters.Count} module updater(s).");
            ctx.Log.Line($"Global update run completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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

                    var afterMtime = DirectoryMaxWriteTime(folder);
                    if (afterMtime > beforeMtime)
                    {
                        ctx.Log.Line($"[{i + 1}/{updaters.Count}] Checking updates for: {folder} -> UPDATED (Files modified: {afterMtime:yyyy-MM-dd HH:mm:ss})");
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

        ctx.Log.Line($"Global update run completed at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (failedCount > 0 && updatedCount == 0)
            return UpdateResult.Failed($"{failedCount} module(s) failed to run");

        return updatedCount > 0
            ? UpdateResult.Updated(updatedCount.ToString(), $"{updatedCount} module(s) updated, {failedCount} failed")
            : UpdateResult.UpToDate($"{failedCount} failed");
    }

    private static DateTime DirectoryMaxWriteTime(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTime)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }
}
