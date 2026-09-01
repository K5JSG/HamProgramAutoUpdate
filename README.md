# Ham Program Auto Update

A Windows tray app that checks a fixed set of ham radio helper programs for
updates, installs them silently, and shows at a glance when each one last
checked and when it was last actually updated.

The update logic for every tracked program runs in-process (no external
updater scripts) - it scrapes each program's own download page, compares
versions, and downloads and installs silently when one is out of date.

Programs tracked: BktTimeSync, CHIRP, GridTracker, Ham Radio Deluxe, Log4OM,
N1MM Logger+, NetLogger, POTA Activator, RT Systems, TQSL, WSJT-X and
WSJT-X Improved.

---

## Install

Download `HamProgramAutoUpdate-x.y.z-setup.exe` from
[Releases](../../releases) and run it.

The installer puts the app in `C:\Program Files\K5JSG\HamProgramAutoUpdate`, adds it to
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

Every tracked program's update log lives together under:

```
%ProgramData%\HamProgramAutoUpdate\Logs\
```

That is machine-wide, not per-user, because the app runs elevated and the
scheduled tasks may run under a different session. A handful of programs
still have per-program state that has to live where the program itself
expects it to (Log4OM's portable config, WSJT-X's install folder, etc.) -
`Services/UpdaterCatalog.cs` and each updater in `Services/Updaters/Programs/`
document those cases individually.

A program's card appears once it's detected as installed (or, failing that,
once it has a log) and disappears again as soon as it's no longer installed -
its log and update history stay on disk regardless, so reinstalling it later
brings the card straight back with its full history intact rather than
starting blank. A program the dashboard has never seen simply gets no card
yet.

Its own record of update dates is kept at:

```
%LOCALAPPDATA%\HamProgramAutoUpdate\update_history.json
```

That is outside the install folder on purpose, so it survives upgrades and
reinstalls. Clearing a log never loses the update date.

---

## Why it needs administrator

Installing anything - an MSI via `msiexec`, a silent Inno Setup or NSIS
installer - normally means a UAC prompt. Since the update logic now runs
in-process rather than shelling out to separate installer scripts, the
dashboard itself always runs elevated (its manifest requires it), so every
program it updates inherits that instead of prompting per program.

Launched from its own scheduled task, Task Scheduler elevates it silently
and there is no prompt at all - the normal path for both the daily
auto-update run and the app starting at logon.

---

## Building it yourself

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) and,
for the installer, [Inno Setup](https://jrsoftware.org/isdl.php) 6 or newer.

`build.ps1` and the `installer\` folder are tracked in this repository -
`build.ps1 -Version 1.1.0` publishes the exe and, if Inno Setup is found,
compiles the installer:

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
and the default `$Version` in `build.ps1`, commit, then push a version tag:

```powershell
git tag V1.2.x
git push origin master V1.2.x
```

Pushing a `V*.*.*` tag runs `.github\workflows\release.yml` on GitHub
Actions, which builds the exe and installer itself (via `build.ps1`, same
as building locally) and publishes the release - no local build or
`gh release create` needed. It reads the version straight from the tag, so
the tag and the version bumped into the csproj/build.ps1 above must match.

The tag is `V<version>` (capital V); the release title the workflow
generates uses lowercase `v<version>`. The workflow always publishes
immediately (not draft/prerelease) with an asset ending in `-setup.exe` -
that's the exact suffix `SelfUpdateService` matches on to find the
installer to offer - plus the standalone exe.

---

## Adding a program

Three pieces:

1. A new `IProgramUpdater` in `Services/Updaters/Programs/` - detects
   whether the program is installed (a registry lookup or a fixed path),
   checks its download page or release feed for the latest version, and
   downloads and installs silently when out of date. The existing updaters
   there are the best reference; no two vendors' sites/installers behave
   quite the same way, so expect to actually run it live against the real
   target rather than trust it from code review alone.
2. One entry in `UpdaterRegistry.All` (`Services/Updaters/UpdaterRegistry.cs`).
3. One entry in `UpdaterCatalog.Entries` (`Services/UpdaterCatalog.cs`) -
   its key (matching the updater's `Key`), display name, and where its log
   should live. The card then appears automatically.

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
