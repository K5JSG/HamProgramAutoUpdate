using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from pota_updater.py - the best-architected of the ten originals,
/// used here as the model for the others: GitHub Releases API instead of
/// HTML scraping, a config file for everything that varies per-deployment,
/// a 3-tier detection fallback, and a backup-and-restore-on-failure zip
/// install path that never overwrites the target's own settings/database
/// files.
/// </summary>
public sealed class PotaUpdater : UpdaterBase
{
    public PotaUpdater() : base("pota", "POTA Activator", DetectPota)
    {
    }

    private static DetectedTarget DetectPota()
    {
        var config = PotaUpdaterConfig.Load();

        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(config.ProductName);
        if (entry is null) return DetectedTarget.NotFound;

        var installDir = entry.InstallLocation;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            // The VS Installer Project (.vdproj) build of this app does not
            // populate InstallLocation in the uninstall registry key, so
            // fall back to its default target dir:
            // [ProgramFiles64Folder][Manufacturer]\[ProductName].
            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "K5JSG", config.ProductName);
            if (Directory.Exists(candidate)) installDir = candidate;
        }

        var exe = installDir is { } loc ? FindExeIn(loc) : null;
        var version = exe is not null ? FileVersionHelper.ReadFileVersion(exe) : entry.DisplayVersion;
        return DetectedTarget.Found(exe ?? installDir, version);
    }

    private static string? FindExeIn(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => !Path.GetFileName(p).Contains("uninst", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled)
        {
            ctx.Log.Line("POTA Activator is not installed on this PC - skipping.");
            ctx.Log.Line("POTA Updater completed successfully");
            return UpdateResult.Skipped("Not installed");
        }

        var config = PotaUpdaterConfig.Load();

        ctx.Log.Line($"Checking GitHub releases for {config.Repository}...");
        GitHubRelease? release;
        try
        {
            release = await FetchLatestReleaseAsync(ctx.Http, config, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"POTA Updater FAILED: could not reach GitHub ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        if (release is null)
        {
            ctx.Log.Line("POTA Updater FAILED: no release found");
            return UpdateResult.Failed("No release found");
        }

        var latest = release.TagName.TrimStart('v', 'V');
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("POTA Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");

        var assetPattern = new Regex(config.AssetPattern, RegexOptions.IgnoreCase);
        var asset = release.Assets.FirstOrDefault(a => assetPattern.IsMatch(a.Name));
        if (asset is null)
        {
            ctx.Log.Line($"POTA Updater FAILED: no release asset matched pattern '{config.AssetPattern}'");
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
            ctx.Log.Line("POTA Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        if (target.InstallPath is { } installedExe && IsRunning(installedExe))
        {
            ctx.Log.Line("POTA Updater: the program is currently running - postponing this update.");
            ctx.Log.Line("POTA Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"PotaUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, asset.Name);

        try
        {
            ctx.Log.Line($"Downloading {asset.BrowserDownloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, asset.BrowserDownloadUrl, downloadPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"POTA Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            var extension = Path.GetExtension(asset.Name).ToLowerInvariant();
            var installDir = target.InstallPath is { } p ? Path.GetDirectoryName(p)! : UpdaterCatalog.HamRadioDir;

            if (extension == ".msi")
            {
                var installLogPath = Path.Combine(tempDir, "install.log");
                ctx.Log.Line("Installing via msiexec...");
                var (installOk, exitCode, message) = await MsiInstaller.InstallAsync(downloadPath, installLogPath, ctx.CancellationToken);
                if (!installOk)
                {
                    ctx.Log.Line($"POTA Updater FAILED: {message} (exit code {exitCode})");
                    return UpdateResult.Failed(message);
                }
            }
            else if (extension == ".zip")
            {
                ctx.Log.Line("Extracting and copying into the install directory (existing settings/data preserved)...");
                InstallZip(downloadPath, installDir, config.PreserveGlobs);
            }
            else
            {
                ctx.Log.Line($"Running installer with args '{config.InstallerArgs}'...");
                var args = config.InstallerArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                    downloadPath, args, ctx.CancellationToken, timeout: TimeSpan.FromSeconds(300));
                if (!installOk)
                {
                    ctx.Log.Line($"POTA Updater FAILED: installer exited with code {exitCode}");
                    return UpdateResult.Failed($"Installer exit code {exitCode}");
                }
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("POTA Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    private static bool IsRunning(string exePath)
    {
        var processName = Path.GetFileNameWithoutExtension(exePath);
        // An empty/whitespace name means detection couldn't resolve a real
        // path - GetProcessesByName("") is not a reliable "nothing" query
        // (observed matching something unrelated on .NET 8/Windows), so
        // treat "we don't know" as "not running" rather than guessing.
        if (string.IsNullOrWhiteSpace(processName)) return false;

        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<GitHubRelease?> FetchLatestReleaseAsync(
        HttpClient http, PotaUpdaterConfig config, CancellationToken ct)
    {
        var url = config.IncludePrereleases
            ? $"https://api.github.com/repos/{config.Repository}/releases?per_page=20"
            : $"https://api.github.com/repos/{config.Repository}/releases/latest";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("HamProgramAutoUpdate");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(config.GitHubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.GitHubToken);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        if (config.IncludePrereleases)
        {
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? new List<GitHubRelease>();
            return releases.Count == 0
                ? null
                : releases.Aggregate((a, b) =>
                    FileVersionHelper.IsNewer(b.TagName.TrimStart('v', 'V'), a.TagName.TrimStart('v', 'V')) ? b : a);
        }

        return JsonSerializer.Deserialize<GitHubRelease>(json);
    }

    // -------------------------------------------------- zip install path

    private static void InstallZip(string zipPath, string installDir, string[] preserveGlobs)
    {
        var backupDir = installDir.TrimEnd('\\') + ".backup";
        var stagingDir = Path.Combine(Path.GetTempPath(), $"PotaStage_{Guid.NewGuid():N}");

        try
        {
            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            if (Directory.Exists(installDir)) CopyDirectory(installDir, backupDir);

            try
            {
                CopyDirectoryPreserving(stagingDir, installDir, preserveGlobs);
            }
            catch (Exception)
            {
                if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true);
                if (Directory.Exists(backupDir)) CopyDirectory(backupDir, installDir);
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch (Exception) { }
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch (Exception) { }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void CopyDirectoryPreserving(string source, string dest, string[] preserveGlobs)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(dest, relative);

            if (File.Exists(destFile) && MatchesAnyGlob(relative, preserveGlobs))
                continue; // keep the existing settings/data file

            File.Copy(file, destFile, overwrite: true);
        }
    }

    private static bool MatchesAnyGlob(string relativePath, string[] globs)
    {
        var name = Path.GetFileName(relativePath);
        foreach (var glob in globs)
        {
            if (glob.StartsWith('*') && name.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, glob, StringComparison.OrdinalIgnoreCase)) return true;
            if (relativePath.Contains(glob, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>Everything that varies about the POTA updater without a rebuild -
/// mirrors the original Python script's config.json shape.</summary>
public sealed class PotaUpdaterConfig
{
    public string Repository { get; set; } = "K5JSG/POTA-Activator-Park-Activations";
    public string ProductName { get; set; } = "POTA Activator Park Activations";
    public string AssetPattern { get; set; } = @".*\.(msi|exe|zip)$";
    public string InstallerArgs { get; set; } = "/S";
    public bool IncludePrereleases { get; set; }
    public string? GitHubToken { get; set; }
    public string[] PreserveGlobs { get; set; } = { "*.ini", "*.json", "*.db", "*.csv", "logs" };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HamProgramAutoUpdate", "pota_updater_config.json");

    public static PotaUpdaterConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<PotaUpdaterConfig>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception)
        {
            // Fall through to defaults.
        }

        return new PotaUpdaterConfig();
    }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";
}
