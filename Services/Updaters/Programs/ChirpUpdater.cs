using System.Diagnostics;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// CHIRP is the one program that stays on its existing Python updater rather
/// than an in-process C# port. archive.chirpmyradio.com sits behind a
/// Cloudflare Turnstile challenge, and a real C# browser-automation attempt
/// (PuppeteerSharp + a hidden desktop + raw Win32 click-posting) was built
/// and tested against the live site, surfacing two independent, fundamental
/// blockers - neither fixable by more coordinate/timing tuning:
///
///   1. On the hidden desktop, Chrome's compositor does not create the same
///      per-iframe RenderWidget windows it does on a real desktop, so there
///      is nothing to post a click to.
///   2. Even when run visibly, with the render widget correctly found and
///      clicks posted at the exact right coordinate, Cloudflare's
///      server-side check rejects the resulting session as non-human (the
///      checkbox visually ticks - the JS handlers fire regardless of
///      isTrusted - but the verification token is refused) and reissues a
///      fresh challenge. Only a real human click gets past it.
///
/// No alternate distribution channel exists either (checked: the official
/// GitHub repo ships no built installers, and even the winget package's own
/// manifest points at this exact same Cloudflare-protected URL) - archive.
/// chirpmyradio.com is the sole source for chirp-next nightly builds.
///
/// So this class just runs the existing, working Python updater - shipped as
/// a sibling file next to HamProgramAutoUpdate.exe (see ChirpUpdaterBinary\ and
/// the csproj) rather than depending on a separate folder the user maintains
/// independently. The exe manages its own log/state/profile files directly
/// under Documents\Ham Radio\Chirp Update Script\ (hardcoded in the Python
/// source, unrelated to wherever the exe binary itself lives), which is the
/// same file LogParser.cs already reads for the "chirp" key - see CreateLog
/// below for why this updater's C#-side log writer is a deliberate no-op.
/// </summary>
public sealed class ChirpUpdater : UpdaterBase
{
    private static string BundledExePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "ChirpUpdaterBinary", "Chirp Update Script.exe");

    public ChirpUpdater() : base("chirp", "CHIRP", TargetDetectors.FixedPaths(
        @"C:\Program Files (x86)\CHIRP\chirpwx.exe",
        @"C:\Program Files\CHIRP\chirpwx.exe"))
    {
    }

    /// <summary>The external exe writes its own header/rotation/lines
    /// directly to the same log file LogParser.cs reads for this key, so the
    /// normal BeginRun/Line/EndRun envelope must not also write to it - two
    /// independent writers on the same file would interleave and corrupt it.</summary>
    public override UpdaterLog CreateLog(string logPath) => new NoOpLog(logPath);

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        if (ctx.DryRun)
            return UpdateResult.UpToDate("Dry run - CHIRP uses its own external updater; no live check performed here.");

        if (!File.Exists(BundledExePath))
            return UpdateResult.Failed($"The bundled CHIRP updater was not found at {BundledExePath}");

        var stateBefore = ReadState();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = BundledExePath,
                WorkingDirectory = Path.GetDirectoryName(BundledExePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return UpdateResult.Failed("The CHIRP updater did not start.");

            await proc.WaitForExitAsync(ctx.CancellationToken);

            var stateAfter = ReadState();

            if (proc.ExitCode != 0)
                return UpdateResult.Failed($"CHIRP updater exited with code {proc.ExitCode}");

            return stateAfter != stateBefore
                ? UpdateResult.Updated(stateAfter ?? "unknown")
                : UpdateResult.UpToDate();
        }
        catch (Exception ex)
        {
            return UpdateResult.Failed(ex.Message);
        }
    }

    private static string? ReadState()
    {
        var path = Path.Combine(UpdaterCatalog.HamRadioDir, "Chirp Update Script", "last_installed_build.txt");
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception) { return null; }
    }

    /// <summary>BeginRun/Line/EndRun are all no-ops (Writer stays null the
    /// whole time) - see the class doc comment on why.</summary>
    private sealed class NoOpLog : UpdaterLog
    {
        public NoOpLog(string logPath) : base(logPath) { }
        public override void BeginRun(string programName, int maxRuns = 3) { }
        public override void EndRun() { }
    }
}
