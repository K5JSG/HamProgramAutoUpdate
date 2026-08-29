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

    public abstract Task<UpdateResult> RunAsync(UpdaterContext ctx);
}
