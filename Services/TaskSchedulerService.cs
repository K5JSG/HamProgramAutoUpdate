using System.Diagnostics;
using System.Text;

namespace HamProgramAutoUpdate.Services;

/// <summary>
/// Manages the Windows scheduled tasks through schtasks.exe.
///
/// schtasks is used rather than the TaskScheduler COM interop or the
/// Microsoft.Win32.TaskScheduler NuGet package so the app keeps zero external
/// dependencies and stays a single self-contained exe.
///
/// Two tasks matter here:
///   "Updater Dashboard"      - starts this app at logon (created by us)
///   "Program Update Scripts" - runs all the updater exes (created by you)
/// </summary>
public static class TaskSchedulerService
{
    public const string FolderName = "My Update Programs";
    public const string DashboardTaskName = "Updater Dashboard";
    public const string UpdaterTaskName = "Program Update Scripts";

    public static string DashboardTaskPath => $@"\{FolderName}\{DashboardTaskName}";
    public static string UpdaterTaskPath => $@"\{FolderName}\{UpdaterTaskName}";

    /// <summary>Names tried when looking for the updater task, in order.</summary>
    private static readonly string[] UpdaterTaskCandidates =
    {
        $@"\{FolderName}\{UpdaterTaskName}",
        UpdaterTaskName,
    };

    // ------------------------------------------------------------ schtasks

    private static (int code, string stdout, string stderr) RunSchtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "", "schtasks did not start");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);

            return (proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    public static bool TaskExists(string taskPath)
        => RunSchtasks("/Query", "/TN", taskPath).code == 0;

    /// <summary>Which updater-task name actually exists, or null.</summary>
    public static string? ResolveUpdaterTask()
        => UpdaterTaskCandidates.FirstOrDefault(TaskExists);

    /// <summary>Status string for a task ("Running", "Ready", ...) or null.</summary>
    public static string? GetStatus(string taskPath)
    {
        var (code, stdout, _) = RunSchtasks("/Query", "/TN", taskPath, "/FO", "LIST", "/V");
        if (code != 0) return null;

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
                return trimmed[7..].Trim();
        }
        return null;
    }

    public static bool IsRunning(string taskPath)
        => string.Equals(GetStatus(taskPath), "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trigger a task. Returns an error string on failure, null on success.</summary>
    public static string? RunTask(string taskPath)
    {
        var (code, stdout, stderr) = RunSchtasks("/Run", "/TN", taskPath);
        if (code == 0) return null;

        var message = (stderr + stdout).Trim();
        if (string.IsNullOrEmpty(message)) message = $"schtasks returned {code}";
        if (message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
            message += "\n\nThe dashboard needs to run as administrator to start this task.";

        return message;
    }

    // ------------------------------------------------- dashboard autostart

    /// <summary>
    /// XML for the "Updater Dashboard" task: starts this exe at logon of any
    /// administrator account, with highest privileges. Scoped to
    /// Administrators (not Users) because the exe's manifest requires
    /// elevation and Task Scheduler can never silently elevate a genuinely
    /// standard account - targeting Users would just mean the task silently
    /// fails to launch at a non-admin logon.
    /// </summary>
    private static string BuildDashboardTaskXml(string exePath)
    {
        var author = $@"{Environment.UserDomainName}\{Environment.UserName}";
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Date>{now}</Date>
    <Author>{System.Security.SecurityElement.Escape(author)}</Author>
    <Description>Starts the Ham Program Auto Update tray app at logon.</Description>
    <URI>{DashboardTaskPath}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-544</GroupId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>
      <WorkingDirectory>{System.Security.SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    /// <summary>
    /// Create (or replace) the "Updater Dashboard" logon task, creating the
    /// "My Update Programs" folder if it does not already exist.
    ///
    /// schtasks creates any missing folders in the task path automatically,
    /// so no separate folder step is needed.
    /// </summary>
    public static (bool ok, string? error) InstallDashboardTask(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return (false, "Could not determine this program's own path.");

        var xmlPath = Path.Combine(Path.GetTempPath(),
            $"HamProgramAutoUpdateTask_{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks /XML requires UTF-16, matching the XML declaration
            File.WriteAllText(xmlPath, BuildDashboardTaskXml(exePath), Encoding.Unicode);

            var (code, stdout, stderr) = RunSchtasks(
                "/Create", "/TN", DashboardTaskPath, "/XML", xmlPath, "/F");

            if (code != 0)
            {
                var message = (stderr + stdout).Trim();
                if (string.IsNullOrEmpty(message)) message = $"schtasks returned {code}";
                return (false, message);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { }
        }
    }

    /// <summary>Remove the logon task. Used by the uninstaller.</summary>
    public static (bool ok, string? error) RemoveDashboardTask()
    {
        var (code, stdout, stderr) = RunSchtasks("/Delete", "/TN", DashboardTaskPath, "/F");

        // 1 is also returned when the task simply is not there, which is fine
        if (code == 0) return (true, null);

        var message = (stderr + stdout).Trim();
        if (message.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            return (true, null);

        return (false, string.IsNullOrEmpty(message) ? $"schtasks returned {code}" : message);
    }

    public static bool DashboardTaskInstalled() => TaskExists(DashboardTaskPath);

    // -------------------------------------------------------- update runs

    /// <summary>
    /// XML for the "Program Update Scripts" task: runs this exe with
    /// --run-updates once a day. Every program's update logic now lives
    /// in-process (see Services/Updaters), so one task/one action replaces
    /// what used to be a hand-maintained action per external updater exe.
    ///
    /// Two triggers: the daily CalendarTrigger, plus a LogonTrigger so a PC
    /// that was off (or asleep) through the 3am run still gets checked as
    /// soon as someone logs back on. StartWhenAvailable covers the case
    /// where the PC is on but the scheduler simply missed the boundary, but
    /// it fires whenever the Task Scheduler service next notices, not
    /// reliably at logon - the LogonTrigger is the direct fix for "off
    /// overnight". Each updater already no-ops when the installed version is
    /// current (see HeadlessUpdateRunner/IProgramUpdater.RunAsync), so an
    /// extra run right after a successful 3am run costs nothing beyond the
    /// version checks. The short delay keeps it from competing with the
    /// Updater Dashboard task for the first couple minutes after logon.
    /// </summary>
    private static string BuildUpdaterTaskXml(string exePath, TimeOnly dailyTime)
    {
        var author = $@"{Environment.UserDomainName}\{Environment.UserName}";
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var startBoundary = $"{DateTime.Today:yyyy-MM-dd}T{dailyTime:HH:mm:ss}";

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Date>{now}</Date>
    <Author>{System.Security.SecurityElement.Escape(author)}</Author>
    <Description>Checks every tracked program for updates and installs them silently.</Description>
    <URI>{UpdaterTaskPath}</URI>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>{startBoundary}</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByDay>
        <DaysInterval>1</DaysInterval>
      </ScheduleByDay>
    </CalendarTrigger>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT2M</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-544</GroupId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>
      <Arguments>--run-updates</Arguments>
      <WorkingDirectory>{System.Security.SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    /// <summary>Create (or replace) the "Program Update Scripts" task, which
    /// runs `HamProgramAutoUpdate.exe --run-updates` once a day at
    /// <paramref name="dailyTime"/> (default 03:00) and again at every
    /// logon (with a short delay), so a PC that was off overnight still gets
    /// checked. The Task Scheduler UI can be used afterward to change the
    /// time like any normal task.</summary>
    public static (bool ok, string? error) InstallUpdaterTask(string? exePath = null, TimeOnly? dailyTime = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return (false, "Could not determine this program's own path.");

        var xmlPath = Path.Combine(Path.GetTempPath(),
            $"HamProgramAutoUpdateUpdaterTask_{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(xmlPath, BuildUpdaterTaskXml(exePath, dailyTime ?? new TimeOnly(3, 0)), Encoding.Unicode);

            var (code, stdout, stderr) = RunSchtasks(
                "/Create", "/TN", UpdaterTaskPath, "/XML", xmlPath, "/F");

            if (code != 0)
            {
                var message = (stderr + stdout).Trim();
                if (string.IsNullOrEmpty(message)) message = $"schtasks returned {code}";
                return (false, message);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { }
        }
    }

    /// <summary>Remove the "Program Update Scripts" task. Used by the uninstaller.</summary>
    public static (bool ok, string? error) RemoveUpdaterTask()
    {
        var (code, stdout, stderr) = RunSchtasks("/Delete", "/TN", UpdaterTaskPath, "/F");

        if (code == 0) return (true, null);

        var message = (stderr + stdout).Trim();
        if (message.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            return (true, null);

        return (false, string.IsNullOrEmpty(message) ? $"schtasks returned {code}" : message);
    }
}
