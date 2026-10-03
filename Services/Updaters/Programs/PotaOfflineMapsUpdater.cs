using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>Runs a separately installed maintenance tool (POTA Offline Map
/// Updater) that keeps the POTA Activator app's downloadable offline state
/// maps current. Only present on the one PC that tool is installed on, so
/// the card never appears anywhere else. Unlike the other updaters, the work
/// happens in that tool's own process; this just launches it, relays its
/// output into this program's log and reports the result.</summary>
public sealed class PotaOfflineMapsUpdater : UpdaterBase
{
    private static readonly string ExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "K5JSG", "POTA Offline Map Updater", "PotaOfflineMapUpdater.exe");

    /// <summary>The tool stops starting new work at about an hour and finishes
    /// the state in progress (a large map upload can take 15+ minutes on a
    /// home connection) - well past the standard HardTimeout.</summary>
    public override TimeSpan? MaxRunTime => TimeSpan.FromMinutes(90);

    private const int ToolBudgetMinutes = 75;

    private static readonly Regex SummaryRegex =
        new(@"Checked (\d+) state\(s\), uploaded (\d+)", RegexOptions.Compiled);

    public PotaOfflineMapsUpdater() : base("pota_offline_maps", "POTA Offline Maps", TargetDetectors.FixedPaths(ExePath))
    {
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        if (!DetectTarget().IsInstalled) return SkipNotInstalled(ctx, closingName: "POTA Offline Maps");

        if (UserSessionLauncher.IsRunningAsSystem) return await RunAsSignedInUserAsync(ctx);

        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        foreach (var a in ToolArgs(ctx)) psi.ArgumentList.Add(a);

        int checkedStates = 0, uploaded = 0;
        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => OnToolLine(ctx, e.Data, ref checkedStates, ref uploaded);
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) ctx.Log.Line(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ctx.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                ctx.Log.Line("POTA Offline Maps Updater FAILED: stopped before it finished");
                return UpdateResult.Failed("Stopped");
            }
            process.WaitForExit(); // flush the redirected output

            if (process.ExitCode != 0)
            {
                ctx.Log.Line($"POTA Offline Maps Updater FAILED: exit code {process.ExitCode}");
                return UpdateResult.Failed($"Exit code {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"POTA Offline Maps Updater FAILED: {ex.Message}");
            return UpdateResult.Failed(ex.Message);
        }

        return Completed(ctx, checkedStates, uploaded);
    }

    private static IEnumerable<string> ToolArgs(UpdaterContext ctx)
    {
        yield return "--run";
        yield return "--max-minutes";
        yield return ToolBudgetMinutes.ToString();
        if (ctx.DryRun) yield return "--dry-run";
        // Force = check every state now instead of only the ones due this
        // week (the tool's own change threshold still decides what uploads).
        if (ctx.Force) yield return "--all";
    }

    private static void OnToolLine(UpdaterContext ctx, string? line, ref int checkedStates, ref int uploaded)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        ctx.Log.Line(line);
        var m = SummaryRegex.Match(line);
        if (m.Success)
        {
            checkedStates = int.Parse(m.Groups[1].Value);
            uploaded = int.Parse(m.Groups[2].Value);
        }
    }

    private static UpdateResult Completed(UpdaterContext ctx, int checkedStates, int uploaded)
    {
        ctx.Log.Line("POTA Offline Maps Updater completed successfully");
        return uploaded > 0
            ? UpdateResult.Updated(null, $"{uploaded} of {checkedStates} checked state map(s) uploaded")
            : UpdateResult.UpToDate($"{checkedStates} state map(s) checked");
    }

    /// <summary>
    /// The nightly task runs as SYSTEM, but this tool only works as the
    /// Windows user who set it up: its GitHub access is that user's own
    /// GitHub CLI login, and its record of what's already uploaded (without
    /// which it would re-upload every state, ~17 GB) lives in that user's
    /// %LOCALAPPDATA%. So from SYSTEM it's started inside the signed-in
    /// user's session, as them (UserSessionLauncher). With nobody signed in
    /// it waits for a night when someone is - the same as before the task
    /// moved to SYSTEM.
    /// </summary>
    private static async Task<UpdateResult> RunAsSignedInUserAsync(UpdaterContext ctx)
    {
        int checkedStates = 0, uploaded = 0;
        var gate = new object();
        try
        {
            using var launched = UserSessionLauncher.Start(ExePath, ToolArgs(ctx), Path.GetDirectoryName(ExePath)!,
                line => { lock (gate) OnToolLine(ctx, line, ref checkedStates, ref uploaded); });

            if (launched is null)
            {
                ctx.Log.Line("Nobody is signed in to this PC, and this tool has to run as the signed-in user - skipped until someone is.");
                ctx.Log.Line("POTA Offline Maps Updater completed successfully");
                return UpdateResult.Skipped("Nobody signed in");
            }

            ctx.Log.Line($"Running as the signed-in user ({launched.UserName}).");
            int exitCode;
            try
            {
                exitCode = await launched.WaitForExitAsync(ctx.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                launched.Kill();
                ctx.Log.Line("POTA Offline Maps Updater FAILED: stopped before it finished");
                return UpdateResult.Failed("Stopped");
            }

            if (exitCode != 0)
            {
                ctx.Log.Line($"POTA Offline Maps Updater FAILED: exit code {exitCode}");
                return UpdateResult.Failed($"Exit code {exitCode}");
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"POTA Offline Maps Updater FAILED: {ex.Message}");
            return UpdateResult.Failed(ex.Message);
        }

        lock (gate) return Completed(ctx, checkedStates, uploaded);
    }
}
