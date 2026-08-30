"""CHIRP-next auto-updater for the segay laptop - fully background, no mouse.

How this works, and why it is built this way
--------------------------------------------
segay is scored low enough by Cloudflare that Turnstile demands an *interactive*
click, which jsgay-desktop and k5jsg-laptop never get asked for. Ruled out along
the way, each with evidence: undetected_chromedriver 3.5.5 (never solves it here),
CDP detach/reconnect cycles, --no-sandbox, window geometry, profile seeding, Chrome
version, and direct HTTP (Cloudflare challenges the .exe URLs too, even with
Chrome's TLS fingerprint via curl_cffi).

What does work, and what this script does:

1. Relaunch onto a hidden Windows desktop, so nothing is ever visible.
2. Drive Chrome with SeleniumBase CDP mode.
3. Click the Turnstile checkbox with PostMessage, NOT PyAutoGUI. SendInput is a
   silent no-op on a non-input desktop (verified: cursor pinned at 0,0), but a
   posted WM_LBUTTONDOWN lands in the target window's queue and Chrome reports it
   as isTrusted=true. Window messages do not care which desktop is displayed.
4. Pick the right window to post to. Chrome creates one RenderWidget HWND per
   out-of-process iframe; on this page there are three (1x1, 890x64, and the real
   1084x605 viewport). Posting to the wrong one put clicks 559px off target. Each
   widget is calibrated at runtime with a mousemove probe and the viewport one -
   offset (0,0) - is selected.
5. Hand the resulting cf_clearance cookie to curl_cffi to download the installer,
   then install silently.

The checkbox coordinates come from captcha_coords.json, produced by coord_test.py's
learn phase on the visible desktop. WINDOW SIZE MUST MATCH what was used then
(1100x700), or the layout shifts and the coordinates miss.

Requires: pip install seleniumbase curl_cffi
Run elevated (the CHIRP installer needs admin).

Where this writes its data
---------------------------
Everything (log, install-state, learned captcha coordinates, the background
Chrome profile, and in-flight downloads) lives in the same folder this script
(or, once frozen, this .exe) runs from - see _base_dir() below. In production
that folder is ChirpUpdaterBinary\\, the sibling folder HamProgramAutoUpdate.exe
ships this exe from, so nothing chirp-related is scattered into the user's
Documents folder. See build_chirp.bat / build.ps1 for how this gets built and
copied there, and ChirpUpdater.cs for the one-time migration of an older
install's files out of Documents\\Ham Radio\\Chirp Update Script.
"""
import os
import re
import sys
import json
import time
import ctypes
import datetime
import subprocess
from ctypes import wintypes

# --------------------------------------------------------------------------
# Configuration
# --------------------------------------------------------------------------
BASE_URL = "https://archive.chirpmyradio.com"
ARCHIVE_URL = f"{BASE_URL}/chirp_next/"
BUILD_PATTERN = r'next-(\d{8})/'
LINK_PATTERN = r'next-\d{8}/'

# Must match the window used when the coordinates were learned.
WIN_W, WIN_H = 1100, 700
DEFAULT_CLICK = (117, 332)

MIN_INSTALLER_BYTES = 5 * 1024 * 1024
PAGE_SETTLE = int(os.environ.get("CHIRP_PAGE_SETTLE", "8"))
CLICK_SETTLE = int(os.environ.get("CHIRP_CLICK_SETTLE", "6"))


def _base_dir():
    """The folder this script's own files (log/state/profile/downloads) live
    in: next to the frozen exe in production, next to the .py in dev - never
    a fixed path under the user's Documents folder."""
    if getattr(sys, "frozen", False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


TARGET_DIR = _base_dir()
os.makedirs(TARGET_DIR, exist_ok=True)
LOG_PATH = os.path.join(TARGET_DIR, "chirp_updater.log")
STATE_PATH = os.path.join(TARGET_DIR, "last_installed_build.txt")
COORDS_PATH = os.path.join(TARGET_DIR, "captcha_coords.json")
PROFILE_DIR = os.path.join(TARGET_DIR, "bg_profile")
DOWNLOAD_DIR = os.path.join(TARGET_DIR, "downloads")

CHIRP_EXE_CANDIDATES = [
    r"C:\Program Files (x86)\CHIRP\chirpwx.exe",
    r"C:\Program Files\CHIRP\chirpwx.exe",
]

MAX_LOG_RUNS = 3
MARKER = "=" * 40

GENERIC_ALL = 0x10000000
STARTF_USESHOWWINDOW = 0x00000001
CREATE_UNICODE_ENVIRONMENT = 0x00000400
WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0200, 0x0201, 0x0202
MK_LBUTTON = 0x0001
HIDDEN_ENV = "CHIRP_BG_CHILD"


# --------------------------------------------------------------------------
# Logging
# --------------------------------------------------------------------------
def log_msg(text):
    line = f"[{datetime.datetime.now():%H:%M:%S}] {text}"
    try:
        print(line, flush=True)
    except Exception:
        pass
    try:
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(line + "\n")
            f.flush()
            os.fsync(f.fileno())
    except Exception:
        pass


def rotate_log():
    header = f"{MARKER}\nCHIRP UPDATER {datetime.datetime.now():%Y-%m-%d %H:%M:%S}\n{MARKER}\n"
    runs = []
    if os.path.exists(LOG_PATH):
        try:
            with open(LOG_PATH, encoding="utf-8") as f:
                text = f.read()
            sp = re.compile(r'(?=^' + re.escape(MARKER) + r'\nCHIRP UPDATER)', re.MULTILINE)
            runs = [c for c in sp.split(text) if c.strip()]
        except Exception:
            runs = []
    runs = runs[-(MAX_LOG_RUNS - 1):] if MAX_LOG_RUNS > 1 else []
    try:
        with open(LOG_PATH, "w", encoding="utf-8") as f:
            f.write("".join(runs))
            f.write(header)
    except Exception:
        pass


# --------------------------------------------------------------------------
# Win32 plumbing
# --------------------------------------------------------------------------
class STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD), ("lpReserved", wintypes.LPWSTR),
        ("lpDesktop", wintypes.LPWSTR), ("lpTitle", wintypes.LPWSTR),
        ("dwX", wintypes.DWORD), ("dwY", wintypes.DWORD),
        ("dwXSize", wintypes.DWORD), ("dwYSize", wintypes.DWORD),
        ("dwXCountChars", wintypes.DWORD), ("dwYCountChars", wintypes.DWORD),
        ("dwFillAttribute", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
        ("wShowWindow", wintypes.WORD), ("cbReserved2", wintypes.WORD),
        ("lpReserved2", ctypes.POINTER(ctypes.c_byte)),
        ("hStdInput", wintypes.HANDLE), ("hStdOutput", wintypes.HANDLE),
        ("hStdError", wintypes.HANDLE),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("hProcess", wintypes.HANDLE), ("hThread", wintypes.HANDLE),
        ("dwProcessId", wintypes.DWORD), ("dwThreadId", wintypes.DWORD),
    ]


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def is_admin():
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def relaunch_on_hidden_desktop():
    """Spawn ourselves on an invisible desktop. Returns True in the parent."""
    if os.name != "nt" or os.environ.get(HIDDEN_ENV) == "1":
        return False

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    name = "chirp_bg_desktop"
    hdesk = user32.CreateDesktopW(name, None, None, 0, GENERIC_ALL, None)
    if not hdesk:
        log_msg(f"CreateDesktopW failed ({ctypes.get_last_error()}) - "
                "cannot run hidden; aborting rather than taking over the mouse")
        return False

    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    si.lpDesktop = name
    si.dwFlags = STARTF_USESHOWWINDOW
    si.wShowWindow = 1
    pi = PROCESS_INFORMATION()

    env = os.environ.copy()
    env[HIDDEN_ENV] = "1"
    block = "\0".join(f"{k}={v}" for k, v in env.items()) + "\0\0"

    if getattr(sys, "frozen", False):
        cmd = f'"{sys.executable}"'
    else:
        cmd = f'"{sys.executable}" "{os.path.abspath(__file__)}"'

    ok = kernel32.CreateProcessW(
        None, ctypes.create_unicode_buffer(cmd), None, None, False,
        CREATE_UNICODE_ENVIRONMENT, ctypes.create_unicode_buffer(block), None,
        ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        log_msg(f"CreateProcessW failed ({kernel32.GetLastError()})")
        user32.CloseDesktop(hdesk)
        return False

    kernel32.WaitForSingleObject(pi.hProcess, 0xFFFFFFFF)
    kernel32.CloseHandle(pi.hProcess)
    kernel32.CloseHandle(pi.hThread)
    user32.CloseDesktop(hdesk)
    return True


def chrome_windows():
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    found = []
    Proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def collect(hwnd, _l):
        buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, buf, 256)
        if buf.value == "Chrome_WidgetWin_1":
            t = ctypes.create_unicode_buffer(512)
            user32.GetWindowTextW(hwnd, t, 512)
            found.append((hwnd, t.value))
        return True

    user32.EnumWindows(Proc(collect), 0)
    return found


def render_widgets(hwnd):
    """Every RenderWidget child, with its client size."""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    res = []
    Proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def collect(child, _l):
        buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(child, buf, 256)
        if "RenderWidget" in buf.value:
            r = RECT()
            user32.GetClientRect(child, ctypes.byref(r))
            res.append((child, r.right - r.left, r.bottom - r.top))
        return True

    user32.EnumChildWindows(hwnd, Proc(collect), 0)
    return res


def post_click(hwnd, x, y):
    """Approach, press, release - paced so it does not look instantaneous."""
    u32 = ctypes.WinDLL("user32", use_last_error=True)
    lp = (y << 16) | (x & 0xFFFF)
    for dx, dy in ((-25, -18), (-12, -8), (-4, -2), (0, 0)):
        u32.PostMessageW(hwnd, WM_MOUSEMOVE, 0, ((y + dy) << 16) | ((x + dx) & 0xFFFF))
        time.sleep(0.04)
    time.sleep(0.12)
    u32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lp)
    time.sleep(0.09)
    u32.PostMessageW(hwnd, WM_LBUTTONUP, 0, lp)


def post_move(hwnd, x, y):
    ctypes.WinDLL("user32", use_last_error=True).PostMessageW(
        hwnd, WM_MOUSEMOVE, 0, (y << 16) | (x & 0xFFFF))


# --------------------------------------------------------------------------
# Challenge handling
# --------------------------------------------------------------------------
def load_click_target():
    if os.path.exists(COORDS_PATH):
        try:
            d = json.load(open(COORDS_PATH, encoding="utf-8"))
            x, y = int(d["client_x"]), int(d["client_y"])
            log_msg(f"Checkbox coordinates from captcha_coords.json: ({x},{y})")
            return x, y
        except Exception as e:
            log_msg(f"Could not read captcha_coords.json ({e}) - using default")
    log_msg(f"Using default checkbox coordinates {DEFAULT_CLICK}")
    return DEFAULT_CLICK


def pick_viewport_widget(sb, widgets, tgt_x, tgt_y):
    """Calibrate each RenderWidget; return (hwnd, post_x, post_y) for the viewport.

    Chrome has one RenderWidget per out-of-process iframe. Only the viewport one
    maps client coordinates 1:1 to page coordinates; posting to a 1x1 stub put
    clicks 559px off. Probing each and comparing what the page reports identifies
    the right one without guessing.
    """
    try:
        sb.execute_script("""
            window.__mm = null;
            document.addEventListener('mousemove', function(e){
                window.__mm = [e.clientX, e.clientY];
            }, true);
        """)
    except Exception as e:
        log_msg(f"  could not install calibration listener: {e}")

    for hw, cw, ch in widgets:
        try:
            sb.execute_script("(function(){ window.__mm = null; })()")
        except Exception:
            pass
        probe = (min(400, max(10, cw // 2)), min(300, max(10, ch // 2)))
        post_move(hw, *probe)
        time.sleep(1.0)
        try:
            got = sb.execute_script("(function(){ return window.__mm; })()")
        except Exception:
            got = None
        if not got:
            log_msg(f"  widget {hw} ({cw}x{ch}): no response")
            continue
        ox, oy = got[0] - probe[0], got[1] - probe[1]
        px, py = tgt_x - ox, tgt_y - oy
        reachable = 0 <= px < max(cw, 1) and 0 <= py < max(ch, 1)
        log_msg(f"  widget {hw} ({cw}x{ch}): offset=({ox},{oy}) -> post ({px},{py}) "
                f"{'REACHABLE' if reachable else 'out of bounds'}")
        if reachable:
            return hw, px, py
    return None, None, None


def clear_challenge(sb, tgt_x, tgt_y):
    """Return True once the build listing is visible."""
    wins = chrome_windows()
    if not wins:
        log_msg("ERROR: no Chrome window found on this desktop")
        return False
    top = next((h for h, t in wins if t.strip()), wins[0][0])
    widgets = render_widgets(top)
    log_msg(f"Chrome window {top}, {len(widgets)} render widget(s)")

    hw, px, py = pick_viewport_widget(sb, widgets, tgt_x, tgt_y)
    if hw is None:
        log_msg("ERROR: could not identify the viewport render widget")
        return False

    # Primary: the learned checkbox position.
    attempts = [(px, py)]
    # Fallbacks: a small ring, then a coarse sweep, in case the layout shifted.
    for d in (-10, 10, -20, 20):
        attempts.append((px + d, py))
        attempts.append((px, py + d))
    for fy in (0.45, 0.55):
        for fx in (0.08, 0.14, 0.20):
            attempts.append((int(WIN_W * fx), int(WIN_H * fy)))

    for i, (x, y) in enumerate(attempts, 1):
        if x < 0 or y < 0:
            continue
        log_msg(f"  posting click {i} at ({x},{y})")
        post_click(hw, x, y)
        time.sleep(CLICK_SETTLE)
        html = sb.get_page_source()
        if re.search(LINK_PATTERN, html):
            log_msg(f"Challenge cleared by the click at ({x},{y})")
            return True
    return False


def fetch_listing():
    """Returns (html, cookies, user_agent) or (None, None, None)."""
    from seleniumbase import SB

    tgt_x, tgt_y = load_click_target()
    os.makedirs(PROFILE_DIR, exist_ok=True)

    with SB(uc=True, headless=False, user_data_dir=PROFILE_DIR) as sb:
        sb.activate_cdp_mode(ARCHIVE_URL)
        sb.sleep(PAGE_SETTLE)
        try:
            # MUST match the size the coordinates were learned at.
            sb.set_window_rect(0, 0, WIN_W, WIN_H)
        except Exception as e:
            log_msg(f"could not set window rect: {e}")
        sb.sleep(2)

        html = sb.get_page_source()
        if re.search(LINK_PATTERN, html):
            log_msg("No challenge presented - page loaded directly")
        else:
            log_msg("Challenge present - clicking it via PostMessage (no mouse used)")
            if not clear_challenge(sb, tgt_x, tgt_y):
                log_msg(f"ERROR: challenge not cleared. title={sb.get_title()!r}")
                return None, None, None, None
            html = sb.get_page_source()

        cookies = {}
        for getter in ("get_cookies", "get_all_cookies"):
            try:
                for c in getattr(sb, getter)():
                    n = c.get("name") if isinstance(c, dict) else getattr(c, "name", None)
                    v = c.get("value") if isinstance(c, dict) else getattr(c, "value", None)
                    if n and v is not None:
                        cookies[n] = v
                if cookies:
                    break
            except Exception:
                continue
        log_msg(f"Collected {len(cookies)} cookie(s): {sorted(cookies)}"
                f"{'' if 'cf_clearance' in cookies else '  <-- no cf_clearance!'}")

        try:
            ua = sb.execute_script("(function(){ return navigator.userAgent; })()")
        except Exception:
            ua = None

        # Decide here, while the session is still open, whether a download is
        # needed - the browser is the only client Cloudflare will serve the .exe to.
        date = newest_build(html)
        if not date:
            return html, cookies, ua, None
        build = f"next-{date}"
        last = read_state()
        if last == build and find_chirp_exe():
            return html, cookies, ua, None

        log_msg(f"Update needed ({last or 'none'} -> {build})")
        installer = download_installer(date, cookies, ua)
        if not installer:
            log_msg("Falling back to downloading inside the browser session")
            installer = browser_download(sb, date)
        return html, cookies, ua, installer


# --------------------------------------------------------------------------
# Download / install
# --------------------------------------------------------------------------
def browser_download(sb, date):
    """Download the installer inside the browser session that holds the clearance.

    Handing cf_clearance to curl_cffi is enough for the listing page but NOT for
    the .exe URL - Cloudflare refuses it with 403 cf-mitigated=challenge no matter
    which impersonation profile is used. The browser itself has every property
    Cloudflare is checking, so navigating there and letting Chrome save the file
    sidesteps the problem entirely.
    """
    url = f"{BASE_URL}/chirp_next/next-{date}/chirp-next-{date}-installer.exe"
    name = f"chirp-next-{date}-installer.exe"

    try:
        dl_dir = sb.get_downloads_folder()
    except Exception:
        dl_dir = os.path.join(os.getcwd(), "downloaded_files")
    os.makedirs(dl_dir, exist_ok=True)
    log_msg(f"Browser download folder: {dl_dir}")

    before = set(os.listdir(dl_dir))

    log_msg(f"Navigating to {url} to let Chrome download it")
    for opener in ("open", "get"):
        try:
            getattr(sb, opener)(url)
            break
        except Exception as e:
            log_msg(f"  sb.{opener}() raised {type(e).__name__} (expected for a download)")

    # Wait for a new file to appear and stop growing.
    deadline = time.time() + 300
    target = None
    last_size = -1
    stable = 0
    while time.time() < deadline:
        time.sleep(2)
        try:
            now = set(os.listdir(dl_dir))
        except Exception:
            continue
        fresh = [f for f in (now - before)
                 if not f.endswith((".crdownload", ".tmp", ".part"))]
        if not fresh:
            continue
        cand = os.path.join(dl_dir, fresh[0])
        try:
            size = os.path.getsize(cand)
        except Exception:
            continue
        if size == last_size and size > 0:
            stable += 1
            if stable >= 2:
                target = cand
                break
        else:
            stable = 0
            last_size = size

    if not target:
        log_msg("ERROR: no completed download appeared within 5 minutes")
        return None

    size = os.path.getsize(target)
    if size < MIN_INSTALLER_BYTES:
        log_msg(f"ERROR: downloaded file is only {size:,} bytes")
        return None
    with open(target, "rb") as f:
        if f.read(2) != b"MZ":
            log_msg("ERROR: downloaded file is not a Windows executable")
            return None

    os.makedirs(DOWNLOAD_DIR, exist_ok=True)
    dest = os.path.join(DOWNLOAD_DIR, name)
    try:
        import shutil
        shutil.move(target, dest)
    except Exception:
        dest = target
    log_msg(f"Browser download complete: {size:,} bytes -> {dest}")
    return dest


def newest_build(html):
    d = sorted(set(re.findall(BUILD_PATTERN, html)))
    return d[-1] if d else None


def download_installer(date, cookies, ua):
    """Fetch the installer, replaying Chrome's TLS fingerprint via curl_cffi.

    IMPORTANT: do NOT send the browser's real User-Agent by default. cf_clearance is
    validated against the TLS fingerprint *and* the UA together. curl_cffi's
    impersonation presents its own Chrome UA that matches its JA3; overriding that
    header with Chrome 152's real UA creates a UA/TLS mismatch and Cloudflare
    returns 403 cf-mitigated=challenge. (Observed exactly that, and the earlier
    working version succeeded only because its UA lookup silently failed.)
    So: try without the UA override first, and only then with it.
    """
    from curl_cffi import requests as creq

    url = f"{BASE_URL}/chirp_next/next-{date}/chirp-next-{date}-installer.exe"
    os.makedirs(DOWNLOAD_DIR, exist_ok=True)
    dest = os.path.join(DOWNLOAD_DIR, f"chirp-next-{date}-installer.exe")

    strategies = [
        ("impersonate=chrome, no UA override", {"Referer": ARCHIVE_URL}, "chrome"),
        ("impersonate=chrome110, no UA override", {"Referer": ARCHIVE_URL}, "chrome110"),
    ]
    if ua:
        strategies.append(
            ("impersonate=chrome, with browser UA",
             {"Referer": ARCHIVE_URL, "User-Agent": ua}, "chrome"))
    strategies.append(("no impersonation", {"Referer": ARCHIVE_URL}, None))

    log_msg(f"Downloading {url}")
    for label, headers, imp in strategies:
        try:
            kwargs = dict(cookies=cookies or {}, headers=headers, timeout=300)
            if imp:
                kwargs["impersonate"] = imp
            r = creq.get(url, **kwargs)
        except Exception as e:
            log_msg(f"  [{label}] request failed: {e}")
            continue

        if r.status_code != 200:
            log_msg(f"  [{label}] HTTP {r.status_code} "
                    f"(cf-mitigated={r.headers.get('cf-mitigated')})")
            continue

        data = r.content
        # Cloudflare serves interstitials with status 200 under the .exe name
        # (CHIRP bug #11986), so check size and PE magic before trusting it.
        if len(data) < MIN_INSTALLER_BYTES:
            log_msg(f"  [{label}] only {len(data):,} bytes - challenge page, not an installer")
            continue
        if data[:2] != b"MZ":
            log_msg(f"  [{label}] not a Windows executable (magic={data[:2]!r})")
            continue

        with open(dest, "wb") as f:
            f.write(data)
        log_msg(f"Download complete via [{label}]: {len(data):,} bytes")
        return dest

    log_msg("ERROR: every download strategy was refused by Cloudflare")
    return None


def install_silently(path):
    if not is_admin():
        log_msg("ERROR: not elevated - the CHIRP installer needs administrator rights")
        return False
    try:
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        p = subprocess.Popen([path, "/S"], stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, creationflags=flags)
        p.wait(timeout=300)
        log_msg(f"Installer exited with code {p.returncode}")
        return p.returncode == 0
    except Exception as e:
        log_msg(f"ERROR: installer failed: {e}")
        return False


def find_chirp_exe():
    for p in CHIRP_EXE_CANDIDATES:
        if os.path.exists(p):
            return p
    return None


def read_state():
    if os.path.exists(STATE_PATH):
        try:
            return open(STATE_PATH, encoding="utf-8").read().strip()
        except Exception:
            pass
    return None


def write_state(build):
    try:
        with open(STATE_PATH, "w", encoding="utf-8") as f:
            f.write(build)
    except Exception as e:
        log_msg(f"WARNING: could not write state file: {e}")


# --------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------
def run():
    log_msg(f"CHIRP-next updater (background build)  PID={os.getpid()}")
    log_msg(f"Hidden desktop: {os.environ.get(HIDDEN_ENV) == '1'}  Elevated: {is_admin()}")

    chirp = find_chirp_exe()
    if chirp:
        log_msg(f"Installed CHIRP: "
                f"{datetime.datetime.fromtimestamp(os.path.getmtime(chirp)):%Y-%m-%d %H:%M:%S}")
    else:
        log_msg("CHIRP not installed - will perform a fresh install")

    last = read_state()
    log_msg(f"Last installed build: {last or 'none recorded'}")

    html, cookies, ua, installer = fetch_listing()
    if not html:
        return False

    date = newest_build(html)
    if not date:
        log_msg("ERROR: no build links found")
        return False

    build = f"next-{date}"
    log_msg(f"Latest available build: {build}")

    if last == build and chirp:
        log_msg("Already up to date - nothing to do")
        return True

    if not installer:
        log_msg("ERROR: could not obtain the installer")
        return False

    log_msg("Installing silently...")
    if not install_silently(installer):
        return False

    write_state(build)
    log_msg(f"SUCCESS: now on {build}")
    try:
        os.remove(installer)
    except Exception:
        pass
    return True


if __name__ == "__main__":
    if os.environ.get(HIDDEN_ENV) != "1":
        rotate_log()
        if relaunch_on_hidden_desktop():
            # The child on the hidden desktop did the work and wrote the log.
            sys.exit(0)
        log_msg("WARNING: could not create a hidden desktop - running on the "
                "current desktop, which means a Chrome window will be visible.")

    ok = False
    try:
        ok = run()
    except ImportError as e:
        log_msg(f"ERROR: missing dependency: {e}")
        log_msg("Install with:  python -m pip install seleniumbase curl_cffi")
    except Exception as e:
        import traceback
        log_msg(f"ERROR: {e}")
        log_msg(traceback.format_exc())
    finally:
        log_msg("Program completed successfully" if ok else "Program completed with errors")
    sys.exit(0 if ok else 1)
