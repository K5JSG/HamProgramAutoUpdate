using HamProgramAutoUpdate.Services.Updaters.Programs.Chirp;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// CHIRP is the one program whose vendor page (archive.chirpmyradio.com) sits
/// behind a Cloudflare Turnstile challenge that demands a genuinely
/// interactive click on this particular machine (its Cloudflare trust score
/// is low enough to trigger one - other machines are waved through). An
/// earlier PuppeteerSharp-based C# attempt hit a wall: on a hidden desktop,
/// Chrome's compositor never created the per-iframe RenderWidget windows
/// needed to even post a click to. A working Python version (SeleniumBase CDP
/// mode, headed rather than headless, PostMessage clicks to a runtime-
/// calibrated RenderWidget) got past that - the most likely explanation is
/// simply headed vs. headless Chrome, not anything Python-specific, so this
/// class ports that same technique natively: see
/// Services/Updaters/Programs/Chirp/ChirpCloudflareAutomation.cs for the
/// mechanism (hidden desktop, Win32 click-posting, a minimal hand-rolled CDP
/// client - no Selenium/chromedriver/Python anywhere in this build anymore).
///
/// This is genuinely experimental: unlike every other updater here, its
/// success can only be verified by actually running it against the live,
/// adversarial target, and Cloudflare's heuristics can change under it at any
/// time. ChirpUpdaterSource\ (the old Python implementation this replaced)
/// is left in the repo, unwired, as a fallback to revert to if this stops
/// working and needs re-diagnosing.
/// </summary>
public sealed class ChirpUpdater : UpdaterBase
{
    private static string WorkingDir => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "ChirpUpdaterBinary");

    public ChirpUpdater() : base("chirp", "CHIRP", TargetDetectors.FixedPaths(
        @"C:\Program Files (x86)\CHIRP\chirpwx.exe",
        @"C:\Program Files\CHIRP\chirpwx.exe"))
    {
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        Directory.CreateDirectory(WorkingDir);
        MigrateLegacyDocumentsFolder();

        var target = DetectTarget();
        var stateBefore = ReadState();

        ChirpCloudflareAutomation.Result result;
        try
        {
            result = await ChirpCloudflareAutomation.RunAsync(
                ctx, WorkingDir, stateBefore, target.IsInstalled, ctx.DryRun, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"CHIRP Updater FAILED: {ex.Message}");
            return UpdateResult.Failed(ex.Message);
        }

        if (!result.Success)
        {
            ctx.Log.Line($"CHIRP Updater FAILED: {result.Error}");
            return UpdateResult.Failed(result.Error ?? "Unknown error");
        }

        if (result.InstallerPath is null)
        {
            // Either genuinely up to date, or a dry run stopped short of
            // downloading - see ChirpCloudflareAutomation.RunAsync.
            ctx.Log.Line("CHIRP Updater completed successfully");
            return UpdateResult.UpToDate(result.LatestBuild is { } b ? $"Latest: {b}" : null);
        }

        ctx.Log.Line("Installing silently...");
        using var suppressor = new InstallerWindowSuppressor(new[] { "CHIRP", "Setup", "Installer", "Wizard" });
        suppressor.Start();

        (bool ok, int exitCode) installResult;
        try
        {
            installResult = await SilentExeInstaller.RunAsync(
                result.InstallerPath, new[] { "/S" }, ctx.CancellationToken, timeout: TimeSpan.FromSeconds(300));
        }
        finally
        {
            suppressor.Stop();
        }

        try { File.Delete(result.InstallerPath); } catch (Exception) { }

        if (!installResult.ok)
        {
            ctx.Log.Line($"CHIRP Updater FAILED: installer exited with code {installResult.exitCode}");
            return UpdateResult.Failed($"Installer exit code {installResult.exitCode}");
        }

        WriteState(result.LatestBuild!);
        ctx.Log.Line($"SUCCESS: now on {result.LatestBuild}");
        ctx.Log.Line("CHIRP Updater completed successfully");
        return UpdateResult.Updated(result.LatestBuild!);
    }

    private static string? ReadState()
    {
        var path = Path.Combine(WorkingDir, "last_installed_build.txt");
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception) { return null; }
    }

    private static void WriteState(string build)
    {
        try { File.WriteAllText(Path.Combine(WorkingDir, "last_installed_build.txt"), build); }
        catch (Exception) { }
    }

    /// <summary>
    /// One-time migration for anyone upgrading from a version where the old
    /// external Python exe wrote to Documents\Ham Radio\Chirp Update Script
    /// (hardcoded in that script). Moves the install-state and browser
    /// profile into WorkingDir so this does not look like a first-ever run -
    /// losing the profile in particular would throw away this machine's
    /// Cloudflare trust and could reintroduce a captcha challenge that
    /// otherwise would not appear. Best-effort and idempotent: skipped
    /// entirely once the new files already exist.
    /// </summary>
    private static void MigrateLegacyDocumentsFolder()
    {
        var legacyDir = Path.Combine(UpdaterCatalog.HamRadioDir, "Chirp Update Script");
        if (!Directory.Exists(legacyDir)) return;

        MoveIfNew(Path.Combine(legacyDir, "last_installed_build.txt"), Path.Combine(WorkingDir, "last_installed_build.txt"));
        MoveIfNew(Path.Combine(legacyDir, "bg_profile"), Path.Combine(WorkingDir, "bg_profile"));

        static void MoveIfNew(string oldPath, string newPath)
        {
            if (File.Exists(newPath) || Directory.Exists(newPath)) return;
            try
            {
                if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
                else if (File.Exists(oldPath)) File.Move(oldPath, newPath);
            }
            catch (Exception)
            {
                // Best-effort: worst case is one redundant reinstall or a
                // fresh browser profile next run, not a failure.
            }
        }
    }
}
