using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
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

        // This install directory also holds bundled utility exes (e.g.
        // CleanupObsoleteFiles.exe, dropped by this product's own installer
        // custom action) whose FileVersion has nothing to do with the
        // product's real version - see ExeFinder's own doc comment for the
        // real bug this already caused before it started preferring the exe
        // matching the product name.
        // No UpdaterLog available here (DetectTarget's TargetDetector delegate
        // takes no context - see TargetDetection.cs), so ambiguity goes to the
        // console rather than being silently dropped, same fallback channel
        // UpdaterLog.BeginRun itself uses when it has no log to write to yet.
        var exe = installDir is { } loc
            ? ExeFinder.FindByProductName(loc, config.ProductName,
                onAmbiguous: msg => Console.WriteLine($"POTA Activator detection: {msg}"))
            : null;
        var version = exe is not null ? FileVersionHelper.ReadFileVersion(exe) : entry.DisplayVersion;
        return DetectedTarget.Found(exe ?? installDir, version);
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx, closingName: "POTA");

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

        var tempDir = Path.Combine(AppPaths.TempDir, $"PotaUpdate_{Guid.NewGuid():N}");
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

    private static bool IsRunning(string exePath) => ProcessFinder.FindByExePath(exePath).Length > 0;

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
        var stagingDir = Path.Combine(AppPaths.TempDir, $"PotaStage_{Guid.NewGuid():N}");

        try
        {
            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            if (Directory.Exists(installDir)) DirectoryCopy.CopyAll(installDir, backupDir);

            try
            {
                CopyDirectoryPreserving(stagingDir, installDir, preserveGlobs);
            }
            catch (Exception updateEx)
            {
                try
                {
                    if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true);
                    if (Directory.Exists(backupDir)) DirectoryCopy.CopyAll(backupDir, installDir);
                }
                catch (Exception restoreEx)
                {
                    // Both the update AND the restore-from-backup failed -
                    // surface both rather than letting the restore failure
                    // silently replace the original error the caller most
                    // needs to see.
                    throw new AggregateException(
                        "POTA update failed and restoring the pre-update backup also failed - " +
                        "the install directory may be left in a partially-updated state.",
                        updateEx, restoreEx);
                }
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch (Exception) { }
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch (Exception) { }
        }
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
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var glob in globs)
        {
            if (glob.StartsWith('*') && name.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase)) return true;

            var normalizedGlob = glob.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (normalizedGlob.Contains(Path.DirectorySeparatorChar))
            {
                // A hand-edited entry containing a path separator (e.g.
                // "data/settings.ini") names one specific relative path -
                // match that exactly rather than as a whole-segment name,
                // since no single segment of it is meant to stand alone.
                if (string.Equals(relativePath, normalizedGlob, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }

            // A plain folder/file name like "logs" (no wildcard, no
            // separator) means "this whole segment, anywhere in the path" -
            // matched by exact path SEGMENT, not by substring. A substring
            // check here previously also matched an unrelated file like
            // "ChangeLogs.txt" or "Catalogs\reference.db", silently freezing
            // it at whatever version was installed when that substring first
            // collided.
            if (string.Equals(name, glob, StringComparison.OrdinalIgnoreCase)) return true;
            if (segments.Any(s => string.Equals(s, glob, StringComparison.OrdinalIgnoreCase))) return true;
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
    // The setup exe is built with Inno Setup, not NSIS - "/S" (NSIS's silent
    // flag) is meaningless to Inno, so the installer showed its full GUI and
    // sat waiting for input until SilentExeInstaller.RunAsync's 300s timeout
    // killed it (exit code -1). Confirmed live 2026-09-03: Select-String
    // over the downloaded 1.4.0 setup exe matched "Inno Setup" /
    // "Inno Setup Setup Data", and the stuck process's window title was
    // literally "Setup - POTA Activator Park Activations 1.4.0".
    public string InstallerArgs { get; set; } = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
    public bool IncludePrereleases { get; set; }
    public string? GitHubToken { get; set; }
    /// <summary>Files/folders under the install dir that survive a zip
    /// update. MatchesAnyGlob only supports a single LEADING "*" wildcard
    /// (e.g. "*.ini") or a plain exact name/relative-path entry (e.g.
    /// "logs", "data/settings.ini") - it is not general glob syntax. An
    /// entry with a wildcard anywhere else (e.g. "backup*", "*data*") will
    /// silently never match anything, so the file it was meant to protect
    /// would be overwritten on the next update instead of preserved.</summary>
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
                if (loaded is not null)
                {
                    MigrateTokenAtRest(loaded);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to defaults.
        }

        return new PotaUpdaterConfig();
    }

    /// <summary>
    /// This file has no UI - a GitHubToken is hand-entered by editing the
    /// JSON directly, so it starts out as plaintext. The first Load() after
    /// that encrypts it at rest with DPAPI (tied to this Windows user and
    /// machine) and rewrites the file, then leaves the plaintext value in
    /// memory either way, so callers (FetchLatestReleaseAsync) never need to
    /// know which form was actually on disk.
    /// </summary>
    private static void MigrateTokenAtRest(PotaUpdaterConfig config)
    {
        if (string.IsNullOrEmpty(config.GitHubToken)) return;

        if (DpapiProtector.IsProtected(config.GitHubToken))
        {
            try
            {
                config.GitHubToken = DpapiProtector.Unprotect(config.GitHubToken);
            }
            catch (Exception)
            {
                // DPAPI ties encryption to this exact Windows user+machine -
                // a config file copied to a new PC, a fresh reinstall, or a
                // different Windows account can't decrypt it. Clear just the
                // unreadable token rather than letting this propagate into
                // Load()'s outer catch, which would otherwise discard the
                // WHOLE config (Repository/ProductName/AssetPattern/etc, not
                // just the token) back to hardcoded defaults.
                config.GitHubToken = null;
            }
            return;
        }

        var plaintext = config.GitHubToken;
        try
        {
            config.GitHubToken = DpapiProtector.Protect(plaintext);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Best-effort: the token still works from memory this run even if
            // the at-rest upgrade didn't stick - it'll just retry next Load().
        }
        finally
        {
            config.GitHubToken = plaintext;
        }
    }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

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
