using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services;

/// <summary>A newer release found on GitHub, ready to download.</summary>
public sealed class ReleaseInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public required string DownloadUrl { get; init; }
    public required string AssetName { get; init; }

    /// <summary>
    /// Download URL of the "&lt;AssetName&gt;.sha256" asset published
    /// alongside the setup exe by .github/workflows/release.yml, or null if
    /// this release predates that checksum being published.
    /// DownloadAndLaunchInstallerAsync refuses to launch a downloaded
    /// installer elevated without one - see it for why.
    /// </summary>
    public string? ChecksumUrl { get; init; }
}

/// <summary>
/// Checks github.com/K5JSG/HamProgramAutoUpdate for a newer release than the
/// running exe, and can download + launch that release's Inno Setup
/// installer. The installer itself (see installer/HamProgramAutoUpdate.iss)
/// already knows how to stop the running app and upgrade in place, so this
/// class's job ends once the downloaded setup exe is started.
/// </summary>
public static class SelfUpdateService
{
    private const string Owner = "K5JSG";
    private const string Repo = "HamProgramAutoUpdate";
    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    /// <summary>
    /// Where a downloaded setup exe is staged before being launched. Kept
    /// under this app's own LocalAppData folder (alongside update_history.json)
    /// rather than the Windows temp folder so a user only ever needs one
    /// antivirus/Norton 360 folder exclusion for this app's own downloads,
    /// not a blanket exclusion on the shared system temp directory.
    /// </summary>
    private static string DownloadDir => Path.Combine(HistoryStore.StateDir, "Updates");

    /// <summary>
    /// Where Setup.exe itself is pointed to self-extract into (see
    /// DownloadAndLaunchInstallerAsync) instead of the shared Windows temp
    /// folder - same reasoning as DownloadDir: one antivirus exclusion
    /// covers everything this app's update flow ever touches on disk.
    /// </summary>
    private static string InstallTempDir => Path.Combine(HistoryStore.StateDir, "InstallTemp");

    /// <summary>
    /// Deletes any setup exe and self-extracted install files left behind by
    /// a previous update. The app shuts itself down immediately after
    /// launching the installer (see DownloadAndLaunchInstallerAsync), so
    /// these can only ever be cleaned up on a later run - this is called
    /// once at startup, by which point either the old version's installer
    /// has already finished (this process IS the result of it) or the
    /// update was abandoned and the leftovers are simply stale. Never throws.
    /// </summary>
    public static void CleanupOldDownloads()
    {
        foreach (var dir in new[] { DownloadDir, InstallTempDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GhAsset> Assets { get; set; } = new();
    }

    private static HttpClient NewHttpClient(TimeSpan timeout)
    {
        // UseProxy = false: same WPAD-hang avoidance as HeadlessUpdateRunner.
        var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HamProgramAutoUpdate", AppInfo.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Only the Major.Minor.Build triplet is meaningful for a
    /// release tag like "v1.2.0"; normalizing away Revision (which System.
    /// Version otherwise treats as -1/undefined and compares inconsistently
    /// against a 4-part file version like "1.2.0.0") keeps the comparison
    /// well-defined either way.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), 0);

    /// <summary>
    /// Returns the newest published (non-draft, non-prerelease) release if
    /// its version is newer than this exe's own, or null if up to date, the
    /// release has no installer asset, or GitHub could not be reached.
    /// Never throws - a failed check should never interrupt using the app.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewHttpClient(TimeSpan.FromSeconds(15));
            var release = await http.GetFromJsonAsync<GhRelease>(LatestReleaseUri, ct);
            if (release is null || release.Draft || release.Prerelease) return null;

            var tag = release.TagName.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest)) return null;

            if (Normalize(latest) <= Normalize(AppInfo.VersionValue)) return null;

            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null) return null;

            var checksumAsset = release.Assets.FirstOrDefault(a =>
                a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

            return new ReleaseInfo
            {
                Version = latest,
                TagName = release.TagName,
                DownloadUrl = asset.BrowserDownloadUrl,
                AssetName = asset.Name,
                ChecksumUrl = checksumAsset?.BrowserDownloadUrl,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the release's setup exe to a temp file and launches it
    /// (elevated, since Setup's own PrivilegesRequired=admin - UseShellExecute
    /// lets Windows handle that the same way the dashboard's own manifest
    /// does). Returns an error message on failure, or null on success; the
    /// caller should shut the app down right after so Setup can replace the
    /// running exe.
    /// </summary>
    public static async Task<string?> DownloadAndLaunchInstallerAsync(
        ReleaseInfo release, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report("Downloading update...");

            using var http = NewHttpClient(TimeSpan.FromMinutes(5));
            Directory.CreateDirectory(DownloadDir);
            var downloadPath = Path.Combine(DownloadDir, release.AssetName);

            using (var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(downloadPath, FileMode.Create, FileAccess.Write);
                await response.Content.CopyToAsync(fs, ct);
            }

            // Confirm it at least looks like a real Windows executable - the
            // same sanity check every other updater in this app applies to
            // its own downloads (see HttpDownloader.LooksLikeExe). This alone
            // only catches an outage, redirect, or truncated transfer handing
            // an HTML error page or partial file to Process.Start under an
            // admin token - it says nothing about a genuinely swapped or
            // tampered file, which is what the checksum check below is for.
            if (new FileInfo(downloadPath).Length < 100_000 || !HttpDownloader.LooksLikeExe(downloadPath))
            {
                TryDelete(downloadPath);
                return "The downloaded file does not look like a real installer.";
            }

            // Never launch (elevated!) a file whose integrity wasn't
            // verified against the checksum release.yml publishes alongside
            // every -setup.exe asset. Fails closed: a release with no
            // checksum asset (e.g. one published before this check existed)
            // or a mismatched hash both refuse the update rather than
            // silently trusting the raw HTTPS download.
            progress?.Report("Verifying update...");
            var checksumError = await VerifyChecksumAsync(release, downloadPath, ct);
            if (checksumError is not null)
            {
                TryDelete(downloadPath);
                return checksumError;
            }

            progress?.Report("Launching installer...");
            if (App.Runner.AnyRunning())
                progress?.Report("Waiting for a running updater to finish...");
            await LaunchWithRedirectedTempDirAsync(downloadPath);

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Downloads the "&lt;asset&gt;.sha256" file published alongside the
    /// installer and compares it against the actual SHA256 of the
    /// downloaded file. Returns null if they match, or an error message
    /// otherwise (including when no checksum was published for this
    /// release - see the ChecksumUrl doc comment on ReleaseInfo).
    /// </summary>
    private static async Task<string?> VerifyChecksumAsync(ReleaseInfo release, string downloadPath, CancellationToken ct)
    {
        if (release.ChecksumUrl is null)
            return "This release did not publish a checksum, so the download cannot be verified.";

        string expectedText;
        try
        {
            using var http = NewHttpClient(TimeSpan.FromSeconds(30));
            expectedText = await http.GetStringAsync(release.ChecksumUrl, ct);
        }
        catch (Exception ex)
        {
            return $"Could not download the update's checksum: {ex.Message}";
        }

        // Get-FileHash's default text output is just the hex hash with
        // trailing whitespace/newline; tolerate a leading "<hash> *<file>"
        // sha256sum-style line too in case the asset is ever regenerated
        // with a different tool.
        var expectedHash = expectedText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(expectedHash))
            return "The update's checksum file was empty or malformed.";

        byte[] actualHashBytes;
        await using (var fs = new FileStream(downloadPath, FileMode.Open, FileAccess.Read))
        {
            actualHashBytes = await SHA256.HashDataAsync(fs, ct);
        }
        var actualHash = Convert.ToHexString(actualHashBytes);

        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)
            ? null
            : "The downloaded update's checksum did not match - it may be corrupted or tampered with.";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    /// <summary>
    /// Launches Setup.exe with TMP/TEMP pointed at InstallTempDir for just
    /// this call, so Inno Setup's own self-extraction (which picks its
    /// "is-XXXXXXXX.tmp" working folder via Win32 GetTempPath - TMP checked
    /// first, then TEMP) lands there instead of the shared Windows temp
    /// folder. UseShellExecute here does not hand the child a fresh
    /// environment block of its own - it inherits the calling process's, and
    /// since this app's own manifest already requires admin the same as
    /// Setup.exe's, no separate UAC elevation broker hop (which could hand
    /// the child a different default environment) is involved. Restored
    /// immediately after starting the process so nothing else in this
    /// process is affected by the redirected TMP/TEMP once launched.
    ///
    /// TMP/TEMP are process-wide, not per-thread, so without
    /// PauseForInstallAsync below, an in-process updater run (App.Runner,
    /// started from a dashboard card's own Run button on a background task)
    /// could have Path.GetTempPath() resolve to InstallTempDir for the width
    /// of this call - confirmed reachable live, since the dashboard and the
    /// self-update prompt run in the same process and nothing previously
    /// stopped both happening at once.
    /// </summary>
    private static async Task LaunchWithRedirectedTempDirAsync(string exePath)
    {
        Directory.CreateDirectory(InstallTempDir);

        using var pause = await App.Runner.PauseForInstallAsync();

        var (prevTmp, prevTemp) = (Environment.GetEnvironmentVariable("TMP"), Environment.GetEnvironmentVariable("TEMP"));
        try
        {
            Environment.SetEnvironmentVariable("TMP", InstallTempDir);
            Environment.SetEnvironmentVariable("TEMP", InstallTempDir);
            Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", prevTmp);
            Environment.SetEnvironmentVariable("TEMP", prevTemp);
        }
    }
}
