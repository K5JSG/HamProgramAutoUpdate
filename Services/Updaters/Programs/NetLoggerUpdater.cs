using System.Net.Http;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from NetLogger_Updater.py. NetLogger's site gates the download
/// behind a "register your callsign/email" POST rather than real auth -
/// see UpdaterSettings for where those values now live instead of being
/// hardcoded in source.
/// </summary>
public sealed class NetLoggerUpdater : UpdaterBase
{
    private const string PageUrl = "https://netlogger.org/download.php";
    private const string DownloadCgiUrl = "https://netlogger.org/cgi-bin/download.cgi";

    private static readonly string[] CandidatePaths =
    {
        @"C:\Program Files (x86)\Net Logger\netlogger.exe",
        @"C:\Program Files\Net Logger\netlogger.exe",
    };

    // The page's <option value="NetLogger_X.Y.Z_Windows_x86.msi" SELECTED="SELECTED">
    // puts the filename in the value attribute, before the SELECTED keyword -
    // not as inner text after it.
    private static readonly Regex SelectedOptionRegex = new(
        @"<option\s+value=[""']NetLogger_(?<ver>\d+\.\d+\.\d+)_Windows_x86\.msi[""'][^>]*\bSELECTED\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FilenameRegex = new(
        @"NetLogger_(\d+\.\d+\.\d+)_Windows_x86\.msi", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Older download options are left in the page wrapped in HTML comments
    // rather than removed - strip comments before scanning for a version so
    // a stale commented-out entry never wins over the real current one.
    private static readonly Regex HtmlCommentRegex = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ReleaseNotesVersionRegex = new(
        @"Version\s+(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MsiUrlInResponseRegex = new(
        @"https?://[^\s""'<>]+\.msi", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public NetLoggerUpdater() : base("netlogger", "NetLogger", DetectNetLogger)
    {
    }

    /// <summary>Same candidate paths as everything else, but with a fallback
    /// the original script needed: some NetLogger builds ship without a
    /// version resource on the exe, so read it out of Release_Notes.html
    /// in the install dir instead.</summary>
    private static DetectedTarget DetectNetLogger()
    {
        foreach (var path in CandidatePaths)
        {
            if (!File.Exists(path)) continue;

            var version = FileVersionHelper.ReadFileVersion(path);
            if (version is null)
            {
                var notesPath = Path.Combine(Path.GetDirectoryName(path)!, "Release_Notes.html");
                try
                {
                    if (File.Exists(notesPath))
                    {
                        var match = ReleaseNotesVersionRegex.Match(File.ReadAllText(notesPath));
                        if (match.Success) version = match.Groups[1].Value;
                    }
                }
                catch (Exception) { }
            }

            return DetectedTarget.Found(path, version);
        }

        return DetectedTarget.NotFound;
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        ctx.Log.Line($"Checking {PageUrl} for the latest version...");
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, PageUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"NetLogger Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        html = HtmlCommentRegex.Replace(html, "");

        var selected = SelectedOptionRegex.Match(html);
        var match = selected.Success ? selected : FilenameRegex.Match(html);
        if (!match.Success)
        {
            ctx.Log.Line("NetLogger Updater FAILED: could not find a version number on the download page");
            return UpdateResult.Failed("Version not found on download page");
        }

        var latest = selected.Success ? selected.Groups["ver"].Value : match.Groups[1].Value;
        var fileName = $"NetLogger_{latest}_Windows_x86.msi";
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("NetLogger Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        var settings = UpdaterSettings.Load();
        var tempDir = Path.Combine(AppPaths.TempDir, $"NetLoggerUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var msiPath = Path.Combine(tempDir, fileName);

        try
        {
            ctx.Log.Line($"Registering download with NetLogger (callsign {settings.NetLoggerCallsign})...");
            var fields = new[]
            {
                new KeyValuePair<string, string>("url", fileName),
                new KeyValuePair<string, string>("name", settings.NetLoggerCallsign),
                new KeyValuePair<string, string>("email", settings.NetLoggerEmail),
            };

            bool downloadOk;
            string? downloadError;
            try
            {
                using var content = new FormUrlEncodedContent(fields);
                using var response = await ctx.Http.PostAsync(DownloadCgiUrl, content, ctx.CancellationToken);

                // The cgi either serves the file directly as the POST response
                // (confirmed against the live site: application/octet-stream,
                // not a redirect) or echoes back a page containing the real
                // URL as text - never assume which without checking the
                // actual Content-Type first, since reading a binary MSI as a
                // string and treating it as a URL throws (and did, in
                // testing: "Invalid URI: The Uri string is too long").
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Log.Line("Download served directly by the registration request - saving it...");
                    await using var source = await response.Content.ReadAsStreamAsync(ctx.CancellationToken);
                    await using var dest = File.Create(msiPath);
                    await source.CopyToAsync(dest, ctx.CancellationToken);

                    downloadOk = new FileInfo(msiPath).Length >= 10_000;
                    downloadError = downloadOk ? null : "Downloaded file is too small to be real";
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
                    var urlMatch = MsiUrlInResponseRegex.Match(responseBody);
                    // Best-effort guess if the response text didn't contain a
                    // usable link - not confirmed against a real response
                    // shaped this way, but a wrong guess just fails the
                    // download cleanly below rather than fetching the wrong
                    // file (DownloadToFileAsync's EnsureSuccessStatusCode +
                    // retry-then-fail).
                    var downloadUrl = urlMatch.Success ? urlMatch.Value : $"https://netlogger.org/downloads/{fileName}";

                    ctx.Log.Line($"Downloading {downloadUrl} ...");
                    (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                        ctx.Http, downloadUrl, msiPath, ctx.CancellationToken);
                }
            }
            catch (Exception ex)
            {
                ctx.Log.Line($"NetLogger Updater FAILED: download registration failed ({ex.Message})");
                return UpdateResult.Failed(ex.Message);
            }

            if (!downloadOk)
            {
                ctx.Log.Line($"NetLogger Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            var installLogPath = Path.Combine(tempDir, "install.log");
            ctx.Log.Line("Installing silently via msiexec...");
            var (installOk, exitCode, message) = await MsiInstaller.InstallAsync(msiPath, installLogPath, ctx.CancellationToken);

            if (!installOk)
            {
                ctx.Log.Line($"NetLogger Updater FAILED: {message} (exit code {exitCode})");
                return UpdateResult.Failed(message);
            }

            ctx.Log.Line($"Updated to {latest}. {message}");
            ctx.Log.Line("NetLogger Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }
}
