using System.Net.Http;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from n1mm_updater.py. The trickiest part isn't the version check -
/// it's that the actual download link sits behind a WordPress "Download
/// Manager" page that may present a form to submit, or a plain link, or a
/// client-side (meta-refresh / JS) redirect rather than a real HTTP redirect.
/// ChaseDownloadUrlAsync follows that chain with plain HTTP + regex, the same
/// way the original script did (no real browser needed).
/// </summary>
public sealed class N1mmUpdater : UpdaterBase
{
    private const string PageUrl = "https://n1mmwp.hamdocs.com/mmfiles/categories/programlatestupdate/";

    private static readonly Regex VersionRegex = new(@"1\.0\.\d{5}", RegexOptions.Compiled);

    private static readonly Regex DownloadPageHrefRegex = new(
        @"href=[""'](?<url>[^""']*(?:wpdmdl=|n1mm-logger-update)[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DownloadHrefRegex = new(
        @"href=[""'](?<url>[^""']*(?:/mmfile/get/file/|wpdmdl=)[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The download form's action looks like a complete file URL, but the
    // server rejects a bare request to it ("Access denied") without its
    // hidden fields (cmdm_nonce, id, shortcodeId, backurl) POSTed too - so
    // this must never be treated as an already-resolved link, only ever
    // driven through the form-submit path below. It must also be specific
    // enough to skip an unrelated, earlier <form action="https://site/">
    // (the site's search box) that would otherwise match first purely by
    // document order.
    private static readonly Regex FormRegex = new(
        @"<form[^>]*action=[""'](?<action>[^""']*(?:/mmfile/get/file/|wpdmdl=)[^""']*)[""'][^>]*>(?<body>.*?)</form>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HiddenInputRegex = new(
        @"<input[^>]*type=[""']hidden[""'][^>]*name=[""'](?<name>[^""']+)[""'][^>]*value=[""'](?<value>[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MetaRefreshRegex = new(
        @"<meta[^>]+http-equiv=[""']?refresh[""']?[^>]+content=[""'][^""']*url=(?<url>[^""'>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsRedirectRegex = new(
        @"location(?:\.href)?\s*(?:=|\.replace\()\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public N1mmUpdater() : base("n1mm", "N1MM Logger+", TargetDetectors.FixedPaths(
        @"C:\Program Files (x86)\N1MM Logger+\N1MMLogger.net.exe",
        @"C:\Program Files\N1MM Logger+\N1MMLogger.net.exe"))
    {
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx, closingName: "N1MM");

        ctx.Log.Line($"Checking {PageUrl} for the latest version...");
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, PageUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"N1MM Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var versions = VersionRegex.Matches(html).Select(m => m.Value).Distinct().ToList();
        if (versions.Count == 0)
        {
            ctx.Log.Line("N1MM Updater FAILED: could not find a version number on the download page");
            return UpdateResult.Failed("Version not found on download page");
        }
        var latest = versions.Aggregate((a, b) => FileVersionHelper.IsNewer(b, a) ? b : a);

        var current = target.Version;
        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("N1MM Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        var pageHref = DownloadPageHrefRegex.Match(html);
        if (!pageHref.Success)
        {
            ctx.Log.Line("N1MM Updater FAILED: could not find a download link on the download page");
            return UpdateResult.Failed("Download link not found");
        }

        var downloadPageUrl = ToAbsolute(pageHref.Groups["url"].Value, PageUrl);

        KillIfRunning(target.InstallPath, ctx);

        var tempDir = Path.Combine(Path.GetTempPath(), $"N1mmUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, $"N1MM-Logger-Update-{latest}.exe");

        try
        {
            ctx.Log.Line($"Resolving the real download link from {downloadPageUrl} ...");
            var (downloadOk, downloadError) = await ChaseAndDownloadAsync(
                ctx.Http, downloadPageUrl, installerPath, ctx.Log, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"N1MM Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            ctx.Log.Line("Installing silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                installerPath, new[] { "/S" }, ctx.CancellationToken, timeout: TimeSpan.FromSeconds(300));

            if (!installOk)
            {
                ctx.Log.Line($"N1MM Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            ctx.Log.Line($"Success: Upgrade applied successfully to version {latest}!");
            ctx.Log.Line("N1MM Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    private static void KillIfRunning(string? installPath, UpdaterContext ctx)
    {
        var processName = installPath is null ? "N1MMLogger.net" : Path.GetFileNameWithoutExtension(installPath);

        try
        {
            foreach (var proc in ProcessFinder.FindByName(processName))
            {
                ctx.Log.Line($"Closing running {processName} (PID {proc.Id}) before installing...");
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception) { }
    }

    /// <summary>Follows the download-manager page - a form to submit (whose
    /// response may itself BE the file, delivered directly as the POST
    /// result rather than via a further redirect - confirmed against the
    /// live site: a correctly-authorized POST returns
    /// application/octet-stream with a Content-Disposition: attachment
    /// header, not HTML), a plain link, or a client-side redirect - until
    /// the real installer is saved to <paramref name="destPath"/> or we give
    /// up after a few hops.</summary>
    private static async Task<(bool ok, string? error)> ChaseAndDownloadAsync(
        HttpClient http, string startUrl, string destPath, UpdaterLog log, CancellationToken ct, int maxHops = 5)
    {
        var url = startUrl;

        for (var hop = 0; hop < maxHops; hop++)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }

            using (response)
            {
                if (IsFileResponse(response))
                    return await SaveResponseAsync(response, destPath, ct);

                var body = await response.Content.ReadAsStringAsync(ct);

                var formMatch = FormRegex.Match(body);
                if (formMatch.Success)
                {
                    var action = ToAbsolute(formMatch.Groups["action"].Value, url);
                    var fields = HiddenInputRegex.Matches(formMatch.Groups["body"].Value)
                        .Select(m => new KeyValuePair<string, string>(m.Groups["name"].Value, m.Groups["value"].Value))
                        .ToList();

                    log.Line($"Submitting download form to {action} ...");
                    using var content = new FormUrlEncodedContent(fields);
                    HttpResponseMessage postResponse;
                    try
                    {
                        postResponse = await http.PostAsync(action, content, ct);
                    }
                    catch (Exception ex)
                    {
                        return (false, ex.Message);
                    }

                    using (postResponse)
                    {
                        if (IsFileResponse(postResponse))
                            return await SaveResponseAsync(postResponse, destPath, ct);

                        var postBody = await postResponse.Content.ReadAsStringAsync(ct);
                        var afterPost = ExtractRedirect(postBody);
                        if (afterPost is not null)
                        {
                            url = ToAbsolute(afterPost, action);
                            continue;
                        }
                    }
                }

                var hrefMatch = DownloadHrefRegex.Match(body);
                if (hrefMatch.Success)
                {
                    url = ToAbsolute(hrefMatch.Groups["url"].Value, url);
                    continue;
                }

                var redirect = ExtractRedirect(body);
                if (redirect is not null)
                {
                    url = ToAbsolute(redirect, url);
                    continue;
                }

                return (false, "Could not resolve a direct download link from the download manager page");
            }
        }

        return (false, "Gave up after too many redirects/form submissions");
    }

    /// <summary>A response is the real file, not another HTML hop, if it
    /// says so via Content-Type or Content-Disposition rather than by
    /// guessing from a URL string.</summary>
    private static bool IsFileResponse(HttpResponseMessage response)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return false;
        if (contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)) return true;
        if (contentType.Contains("exe", StringComparison.OrdinalIgnoreCase)) return true;
        return response.Content.Headers.ContentDisposition?.DispositionType == "attachment";
    }

    private static async Task<(bool ok, string? error)> SaveResponseAsync(
        HttpResponseMessage response, string destPath, CancellationToken ct)
    {
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = File.Create(destPath))
        {
            await source.CopyToAsync(dest, ct);
        }

        if (new FileInfo(destPath).Length < 10_000 || !HttpDownloader.LooksLikeExe(destPath))
            return (false, "Downloaded file does not look like a real installer");

        return (true, null);
    }

    private static string? ExtractRedirect(string html)
    {
        var meta = MetaRefreshRegex.Match(html);
        if (meta.Success) return meta.Groups["url"].Value;

        var js = JsRedirectRegex.Match(html);
        return js.Success ? js.Groups["url"].Value : null;
    }

    private static string ToAbsolute(string url, string baseUrl) =>
        Uri.TryCreate(new Uri(baseUrl), url, out var abs) ? abs.ToString() : url;
}
