using System.Diagnostics;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using Microsoft.Win32;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// N3FJP Software (https://www.n3fjp.com/): one card for the whole suite -
/// AC Log, the ARRL/CQ/state-QSO-party contest logs and the other contest
/// logs (see <see cref="N3fjpCatalog"/>) - the same "one card, several
/// installed modules" shape as RT Systems. Whichever of them are installed
/// are each checked and updated.
///
/// How it works (all confirmed by really installing and uninstalling every
/// installer, 2026-10-18):
/// * Every installer is an Inno Setup exe at n3fjp.com/programs/&lt;file&gt; whose
///   version resource carries the product's version ("6.6.11"); there is no
///   other version feed. So a check downloads that small (3-4 MB) installer
///   and reads its ProductVersion, then compares it with the installed
///   program's Add/Remove Programs DisplayVersion.
/// * Installed programs are found by their Add/Remove Programs entry
///   (Publisher "Affirmatech Inc.", DisplayName from the catalog).
/// * Running the newer installer over the existing install, with no /DIR, is
///   N3FJP's supported upgrade (Inno reuses the previous folder).
///   /MERGETASKS=!desktopicon stops it adding a shared desktop shortcut.
/// * Even with /VERYSILENT the installer launches the program afterwards, so
///   the installer runs on a hidden desktop (like RT Systems' updaters): the
///   program it starts opens there, unseen, and is closed. A program the
///   user already has open is never updated - that run is postponed.
/// * A user's logs and settings live under their own Documents\Affirmatech
///   and HKCU\Software\Affirmatech; the installer never touches them.
/// </summary>
public sealed class N3fjpUpdater : UpdaterBase
{
    // A fresh install/upgrade takes seconds; this only catches a stuck one.
    private static readonly TimeSpan ModuleTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] UninstallRoots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    private sealed record InstalledProgram(N3fjpProgram Program, string? Version, string? InstallDir);

    public N3fjpUpdater() : base("n3fjp", "N3FJP Software", DetectN3fjp)
    {
    }

    /// <summary>Up to ~128 programs could each take a download plus an
    /// install; the default 10-minute cap is too tight for a PC with many.</summary>
    public override TimeSpan? MaxRunTime => TimeSpan.FromMinutes(45);

    private static DetectedTarget DetectN3fjp() =>
        FindInstalled().Count > 0 ? DetectedTarget.Found() : DetectedTarget.NotFound;

    /// <summary>Every catalog program with an Add/Remove Programs entry on
    /// this PC. Cheap (registry reads only), so safe for dashboard refreshes.</summary>
    private static List<InstalledProgram> FindInstalled()
    {
        var byName = N3fjpCatalog.Programs.ToDictionary(p => p.DisplayName, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var basePath in UninstallRoots)
            {
                using var root = Registry.LocalMachine.OpenSubKey(basePath);
                if (root is null) continue;

                foreach (var keyName in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(keyName);
                    if (key?.GetValue("DisplayName") is not string displayName) continue;
                    if (!byName.TryGetValue(displayName, out var program)) continue;
                    if (!string.Equals(key.GetValue("Publisher") as string, N3fjpCatalog.Publisher,
                            StringComparison.OrdinalIgnoreCase)) continue;

                    found[displayName] = new InstalledProgram(
                        program,
                        (key.GetValue("DisplayVersion") as string)?.Trim(),
                        (key.GetValue("InstallLocation") as string)?.Trim());
                }
            }
        }
        catch (Exception)
        {
            // A registry-access failure just means "nothing found" here.
        }

        return found.Values.OrderBy(p => p.Program.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        var installed = FindInstalled();
        if (installed.Count == 0)
        {
            ctx.Log.Line("No N3FJP programs are installed on this PC - skipping.");
            ctx.Log.Line("N3FJP Updater completed successfully");
            return UpdateResult.Skipped("Not installed");
        }

        if (!ctx.DryRun && !AppInfo.IsElevated)
        {
            ctx.Log.Line("N3FJP Updater FAILED: administrator privileges are required to install updates");
            return UpdateResult.Failed("Not elevated");
        }

        ctx.Log.Line($"Found {installed.Count} N3FJP program(s) installed.");

        // Unique per run, same reasoning as RT Systems' and CHIRP's.
        var desktopName = $"n3fjp_bg_desktop_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var hDesktop = ctx.DryRun ? IntPtr.Zero : HiddenDesktopAutomation.CreateHiddenDesktop(desktopName);

        var updated = 0;
        var failed = 0;
        var postponed = 0;

        try
        {
            for (var i = 0; i < installed.Count; i++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();

                var item = installed[i];
                var label = $"[{i + 1}/{installed.Count}] {item.Program.DisplayName}";
                try
                {
                    var outcome = await UpdateOneAsync(ctx, item, label, desktopName, hDesktop);
                    switch (outcome)
                    {
                        case ModuleOutcome.Updated: updated++; break;
                        case ModuleOutcome.Postponed: postponed++; break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Log.Line($"{label}: FAILED ({ex.Message})");
                    failed++;
                }
            }
        }
        finally
        {
            HiddenDesktopAutomation.DestroyDesktop(hDesktop);
        }

        if (failed > 0 && updated == 0)
        {
            ctx.Log.Line($"N3FJP Updater FAILED: {failed} program(s) failed to update");
            return UpdateResult.Failed($"{failed} program(s) failed");
        }

        ctx.Log.Line("N3FJP Updater completed successfully");
        if (updated > 0) return UpdateResult.Updated(null, $"{updated} program(s) updated, {failed} failed");
        if (postponed > 0 && failed == 0) return UpdateResult.Skipped($"{postponed} program(s) running");
        return UpdateResult.UpToDate($"{failed} failed");
    }

    private enum ModuleOutcome { UpToDate, Updated, Postponed }

    private static async Task<ModuleOutcome> UpdateOneAsync(
        UpdaterContext ctx, InstalledProgram item, string label, string desktopName, IntPtr hDesktop)
    {
        var program = item.Program;
        var url = N3fjpCatalog.DownloadBase + program.InstallerFile;

        var tempDir = Path.Combine(AppPaths.TempDir, $"N3fjpUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, program.InstallerFile);

        try
        {
            // The installer is the only place the latest version is published,
            // so it has to be fetched even just to check.
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, url, installerPath, ctx.CancellationToken,
                configureRequest: r => r.Headers.UserAgent.ParseAdd("HamProgramAutoUpdate"));
            if (!downloadOk || !HttpDownloader.LooksLikeExe(installerPath))
                throw new IOException($"could not download {url} ({downloadError ?? "not a real installer"})");

            var latest = FileVersionInfo.GetVersionInfo(installerPath).ProductVersion?.Trim();
            if (string.IsNullOrWhiteSpace(latest))
                throw new InvalidDataException("the installer has no version number");

            var current = item.Version;
            if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current))
            {
                ctx.Log.Line($"{label}: already up to date (installed {current ?? "unknown"}, latest {latest}).");
                return ModuleOutcome.UpToDate;
            }

            ctx.Log.Line($"{label}: new version available: {latest} (installed: {current ?? "unknown"})");
            if (ctx.DryRun)
            {
                ctx.Log.Line($"{label}: dry run - would install {latest}.");
                return ModuleOutcome.UpToDate;
            }

            // Never update a contest log someone has open.
            var exePath = item.InstallDir is { Length: > 0 } dir ? Path.Combine(dir, program.MainExe) : null;
            if (exePath is not null && ProcessFinder.FindByExePath(exePath).Length > 0)
            {
                ctx.Log.Line($"{label}: the program is currently running - deferred to the next run.");
                return ModuleOutcome.Postponed;
            }

            // No /DIR: the installer reuses the existing install folder.
            using var proc = HiddenDesktopAutomation.StartOnDesktop(
                installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=!desktopicon", desktopName);
            using var moduleCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            moduleCts.CancelAfter(ModuleTimeout);
            try
            {
                await proc.WaitForExitAsync(moduleCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                ctx.CancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"the installer did not finish within {ModuleTimeout.TotalMinutes:0} minutes");
            }

            // The installer starts the program itself once it is done.
            await CloseLaunchedProgramAsync(ctx, label, hDesktop, exePath);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"the installer exited with code {proc.ExitCode}");

            // Trust the registry, not the exit code, for what is installed now.
            var now = FindInstalled().FirstOrDefault(p =>
                string.Equals(p.Program.DisplayName, program.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (now?.Version is null || FileVersionHelper.IsNewer(latest, now.Version))
                throw new InvalidOperationException(
                    $"the installer finished but the installed version is still {now?.Version ?? "unknown"}");

            ctx.Log.Line($"{label}: Updated to {now.Version}.");
            return ModuleOutcome.Updated;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Inno's post-install "launch the program" step ran on the hidden desktop,
    /// so nothing is visible - but the program is still running there. Poll
    /// briefly (it can take a moment to start and open a window), closing
    /// whatever is on the hidden desktop and any copy of the program's exe.
    /// </summary>
    private static async Task CloseLaunchedProgramAsync(
        UpdaterContext ctx, string label, IntPtr hDesktop, string? exePath)
    {
        var closed = false;
        for (var i = 0; i < 16; i++)
        {
            try
            {
                foreach (var pid in HiddenDesktopAutomation.WindowOwnerProcessIds(hDesktop))
                {
                    if (pid == Environment.ProcessId) continue;
                    closed |= TryKill(pid);
                }

                if (exePath is not null)
                {
                    foreach (var p in ProcessFinder.FindByExePath(exePath))
                    {
                        using (p) closed |= TryKill(p.Id);
                    }
                }
            }
            catch (Exception) { }

            try { await Task.Delay(500, ctx.CancellationToken); }
            catch (OperationCanceledException) { return; }
        }

        if (closed) ctx.Log.Line($"{label}: closed the copy the installer started.");
    }

    private static bool TryKill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
