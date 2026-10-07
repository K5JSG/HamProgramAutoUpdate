using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services;
using HamProgramAutoUpdate.Services.Updaters.Shared;
using Microsoft.Win32;
using AppInfo = HamProgramAutoUpdate.AppInfo;

namespace HamProgramAutoUpdate.Services.Updaters.Programs;

/// <summary>
/// fldigi and the W1HKJ companion programs that go with it (flamp, flrig,
/// flmsg, flwrap) - one card, since they come from the same author and site
/// and are normally installed together. Each one that's installed is updated
/// on its own; none of them is required.
///
/// Every program has its own folder under https://www.w1hkj.org/files/
/// holding exactly one current "&lt;name&gt;-&lt;ver&gt;_setup.exe" (NSIS,
/// silent with /S). The catch is that each version installs into its own
/// versioned folder ("Program Files\Fldigi-4.2.11", "Program Files
/// (x86)\flrig-2.0.10") with its own Add/Remove Programs entry, so a new
/// version lands alongside the old one instead of replacing it. After the
/// new one is in, the old one is uninstalled silently (its uninstaller only
/// touches its own folder and shortcuts; settings live in the user's
/// "&lt;name&gt;.files" folder and are kept).
///
/// Start menu shortcuts are per-user ("current" NSIS shell context), so when
/// the nightly task runs this as SYSTEM the installer puts the new shortcuts
/// in SYSTEM's own profile and the old uninstaller can't reach the user's
/// old ones. <see cref="MoveStartMenuShortcuts"/> fixes that up afterward in
/// every profile, so the user's Start menu keeps working shortcuts.
/// </summary>
public sealed class FldigiUpdater : UpdaterBase
{
    private const string FilesBaseUrl = "https://www.w1hkj.org/files/";

    /// <param name="Name">Folder and setup-file name on w1hkj.org, and the
    /// Add/Remove Programs DisplayName prefix ("Fldigi 4.2.11", "flrig 2.0.10").</param>
    /// <param name="ProcessNames">Running exes that postpone this program's update.</param>
    /// <param name="StartMenuGroup">Its Start menu folder; the versioned
    /// subfolder inside it is named after the install folder.</param>
    private sealed record Component(string Name, string[] ProcessNames, string StartMenuGroup);

    private static readonly Component[] Components =
    {
        new("fldigi", new[] { "fldigi", "flarq" }, "Fldigi"),
        new("flamp", new[] { "flamp" }, "flamp"),
        new("flrig", new[] { "flrig" }, "flrig"),
        new("flmsg", new[] { "flmsg" }, "flmsg"),
        new("flwrap", new[] { "flwrap" }, "flwrap"),
    };

    private sealed record InstalledCopy(string Version, string InstallDir, string Uninstaller);

    public FldigiUpdater() : base("fldigi", "fldigi", DetectFamily)
    {
    }

    /// <summary>Installed if any of the five is; the card's path/version are
    /// fldigi's own when it's there.</summary>
    private static DetectedTarget DetectFamily()
    {
        foreach (var c in Components)
        {
            var newest = FindInstalled(c).FirstOrDefault();
            if (newest is not null) return DetectedTarget.Found(newest.InstallDir, newest.Version);
        }
        return DetectedTarget.NotFound;
    }

    /// <summary>Every installed copy of this program, newest first. More
    /// than one only if an old version was never removed.</summary>
    private static List<InstalledCopy> FindInstalled(Component c)
    {
        var nameRegex = new Regex($@"^{Regex.Escape(c.Name)}\s+(?<ver>\d+(?:\.\d+)+)\s*$", RegexOptions.IgnoreCase);
        var found = new List<InstalledCopy>();

        foreach (var (hive, keyPath) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                 })
        {
            try
            {
                using var root = hive.OpenSubKey(keyPath);
                if (root is null) continue;

                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(subKeyName);
                    if (sub?.GetValue("DisplayName") is not string displayName) continue;

                    var m = nameRegex.Match(displayName);
                    if (!m.Success) continue;

                    var uninstaller = (sub.GetValue("UninstallString") as string)?.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(uninstaller)) continue;

                    var dir = Path.GetDirectoryName(uninstaller);
                    if (string.IsNullOrWhiteSpace(dir)) continue;

                    var version = sub.GetValue("DisplayVersion") as string ?? m.Groups["ver"].Value;
                    found.Add(new InstalledCopy(version, dir, uninstaller));
                }
            }
            catch (Exception)
            {
                // Treat any registry-access failure as "not found here".
            }
        }

        found.Sort((a, b) =>
            FileVersionHelper.IsNewer(a.Version, b.Version) ? -1 :
            FileVersionHelper.IsNewer(b.Version, a.Version) ? 1 : 0);
        return found;
    }

    private static bool SameVersion(string a, string b) =>
        !FileVersionHelper.IsNewer(a, b) && !FileVersionHelper.IsNewer(b, a);

    private enum ComponentOutcome { NotInstalled, UpToDate, Updated, Postponed, Failed }

    public override async Task<UpdateResult> RunAsync(UpdaterContext ctx)
    {
        if (!DetectTarget().IsInstalled)
            return SkipNotInstalled(ctx, "Neither fldigi nor any of its companion programs");

        var updated = new List<string>();
        var failed = new List<string>();

        foreach (var c in Components)
        {
            ComponentOutcome outcome;
            string? newVersion = null;
            try
            {
                (outcome, newVersion) = await UpdateComponentAsync(ctx, c);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Log.Line($"{c.Name}: could not be updated ({ex.Message}).");
                outcome = ComponentOutcome.Failed;
            }

            if (outcome == ComponentOutcome.Updated) updated.Add($"{c.Name} {newVersion}");
            if (outcome == ComponentOutcome.Failed) failed.Add(c.Name);
        }

        if (failed.Count > 0)
        {
            ctx.Log.Line($"fldigi Updater FAILED: could not update {string.Join(", ", failed)}");
            return UpdateResult.Failed($"Could not update {string.Join(", ", failed)}");
        }

        ctx.Log.Line("fldigi Updater completed successfully");
        return updated.Count > 0
            ? UpdateResult.Updated(null, string.Join(", ", updated))
            : UpdateResult.UpToDate();
    }

    private async Task<(ComponentOutcome, string?)> UpdateComponentAsync(UpdaterContext ctx, Component c)
    {
        var installed = FindInstalled(c);
        if (installed.Count == 0) return (ComponentOutcome.NotInstalled, null);

        var current = installed[0];

        ctx.Log.Line($"{c.Name}: checking {FilesBaseUrl}{c.Name}/ ...");
        var (latest, setupFile) = await FetchLatestAsync(ctx, c);
        if (latest is null || setupFile is null)
        {
            ctx.Log.Line($"{c.Name}: could not find a Windows setup file on the download page.");
            return (ComponentOutcome.Failed, null);
        }

        if (!ctx.Force && !FileVersionHelper.IsNewer(latest, current.Version))
        {
            ctx.Log.Line($"{c.Name}: already up to date (installed {current.Version}, latest {latest}).");
            return (ComponentOutcome.UpToDate, null);
        }

        ctx.Log.Line($"{c.Name}: new version available: {latest} (installed: {current.Version})");

        if (ctx.DryRun)
        {
            ctx.Log.Line($"{c.Name}: dry run - would download {setupFile} and install {latest}.");
            return (ComponentOutcome.UpToDate, null);
        }

        if (!AppInfo.IsElevated)
        {
            ctx.Log.Line($"{c.Name}: administrator privileges are required to install updates.");
            return (ComponentOutcome.Failed, null);
        }

        // Programs you operate with, not tray apps - don't pull one out from
        // under the user mid-QSO; the next run picks it up.
        if (c.ProcessNames.Any(p => ProcessFinder.FindByName(p).Length > 0))
        {
            ctx.Log.Line($"{c.Name}: the program is currently running - postponing this update.");
            return (ComponentOutcome.Postponed, null);
        }

        var tempDir = Path.Combine(AppPaths.TempDir, $"FldigiUpdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var downloadPath = Path.Combine(tempDir, setupFile);

        try
        {
            var url = FilesBaseUrl + c.Name + "/" + setupFile;
            ctx.Log.Line($"{c.Name}: downloading {url} ...");
            var (downloadOk, downloadError) = await HttpDownloader.DownloadToFileAsync(
                ctx.Http, url, downloadPath, ctx.CancellationToken);
            if (!downloadOk)
            {
                ctx.Log.Line($"{c.Name}: download did not complete ({downloadError}).");
                return (ComponentOutcome.Failed, null);
            }

            // Keep the user's chosen location: if the current copy lives in
            // the standard "<name>-<ver>" folder, put the new one next to it
            // under the same naming, instead of the installer's own default.
            var args = new List<string> { "/S" };
            var currentFolder = Path.GetFileName(current.InstallDir.TrimEnd('\\'));
            var parent = Path.GetDirectoryName(current.InstallDir.TrimEnd('\\'));
            var folderRegex = new Regex($@"^(?<prefix>{Regex.Escape(c.Name)})-{Regex.Escape(current.Version)}$", RegexOptions.IgnoreCase);
            if (folderRegex.Match(currentFolder) is { Success: true } fm && parent is not null)
                args.Add($"/D={Path.Combine(parent, $"{fm.Groups["prefix"].Value}-{latest}")}");

            ctx.Log.Line($"{c.Name}: installing {latest} silently...");
            var (installOk, exitCode) = await SilentExeInstaller.RunAsync(
                downloadPath, args, ctx.CancellationToken, timeout: TimeSpan.FromSeconds(300));
            if (!installOk)
            {
                ctx.Log.Line($"{c.Name}: installer exited with code {exitCode}.");
                return (ComponentOutcome.Failed, null);
            }

            var fresh = FindInstalled(c).FirstOrDefault(i => SameVersion(i.Version, latest));
            if (fresh is null)
            {
                ctx.Log.Line($"{c.Name}: the installer finished but {latest} is not in Add/Remove Programs.");
                return (ComponentOutcome.Failed, null);
            }

            foreach (var old in FindInstalled(c).Where(i => !SameVersion(i.Version, latest)))
            {
                if (string.Equals(old.InstallDir.TrimEnd('\\'), fresh.InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    continue;

                await RemoveOldCopyAsync(ctx, c, old);
                MoveStartMenuShortcuts(ctx, c, old.InstallDir, fresh.InstallDir);
            }
            RemoveSystemProfileShortcuts(c, fresh.InstallDir);

            ctx.Log.Line($"{c.Name}: Updated to {latest}.");
            return (ComponentOutcome.Updated, latest);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Newest "&lt;name&gt;-&lt;ver&gt;_setup.exe" listed in the
    /// program's w1hkj.org files folder (normally the only one).</summary>
    private static async Task<(string? version, string? fileName)> FetchLatestAsync(UpdaterContext ctx, Component c)
    {
        string html;
        try
        {
            html = await HttpDownloader.GetStringAsync(ctx.Http, $"{FilesBaseUrl}{c.Name}/", ctx.CancellationToken);
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Log.Line($"{c.Name}: could not reach w1hkj.org ({ex.Message}).");
            return (null, null);
        }

        var regex = new Regex($@"href=""(?<file>{Regex.Escape(c.Name)}-(?<ver>\d+(?:\.\d+)+)_setup\.exe)""", RegexOptions.IgnoreCase);
        string? bestVersion = null, bestFile = null;
        foreach (Match m in regex.Matches(html))
        {
            var ver = m.Groups["ver"].Value;
            if (bestVersion is null || FileVersionHelper.IsNewer(ver, bestVersion))
            {
                bestVersion = ver;
                bestFile = m.Groups["file"].Value;
            }
        }
        return (bestVersion, bestFile);
    }

    /// <summary>Runs the old copy's own NSIS uninstaller silently. _?= makes
    /// it run in place and wait (without it, it copies itself to %TEMP% and
    /// returns at once), at the cost of leaving its own exe and folder
    /// behind - removed here afterward.</summary>
    private static async Task RemoveOldCopyAsync(UpdaterContext ctx, Component c, InstalledCopy old)
    {
        if (!File.Exists(old.Uninstaller))
        {
            ctx.Log.Line($"{c.Name}: old {old.Version} has no uninstaller at {old.Uninstaller} - left in place.");
            return;
        }

        ctx.Log.Line($"{c.Name}: removing old version {old.Version} from {old.InstallDir} ...");
        var (ok, exitCode) = await SilentExeInstaller.RunAsync(
            old.Uninstaller,
            new[] { "/S", $"_?={old.InstallDir.TrimEnd('\\')}" },
            ctx.CancellationToken,
            timeout: TimeSpan.FromSeconds(120));
        if (!ok)
        {
            ctx.Log.Line($"{c.Name}: the old version's uninstaller exited with code {exitCode} - it may still be listed.");
            return;
        }

        try
        {
            File.Delete(old.Uninstaller);
            if (Directory.Exists(old.InstallDir) && !Directory.EnumerateFileSystemEntries(old.InstallDir).Any())
                Directory.Delete(old.InstallDir);
        }
        catch (Exception)
        {
            // An empty leftover folder isn't worth failing the update over.
        }
    }

    /// <summary>Every Start menu "Programs" folder a shortcut could be in:
    /// each user profile's (including SYSTEM's) plus the all-users one.</summary>
    private static IEnumerable<string> StartMenuProgramDirs()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            foreach (var sid in profiles?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var p = profiles!.OpenSubKey(sid);
                if (p?.GetValue("ProfileImagePath") is string path && !string.IsNullOrWhiteSpace(path))
                {
                    dirs.Add(Path.Combine(Environment.ExpandEnvironmentVariables(path),
                        @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"));
                }
            }
        }
        catch (Exception)
        {
            // Fall back to just this process's own and the all-users folder.
        }

        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));
    }

    /// <summary>
    /// Points a user's leftover "&lt;group&gt;\&lt;old folder&gt;" shortcuts
    /// at the new install and renames the folder to match, wherever the old
    /// uninstaller couldn't remove them itself (it only reaches the profile
    /// it runs in - SYSTEM's, on the nightly run). Where the new folder is
    /// already there too, the old one is just removed.
    /// </summary>
    private static void MoveStartMenuShortcuts(UpdaterContext ctx, Component c, string oldDir, string newDir)
    {
        var oldFolder = Path.GetFileName(oldDir.TrimEnd('\\'));
        var newFolder = Path.GetFileName(newDir.TrimEnd('\\'));

        foreach (var programs in StartMenuProgramDirs())
        {
            var oldPath = Path.Combine(programs, c.StartMenuGroup, oldFolder);
            if (!Directory.Exists(oldPath)) continue;

            try
            {
                var newPath = Path.Combine(programs, c.StartMenuGroup, newFolder);
                if (Directory.Exists(newPath))
                {
                    Directory.Delete(oldPath, recursive: true);
                    continue;
                }

                foreach (var lnk in Directory.EnumerateFiles(oldPath, "*.lnk"))
                    RetargetShortcut(lnk, oldDir.TrimEnd('\\'), newDir.TrimEnd('\\'));

                Directory.Move(oldPath, newPath);
                ctx.Log.Line($"{c.Name}: moved Start menu shortcuts in {programs} to {newFolder}.");
            }
            catch (Exception ex)
            {
                ctx.Log.Line($"{c.Name}: could not move the Start menu shortcuts in {programs} ({ex.Message}).");
            }
        }
    }

    /// <summary>On the nightly run the installer's new shortcuts land in
    /// SYSTEM's own Start menu, where nobody ever sees them.</summary>
    private static void RemoveSystemProfileShortcuts(Component c, string newDir)
    {
        if (!UserSessionLauncher.IsRunningAsSystem) return;

        try
        {
            var group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), c.StartMenuGroup);
            var folder = Path.Combine(group, Path.GetFileName(newDir.TrimEnd('\\')));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            if (Directory.Exists(group) && !Directory.EnumerateFileSystemEntries(group).Any()) Directory.Delete(group);
        }
        catch (Exception)
        {
            // Invisible clutter at worst.
        }
    }

    private static void RetargetShortcut(string lnkPath, string oldDir, string newDir)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(lnkPath);
            try
            {
                // Only set what actually changes: fldigi's "Documentation"
                // shortcuts point at a web page, which WScript reports as an
                // empty TargetPath - writing that "" back wiped the link
                // (seen live). Their WorkingDirectory still gets moved.
                string target = link.TargetPath;
                string workDir = link.WorkingDirectory;
                string icon = link.IconLocation;
                var changed = false;

                if (Replace(target) is var newTarget && newTarget != target) { link.TargetPath = newTarget; changed = true; }
                if (Replace(workDir) is var newWorkDir && newWorkDir != workDir) { link.WorkingDirectory = newWorkDir; changed = true; }
                if (Replace(icon) is var newIcon && newIcon != icon) { link.IconLocation = newIcon; changed = true; }
                if (changed) link.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        string Replace(string value) =>
            string.IsNullOrEmpty(value) ? value : value.Replace(oldDir, newDir, StringComparison.OrdinalIgnoreCase);
    }
}
