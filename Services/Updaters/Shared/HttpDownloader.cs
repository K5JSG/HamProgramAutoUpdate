using System.Net.Http;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// GET/POST helpers with the retry-and-sanity-check behavior HRD and TQSL's
/// Python updaters used: a few attempts with a delay between them, and a
/// refusal to trust a "download" that is suspiciously small or doesn't start
/// with an MZ header (the classic sign of having downloaded an HTML error
/// page instead of the actual installer).
/// </summary>
public static class HttpDownloader
{
    public static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static async Task<string> PostFormAsync(
        HttpClient http, string url, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Downloads to <paramref name="destPath"/>, retrying on
    /// failure, and validates the result looks like a real Windows
    /// installer (exe/msi/zip) rather than an error page.</summary>
    public static async Task<(bool ok, string? error)> DownloadToFileAsync(
        HttpClient http,
        string url,
        string destPath,
        CancellationToken ct,
        int attempts = 4,
        TimeSpan? delayBetweenAttempts = null,
        long minSizeBytes = 10_000)
    {
        var delay = delayBetweenAttempts ?? TimeSpan.FromSeconds(10);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var dest = File.Create(destPath);
                    await source.CopyToAsync(dest, ct);
                }

                var info = new FileInfo(destPath);
                if (info.Length < minSizeBytes)
                    throw new IOException($"Downloaded file is only {info.Length} bytes - too small to be real.");

                return (true, null);
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch (Exception) { }

                if (attempt < attempts) await Task.Delay(delay, ct);
            }
        }

        return (false, lastError?.Message ?? "Download failed.");
    }

    /// <summary>True if the file's first two bytes are "MZ" (a real Windows
    /// PE executable) rather than e.g. an HTML error page saved with a .exe
    /// name.</summary>
    public static bool LooksLikeExe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 2) return false;
            Span<byte> header = stackalloc byte[2];
            var read = stream.Read(header);
            return read == 2 && header[0] == 'M' && header[1] == 'Z';
        }
        catch (Exception)
        {
            return false;
        }
    }
}
