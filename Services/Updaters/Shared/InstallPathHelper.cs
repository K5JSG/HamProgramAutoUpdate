namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>Was an identical private method in both WsjtxUpdater and
/// WsjtxImprovedUpdater before being pulled out here - both forks share the
/// same "main exe sits in a bin\ subfolder of the real install dir"
/// layout, so drift between the two copies would risk exactly the kind of
/// install-path mixup both classes already have to guard against for
/// other reasons (see their class doc comments).</summary>
public static class InstallPathHelper
{
    /// <summary>Strips a trailing "bin" segment from an exe's directory, if
    /// present, to get the program's true install root.</summary>
    public static string InstallDirFor(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        return string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir)!
            : dir;
    }
}
