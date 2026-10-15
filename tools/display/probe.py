r"""Display probe (dev research for 0.3.0 overlays and OBS): what Lockpick will need to know about the game's window.

    python -I tools/display/probe.py monitors
    python -I tools/display/probe.py window [--json out.json]
    python -I tools/display/probe.py windows                            (all of the game's windows, and the foreground)
    python -I tools/display/probe.py capture <out-dir> [--tag name]
    python -I tools/display/probe.py overlay <out-dir> [--tag name] [--seconds 2]
    python -I tools/display/probe.py move <x> <y> [<width> <height>]     (moves the game window; physical pixels)

Runs per-monitor DPI aware (v2), so every rectangle is in physical pixels on every monitor, the same coordinates an
overlay window made by Lockpick would use. Read-only towards the game: it looks at the window, copies pixels, and the
overlay test shows a click-through, non-activating window over the game (the game pauses when it loses focus, so an
overlay must never take it). `move` repositions the game's window, which is what a player dragging it does.
Needs Pillow (the research venv).
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import sys
import time
from pathlib import Path

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
shcore = ctypes.WinDLL("shcore")
dwmapi = ctypes.WinDLL("dwmapi")
shell32 = ctypes.WinDLL("shell32")
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2

GAME_TITLE = "RESIDENT EVIL"
WS = {"CAPTION": 0x00C00000, "THICKFRAME": 0x00040000, "MAXIMIZEBOX": 0x00010000, "MINIMIZEBOX": 0x00020000,
      "SYSMENU": 0x00080000, "POPUP": 0x80000000, "VISIBLE": 0x10000000, "MAXIMIZE": 0x01000000}
WS_EX = {"TOPMOST": 0x8, "TOOLWINDOW": 0x80, "APPWINDOW": 0x40000, "LAYERED": 0x80000, "NOREDIRECTIONBITMAP": 0x200000,
         "WINDOWEDGE": 0x100, "CLIENTEDGE": 0x200}
QUNS = {1: "not present", 2: "busy (fullscreen app, not D3D)", 3: "D3D exclusive fullscreen", 4: "presentation mode",
        5: "accepts notifications", 6: "quiet time", 7: "app (Windows Store)"}
AWARENESS = {-1: "invalid", 0: "unaware", 1: "system aware", 2: "per-monitor aware"}


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long), ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

    def tuple(self) -> list[int]:
        return [self.left, self.top, self.right - self.left, self.bottom - self.top]


class MONITORINFOEX(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", RECT), ("rcWork", RECT), ("dwFlags", wt.DWORD),
                ("szDevice", wt.WCHAR * 32)]


def monitors() -> list[dict]:
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_int, wt.HMONITOR, wt.HDC, ctypes.POINTER(RECT), wt.LPARAM)
    def each(handle, _dc, _rect, _data):
        info = MONITORINFOEX(); info.cbSize = ctypes.sizeof(info)
        user32.GetMonitorInfoW(handle, ctypes.byref(info))
        dx, dy = wt.UINT(), wt.UINT()
        shcore.GetDpiForMonitor(handle, 0, ctypes.byref(dx), ctypes.byref(dy))
        found.append({"handle": int(handle), "device": info.szDevice, "primary": bool(info.dwFlags & 1),
                      "bounds": info.rcMonitor.tuple(), "work": info.rcWork.tuple(), "dpi": dx.value,
                      "scale": round(dx.value / 96, 2)})
        return 1

    user32.EnumDisplayMonitors(None, None, each, 0)
    return found


def game_pid() -> int | None:
    import subprocess
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq ResidentEvil.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[0].lower().startswith("residentevil"):
            return int(parts[1])
    return None


def game_window() -> int | None:
    pid = game_pid()
    if pid is None:
        return None
    hits = []

    @ctypes.WINFUNCTYPE(ctypes.c_int, wt.HWND, wt.LPARAM)
    def each(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            hits.append(hwnd)
        return 1

    user32.EnumWindows(each, 0)
    return hits[0] if hits else None


def game_windows() -> dict:
    """Every top-level window of the game's process (visible or not), and which window has the foreground."""
    pid = game_pid()
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_int, wt.HWND, wt.LPARAM)
    def each(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid:
            r = RECT(); user32.GetWindowRect(hwnd, ctypes.byref(r))
            style = user32.GetWindowLongW(hwnd, -16) & 0xFFFFFFFF
            found.append({"hwnd": int(hwnd), "title": text(hwnd), "class": class_name(hwnd), "rect": r.tuple(),
                          "visible": bool(user32.IsWindowVisible(hwnd)), "style": [k for k, v in WS.items() if style & v]})
        return 1

    user32.EnumWindows(each, 0)
    fg = user32.GetForegroundWindow()
    owner = wt.DWORD(); user32.GetWindowThreadProcessId(fg, ctypes.byref(owner))
    return {"pid": pid, "windows": found,
            "foreground": {"hwnd": int(fg or 0), "title": text(fg), "class": class_name(fg), "pid": owner.value}}


def text(hwnd) -> str:
    buffer = ctypes.create_unicode_buffer(256)
    user32.GetWindowTextW(hwnd, buffer, 256)
    return buffer.value


def class_name(hwnd) -> str:
    buffer = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buffer, 256)
    return buffer.value


def client_rect(hwnd) -> list[int]:
    r = RECT(); user32.GetClientRect(hwnd, ctypes.byref(r))
    origin = wt.POINT(0, 0); user32.ClientToScreen(hwnd, ctypes.byref(origin))
    return [origin.x, origin.y, r.right, r.bottom]


def window_info(hwnd) -> dict:
    r = RECT(); user32.GetWindowRect(hwnd, ctypes.byref(r))
    frame = RECT(); dwmapi.DwmGetWindowAttribute(hwnd, 9, ctypes.byref(frame), ctypes.sizeof(frame))
    style = user32.GetWindowLongW(hwnd, -16) & 0xFFFFFFFF
    exstyle = user32.GetWindowLongW(hwnd, -20) & 0xFFFFFFFF
    monitor = user32.MonitorFromWindow(hwnd, 2)
    mons = {m["handle"]: m for m in monitors()}
    mon = mons.get(int(monitor or 0), {})
    client = client_rect(hwnd)
    user32.GetWindowDpiAwarenessContext.restype = ctypes.c_void_p
    user32.GetAwarenessFromDpiAwarenessContext.argtypes = [ctypes.c_void_p]
    awareness = user32.GetAwarenessFromDpiAwarenessContext(user32.GetWindowDpiAwarenessContext(hwnd))
    state = ctypes.c_int()
    shell32.SHQueryUserNotificationState(ctypes.byref(state))
    covers = bool(mon) and client == mon["bounds"]
    return {
        "hwnd": int(hwnd), "title": text(hwnd), "class": class_name(hwnd),
        "window": r.tuple(), "frame": frame.tuple(), "client": client,
        "style": [k for k, v in WS.items() if style & v], "exstyle": [k for k, v in WS_EX.items() if exstyle & v],
        "zoomed": bool(user32.IsZoomed(hwnd)), "iconic": bool(user32.IsIconic(hwnd)),
        "monitor": mon.get("device"), "monitor_bounds": mon.get("bounds"), "monitor_scale": mon.get("scale"),
        "client_covers_monitor": covers, "window_dpi": user32.GetDpiForWindow(hwnd),
        "dpi_awareness": AWARENESS.get(awareness, awareness), "foreground": user32.GetForegroundWindow() == hwnd,
        "notification_state": QUNS.get(state.value, state.value),
    }


def grab_window(hwnd, client_only: bool = True):
    """PrintWindow with PW_RENDERFULLCONTENT: what a window capture that works behind other windows sees."""
    from PIL import Image
    x, y, w, h = client_rect(hwnd) if client_only else window_info(hwnd)["window"]
    return _bitmap(w, h, lambda dc: user32.PrintWindow(hwnd, dc, 3 if client_only else 2))


def grab_screen(x: int, y: int, w: int, h: int):
    """BitBlt from the screen: what DWM composes there (what a display capture sees)."""
    screen = user32.GetDC(None)
    try:
        return _bitmap(w, h, lambda dc: gdi32.BitBlt(dc, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000))
    finally:
        user32.ReleaseDC(None, screen)


def _bitmap(w: int, h: int, draw):
    from PIL import Image
    screen = user32.GetDC(None)
    dc = gdi32.CreateCompatibleDC(screen)
    bmp = gdi32.CreateCompatibleBitmap(screen, w, h)
    old = gdi32.SelectObject(dc, bmp)
    ok = draw(dc)
    gdi32.SelectObject(dc, old)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                    ("biBitCount", wt.WORD), ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD),
                    ("biXPelsPerMeter", ctypes.c_long), ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                    ("biClrImportant", wt.DWORD)]

    header = BITMAPINFOHEADER(ctypes.sizeof(BITMAPINFOHEADER), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
    buffer = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(dc, bmp, 0, h, buffer, ctypes.byref(header), 0)
    gdi32.DeleteObject(bmp); gdi32.DeleteDC(dc); user32.ReleaseDC(None, screen)
    return Image.frombuffer("RGBA", (w, h), buffer.raw, "raw", "BGRA", 0, 1).convert("RGB"), bool(ok)


def image_stats(image) -> dict:
    """Mean brightness and the bounding box of non-black pixels (where the game's picture is inside the client)."""
    from PIL import ImageStat
    gray = image.convert("L")
    box = gray.point(lambda v: 255 if v > 12 else 0).getbbox()
    return {"size": list(image.size), "mean": round(ImageStat.Stat(gray).mean[0], 1),
            "picture": [box[0], box[1], box[2] - box[0], box[3] - box[1]] if box else None}


def capture(out: Path, tag: str) -> dict:
    hwnd = game_window()
    if not hwnd:
        return {"error": "no game window"}
    info = window_info(hwnd)
    out.mkdir(parents=True, exist_ok=True)
    result = {"window": info}
    # PrintWindow returns the last frame the wrapper presented to its window, which is black between some frames
    # (seen on the title screen): keep the brightest of a few samples.
    best, best_stats, ok, samples = None, None, False, []
    for _ in range(4):
        image, ok = grab_window(hwnd)
        stats = image_stats(image)
        samples.append(stats["mean"])
        if best is None or stats["mean"] > best_stats["mean"]:
            best, best_stats = image, stats
        time.sleep(0.4)
    best.save(out / f"{tag}-printwindow.png")
    result["printwindow"] = {"ok": ok, **best_stats, "samples": samples}
    x, y, w, h = info["client"]
    image, ok = grab_screen(x, y, w, h)
    image.save(out / f"{tag}-screen.png")
    result["screen"] = {"ok": ok, **image_stats(image)}
    return result


def overlay(out: Path, tag: str, seconds: float = 2.0) -> dict:
    """Shows a click-through, topmost, non-activating box over the game's client area (what a 0.3.0 overlay would
    be), then checks the game kept the foreground and whether the box shows up in a screen grab and a window grab."""
    hwnd = game_window()
    if not hwnd:
        return {"error": "no game window"}
    before = window_info(hwnd)
    x, y, w, h = before["client"]
    box_w, box_h = max(120, w // 5), max(40, h // 10)
    # In the middle: other overlays (Discord's "use the overlay" toast) appear in the top-left corner.
    bx, by = (w - box_w) // 2, (h - box_h) // 2
    ex = 0x8 | 0x80 | 0x80000 | 0x20 | 0x08000000  # TOPMOST TOOLWINDOW LAYERED TRANSPARENT NOACTIVATE
    WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
    user32.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
    user32.DefWindowProcW.restype = ctypes.c_ssize_t
    proc = WNDPROC(lambda h_, m, wp, lp: user32.DefWindowProcW(h_, m, wp, lp))

    class WNDCLASS(ctypes.Structure):
        _fields_ = [("style", wt.UINT), ("lpfnWndProc", WNDPROC), ("cbClsExtra", ctypes.c_int),
                    ("cbWndExtra", ctypes.c_int), ("hInstance", wt.HINSTANCE), ("hIcon", wt.HICON),
                    ("hCursor", wt.HANDLE), ("hbrBackground", wt.HBRUSH), ("lpszMenuName", wt.LPCWSTR),
                    ("lpszClassName", wt.LPCWSTR)]

    brush = gdi32.CreateSolidBrush(0x00FF00FF)  # magenta (BGR)
    wc = WNDCLASS(0, proc, 0, 0, kernel32.GetModuleHandleW(None), None, None, brush, None, "LockpickOverlayProbe")
    user32.RegisterClassW(ctypes.byref(wc))
    user32.CreateWindowExW.restype = wt.HWND
    ov = user32.CreateWindowExW(ex, "LockpickOverlayProbe", "overlay probe", 0x80000000, x + bx, y + by, box_w, box_h,
                                None, None, wc.hInstance, None)
    user32.SetLayeredWindowAttributes(ov, 0, 200, 2)
    user32.ShowWindow(ov, 4)  # SW_SHOWNOACTIVATE
    msg = wt.MSG()
    end = time.time() + seconds
    while time.time() < end:
        while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, 1):
            user32.TranslateMessage(ctypes.byref(msg)); user32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.02)
    out.mkdir(parents=True, exist_ok=True)
    screen, _ = grab_screen(x, y, w, h)
    screen.save(out / f"{tag}-overlay-screen.png")
    window, _ = grab_window(hwnd)
    after = window_info(hwnd)
    user32.DestroyWindow(ov)
    gdi32.DeleteObject(brush)
    user32.UnregisterClassW("LockpickOverlayProbe", wc.hInstance)

    def magenta(image) -> float:
        px = list(image.crop((bx + 8, by + 8, bx + box_w - 8, by + box_h - 8)).convert("RGB").getdata())
        hits = sum(1 for r, g, b in px if r > 150 and b > 150 and g < 120)
        return round(hits / max(1, len(px)), 2)

    return {"foreground_before": before["foreground"], "foreground_after": after["foreground"],
            "overlay_in_screen_grab": magenta(screen), "overlay_in_window_grab": magenta(window),
            "notification_state": after["notification_state"]}


def idle_seconds() -> float:
    """Seconds since the last keyboard or mouse input on this PC (focus tests wait until nobody is using it)."""
    class LASTINPUTINFO(ctypes.Structure):
        _fields_ = [("cbSize", wt.UINT), ("dwTime", wt.DWORD)]
    info = LASTINPUTINFO(ctypes.sizeof(LASTINPUTINFO), 0)
    user32.GetLastInputInfo(ctypes.byref(info))
    return (kernel32.GetTickCount() - info.dwTime) / 1000


def move(x: int, y: int, w: int | None = None, h: int | None = None) -> dict:
    hwnd = game_window()
    flags = 0x0004 | 0x0010 | (0x0001 if w is None else 0)  # NOZORDER NOACTIVATE [NOSIZE]
    user32.SetWindowPos(hwnd, None, x, y, w or 0, h or 0, flags)
    time.sleep(0.8)
    return window_info(hwnd)


def main(argv: list[str]) -> int:
    command = argv[0] if argv else "window"
    tag = argv[argv.index("--tag") + 1] if "--tag" in argv else time.strftime("%H%M%S")
    if command == "monitors":
        result = monitors()
    elif command == "windows":
        result = game_windows()
    elif command == "window":
        hwnd = game_window()
        result = window_info(hwnd) if hwnd else {"error": "no game window"}
    elif command == "capture":
        result = capture(Path(argv[1]), tag)
    elif command == "overlay":
        seconds = float(argv[argv.index("--seconds") + 1]) if "--seconds" in argv else 2.0
        result = overlay(Path(argv[1]), tag, seconds)
    elif command == "move":
        numbers = [int(a) for a in argv[1:5]]
        result = move(numbers[0], numbers[1], *(numbers[2:4] if len(numbers) == 4 else (None, None)))
    else:
        print(__doc__)
        return 2
    text_out = json.dumps(result, indent=1)
    if "--json" in argv:
        Path(argv[argv.index("--json") + 1]).write_text(text_out, encoding="utf-8")
    print(text_out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
