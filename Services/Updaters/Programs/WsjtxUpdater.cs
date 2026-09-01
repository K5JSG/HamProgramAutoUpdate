using System.Net;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from WSJTX_Update_Script.py. Uses SourceForge's RSS feed rather
/// than the project's HTML page - the HTML front-end 403s scripted requests,
/// RSS doesn't. Release-candidate folders (-rcN) are excluded before picking
/// the newest final release.
///
/// The original script hardcoded C:\WSJT\wsjtx as the install location
/// (confirmed against one specific machine's layout); this port also checks
/// the default Program Files\WSJT-X location so it isn't tied to that one
/// custom layout.
///
/// C:\WSJT\wsjtx\bin\wsjtx.exe - the first candidate path checked - is also
/// WSJT-X Improved's own default install location and exe filename (see
/// WsjtxImprovedUpdater). Confirmed live: installing that fork with no
/// explicit /D= override silently overwrote a real WSJT-X install sitting
/// at that exact path. Its exe's own ProductName resource reliably says
/// "WSJT-X-improved" though (real WSJT-X's just says "WSJT-X"), so
/// DetectRealWsjtx skips any candidate that isn't the real thing rather
/// than risk this updater "updating" WSJT-X Improved with a genuine WSJT-X
/// build, or vice versa - a plain TargetDetectors.FixedPaths can't tell.
/// </summary>
public sealed class WsjtxUpdater : UpdaterBase
{
    private const string ReleasesRssUrl = "https://sourceforge.net/projects/wsjt/rss?path=/";

    private static readonly string[] CandidatePaths =
    {
        @"C:\WSJT\wsjtx\bin\wsjtx.exe",
        @"C:\Program Files\WSJT-X\bin\wsjtx.exe",
        @"C:\Program Files\WSJT-X\wsjtx.exe",
        @"C:\Program Files (x86)\WSJT-X\wsjtx.exe",
    };

    private static readonly Regex ReleaseFolderRegex = new(
        @"/wsjtx-(?<ver>\d+\.\d+\.\d+)(?<rc>-rc\d+)?/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public WsjtxUpdater() : base("wsjtx", "WSJT-X", DetectRealWsjtx)
    {
    }

    private static DetectedTarget DetectRealWsjtx()
    {
        foreach (var path in CandidatePaths)
        {
            if (!File.Exists(path)) continue;

            var productName = FileVersionHelper.ReadProductName(path);
            if (productName is not null && productName.Contains("improved", StringComparison.OrdinalIgnoreCase))
                continue;

            return DetectedTarget.Found(path, FileVersionHelper.ReadFileVersion(path));
        }
        return DetectedTarget.NotFound;
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        ctx.Log.Line($"Checking {ReleasesRssUrl} for the latest final release...");
        string rss;
        try
        {
            rss = await HttpDownloader.GetStringAsync(ctx.Http, ReleasesRssUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"WSJT-X Updater FAILED: could not reach the release feed ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var finals = ReleaseFolderRegex.Matches(rss)
            .Where(m => !m.Groups["rc"].Success)
            .Select(m => m.Groups["ver"].Value)
            .Distinct()
            .ToList();

        if (finals.Count == 0)
        {
            ctx.Log.Line("WSJT-X Updater FAILED: no final release found in the release feed");
            return UpdateResult.Failed("No final release found");
        }

        var latest = finals.Aggregate((a, b) => FileVersionHelper.IsNewer(b, a) ? b : a);
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("WSJT-X Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        var filesRssUrl = $"https://sourceforge.net/projects/wsjt/rss?path=/wsjtx-{latest}";
        string filesRss;
        try
        {
            filesRss = await HttpDownloader.GetStringAsync(ctx.Http, filesRssUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"WSJT-X Updater FAILED: could not list files for {latest} ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var linkMatch = Regex.Match(filesRss, @"<link>(?<url>[^<]*wsjtx-[^<]*win64\.exe[^<]*)</link>", RegexOptions.IgnoreCase);
        if (!linkMatch.Success)
            linkMatch = Regex.Match(filesRss, @"<link>(?<url>[^<]*wsjtx-[^<]*win32\.exe[^<]*)</link>", RegexOptions.IgnoreCase);

        if (!linkMatch.Success)
        {
            ctx.Log.Line($"WSJT-X Updater FAILED: no Windows installer found among the files for {latest}");
            return UpdateResult.Failed("Windows installer not found in release files");
        }

        var downloadUrl = WebUtility.HtmlDecode(linkMatch.Groups["url"].Value.Trim());
        var installDir = InstallPathHelper.InstallDirFor(target.InstallPath!);

        var tempDir = Path.Combine(Path.GetTempPath(), $"WsjtxUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, $"wsjtx-{latest}-setup.exe");

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, downloadUrl, installerPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"WSJT-X Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            ctx.Log.Line("Installing silently...");
            using var suppressor = new InstallerWindowSuppressor(
                new[] { "Setup", "Please wait", "WSJT-X", "Installing", "Wizard" });
            suppressor.Start();

            (bool ok, int exitCode) result;
            try
            {
                // NSIS's /D= must be the last argument and must not be
                // quoted - ArgumentList won't quote it since it has no
                // spaces, but do not reorder these.
                result = await SilentExeInstaller.RunAsync(
                    installerPath,
                    new[] { "/S", $"/D={installDir}" },
                    ctx.CancellationToken,
                    timeout: TimeSpan.FromSeconds(300));
            }
            finally
            {
                suppressor.Stop();
            }

            if (!result.ok)
            {
                ctx.Log.Line($"WSJT-X Updater FAILED: installer exited with code {result.exitCode}");
                return UpdateResult.Failed($"Installer exit code {result.exitCode}");
            }

            await DesktopShortcutCleaner.RemoveMatchingWithDelayAsync(
                TimeSpan.FromSeconds(2), "WSJT-X", "WSJTX");

            ctx.Log.Line($"Upgrade applied successfully to version {latest}.");
            ctx.Log.Line("WSJT-X Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

}
