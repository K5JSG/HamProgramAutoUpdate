using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// DXPeditions Tracker (https://github.com/K5JSG/DXPeditions) - another
/// K5JSG tool, same shape as POTA Activator: GitHub Releases API, one Inno
/// Setup exe asset per release, installed silently with /VERYSILENT. Unlike
/// POTA it ships as a single self-contained exe (DXPeditions.App.exe) whose
/// filename doesn't match the product name ("DXPeditions Tracker"), so
/// detection matches ExeFinder against the actual exe filename instead of
/// the product name.
/// </summary>
public sealed class DXPeditionsUpdater : UpdaterBase
{
    private const string Repository = "K5JSG/DXPeditions";
    private const string ProductName = "DXPeditions Tracker";
    private const string ExeBaseName = "DXPeditions.App";

    public DXPeditionsUpdater() : base("dxpeditions", ProductName, DetectDXPeditions)
    {
    }

    private static DetectedTarget DetectDXPeditions()
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
            ? ExeFinder.FindByProductName(loc, ExeBaseName,
                onAmbiguous: msg => Console.WriteLine($"DXPeditions Tracker detection: {msg}"))
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
            ctx.Log.Line($"DXPeditions Tracker Updater FAILED: could not reach GitHub ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        if (release is null)
        {
            ctx.Log.Line("DXPeditions Tracker Updater FAILED: no release found");
            return UpdateResult.Failed("No release found");
        }

        var latest = release.TagName.TrimStart('v', 'V');
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("DXPeditions Tracker Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            ctx.Log.Line("DXPeditions Tracker Updater FAILED: no .exe release asset found");
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
            ctx.Log.Line("DXPeditions Tracker Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        if (target.InstallPath is { } installedExe && IsRunning(installedExe))
        {
            ctx.Log.Line("DXPeditions Tracker Updater: the program is currently running - postponing this update.");
            ctx.Log.Line("DXPeditions Tracker Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"DXPeditionsUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, asset.Name);

        try
        {
            ctx.Log.Line($"Downloading {asset.BrowserDownloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, asset.BrowserDownloadUrl, downloadPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"DXPeditions Tracker Updater FAILED: download failed ({downloadError})");
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
                ctx.Log.Line($"DXPeditions Tracker Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("DXPeditions Tracker Updater completed successfully");
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
