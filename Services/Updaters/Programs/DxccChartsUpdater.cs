using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// DXCC Charts (K5JSG/DxccCharts) - same shape as QSL Card Submissions: a
/// private repo, GitHub Releases API with a read-only token
/// (PrivateGitHubRepo), one Inno Setup "-setup.exe" asset per release,
/// installed silently.
///
/// Token: its own dxcc_charts_config.json if present, otherwise the QSL
/// Card Submissions or VSPE switcher token file - so a single token granted
/// access to several repos works with no extra file on each PC.
///
/// A tool you work in, so - like QSL Card Submissions and DXPeditions
/// Tracker - the update is postponed while it's open rather than letting its
/// installer close it mid-work.
/// </summary>
public sealed class DxccChartsUpdater : UpdaterBase
{
    private const string Repository = "K5JSG/DxccCharts";
    private const string ProductName = "DXCC Charts";
    private const string ExeName = "DxccCharts.exe";
    private const string InstallFolderName = "DxccCharts";

    private static string OwnTokenFilePath => PrivateGitHubRepo.TokenFilePath("dxcc_charts_config.json");

    public DxccChartsUpdater() : base("dxcc_charts", ProductName, DetectDxccCharts)
    {
    }

    private static DetectedTarget DetectDxccCharts()
    {
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(ProductName);
        if (entry is null) return DetectedTarget.NotFound;

        var installDir = entry.InstallLocation;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "K5JSG", InstallFolderName);
            if (Directory.Exists(candidate)) installDir = candidate;
        }

        // The folder and exe names have no space, unlike the product name, so
        // look for the exe directly rather than by product name.
        var exe = installDir is { } dir && File.Exists(Path.Combine(dir, ExeName)) ? Path.Combine(dir, ExeName) : null;
        var version = exe is not null ? FileVersionHelper.ReadFileVersion(exe) : entry.DisplayVersion;
        return DetectedTarget.Found(exe ?? installDir, version);
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        var token = PrivateGitHubRepo.LoadToken(OwnTokenFilePath, ctx.Log)
                    ?? PrivateGitHubRepo.LoadToken(QslSubmissionsUpdater.TokenFilePath, ctx.Log)
                    ?? PrivateGitHubRepo.LoadToken(HamRadioVSPESwitcherUpdater.TokenFilePath, ctx.Log);
        if (token is null)
        {
            ctx.Log.Line($"{ProductName} Updater FAILED: no GitHub token configured - put a read-only token for {Repository} in {OwnTokenFilePath}");
            return UpdateResult.Failed("No GitHub token configured");
        }

        ctx.Log.Line($"Checking GitHub releases for {Repository}...");
        GitHubRelease? release;
        try
        {
            release = await PrivateGitHubRepo.FetchLatestReleaseAsync(ctx.Http, Repository, token, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            // A 404 here with a working token usually means the token doesn't
            // include this repo yet.
            ctx.Log.Line($"{ProductName} Updater FAILED: could not reach the release on GitHub ({ex.Message}) - check the token has access to {Repository}");
            return UpdateResult.Failed(ex.Message);
        }

        if (release is null)
        {
            ctx.Log.Line($"{ProductName} Updater FAILED: no release found");
            return UpdateResult.Failed("No release found");
        }

        var latest = release.TagName.TrimStart('v', 'V');
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line($"{ProductName} Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrEmpty(asset.ApiUrl))
        {
            ctx.Log.Line($"{ProductName} Updater FAILED: no -setup.exe release asset found");
            return UpdateResult.Failed("No matching release asset");
        }

        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download {asset.Name} and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        if (!AppInfo.IsElevated)
        {
            ctx.Log.Line($"{ProductName} Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        if (target.InstallPath is { } installedExe && ProcessFinder.FindByExePath(installedExe).Length > 0)
        {
            ctx.Log.Line($"{ProductName} Updater: the program is currently open - postponing this update.");
            ctx.Log.Line($"{ProductName} Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"DxccChartsUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, asset.Name);

        try
        {
            ctx.Log.Line($"Downloading {asset.Name} ...");
            var (downloadOk, downloadError) = await PrivateGitHubRepo.DownloadAssetAsync(
                ctx.Http, asset.ApiUrl, token, downloadPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"{ProductName} Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            ctx.Log.Line("Installing silently...");
            // 10 minutes rather than 5: on a PC without the .NET 10 Desktop
            // Runtime the installer downloads and installs it first.
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                downloadPath,
                // !desktopicon: leave the desktop shortcut alone - see
                // CallsignLookupUpdater (rewriting it moves the icon).
                new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MERGETASKS=!desktopicon" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromMinutes(10));
            if (!installOk)
            {
                ctx.Log.Line($"{ProductName} Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line($"{ProductName} Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }
}
