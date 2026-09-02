using System.Diagnostics;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Every Python updater hand-rolled GetFileVersionInfoW/VerQueryValueW via
/// ctypes to read an exe's version resource. .NET does this in one call.
/// </summary>
public static class FileVersionHelper
{
    public static string? ReadFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The exe's own ProductName resource - useful when a
    /// program's install path/exe name alone can't tell it apart from
    /// something else (see WsjtxUpdater: WSJT-X Improved's installer
    /// defaults to the exact same path and exe filename as real WSJT-X,
    /// confirmed live, but each reports a different ProductName).</summary>
    public static string? ReadProductName(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var name = FileVersionInfo.GetVersionInfo(path).ProductName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Parses a dotted version string, ignoring anything after the
    /// first run of digit-groups (e.g. "1.2.3.0 beta" -> [1,2,3,0]).</summary>
    public static int[]? ParseVersionParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<int>();
        foreach (var part in parts)
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, out var value)) break;
            result.Add(value);
        }
        return result.Count > 0 ? result.ToArray() : null;
    }

    /// <summary>True if `candidate` is a strictly newer version than `current`.
    /// A missing current version counts as "always newer".</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var candidateParts = ParseVersionParts(candidate);
        if (candidateParts is null) return false;

        var currentParts = ParseVersionParts(current);
        if (currentParts is null) return true;

        var length = Math.Max(candidateParts.Length, currentParts.Length);
        for (var i = 0; i < length; i++)
        {
            var c = i < candidateParts.Length ? candidateParts[i] : 0;
            var cur = i < currentParts.Length ? currentParts[i] : 0;
            if (c != cur) return c > cur;
        }
        return false;
    }
}
