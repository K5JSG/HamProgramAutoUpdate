using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// HamRadioVSPESwitcher (K5JSG/HamRadioVSPESwitcher) - same shape as
/// DXPeditions Tracker: GitHub Releases API, one Inno Setup exe asset per
/// release, installed silently. The repo is private, so both the release
/// lookup and the asset download are authenticated with the GitHub CLI's
/// signed-in token (`gh auth token`) for the account this runs as.
/// </summary>
public sealed class HamRadioVSPESwitcherUpdater : UpdaterBase
{
    private const string Repository = "K5JSG/HamRadioVSPESwitcher";
    private const string ProductName = "HamRadioVSPESwitcher";

    public HamRadioVSPESwitcherUpdater() : base("vspe_switcher", ProductName, DetectSwitcher)
    {
    }

    private static DetectedTarget DetectSwitcher()
    {
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(ProductName);
        if (entry is null) return DetectedTarget.NotFound;

        var installDir = entry.InstallLocation;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "K5JSG", ProductName);
            if (Directory.Exists(candidate)) installDir = candidate;
        }

        var exe = installDir is { } loc
            ? ExeFinder.FindByProductName(loc, ProductName,
                onAmbiguous: msg => Console.WriteLine($"{ProductName} detection: {msg}"))
            : null;
        var version = exe is not null ? FileVersionHelper.ReadFileVersion(exe) : entry.DisplayVersion;
        return DetectedTarget.Found(exe ?? installDir, version);
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        var token = await GetGhTokenAsync(ctx.CancellationToken);
        if (token is null)
        {
            ctx.Log.Line($"{ProductName} Updater FAILED: no GitHub login found - install the GitHub CLI and run 'gh auth login' as this user");
            return UpdateResult.Failed("No GitHub login");
        }

        ctx.Log.Line($"Checking GitHub releases for {Repository}...");
        GitHubRelease? release;
        try
        {
            release = await FetchLatestReleaseAsync(ctx.Http, token, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"{ProductName} Updater FAILED: could not reach GitHub ({ex.Message})");
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

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrEmpty(asset.ApiUrl))
        {
            ctx.Log.Line($"{ProductName} Updater FAILED: no .exe release asset found");
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

        // No running-program postpone here, unlike DXPeditions: this is an
        // always-running tray app, so it would never update. Its installer
        // stops it itself and the [Run] step re-registers its logon task;
        // it's restarted below since a silent install skips the postinstall
        // "start now" step.
        var wasRunning = target.InstallPath is { } installedExe && IsRunning(installedExe);

        var tempDir = Path.Combine(AppPaths.TempDir, $"VspeSwitcherUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, asset.Name);

        try
        {
            ctx.Log.Line($"Downloading {asset.Name} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, asset.ApiUrl, downloadPath, ctx.CancellationToken,
                configureRequest: request => AddGitHubHeaders(request, token, "application/octet-stream"));
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
                timeout: TimeSpan.FromSeconds(300));
            if (!installOk)
            {
                ctx.Log.Line($"{ProductName} Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            if (wasRunning) RestartViaLogonTask(ctx);

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line($"{ProductName} Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    private static bool IsRunning(string exePath) => ProcessFinder.FindByExePath(exePath).Length > 0;

    /// <summary>Starts it again the same way it starts at logon (its own
    /// scheduled task), so it runs in the signed-in user's session with the
    /// task's settings rather than as a child of this process.</summary>
    private static void RestartViaLogonTask(UpdaterContext ctx)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/Run");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(@"\K5JSG\HamRadioVSPESwitcher\HamRadioVSPESwitcher");
            using var process = Process.Start(psi);
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

    /// <summary>The GitHub CLI's token for its signed-in account, or null if
    /// gh isn't installed or isn't signed in for the user this runs as.</summary>
    private static async Task<string?> GetGhTokenAsync(CancellationToken ct)
    {
        var ghPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
        if (!File.Exists(ghPath)) ghPath = "gh.exe"; // fall back to PATH

        try
        {
            var psi = new ProcessStartInfo(ghPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");

            using var process = Process.Start(psi);
            if (process is null) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                throw;
            }

            var token = (await stdout).Trim();
            return process.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddGitHubHeaders(HttpRequestMessage request, string token, string accept)
    {
        request.Headers.UserAgent.ParseAdd("HamProgramAutoUpdate");
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<GitHubRelease?> FetchLatestReleaseAsync(HttpClient http, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        AddGitHubHeaders(request, token, "application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<GitHubRelease>(json);
    }
}
