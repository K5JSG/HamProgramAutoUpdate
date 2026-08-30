"""Learn the Turnstile checkbox location on the visible desktop, then replay that
click on a hidden desktop via PostMessage.

The DOM dump found no iframes at all - Cloudflare puts the widget in a closed
shadow root, so JS cannot measure it. But we already know two things:

  - SeleniumBase's uc_gui_click_captcha() finds and hits it on the visible desktop
  - PostMessage reaches Chrome on a hidden desktop with isTrusted=True

So: run visibly, record where the successful click landed, then reuse those exact
coordinates hidden. Both phases force the SAME window size, so the page layout -
and therefore the checkbox position - is identical.

    python coord_test.py learn     (visible; mouse moves; records coordinates)
    python coord_test.py replay    (hidden; mouse must NOT move)

Coordinates are saved to captcha_coords.json between the two, in this same
folder (see BASE below) - the folder this script lives in, which is also
where the built exe (and its own captcha_coords.json) end up.
"""
import os
import re
import sys
import json
import time
import ctypes
import datetime
from ctypes import wintypes

URL = "https://archive.chirpmyradio.com/chirp_next/"
PATTERN = r'next-\d{8}/'
BASE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(BASE, "coord_test.log")
COORDS = os.path.join(BASE, "captcha_coords.json")
PROFILE = os.path.join(BASE, "coord_profile")
# Separate profile for replay: sharing one with the learn phase let a cached
# cf_clearance cookie clear the challenge on its own and masquerade as a
# successful click (observed: "SUCCESS" on a click that delivered no events).
REPLAY_PROFILE = os.path.join(BASE, "coord_profile_replay")

# Both phases MUST use this, or the layout shifts and the coordinates are useless.
WIN_W, WIN_H = 1100, 700

GENERIC_ALL = 0x10000000
STARTF_USESHOWWINDOW = 0x00000001
WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0200, 0x0201, 0x0202
MK_LBUTTON = 0x0001


def log(msg):
    line = f"[{datetime.datetime.now():%H:%M:%S}] {msg}"
    print(line, flush=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


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


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


def chrome_windows():
    """(hwnd, title) for every Chrome_WidgetWin_1 on this desktop."""
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


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def render_widgets(hwnd):
    """ALL RenderWidget children. Chrome makes one per out-of-process iframe, so
    the Cloudflare challenge frame has its own - and picking the wrong one is why
    posted coordinates were landing 559px off."""
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


def render_widget(hwnd):
    w = render_widgets(hwnd)
    return w[0][0] if w else None


def screen_to_client(hwnd, sx, sy):
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    p = POINT(sx, sy)
    user32.ScreenToClient(hwnd, ctypes.byref(p))
    return p.x, p.y


def learn():
    """Visible run: click the captcha, record where the cursor was when it worked."""
    from seleniumbase import SB
    import pyautogui

    os.makedirs(PROFILE, exist_ok=True)
    with SB(uc=True, headless=False, user_data_dir=PROFILE) as sb:
        sb.activate_cdp_mode(URL)
        sb.sleep(8)
        try:
            sb.set_window_rect(0, 0, WIN_W, WIN_H)
            log(f"window forced to {WIN_W}x{WIN_H} at (0,0)")
        except Exception as e:
            log(f"could not set window rect: {e}")
        sb.sleep(3)

        html = sb.get_page_source()
        if re.findall(PATTERN, html):
            log("no challenge appeared - rerun when it does")
            return False

        # Record the click in PAGE coordinates - that is the frame of reference the
        # hidden replay can actually reproduce, unlike screen/client coords.
        try:
            sb.execute_script("""
                window.__evts = [];
                ['mousedown','click'].forEach(function(t){
                    document.addEventListener(t, function(e){
                        window.__evts.push([t, e.clientX, e.clientY, e.isTrusted]);
                    }, true);
                });
            """)
        except Exception as e:
            log(f"could not install listeners: {e}")

        before = pyautogui.position()
        log(f"cursor before click: {tuple(before)}")
        try:
            sb.uc_gui_click_captcha()
        except Exception as e:
            log(f"uc_gui_click_captcha failed: {e}")
            return False
        after = pyautogui.position()
        log(f"cursor AFTER click (screen coords): {tuple(after)}")
        sb.sleep(6)

        html = sb.get_page_source()
        if not re.findall(PATTERN, html):
            log("click did not clear the challenge - coordinates not trustworthy")
            return False
        log("challenge cleared on the visible desktop")

        page_x = page_y = None
        try:
            evts = sb.execute_script("(function(){ return window.__evts; })()")
            log(f"page events during the successful click: {evts}")
            for e in (evts or []):
                if e[0] == "mousedown":
                    page_x, page_y = e[1], e[2]
                    break
        except Exception as e:
            log(f"could not read page events: {e}")
        log(f"=> checkbox PAGE coords: ({page_x},{page_y})")

        wins = chrome_windows()
        if not wins:
            log("ERROR: Chrome window vanished before measuring")
            return False
        top = next((h for h, t in wins if t.strip()), wins[0][0])
        rw = render_widget(top) or top
        cx, cy = screen_to_client(rw, after.x, after.y)
        log(f"=> client coords inside the render widget: ({cx},{cy})")

        try:
            vw = sb.execute_script(
                "(function(){ return [window.innerWidth, window.innerHeight]; })()")
        except Exception:
            vw = [None, None]
        log(f"viewport at click time: {vw}")

        with open(COORDS, "w", encoding="utf-8") as f:
            json.dump({"client_x": cx, "client_y": cy,
                       "page_x": page_x, "page_y": page_y,
                       "screen_x": after.x, "screen_y": after.y,
                       "win_w": WIN_W, "win_h": WIN_H,
                       "viewport": vw}, f, indent=2)
        log(f"saved to {COORDS}")
        return True


def post_click(hwnd, x, y):
    """Approach, press, release - with human-ish gaps between messages."""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    lp = (y << 16) | (x & 0xFFFF)
    for dx, dy in ((-25, -18), (-12, -8), (-4, -2), (0, 0)):
        l2 = ((y + dy) << 16) | ((x + dx) & 0xFFFF)
        user32.PostMessageW(hwnd, WM_MOUSEMOVE, 0, l2)
        time.sleep(0.04)
    time.sleep(0.12)
    user32.PostMessageW(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lp)
    time.sleep(0.09)
    user32.PostMessageW(hwnd, WM_LBUTTONUP, 0, lp)


def replay_child():
    from seleniumbase import SB

    if not os.path.exists(COORDS):
        log("no captcha_coords.json - run 'learn' first")
        return False
    saved = json.load(open(COORDS, encoding="utf-8"))
    log(f"replaying coordinates {saved}")

    import shutil
    if os.path.isdir(REPLAY_PROFILE):
        try:
            shutil.rmtree(REPLAY_PROFILE, ignore_errors=True)
            log("wiped the replay profile so no cached clearance can fake a pass")
        except Exception:
            pass
    os.makedirs(REPLAY_PROFILE, exist_ok=True)
    with SB(uc=True, headless=False, user_data_dir=REPLAY_PROFILE) as sb:
        sb.activate_cdp_mode(URL)
        sb.sleep(8)
        try:
            sb.set_window_rect(0, 0, saved["win_w"], saved["win_h"])
        except Exception as e:
            log(f"could not set window rect: {e}")
        sb.sleep(3)

        html = sb.get_page_source()
        if re.findall(PATTERN, html):
            log("no challenge - nothing to prove this run")
            return True

        wins = chrome_windows()
        if not wins:
            log("ERROR: no Chrome window on this desktop")
            return False
        for h, t in wins:
            log(f"  window {h} title={t[:60]!r}")
        top = next((h for h, t in wins if t.strip()), wins[0][0])
        widgets = render_widgets(top)
        for h, w, ht in widgets:
            log(f"  render widget {h}: client {w}x{ht}")
        rw = widgets[0][0] if widgets else top
        log(f"chose top={top} render_widget={rw} (of {len(widgets)})")

        # --- Focus diagnostics -------------------------------------------
        # Turnstile is known to check document.hasFocus(). A hidden desktop has no
        # foreground window, so the page may consider itself unfocused and refuse
        # the click even though the event arrives and is trusted.
        def js(expr, default=None):
            try:
                return sb.execute_script(f"(function(){{ return {expr}; }})()")
            except Exception as e:
                log(f"    js({expr}) failed: {type(e).__name__}")
                return default

        log(f"  document.hasFocus() = {js('document.hasFocus()')}")
        log(f"  document.visibilityState = {js('document.visibilityState')!r}")
        log(f"  document.hidden = {js('document.hidden')}")

        u32 = ctypes.WinDLL("user32", use_last_error=True)
        for fn, arg in (("SetForegroundWindow", top), ("SetActiveWindow", top),
                        ("BringWindowToTop", top), ("SetFocus", rw)):
            try:
                r = getattr(u32, fn)(arg)
                log(f"  {fn}({arg}) -> {r}")
            except Exception as e:
                log(f"  {fn} failed: {e}")
        time.sleep(1.0)
        log(f"  after focus attempts, hasFocus() = {js('document.hasFocus()')}")

        # Record whether the posted click actually lands as a DOM event.
        try:
            sb.execute_script("""
                window.__evts = [];
                ['mousedown','mouseup','click','pointerdown'].forEach(function(t){
                    document.addEventListener(t, function(e){
                        window.__evts.push([t, e.clientX, e.clientY, e.isTrusted]);
                    }, true);
                });
            """)
            log("  event listeners installed")
        except Exception as e:
            log(f"  could not install listeners: {e}")

        # --- Calibrate every render widget --------------------------------
        # Post a mousemove into each widget and see what the page reports. The
        # widget whose offset puts the target inside its client area is the one
        # that owns the viewport.
        tgt_x = saved["client_x"]
        tgt_y = saved["client_y"]
        log(f"  target (viewport coords from the visible run): ({tgt_x},{tgt_y})")

        try:
            sb.execute_script("""
                window.__mm = null;
                document.addEventListener('mousemove', function(e){
                    window.__mm = [e.clientX, e.clientY];
                }, true);
            """)
        except Exception as e:
            log(f"  listener install failed: {e}")

        best = None
        for hw, cw, ch in (widgets or [(rw, 0, 0)]):
            try:
                sb.execute_script("(function(){ window.__mm = null; })()")
            except Exception:
                pass
            probe = (min(400, max(10, cw // 2)), min(300, max(10, ch // 2)))
            u32.PostMessageW(hw, WM_MOUSEMOVE, 0,
                             (probe[1] << 16) | (probe[0] & 0xFFFF))
            time.sleep(1.0)
            try:
                got = sb.execute_script("(function(){ return window.__mm; })()")
            except Exception:
                got = None
            if not got:
                log(f"  widget {hw}: no response to probe")
                continue
            ox, oy = got[0] - probe[0], got[1] - probe[1]
            px, py = tgt_x - ox, tgt_y - oy
            reachable = (0 <= px < max(cw, 1)) and (0 <= py < max(ch, 1))
            log(f"  widget {hw} ({cw}x{ch}): probe {probe} -> page {tuple(got)}, "
                f"offset=({ox},{oy}), post at ({px},{py}) "
                f"{'REACHABLE' if reachable else 'out of bounds'}")
            if reachable and best is None:
                best = (hw, px, py)

        if best is None:
            log("  no widget can reach the target - falling back to widget 0, offset 0")
            target_hwnd, base_x, base_y = rw, tgt_x, tgt_y
        else:
            target_hwnd, base_x, base_y = best
            log(f"  => using widget {target_hwnd} at client ({base_x},{base_y})")

        rw = target_hwnd
        points = [(base_x, base_y)]
        for dx in (-10, 10):
            points.append((base_x + dx, base_y))
        for dy in (-10, 10):
            points.append((base_x, base_y + dy))

        for i, (x, y) in enumerate(points, 1):
            got_events = False
            log(f"  posting click {i}/{len(points)} at client ({x},{y})")
            post_click(rw, x, y)
            time.sleep(2)
            try:
                evts = sb.execute_script("(function(){ return window.__evts; })()")
                got_events = bool(evts)
                if evts:
                    log(f"    page received: {evts}")
                    sb.execute_script("(function(){ window.__evts = []; })()")
                else:
                    log("    page received NO mouse events from this click")
            except Exception:
                pass
            time.sleep(4)
            html = sb.get_page_source()
            if re.findall(PATTERN, html):
                log(f"CHALLENGE CLEARED at ({x},{y}) - "
                    f"{len(re.findall(PATTERN, html))} links")
                if got_events:
                    log("=> the click delivered events AND cleared it. "
                        "FULLY BACKGROUND OPERATION IS POSSIBLE.")
                else:
                    log("=> WARNING: cleared without the click delivering any events; "
                        "this may not be attributable to the click.")
                return True
            log(f"    still challenged, title={sb.get_title()!r}")

        log("learned coordinates did not clear it on the hidden desktop")
        return False


def replay_parent():
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    name = "coord_replay_desktop"
    hdesk = user32.CreateDesktopW(name, None, None, 0, GENERIC_ALL, None)
    if not hdesk:
        log(f"CreateDesktopW failed: {ctypes.get_last_error()}")
        return 2

    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    si.lpDesktop = name
    si.dwFlags = STARTF_USESHOWWINDOW
    si.wShowWindow = 1
    pi = PROCESS_INFORMATION()

    env = os.environ.copy()
    env["COORD_CHILD"] = "1"
    block = "\0".join(f"{k}={v}" for k, v in env.items()) + "\0\0"
    cmd = f'"{sys.executable}" "{os.path.abspath(__file__)}" replay'

    ok = kernel32.CreateProcessW(
        None, ctypes.create_unicode_buffer(cmd), None, None, False,
        0x00000400, ctypes.create_unicode_buffer(block), None,
        ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        log(f"CreateProcessW failed: {kernel32.GetLastError()}")
        user32.CloseDesktop(hdesk)
        return 2

    log("child launched on hidden desktop - your mouse should NOT move")
    kernel32.WaitForSingleObject(pi.hProcess, 300000)
    kernel32.CloseHandle(pi.hProcess)
    kernel32.CloseHandle(pi.hThread)
    user32.CloseDesktop(hdesk)
    log("done")
    return 0


if __name__ == "__main__":
    mode = sys.argv[1].lower() if len(sys.argv) > 1 else ""
    if os.environ.get("COORD_CHILD") == "1":
        try:
            replay_child()
        except Exception as e:
            import traceback
            log(f"child error: {e}")
            log(traceback.format_exc())
        sys.exit(0)

    if mode == "learn":
        open(LOG, "w").close()
        log("=== LEARN (visible - the mouse will move) ===")
        sys.exit(0 if learn() else 1)
    elif mode == "replay":
        log("=== REPLAY (hidden - the mouse must NOT move) ===")
        sys.exit(replay_parent())
    else:
        print(__doc__)
        sys.exit(2)
