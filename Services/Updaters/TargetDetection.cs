namespace HamProgramAutoUpdate.Services.Updaters;

/// <summary>What we found out about the actual ham radio program on this PC
/// (not the updater - the program the updater installs updates for).</summary>
public sealed class DetectedTarget
{
    public bool IsInstalled { get; init; }
    public string? InstallPath { get; init; }
    public string? Version { get; init; }

    public static readonly DetectedTarget NotFound = new() { IsInstalled = false };

    public static DetectedTarget Found(string? path = null, string? version = null) =>
        new() { IsInstalled = true, InstallPath = path, Version = version };
}

/// <summary>
/// Answers "is this program actually installed on this PC, and if so where".
/// Kept as a delegate rather than an interface: every detector is a handful of
/// lines built from the shared registry/path-probe helpers below, so a type
/// hierarchy would only add ceremony.
/// </summary>
public delegate DetectedTarget TargetDetector();

public static class TargetDetectors
{
    /// <summary>Installed if any candidate path exists; version read from
    /// whichever one matched. Used by programs with a small, known set of
    /// possible install locations (Program Files vs Program Files (x86)).</summary>
    public static TargetDetector FixedPaths(params string[] candidatePaths) => () =>
    {
        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
                return DetectedTarget.Found(path, Shared.FileVersionHelper.ReadFileVersion(path));
        }
        return DetectedTarget.NotFound;
    };
}
