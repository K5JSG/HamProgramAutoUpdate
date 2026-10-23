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

    /// <summary>Full paths of the shortcuts whose names contain one of
    /// <paramref name="nameSubstrings"/>, on every desktop a silent install
    /// could put one on (see <see cref="AllDesktops"/>). Take this before
    /// installing, then hand it to <see cref="RemoveNewWithDelayAsync"/>.</summary>
    public static HashSet<string> Snapshot(params string[] nameSubstrings)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var desktop in AllDesktops())
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(desktop, "*.lnk"))
                    if (Matches(file, nameSubstrings)) found.Add(file);
            }
            catch (Exception) { }
        }
        return found;
    }

    /// <summary>Removes matching shortcuts that were not in
    /// <paramref name="before"/>, twice (right away and after
    /// <paramref name="delay"/>) like <see cref="RemoveMatchingWithDelayAsync"/>.
    /// A shortcut a PC already had is left alone. Returns the names removed.</summary>
    public static async Task<List<string>> RemoveNewWithDelayAsync(
        HashSet<string> before, TimeSpan delay, params string[] nameSubstrings)
    {
        var removed = new List<string>();
        RemoveNew(before, nameSubstrings, removed);
        await Task.Delay(delay);
        RemoveNew(before, nameSubstrings, removed);
        return removed;
    }

    private static void RemoveNew(HashSet<string> before, string[] nameSubstrings, List<string> removed)
    {
        foreach (var desktop in AllDesktops())
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(desktop, "*.lnk").ToList())
                {
                    if (!Matches(file, nameSubstrings) || before.Contains(file)) continue;
                    try { File.Delete(file); removed.Add(Path.GetFileName(file)); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }

    private static bool Matches(string file, string[] nameSubstrings)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return nameSubstrings.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The running account's desktop, the Public desktop, and every
    /// profile's own (and OneDrive-redirected) desktop - a nightly run as
    /// SYSTEM has a desktop of its own that isn't any user's.</summary>
    private static IEnumerable<string> AllDesktops()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Desktops()) dirs.Add(d);
        try
        {
            var usersRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Users");
            if (Directory.Exists(usersRoot))
                foreach (var profile in Directory.EnumerateDirectories(usersRoot))
                {
                    dirs.Add(Path.Combine(profile, "Desktop"));
                    dirs.Add(Path.Combine(profile, "OneDrive", "Desktop"));
                }
        }
        catch (Exception) { }
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));
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
