using System.IO.Compression;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from BktTimeSync_Updater.py. Simplest of the ten: scrape a version
/// number off a static page, download a zip, run the Inno Setup installer
/// inside it silently.
/// </summary>
public sealed class BktTimeSyncUpdater : UpdaterBase
{
    private const string PageUrl = "https://www.maniaradio.it/en/bkttimesync.html";

    private static readonly Regex VersionRegex =
        new(@"BktTimeSync[_-](\d+\.\d+\.\d+)\.zip", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BktTimeSyncUpdater() : base("bkttimesync", "BktTimeSync", TargetDetectors.FixedPaths(
        @"C:\Program Files (x86)\BktTimeSync\BktTimeSync.exe",
        @"C:\Program Files\BktTimeSync\BktTimeSync.exe"))
    {
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled)
        {
            ctx.Log.Line("BktTimeSync is not installed on this PC - skipping.");
            ctx.Log.Line("BktTimeSync Updater completed successfully");
            return UpdateResult.Skipped("Not installed");
        }

        ctx.Log.Line($"Checking {PageUrl} for the latest version...");
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, PageUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"BktTimeSync Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var match = VersionRegex.Match(html);
        if (!match.Success)
        {
            ctx.Log.Line("BktTimeSync Updater FAILED: could not find a version number on the download page");
            return UpdateResult.Failed("Version not found on download page");
        }

        var latest = match.Groups[1].Value;
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("BktTimeSync Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        var downloadUrl = $"https://www.maniaradio.it/OldVersion/elenco.php?nomefile=BktTimeSync%2FBktTimeSync_{latest}.zip";
        var tempDir = Path.Combine(Path.GetTempPath(), $"BktTimeSyncUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "BktTimeSync.zip");

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, downloadUrl, zipPath, ctx.CancellationToken, minSizeBytes: 1000);
            if (!downloadOk)
            {
                ctx.Log.Line($"BktTimeSync Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

            var installerExe = Directory.EnumerateFiles(tempDir, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(p => !Path.GetFileName(p).Contains("uninst", StringComparison.OrdinalIgnoreCase));

            if (installerExe is null)
            {
                ctx.Log.Line("BktTimeSync Updater FAILED: no installer exe found in the downloaded zip");
                return UpdateResult.Failed("Installer not found in zip");
            }

            var installLogPath = Path.Combine(tempDir, "install.log");
            ctx.Log.Line("Installing silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                installerExe,
                new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", $"/LOG={installLogPath}" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromSeconds(300));

            if (!installOk)
            {
                ctx.Log.Line($"BktTimeSync Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("BktTimeSync Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }
}
