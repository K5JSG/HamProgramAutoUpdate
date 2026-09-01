using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>Ported from TQSL_Updater.py: scrape arrl.org for the current
/// TQSL version, download the msi, run it silently via msiexec.</summary>
public sealed class TqslUpdater : UpdaterBase
{
    private const string PageUrl = "https://www.arrl.org/tqsl-download";

    private static readonly Regex DirectUrlRegex =
        new(@"https://www\.arrl\.org/tqsl/tqsl-(\d+\.\d+\.\d+)\.msi", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HeadingRegex =
        new(@"Download and Install TQSL\s+(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public TqslUpdater() : base("tqsl", "TQSL", TargetDetectors.FixedPaths(
        @"C:\Program Files (x86)\TrustedQSL\tqsl.exe",
        @"C:\Program Files\TrustedQSL\tqsl.exe"))
    {
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        ctx.Log.Line($"Checking {PageUrl} for the latest version...");
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, PageUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"TQSL Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        string latest;
        string downloadUrl;

        var direct = DirectUrlRegex.Match(html);
        if (direct.Success)
        {
            latest = direct.Groups[1].Value;
            downloadUrl = direct.Value;
        }
        else
        {
            var heading = HeadingRegex.Match(html);
            if (!heading.Success)
            {
                ctx.Log.Line("TQSL Updater FAILED: could not find a version number on the download page");
                return UpdateResult.Failed("Version not found on download page");
            }
            latest = heading.Groups[1].Value;
            downloadUrl = $"https://www.arrl.org/tqsl/tqsl-{latest}.msi";
        }

        var current = target.Version;
        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("TQSL Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"TqslUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var msiPath = Path.Combine(tempDir, $"tqsl-{latest}.msi");

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, downloadUrl, msiPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"TQSL Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            var installLogPath = Path.Combine(tempDir, "install.log");
            ctx.Log.Line("Installing silently via msiexec...");
            var (installOk, exitCode, message) = await MsiInstaller.InstallAsync(msiPath, installLogPath, ctx.CancellationToken);

            if (!installOk)
            {
                ctx.Log.Line($"TQSL Updater FAILED: {message} (exit code {exitCode})");
                return UpdateResult.Failed(message);
            }

            ctx.Log.Line($"Updated to {latest}. {message}");
            ctx.Log.Line("TQSL Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }
}
