using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using Microsoft.Win32;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// JS8Call, as maintained by the JS8Call-improved project
/// (https://github.com/JS8Call-improved/JS8Call-improved) - GitHub Releases
/// API, one Inno Setup "JS8Call-&lt;ver&gt;-installer.exe" asset per release,
/// installed silently in place (Program Files\JS8Call, same folder every
/// version). /releases/latest skips the project's prerelease "Development
/// Build" releases.
///
/// Detection is pinned to the fork's Inno AppId uninstall keys rather than a
/// "JS8Call" name match: the original JS8Call (KN4CRD's 2.2.0) registers
/// under that name too, and installing the fork over it would leave two
/// separate installs instead of updating the one the user has.
///
/// The fork changed its AppId with 3.0.3 (their June 2026 Inno script
/// rewrite). 3.0.3 installs over the same folder but registers as a second
/// app, so upgrading from 3.0.2 - by hand or here - leaves two Installed Apps
/// entries and two Start menu shortcuts (seen live).
/// <see cref="RemoveStaleOldAppIdEntry"/> cleans that up on every run.
/// </summary>
public sealed class Js8CallUpdater : UpdaterBase
{
    private const string Repository = "JS8Call-improved/JS8Call-improved";
    private const string ProductName = "JS8Call";
    private const string CurrentAppId = "{B5281957-28FD-4BAE-8D06-FC59898D850E}";
    private const string OldAppId = "{30445EF9-10CD-42A3-AE4F-C44859691B47}";

    private static readonly Regex InstallerAssetRegex = new(
        @"^JS8Call-[\d.]+-installer\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Js8CallUpdater() : base("js8call", ProductName, DetectJs8Call)
    {
    }

    private sealed record InnoEntry(string KeyPath, string? Version, string? InstallDir, string? Uninstaller);

    private static InnoEntry? FindEntry(string appId)
    {
        foreach (var keyPath in new[]
                 {
                     $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{appId}_is1",
                     $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{appId}_is1",
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key is null) continue;

                return new InnoEntry(
                    keyPath,
                    key.GetValue("DisplayVersion") as string,
                    key.GetValue("InstallLocation") as string,
                    (key.GetValue("UninstallString") as string)?.Trim().Trim('"'));
            }
            catch (Exception)
            {
                // Treat a registry-access failure as "not found here".
            }
        }
        return null;
    }

    private static DetectedTarget DetectJs8Call()
    {
        var entry = new[] { FindEntry(CurrentAppId), FindEntry(OldAppId) }
            .Where(e => e is not null)
            .OrderByDescending(e => e!.Version, Comparer<string?>.Create((a, b) =>
                FileVersionHelper.IsNewer(a, b) ? 1 : FileVersionHelper.IsNewer(b, a) ? -1 : 0))
            .FirstOrDefault();
        if (entry is null) return DetectedTarget.NotFound;

        var exe = string.IsNullOrWhiteSpace(entry.InstallDir) ? null : Path.Combine(entry.InstallDir, "JS8Call.exe");
        return DetectedTarget.Found(exe is not null && File.Exists(exe) ? exe : entry.InstallDir, entry.Version);
    }

    /// <summary>
    /// Once the new-AppId install is in the same folder as an old-AppId one,
    /// the old entry is stale: its files were all overwritten. Its own
    /// uninstaller can't be used (it would delete the files 3.0.3 just
    /// wrote), so remove just what's left of it - its registry entry, its own
    /// unins00N.exe/.dat (only if that .dat really is the old AppId's), and
    /// the old installer's "js8call" Start menu folder, which the new one
    /// replaced with a single JS8Call shortcut.
    /// </summary>
    private static void RemoveStaleOldAppIdEntry(UpdaterContext ctx)
    {
        var current = FindEntry(CurrentAppId);
        var old = FindEntry(OldAppId);
        if (current is null || old is null) return;
        if (string.IsNullOrWhiteSpace(current.InstallDir) || string.IsNullOrWhiteSpace(old.InstallDir) ||
            !string.Equals(current.InstallDir.TrimEnd('\\'), old.InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return;

        ctx.Log.Line($"Removing the leftover Installed Apps entry for JS8Call {old.Version ?? ""} (its files were replaced by {current.Version}).");
        try
        {
            if (old.Uninstaller is { } oldUninstaller &&
                !string.Equals(oldUninstaller, current.Uninstaller, StringComparison.OrdinalIgnoreCase))
            {
                var dat = Path.ChangeExtension(oldUninstaller, ".dat");
                if (File.Exists(dat) && DatBelongsTo(dat, OldAppId))
                {
                    File.Delete(dat);
                    if (File.Exists(oldUninstaller)) File.Delete(oldUninstaller);
                }
            }

            var oldGroup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "js8call");
            var newShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "JS8Call.lnk");
            if (Directory.Exists(oldGroup) && File.Exists(newShortcut))
                Directory.Delete(oldGroup, recursive: true);

            Registry.LocalMachine.DeleteSubKeyTree(old.KeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"Could not fully remove the leftover entry ({ex.Message}) - will try again next run.");
        }
    }

    /// <summary>An Inno uninstall log names its AppId in its header.</summary>
    private static bool DatBelongsTo(string datPath, string appId)
    {
        try
        {
            using var stream = File.OpenRead(datPath);
            var buffer = new byte[512];
            var read = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.Latin1.GetString(buffer, 0, read).Contains(appId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        if (!ctx.DryRun && AppInfo.IsElevated) RemoveStaleOldAppIdEntry(ctx);

        ctx.Log.Line($"Checking GitHub releases for {Repository}...");
        GitHubRelease? release;
        try
        {
            release = await FetchLatestReleaseAsync(ctx.Http, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"JS8Call Updater FAILED: could not reach GitHub ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        if (release is null)
        {
            ctx.Log.Line("JS8Call Updater FAILED: no release found");
            return UpdateResult.Failed("No release found");
        }

        var latest = release.TagName.TrimStart('v', 'V');
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("JS8Call Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");

        var asset = release.Assets.FirstOrDefault(a => InstallerAssetRegex.IsMatch(a.Name));
        if (asset is null)
        {
            ctx.Log.Line("JS8Call Updater FAILED: no Windows installer asset found");
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
            ctx.Log.Line("JS8Call Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        // A program you operate with (not a tray app), so don't pull it out
        // from under the user mid-QSO - try again next run.
        if (ProcessFinder.FindByName("JS8Call").Length > 0)
        {
            ctx.Log.Line("JS8Call Updater: the program is currently running - postponing this update.");
            ctx.Log.Line("JS8Call Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"JS8CallUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, asset.Name);

        try
        {
            ctx.Log.Line($"Downloading {asset.BrowserDownloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, asset.BrowserDownloadUrl, downloadPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"JS8Call Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            ctx.Log.Line("Installing silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                downloadPath,
                // !desktopicon: leave the desktop shortcut alone - see
                // CallsignLookupUpdater (rewriting it moves the icon).
                new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/MERGETASKS=!desktopicon" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromSeconds(300));
            if (!installOk)
            {
                ctx.Log.Line($"JS8Call Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            RemoveStaleOldAppIdEntry(ctx);

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("JS8Call Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

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
