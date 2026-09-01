namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>Was an identical private method in both PotaUpdater and
/// Log4omUpdater before being pulled out here.</summary>
public static class DirectoryCopy
{
    /// <summary>Copies every file and subfolder from <paramref name="source"/>
    /// into <paramref name="dest"/>, overwriting anything already there.</summary>
    public static void CopyAll(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
