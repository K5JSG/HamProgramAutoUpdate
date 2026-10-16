using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// EchoLink (https://echolink.org/download.htm). There is no version feed:
/// the download page itself lists the current "EchoLinkSetup-x-y-zzz.exe"
/// link and a "Version x.y.zzz" line, so this scrapes both. The installer is
/// a WiX Burn bundle (it unpacks to C:\ProgramData\Package Cache and registers
/// both a bundle and an MSI entry named "EchoLink"), installed silently with
/// /quiet /norestart; Burn itself handles replacing the older version. The
/// vendor says installing over a newer version isn't possible, which the
/// IsNewer check already guarantees we never attempt.
/// </summary>
public sealed class EchoLinkUpdater : UpdaterBase
{
    private const string PageUrl = "https://echolink.org/download.htm";
    private const string ProductName = "EchoLink";

    // The link must be an EchoLinkSetup exe on the vendor's own CDN or site,
    // so a changed/hijacked page can't point the updater at an arbitrary file.
    private static readonly Regex DownloadHrefRegex = new(
        @"href=[""'](?<url>https://(?:d34yfhzor7jtt3\.cloudfront\.net|(?:www\.)?echolink\.org)/[^""']*EchoLinkSetup[-_][\d_-]+\.exe)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionRegex = new(
        @"Version\s+(?<v>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public EchoLinkUpdater() : base("echolink", ProductName, DetectEchoLink)
    {
    }

    private static DetectedTarget DetectEchoLink()
    {
        // Two entries share the DisplayName "EchoLink" (the Burn bundle and
        // its MSI); either gives the same version.
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(ProductName);
        if (entry is null || !string.Equals(entry.DisplayName, ProductName, StringComparison.OrdinalIgnoreCase))
            return DetectedTarget.NotFound;

        string? exe = null;
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        })
        {
            var candidate = Path.Combine(root, "K1RFD", "EchoLink", "EchoLink.exe");
            if (File.Exists(candidate)) { exe = candidate; break; }
        }

        var version = entry.DisplayVersion ?? (exe is not null ? FileVersionHelper.ReadFileVersion(exe) : null);
        return DetectedTarget.Found(exe, version);
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
            ctx.Log.Line($"EchoLink Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var hrefMatch = DownloadHrefRegex.Match(html);
        var versionMatch = VersionRegex.Match(html);
        if (!hrefMatch.Success || !versionMatch.Success)
        {
            ctx.Log.Line("EchoLink Updater FAILED: could not find the current version and download link on the download page");
            return UpdateResult.Failed("Version or download link not found");
        }

        var downloadUrl = hrefMatch.Groups["url"].Value;
        var latest = versionMatch.Groups["v"].Value;
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("EchoLink Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");

        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download {downloadUrl} and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        if (!AppInfo.IsElevated)
        {
            ctx.Log.Line("EchoLink Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        // EchoLink may be on the air; never close it from under the user.
        if (ProcessFinder.FindByName("EchoLink").Length > 0)
        {
            ctx.Log.Line("EchoLink Updater: EchoLink is currently running - postponing this update.");
            ctx.Log.Line("EchoLink Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"EchoLinkUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, Path.GetFileName(new Uri(downloadUrl).LocalPath));

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, downloadUrl, downloadPath, ctx.CancellationToken);
            if (!downloadOk || !HttpDownloader.LooksLikeExe(downloadPath))
            {
                ctx.Log.Line($"EchoLink Updater FAILED: download failed ({downloadError ?? "not a real installer"})");
                return UpdateResult.Failed(downloadError ?? "Download is not a real installer");
            }

            ctx.Log.Line("Installing silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                downloadPath, new[] { "/quiet", "/norestart" }, ctx.CancellationToken,
                timeout: TimeSpan.FromSeconds(300));
            if (!installOk)
            {
                ctx.Log.Line($"EchoLink Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("EchoLink Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }
}
