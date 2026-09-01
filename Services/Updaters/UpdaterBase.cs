using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters;

/// <summary>Shared ceremony every IProgramUpdater needs: key/display name and
/// wiring the target-detection delegate. Subclasses implement RunAsync only.</summary>
public abstract class UpdaterBase : IProgramUpdater
{
    public string Key { get; }
    public string DisplayName { get; }

    private readonly TargetDetector _detect;

    protected UpdaterBase(string key, string displayName, TargetDetector detect)
    {
        Key = key;
        DisplayName = displayName;
        _detect = detect;
    }

    public DetectedTarget DetectTarget() => _detect();

    public virtual UpdaterLog CreateLog(string logPath) => new(logPath);

    /// <summary>The standard "not installed, skip this run" result ten of
    /// the twelve updaters need verbatim (RT Systems' "no modules found"
    /// wording differs enough to stay custom; CHIRP doesn't use this shape
    /// at all) - centralizes the
    /// *shape* (always both a user-facing skip line and the closing line
    /// LogParser looks for) rather than forcing every caller to hand-write
    /// both. <paramref name="subject"/> and <paramref name="closingName"/>
    /// default to DisplayName but are separately overridable: a few
    /// updaters' already-shipped closing line uses a shortened name
    /// (HRD, N1MM, POTA) or different casing (GridTracker, matching its
    /// original Python script's own log text) that doesn't derive from
    /// DisplayName, and changing already-shipped log wording without a
    /// reason isn't worth the risk just to make this a one-liner
    /// everywhere.</summary>
    protected UpdateResult SkipNotInstalled(UpdaterContext ctx, string? subject = null, string? closingName = null)
    {
        ctx.Log.Line($"{subject ?? DisplayName} is not installed on this PC - skipping.");
        ctx.Log.Line($"{closingName ?? DisplayName} Updater completed successfully");
        return UpdateResult.Skipped("Not installed");
    }

    public abstract Task<UpdateResult> RunAsync(UpdaterContext ctx);
}
