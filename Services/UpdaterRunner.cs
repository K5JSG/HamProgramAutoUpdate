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
                // cts is deliberately NOT disposed on this path: runTask is
                // abandoned here, not awaited, and may still read
                // ctx.CancellationToken later (e.g. a linked
                // CancellationTokenSource inside SilentExeInstaller.RunAsync)
                // - disposing out from under it risks an
                // ObjectDisposedException inside a task nobody observes,
                // silently swallowed since nothing ever awaits runTask again.
                // Leaking one CTS on this rare timeout path costs far less
                // than that.
                return UpdateResult.Failed("Timed out");
            }

            try
            {
                return await runTask;
            }
            finally
            {
                cts.Dispose();
            }
        }
        catch (Exception ex)
        {
            log.Line($"{updater.DisplayName} Updater FAILED: {ex.Message}");
            return UpdateResult.Failed(ex.Message);
        }
        finally
        {
            log.EndRun();
        }
    }

    public void Dispose() => _http.Dispose();
}
