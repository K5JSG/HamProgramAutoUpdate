using System.Globalization;
using System.Net.Http;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;

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

    /// <summary>
    /// The "Program Update Scripts" task has two triggers on the same task
    /// (see TaskSchedulerService.BuildUpdaterTaskXml): the daily 3am
    /// CalendarTrigger, whose StartWhenAvailable fires it as a catch-up run
    /// the moment Task Scheduler notices a PC that was off/asleep through
    /// 3am, and a LogonTrigger 2 minutes after sign-in as the more reliable
    /// fix for that same case. MultipleInstancesPolicy=IgnoreNew only blocks
    /// a second instance while the first is still *running* - it does
    /// nothing once the catch-up run has already finished, which it usually
    /// has within seconds since every updater no-ops when current. Result:
    /// two full runs back to back around every boot, in whichever order the
    /// triggers happen to fire. This marker file + mutex makes a real
    /// (non-dry-run) run skip entirely if another one already completed
    /// within RecentFullRunWindow, so only one of the two triggers actually
    /// does anything.
    /// </summary>
    private static readonly TimeSpan RecentFullRunWindow = TimeSpan.FromMinutes(20);

    private static string LastFullRunMarkerPath => Path.Combine(HistoryStore.StateDir, "last_full_run.txt");

    /// <summary>Returns false (meaning: skip this run) if a real run already
    /// completed within RecentFullRunWindow; otherwise claims the window for
    /// this run and returns true. Fails open (allows the run) on any I/O
    /// problem - a guard-file glitch must never be the reason a real update
    /// run never happens.</summary>
    private static bool TryClaimFullRun()
    {
        try
        {
            Directory.CreateDirectory(HistoryStore.StateDir);

            using var mutex = new Mutex(false, @"Global\HamProgramAutoUpdate_FullRunGuard");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }

                if (File.Exists(LastFullRunMarkerPath) &&
                    DateTime.TryParse(File.ReadAllText(LastFullRunMarkerPath), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var last) &&
                    DateTime.UtcNow - last < RecentFullRunWindow)
                {
                    return false;
                }

                File.WriteAllText(LastFullRunMarkerPath, DateTime.UtcNow.ToString("o"));
                return true;
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static async Task<int> RunAllAsync(bool dryRun = false)
    {
        if (!dryRun && !TryClaimFullRun())
        {
            Console.WriteLine(
                $"A full update run already completed within the last {RecentFullRunWindow.TotalMinutes:0} minutes " +
                "(the missed-schedule catch-up and logon triggers likely both fired around this boot) - skipping.");
            return 0;
        }

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

        // Closes a real cross-process race: the interactive dashboard
        // (UpdaterRunner, a separate process from this headless one) can run
        // the same program's updater from a card's own Run button at the
        // same time as this scheduled run - see CrossProcessUpdaterLock's
        // own doc comment for why this is confirmed reachable, not just
        // theoretical.
        var crossProcessLock = CrossProcessUpdaterLock.TryAcquire(entry.Key);
        if (crossProcessLock is null)
        {
            log.BeginRun(updater.DisplayName);
            log.Line($"{updater.DisplayName} Updater: already running via the dashboard or another instance - skipping.");
            log.Line($"{updater.DisplayName} Updater completed successfully");
            log.EndRun();
            Console.WriteLine($"{entry.DisplayName}: SKIPPED (already running elsewhere)");
            return UpdateOutcome.Skipped;
        }

        var cts = new CancellationTokenSource();
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
                // Neither cts nor crossProcessLock is released on this path:
                // runTask is abandoned here, not awaited, and may still be
                // doing real work (e.g. still running an installer, still
                // reading ctx.CancellationToken) well after this method
                // returns. Releasing the cross-process lock here would let
                // another process start a genuinely concurrent run against
                // that still-active work - exactly the race the lock exists
                // to prevent. Same tradeoff already accepted for cts:
                // leaking one CTS and one Mutex handle on this rare timeout
                // path costs far less than either an ObjectDisposedException
                // in an unobserved task, or a real double-install race. Same
                // precedent as UpdaterRunner.RunAndCloseLogAsync.
                return UpdateOutcome.Failed;
            }

            try
            {
                var result = await runTask;
                Console.WriteLine($"{entry.DisplayName}: {result.Outcome} {result.Message}");
                return result.Outcome;
            }
            finally
            {
                cts.Dispose();
                crossProcessLock.Dispose();
            }
        }
        catch (Exception ex)
        {
            crossProcessLock.Dispose();
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
