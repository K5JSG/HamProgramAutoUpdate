using System.Net.Http;
using HamProgramAutoUpdate.Services.Updaters;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services;

/// <summary>Abstraction over <see cref="UpdaterRunner"/> so callers (MainWindow,
/// StatusService, and any future test) depend on this contract rather than the
/// concrete class, which owns a real HttpClient and launches real updaters.</summary>
public interface IUpdaterRunner : IDisposable
{
    bool IsRunning(string key);
    bool AnyRunning();
    string? Run(string key);

    /// <summary>
    /// Blocks new Run() calls and waits for every currently-running updater
    /// to finish, so the caller can rely on no updater running - and
    /// therefore none calling Path.GetTempPath() - until the returned
    /// IDisposable is disposed. Used by SelfUpdateService to close a race
    /// where a running updater's temp-path lookup could observe the
    /// installer's briefly-redirected TMP/TEMP - see
    /// SelfUpdateService.LaunchWithRedirectedTempDirAsync.
    /// </summary>
    Task<IDisposable> PauseForInstallAsync();
}

/// <summary>
/// Runs one program's in-process updater (see Services/Updaters) on a
/// background task and keeps track of whether it is still running, so the
/// card can show a spinner and refuse a second launch.
///
/// This used to shell out to each program's separate PyInstaller exe; now
/// every updater lives in-process (see UpdaterRegistry), so "running" means
/// "the Task hasn't completed" rather than "the child process hasn't exited".
/// Same public contract as before, so MainWindow.xaml.cs and the tray menu
/// need no changes.
/// </summary>
public sealed class UpdaterRunner : IUpdaterRunner
{
    /// <summary>Hard ceiling on one program's whole run - see the identical
    /// constant in HeadlessUpdateRunner for why this exists: some hangs
    /// happen below the managed network stack and do not respect
    /// HttpClient.Timeout or a cancellation token.</summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Task<UpdateResult>> _running = new();
    private readonly object _lock = new();
    private bool _installPending;

    // UseProxy = false skips Windows' WPAD/proxy auto-detection, a
    // synchronous native call that can hang for a long time on some
    // networks - see the matching comment in HeadlessUpdateRunner.
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public bool IsRunning(string key)
    {
        lock (_lock)
        {
            return _running.TryGetValue(key, out var task) && !task.IsCompleted;
        }
    }

    public bool AnyRunning()
    {
        lock (_lock)
        {
            return _running.Values.Any(t => !t.IsCompleted);
        }
    }

    /// <summary>Start one program's updater. Returns an error string on failure to start.</summary>
    public string? Run(string key)
    {
        var updater = UpdaterRegistry.Find(key);
        if (updater is null) return "Unknown program";

        var entry = UpdaterCatalog.Find(key);
        if (entry is null) return "Unknown program";

        lock (_lock)
        {
            if (_installPending)
                return "An app update is installing right now - try again in a moment.";

            if (_running.TryGetValue(key, out var existing) && !existing.IsCompleted)
                return "This updater is already running.";

            // Task.Run here too, not just inside RunAndCloseLogAsync's own
            // one: Task.Run always defers its delegate to the thread pool
            // rather than starting it inline, whereas calling an async
            // method directly runs it synchronously up to its first await -
            // and BeginRun's log rotation (a synchronous file read/rewrite)
            // sits before that first await. Without this, that I/O ran on
            // the caller's thread (the UI thread, for every card's Run
            // button) while still holding _lock, blocking IsRunning/
            // AnyRunning/Run for its duration.
            _running[key] = Task.Run(() => RunAndCloseLogAsync(updater, entry, _http));
            return null;
        }
    }

    private static async Task<UpdateResult> RunAndCloseLogAsync(
        IProgramUpdater updater, UpdaterEntry entry, HttpClient http)
    {
        var log = updater.CreateLog(UpdaterCatalog.LogPath(entry));

        // Closes a real cross-process race: the headless --run-updates
        // scheduled task (a separate process) can run the same program's
        // updater at the same time as this one - see
        // CrossProcessUpdaterLock's own doc comment for why this is
        // confirmed reachable, not just theoretical.
        var crossProcessLock = CrossProcessUpdaterLock.TryAcquire(entry.Key);
        if (crossProcessLock is null)
        {
            log.BeginRun(updater.DisplayName);
            log.Line($"{updater.DisplayName} Updater: already running via the scheduled task or another instance - skipping.");
            log.Line($"{updater.DisplayName} Updater completed successfully");
            log.EndRun();
            return UpdateResult.Skipped("Already running via the scheduled task or another instance");
        }

        var cts = new CancellationTokenSource();
        var ctx = new UpdaterContext(http, log, DryRun: false, Force: false, cts.Token);

        log.BeginRun(updater.DisplayName);
        try
        {
            // Task.Run, not a direct call: if RunAsync blocks synchronously
            // before its first real await, calling it directly would block
            // this method too, and Task.WhenAny below would never be reached.
            var runTask = Task.Run(() => updater.RunAsync(ctx));
            var winner = await Task.WhenAny(runTask, Task.Delay(HardTimeout));

            if (winner != runTask)
            {
                cts.Cancel();
                log.Line($"{updater.DisplayName} Updater FAILED: timed out after {HardTimeout.TotalMinutes:0} minutes with no progress");
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
                // in an unobserved task, or a real double-install race.
                return UpdateResult.Failed("Timed out");
            }

            try
            {
                return await runTask;
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
            return UpdateResult.Failed(ex.Message);
        }
        finally
        {
            log.EndRun();
        }
    }

    public async Task<IDisposable> PauseForInstallAsync()
    {
        List<Task<UpdateResult>> running;
        lock (_lock)
        {
            _installPending = true;
            running = _running.Values.Where(t => !t.IsCompleted).ToList();
        }

        // Best-effort drain: this only waits for RunAndCloseLogAsync's own
        // outer task, which completes at the latest by HardTimeout. A run
        // that hit HardTimeout has its own inner task abandoned rather than
        // awaited (see the CTS-leak comment above) and could in theory still
        // be mid-flight past this point - too rare an edge case (timeout AND
        // a same-instant install) to justify tracking abandoned tasks too.
        foreach (var task in running)
        {
            try { await task; } catch { }
        }

        return new InstallPause(this);
    }

    private void EndInstallPause()
    {
        lock (_lock) { _installPending = false; }
    }

    private sealed class InstallPause : IDisposable
    {
        private readonly UpdaterRunner _owner;
        public InstallPause(UpdaterRunner owner) => _owner = owner;
        public void Dispose() => _owner.EndInstallPause();
    }

    public void Dispose() => _http.Dispose();
}
