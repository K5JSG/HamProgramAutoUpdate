using System.Net.Http;
using System.Text.Json;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Voice Keyer (https://github.com/K5JSG/VoiceKeyer) - another K5JSG tool,
/// same shape as Callsign Lookup: GitHub Releases API (tags like "V1.0.0"),
/// one Inno Setup exe asset per release ("VoiceKeyer-v1.0.0-setup.exe", ~190
/// MB because it bundles the stock voice), installed silently with
/// /VERYSILENT. Detection insists on an exact Add/Remove Programs DisplayName
/// match ("Voice Keyer") and looks for VoiceKeyer.exe in its install folder.
/// </summary>
public sealed class VoiceKeyerUpdater : UpdaterBase
{
    private const string Repository = "K5JSG/VoiceKeyer";
    private const string ProductName = "Voice Keyer";
    private const string ExeName = "VoiceKeyer.exe";

    public VoiceKeyerUpdater() : base("voice_keyer", ProductName, DetectVoiceKeyer)
    {
    }

    private static DetectedTarget DetectVoiceKeyer()
    {
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(ProductName);
        if (entry is null || !string.Equals(entry.DisplayName, ProductName, StringComparison.OrdinalIgnoreCase))
            return DetectedTarget.NotFound;

        var installDir = entry.InstallLocation;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "K5JSG", "VoiceKeyer");
            if (Directory.Exists(candidate)) installDir = candidate;
        }

        var exe = installDir is { } loc ? Path.Combine(loc, ExeName) : null;
        if (exe is not null && !File.Exists(exe)) exe = null;

        var version = exe is not null ? FileVersionHelper.ReadFileVersion(exe) : null;
        return DetectedTarget.Found(exe ?? installDir, version ?? entry.DisplayVersion);
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
            ctx.Log.Line($"Voice Keyer Updater FAILED: could not reach GitHub ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        if (release is null)
        {
            ctx.Log.Line("Voice Keyer Updater FAILED: no release found");
            return UpdateResult.Failed("No release found");
        }

        var latest = release.TagName.TrimStart('v', 'V');
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("Voice Keyer Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            ctx.Log.Line("Voice Keyer Updater FAILED: no .exe release asset found");
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
            ctx.Log.Line("Voice Keyer Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        // Its installer would close a running copy mid-use - wait for the
        // next run instead, same as Callsign Lookup.
        if (target.InstallPath is { } installedPath && IsRunning(installedPath))
        {
            ctx.Log.Line("Voice Keyer Updater: the program is currently running - postponing this update.");
            ctx.Log.Line("Voice Keyer Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"VoiceKeyerUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, asset.Name);

        try
        {
            ctx.Log.Line($"Downloading {asset.BrowserDownloadUrl} ...");
            // ~190 MB, so allow far longer than the default 5-minute stall limit.
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, asset.BrowserDownloadUrl, downloadPath, ctx.CancellationToken,
                perAttemptTimeout: TimeSpan.FromMinutes(20));
            if (!downloadOk)
            {
                ctx.Log.Line($"Voice Keyer Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            // !desktopicon: leave the desktop shortcut alone (see CallsignLookupUpdater).
            ctx.Log.Line("Installing silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                downloadPath,
                new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MERGETASKS=!desktopicon" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromSeconds(600));
            if (!installOk)
            {
                ctx.Log.Line($"Voice Keyer Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("Voice Keyer Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    private static bool IsRunning(string exePath) =>
        exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && ProcessFinder.FindByExePath(exePath).Length > 0;

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
