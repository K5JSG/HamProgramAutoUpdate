namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Cross-process mutual exclusion per program key, so the interactive
/// dashboard (UpdaterRunner, started from a card's own Run button) and the
/// headless --run-updates scheduled task (HeadlessUpdateRunner) - two
/// separate processes with no other shared state - can never run the SAME
/// program's updater at the same time.
///
/// Confirmed reachable, not just theoretical: TaskSchedulerService's updater-
/// task LogonTrigger has only a 2-minute delay specifically to reduce (not
/// eliminate) colliding with the dashboard at logon - see its own doc
/// comment. Nothing stops the daily 3am run, or a logon-delayed run, from
/// overlapping a still-in-progress manual run on the same program (e.g. a
/// slow CHIRP browser-automation run started by the user). Two installers
/// racing for the same program is worse than the log-rotation interleaving
/// this was originally spotted from.
///
/// Same named-Mutex pattern as HistoryStore.Save's cross-process lock, just
/// keyed per program instead of one lock for the whole history file.
/// </summary>
public static class CrossProcessUpdaterLock
{
    /// <summary>
    /// Tries to acquire the lock for <paramref name="key"/>, waiting briefly
    /// for a near-simultaneous start elsewhere to clear rather than failing
    /// outright - but never long enough to make an interactive "Run" click
    /// feel hung. Returns null if another process already holds it (caller
    /// should treat that as "already running elsewhere", not a real
    /// failure), otherwise a scope that releases the lock on Dispose.
    /// </summary>
    public static IDisposable? TryAcquire(string key)
    {
        // Both the constructor and WaitOne are guarded, matching
        // HistoryStore.Save's identical Mutex usage - and deliberately FAIL
        // OPEN (proceed as if acquired) rather than skip a legitimate update
        // over this secondary safety mechanism's own failure, which would be
        // a regression versus this codebase's original "the updater runs
        // when asked" behavior. This is strictly rarer than the already-rare
        // cross-process race it exists to close.
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, $@"Global\HamProgramAutoUpdate_Updater_{key}");
        }
        catch (Exception)
        {
            return NoOpScope.Instance;
        }

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            // The previous owning process died mid-run without releasing it
            // - we still got it. Nothing shared-memory-based is protected
            // here (just "is someone else currently running this key"), so
            // there's no corrupted state left behind to worry about.
            acquired = true;
        }
        catch (Exception)
        {
            mutex.Dispose();
            return NoOpScope.Instance;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new ReleaseScope(mutex);
    }

    private sealed class ReleaseScope : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        public ReleaseScope(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _mutex.ReleaseMutex(); } catch (Exception) { }
            _mutex.Dispose();
        }
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }
}
