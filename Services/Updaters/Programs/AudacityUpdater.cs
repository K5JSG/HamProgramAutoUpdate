using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Audacity (https://github.com/audacity/audacity). Releases come from the
/// GitHub API, tagged "Audacity-4.0.1" (stable) or "Audacity-4.0.0-beta-4"
/// (flagged pre-release, always skipped). Audacity 3.x and 4.x are separate
/// product lines that install side by side under different Add/Remove
/// Programs names, so this only ever offers a release from the SAME major
/// version as the installed copy - a 3.x install is never "upgraded" into 4.x.
/// 4.x ships a Windows .msi (installed with msiexec); 3.x ships an Inno
/// "-64bit.exe" (installed with /VERYSILENT).
/// </summary>
public sealed class AudacityUpdater : UpdaterBase
{
    private const string Repository = "audacity/audacity";
    private const string ProductName = "Audacity";

    private static readonly Regex TagVersionRegex =
        new(@"^Audacity[- ](?<v>\d+\.\d+\.\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AudacityUpdater() : base("audacity", ProductName, DetectAudacity)
    {
    }

    private static DetectedTarget DetectAudacity()
    {
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(ProductName);
        if (entry is null || !entry.DisplayName.StartsWith(ProductName, StringComparison.OrdinalIgnoreCase))
            return DetectedTarget.NotFound;

        string? exe = null;
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            foreach (var relative in new[] { @"bin\Audacity4.exe", @"bin\Audacity.exe", "Audacity.exe" })
            {
                var candidate = Path.Combine(entry.InstallLocation, relative);
                if (File.Exists(candidate)) { exe = candidate; break; }
            }
        }

        // The registry's DisplayVersion (e.g. "4.0.1.262721353") is the
        // installer's own version and the one Windows shows; fall back to it
        // when the exe's version can't be read.
        var version = entry.DisplayVersion ?? (exe is not null ? FileVersionHelper.ReadFileVersion(exe) : null);
        return DetectedTarget.Found(exe ?? entry.InstallLocation, version);
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        var current = target.Version;
        var installedMajor = FileVersionHelper.ParseVersionParts(current)?.FirstOrDefault();

        ctx.Log.Line($"Checking GitHub releases for {Repository}...");
        List<GitHubRelease> releases;
        try
        {
            releases = await FetchReleasesAsync(ctx.Http, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"Audacity Updater FAILED: could not reach GitHub ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        // Newest stable release in the installed major version line.
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        (string version, GitHubAsset asset)? best = null;
        foreach (var release in releases)
        {
            if (release.Prerelease || release.Draft) continue;

            var match = TagVersionRegex.Match(release.TagName);
            if (!match.Success) continue;

            var version = match.Groups["v"].Value;
            if (installedMajor is { } major && FileVersionHelper.ParseVersionParts(version)?[0] != major) continue;
            if (best is { } b && !FileVersionHelper.IsNewer(version, b.version)) continue;

            var asset = PickAsset(release, arm);
            if (asset is not null) best = (version, asset);
        }

        if (best is null)
        {
            ctx.Log.Line("Audacity Updater FAILED: no matching stable release with a Windows installer found");
            return UpdateResult.Failed("No release found");
        }

        var (latest, chosen) = best.Value;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("Audacity Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");

        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download {chosen.Name} and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        if (!AppInfo.IsElevated)
        {
            ctx.Log.Line("Audacity Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        // An open project would be closed by the installer - wait for the next run.
        if (IsAudacityRunning())
        {
            ctx.Log.Line("Audacity Updater: Audacity is currently running - postponing this update.");
            ctx.Log.Line("Audacity Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"AudacityUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, chosen.Name);

        try
        {
            ctx.Log.Line($"Downloading {chosen.BrowserDownloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, chosen.BrowserDownloadUrl, downloadPath, ctx.CancellationToken,
                perAttemptTimeout: TimeSpan.FromMinutes(15));
            if (!downloadOk)
            {
                ctx.Log.Line($"Audacity Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            if (chosen.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Log.Line("Installing via msiexec...");
                var (ok, exitCode, message) = await MsiInstaller.InstallAsync(
                    downloadPath, Path.Combine(tempDir, "install.log"), ctx.CancellationToken);
                if (!ok)
                {
                    ctx.Log.Line($"Audacity Updater FAILED: {message} (exit code {exitCode})");
                    return UpdateResult.Failed(message);
                }
            }
            else
            {
                ctx.Log.Line("Installing silently...");
                var (ok, exitCode) = await SilentExeInstaller.RunAsync(
                    downloadPath,
                    new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MERGETASKS=!desktopicon" },
                    ctx.CancellationToken,
                    timeout: TimeSpan.FromSeconds(300));
                if (!ok)
                {
                    ctx.Log.Line($"Audacity Updater FAILED: installer exited with code {exitCode}");
                    return UpdateResult.Failed($"Installer exit code {exitCode}");
                }
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("Audacity Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>The Windows installer for this PC's architecture: the .msi
    /// for 4.x, the "-64bit.exe" / "-arm64.exe" Inno installer for 3.x.</summary>
    private static GitHubAsset? PickAsset(GitHubRelease release, bool arm)
    {
        var archTag = arm ? "arm64" : "x86_64";
        var msi = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("audacity-win-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith($"-{archTag}.msi", StringComparison.OrdinalIgnoreCase));
        if (msi is not null) return msi;

        var exeTag = arm ? "arm64" : "64bit";
        return release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("audacity-win-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith($"-{exeTag}.exe", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAudacityRunning() =>
        ProcessFinder.FindByName("Audacity").Length > 0 || ProcessFinder.FindByName("Audacity4").Length > 0;

    private static async Task<List<GitHubRelease>> FetchReleasesAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd("HamProgramAutoUpdate");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? new List<GitHubRelease>();
    }
}
