using Microsoft.Win32;

namespace HamProgramAutoUpdate.Services.Updaters.Programs.Chirp;

/// <summary>Finds an installed Chrome/Chromium-family browser to drive via
/// CDP. Checked in order: the normal per-machine and per-user Chrome install
/// locations, then the "App Paths" registry key Chrome's installer
/// registers, then Edge (Chromium-based, speaks the same DevTools protocol)
/// as a last resort so a machine without Chrome specifically still works.</summary>
public static class ChromeLocator
{
    public static string? Find()
    {
        string[] fixedPaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        };
        foreach (var path in fixedPaths)
        {
            if (File.Exists(path)) return path;
        }

        var fromAppPaths = ReadAppPath(@"chrome.exe");
        if (fromAppPaths is not null) return fromAppPaths;

        string[] edgePaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        foreach (var path in edgePaths)
        {
            if (File.Exists(path)) return path;
        }

        return ReadAppPath(@"msedge.exe");
    }

    private static string? ReadAppPath(string exeName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            var value = key?.GetValue(null) as string;
            return !string.IsNullOrEmpty(value) && File.Exists(value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
