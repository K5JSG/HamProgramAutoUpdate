namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Picking "whichever .exe happens to be in the folder" instead of the
/// actual product has already caused one real, confirmed bug in this app:
/// POTA Activator falsely detected version 1.0.0.0 forever because a
/// bundled cleanup helper exe (permanently versioned 1.0.0.0) sorted ahead
/// of the real product exe. BktTimeSyncUpdater had the exact same
/// unguarded pattern - found during a later review - fixed by routing both
/// through this instead of just fixing BktTimeSync's own copy.
/// </summary>
public static class ExeFinder
{
    /// <summary>Finds the exe for <paramref name="productName"/> among
    /// non-uninstaller exes under <paramref name="dir"/>: an exact filename
    /// match first, then a filename-starts-with match (vendors often suffix
    /// their installer with a version, e.g. "BktTimeSync_1.21.0.exe" for
    /// product "BktTimeSync" - confirmed live against the real download;
    /// without this tier that case fell through to the "first candidate"
    /// guess below, only ever correct because the zip happened to contain
    /// nothing else), then - if there is exactly one non-uninstaller exe at
    /// all - that one, unambiguous regardless of its name. Only when none of
    /// that resolves it does this guess at the first candidate found,
    /// invoking <paramref name="onAmbiguous"/> (if given) so a genuinely
    /// ambiguous multi-candidate case is at least visible rather than a
    /// silent, unverifiable guess.</summary>
    public static string? FindByProductName(
        string dir, string productName, SearchOption searchOption = SearchOption.TopDirectoryOnly,
        Action<string>? onAmbiguous = null)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;

            var candidates = Directory.EnumerateFiles(dir, "*.exe", searchOption)
                .Where(p => !Path.GetFileName(p).Contains("uninst", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var exact = candidates.FirstOrDefault(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), productName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var prefixed = candidates.FirstOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).StartsWith(productName, StringComparison.OrdinalIgnoreCase));
            if (prefixed is not null) return prefixed;

            if (candidates.Count == 1) return candidates[0];

            if (candidates.Count > 1)
                onAmbiguous?.Invoke(
                    $"Could not find an exe named or prefixed \"{productName}\" among {candidates.Count} " +
                    $"candidates - guessing \"{Path.GetFileName(candidates[0])}\".");

            return candidates.FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
