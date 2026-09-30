using System.Net.Http;
using System.Text.Json;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Callsign Lookup (https://github.com/K5JSG/Callsign-Lookup) - another K5JSG
/// tool, same shape as DXPeditions Tracker: GitHub Releases API (bare tags
/// like "1.0.0"), one Inno Setup exe asset per release, installed silently
/// with /VERYSILENT. Its exe ("Callsign Lookup.exe") matches the product
/// name, but "Callsign Lookup" is generic enough that another vendor's
/// product could contain it, so detection insists on an exact
/// Add/Remove Programs DisplayName match rather than trusting the substring
/// lookup alone.
/// </summary>
public sealed class CallsignLookupUpdater : UpdaterBase
{
    private const string Repository = "K5JSG/Callsign-Lookup";
    private const string ProductName = "Callsign Lookup";

    public CallsignLookupUpdater() : base("callsign_lookup", ProductName, DetectCallsignLookup)
    {
    }

    private static DetectedTarget DetectCallsignLookup()
    {
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

        var exe = installDir is { } loc
            ? ExeFinder.FindByProductName(loc, ProductName,
                onAmbiguous: msg => Console.WriteLine($"Callsign Lookup detection: {msg}"))
            : null;
        var version = exe is not null ? FileVersionHelper.ReadFileVersion(exe) : entry.DisplayVersion;
        return DetectedTarget.Found(exe ?? installDir, version);
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
            ctx.Log.Line($"Callsign Lookup Updater FAILED: could not reach GitHub ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        if (release is null)
        {
            ctx.Log.Line("Callsign Lookup Updater FAILED: no release found");
            return UpdateResult.Failed("No release found");
        }

        var latest = release.TagName.TrimStart('v', 'V');
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("Callsign Lookup Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            ctx.Log.Line("Callsign Lookup Updater FAILED: no .exe release asset found");
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
            ctx.Log.Line("Callsign Lookup Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        // Its installer would kill a running copy mid-lookup - wait for the
        // next run instead, same as DXPeditions Tracker.
        if (target.InstallPath is { } installedExe && IsRunning(installedExe))
        {
            ctx.Log.Line("Callsign Lookup Updater: the program is currently running - postponing this update.");
            ctx.Log.Line("Callsign Lookup Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"CallsignLookupUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, asset.Name);

        try
        {
            ctx.Log.Line($"Downloading {asset.BrowserDownloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, asset.BrowserDownloadUrl, downloadPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"Callsign Lookup Updater FAILED: download failed ({downloadError})");
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
                ctx.Log.Line($"Callsign Lookup Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("Callsign Lookup Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    private static bool IsRunning(string exePath) => ProcessFinder.FindByExePath(exePath).Length > 0;

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
