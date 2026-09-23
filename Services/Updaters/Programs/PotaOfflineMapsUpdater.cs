using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
        psi.ArgumentList.Add("--run");
        psi.ArgumentList.Add("--max-minutes");
        psi.ArgumentList.Add(ToolBudgetMinutes.ToString());
        if (ctx.DryRun) psi.ArgumentList.Add("--dry-run");
        // Force = check every state now instead of only the ones due this
        // week (the tool's own change threshold still decides what uploads).
        if (ctx.Force) psi.ArgumentList.Add("--all");

        int checkedStates = 0, uploaded = 0;
        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                ctx.Log.Line(e.Data);
                var m = SummaryRegex.Match(e.Data);
                if (m.Success)
                {
                    checkedStates = int.Parse(m.Groups[1].Value);
                    uploaded = int.Parse(m.Groups[2].Value);
                }
            };
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

        ctx.Log.Line("POTA Offline Maps Updater completed successfully");
        return uploaded > 0
            ? UpdateResult.Updated(null, $"{uploaded} of {checkedStates} checked state map(s) uploaded")
            : UpdateResult.UpToDate($"{checkedStates} state map(s) checked");
    }
}
