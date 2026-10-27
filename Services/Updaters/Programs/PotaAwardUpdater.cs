using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// POTA Award Auto Updater (K5JSG/POTA-Award-Auto-Updater) - a private repo,
/// same shape as QSL Card Submissions: GitHub Releases API with a read-only
/// token (PrivateGitHubRepo), one Inno Setup "-setup.exe" asset per release
/// ("PotaAwardAutoUpdater-1.1.0-setup.exe"), installed silently. Its exe is
/// self-contained, so no runtime is needed.
///
/// The program does its own daily work through its own scheduled task; this
/// card only keeps the program itself current. Its installer re-creates that
/// task on every install, keeping the user's chosen run time. The update is
/// postponed while its settings window is open.
///
/// Token: its own pota_award_config.json if present, otherwise the Claude
/// Repo Hub, DXCC Charts, QSL or VSPE switcher token file.
/// </summary>
public sealed class PotaAwardUpdater : UpdaterBase
{
    private const string Repository = "K5JSG/POTA-Award-Auto-Updater";
    private const string ProductName = "POTA Award Auto Updater";
    private const string ExeName = "PotaAwardAutoUpdater.exe";

    private static string OwnTokenFilePath => PrivateGitHubRepo.TokenFilePath("pota_award_config.json");

    public PotaAwardUpdater() : base("pota_award", ProductName, DetectPotaAward)
    {
    }

    private static DetectedTarget DetectPotaAward()
    {
        // Exact name: other K5JSG programs also start with "POTA".
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(ProductName);
        if (entry is null || !string.Equals(entry.DisplayName, ProductName, StringComparison.OrdinalIgnoreCase))
            return DetectedTarget.NotFound;

        var installDir = entry.InstallLocation;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "K5JSG", ProductName);
            if (Directory.Exists(candidate)) installDir = candidate;
        }

        var exe = installDir is { } dir && File.Exists(Path.Combine(dir, ExeName)) ? Path.Combine(dir, ExeName) : null;
        var version = !string.IsNullOrWhiteSpace(entry.DisplayVersion)
            ? entry.DisplayVersion
            : exe is not null ? FileVersionHelper.ReadFileVersion(exe) : null;
        return DetectedTarget.Found(exe ?? installDir, version);
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        var token = PrivateGitHubRepo.LoadToken(OwnTokenFilePath, ctx.Log)
                    ?? PrivateGitHubRepo.LoadToken(PrivateGitHubRepo.TokenFilePath("claude_repo_hub_config.json"), ctx.Log)
                    ?? PrivateGitHubRepo.LoadToken(PrivateGitHubRepo.TokenFilePath("dxcc_charts_config.json"), ctx.Log)
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

        // Also open while its own daily run is going; either way, wait for
        // the next night rather than have the installer replace the exe.
        if (target.InstallPath is { } installedExe && installedExe.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase)
            && ProcessFinder.FindByExePath(installedExe).Length > 0)
        {
            ctx.Log.Line($"{ProductName} Updater: the program is currently running - postponing this update.");
            ctx.Log.Line($"{ProductName} Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"PotaAwardUpdate_{Guid.NewGuid():N}");
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
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                downloadPath,
                new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromMinutes(5));
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
