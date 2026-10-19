using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>Runs a separately installed maintenance tool (N1MM County Data
/// Updater) that keeps the county data of N1MM Mobile County Automation
/// current with N1MM+'s QSO party lists. Only present on the one PC that
/// tool's server part is installed on, so the card never appears anywhere
/// else. Like the POTA Offline Maps card, the work happens in the tool's own
/// process; this just launches it, relays its output into this program's log
/// and reports the result.</summary>
public sealed class N1mmCountyDataToolUpdater : UpdaterBase
{
    private static readonly string ExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "K5JSG", "N1MM County Data Updater", "N1mmCountyDataUpdater.exe");

    /// <summary>A nightly run is a quick list comparison; only a change makes it
    /// rebuild the data (about 15 seconds) and upload a few small files.</summary>
    public override TimeSpan? MaxRunTime => TimeSpan.FromMinutes(30);

    private const int ToolBudgetMinutes = 25;

    private static readonly Regex SummaryRegex =
        new(@"Checked (\d+) part\(ies\), (\d+) changed, (\d+) published, (\d+) waiting for review(?:, (\d+) need attention)?", RegexOptions.Compiled);

    public N1mmCountyDataToolUpdater() : base("n1mm_county_data", "N1MM County Data", TargetDetectors.FixedPaths(ExePath))
    {
    }

    private sealed class Counts
    {
        public int Checked;
        public int Changed;
        public int Published;
        public int Waiting;
        public int Attention;
    }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        if (!DetectTarget().IsInstalled) return SkipNotInstalled(ctx, closingName: "N1MM County Data");

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

        var counts = new Counts();
        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => OnToolLine(ctx, e.Data, counts);
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
                ctx.Log.Line("N1MM County Data Updater FAILED: stopped before it finished");
                return UpdateResult.Failed("Stopped");
            }
            process.WaitForExit(); // flush the redirected output

            if (process.ExitCode != 0)
            {
                ctx.Log.Line($"N1MM County Data Updater FAILED: exit code {process.ExitCode}");
                return UpdateResult.Failed($"Exit code {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"N1MM County Data Updater FAILED: {ex.Message}");
            return UpdateResult.Failed(ex.Message);
        }

        return Completed(ctx, counts);
    }

    private static IEnumerable<string> ToolArgs(UpdaterContext ctx)
    {
        yield return "--run";
        yield return "--max-minutes";
        yield return ToolBudgetMinutes.ToString();
        if (ctx.DryRun) yield return "--dry-run";
        // Force = rebuild every party even if no list changed, to compare with
        // what is published (the tool still only publishes real changes).
        if (ctx.Force) yield return "--force";
    }

    private static void OnToolLine(UpdaterContext ctx, string? line, Counts counts)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        ctx.Log.Line(line);
        var m = SummaryRegex.Match(line);
        if (m.Success)
        {
            counts.Checked = int.Parse(m.Groups[1].Value);
            counts.Changed = int.Parse(m.Groups[2].Value);
            counts.Published = int.Parse(m.Groups[3].Value);
            counts.Waiting = int.Parse(m.Groups[4].Value);
            counts.Attention = m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0;
        }
    }

    private static UpdateResult Completed(UpdaterContext ctx, Counts counts)
    {
        ctx.Log.Line("N1MM County Data Updater completed successfully");
        var waiting = counts.Waiting > 0 ? $"; {counts.Waiting} waiting for your review" : string.Empty;
        var attention = counts.Attention > 0 ? $"; {counts.Attention} need attention" : string.Empty;
        return counts.Published > 0
            ? UpdateResult.Updated(null, $"{counts.Published} party data set(s) published{waiting}{attention}")
            : UpdateResult.UpToDate($"{counts.Checked} part(ies) checked{waiting}{attention}");
    }

    /// <summary>
    /// The nightly task runs as SYSTEM, but this tool only works as the
    /// Windows user who set it up: its GitHub access is that user's own
    /// GitHub CLI login. So from SYSTEM it's started inside the signed-in
    /// user's session, as them (UserSessionLauncher), exactly like the POTA
    /// Offline Maps tool. With nobody signed in it waits for a night when
    /// someone is.
    /// </summary>
    private static async Task<UpdateResult> RunAsSignedInUserAsync(UpdaterContext ctx)
    {
        var counts = new Counts();
        var gate = new object();
        try
        {
            using var launched = UserSessionLauncher.Start(ExePath, ToolArgs(ctx), Path.GetDirectoryName(ExePath)!,
                line => { lock (gate) OnToolLine(ctx, line, counts); });

            if (launched is null)
            {
                ctx.Log.Line("Nobody is signed in to this PC, and this tool has to run as the signed-in user - skipped until someone is.");
                ctx.Log.Line("N1MM County Data Updater completed successfully");
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
                ctx.Log.Line("N1MM County Data Updater FAILED: stopped before it finished");
                return UpdateResult.Failed("Stopped");
            }

            if (exitCode != 0)
            {
                ctx.Log.Line($"N1MM County Data Updater FAILED: exit code {exitCode}");
                return UpdateResult.Failed($"Exit code {exitCode}");
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"N1MM County Data Updater FAILED: {ex.Message}");
            return UpdateResult.Failed(ex.Message);
        }

        lock (gate) return Completed(ctx, counts);
    }
}
