namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Some installers (GridTracker, WSJT-X) drop an unwanted desktop shortcut
/// even when run silently. Their Python updaters deleted it from both the
/// personal and the all-users desktop, twice (once right after install, once
/// again after a short delay, since the installer's own background process
/// sometimes recreates it a moment later).
/// </summary>
public static class DesktopShortcutCleaner
{
    public static void RemoveMatching(params string[] nameSubstrings)
    {
        foreach (var desktop in Desktops())
        {
            RemoveFrom(desktop, nameSubstrings);
        }
    }

    public static async Task RemoveMatchingWithDelayAsync(TimeSpan delay, params string[] nameSubstrings)
    {
        RemoveMatching(nameSubstrings);
        await Task.Delay(delay);
        RemoveMatching(nameSubstrings);
    }

    private static IEnumerable<string> Desktops()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static void RemoveFrom(string desktopDir, string[] nameSubstrings)
    {
        try
        {
            if (!Directory.Exists(desktopDir)) return;

            foreach (var file in Directory.EnumerateFiles(desktopDir, "*.lnk"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (nameSubstrings.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(file); } catch (Exception) { }
                }
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup; never fail the update over a stray shortcut.
        }
    }
}
