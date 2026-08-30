using System.Net.Http;
using HamProgramAutoUpdate.Services;

namespace HamProgramAutoUpdate.Services.Updaters;

/// <summary>
/// Entry points for the dashboard's headless CLI flags:
///   --run-updates            used by the "Program Update Scripts" scheduled
///                             task (see TaskSchedulerService) - runs every
///                             detected program's updater for real.
///   --check-updates          same, but with DryRun set - checks live
///                             versions and logs the result without
///                             downloading or installing anything.
///   --force-update &lt;key&gt;     runs one program's updater for real with
///                             Force set, even if it is already up to date -
///                             the CLI equivalent of the original scripts'
///                             own -Force/--force switches.
/// </summary>
public static class HeadlessUpdateRunner
{
    /// <summary>
    /// Hard ceiling on one program's whole run (check + download + install),
    /// independent of HttpClient's own per-request timeout. Found necessary
    /// in practice: a fresh, unsigned, elevated exe's first outbound HTTPS
    /// call can hang indefinitely on some Windows configurations (proxy
    /// auto-detection stalling) in a way that does not respect
    /// HttpClient.Timeout or a passed CancellationToken, since the hang
    /// happens below the managed network stack. Without this, one bad
    /// program would block the entire nightly scheduled task forever.
    ///
    /// 15 minutes rather than 10: CHIRP's browser-automation path (Cloudflare
    /// challenge wait/clicking plus an in-browser ~20MB download) can
    /// legitimately need most of that on a slow connection - observed
    /// directly, not hung, just slow - and every other program's own
    /// check/download/install finishes in well under a minute regardless, so
    /// this only changes how long a genuinely stuck one is allowed to run.
    /// </summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// UseProxy = false skips Windows' WPAD/proxy auto-detection entirely.
    /// That auto-detection is what was actually hanging in practice - it is
    /// a synchronous, non-cancelable native call that can block for a very
    /// long time on some networks, and it blocks before HttpClient's async
    /// machinery (and therefore Task.WhenAny below) ever gets a chance to
    /// race it. Home/ham-radio setups essentially never need a proxy for
    /// these direct internet downloads.
    /// </summary>
    private static HttpClient NewHttpClient() =>
        new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<int> RunAllAsync(bool dryRun = false)
    {
        using var http = NewHttpClient();
        var anyFailed = false;

        foreach (var entry in UpdaterCatalog.Entries)
        {
            var updater = UpdaterRegistry.Find(entry.Key);
            if (updater is null) continue;

            DetectedTarget target;
            try
            {
                target = updater.DetectTarget();
            }
            catch (Exception)
            {
                continue;
            }

            if (!target.IsInstalled)
            {
                Console.WriteLine($"{entry.DisplayName}: not detected on this PC, skipping.");
                continue;
            }

            var outcome = await RunSingleAsync(http, entry, updater, dryRun, force: false);
            if (outcome == UpdateOutcome.Failed) anyFailed = true;
        }

        return anyFailed ? 1 : 0;
    }

    /// <summary>Dry-run check of a single program, live network/detection
    /// included but nothing downloaded or installed - the single-program
    /// counterpart to --check-updates, useful for validating one updater
    /// (e.g. CHIRP's browser automation) without touching every program.</summary>
    public static async Task<int> CheckOneAsync(string key)
    {
        var entry = UpdaterCatalog.Find(key);
        var updater = UpdaterRegistry.Find(key);
        if (entry is null || updater is null)
        {
            Console.WriteLine($"Unknown program key: {key}");
            Console.WriteLine("Valid keys: " + string.Join(", ", UpdaterCatalog.Entries.Select(e => e.Key)));
            return 1;
        }

        using var http = NewHttpClient();

        DetectedTarget target;
        try
        {
            target = updater.DetectTarget();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{entry.DisplayName}: could not detect target program ({ex.Message})");
            return 1;
        }

        if (!target.IsInstalled)
        {
            Console.WriteLine($"{entry.DisplayName}: not detected on this PC.");
            return 1;
        }

        var outcome = await RunSingleAsync(http, entry, updater, dryRun: true, force: false);
        return outcome == UpdateOutcome.Failed ? 1 : 0;
    }

    public static async Task<int> RunOneAsync(string key, bool force)
    {
        var entry = UpdaterCatalog.Find(key);
        var updater = UpdaterRegistry.Find(key);
        if (entry is null || updater is null)
        {
            Console.WriteLine($"Unknown program key: {key}");
            Console.WriteLine("Valid keys: " + string.Join(", ", UpdaterCatalog.Entries.Select(e => e.Key)));
            return 1;
        }

        using var http = NewHttpClient();

        DetectedTarget target;
        try
        {
            target = updater.DetectTarget();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{entry.DisplayName}: could not detect target program ({ex.Message})");
            return 1;
        }

        if (!target.IsInstalled)
        {
            Console.WriteLine($"{entry.DisplayName}: not detected on this PC.");
            return 1;
        }

        var outcome = await RunSingleAsync(http, entry, updater, dryRun: false, force);
        return outcome == UpdateOutcome.Failed ? 1 : 0;
    }

    private static async Task<UpdateOutcome> RunSingleAsync(
        HttpClient http, UpdaterEntry entry, IProgramUpdater updater, bool dryRun, bool force)
    {
        var log = updater.CreateLog(UpdaterCatalog.LogPath(entry));
        using var cts = new CancellationTokenSource();
        var ctx = new UpdaterContext(http, log, DryRun: dryRun, Force: force, cts.Token);

        log.BeginRun(updater.DisplayName);
        try
        {
            // Task.Run, not a direct call: if RunAsync blocks synchronously
            // before its first real await (as the WPAD hang did), calling it
            // directly would block this method too, and Task.WhenAny below
            // would never even get reached.
            var runTask = Task.Run(() => updater.RunAsync(ctx));
            var winner = await Task.WhenAny(runTask, Task.Delay(HardTimeout));

            if (winner != runTask)
            {
                cts.Cancel();
                log.Line($"{updater.DisplayName} Updater FAILED: timed out after {HardTimeout.TotalMinutes:0} minutes with no progress");
                Console.WriteLine($"{entry.DisplayName}: TIMED OUT");
                return UpdateOutcome.Failed;
            }

            var result = await runTask;
            Console.WriteLine($"{entry.DisplayName}: {result.Outcome} {result.Message}");
            return result.Outcome;
        }
        catch (Exception ex)
        {
            log.Line($"{updater.DisplayName} Updater FAILED: {ex.Message}");
            Console.WriteLine($"{entry.DisplayName}: FAILED {ex.Message}");
            return UpdateOutcome.Failed;
        }
        finally
        {
            log.EndRun();
        }
    }
}
