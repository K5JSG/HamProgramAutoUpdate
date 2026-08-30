# -*- mode: python ; coding: utf-8 -*-

from PyInstaller.utils.hooks import collect_all, collect_submodules

# This build uses SeleniumBase (NOT undetected_chromedriver, which never solved
# the Cloudflare challenge on this machine) plus curl_cffi. Both ship data files
# and do a lot of dynamic importing, so collect_all is required - PyInstaller's
# static analysis under-collects them badly and the exe dies at runtime.
datas = []
binaries = []
hiddenimports = []

for _pkg in ("seleniumbase", "selenium", "mycdp", "curl_cffi", "certifi",
             "websockets", "requests", "trio", "trio_websocket", "wsproto",
             "outcome", "sniffio", "sortedcontainers", "filelock", "fasteners",
             "platformdirs", "pynose", "parse", "parse_type", "rich",
             "pygments", "markdown_it", "bs4", "soupsieve", "cssselect"):
    try:
        _d, _b, _h = collect_all(_pkg)
        datas += _d
        binaries += _b
        hiddenimports += _h
    except Exception as _e:
        print(f"spec: could not collect {_pkg}: {_e}")

# SeleniumBase imports pytest machinery even when driven through the SB() context
# manager, and misses these under static analysis.
hiddenimports += collect_submodules("seleniumbase")
hiddenimports += [
    "pytest", "pluggy", "iniconfig", "packaging",
    "seleniumbase.undetected", "seleniumbase.core.browser_launcher",
    "seleniumbase.fixtures.base_case", "seleniumbase.plugins.sb_manager",
    "curl_cffi.requests", "curl_cffi.const",
]


a = Analysis(
    ['Chirp Update Script.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=['undetected_chromedriver', 'pyautogui', 'tkinter'],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='Chirp Update Script',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    # UPX is disabled: it is a common antivirus false-positive trigger and saves
    # little on a build this size.
    upx=False,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    # The CHIRP installer needs administrator rights.
    uac_admin=True,
)
