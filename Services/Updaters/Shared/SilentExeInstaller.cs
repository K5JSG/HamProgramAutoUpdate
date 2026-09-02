using System.Diagnostics;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

public enum InstallerKind
{
    Unknown,
    Inno,
    Nsis,
    WixBurn,
    InstallShield,
}

/// <summary>
/// Runs a downloaded installer exe silently. Most programs' installer type is
/// already known from research (see each Programs/*Updater.cs), so they build
/// their own exact flag list. Sniff()/DefaultSilentArgs() exist for HRD, which
/// downloads from a page that doesn't say what installer technology it used.
/// </summary>
public static class SilentExeInstaller
{
    /// <summary>Reads the first few MB of the exe looking for a known
    /// installer engine's signature string. Best-effort - HRD's Python script
    /// fell back to trying every known flag set in turn when this returns
    /// Unknown, and the C# port should do the same.</summary>
    public static InstallerKind Sniff(string exePath, int maxBytes = 8 * 1024 * 1024)
    {
        try
        {
            using var stream = File.OpenRead(exePath);
            var length = (int)Math.Min(stream.Length, maxBytes);
            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = stream.Read(buffer, read, length - read);
                if (n == 0) break;
                read += n;
            }

            var text = System.Text.Encoding.Latin1.GetString(buffer, 0, read);

            if (text.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase)) return InstallerKind.Inno;
            if (text.Contains("Nullsoft", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("NSIS", StringComparison.OrdinalIgnoreCase)) return InstallerKind.Nsis;
            if (text.Contains("WixBundle", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Burn", StringComparison.Ordinal)) return InstallerKind.WixBurn;
            if (text.Contains("InstallShield", StringComparison.OrdinalIgnoreCase)) return InstallerKind.InstallShield;

            return InstallerKind.Unknown;
        }
        catch (Exception)
        {
            return InstallerKind.Unknown;
        }
    }

    public static string[] DefaultSilentArgs(InstallerKind kind) => kind switch
    {
        InstallerKind.Inno => new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" },
        InstallerKind.Nsis => new[] { "/S" },
        InstallerKind.WixBurn => new[] { "/quiet", "/norestart" },
        InstallerKind.InstallShield => new[] { "/s", "/v/qn" },
        _ => Array.Empty<string>(),
    };

    /// <summary>All four flag sets, for the "installer type unknown - try
    /// everything" fallback.</summary>
    public static IEnumerable<string[]> AllKnownSilentArgs() =>
        new[] { InstallerKind.Inno, InstallerKind.Nsis, InstallerKind.WixBurn, InstallerKind.InstallShield }
            .Select(DefaultSilentArgs);

    public static async Task<(bool ok, int exitCode)> RunAsync(
        string exePath, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // NSIS's /D=<dir> switch must be the last token on the command line
        // and completely unquoted, even when <dir> contains spaces - NSIS
        // silently ignores a quoted /D= and falls back to its compiled-in
        // default directory instead (confirmed live - see WsjtxUpdater's
        // doc comment on this). ArgumentList quotes any token containing a
        // space, which a /D= whose directory has one (e.g. the default
        // "C:\Program Files\WSJT-X") would trip - build the command line by
        // hand in that case so only the trailing /D= token stays raw.
        if (args.Count > 0 && args[^1].StartsWith("/D=", StringComparison.Ordinal))
        {
            var quotedHead = args.Take(args.Count - 1).Select(QuoteIfNeeded);
            psi.Arguments = string.Join(' ', quotedHead.Append(args[^1]));
        }
        else
        {
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null) return (false, -1);

        using var timeoutCts = timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(linked?.Token ?? ct);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
            return (false, -1);
        }

        // NSIS reports ERROR_CANCELLED (1223) even on some successful silent
        // runs - N1MM's Python script treated both 0 and 1223 as success.
        var ok = proc.ExitCode is 0 or 1223;
        return (ok, proc.ExitCode);
    }

    /// <summary>Wraps <paramref name="arg"/> in quotes if it contains
    /// whitespace. Not a general command-line quoting implementation (it
    /// doesn't escape embedded quote characters) - every non-/D= argument
    /// passed through here is a hardcoded literal flag, never one that
    /// could contain a `"`.</summary>
    private static string QuoteIfNeeded(string arg) =>
        arg.IndexOfAny(new[] { ' ', '\t' }) >= 0 ? $"\"{arg}\"" : arg;
}
