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

            var logPath = UpdaterCatalog.LogPath(entry);
            var log = updater.CreateLog(logPath);
            var cts = new CancellationTokenSource();
            var ctx = new UpdaterContext(_http, log, DryRun: false, Force: false, cts.Token);

            _running[key] = RunAndCloseLogAsync(updater, log, ctx, cts);
            return null;
        }
    }

    private static async Task<UpdateResult> RunAndCloseLogAsync(
        IProgramUpdater updater, UpdaterLog log, UpdaterContext ctx, CancellationTokenSource cts)
    {
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
                return UpdateResult.Failed("Timed out");
            }

            return await runTask;
        }
        catch (Exception ex)
        {
            log.Line($"{updater.DisplayName} Updater FAILED: {ex.Message}");
            return UpdateResult.Failed(ex.Message);
        }
        finally
        {
            log.EndRun();
            cts.Dispose();
        }
    }

    public void Dispose() => _http.Dispose();
}
