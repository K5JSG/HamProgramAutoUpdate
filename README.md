# Ham Program Auto Update

A Windows tray app that shows, at a glance, when each of your ham radio
programs last checked for updates and when it was last actually updated.

It does not update anything itself. It reads the logs your existing updater
scripts write, and can launch them on demand.

Programs tracked: BktTimeSync, CHIRP, GridTracker, Ham Radio Deluxe,
N1MM Logger+, NetLogger, POTA Activator, RT Systems, TQSL and WSJT-X.

---

## Install

Download `HamProgramAutoUpdate-x.y.z-setup.exe` from
[Releases](../../releases) and run it.

The installer puts the app in `C:\Program Files\HamProgramAutoUpdate`, adds it to
Add/Remove Programs, and offers to create a scheduled task so it starts with
Windows.

There is also a standalone `HamProgramAutoUpdate.exe` on the release page if you
would rather not install anything: copy it to any writable folder and run it.
No .NET runtime is needed either way.

Neither file is code-signed, so Windows SmartScreen will warn the first time.
Choose **More info** then **Run anyway**.

---

## What it shows

Each program gets a card with:

- **Status** of its most recent run: Success, Failed, Running or Empty
- **Last Run** - when the updater last checked
- **Last Update** - when a new version was last actually installed, highlighted
  when that was in the last three days

A small teal dot next to *Last Update* means the date came from the dashboard's
own records because the log no longer goes back that far.

Per card: **Run** the updater now, **Clear** its log, or **View Log**.
Across the top: run every updater through the scheduled task, clear all logs,
or refresh.

---

## Where things live

The dashboard looks for logs and updater programs under:

```
%USERPROFILE%\Documents\Ham Radio\
```

Paths resolve per user at runtime, so the same build works on any Windows
account with no configuration. A program with neither a log nor an updater
present simply gets no card.

Its own record of update dates is kept at:

```
%LOCALAPPDATA%\HamProgramAutoUpdate\update_history.json
```

That is outside the install folder on purpose, so it survives upgrades and
reinstalls. Clearing a log never loses the update date.

---

## Why it needs administrator

Several of the updaters install software through `msiexec` and carry their own
administrator manifests. A normal process cannot launch them without a UAC
prompt for each one.

The dashboard therefore requests elevation itself, and the updaters it starts
inherit that. Launched from its scheduled task, there is no prompt at all.

---

## Building it yourself

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) and,
for the installer, [Inno Setup](https://jrsoftware.org/isdl.php) 6 or newer.

`build.ps1` and the `installer\` folder live one level above this repository
(alongside it, not inside it) - `build.ps1 -Version 1.1.0` publishes the exe
and, if Inno Setup is found, compiles the installer:

```powershell
.\build.ps1                      # exe + installer
.\build.ps1 -Version 1.1.0       # stamp a version
.\build.ps1 -SkipInstaller       # exe only
```

Output lands in `publish\` (the exe) and `dist\` (the installer).

During development, run the exe directly rather than using `dotnet run` -
`dotnet run` cannot start a process that requires elevation:

```powershell
dotnet build
Start-Process .\bin\Debug\net8.0-windows\HamProgramAutoUpdate.exe
```

A test project lives alongside the app at `Tests\HamProgramAutoUpdate.Tests`;
`dotnet test HamProgramAutoUpdate.slnx` runs it (and runs automatically on
every push via `.github\workflows\build.yml`).

---

## Releasing

Bump `Version`/`FileVersion`/`AssemblyVersion` in `HamProgramAutoUpdate.csproj`
and the default `$Version` in `build.ps1`, commit, then:

```powershell
.\build.ps1
git tag V1.2.x
git push origin master V1.2.x
gh release create V1.2.x dist\HamProgramAutoUpdate-v1.2.x-setup.exe dist\HamProgramAutoUpdate-v1.2.x.exe `
    --title "Ham Program Auto Update v1.2.x"
```

The tag is `V<version>` (capital V); the release title uses lowercase
`v<version>`. The release must be published (not draft/prerelease) and must
include an asset ending in `-setup.exe` - that's the exact suffix
`SelfUpdateService` matches on to find the installer to offer.

---

## Adding a program

Add one entry to `Entries` in
`Services/UpdaterCatalog.cs`, giving its key, display
name, log path and updater path relative to `Documents\Ham Radio`. Nothing
else needs changing - the card appears automatically.

If the new updater words its log differently, `LogParser.cs` may need a
pattern adding. Two rules matter there:

- A run's **closing line** decides success or failure, so an error that was
  recovered from does not show as a failure.
- Negation phrases such as "No update needed" are checked first and can never
  count as an update.

---

## Licence

Free to use, copy, modify, and distribute for non-commercial purposes only.
Commercial use requires prior written authorization from the copyright
holder. See [LICENSE](LICENSE.txt).
