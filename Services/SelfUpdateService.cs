using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HamProgramAutoUpdate.Services;

/// <summary>A newer release found on GitHub, ready to download.</summary>
public sealed class ReleaseInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public required string DownloadUrl { get; init; }
    public required string AssetName { get; init; }
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

            return new ReleaseInfo
            {
                Version = latest,
                TagName = release.TagName,
                DownloadUrl = asset.BrowserDownloadUrl,
                AssetName = asset.Name,
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
            var tempPath = Path.Combine(Path.GetTempPath(), release.AssetName);

            using (var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                await response.Content.CopyToAsync(fs, ct);
            }

            progress?.Report("Launching installer...");
            Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
