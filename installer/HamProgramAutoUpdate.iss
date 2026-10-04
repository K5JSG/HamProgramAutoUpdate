; ===================================================================
;  Ham Program Auto Update - installer
;
;  Build with Inno Setup 6 or newer (run from the repo root):
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
; This installer's AppId (see [Setup]), also used by [Code] to tell its own
; Installed Apps entry apart from older copies.
#define MyAppId "{{7C4F1B62-2E5D-4A93-9F7C-8B6D3A1E5C40}"
; Must match TaskSchedulerService.DashboardTaskPath exactly.
; Must match TaskSchedulerService.DashboardTaskPath exactly.
#define DashboardTaskPath "\K5JSG\HamProgramAutoUpdate\Updater Dashboard"

; Overridable from the command line: iscc /DMyAppVersion=1.2.0 ...
#ifndef MyAppVersion
  #define MyAppVersion "1.2.8"
#endif

[Setup]
; Keep this GUID stable forever: it is how Windows recognises an upgrade
; of the same product rather than a second installation.
AppId={#MyAppId}
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
; STANDING POLICY: every upgrade empties the program folder before the new
; files go in (CleanAppFolder in [Code]), so a file retired from the install
; needs nothing here - EXCEPT inside ChirpUpdaterBinary\, which that cleanup
; deliberately skips because it holds CHIRP's runtime state. Whenever a
; shipped file in ChirpUpdaterBinary\ becomes obsolete (replaced,
; superseded, moved), add a "Type: files" or "Type: filesandordirs" entry
; for its exact old path HERE, in the same change that retires it, so the
; next upgrade on every machine removes it. [UninstallDelete] further below
; is not a substitute - it only cleans up when THIS version is itself later
; uninstalled, not during an in-place upgrade over an older one.
;
; Do NOT add entries for runtime-generated data that must survive an
; upgrade: ChirpUpdaterBinary\bg_profile\ (the Chrome profile - losing it
; resets this machine's accumulated Cloudflare trust), captcha_coords.json
; (may have been re-learned per-machine), downloads\ or
; last_installed_build.txt. Only ever list paths for things this project no
; longer uses at all, not working state for things it still does.
;
; 2026-08-29: CHIRP's updater used to bundle a separate ~47MB Python exe
; here; a native in-process C# port replaced it (Services/Updaters/Programs/
; Chirp/ChirpCloudflareAutomation.cs).
Type: files; Name: "{app}\ChirpUpdaterBinary\Chirp Update Script.exe"

[Icons]
; The desktop shortcut must come BEFORE the Start menu ones. When it was
; created after them, every upgrade made Explorer drop the desktop icon into
; the next free spot instead of leaving it where the user had put it.
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Create the "Updater Dashboard" scheduled task under the
; \K5JSG\HamProgramAutoUpdate folder, first moving both tasks out of the old
; "My Update Programs" folder on upgrade. The app does this itself so the task XML
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

// Updates install IN PLACE over the existing copy (same AppId), so Installed
// Apps keeps one entry that just shows the new version number, and the
// desktop and Start menu shortcuts stay where they are. Before the new files
// go in:
//   - any OTHER installed copy of this app (an entry registered somewhere
//     other than this installer's own) is silently uninstalled;
//   - the program folder is emptied (all but the uninstaller and
//     ChirpUpdaterBinary\), so no file that an older version shipped and this
//     one dropped is left behind.
// The app's data - update history, settings and logs, all under
// {commonappdata}\HamProgramAutoUpdate (versions before 1.9.0 kept the
// history in {localappdata}\HamProgramAutoUpdate) - lives outside the
// program folder and is never touched by an upgrade.

const
  UninstallKeyRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';

var
  OldInstallDirs: TArrayOfString;

function IsOurProductName(const Name: String): Boolean;
begin
  Result := CompareText(Name, '{#MyAppName}') = 0;
end;

// This installer's own Installed Apps entry: the one an upgrade updates in
// place instead of removing. Admin installs in 64-bit mode register it under
// HKLM's 64-bit view.
function IsOwnEntry(RootKey: Integer; const SubkeyName: String): Boolean;
begin
  Result := (RootKey = HKLM64) and
            (CompareText(SubkeyName, ExpandConstant('{#MyAppId}_is1')) = 0);
end;

function SameDir(const A, B: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(A), RemoveBackslashUnlessRoot(B)) = 0;
end;

procedure RememberOldInstallDir(const Dir: String);
var
  N: Integer;
begin
  if Dir = '' then Exit;
  N := GetArrayLength(OldInstallDirs);
  SetArrayLength(OldInstallDirs, N + 1);
  OldInstallDirs[N] := Dir;
end;

// Only ever touches a folder that is clearly this app's own (named after the
// product), never some general-purpose folder the user may have picked on
// the directory page.
function IsOurAppFolder(const Dir: String): Boolean;
begin
  Result := (Dir <> '') and DirExists(Dir) and
            (CompareText(ExtractFileName(Dir), '{#MyAppShortName}') = 0);
end;

procedure DeleteAppFolder(Dir: String);
begin
  Dir := RemoveBackslashUnlessRoot(Dir);
  if IsOurAppFolder(Dir) then
  begin
    DelTree(Dir, True, True, True);
    // The K5JSG publisher folder above it - RemoveDir only succeeds if empty
    RemoveDir(ExtractFileDir(Dir));
  end;
end;

// An Inno uninstaller re-launches itself from %TEMP% and deletes its own
// unins*.exe as the very last step, so this confirms it has really finished
// before the new files go in.
procedure WaitForFileGone(const FileName: String; TimeoutMs: Integer);
begin
  while FileExists(FileName) and (TimeoutMs > 0) do
  begin
    Sleep(250);
    TimeoutMs := TimeoutMs - 250;
  end;
end;

// Silently uninstalls every installed copy of this app registered under
// RootKey, except this installer's own entry (see IsOwnEntry). Returns an
// error message, or '' if everything went fine.
function UninstallOldVersions(RootKey: Integer): String;
var
  Names: TArrayOfString;
  I, ResultCode: Integer;
  Key, DisplayName, Publisher, Location, Uninstaller: String;
  IsMsi: Cardinal;
begin
  Result := '';
  if not RegGetSubkeyNames(RootKey, UninstallKeyRoot, Names) then Exit;

  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Key := UninstallKeyRoot + '\' + Names[I];
    if not IsOwnEntry(RootKey, Names[I]) and
       RegQueryStringValue(RootKey, Key, 'DisplayName', DisplayName) and
       IsOurProductName(DisplayName) and
       RegQueryStringValue(RootKey, Key, 'Publisher', Publisher) and
       (CompareText(Publisher, '{#MyAppPublisher}') = 0) then
    begin
      if RegQueryStringValue(RootKey, Key, 'InstallLocation', Location) then
        RememberOldInstallDir(Location);

      if RegQueryDWordValue(RootKey, Key, 'WindowsInstaller', IsMsi) and (IsMsi = 1) then
      begin
        // An MSI build: the subkey name is its ProductCode, and it may not
        // have recorded InstallLocation, so fall back to the usual folder.
        RememberOldInstallDir(ExpandConstant('{autopf}\{#MyAppPublisher}\') + DisplayName);
        if not Exec(ExpandConstant('{sys}\msiexec.exe'),
                    '/x ' + Names[I] + ' /qn /norestart',
                    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
          ResultCode := -1;
        // 1605 = already gone, 3010 = done but wants a reboot
        if (ResultCode <> 0) and (ResultCode <> 1605) and (ResultCode <> 3010) then
        begin
          Result := Format('%s could not be removed automatically (error %d).', [DisplayName, ResultCode]);
          Exit;
        end;
      end
      else if RegQueryStringValue(RootKey, Key, 'UninstallString', Uninstaller) then
      begin
        Uninstaller := RemoveQuotes(Uninstaller);
        RememberOldInstallDir(ExtractFileDir(Uninstaller));
        if FileExists(Uninstaller) then
        begin
          if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART',
                      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
            ResultCode := -1;
          if ResultCode <> 0 then
          begin
            Result := Format('%s could not be removed automatically (error %d).', [DisplayName, ResultCode]);
            Exit;
          end;
          WaitForFileGone(Uninstaller, 60000);
        end;
        // An entry whose uninstaller is missing (folder deleted by hand)
        // would otherwise linger in Installed Apps forever.
        if RegKeyExists(RootKey, Key) then
          RegDeleteKeyIncludingSubkeys(RootKey, Key);
      end;
    end;
  end;
end;

// Removes every other installed copy (see UninstallOldVersions) and whatever
// their uninstallers left behind. Returns an error message for
// PrepareToInstall, or '' if everything went fine.
function RemoveOtherCopies(): String;
var
  I: Integer;
begin
  Result := UninstallOldVersions(HKLM64);
  if Result = '' then Result := UninstallOldVersions(HKLM32);
  if Result = '' then Result := UninstallOldVersions(HKCU);

  if Result <> '' then
  begin
    Result := Result + #13#10#13#10 +
      'Please uninstall it from Settings > Apps > Installed apps, then run this setup again.';
    Exit;
  end;

  // Files the old uninstallers didn't remove themselves. The folder being
  // upgraded is left to CleanAppFolder, which keeps its uninstaller.
  for I := 0 to GetArrayLength(OldInstallDirs) - 1 do
    if not SameDir(OldInstallDirs[I], ExpandConstant('{app}')) then
      DeleteAppFolder(OldInstallDirs[I]);
end;

// What the folder cleanup below leaves in place: the uninstaller
// (unins000.exe/.dat/.msg), which the upgraded copy carries on using, and
// ChirpUpdaterBinary\, where CHIRP's updater keeps this machine's browser
// profile, learned captcha coordinates, downloads and last-installed build
// (see ChirpUpdater.cs) - state that must survive upgrades.
function KeepOnUpgrade(const Name: String): Boolean;
begin
  Result := (CompareText(Copy(Name, 1, 5), 'unins') = 0) or
            (CompareText(Name, 'ChirpUpdaterBinary') = 0);
end;

// Empties the program folder just before the new version's files are copied
// in, so nothing an older version installed and this one no longer ships is
// left behind.
procedure CleanAppFolder();
var
  Dir: String;
  FindRec: TFindRec;
begin
  Dir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if not IsOurAppFolder(Dir) then Exit;

  if FindFirst(Dir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') and not KeepOnUpgrade(FindRec.Name) then
        begin
          if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
            DelTree(Dir + '\' + FindRec.Name, True, True, True)
          else
            DeleteFile(Dir + '\' + FindRec.Name);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanAppFolder();
end;

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
  Result := RemoveOtherCopies();
end;

// Looks up where this AppId is currently registered as installed (works
// whether that was the old default path or a directory the user picked
// themselves), independent of DefaultDirName/UsePreviousAppDir above.
function GetRegisteredInstallDir(): String;
var
  Dir: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, UninstallKeyRoot + '\' + ExpandConstant('{#MyAppId}_is1'),
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

// Deletes the files directly in Dir (not its subfolders, e.g. Logs\).
procedure DeleteFilesIn(const Dir: String);
var
  FindRec: TFindRec;
begin
  if FindFirst(Dir + '\*', FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
          DeleteFile(Dir + '\' + FindRec.Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// The app's data lives outside the install folder on purpose, so it survives
// upgrades. Offer to remove it on uninstall rather than orphaning it: the
// shared data files (not the Logs\ folder), plus the per-user folder versions
// before 1.9.0 used.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, LegacyDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\HamProgramAutoUpdate');
    LegacyDir := ExpandConstant('{localappdata}\HamProgramAutoUpdate');
    if FileExists(DataDir + '\update_history.json') or DirExists(LegacyDir) then
    begin
      if MsgBox('Also remove the record of when each program was last updated, and the app''s settings?' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                'Choose No to keep them, so the dates are still there if you reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DeleteFilesIn(DataDir);
        if DirExists(LegacyDir) then DelTree(LegacyDir, True, True, True);
      end;
    end;
  end;
end;
