using System.Net.Http;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters;

/// <summary>Shared state every updater needs to do its work and log it.</summary>
public sealed record UpdaterContext(
    HttpClient Http,
    UpdaterLog Log,
    bool DryRun,
    bool Force,
    CancellationToken CancellationToken);

public enum UpdateOutcome
{
    UpToDate,
    Updated,
    Failed,
    Skipped,
}

public sealed class UpdateResult
{
    public required UpdateOutcome Outcome { get; init; }
    public string? NewVersion { get; init; }
    public string? Message { get; init; }

    public static UpdateResult UpToDate(string? message = null) =>
        new() { Outcome = UpdateOutcome.UpToDate, Message = message };

    public static UpdateResult Updated(string version, string? message = null) =>
        new() { Outcome = UpdateOutcome.Updated, NewVersion = version, Message = message };

    public static UpdateResult Failed(string message) =>
        new() { Outcome = UpdateOutcome.Failed, Message = message };

    public static UpdateResult Skipped(string message) =>
        new() { Outcome = UpdateOutcome.Skipped, Message = message };
}

/// <summary>
/// One program's update logic: check the vendor's site, download if there is
/// something newer, install it silently, and log every step through
/// <see cref="UpdaterContext.Log"/> in the format Services/LogParser.cs reads.
///
/// Implementations must never throw out of RunAsync - UpdaterRunner treats an
/// unhandled exception as a bug, not a normal failed update. Catch everything
/// or -check and return UpdateResult.Failed instead.
/// </summary>
public interface IProgramUpdater
{
    /// <summary>Matches the key in UpdaterCatalog.Entries.</summary>
    string Key { get; }

    /// <summary>Display name used in the log header and UI messages.</summary>
    string DisplayName { get; }

    /// <summary>Is the actual ham radio program (not this updater) installed
    /// on this PC, and if so where/what version. Cheap enough to call on
    /// every dashboard refresh - registry reads and File.Exists checks only,
    /// no network access.</summary>
    DetectedTarget DetectTarget();

    /// <summary>Log writer for this program's log file. RT Systems overrides
    /// this to emit the different header format LogParser.RtHeader expects;
    /// every other program uses the default.</summary>
    UpdaterLog CreateLog(string logPath);

    Task<UpdateResult> RunAsync(UpdaterContext ctx);
}
