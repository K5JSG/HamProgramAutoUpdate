using System.Text.RegularExpressions;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Writes updater logs in exactly the format Services/LogParser.cs already
/// parses: a "====" header naming the program and a timestamp, "[HH:mm:ss]"
/// lines, flushed per-line so a crash mid-run still leaves a trace (this is
/// the one behavior TQSL's Python script got right that the others didn't).
/// Every call site should pass the exact closing phrase LogParser looks for
/// (see LogParser.CloserSuccess/CloserFailure/Updates) - this class only
/// owns the envelope (header, timestamps, rotation), not the wording.
/// </summary>
public class UpdaterLog : IDisposable
{
    private static readonly Regex RunHeaderStart = new(
        @"(?m)^=+\r?\n.*?\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}", RegexOptions.Compiled);

    protected readonly string LogPath;
    protected StreamWriter? Writer;

    public UpdaterLog(string logPath) => LogPath = logPath;

    /// <summary>Trims older runs (keeping the most recent <paramref name="maxRuns"/> - 1,
    /// leaving room for the one about to start) and opens the log for the new run.
    ///
    /// Never throws: if the log file is locked by something else (another
    /// process reading/writing it, an editor with it open, etc.) this update
    /// still has to run - it just won't be recorded to disk. Line() is a
    /// no-op while Writer is null, so callers don't need to know this failed.</summary>
    public virtual void BeginRun(string programName, int maxRuns = 3)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            RotateKeepingLast(maxRuns);

            var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            Writer = new StreamWriter(stream) { AutoFlush = true };
            WriteHeader(programName);
        }
        catch (Exception ex)
        {
            Writer = null;
            Console.WriteLine($"WARNING: could not open log file '{LogPath}' ({ex.Message}) - this run will not be recorded to it.");
        }
    }

    protected virtual void WriteHeader(string programName)
    {
        var bar = new string('=', 40);
        Writer!.WriteLine(bar);
        Writer.WriteLine($"{programName.ToUpperInvariant()} UPDATER {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Writer.WriteLine(bar);
    }

    /// <summary>Writes one "[HH:mm:ss] message" line. Pass the exact phrase
    /// LogParser expects for anything meant to be recognized as a success/
    /// failure closer or a real update.</summary>
    public void Line(string message) => Writer?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    public virtual void EndRun()
    {
        Writer?.WriteLine();
        Writer?.Dispose();
        Writer = null;
    }

    public void Dispose() => EndRun();

    private void RotateKeepingLast(int maxRuns)
    {
        try
        {
            if (!File.Exists(LogPath) || maxRuns < 1) return;

            var content = File.ReadAllText(LogPath);
            var matches = RunHeaderStart.Matches(content);
            if (matches.Count < maxRuns) return;

            var keepFromIndex = matches.Count - (maxRuns - 1);
            var keepFrom = matches[keepFromIndex].Index;
            File.WriteAllText(LogPath, content[keepFrom..]);
        }
        catch (Exception)
        {
            // Rotation is a nicety; never block a real update run over it.
        }
    }
}

/// <summary>
/// RT Systems' log uses a different header (50 "=" chars, a colon after
/// "RUN") that Services/LogParser.cs's RtHeader regex specifically expects -
/// see LogParser.ParseRuns, which special-cases this format before falling
/// back to the generic Headers patterns every other program uses.
/// </summary>
public sealed class RtSystemsLog : UpdaterLog
{
    public RtSystemsLog(string logPath) : base(logPath) { }

    protected override void WriteHeader(string programName)
    {
        var bar = new string('=', 50);
        Writer!.WriteLine(bar);
        Writer.WriteLine($"RT SYSTEMS GLOBAL UPDATE RUN: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Writer.WriteLine(bar);
    }
}
