using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// RigExpert AntScope2. RigExpert's product pages sit behind Cloudflare
/// (403 to anything that isn't a browser), but the vendor's own file folder
/// for the Windows build is a plain directory listing that answers normally:
/// https://rigexpert.com/files/software/Antscope/antscope2forwindows/ holds
/// the current "SetupREAntScope2-x.y.z.exe" (older ones are in its Old/
/// subfolder, which is ignored). The installer is NSIS (silent /S) and
/// installs over the existing copy under Program Files (x86). The exe's own
/// file version is 0.0.0.0, so the installed version comes from the
/// Add/Remove Programs entry ("RigExpert AntScope2 1.4.12").
/// </summary>
public sealed class AntScope2Updater : UpdaterBase
{
    private const string FolderUrl = "https://rigexpert.com/files/software/Antscope/antscope2forwindows/";
    private const string ProductName = "RigExpert AntScope2";

    private static readonly Regex InstallerRegex = new(
        @"href=[""'](?<file>SetupREAntScope2-(?<v>\d+\.\d+\.\d+)\.exe)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AntScope2Updater() : base("antscope2", "AntScope2", DetectAntScope2)
    {
    }

    private static DetectedTarget DetectAntScope2()
    {
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring(ProductName);
        if (entry is null) return DetectedTarget.NotFound;

        // DisplayVersion is usually present; the version is also the tail of
        // the DisplayName ("RigExpert AntScope2 1.4.12") if it isn't.
        var version = entry.DisplayVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            var tail = Regex.Match(entry.DisplayName, @"(\d+\.\d+\.\d+)\s*$");
            if (tail.Success) version = tail.Groups[1].Value;
        }

        string? exe = null;
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        })
        {
            var candidate = Path.Combine(root, ProductName, "AntScope2.exe");
            if (File.Exists(candidate)) { exe = candidate; break; }
        }

        return DetectedTarget.Found(exe ?? entry.InstallLocation, version);
    }

    /// <summary>True if the Visual C++ 2015-2022 x64 runtime is installed.</summary>
    private static bool VcRuntimeInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            return key?.GetValue("Installed") is int installed && installed == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Polls for the installer's bundled "VC_redist*" processes and
    /// ends them (logging once) until cancelled.</summary>
    private static async Task CloseRedistributablesAsync(UpdaterLog log, CancellationToken ct)
    {
        var logged = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var proc in System.Diagnostics.Process.GetProcesses())
                {
                    using (proc)
                    {
                        if (!proc.ProcessName.StartsWith("VC_redist", StringComparison.OrdinalIgnoreCase)) continue;
                        try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                        if (!logged)
                        {
                            log.Line("Closed the installer's bundled Visual C++ redistributable (the runtime is already installed).");
                            logged = true;
                        }
                    }
                }
            }
            catch (Exception) { }

            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = DetectTarget();
        if (!target.IsInstalled) return SkipNotInstalled(ctx);

        ctx.Log.Line($"Checking {FolderUrl} for the latest version...");
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, FolderUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"AntScope2 Updater FAILED: could not reach the download folder ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var found = InstallerRegex.Matches(html)
            .Select(m => (file: m.Groups["file"].Value, version: m.Groups["v"].Value))
            .ToList();
        if (found.Count == 0)
        {
            ctx.Log.Line("AntScope2 Updater FAILED: could not find an installer in the download folder");
            return UpdateResult.Failed("Installer not found");
        }

        var best = found.Aggregate((a, b) => FileVersionHelper.IsNewer(b.version, a.version) ? b : a);
        var latest = best.version;
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("AntScope2 Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        var downloadUrl = new Uri(new Uri(FolderUrl), best.file).ToString();

        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download {downloadUrl} and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        if (!AppInfo.IsElevated)
        {
            ctx.Log.Line("AntScope2 Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        // It may be connected to an analyzer mid-measurement - never close it.
        if (ProcessFinder.FindByName("AntScope2").Length > 0)
        {
            ctx.Log.Line("AntScope2 Updater: AntScope2 is currently running - postponing this update.");
            ctx.Log.Line("AntScope2 Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"AntScope2Update_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, best.file);

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, downloadUrl, downloadPath, ctx.CancellationToken,
                perAttemptTimeout: TimeSpan.FromMinutes(15));
            if (!downloadOk || !HttpDownloader.LooksLikeExe(downloadPath))
            {
                ctx.Log.Line($"AntScope2 Updater FAILED: download failed ({downloadError ?? "not a real installer"})");
                return UpdateResult.Failed(downloadError ?? "Download is not a real installer");
            }

            // The installer also bundles the Visual C++ redistributables and,
            // even with /S, launches them as a visible, interactive window
            // (license page + Install button - seen live 2026-10-16, where it
            // failed with "another version is already installed" because the
            // PC already had a newer runtime). That window would sit there
            // until someone closed it (or hang a hidden SYSTEM run), so while
            // the installer runs, close those redistributable processes -
            // but only when the runtime is already on this PC, since then
            // they have nothing to add.
            ctx.Log.Line("Installing silently...");
            using var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            var watcher = VcRuntimeInstalled()
                ? CloseRedistributablesAsync(ctx.Log, watcherCts.Token)
                : Task.CompletedTask;
            (bool installOk, int exitCode) result;
            try
            {
                result = await SilentExeInstaller.RunAsync(
                    downloadPath, new[] { "/S" }, ctx.CancellationToken,
                    timeout: TimeSpan.FromSeconds(600));
            }
            finally
            {
                watcherCts.Cancel();
                try { await watcher; } catch (Exception) { }
            }
            var (installOk, exitCode) = result;
            if (!installOk)
            {
                ctx.Log.Line($"AntScope2 Updater FAILED: installer exited with code {exitCode}");
                return UpdateResult.Failed($"Installer exit code {exitCode}");
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("AntScope2 Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }
}
