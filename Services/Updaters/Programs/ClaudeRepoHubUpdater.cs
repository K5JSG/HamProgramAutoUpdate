using System.Diagnostics;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Claude Repo Hub (K5JSG/Claude-Repo-Hub) - a private repo, same shape as
/// DXCC Charts: GitHub Releases API with a read-only token
/// (PrivateGitHubRepo), one Inno Setup "-setup.exe" asset per release
/// ("ClaudeRepoHub-v1.0.0-setup.exe"), installed silently. The installer's
/// own version is its AppVersion, so the installed version is read from the
/// Installed Apps entry.
///
/// Token: its own claude_repo_hub_config.json if present, otherwise the DXCC
/// Charts, QSL or VSPE switcher token file.
///
/// It's an always-running tray app, so - like the VSPE tray app - the update
/// is never postponed while it runs: the installer closes it, and because a
/// silent install skips the installer's own "start now" step, this restarts
/// it afterwards in the signed-in user's session.
/// </summary>
public sealed class ClaudeRepoHubUpdater : UpdaterBase
{
    private const string Repository = "K5JSG/Claude-Repo-Hub";
    private const string ProductName = "Claude Repo Hub";
    private const string ExeName = "HubTray.exe";
    private const string InstallFolderName = "Claude Repo Hub";

    private static string OwnTokenFilePath => PrivateGitHubRepo.TokenFilePath("claude_repo_hub_config.json");

    public ClaudeRepoHubUpdater() : base("claude_repo_hub", ProductName, DetectHub)
    {
    }

    private static DetectedTarget DetectHub()
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

        var installedExe = target.InstallPath is { } p && p.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase) ? p : null;
        var wasRunning = installedExe is not null && ProcessFinder.FindByExePath(installedExe).Length > 0;

        var tempDir = Path.Combine(AppPaths.TempDir, $"ClaudeRepoHubUpdate_{Guid.NewGuid():N}");
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

            ctx.Log.Line(wasRunning ? "Installing silently (the running tray app will be closed and restarted)..." : "Installing silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                downloadPath,
                // !desktopicon / !startmenuicon are unchecked-by-default tasks
                // anyway - /MERGETASKS only ever adds, so naming them off
                // keeps an upgrade from touching the user's shortcuts.
                new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MERGETASKS=!desktopicon" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromMinutes(5));
            if (!installOk)
            {
                ctx.Log.Line($"{ProductName} Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            if (wasRunning && installedExe is not null) RestartTray(ctx, installedExe);

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line($"{ProductName} Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Starts the tray app again as the signed-in user, never
    /// elevated. From the nightly SYSTEM run it goes through
    /// <see cref="UserSessionLauncher"/> (its job object has no kill-on-close,
    /// so letting go of it leaves the tray running); from a dashboard run, the
    /// elevated copy of this app hands the start to Explorer, which runs it as
    /// the normal user. Failure is logged and not fatal: it starts again at
    /// next logon if "Start with Windows" is ticked.</summary>
    private static void RestartTray(UpdaterContext ctx, string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath) ?? "";
            if (UserSessionLauncher.IsRunningAsSystem)
            {
                var launched = UserSessionLauncher.Start(exePath, Array.Empty<string>(), dir, _ => { });
                if (launched is null)
                {
                    ctx.Log.Line("Nobody is signed in, so the tray app was not restarted.");
                    return;
                }
                ctx.Log.Line($"Restarted the tray app as {launched.UserName}.");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exePath}\"") { UseShellExecute = false })?.Dispose();
            ctx.Log.Line("Restarted the tray app.");
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"Could not restart the tray app ({ex.Message}) - it will start again at next logon if 'Start with Windows' is ticked.");
        }
    }
}
