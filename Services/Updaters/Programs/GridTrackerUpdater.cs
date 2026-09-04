using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from Gridtracker_Update_Script.py. Detects the installed copy via
/// the registry (rather than a fixed path - GridTracker's installer picks
/// its own location), scrapes the downloads page for the newest installer
/// link, and runs it with every common silent flag at once since the
/// original script didn't pin down exactly which installer engine it is.
///
/// Note: the Python version compared versions by stripping every non-digit
/// character and concatenating what's left into one integer (e.g.
/// "2.260723.0" -&gt; 22607230). That scheme can collide across different
/// versions (2.2.10 and 2.21.0 both become "2210") and nothing persisted
/// depends on it - update_history.json only ever stores dates, never GridTracker's
/// version string - so this port uses a normal per-segment version compare instead.
/// </summary>
public sealed class GridTrackerUpdater : UpdaterBase
{
    private const string PageUrl = "https://gridtracker.org/index.php/downloads/gridtracker-downloads";

    private static readonly Regex LinkRegex = new(
        @"href=[""'](?<url>https?://[^""']+\.exe)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionInUrlRegex = new(
        @"GridTracker2?-(?<ver>\d+\.\d+\.\d+)-setup\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GridTrackerUpdater() : base("gridtracker", "GridTracker",
        RegistryUninstallLookup.Detector("gridtracker"))
    {
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx, closingName: "Gridtracker");

        ctx.Log.Line($"Checking {PageUrl} for the latest version...");
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, PageUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"Gridtracker Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        string? downloadUrl = null;
        string? latest = null;

        foreach (Match link in LinkRegex.Matches(html))
        {
            var url = link.Groups["url"].Value;
            if (!url.Contains("GridTracker", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("installer", StringComparison.OrdinalIgnoreCase))
                continue;

            var verMatch = VersionInUrlRegex.Match(url);
            if (!verMatch.Success) continue;

            downloadUrl = url;
            latest = verMatch.Groups["ver"].Value;
            break;
        }

        if (downloadUrl is null || latest is null)
        {
            ctx.Log.Line("Gridtracker Updater FAILED: could not find a download link on the downloads page");
            return UpdateResult.Failed("Download link not found");
        }

        var current = target.Version;
        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("Gridtracker Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"GridTrackerUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, "GridTracker-setup.exe");

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, downloadUrl, installerPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"Gridtracker Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            ctx.Log.Line("Installing silently...");
            using var suppressor = new InstallerWindowSuppressor(
                new[] { "Installing update", "Please wait", "GridTracker" });
            suppressor.Start();

            (bool ok, int exitCode) result;
            try
            {
                result = await SilentExeInstaller.RunAsync(
                    installerPath,
                    new[] { "/S", "/VERYSILENT", "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/NOICONS" },
                    ctx.CancellationToken,
                    timeout: TimeSpan.FromSeconds(300));
            }
            finally
            {
                suppressor.Stop();
            }

            if (!result.ok)
            {
                ctx.Log.Line($"Gridtracker Updater FAILED: installer exited with code {result.exitCode}");
                return UpdateResult.Failed($"Installer exit code {result.exitCode}");
            }

            // The installer drops an unwanted desktop shortcut even silently.
            await DesktopShortcutCleaner.RemoveMatchingWithDelayAsync(
                TimeSpan.FromSeconds(2), "GridTracker", "GridTracker2");

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("Gridtracker Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }
}
