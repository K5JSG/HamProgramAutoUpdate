using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// New addition - Log4OM never had a legacy Python updater script to port
/// from. Its download page (log4om.com/download) ships two independent
/// flavors of the same release: a normal Inno Setup installer ("full") and
/// a self-contained, no-installer ("portable") zip. A PC can have either
/// one; this updater detects which and downloads/installs that same
/// flavor - it never switches a machine from one to the other.
///
/// Confirmed live by downloading and inspecting both zips (2.41.0.0):
///  - The full installer's install directory
///    (Program Files (x86)\IW3HMH\Log4OM NextGen on this PC) has no
///    "config" folder at all - Log4OM keeps that flavor's settings/database
///    outside Program Files (Documents\LOG4OM2 etc.), so a normal silent
///    Inno Setup upgrade in place is safe as-is, same as every other
///    Inno-based updater here (GridTracker, BktTimeSync).
///  - The portable zip's "config" folder (config\config.ini, plus the real
///    QSO log at config\Log4OMNG.SQLite and two large bundled reference
///    databases) is the ONLY copy of that data - it ships fresh inside
///    every release zip. Extracting a new release straight over an old
///    portable install would silently replace a real operator's logged
///    QSOs with the zip's empty defaults, so the portable install path
///    below never overwrites an existing "config" folder.
///
/// log4om.com sits behind Cloudflare, which 403s the download page for any
/// request with no User-Agent header at all (confirmed live - curl with
/// -H "User-Agent:" gets a 403, the same page with any non-empty UA gets a
/// 200). HttpClient sends no User-Agent by default, so this - unlike every
/// other updater here - can't just reuse HttpDownloader.GetStringAsync
/// as-is; the page fetch below builds its own request with one instead.
///
/// The full installer also silently launches a bundled dependency
/// installer (OmniRigSetup.exe, as of 2.41.0.0) mid-install with no silent
/// flags of its own, and a plain "did the process I started exit" check
/// can report success while that - or the real install itself - is still
/// running in the background. Both confirmed live; see
/// RunFullInstallerAsync's and HandleBundledInstallerAsync's doc comments
/// for the full story - short version, a bundled installer that isn't
/// already present gets driven silently too rather than either hung on
/// (nobody to click through it on an unattended run) or silently skipped
/// (the operator may have actually needed it).
/// </summary>
public sealed class Log4omUpdater : UpdaterBase
{
    private const string PageUrl = "https://www.log4om.com/download/";
    private const string UserAgent = "HamProgramAutoUpdate";

    private static readonly Regex CurrentVersionRegex = new(
        @"Current release\s+(?<ver>\d+\.\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Deliberately excludes "_portable.zip" links: the version group must be
    // followed immediately by ".zip", which "..._portable.zip" never is.
    private static readonly Regex FullZipRegex = new(
        @"href=[""'](?<url>https?://[^""']+/Log4OM2_(?<ver>\d+_\d+_\d+_\d+)\.zip)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PortableZipRegex = new(
        @"href=[""'](?<url>https?://[^""']+/Log4OM2_(?<ver>\d+_\d+_\d+_\d+)_portable\.zip)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum Flavor { Full, Portable }

    private sealed record Target(Flavor Flavor, string ExePath, string InstallDir, string? Version);

    public Log4omUpdater() : base("log4om", "Log4OM", () =>
    {
        var t = Detect();
        return t is null ? DetectedTarget.NotFound : DetectedTarget.Found(t.ExePath, t.Version);
    })
    {
    }

    /// <summary>Full flavor first - Inno Setup always registers a normal
    /// uninstall entry. Portable only via UpdaterSettings.Log4omPortablePath,
    /// since a portable copy has no installer to register it anywhere and
    /// can live wherever its owner chose to unzip it.</summary>
    private static Target? Detect()
    {
        var entry = RegistryUninstallLookup.FindByDisplayNameSubstring("Log4OM");
        if (entry is not null)
        {
            var installDir = entry.InstallLocation ?? "";
            // Don't build a bogus bare "L4ONG.exe" (a relative path with no
            // real directory) if InstallLocation wasn't populated - that
            // would never File.Exists, so ExePath's post-install version
            // check would fail forever even after a genuinely successful
            // update. Fall back to the empty installDir instead, same as
            // this codebase's other updaters do when an exe can't be
            // resolved (e.g. PotaUpdater's `exe ?? installDir`).
            var exePath = string.IsNullOrWhiteSpace(installDir) ? installDir : Path.Combine(installDir, "L4ONG.exe");
            var version = File.Exists(exePath) ? FileVersionHelper.ReadFileVersion(exePath) : entry.DisplayVersion;
            return new Target(Flavor.Full, exePath, installDir, version);
        }

        var portableDir = UpdaterSettings.Load().Log4omPortablePath;
        if (string.IsNullOrWhiteSpace(portableDir)) return null;

        var portableExe = Path.Combine(portableDir, "L4ONG.exe");
        return File.Exists(portableExe)
            ? new Target(Flavor.Portable, portableExe, portableDir, FileVersionHelper.ReadFileVersion(portableExe))
            : null;
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var target = Detect();
        if (target is null) return SkipNotInstalled(ctx);

        var flavorName = target.Flavor == Flavor.Portable ? "portable" : "full";
        ctx.Log.Line($"Detected the {flavorName} install at {target.InstallDir}.");

        // A dedicated client, not ctx.Http: the shared one times out at 60s
        // (see HeadlessUpdateRunner/UpdaterRunner), which the full installer
        // (~115MB) or portable zip (~165MB) - far bigger than any other
        // program tracked here - can easily exceed on anything slower than
        // a fast connection. Also carries the User-Agent the page needs.
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(8),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        ctx.Log.Line($"Checking {PageUrl} for the latest version...");
        string html;
        try
        {
            html = await http.GetStringAsync(PageUrl, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"Log4OM Updater FAILED: could not reach the download page ({ex.Message})");
            return UpdateResult.Failed(ex.Message);
        }

        var currentMatch = CurrentVersionRegex.Match(html);
        if (!currentMatch.Success)
        {
            ctx.Log.Line("Log4OM Updater FAILED: could not find the current version on the download page");
            return UpdateResult.Failed("Version not found on download page");
        }

        var latest = currentMatch.Groups["ver"].Value;
        var current = target.Version;

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
        {
            ctx.Log.Line($"Already up to date (installed {current ?? "unknown"}, latest {latest}).");
            ctx.Log.Line("Log4OM Updater completed successfully");
            return UpdateResult.UpToDate();
        }

        ctx.Log.Line($"New version available: {latest} (installed: {current ?? "unknown"})");
        if (ctx.DryRun)
        {
            ctx.Log.Line($"Dry run - would download and install {latest}.");
            ctx.Log.Line("Update Check Finished (dry run).");
            return UpdateResult.UpToDate("Dry run");
        }

        var linkRegex = target.Flavor == Flavor.Portable ? PortableZipRegex : FullZipRegex;
        var downloadUrl = linkRegex.Matches(html)
            .Where(m => m.Groups["ver"].Value.Replace('_', '.') == latest)
            .Select(m => m.Groups["url"].Value)
            .FirstOrDefault();

        if (downloadUrl is null)
        {
            ctx.Log.Line($"Log4OM Updater FAILED: could not find a {flavorName} download link for version {latest}");
            return UpdateResult.Failed("Download link not found");
        }

        if (IsRunning(target.ExePath))
        {
            ctx.Log.Line("Log4OM Updater: the program is currently running - postponing this update.");
            ctx.Log.Line("Log4OM Updater completed successfully");
            return UpdateResult.Skipped("Program is running");
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"Log4omUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "log4om.zip");

        try
        {
            ctx.Log.Line($"Downloading {downloadUrl} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                http, downloadUrl, zipPath, ctx.CancellationToken, minSizeBytes: 1_000_000,
                perAttemptTimeout: TimeSpan.FromMinutes(8));
            if (!downloadOk)
            {
                ctx.Log.Line($"Log4OM Updater FAILED: download failed ({downloadError})");
                return UpdateResult.Failed(downloadError ?? "Download failed");
            }

            ctx.Log.Line("Extracting...");
            var extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            if (target.Flavor == Flavor.Full)
            {
                var installerExe = Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (installerExe is null)
                {
                    ctx.Log.Line("Log4OM Updater FAILED: no installer exe found in the downloaded zip");
                    return UpdateResult.Failed("Installer not found in zip");
                }

                ctx.Log.Line("Installing silently (Inno Setup)...");
                var (installOk, installError) = await RunFullInstallerAsync(installerExe, tempDir, ctx.Log, ctx.CancellationToken);
                if (!installOk)
                {
                    ctx.Log.Line($"Log4OM Updater FAILED: {installError}");
                    return UpdateResult.Failed(installError ?? "Install failed");
                }
            }
            else
            {
                // The zip's payload sits under a single top-level folder
                // ("Portable\") rather than at the zip root.
                var payloadDir = Directory.GetDirectories(extractDir).FirstOrDefault() ?? extractDir;

                ctx.Log.Line("Copying into the portable install (existing config/database preserved)...");
                InstallPortable(payloadDir, target.InstallDir);
            }

            // Trust the file on disk, not a process exit code - see
            // RunFullInstallerAsync's doc comment for why a "the installer
            // process exited" signal alone already proved unreliable here.
            var installedVersion = FileVersionHelper.ReadFileVersion(target.ExePath);
            if (string.IsNullOrEmpty(installedVersion) || FileVersionHelper.IsNewer(latest, installedVersion))
            {
                ctx.Log.Line($"Log4OM Updater FAILED: install finished but {target.ExePath} still reports version {installedVersion ?? "unknown"}, not {latest}");
                return UpdateResult.Failed("Installed version does not match latest after install");
            }

            ctx.Log.Line($"Updated to {latest}.");
            ctx.Log.Line("Log4OM Updater completed successfully");
            return UpdateResult.Updated(latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Runs Log4OM's Inno Setup installer silently, working around a real,
    /// live-confirmed problem: as of 2.41.0.0, its script bundles a
    /// dependency's own installer (OmniRigSetup.exe) as a [Run] step with
    /// no silent flags passed through, so it opens a real, interactive
    /// "Setup - Omni-Rig" wizard that our /VERYSILENT etc. never reaches
    /// and that never dismisses itself on its own. What happens to it is
    /// decided by HandleBundledInstallerAsync - installed already, decline
    /// the redundant reinstall; not installed, install it silently
    /// ourselves instead of leaving either an unattended hang (nobody to
    /// click through it) or a silent no-op (the operator actually needed
    /// it and now doesn't have it).
    ///
    /// This also fixes a second, independent problem the same investigation
    /// found: Inno Setup's stub relaunches itself as a same-named ".tmp"
    /// copy that does the real work, and the ORIGINAL process (the only one
    /// SilentExeInstaller.RunAsync's plain Process.WaitForExitAsync was
    /// watching) exits early once it hands off - so a plain "did the
    /// process I started exit" check declares success while the real
    /// install (and, that one time, the bundled OmniRig wizard) is still
    /// running completely unsupervised in the background. Confirmed live:
    /// a real run reported "Updated to 2.41.0.0" after 29 seconds even
    /// though the OmniRig wizard, when it appears, doesn't even show up
    /// until ~45-60 seconds in. This polls for every process still running
    /// under our own installer's name instead of trusting a single exit
    /// code, so it can't return early the same way.
    /// </summary>
    private static async Task<(bool ok, string? error)> RunFullInstallerAsync(
        string installerExe, string tempDir, UpdaterLog log, CancellationToken ct)
    {
        var ownNamePrefix = Path.GetFileNameWithoutExtension(installerExe);
        // Inno Setup always stages a [Run] entry's target under its own
        // private "%TEMP%\is-XXXXX.tmp\" folder before launching it -
        // confirmed live (that's exactly where OmniRigSetup.exe showed up).
        // Scoping to this prefix means we can never act on a process that
        // isn't something Inno itself just extracted. TMP/TEMP below are
        // redirected to AppPaths.TempDir for this child process only (see
        // that override), so this matches Inno's actual staging location
        // rather than the shared Windows temp folder.
        var innoStagingPrefix = Path.Combine(AppPaths.TempDir, "is-");
        // One decision per bundled installer, even though it (like our own
        // outer one) may show up as more than one process over time via the
        // same self-relaunch-as-.tmp pattern - keyed by its staging folder,
        // which is the same across all of its own stages.
        var handledStagingDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            FileName = installerExe,
            WorkingDirectory = Path.GetDirectoryName(installerExe) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" })
            psi.ArgumentList.Add(a);
        // UseShellExecute = false gives this child process its own
        // environment block seeded from ours - setting just these two keys
        // overrides Inno's %TEMP%\is-XXXXX.tmp self-extraction target
        // without touching this process's own TMP/TEMP.
        psi.Environment["TMP"] = AppPaths.TempDir;
        psi.Environment["TEMP"] = AppPaths.TempDir;

        using (var proc = Process.Start(psi))
        {
            if (proc is null) return (false, "Failed to start installer");
        }

        // The 500ms decision loop below is fast enough to never actually
        // hang on a bundled installer, but not fast enough to stop one from
        // being visible for a moment first - a window can render well
        // inside 500ms. Confirmed live: users see it flash open even though
        // it's declined/handled correctly a moment later. This runs a much
        // faster reactive hide (see HideForeignWindowsLoopAsync - the same
        // EnumWindows/ShowWindow mechanism InstallerWindowSuppressor already
        // uses for GridTracker, just polled tighter) alongside the decision
        // loop so it never gets the chance to sit on screen, while the
        // slower loop still does the actual decide-and-act work against the
        // (now hidden, still running) process underneath.
        using var hideCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hideTask = HideForeignWindowsLoopAsync(ownNamePrefix, innoStagingPrefix, hideCts.Token);

        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(300);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, ct);

                var anyOfOursStillRunning = false;
                foreach (var p in Process.GetProcesses())
                {
                    using (p)
                    {
                        if (p.ProcessName.StartsWith(ownNamePrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            anyOfOursStillRunning = true;
                            continue;
                        }

                        string? path;
                        try { path = p.MainModule?.FileName; } catch (Exception) { path = null; }
                        if (path is null || p.HasExited) continue;
                        if (!path.StartsWith(innoStagingPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                        var stagingDir = Path.GetDirectoryName(path) ?? path;
                        if (!handledStagingDirs.Add(stagingDir)) continue;

                        await HandleBundledInstallerAsync(path, stagingDir, tempDir, log, ct);
                    }
                }

                if (!anyOfOursStillRunning) return (true, null);
            }

            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (!p.ProcessName.StartsWith(ownNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
                }
            }
            return (false, "Timed out after 300s waiting for the installer to finish");
        }
        finally
        {
            hideCts.Cancel();
            try { await hideTask; } catch (Exception) { }
        }
    }

    /// <summary>Every ~5ms, hides (not closes - the decision loop above
    /// still needs the process alive to inspect and act on) any visible
    /// window owned by a process staged under Inno's private "is-*" temp
    /// folder that isn't our own installer. Same EnumWindows-and-ShowWindow
    /// mechanism as InstallerWindowSuppressor, just matched by the owning
    /// process's path instead of the window's title text - a title
    /// substring would mean hardcoding "Setup - Omni-Rig" (or whatever a
    /// future bundled dependency is called) in advance; matching by where
    /// Inno staged it works for any of them without knowing the name.
    /// InstallerWindowSuppressor itself polls every 30ms - confirmed live
    /// that isn't tight enough here (a real run still showed the OmniRig
    /// wizard, titled "Setup - Omni-Rig", visible for ~47ms with a 30ms
    /// poll); EnumWindows plus a handful of process lookups is cheap enough
    /// to poll much faster than that for the few seconds this actually
    /// runs.</summary>
    private static async Task HideForeignWindowsLoopAsync(string ownNamePrefix, string innoStagingPrefix, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                NativeMethods.EnumWindows((hWnd, _) =>
                {
                    if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                    NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                    if (pid == 0) return true;

                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        if (proc.ProcessName.StartsWith(ownNamePrefix, StringComparison.OrdinalIgnoreCase)) return true;

                        var path = proc.MainModule?.FileName;
                        if (path is not null && path.StartsWith(innoStagingPrefix, StringComparison.OrdinalIgnoreCase))
                            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
                    }
                    catch (Exception) { }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { }

            try { await Task.Delay(5, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static class NativeMethods
    {
        public const int SW_HIDE = 0;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }

    /// <summary>
    /// A [Run]-launched process Log4OM's installer didn't ask permission
    /// for. Captures a copy of it first (its own staging folder may not
    /// survive it being killed), then: if the product it installs is
    /// already present, declines the redundant reinstall; otherwise drives
    /// the captured copy silently ourselves, so an unattended run ends with
    /// the same outcome a person clicking through it would have chosen
    /// (install what's missing) instead of either hanging on it or quietly
    /// skipping it.
    ///
    /// Confirmed live end-to-end for OmniRig specifically: its own
    /// installer is also Inno Setup ("This installation was built with
    /// Inno Setup." in its version resource, same as Log4OM's), its
    /// ProductName resource reads "OmniRig" (no hyphen) while it registers
    /// itself in Programs and Features as "Omni-Rig 1.20" (with one) - a
    /// plain substring match against that would wrongly conclude "not
    /// installed" on a PC where it demonstrably is, hence
    /// RegistryUninstallLookup.FindByNormalizedProductName instead of
    /// FindByDisplayNameSubstring here - and /VERYSILENT /SUPPRESSMSGBOXES
    /// /NORESTART against the captured copy installs it with no window and
    /// no leftover process, verified against this PC's real (already
    /// present) OmniRig install.
    /// </summary>
    private static async Task HandleBundledInstallerAsync(
        string exePath, string stagingDir, string tempDir, UpdaterLog log, CancellationToken ct)
    {
        string productName = "";
        string? capturedCopyPath = null;
        try
        {
            productName = FileVersionInfo.GetVersionInfo(exePath).ProductName?.Trim() ?? "";
            capturedCopyPath = Path.Combine(tempDir, $"bundled_{Guid.NewGuid():N}_{Path.GetFileName(exePath)}");
            File.Copy(exePath, capturedCopyPath, overwrite: true);
        }
        catch (Exception) { }

        var displayName = productName.Length > 0 ? productName : Path.GetFileNameWithoutExtension(exePath);

        KillAllUnder(stagingDir);

        if (capturedCopyPath is null || !File.Exists(capturedCopyPath))
        {
            log.Line($"Log4OM installer tried to launch an unexpected bundled installer ('{displayName}') that couldn't be captured for inspection - declining it.");
            return;
        }

        var alreadyInstalled = productName.Length > 0 && RegistryUninstallLookup.FindByNormalizedProductName(productName) is not null;
        if (alreadyInstalled)
        {
            log.Line($"Log4OM installer tried to launch a bundled installer for '{displayName}', which is already installed - declining the redundant reinstall.");
            return;
        }

        log.Line($"Log4OM installer bundles '{displayName}', which isn't installed on this PC - installing it silently too.");
        var ok = await RunInnoSilentlyAsync(capturedCopyPath, TimeSpan.FromSeconds(120), ct);
        log.Line(ok
            ? $"'{displayName}' installed successfully."
            : $"'{displayName}' did not finish installing within the expected time - continuing with the Log4OM update regardless.");
    }

    /// <summary>Runs an Inno Setup installer with the standard silent flags
    /// and polls by process name until nothing matching it is left running,
    /// same as RunFullInstallerAsync and for the same reason (a plain exit
    /// check is not trustworthy against Inno's self-relaunch-as-.tmp
    /// pattern) - deliberately not reused as the same method, since this
    /// one is for a bundled dependency and has no bundled-installer
    /// interception of its own to do while it waits.</summary>
    private static async Task<bool> RunInnoSilentlyAsync(string installerExe, TimeSpan timeout, CancellationToken ct)
    {
        var ownNamePrefix = Path.GetFileNameWithoutExtension(installerExe);
        var psi = new ProcessStartInfo
        {
            FileName = installerExe,
            WorkingDirectory = Path.GetDirectoryName(installerExe) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["TMP"] = AppPaths.TempDir;
        psi.Environment["TEMP"] = AppPaths.TempDir;
        foreach (var a in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" })
            psi.ArgumentList.Add(a);

        using (var proc = Process.Start(psi))
        {
            if (proc is null) return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);

            var stillRunning = false;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.ProcessName.StartsWith(ownNamePrefix, StringComparison.OrdinalIgnoreCase) && !p.HasExited)
                        stillRunning = true;
                }
            }
            if (!stillRunning) return true;
        }
        return false;
    }

    /// <summary>Kills every currently-running process whose main module
    /// path sits under <paramref name="stagingDir"/> - scoped this way
    /// (rather than by process name) so it correctly catches every stage of
    /// Inno's own self-relaunch-as-.tmp pattern without needing to know its
    /// name in advance.</summary>
    private static void KillAllUnder(string stagingDir)
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path is not null && path.StartsWith(stagingDir, StringComparison.OrdinalIgnoreCase) && !p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch (Exception) { }
            }
        }
    }

    private static bool IsRunning(string exePath) => ProcessFinder.FindByExePath(exePath).Length > 0;

    // -------------------------------------------------- portable install

    /// <summary>
    /// Copies the freshly extracted release over the existing portable
    /// install, except an already-existing "config" folder - see the class
    /// doc comment for why that folder can never be blindly replaced. Backs
    /// up the (non-config) files it is about to touch first and restores
    /// them if the copy fails partway, so a disk-full or permission error
    /// can't leave a broken half-updated app behind.
    /// </summary>
    private static void InstallPortable(string source, string installDir)
    {
        var backupDir = installDir.TrimEnd('\\', '/') + ".backup";
        try
        {
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            CopyExceptConfig(installDir, backupDir);

            try
            {
                CopyExceptConfig(source, installDir);

                // First-time deploy only (see class doc comment - config
                // ships inside every release zip, so a real existing
                // install already has one and this normally no-ops).
                var newConfigDir = Path.Combine(source, "config");
                var existingConfigDir = Path.Combine(installDir, "config");
                if (!Directory.Exists(existingConfigDir) && Directory.Exists(newConfigDir))
                    DirectoryCopy.CopyAll(newConfigDir, existingConfigDir);
            }
            catch (Exception updateEx)
            {
                try
                {
                    CopyExceptConfig(backupDir, installDir);
                }
                catch (Exception restoreEx)
                {
                    // Both the update AND the restore-from-backup failed -
                    // surface both rather than letting the restore failure
                    // silently replace the original error the caller most
                    // needs to see.
                    throw new AggregateException(
                        "Log4OM portable update failed and restoring the pre-update backup also failed - " +
                        "the install directory may be left in a partially-updated state.",
                        updateEx, restoreEx);
                }
                throw;
            }
        }
        finally
        {
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Copies everything under <paramref name="source"/> into
    /// <paramref name="dest"/> except a top-level "config" subfolder.</summary>
    private static void CopyExceptConfig(string source, string dest)
    {
        if (!Directory.Exists(source)) return;

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar, 2)[0].Equals("config", StringComparison.OrdinalIgnoreCase))
                continue;

            var destFile = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: true);
        }
    }

}
