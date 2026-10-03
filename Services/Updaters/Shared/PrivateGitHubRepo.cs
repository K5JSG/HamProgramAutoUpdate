using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using HamProgramAutoUpdate.Services.Updaters.Programs;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// GitHub access for updaters whose repo is private: the release lookup and
/// the asset download are authenticated with a read-only token kept in a
/// per-PC file under HistoryStore.StateDir - no GitHub CLI or other software
/// needed. The file holds either the bare token or {"GitHubToken": "..."};
/// a plaintext (or older user-scoped) token is rewritten DPAPI-encrypted on
/// first read, with the file locked to Administrators and SYSTEM (see
/// DpapiProtector). Never ship or commit a token.
/// </summary>
public static class PrivateGitHubRepo
{
    public static string TokenFilePath(string fileName) => Path.Combine(HistoryStore.StateDir, fileName);

    /// <summary>The token from <paramref name="filePath"/>, or null if the
    /// file is missing/empty/unreadable (logged).</summary>
    public static string? LoadToken(string filePath, UpdaterLog log)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            var text = File.ReadAllText(filePath).Trim();
            if (text.Length == 0) return null;

            var stored = text;
            if (text.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                stored = doc.RootElement.TryGetProperty("GitHubToken", out var value)
                    ? value.GetString()?.Trim() ?? ""
                    : "";
                if (stored.Length == 0) return null;
            }

            string token;
            if (DpapiProtector.IsProtected(stored))
            {
                try
                {
                    token = DpapiProtector.Unprotect(stored);
                }
                catch (Exception)
                {
                    // Encrypted on a different PC (the file was copied over),
                    // or an older per-user value read by another account.
                    log.Line($"The GitHub token in {filePath} can't be read on this PC or account - replace it with the token itself.");
                    return null;
                }
                if (!DpapiProtector.NeedsUpgrade(stored)) return token;
            }
            else
            {
                token = stored;
            }

            try
            {
                var json = JsonSerializer.Serialize(new { GitHubToken = DpapiProtector.Protect(token) });
                File.WriteAllText(filePath, json);
                SecureFile.RestrictToAdmins(filePath);
            }
            catch (Exception)
            {
                // Best-effort: the token still works this run; encrypting
                // it at rest is retried next time.
            }
            return token;
        }
        catch (Exception ex)
        {
            log.Line($"Could not read {filePath} ({ex.Message}).");
            return null;
        }
    }

    public static void AddHeaders(HttpRequestMessage request, string token, string accept)
    {
        request.Headers.UserAgent.ParseAdd("HamProgramAutoUpdate");
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    internal static async Task<GitHubRelease?> FetchLatestReleaseAsync(HttpClient http, string repository, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        AddHeaders(request, token, "application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<GitHubRelease>(json);
    }

    /// <summary>Downloads a release asset through its API URL (the one that
    /// works for a private repo).</summary>
    public static Task<(bool ok, string? error)> DownloadAssetAsync(
        HttpClient http, string assetApiUrl, string token, string destination, CancellationToken ct) =>
        HttpDownloader.DownloadToFileAsync(http, assetApiUrl, destination, ct,
            configureRequest: request => AddHeaders(request, token, "application/octet-stream"));
}
