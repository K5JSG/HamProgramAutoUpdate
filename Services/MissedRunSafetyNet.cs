using System.Globalization;
using HamProgramAutoUpdate.Services.Updaters;

namespace HamProgramAutoUpdate.Services;

/// <summary>
/// Catches the one case the "Program Update Scripts" task's own triggers
/// can miss. That task only runs while an administrator is signed in (its
/// principal is the Administrators group, with no stored password), so a PC
/// left at the sign-in screen after a restart skips its 3am runs. Its
/// LogonTrigger normally catches up 2 minutes after the next sign-in, but
/// that can still be lost - e.g. an upgrade re-registering the task right
/// as the delayed logon run was due (seen live on 2026-10-03).
///
/// So once per dashboard start, a few minutes in, this checks when the last
/// full run happened and starts the task itself if it's more than a day
/// old. It never double-runs with the logon catch-up:
///   - CheckDelay is well past the LogonTrigger's 2-minute delay, so in the
///     normal case that run has already started and stamped
///     last_full_run.txt, and this sees a fresh marker and does nothing;
///   - it skips if the task is running right now;
///   - and if the logon run is late anyway, whichever of the two starts
///     second is stopped by the task's IgnoreNew policy (if the first is
///     still running) or HeadlessUpdateRunner's 20-minute
///     RecentFullRunWindow guard (if it has finished).
/// </summary>
public static class MissedRunSafetyNet
{
    /// <summary>How long after the dashboard starts to check.</summary>
    public static readonly TimeSpan CheckDelay = TimeSpan.FromMinutes(5);

    /// <summary>A full run older than this means at least one daily run was
    /// missed.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>One line recording the last time this stepped in, so a
    /// catch-up run started from here can be told apart from the task's own
    /// triggers.</summary>
    private static string RecordPath => Path.Combine(HistoryStore.StateDir, "missed_run_safety_net.txt");

    /// <summary>Starts the updater task if the last full run is overdue.
    /// Blocks on schtasks, so call it off the UI thread. Never throws.</summary>
    public static void CheckAndRun()
    {
        try
        {
            var last = HeadlessUpdateRunner.LastFullRunUtc();
            if (last is { } l && DateTime.UtcNow - l < MaxAge) return;

            // No task means the user removed the nightly updates on purpose
            // (or a reinstall didn't recreate it) - not this class's job to
            // run them anyway.
            if (!TaskSchedulerService.TaskExists(TaskSchedulerService.UpdaterTaskPath)) return;
            if (TaskSchedulerService.IsRunning(TaskSchedulerService.UpdaterTaskPath)) return;

            var lastText = last is { } t
                ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "never";
            var error = TaskSchedulerService.RunTask(TaskSchedulerService.UpdaterTaskPath);
            Record(error is null
                ? $"Started a catch-up update run (last full run: {lastText})."
                : $"Could not start a catch-up update run (last full run: {lastText}): {error}");
        }
        catch (Exception)
        {
            // A safety net must never take the dashboard down with it.
        }
    }

    private static void Record(string message)
    {
        try
        {
            Directory.CreateDirectory(HistoryStore.StateDir);
            File.WriteAllText(RecordPath,
                $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
