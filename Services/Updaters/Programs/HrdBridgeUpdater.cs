using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// HRD CAT Bridge (K5JSG/HRD-CAT-Bridge) - a private repo, same shape as the
/// Claude Repo Hub card: GitHub Releases API with a read-only token
/// (PrivateGitHubRepo), one Inno Setup "-setup.exe" asset per release
/// ("HrdBridge-v1.0.0-setup.exe"), installed silently. The installed version
/// is read from the Installed Apps entry.
///
/// Token: its own hrd_bridge_config.json if present, otherwise another
/// private card's token file.
///
/// It's an always-running tray app that runs elevated through its own
/// "HRD Bridge" scheduled task, so the update is never postponed while it
/// runs: the installer closes it and this starts it again through that task
/// (in the signed-in user's session, no UAC prompt).
///
/// The installer (re)registers that task for the account running Setup, so
/// the install only runs from an elevated run as the signed-in user (the
/// dashboard, or a forced update). The nightly SYSTEM run checks and logs
/// the new version but leaves the install for the next dashboard run, rather
/// than moving the task over to SYSTEM.
/// </summary>
public sealed class HrdBridgeUpdater : UpdaterBase
{
    private const string Repository = "K5JSG/HRD-CAT-Bridge";
    private const string ProductName = "HRD CAT Bridge";
    private const string ExeName = "HrdBridge.exe";
    private const string InstallFolderName = "HRD CAT Bridge";
    private const string TaskName = "HRD Bridge";

    private static string OwnTokenFilePath => PrivateGitHubRepo.TokenFilePath("hrd_bridge_config.json");

    public HrdBridgeUpdater() : base("hrd_bridge", ProductName, DetectBridge)
    {
    }

    private static DetectedTarget DetectBridge()
    {
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(ProductName);
        if (entry is null || !string.Equals(entry.DisplayName, ProductName, StringComparison.OrdinalIgnoreCase))
            return DetectedTarget.NotFound;

        var installDir = entry.InstallLocation;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "K5JSG", InstallFolderName);
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

        if (UserSessionLauncher.IsRunningAsSystem)
        {
            ctx.Log.Line($"{latest} is available, but its installer sets up the \"{TaskName}\" task for the account running it, so it is not installed from the nightly run. Update it from the dashboard.");
            return UpdateResult.Skipped($"Update {latest} waiting for a dashboard run");
        }

        if (!AppInfo.IsElevated)
        {
            ctx.Log.Line($"{ProductName} Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        var installedExe = target.InstallPath is { } p && p.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase) ? p : null;
        var wasRunning = installedExe is not null && ProcessFinder.FindByExePath(installedExe).Length > 0;

        var tempDir = Path.Combine(AppPaths.TempDir, $"HrdBridgeUpdate_{Guid.NewGuid():N}");
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

            ctx.Log.Line(wasRunning ? "Installing silently (the running bridge will be closed and restarted)..." : "Installing silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                downloadPath,
                // /MERGETASKS only ever adds, so naming the desktop icon off
                // keeps an upgrade from touching the user's shortcuts, and
                // the "Start with Windows" choice carries over from the
                // previous install.
                new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MERGETASKS=!desktopicon" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromMinutes(5));
            if (!installOk)
            {
                ctx.Log.Line($"{ProductName} Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            if (wasRunning) RestartViaTask(ctx);

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line($"{ProductName} Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Starts the bridge again through its own scheduled task, which
    /// runs it elevated in the signed-in user's session with no UAC prompt.
    /// Failure is logged and not fatal: the Start menu shortcut or the next
    /// logon (if Start with Windows is ticked) starts it.</summary>
    private static void RestartViaTask(UpdaterContext ctx)
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
            psi.ArgumentList.Add(TaskName);
            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(15_000);
            ctx.Log.Line(process?.ExitCode == 0
                ? "Restarted the bridge via its task."
                : "Could not restart the bridge via its task - start it from the Start menu.");
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"Could not restart the bridge ({ex.Message}) - start it from the Start menu.");
        }
    }
}
