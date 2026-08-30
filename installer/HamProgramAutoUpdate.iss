; ===================================================================
;  Ham Program Auto Update - installer
;
;  Build with Inno Setup 6 or newer (run from the repo root,
;  src\HamProgramAutoUpdate\ - this script now lives inside the repo):
;      iscc installer\HamProgramAutoUpdate.iss
;
;  Expects the published single-file exe at:
;      publish\HamProgramAutoUpdate.exe
;  (build.ps1 puts it there)
; ===================================================================

#define MyAppName "Ham Program Auto Update"
#define MyAppShortName "HamProgramAutoUpdate"
#define MyAppPublisher "K5JSG"
#define MyAppURL "https://github.com/K5JSG/HamProgramAutoUpdate"
#define MyAppExeName "HamProgramAutoUpdate.exe"
; Must match TaskSchedulerService.DashboardTaskPath exactly.
#define DashboardTaskPath "\My Update Programs\Updater Dashboard"

; Overridable from the command line: iscc /DMyAppVersion=1.2.0 ...
#ifndef MyAppVersion
  #define MyAppVersion "1.2.8"
#endif

[Setup]
; Keep this GUID stable forever: it is how Windows recognises an upgrade
; of the same product rather than a second installation.
AppId={{7C4F1B62-2E5D-4A93-9F7C-8B6D3A1E5C40}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}

DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppShortName}
; Older versions installed directly under {autopf}\HamProgramAutoUpdate.
; AppId stays the same across versions (by design, so Windows treats this as
; an upgrade), which would otherwise make Setup silently keep reusing that
; old location forever instead of the new K5JSG subfolder above. This forces
; every install/upgrade onto DefaultDirName; RemoveLegacyInstall in [Code]
; below deletes the old folder itself if one is found there.
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
LicenseFile=..\LICENSE.txt

; The app itself requires administrator, so install machine-wide.
PrivilegesRequired=admin

OutputDir=..\dist
OutputBaseFilename={#MyAppShortName}-v{#MyAppVersion}-setup
SetupIconFile=..\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 10 1809 or newer
MinVersion=10.0.17763

; Offer to shut the app down instead of demanding a reboot
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional shortcuts:"; Flags: unchecked

Name: "startuptask"; Description: "Start the dashboard automatically at logon (creates a scheduled task, no UAC prompt)"; \
    GroupDescription: "Startup:"

Name: "updatestask"; Description: "Check for program updates automatically once a day (creates a scheduled task, no UAC prompt)"; \
    GroupDescription: "Startup:"

[Files]
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md";               DestDir: "{app}"; Flags: ignoreversion isreadme skipifsourcedoesntexist
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; CHIRP's updater runs in-process (Services/Updaters/Programs/Chirp/
; ChirpCloudflareAutomation.cs) - the only thing it still needs on disk is the
; learned Cloudflare Turnstile checkbox coordinates it reads from its own
; folder at runtime. onlyifdoesntexist: an upgrade must not clobber
; coordinates this machine may have since re-learned (see
; ChirpUpdaterSource\coord_test.py) with the shipped
; default.
Source: "..\publish\ChirpUpdaterBinary\captcha_coords.json"; DestDir: "{app}\ChirpUpdaterBinary"; Flags: onlyifdoesntexist skipifsourcedoesntexist

[InstallDelete]
; STANDING POLICY: this app is installed on several machines (see git log /
; session history), upgraded in place rather than reinstalled from scratch,
; and Inno Setup never removes a file on its own just because a newer
; version's [Files] section stopped listing it - it would otherwise sit
; there forever on every one of those machines. So: whenever a file or
; folder that used to be part of this project's shipped install becomes
; obsolete (replaced, superseded, moved), add a "Type: files" or
; "Type: filesandordirs" entry for its exact old path HERE, in the same
; change that retires it, so the next install/upgrade on any machine removes
; it automatically. [UninstallDelete] further below is not a substitute -
; it only cleans up when THIS version is itself later uninstalled, not
; during an in-place upgrade over an older one.
;
; Do NOT add entries for runtime-generated data that must survive an
; upgrade: ChirpUpdaterBinary\bg_profile\ (the Chrome profile - losing it
; resets this machine's accumulated Cloudflare trust), captcha_coords.json
; (may have been re-learned per-machine), downloads\, last_installed_build.txt,
; or anything under {commonappdata}\HamProgramAutoUpdate or
; {localappdata}\HamProgramAutoUpdate. Only ever list paths for things this
; project no longer uses at all, not working state for things it still does.
;
; 2026-08-29: CHIRP's updater used to bundle a separate ~47MB Python exe
; here; a native in-process C# port replaced it (Services/Updaters/Programs/
; Chirp/ChirpCloudflareAutomation.cs).
Type: files; Name: "{app}\ChirpUpdaterBinary\Chirp Update Script.exe"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Create the "Updater Dashboard" scheduled task under the
; "My Update Programs" folder. The app does this itself so the task XML
; lives with the code rather than being duplicated here.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install-task"; \
    StatusMsg: "Creating the startup scheduled task..."; \
    Flags: runhidden waituntilterminated; Tasks: startuptask

Filename: "{app}\{#MyAppExeName}"; Parameters: "--install-updates-task"; \
    StatusMsg: "Creating the daily update-check scheduled task..."; \
    Flags: runhidden waituntilterminated; Tasks: updatestask

; Prefer relaunching through the "Updater Dashboard" scheduled task (just
; created above, if startuptask was checked): it runs "with highest
; privileges", and Task Scheduler elevates that non-interactively - no UAC
; prompt at all, the same way "Program Update Scripts" already runs silently.
; This is NOT the same call as {app}\{#MyAppExeName} directly - launching the
; exe itself needs ShellExecute (CreateProcess fails with code 740 against a
; requireAdministrator manifest), and since Setup itself is already elevated,
; a same-process ShellExecute would normally just inherit that with no
; prompt too - but a bug here previously used "nowait", which for a shellexec
; entry hands the launch off to Explorer's own (non-elevated) token instead
; of Setup's, so the app's manifest then demands a *second* UAC prompt that's
; easy to miss right after Setup's own window closes (confirmed as the cause
; of the app not auto-starting after a self-update). The scheduled task
; avoids that whole problem; falling back to the old direct-launch entry
; below only if that task was never created (startuptask left unchecked).
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#DashboardTaskPath}"""; \
    Description: "Launch the dashboard now"; \
    Flags: postinstall skipifsilent runhidden nowait; Check: DashboardTaskExists

Filename: "{app}\{#MyAppExeName}"; Description: "Launch the dashboard now"; \
    Flags: postinstall nowait skipifsilent shellexec; Check: not DashboardTaskExists

[UninstallDelete]
; Logs are recreated on every run and carry no history of their own (the
; "last updated" dates are in HistoryDir, handled separately below), so
; these are removed unconditionally rather than prompted for.
Type: filesandordirs; Name: "{commonappdata}\HamProgramAutoUpdate\Logs"
; CHIRP's updater keeps its own state/browser-profile/downloads directly in
; this folder (see ChirpCloudflareAutomation.cs) - the normal uninstall only
; removes captcha_coords.json, the one file it explicitly installed, so this
; catches everything else written there at runtime.
Type: filesandordirs; Name: "{app}\ChirpUpdaterBinary"

[UninstallRun]
; Remove the scheduled tasks before the exe is deleted, or they would be
; left behind pointing at a file that no longer exists.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--remove-task"; \
    Flags: runhidden waituntilterminated; RunOnceId: "RemoveDashboardTask"

Filename: "{app}\{#MyAppExeName}"; Parameters: "--remove-updates-task"; \
    Flags: runhidden waituntilterminated; RunOnceId: "RemoveUpdaterTask"

[Code]

// Whether the "Updater Dashboard" scheduled task exists on this machine -
// see the [Run] section comment for why the postinstall launch prefers it.
function DashboardTaskExists(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "{#DashboardTaskPath}"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// Stop a running instance before installing or uninstalling, otherwise the
// exe is locked and the file copy fails.
procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'),
       '/C taskkill /F /IM {#MyAppExeName} >nul 2>&1',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

// Looks up where this AppId is currently registered as installed (works
// whether that was the old default path or a directory the user picked
// themselves), independent of DefaultDirName/UsePreviousAppDir above.
function GetRegisteredInstallDir(): String;
var
  Dir: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C4F1B62-2E5D-4A93-9F7C-8B6D3A1E5C40}_is1',
       'InstallLocation', Dir) then
    Result := Dir;
end;

// One-time migration to the new {autopf}\K5JSG\HamProgramAutoUpdate layout:
// if a previous version is still sitting at whatever folder it was
// registered under, remove its scheduled tasks (via its own exe, the same
// way [UninstallRun] does) and delete that folder, so nothing is left
// behind at the old location and the fresh install below lands cleanly in
// the new one. A no-op once every machine has migrated.
procedure RemoveLegacyInstall();
var
  OldDir, NewDir, OldExe: String;
  ResultCode: Integer;
begin
  OldDir := GetRegisteredInstallDir();
  if OldDir = '' then
    Exit;
  if OldDir[Length(OldDir)] = '\' then
    Delete(OldDir, Length(OldDir), 1);

  NewDir := ExpandConstant('{autopf}\{#MyAppPublisher}\{#MyAppShortName}');
  if (CompareText(OldDir, NewDir) = 0) or (not DirExists(OldDir)) then
    Exit;

  StopRunningApp();

  OldExe := OldDir + '\{#MyAppExeName}';
  if FileExists(OldExe) then
  begin
    Exec(OldExe, '--remove-task', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(OldExe, '--remove-updates-task', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  DelTree(OldDir, True, True, True);
end;

function InitializeSetup(): Boolean;
begin
  RemoveLegacyInstall();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;

// The update history lives outside the install folder on purpose, so it
// survives upgrades. Offer to remove it on uninstall rather than orphaning it.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  HistoryDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    HistoryDir := ExpandConstant('{localappdata}\HamProgramAutoUpdate');
    if DirExists(HistoryDir) then
    begin
      if MsgBox('Also remove the record of when each program was last updated?' + #13#10 + #13#10 +
                HistoryDir + #13#10 + #13#10 +
                'Choose No to keep it, so the dates are still there if you reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(HistoryDir, True, True, True);
    end;
  end;
end;
