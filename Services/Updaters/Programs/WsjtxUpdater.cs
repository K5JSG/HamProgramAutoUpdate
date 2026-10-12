using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from WSJTX_Update_Script.py. The project moved its releases from
/// SourceForge to GitHub (WSJTX/wsjtx) in 2026, so this reads GitHub's
/// "latest release" endpoint, which by definition skips the beta and
/// release-candidate pre-releases the project also publishes there.
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
    private const string Repository = "WSJTX/wsjtx";

    private static readonly string[] CandidatePaths =
    {
        @"C:\WSJT\wsjtx\bin\wsjtx.exe",
        @"C:\Program Files\WSJT-X\bin\wsjtx.exe",
        @"C:\Program Files\WSJT-X\wsjtx.exe",
        @"C:\Program Files (x86)\WSJT-X\wsjtx.exe",
    };

    // The installer, not the bare wsjtx.exe / jt9.exe / wsprd.exe assets
    // that are attached to the same release.
    private static readonly Regex Win64InstallerRegex = new(
        @"^wsjtx-.*win64\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Win32InstallerRegex = new(
        @"^wsjtx-.*win32\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        ctx.Log.Line($"Checking GitHub releases for {Repository}...");
        GitHubRelease? release;
        try
        {
            release = await FetchLatestReleaseAsync(ctx.Http, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"WSJT-X Updater FAILED: could not reach GitHub ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            ctx.Log.Line("WSJT-X Updater FAILED: no final release found");
            return UpdateResult.Failed("No final release found");
        }

        var latest = release.TagName.TrimStart('v', 'V');
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

        var asset = release.Assets.FirstOrDefault(a => Win64InstallerRegex.IsMatch(a.Name))
                    ?? release.Assets.FirstOrDefault(a => Win32InstallerRegex.IsMatch(a.Name));
        if (asset is null)
        {
            ctx.Log.Line($"WSJT-X Updater FAILED: no Windows installer found among the files for {latest}");
            return UpdateResult.Failed("Windows installer not found in release files");
        }

        var downloadUrl = asset.BrowserDownloadUrl;
        var installDir = InstallPathHelper.InstallDirFor(target.InstallPath!);

        var tempDir = Path.Combine(AppPaths.TempDir, $"WsjtxUpdate_{Guid.NewGuid():N}");
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
                // quoted, which matters here since installDir can be
                // "C:\Program Files\WSJT-X" - SilentExeInstaller.RunAsync
                // special-cases a trailing /D= to keep it unquoted; do not
                // reorder these.
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

    private static async Task<GitHubRelease?> FetchLatestReleaseAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("HamProgramAutoUpdate");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<GitHubRelease>(json);
    }
}
