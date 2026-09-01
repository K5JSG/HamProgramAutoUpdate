using System.Text.RegularExpressions;
using Microsoft.Win32;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// Ported from HRD_Update_Script.ps1 - the most defensive of the ten
/// originals. Detection has a three-tier fallback (registry uninstall entry,
/// then App Paths, then a directory scan under "HRD Software"); the
/// installer's engine isn't known in advance, so this sniffs it and, if that
/// fails, tries every known silent-flag set in turn until the installed
/// version actually changes.
/// </summary>
public sealed class HrdUpdater : UpdaterBase
{
    private const string PageUrl = "https://www.hamradiodeluxe.com/downloads/";

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>]*setupHRD[^\s""'<>]*\.exe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionTextRegex = new(
        @"Ham Radio Deluxe[^<]*?v(?<ver>\d+\.\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FilenameDigitsRegex = new(
        @"setupHRD_?(?<digits>\d{6,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public HrdUpdater() : base("hrd", "Ham Radio Deluxe", DetectHrd)
    {
    }

    private static DetectedTarget DetectHrd()
    {
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring("Ham Radio Deluxe");
        if (entry is not null)
        {
            var exe = entry.InstallLocation is { } loc ? FindHrdExeIn(loc) : null;
            return DetectedTarget.Found(exe ?? entry.InstallLocation, entry.DisplayVersion);
        }

        foreach (var appName in new[] { "HRDLogbook.exe", "Ham Radio Deluxe.exe" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{appName}");
                if (key?.GetValue(null) is string exePath && File.Exists(exePath))
                    return DetectedTarget.Found(exePath, FileVersionHelper.ReadFileVersion(exePath));
            }
            catch (Exception) { }
        }

        foreach (var dir in new[] { @"C:\Program Files\HRD Software", @"C:\Program Files (x86)\HRD Software" })
        {
            var exe = FindHrdExeIn(dir);
            if (exe is not null) return DetectedTarget.Found(exe, FileVersionHelper.ReadFileVersion(exe));
        }

        return DetectedTarget.NotFound;
    }

    private static string? FindHrdExeIn(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(p =>
                    Path.GetFileName(p).Contains("HRD", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(p).Contains("Ham Radio Deluxe", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx, closingName: "HRD");

        ctx.Log.Line($"Checking {PageUrl} for the latest version...");
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, PageUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"HRD Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var urlMatch = UrlRegex.Match(html);
        if (!urlMatch.Success)
        {
            ctx.Log.Line("HRD Updater FAILED: could not find a download link on the downloads page");
            return UpdateResult.Failed("Download link not found");
        }

        var downloadUrl = urlMatch.Value;
        var latest = VersionTextRegex.Match(html) is { Success: true } vm
            ? vm.Groups["ver"].Value
            : VersionFromFilename(downloadUrl);

        if (latest is null)
        {
            ctx.Log.Line("HRD Updater FAILED: could not determine the latest version number");
            return UpdateResult.Failed("Version not found");
        }

        var current = target.Version;
        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("HRD Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        if (!AppInfo.IsElevated)
        {
            ctx.Log.Line("HRD Updater FAILED: administrator privileges are required to install HRD updates");
            return UpdateResult.Failed("Not elevated");
        }

        CloseIfRunning(target.InstallPath, ctx);

        var tempDir = Path.Combine(Path.GetTempPath(), $"HrdUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, "setupHRD.exe");

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, downloadUrl, installerPath, ctx.CancellationToken, minSizeBytes: 10_000_000);
            if (!downloadOk || !HttpDownloader.LooksLikeExe(installerPath))
            {
                ctx.Log.Line($"HRD Updater FAILED: download failed or did not look like a real installer ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download did not look like a real installer");
            }

            var kind = SilentExeInstaller.Sniff(installerPath);
            ctx.Log.Line($"Detected installer type: {kind}. Installing silently...");

            var argSets = kind == InstallerKind.Unknown
                ? SilentExeInstaller.AllKnownSilentArgs().ToList()
                : new List<string[]> { SilentExeInstaller.DefaultSilentArgs(kind) };

            var installed = false;
            foreach (var args in argSets)
            {
                var (ok, exitCode) = await SilentExeInstaller.RunAsync(
                    installerPath, args, ctx.CancellationToken, timeout: TimeSpan.FromSeconds(300));

                var newVersion = target.InstallPath is { } p ? FileVersionHelper.ReadFileVersion(p) : null;
                if (ok && FileVersionHelper.IsNewer(newVersion, current))
                {
                    installed = true;
                    break;
                }

                ctx.Log.Line($"Silent flags {string.Join(' ', args)} did not result in a version change (exit code {exitCode}); trying next set.");
            }

            if (!installed)
            {
                ctx.Log.Line("HRD Updater FAILED: none of the known silent install flag sets resulted in a version change");
                return UpdateResult.Failed("Silent install did not change the installed version");
            }

            ctx.Log.Line($"Successfully installed version {latest}");
            ctx.Log.Line("HRD Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    private static void CloseIfRunning(string? installPath, UpdaterContext ctx)
    {
        try
        {
            foreach (var proc in ProcessFinder.FindByExePath(installPath))
            {
                ctx.Log.Line($"Closing running {proc.ProcessName} (PID {proc.Id}) before installing...");
                try
                {
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);
                }
                catch (Exception) { }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception) { }
    }

    private static string? VersionFromFilename(string url)
    {
        var match = FilenameDigitsRegex.Match(url);
        if (!match.Success) return null;

        var digits = match.Groups["digits"].Value;
        if (digits.Length < 4) return null;

        return $"{digits[0]}.{digits[1]}.{digits[2]}.{digits[3..]}";
    }
}
