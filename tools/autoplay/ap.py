"""Python side of the dev-only autoplay harness: sends commands to `lockpick-autoplay serve` and returns its log.

    from ap import send, look, state
"""
from __future__ import annotations

import os
import re
import subprocess
from pathlib import Path

EXE = Path(__file__).resolve().parents[1] / "Lockpick.Autoplay" / "bin" / "Release" / "net10.0-windows" / "lockpick-autoplay.exe"


class Failed(RuntimeError):
    pass


def send(*commands: str, check: bool = True, timeout: float = 600) -> str:
    """Runs commands on the server; returns the log lines (without timestamps). Raises Failed on a FAILED line."""
    script = "\n".join(commands)
    out = subprocess.run([str(EXE), "send", script.replace("\n", ";")], capture_output=True, text=True,
                         encoding="utf-8", timeout=timeout).stdout
    lines = [re.sub(r"^\[[0-9:.]+\] ", "", l) for l in out.splitlines()]
    if check and any(l.startswith("FAILED") for l in lines):
        raise Failed("\n".join(lines))
    return "\n".join(lines)


def look(question: str) -> str:
    out = send(f"look {question}")
    return "\n".join(l.strip() for l in out.splitlines() if not l.startswith(">") and l.strip() != "ok").strip()


def state() -> dict:
    out = send("state")
    m = re.search(r"gameplay=(\S+) room=(\S+) hp=(\S+) char=(\S+) pos=(.*)$", out, re.M)
    return dict(zip(("gameplay", "room", "hp", "char", "pos"), m.groups())) if m else {"raw": out}


SHOTS = Path(os.environ.get("TEMP", ".")) / "lockpick-autoplay"
LOG = Path(os.environ.get("LOCALAPPDATA", ".")) / "Lockpick" / "research" / "autoplay-serve.log"


def serve(vision_url: str | None = os.environ.get("LOCKPICK_VISION_URL"),
          vision_model: str | None = os.environ.get("LOCKPICK_VISION_MODEL")) -> None:
    """Stops any server and game, then starts the server, which launches the game with the pad plugged in."""
    import time
    send("quit", check=False, timeout=30)  # an older server holds the pipe; it also removes its patch
    subprocess.run(["taskkill", "/IM", "lockpick-autoplay.exe", "/F"], capture_output=True)
    subprocess.run(["taskkill", "/IM", "ResidentEvil.exe", "/F"], capture_output=True)
    for _ in range(40):
        listed = subprocess.run(["tasklist", "/FI", "IMAGENAME eq ResidentEvil.exe"], capture_output=True, text=True).stdout
        if "ResidentEvil.exe" not in listed:
            break
        time.sleep(0.25)
    else:
        raise Failed("the old game did not close")
    env = dict(os.environ)
    if vision_url:
        env["LOCKPICK_VISION_URL"], env["LOCKPICK_VISION_MODEL"] = vision_url, vision_model or "default"
    LOG.parent.mkdir(parents=True, exist_ok=True)
    subprocess.Popen([str(EXE), "serve", "--launch", "--background"], stdout=LOG.open("w", encoding="utf-8"),
                     stderr=subprocess.STDOUT, env=env, creationflags=0x08000000)
    for _ in range(60):
        time.sleep(1)
        if "serving on" in LOG.read_text(encoding="utf-8", errors="replace"):
            return
    raise Failed("server did not start:\n" + LOG.read_text(encoding="utf-8", errors="replace"))


def quit_server() -> None:
    send("quit", check=False)


def shot(name: str, width: int = 480) -> Path:
    send(f"shot {name} {width}")
    return SHOTS / name


def pos() -> tuple[int, ...] | None:
    p = state().get("pos", "")
    return tuple(int(v) for v in p.split(", ")) if p and p[0].isdigit() or p.startswith("-") else None


def controllable() -> bool:
    """True when the player stands still on their own and moves when pushed (not in a cutscene or menu)."""
    a = pos()
    send("wait 500")
    b = pos()
    if a is None or a != b:
        return False
    send("press LDown 400")
    c = pos()
    if c != b:
        send("press LUp 400")
        return True
    return False


SCREENS = ("Answer with exactly one word from this list: black, logo, title (big RESIDENT EVIL logo with PRESS ANY "
           "BUTTON), newload (a NEW GAME / LOAD GAME menu), card (a police ID card), text (white text on black), "
           "loadmenu (a list of save files), gameplay, other.")


# Screens where pressing A only skips ahead. Never press on NEW GAME / LOAD GAME, the load list or the ID card:
# a stray press there can load the owner's save (it did once on 2026-10-14).
SAFE_TO_PRESS = {"black", "logo", "title", "text"}


def screen() -> str:
    try:
        return look(SCREENS).strip().lower().strip(".")
    except Failed:
        return "black"  # no picture yet (the window is still being created, or a mode switch)


def wait_screen(wanted: set[str], timeout: float = 60, press: str | None = None) -> str:
    """Polls the screen class until it is one of `wanted`, optionally pressing an input between polls."""
    import time
    end = time.time() + timeout
    while time.time() < end:
        kind = screen()
        if kind in wanted:
            return kind
        if kind == "loadmenu":
            send("press X", "wait 700")  # never pick a save by accident: back out of the load list
        elif press and kind in SAFE_TO_PRESS:
            send(f"press {press}")
        send("wait 700")
    raise Failed(f"screen never became {wanted}")


# Title and character select state (ecruells/resident-evil-pc-decomp TitleScreen.cpp / CharacterSelectionScreen.cpp;
# traced live 2026-10-14): loop 0xD22777 = 1 once the title runs (its cursor 0xD22774 is preset to LOAD GAME = 2 when
# saves exist); 0xD22775 = 1 only while the NEW GAME / LOAD GAME menu is shown; Up/Down move the cursor (1 NEW, 2 LOAD).
TITLE_CURSOR, MENU_SHOWN, TITLE_LOOP = 0xD22774, 0xD22775, 0xD22777
SELECT_STATE, SELECT_CHARACTER = 0xAC9880, 0xAC93F0                  # 1 = waiting for input; 0 Chris, 1 Jill
CHARACTERS = {"chris": 0, "jill": 1}


def title() -> tuple[int, int, int]:
    """(cursor, menu shown, title loop)."""
    raw = peek(TITLE_CURSOR, 4)
    return raw[0], raw[1], raw[3]


def wait_for(check, timeout: float, press: str | None = None, every: float = 1.5) -> bool:
    import time
    end, last = time.time() + timeout, 0.0
    while time.time() < end:
        if check():
            return True
        if press and time.time() - last > every:
            send(f"press {press}")
            last = time.time()
        send("wait 200")
    return False


def open_title_menu(timeout: float = 90) -> None:
    """Presses through the logos and title until NEW GAME / LOAD GAME is shown. Presses only while the menu isn't
    shown, so a press can never land on the menu and choose an entry."""
    import time
    end, last = time.time() + timeout, 0.0
    while time.time() < end:
        cursor, shown, loop = title()
        if shown == 1 and cursor in (1, 2):
            send("wait 400")
            return
        if shown == 0 and time.time() - last > 1.5:
            send("press A")
            last = time.time()
        send("wait 200")
    raise Failed(f"menu never opened (title state {title()})")


def new_game(character: str = "chris", fast_ms: int = 4) -> float:
    """From a fresh launch to player control in a new game; returns seconds taken. Reads the game's own title and
    character-select state instead of guessing from screenshots, so presses only land where they should. Never
    touches save files."""
    import time
    t = time.time()
    open_title_menu()
    if not wait_for(lambda: title()[0] == 1, 10, press="Up", every=0.8):
        raise Failed(f"cursor never reached NEW GAME (title state {title()})")
    send("wait 300", "press A")
    # Character select: wait until it takes input, then move to the right card.
    want = CHARACTERS[character]
    if not wait_for(lambda: peek(SELECT_STATE, 1)[0] == 1, 40):
        raise Failed(f"character select never became ready (state {peek(SELECT_STATE, 1)[0]})")
    for _ in range(3):
        if peek(SELECT_CHARACTER, 1)[0] == want:
            break
        send("press Right")
        wait_for(lambda: peek(SELECT_STATE, 1)[0] == 1, 10)
    if peek(SELECT_CHARACTER, 1)[0] != want:
        raise Failed(f"could not select {character}")
    send("press A")
    # Past the card there is no menu left to misfire on: press through the opening text and movie.
    for _ in range(240):
        if state().get("gameplay") == "True":
            break
        send("press A", "wait 500")
    # The opening has calm gaps between its scenes (2026-10-14: a gap passed for control at 33 s, then the main-hall
    # scene resumed), so control is confirmed by pushing the player, not only by the flags.
    end = time.time() + 300
    while time.time() < end:
        send("wait 1500")
        if not settle(120, fast_ms):
            continue
        send("wait 2500")
        # Calm and still isn't proof (the opening idles before its main-hall scene): the player must also move when
        # pushed — controllable() — and still have no busy flags.
        if in_control() and controllable() and state().get("char", "").lower() == character:
            return time.time() - t
        send("press A 120")  # an idle scene or a message may be waiting for a button
    raise Failed(f"no control after the intro: {state()}")


def peek(address: int, length: int) -> bytes:
    out = send(f"peek {address:X} {length}")
    try:
        data = bytes.fromhex(out.split(": ")[-1].split()[0])
    except (IndexError, ValueError) as e:
        raise Failed(f"unexpected peek reply: {out[-200:]!r}") from e
    if len(data) != length:
        raise Failed(f"peek returned {len(data)} of {length} bytes")
    return data


def events() -> list[tuple[int, int]]:
    """(state, active) of the 8 script event slots (0xBF084C, 0x34 bytes each; decomp lead)."""
    raw = peek(0xBF084C, 8 * 0x34)
    return [(raw[i * 0x34], raw[i * 0x34 + 2]) for i in range(8)]


STATE_FLAGS = 0xBE41C0
# Bits set while the player can't act (state flags, live 2026-10-14: 0x80800000 in control; cutscenes add camera lock
# 0x100000, voice 0x20000 and ramp 0x10000): door 0x80, menus 0xFF00, ramp, voice, FMV, camera lock, dead, room
# transition, fade. The script bits 0x0C are not "busy": room 104's script leaves 0x4 set while the player walks.
BUSY = 0x80 | 0xFF00 | 0x10000 | 0x20000 | 0x40000 | 0x100000 | 0x01000000 | 0x04000000 | 0x20000000
DEAD = 0x01000000
MESSAGE = 0x8000  # state flags: menu/message mode (the status screen sets it too)
MESSAGE_FLAGS = 0xBEBCC0  # u16; bit 0x100 is clear while an on-screen message waits for a press (live 2026-10-14)


def message_waiting() -> bool:
    return not int.from_bytes(peek(MESSAGE_FLAGS, 2), "little") & 0x100
CUTSCENE = 0x100000 | 0x20000 | 0x10000 | 0x40000


def flags() -> int:
    return int.from_bytes(peek(STATE_FLAGS, 4), "little")


def in_control() -> bool:
    s = state()
    return s.get("gameplay") == "True" and flags() & BUSY == 0


def settle(timeout: float = 120, fast_ms: int = 4) -> bool:
    """Fast-forwards cutscenes and dialogue until the player has control; True when reached."""
    import time
    end = time.time() + timeout
    send("wait 1500")  # a door animation or fade may still be starting
    calm = 0
    screen_presses = 0
    while time.time() < end:
        if in_control():
            calm += 1
            if calm >= 3:
                send("speed normal")
                return True
            send("wait 300")
            continue
        calm = 0
        # Fast-forward only real cutscenes (camera lock, voice, ramp, FMV) — not damage reactions, which would speed
        # up the attack. Press A only to close a message box (0x8000): a stray press can carry into control in front
        # of a typewriter or item.
        f = flags()
        speed = f"speed {fast_ms}" if f & CUTSCENE else "speed normal"
        # Messages, the pickup screen (0x100: also documents and maps, page by page) and the item viewer (0x800) wait
        # for a press; a document read on pickup sets camera lock and ramp too, so it looks like a cutscene otherwise.
        if message_waiting() or f & (0x100 | 0x800):
            # A waiting message, the pickup/document screen or the item viewer: A advances. Documents can loop their
            # pages (2026-10-14: the researcher's will in 20A); cancel (X) closes them.
            screen_presses = screen_presses + 1 if f & 0x100 else 0
            key = "X" if screen_presses > 8 and screen_presses % 2 == 0 else "A"
            send(speed, f"press {key} 120", "wait 250")
        elif f & MESSAGE and peek(MENU_INPUT, 1)[0]:
            # The status screen sets the same state bit as messages; A there would pick an item (2026-10-14: it
            # opened the knife's submenu at the fireplace). Close it instead.
            send(speed, "press X 120", "wait 400")
        else:
            send(speed, "wait 300")
    send("speed normal")
    return False


def describe(image: Path, question: str, max_tokens: int = 200) -> str:
    """Asks the vision model about a screenshot file (no second capture). Dark scenes are not 'black': only a fully
    black frame (a fade) is."""
    import base64
    import json as _json
    import urllib.request
    url = (os.environ.get("LOCKPICK_VISION_URL") or "").rstrip("/")
    if not url:
        return "(LOCKPICK_VISION_URL not set)"
    data = "data:image/png;base64," + base64.b64encode(Path(image).read_bytes()).decode()
    body = {"model": os.environ.get("LOCKPICK_VISION_MODEL", "default"), "max_tokens": max_tokens, "temperature": 0,
            "chat_template_kwargs": {"enable_thinking": False},
            "messages": [{"role": "user", "content": [
                {"type": "image_url", "image_url": {"url": data}},
                {"type": "text", "text": "This is a screenshot of Resident Evil (1996, PC); its scenes are often dark. "
                                         "Answer 'black' only if the whole frame is uniformly black (a fade). " + question}]}]}
    req = urllib.request.Request(url + "/chat/completions", _json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as r:
        return _json.load(r)["choices"][0]["message"]["content"].strip()


# Inventory menu (ecruells/resident-evil-pc-decomp MainMenu.cpp; to be live-checked): 0xAE9F20 = 1 while the item grid
# takes input; cursor 0xAE9F23: 0–7 the tab row, 8 + 2·slot the item slots (two per row; Left/Right toggles bit 2,
# Down +4, Up −4); 0xAE9F1B = the selected item id. USE needs the room to allow the item there (g_itemUseFlags).
MENU_INPUT, MENU_CURSOR, MENU_ITEM = 0xAE9F20, 0xAE9F23, 0xAE9F1B
INVENTORY_SLOTS = 0xBE9944


def close_menu() -> None:
    """Backs out of the status screen and any submenu (X), whatever state it's in."""
    for _ in range(4):
        if not peek(MENU_INPUT, 1)[0] and not flags() & MESSAGE:
            return
        send("press X 120", "wait 600")


def use_item(item: int, timeout: float = 30) -> dict:
    """Opens the status screen, moves to the item's slot, confirms and chooses USE (the first submenu entry). Every
    press is checked: the cursor must move (the menu ignores input while it opens), the selected item must be the
    target before A, and the menu is closed again if anything goes wrong."""
    import time
    raw = peek(INVENTORY_SLOTS, 16)
    slots = [raw[i * 2] for i in range(8)]
    if item not in slots:
        raise Failed(f"item {item} is not in the inventory ({slots})")
    target = 8 + 2 * slots.index(item)
    send("press Y")
    try:
        if not wait_for(lambda: peek(MENU_INPUT, 1)[0] == 1, 10):
            raise Failed("the status screen didn't open")
        send("wait 800")  # the opening animation ignores input
        end = time.time() + timeout
        while (cursor := peek(MENU_CURSOR, 1)[0]) != target:
            if time.time() > end:
                raise Failed(f"cursor never reached slot {slots.index(item)} (at {cursor})")
            if (cursor & ~2) < (target & ~2):
                key = "Down"
            elif (cursor & ~2) > (target & ~2):
                key = "Up"
            else:
                key = "Right"
            send(f"press {key} 100")
            wait_for(lambda: peek(MENU_CURSOR, 1)[0] != cursor, 1.0)
        selected = peek(MENU_ITEM, 1)[0]
        if selected != item:
            raise Failed(f"the menu selected item {selected}, not {item}")
        send("press A 120")
        # The submenu (USE / CHECK / COMBN) takes input once the menu state reads 2; USE is its first entry.
        if not wait_for(lambda: peek(MENU_INPUT, 1)[0] == 2, 5):
            raise Failed("the item submenu didn't open")
        send("wait 400", "press A 120", "wait 1500")
        return {"slot": slots.index(item), "selected": selected}
    except Failed:
        close_menu()
        raise
