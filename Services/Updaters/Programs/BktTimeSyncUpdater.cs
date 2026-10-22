using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from BktTimeSync_Updater.py. maniaradio.it redesigned the page
/// (2026-10): bkttimesync.html no longer prints a version number or links.
/// The "All Versions" page (en/all-versions.html) shows the vendor's file
/// list, elenco.php, which names every zip ("BktTimeSync_1.21.0.zip"), so
/// the newest version is read from that list and downloaded by name. The zip
/// holds one NSIS installer exe (no version resource), run silently with /S.
/// </summary>
public sealed class BktTimeSyncUpdater : UpdaterBase
{
    private const string ListUrl = "https://www.maniaradio.it/OldVersion/elenco.php";

    private static readonly Regex VersionRegex =
        new(@"BktTimeSync%2FBktTimeSync_(\d+\.\d+\.\d+)\.zip", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BktTimeSyncUpdater() : base("bkttimesync", "BktTimeSync", TargetDetectors.FixedPaths(
        @"C:\Program Files (x86)\BktTimeSync\BktTimeSync.exe",
        @"C:\Program Files\BktTimeSync\BktTimeSync.exe"))
    {
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        ctx.Log.Line($"Checking {ListUrl} for the latest version...");
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, ListUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"BktTimeSync Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        // Every BktTimeSync zip is listed; the newest is the highest version.
        var versions = VersionRegex.Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (versions.Count == 0)
        {
            ctx.Log.Line("BktTimeSync Updater FAILED: could not find a version number on the download page (the site may have changed again)");
            return UpdateResult.Failed("Version not found on download page");
        }

        var latest = versions.Aggregate((a, b) => FileVersionHelper.IsNewer(b, a) ? b : a);
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

        var downloadUrl = $"{ListUrl}?nomefile=BktTimeSync%2FBktTimeSync_{latest}.zip";
        var tempDir = Path.Combine(AppPaths.TempDir, $"BktTimeSyncUpdate_{Guid.NewGuid():N}");
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

            // The zip holds one "BktTimeSync_x.y.z.exe" installer. It carries no
            // version/product resource any more, so pick it by file name.
            var installerExe = Directory.EnumerateFiles(tempDir, "BktTimeSync*.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (installerExe is null)
            {
                ctx.Log.Line("BktTimeSync Updater FAILED: no installer exe found in the downloaded zip");
                return UpdateResult.Failed("Installer not found in zip");
            }

            // A background time-sync program that is normally always running, so
            // (like the VSPE tray app) there is no "postpone while it runs" - it
            // would never update. The installer closes it, so note whether it was
            // running and start it again afterwards.
            var wasRunning = target.InstallPath is { } installedExe && ProcessFinder.FindByExePath(installedExe).Length > 0;

            // NSIS installer (same as the installed copy's uninst.exe): /S, not
            // Inno's /VERYSILENT, which NSIS ignores.
            ctx.Log.Line("Installing silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                installerExe,
                new[] { "/S" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromSeconds(300));

            if (!installOk)
            {
                ctx.Log.Line($"BktTimeSync Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            if (wasRunning) RestartViaLogonTask(ctx);

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("BktTimeSync Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Starts it again the way it starts at logon - its own
    /// "BktTimeSyncStart" scheduled task - so it runs in the signed-in user's
    /// session rather than as a child of this (possibly SYSTEM) process.</summary>
    private static void RestartViaLogonTask(UpdaterContext ctx)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/Run");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add("BktTimeSyncStart");
            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(15_000);
            ctx.Log.Line(process?.ExitCode == 0
                ? "Restarted it via its logon task."
                : "Could not restart it via its logon task - it will start at next logon.");
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"Could not restart it ({ex.Message}) - it will start at next logon.");
        }
    }
}
