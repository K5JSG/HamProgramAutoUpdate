using Microsoft.Win32;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Searches the Windows "Programs and Features" registry entries for a
/// program by (partial) display name. GridTracker, HRD and POTA's Python
/// updaters each reimplemented this same HKLM/HKLM-WOW6432Node/HKCU walk.
/// </summary>
public static class RegistryUninstallLookup
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKeyPathWow6432 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public sealed record Entry(string DisplayName, string? DisplayVersion, string? InstallLocation);

    /// <summary>First uninstall entry whose DisplayName contains
    /// <paramref name="substring"/> (case-insensitive), or null.</summary>
    public static Entry? FindByDisplayNameSubstring(string substring)
    {
        try
        {
            foreach (var (hive, keyPath) in Roots())
            {
                using var uninstallKey = hive.OpenSubKey(keyPath);
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey?.GetValue("DisplayName") is not string displayName) continue;

                    if (displayName.Contains(substring, StringComparison.OrdinalIgnoreCase))
                    {
                        return new Entry(
                            displayName,
                            subKey.GetValue("DisplayVersion") as string,
                            subKey.GetValue("InstallLocation") as string);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Treat any registry-access failure as "not found" rather than
            // faulting whichever updater called us.
        }

        return null;
    }

    /// <summary>Same search as <see cref="FindByDisplayNameSubstring"/>, but
    /// tolerant of punctuation differences between how a product names
    /// itself in its own version resource (e.g. "OmniRig") and how its
    /// installer actually registers in Programs and Features (e.g.
    /// "Omni-Rig 1.20") - both strings are reduced to lowercase
    /// letters/digits only before comparing. Confirmed necessary live:
    /// OmniRig's ProductName resource has no hyphen; its registered
    /// DisplayName does, so a plain substring search never matches it even
    /// though it really is installed.</summary>
    public static Entry? FindByNormalizedProductName(string productName)
    {
        var needle = Normalize(productName);
        if (needle.Length == 0) return null;

        try
        {
            foreach (var (hive, keyPath) in Roots())
            {
                using var uninstallKey = hive.OpenSubKey(keyPath);
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey?.GetValue("DisplayName") is not string displayName) continue;

                    if (Normalize(displayName).Contains(needle, StringComparison.Ordinal))
                    {
                        return new Entry(
                            displayName,
                            subKey.GetValue("DisplayVersion") as string,
                            subKey.GetValue("InstallLocation") as string);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Treat any registry-access failure as "not found" rather than
            // faulting whichever updater called us.
        }

        return null;
    }

    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IEnumerable<(RegistryKey Hive, string KeyPath)> Roots()
    {
        yield return (Registry.LocalMachine, UninstallKeyPath);
        yield return (Registry.LocalMachine, UninstallKeyPathWow6432);
        yield return (Registry.CurrentUser, UninstallKeyPath);
    }

    /// <summary>A ready-to-use TargetDetector for the common case of "found
    /// by display name substring, installed if present".</summary>
    public static TargetDetector Detector(string displayNameSubstring) => () =>
    {
        var entry = FindByDisplayNameSubstring(displayNameSubstring);
        return entry is null
            ? DetectedTarget.NotFound
            : DetectedTarget.Found(entry.InstallLocation, entry.DisplayVersion);
    };
}
