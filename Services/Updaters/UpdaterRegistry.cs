using HamProgramAutoUpdate.Services.Updaters.Programs;

namespace HamProgramAutoUpdate.Services.Updaters;

/// <summary>The one instance of every program's updater, keyed the same way
/// as UpdaterCatalog.Entries.</summary>
public static class UpdaterRegistry
{
    public static readonly IReadOnlyDictionary<string, IProgramUpdater> All =
        new IProgramUpdater[]
        {
            new BktTimeSyncUpdater(),
            new ChirpUpdater(),
            new GridTrackerUpdater(),
            new HrdUpdater(),
            new N1mmUpdater(),
            new NetLoggerUpdater(),
            new PotaUpdater(),
            new RtSystemsUpdater(),
            new TqslUpdater(),
            new WsjtxUpdater(),
        }.ToDictionary(u => u.Key);

    public static IProgramUpdater? Find(string key) => All.GetValueOrDefault(key);
}
