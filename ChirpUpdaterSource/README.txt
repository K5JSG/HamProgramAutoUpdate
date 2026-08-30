CHIRP-next Auto-Updater - Python implementation (DORMANT FALLBACK)
====================================================================

STATUS: not part of the current build. CHIRP's updater was ported to native
C# - see ..\Services\Updaters\Programs\ChirpUpdater.cs and
..\Services\Updaters\Programs\Chirp\ChirpCloudflareAutomation.cs, which use
the same technique this Python version pioneered (hidden desktop, headed
Chrome, PostMessage clicks calibrated against a RenderWidget, an in-browser
download since Cloudflare refuses the .exe URL to a plain HTTP client). That
port was validated empirically: on the machine it was built on, a click posted
this same way to a hidden-desktop Chrome window registered as a real
isTrusted DOM event, which is the specific thing an earlier PuppeteerSharp C#
attempt could never get working. Only captcha_coords.json from this folder is
still used (see the csproj) - everything else here is kept only as a
reference/fallback in case the native port needs re-diagnosing against a
future Cloudflare change; build.ps1 no longer builds or ships this exe.

To bring this back as the active implementation: revert ChirpUpdater.cs to
run "Chirp Update Script.exe" as an external process again (see git history
for the version that did), and restore build.ps1's step that built this
folder into ChirpUpdaterBinary\ before publishing.


FILES IN THIS FOLDER
--------------------
Chirp Update Script.py     The updater.
Chirp Update Script.spec   PyInstaller spec (SeleniumBase + curl_cffi).
build_chirp.bat            Builds the exe into .\dist by hand (not called by
                            build.ps1 anymore - see STATUS above).
coord_test.py               Re-learns the Turnstile checkbox position if it moves.
captcha_coords.json         KEEP THIS. The learned checkbox position (117,332).
                             Still actively used - the csproj ships it next to
                             HamProgramAutoUpdate.exe for the native C# port to
                             read, independent of everything else in this folder.

Generated at runtime, safe to delete (all gitignored): chirp_updater.log,
build_log.txt, build\, dist\, downloads\, downloaded_files\, bg_profile\,
coord_profile*\, last_installed_build.txt.


WHERE THIS RUNS FROM AND WRITES TO (if rebuilt and run by hand)
------------------------------------------------------------------
This script/exe does not write anything under the user's Documents folder.
Its log, install-state, browser profile, and downloads all live next to
whatever copy of it is currently running - see _base_dir() in the .py.

To rebuild by hand:
    python -m pip install seleniumbase curl_cffi pyinstaller
    build_chirp.bat


HOW IT WORKS
------------
This machine is scored low enough by Cloudflare that Turnstile demands an
interactive click - the other two PCs are waved through without one. So:

  1. The script relaunches itself onto a hidden Windows desktop.
  2. SeleniumBase CDP mode drives Chrome there.
  3. The Turnstile checkbox is clicked with PostMessage, not PyAutoGUI.
     SendInput is a silent no-op on a non-input desktop (verified: the cursor
     stays pinned at 0,0), but a posted WM_LBUTTONDOWN lands in the target
     window's message queue and Chrome reports isTrusted=true.
  4. Chrome creates one RenderWidget HWND per out-of-process iframe - three on
     this page (1x1, 890x64, and the real 1084x605 viewport). Posting to the
     wrong one put clicks 559px off target, so each is calibrated at runtime
     with a mousemove probe and the offset-(0,0) one is chosen.
  5. Chrome downloads the installer. Cloudflare refuses the .exe URL to
     curl_cffi even holding a valid cf_clearance cookie, so the download has to
     happen inside the browser session.
  6. Silent install via /S, then the state file is updated.

Nothing is visible and the mouse is never touched.


SCHEDULED TASK
--------------
  General:    "Run with highest privileges" = ON
              "Run whether user is logged on or not" = OFF
              (session 0 has no desktop; Chrome needs one, even a hidden one)
  Conditions: no idle conditions needed - nothing touches the cursor.


IF IT BREAKS LATER
------------------
Symptom: the log shows all 15 click attempts failing.
Cause:   Chrome or Cloudflare moved the checkbox.
Fix:     python coord_test.py learn      (visible; the mouse moves briefly)
         then confirm with:
         python coord_test.py replay     (hidden; the mouse must NOT move)

The coordinates are tied to a 1100x700 window (WIN_W/WIN_H in the script).
Changing that window size invalidates them - relearn if you change it.


THE OTHER TWO MACHINES
----------------------
jsgay-desktop and k5jsg-laptop clear Turnstile with no click at all and still
run the older undetected_chromedriver script. Leave them alone - none of the
above applies to them.
