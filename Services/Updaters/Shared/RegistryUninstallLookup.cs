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
