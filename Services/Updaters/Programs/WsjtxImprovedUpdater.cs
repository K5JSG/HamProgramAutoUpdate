using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using Microsoft.Win32;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// New addition - WSJT-X Improved (sourceforge.net/projects/wsjt-x-improved,
/// maintained by Uwe Risse DG2YCB) is a separate, independently-distributed
/// fork of WSJT-X, not a newer WSJT-X release - it has its own SourceForge
/// project, its own version numbering, and needs its own updater entirely
/// separate from WsjtxUpdater.
///
/// Things confirmed live, not guessed, because each one would have silently
/// broken this updater (or worse) otherwise:
///
///  - Its NSIS installer's default install path AND its main exe's filename
///    (bin\wsjtx.exe) are IDENTICAL to real WSJT-X's. Installing it with no
///    explicit /D= override silently overwrote this PC's real WSJT-X
///    install in place - had to be caught and fixed by hand before this
///    updater could even be written safely. Detection therefore checks
///    CandidatePaths (both a dedicated folder and the shared default) but
///    only ever accepts a match whose exe ProductName says "improved" - see
///    WsjtxUpdater's class doc comment for the matching fix on that side.
///    Updates always reinstall to wherever the exe was actually found (same
///    "never move the user's existing install" philosophy WsjtxUpdater
///    already uses for real WSJT-X), never a hardcoded path.
///  - Every release ships three GUI variants of the same build (standard,
///    "AL" - compact, for small screens, and "widescreen" - wider Band
///    Activity window) plus 32/64-bit, all bundling audible-alerts/Cloudlog
///    support ("PLUS"). This only ever fetches 64-bit builds. Which GUI
///    variant is installed can NOT be read back from the exe at all -
///    confirmed live, all three variants' bin\wsjtx.exe report byte-for-byte
///    identical ProductName/FileVersion/FileDescription/InternalName, and
///    only differ in raw MD5 (each variant IS a genuinely different binary,
///    the version resource is just not stamped per-variant). Once this
///    updater has installed a given variant, it remembers which one in its
///    own marker file (ReadInstalledBuildMarker) and keeps installing that
///    same one on every future update - cheap, no network needed.
///
///    The very first time it sees an install it didn't manage itself (no
///    marker yet) is where this got genuinely hard, and is worth recording
///    in full: the RSS feed DOES publish an MD5 per release file, but that
///    hash is of the ~71MB installer container, not the extracted
///    bin\wsjtx.exe payload actually sitting on disk after install -
///    confirmed live (the installer's own hash matched RSS exactly; the
///    payload's did not, for any variant). Determining the real payload
///    hash means actually installing a candidate silently to a throwaway
///    scratch folder and hashing what comes out - which turned out to have
///    its own sharp edge, also confirmed live: this vendor's NSIS installer
///    reuses the SAME registry uninstall key regardless of /D= target, so a
///    scratch-folder probe silently hijacks the real install's registry
///    entry (UninstallString ends up pointing at the scratch folder, which
///    then gets deleted) unless every value under that key is saved before
///    probing and restored after (DetectVariantByHashAsync does this via
///    SaveRegistryValues/RestoreRegistryValues, in a try/finally so a
///    failure partway through - a bad download, a cancelled run - still
///    restores the real entry rather than leaving it broken). This only
///    even has a chance of matching if the installed copy is still on the
///    CURRENT release to begin with (an older build's payload hash can't
///    match any of today's three either way), and only runs for a real
///    (non-dry-run) update that's already been confirmed necessary at the
///    X.Y.Z level - never for a plain --check-update, and never just to
///    resolve a check that would have reported "already up to date"
///    regardless of which variant it turns out to be. When even that
///    fails, it assumes standard (logged clearly) rather than guessing
///    wrong silently.
///  - The installed exe's version resource is unreliable in a second way
///    too: FileVersion is only 3 segments (e.g. "3.2.0") with no build-date
///    info at all, but releases are actually identified by a build DATE
///    suffix in the download filename (e.g. "_PLUS_260818") that the
///    project's own README says can advance independent of the X.Y.Z
///    version ("Always use the latest builds available before reporting
///    any bugs!"). Since that date isn't recoverable from the installed
///    exe at all, the marker file records the exact build string (X.Y.Z +
///    date) alongside the variant, not just the variant alone - naively
///    comparing a constructed "X.Y.Z.date" against a FileVersion that can
///    never have a date segment would otherwise report "update available"
///    forever even when nothing had changed.
/// </summary>
public sealed class WsjtxImprovedUpdater : UpdaterBase
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\WSJT\wsjtx-improved\bin\wsjtx.exe",
        @"C:\WSJT\wsjtx\bin\wsjtx.exe",
    };

    private const string ProjectRssBase = "https://sourceforge.net/projects/wsjt-x-improved/rss";

    private static readonly Regex VersionFolderRegex = new(
        @"WSJT-X_v(?<ver>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum Variant { Standard, Al, Widescreen }

    // Infix sits between "improved_" and "PLUS_" in the filename.
    private static readonly (Variant Variant, string Infix, string Label)[] Variants =
    {
        (Variant.Standard, "", "standard"),
        (Variant.Al, "AL_", "AL"),
        (Variant.Widescreen, "widescreen_", "widescreen"),
    };

    public WsjtxImprovedUpdater() : base("wsjtx_improved", "WSJT-X Improved", Detect)
    {
    }

    /// <summary>Only ever accepts a candidate whose ProductName confirms
    /// it's really WSJT-X Improved - see the class doc comment on why a
    /// bare path/filename match isn't safe here.</summary>
    private static DetectedTarget Detect()
    {
        foreach (var path in CandidatePaths)
        {
            if (!File.Exists(path)) continue;

            var productName = FileVersionHelper.ReadProductName(path);
            if (productName is null || !productName.Contains("improved", StringComparison.OrdinalIgnoreCase))
                continue;

            return DetectedTarget.Found(path, FileVersionHelper.ReadFileVersion(path));
        }
        return DetectedTarget.NotFound;
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled)
        {
            ctx.Log.Line("WSJT-X Improved is not installed on this PC - skipping.");
            ctx.Log.Line("WSJT-X Improved Updater completed successfully");
            return UpdateResult.Skipped("Not installed");
        }

        var installDir = InstallDirFor(target.InstallPath!);

        // A dedicated client, not ctx.Http: the shared one times out at 60s
        // (see HeadlessUpdateRunner/UpdaterRunner), which these ~71MB
        // installers can exceed on anything slower than a fast connection -
        // same reasoning as Log4omUpdater's dedicated client.
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(8),
        };

        ctx.Log.Line($"Checking {ProjectRssBase}?path=/ for the latest release folder...");
        string rootRss;
        try
        {
            rootRss = await HttpDownloader.GetStringAsync(http, $"{ProjectRssBase}?path=/", ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"WSJT-X Improved Updater FAILED: could not reach the release feed ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var versions = VersionFolderRegex.Matches(rootRss)
            .Select(m => m.Groups["ver"].Value)
            .Distinct()
            .ToList();

        if (versions.Count == 0)
        {
            ctx.Log.Line("WSJT-X Improved Updater FAILED: no release folder found in the release feed");
            return UpdateResult.Failed("No release folder found");
        }

        var latestVer = versions.Aggregate((a, b) => FileVersionHelper.IsNewer(b, a) ? b : a);

        string folderRss;
        try
        {
            folderRss = await HttpDownloader.GetStringAsync(http, $"{ProjectRssBase}?path=/WSJT-X_v{latestVer}", ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"WSJT-X Improved Updater FAILED: could not list files for {latestVer} ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var found = new Dictionary<Variant, (string Url, string Date, string Hash)>();
        foreach (var (candidateVariant, infix, _) in Variants)
        {
            var info = FindVariantFile(folderRss, latestVer, infix);
            if (info.Url is not null) found[candidateVariant] = (info.Url, info.Date!, info.Hash!);
        }

        if (found.Count == 0)
        {
            ctx.Log.Line($"WSJT-X Improved Updater FAILED: no win64 builds found for {latestVer}");
            return UpdateResult.Failed("Download link not found");
        }

        var marker = ReadInstalledBuildMarker();
        Variant variant;
        string latestFull;
        string? current;

        if (marker is { } m)
        {
            variant = m.Variant;
            current = m.Build;

            if (!found.TryGetValue(variant, out var trackedBuild))
            {
                ctx.Log.Line($"WSJT-X Improved Updater FAILED: no {VariantLabel(variant)} win64 build found for {latestVer}");
                return UpdateResult.Failed("Download link not found for tracked variant");
            }
            latestFull = $"{latestVer}.{trackedBuild.Date}";

            if (!ctx.Force && !FileVersionHelper.IsNewer(latestFull, current))
            {
                ctx.Log.Line($"Already up to date ({VariantLabel(variant)} variant, installed {current}, latest {latestFull}).");
                ctx.Log.Line("WSJT-X Improved Updater completed successfully");
                return UpdateResult.UpToDate();
            }
        }
        else
        {
            current = target.Version;

            // Cheap check first, before spending anything on figuring out
            // which variant this is: all three variants share the same
            // X.Y.Z, so if that alone isn't newer than what's installed,
            // nothing needs downloading regardless of which one it turns
            // out to be.
            if (!ctx.Force && !FileVersionHelper.IsNewer(latestVer, current))
            {
                ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latestVer} - variant not yet confirmed, not needed either way).");
                ctx.Log.Line("WSJT-X Improved Updater completed successfully");
                return UpdateResult.UpToDate();
            }

            // An update really is needed - now it's worth knowing which
            // variant, but only for a real run. See the class doc comment
            // for why this is expensive and what it protects against.
            if (!ctx.DryRun)
            {
                var matched = await DetectVariantByHashAsync(http, target.InstallPath!, installDir, found, ctx.Log, ctx.CancellationToken);
                variant = matched ?? Variant.Standard;
                if (matched is null)
                    ctx.Log.Line("Could not determine which GUI variant is installed (no marker yet, and the installed build doesn't match any current release variant - likely an older version) - assuming standard.");
            }
            else
            {
                variant = Variant.Standard;
            }

            if (!found.TryGetValue(variant, out var chosenBuild))
            {
                ctx.Log.Line($"WSJT-X Improved Updater FAILED: no {VariantLabel(variant)} win64 build found for {latestVer}");
                return UpdateResult.Failed("Download link not found for tracked variant");
            }
            latestFull = $"{latestVer}.{chosenBuild.Date}";
        }

        ctx.Log.Line($"New version available: {latestFull} {VariantLabel(variant)} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latestFull} ({VariantLabel(variant)}{(marker is null ? " - variant not yet confirmed, a real run verifies by file hash" : "")}).");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        var chosen = found[variant];
        var downloadUrl = WebUtility.HtmlDecode(chosen.Url.Trim());

        var tempDir = Path.Combine(Path.GetTempPath(), $"WsjtxImprovedUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, Path.GetFileName(new Uri(downloadUrl).AbsolutePath));

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                http, downloadUrl, installerPath, ctx.CancellationToken, perAttemptTimeout: TimeSpan.FromMinutes(8));
            if (!downloadOk)
            {
                ctx.Log.Line($"WSJT-X Improved Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            ctx.Log.Line($"Installing silently to {installDir}...");
            // NSIS's /D= must be the last argument and must not be quoted.
            // No InstallerWindowSuppressor/DesktopShortcutCleaner here (both
            // used by WsjtxUpdater for real WSJT-X's installer) - confirmed
            // live this installer shows no window and drops no shortcut
            // under plain /S, so neither is needed.
            var result = await SilentExeInstaller.RunAsync(
                installerPath,
                new[] { "/S", $"/D={installDir}" },
                ctx.CancellationToken,
                timeout: TimeSpan.FromSeconds(300));

            if (!result.ok)
            {
                ctx.Log.Line($"WSJT-X Improved Updater FAILED: installer exited with code {result.exitCode}");
                return UpdateResult.Failed($"Installer exit code {result.exitCode}");
            }

            WriteInstalledBuildMarker(variant, latestFull);

            ctx.Log.Line($"Updated to {latestVer} (build {chosen.Date}, {VariantLabel(variant)} variant).");
            ctx.Log.Line("WSJT-X Improved Updater completed successfully");
            return UpdateResult.Updated(latestVer, $"build {chosen.Date}, {VariantLabel(variant)}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    private static string InstallDirFor(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        return string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir)!
            : dir;
    }

    private static string VariantLabel(Variant v) => Variants.First(x => x.Variant == v).Label;

    /// <summary>Finds this variant's build in a folder-scoped RSS response by
    /// reading its &lt;media:content url=... filesize=...&gt;&lt;media:hash&gt;
    /// element (url and hash together in one place), not the separate
    /// &lt;link&gt; element - simpler than cross-referencing two elements
    /// across an &lt;item&gt; block for the same result. The infix
    /// ("", "AL_", "widescreen_") sits between "improved_" and "PLUS_"; an
    /// empty infix can't accidentally match the AL/widescreen filenames
    /// since those insert extra characters right there that "PLUS_" would
    /// then fail to immediately follow.</summary>
    private static (string? Url, string? Date, string? Hash) FindVariantFile(string rss, string ver, string infix)
    {
        var pattern = $@"<media:content[^>]*\burl=""(?<url>[^""]*wsjtx-{Regex.Escape(ver)}-win64_improved_{infix}PLUS_(?<date>\d+)\.exe[^""]*)""[^>]*><media:hash algo=""md5"">(?<hash>[0-9a-fA-F]+)</media:hash>";
        var m = Regex.Match(rss, pattern, RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups["url"].Value, m.Groups["date"].Value, m.Groups["hash"].Value) : (null, null, null);
    }

    private static string? ComputeMd5(string path)
    {
        try
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // -------------------------------------------------- bootstrap variant detection

    /// <summary>
    /// Determines which GUI variant is installed by actually installing each
    /// candidate silently to its own throwaway scratch folder and comparing
    /// the resulting bin\wsjtx.exe's hash against the real installed one -
    /// see the class doc comment for why this is the only way (the RSS feed
    /// only publishes the installer's own hash, not the extracted payload's)
    /// and why it needs a registry save/restore around it (this vendor's
    /// NSIS installer keys its uninstall registry entry by product, not by
    /// install path, so a scratch-folder install overwrites the real
    /// install's entry until something restores it). Stops at the first
    /// match rather than trying all three once one succeeds. Returns null
    /// if nothing matched (installed copy predates the current release, or
    /// something failed) - the caller falls back to assuming standard.
    /// </summary>
    private static async Task<Variant?> DetectVariantByHashAsync(
        HttpClient http,
        string installedExePath,
        string realInstallDir,
        Dictionary<Variant, (string Url, string Date, string Hash)> found,
        UpdaterLog log,
        CancellationToken ct)
    {
        var installedHash = ComputeMd5(installedExePath);
        if (installedHash is null) return null;

        var regKey = FindRegistryKeyForInstallDir(realInstallDir);
        var savedValues = regKey is { } rk ? SaveRegistryValues(rk.Hive, rk.KeyPath) : null;

        try
        {
            foreach (var (candidateVariant, _, label) in Variants)
            {
                if (ct.IsCancellationRequested) break;
                if (!found.TryGetValue(candidateVariant, out var info)) continue;

                var scratchDir = Path.Combine(Path.GetTempPath(), $"WsjtxImprovedProbe_{Guid.NewGuid():N}");
                var probeInstallerPath = scratchDir + ".exe";

                try
                {
                    log.Line($"Checking whether the installed copy is the {label} variant...");
                    // attempts: 1 - trying three candidate variants already
                    // gives natural fan-out; retrying the same stalled/
                    // throttled mirror four times each (the default) would
                    // make a bad connection take forever across all three.
                    var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                        http, info.Url, probeInstallerPath, ct, attempts: 1, perAttemptTimeout: TimeSpan.FromMinutes(3));
                    if (!downloadOk)
                    {
                        log.Line($"Could not download the {label} variant to check it ({downloadError}) - trying the next one.");
                        continue;
                    }

                    var installResult = await SilentExeInstaller.RunAsync(
                        probeInstallerPath, new[] { "/S", $"/D={scratchDir}" }, ct, timeout: TimeSpan.FromSeconds(300));
                    if (!installResult.ok) continue;

                    var probeExe = Path.Combine(scratchDir, "bin", "wsjtx.exe");
                    var probeHash = ComputeMd5(probeExe);
                    if (probeHash is not null && string.Equals(probeHash, installedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        log.Line($"Match: installed copy is the {label} variant.");
                        return candidateVariant;
                    }
                }
                finally
                {
                    try { if (Directory.Exists(scratchDir)) Directory.Delete(scratchDir, recursive: true); } catch (Exception) { }
                    try { if (File.Exists(probeInstallerPath)) File.Delete(probeInstallerPath); } catch (Exception) { }
                }
            }

            return null;
        }
        finally
        {
            if (regKey is { } rk2 && savedValues is not null)
                RestoreRegistryValues(rk2.Hive, rk2.KeyPath, savedValues);
        }
    }

    private static readonly (RegistryKey Hive, string BasePath)[] UninstallRoots =
    {
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    private static (RegistryKey Hive, string KeyPath)? FindRegistryKeyForInstallDir(string installDir)
    {
        try
        {
            foreach (var (hive, basePath) in UninstallRoots)
            {
                using var uninstallKey = hive.OpenSubKey(basePath);
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey?.GetValue("UninstallString") is not string uninstallString) continue;

                    if (uninstallString.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                        return (hive, $@"{basePath}\{subKeyName}");
                }
            }
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static Dictionary<string, object>? SaveRegistryValues(RegistryKey hive, string keyPath)
    {
        try
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key is null) return null;

            var values = new Dictionary<string, object>();
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is { } val) values[name] = val;
            }
            return values;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void RestoreRegistryValues(RegistryKey hive, string keyPath, Dictionary<string, object> values)
    {
        try
        {
            using var key = hive.CreateSubKey(keyPath, writable: true);
            if (key is null) return;
            foreach (var (name, val) in values) key.SetValue(name, val);
        }
        catch (Exception)
        {
        }
    }

    // -------------------------------------------------- installed-build marker

    private readonly record struct InstalledMarker(Variant Variant, string Build);

    private static string MarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HamProgramAutoUpdate", "wsjtx_improved_build.txt");

    private static InstalledMarker? ReadInstalledBuildMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;

            var parts = File.ReadAllText(MarkerPath).Trim().Split('|', 2);
            if (parts.Length != 2) return null;

            var entry = Variants.FirstOrDefault(x => string.Equals(x.Label, parts[0], StringComparison.OrdinalIgnoreCase));
            return entry.Label is null ? null : new InstalledMarker(entry.Variant, parts[1]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteInstalledBuildMarker(Variant variant, string build)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, $"{VariantLabel(variant)}|{build}");
        }
        catch (Exception)
        {
            // Best-effort: worst case, the next check falls back to
            // hash-matching (or "assume standard") again instead of
            // remembering the exact variant/build we just installed.
        }
    }
}
