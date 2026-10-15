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
                         encoding="utf-8", errors="replace", timeout=timeout).stdout or ""
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
    """From a fresh launch to player control in a new game, with no presses; returns seconds taken. The harness hands
    the title task straight to game_start as the character (NewGame.cs: the path the attract demo takes), the opening
    plays by itself (fast-forwarded), and control is read from the game's state: both openings hand over in the
    dining room (105), and the main-hall scene has calm gaps that look like control, so the room is part of the test.
    Never touches save files."""
    import time
    t = time.time()
    send(f"new-game {character} 120")
    if fast_ms:
        send(f"speed {fast_ms}")
    try:
        end, since = time.time() + 300, None
        while time.time() < end:
            ready = (state().get("room") == "105" and in_control()
                     and state().get("char", "").lower() == character)
            since = (since or time.time()) if ready else None
            if since and time.time() - since >= 1.5:
                return time.time() - t
            send("wait 250")
        raise Failed(f"no control after the opening: {state()} flags 0x{flags():08X}")
    finally:
        if fast_ms:
            send("speed normal", check=False)


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
MESSAGE_FLAGS = 0xBEBCC0  # g_message_flags (u16)
# The game's own message state (decomp UpdateMessageDisplay 0x004557b0, checked live 2026-10-14): g_menu_choice_id
# (0xBE9825) bit 0x80 is set while a message is up and bit 0 is the yes/no choice (0 Yes, 1 No); the state byte
# (0xBF0A16) is 1 while text types out, 2 waiting on a page (the ▼), 3 after a line break, 4 a yes/no prompt, 5 waiting
# for a press to close, 6 closing by itself. "It's locked." leaves the state flags at plain control (0x80800000), so
# the flags alone can't tell a message is up.
MESSAGE_ACTIVE, MESSAGE_STATE = 0xBE9825, 0xBF0A16
TYPING, PAGE, PROMPT, WAITING, AUTO = 1, 2, 4, 5, 6


def message_active() -> bool:
    return bool(peek(MESSAGE_ACTIVE, 1)[0] & 0x80)


def message_state() -> int:
    """0 when no message is up, otherwise the message state (TYPING, PAGE, PROMPT, WAITING, AUTO)."""
    return peek(MESSAGE_STATE, 1)[0] if message_active() else 0


def message_waiting() -> bool:
    """A message is up and waiting for A (a page or the end; prompts are separate: see answer())."""
    return message_state() in (PAGE, WAITING)


CUTSCENE = 0x100000 | 0x20000 | 0x10000 | 0x40000


def flags() -> int:
    return int.from_bytes(peek(STATE_FLAGS, 4), "little")


def in_control() -> bool:
    s = state()
    return s.get("gameplay") == "True" and flags() & BUSY == 0 and not message_active()


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
        if message_state() == PROMPT:
            # A yes/no question is the caller's decision (e.g. "Discard?" for a used-up key); settle never answers.
            send("speed normal")
            return False
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
    """Backs out of the status screen and any submenu (X), reading first: messages are read and closed with dismiss(),
    a yes/no prompt is left for the caller (Failed), and X is pressed only while the status screen is open."""
    for _ in range(8):
        if message_state() == PROMPT:
            raise Failed(f"a prompt is open: {message_text()!r}")
        if message_active():
            dismiss()
            continue
        if not status_open():
            return
        input_before = peek(MENU_INPUT, 1)[0]
        send("press X 120")
        wait_for(lambda: not status_open() or peek(MENU_INPUT, 1)[0] != input_before, 2)
    raise Failed("the status screen didn't close")


SUBMENU_CURSOR = 0xAE9F24          # item submenu: 0 USE, 1 CHECK, 2 COMBN (live 2026-10-14)
MESSAGE_START, MESSAGE_END = 0xBF0A1C, 0xBF0A20  # current message text: start, revealed-so-far (pointers)
ITEM_EXAMINED = 0xBE98BC           # 4 bytes, MSB-first: examined items show their real name
ITEM_LOOKUP, ITEM_NAMES, GENERIC_NAMES = 0x4BD81D, 0x4BF0A0, 0x4BF260
NOTHING_IMPORTANT = "Nothing important."

# The game's text encoding (docs/MEMORY_MAP.md, "Text"): glyph bytes and the message tags that matter here.
_GLYPHS = {0x00: " ", 0x1A: "!", 0x19: '"', 0x78: '"', 0x18: ",", 0x79: ".", 0x16: ":", 0x17: ";", 0x1B: "?",
           0x3A: "'", 0x37: "(", 0x39: ")", 0x3B: "-", 0x38: "/"}
_GLYPHS.update({0x0C + i: str(i) for i in range(10)})
_GLYPHS.update({0x1D + i: chr(65 + i) for i in range(26)})
_GLYPHS.update({0x3D + i: chr(97 + i) for i in range(26)})


def decode_text(raw: bytes, selected: int = 0) -> str:
    """Decodes a message: 0x01 ends it, 0x02/0x03 break lines, 0x05 n sets a colour, 0x06 n inserts an item's name
    as the game shows it (n = 0: the selected item), 0x08 is a yes/no prompt."""
    out, i = [], 0
    while i < len(raw):
        b = raw[i]
        if b in (0x01, 0x07):
            break
        if b in (0x02, 0x03):
            out.append("\n")
        elif b == 0x04:
            pass  # "skip embedded tags": no glyph
        elif b == 0x05:
            i += 1
        elif b == 0x06 and i + 1 < len(raw):
            i += 1
            out.append(item_name(raw[i] or selected))
        elif b == 0x08:
            out.append(" [yes/no]")
        else:
            out.append(_GLYPHS.get(b, f"<{b:02x}>"))
        i += 1
    return "".join(out).strip()


def _u32(address: int) -> int:
    return int.from_bytes(peek(address, 4), "little")


def message_pointer() -> int:
    """Start of the message text the game shows (changes with each new message)."""
    return _u32(MESSAGE_START)


def status_open() -> bool:
    """The status screen (inventory) is open."""
    return bool(flags() & 0x8000) and peek(MENU_INPUT, 1)[0] != 0


def message_text() -> str:
    """The text of the message on screen (or the last one shown)."""
    start = _u32(MESSAGE_START)
    return decode_text(peek(start, 400), peek(MENU_ITEM, 1)[0]) if start else ""


def dismiss(timeout: float = 10) -> list[str]:
    """Reads and closes the message on screen page by page: A only while it waits (PAGE/WAITING), never during a
    prompt; each press must change the state or the text. Returns the pages read."""
    import time
    pages, end = [], time.time() + timeout
    while message_active() and time.time() < end:
        state_now = message_state()
        if state_now == PROMPT:
            break
        if state_now in (PAGE, WAITING):
            text, pointer = message_text(), message_pointer()
            if not pages or pages[-1] != text:
                pages.append(text)
            send("press A 120")
            wait_for(lambda: message_state() != state_now or message_pointer() != pointer, 2)
        else:
            send("wait 150")
    return pages


def answer(yes: bool, timeout: float = 10) -> bool:
    """Answers the yes/no prompt on screen: moves the choice (bit 0 of 0xBE9825: 0 Yes, 1 No) until it reads the
    wanted answer, then confirms; True once the prompt has closed."""
    import time
    end = time.time() + timeout
    if message_state() != PROMPT:
        return False
    want = 0 if yes else 1
    while peek(MESSAGE_ACTIVE, 1)[0] & 1 != want:
        if time.time() > end:
            return False
        before = peek(MESSAGE_ACTIVE, 1)[0] & 1
        send("press Right 100")
        wait_for(lambda: peek(MESSAGE_ACTIVE, 1)[0] & 1 != before, 1)
    send("press A 120")
    return wait_for(lambda: message_state() != PROMPT, 5)


def examined_index(item: int) -> int | None:
    """Index into the examined bank for items that show a generic name until examined (keys, cranks, books)."""
    if not 1 <= item <= 0x4D:
        return None
    index = peek(ITEM_LOOKUP + item * 4 + 2, 1)[0]
    return None if index & 0x80 else index


def examined(item: int) -> bool | None:
    index = examined_index(item)
    return None if index is None else bool(_u32(ITEM_EXAMINED) & (0x80000000 >> index))


def item_name(item: int, shown: bool = True) -> str:
    """The item's name from the game's own table — as the inventory shows it now (generic until examined), or the
    real one with shown=False."""
    if not 1 <= item <= 0x80:
        return f"item {item}"
    pointer = _u32(ITEM_NAMES + (item - 1) * 4)
    index = examined_index(item)
    if shown and index is not None and not examined(item):
        pointer = _u32(GENERIC_NAMES + index * 4)
    raw = peek(pointer, 32)
    return decode_text(raw[: raw.index(0x07)] if 0x07 in raw else raw)


HELD_COUNT = 0xBE9827  # g_TotalHeldItems: the game ignores slots at or past it


def inventory() -> list[tuple[int, int, str]]:
    """(item, quantity, name as shown) per held slot — only the first HELD_COUNT slots count, as in the game."""
    raw, held = peek(INVENTORY_SLOTS, 16), peek(HELD_COUNT, 1)[0]
    return [(raw[i * 2], raw[i * 2 + 1], item_name(raw[i * 2])) for i in range(min(held, 8)) if raw[i * 2]]


ITEM_BOX = 0xBE98E4      # 48 entries, item | quantity << 8
BOX_MODE = 0x1000        # stateFlags: the item box screen (pending or open)
BOX_CURSOR = 0xAE9F24    # item box screen: the box entry under the cursor (0 when the screen opens)


def slot_limit() -> int:
    """Jill carries 8, Chris and Rebecca 6."""
    return 8 if peek(0xBE62E5, 1)[0] & 3 == 1 else 6


def item_box() -> list[tuple[int, int]]:
    raw = peek(ITEM_BOX, 96)
    return [(raw[i * 2], raw[i * 2 + 1]) for i in range(48)]


def box_screen() -> int | None:
    """The item box screen's state (1 inventory side, 2 box side, 3 paging), or None when it isn't open."""
    return peek(MENU_INPUT, 1)[0] if flags() & BOX_MODE and flags() & 0x8000 else None


def give(item: int, quantity: int = 1, to: str = "inventory", log=print) -> int:
    """Gives the player an item that looks right. to="box": into the first empty item-box entry (the box screen draws
    icons from the ids). to="inventory": through the game's own item box swap, wherever the player is — the item goes
    in box entry 0, the box screen is opened by its mode bit, the inventory cursor is put on the first empty slot and
    A, A takes it out (the game loads the slot's icon), X closes. Every press waits on the screen's state. Returns the
    box entry or the inventory slot."""
    if to == "box":
        out = send(f"give-box {item} {quantity}")
        return int(out.rsplit("entry ", 1)[-1].split()[0])
    if to != "inventory":
        raise ValueError(f"to must be 'inventory' or 'box', not {to!r}")
    held = peek(HELD_COUNT, 1)[0]
    if held >= slot_limit():
        raise Failed(f"the inventory is full ({held} of {slot_limit()})")
    if not in_control():
        raise Failed(f"the player isn't free to open the item box: {state()}")
    send(f"give-box {item} {quantity} --front", "open-box")
    if not wait_for(lambda: box_screen() == 1, 5):
        raise Failed(f"the item box screen didn't open (flags 0x{flags():08X})")
    send(f"box-cursor {held}")
    send("press A 120")
    if not wait_for(lambda: box_screen() == 2, 3):
        raise Failed(f"the box side didn't take the cursor (screen state {box_screen()})")
    if peek(BOX_CURSOR, 1)[0] != 0 or item_box()[0] != (item, quantity):
        raise Failed(f"the box cursor isn't on the item (entry {peek(BOX_CURSOR, 1)[0]}, entry 0 {item_box()[0]})")
    send("press A 120")
    if not wait_for(lambda: box_screen() == 1 and peek(HELD_COUNT, 1)[0] == held + 1, 3):
        raise Failed(f"the swap didn't happen (screen {box_screen()}, held {peek(HELD_COUNT, 1)[0]})")
    send("press X 120")
    if not wait_for(lambda: box_screen() is None and in_control(), 5):
        raise Failed(f"the item box screen didn't close (flags 0x{flags():08X})")
    slot = peek(INVENTORY_SLOTS + held * 2, 2)
    if (slot[0], slot[1]) != (item, quantity):
        raise Failed(f"slot {held} holds {tuple(slot)}, not {(item, quantity)}")
    log(f"  gave {item_name(item, shown=False)} x{quantity} in slot {held} through the item box")
    return held


def _open_submenu(item: int, timeout: float) -> int:
    """Opens the status screen on the item's slot and its submenu (USE / CHECK / COMBN); returns the slot. Every press
    is checked: the cursor must move (the menu ignores input while it opens) and the selected item must be the target
    before A."""
    import time
    raw, held = peek(INVENTORY_SLOTS, 16), peek(HELD_COUNT, 1)[0]
    slots = [raw[i * 2] if i < held else 0 for i in range(8)]
    if item not in slots:
        raise Failed(f"item {item} is not in the inventory ({slots})")
    target = 8 + 2 * slots.index(item)
    if message_active():
        dismiss()
    if not in_control():
        raise Failed(f"not in control (message state {message_state()}, flags {flags():#x}): the menu won't open")
    send("press Y")
    if not wait_for(lambda: status_open() and peek(MENU_INPUT, 1)[0] == 1, 10):
        shot_path = SHOTS / f"menu-failed-{int(time.time())}.png"
        send(f"shot {shot_path} 640", check=False)
        raise Failed(f"the status screen didn't open (flags {flags():#x}, message state {message_state()}, "
                     f"HP {hp()}, menu input {peek(MENU_INPUT, 1)[0]}, shot {shot_path})")
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
    # The selected-item byte follows the cursor a few frames later.
    wait_for(lambda: peek(MENU_ITEM, 1)[0] == item, 2)
    selected = peek(MENU_ITEM, 1)[0]
    if selected != item:
        raise Failed(f"the menu selected item {selected}, not {item}")
    send("press A 120")
    if not wait_for(lambda: peek(MENU_INPUT, 1)[0] == 2, 5):
        raise Failed("the item submenu didn't open")
    send("wait 400")
    return slots.index(item)


def _choose(entry: int) -> None:
    for _ in range(4):
        cursor = peek(SUBMENU_CURSOR, 1)[0]
        if cursor == entry:
            send("press A 120")
            return
        send(f"press {'Down' if cursor < entry else 'Up'} 100")
        wait_for(lambda: peek(SUBMENU_CURSOR, 1)[0] != cursor, 1.0)
    raise Failed(f"submenu cursor never reached entry {entry}")


def use_item(item: int, timeout: float = 30) -> dict:
    """Chooses USE on the item (closing the menu again if anything goes wrong)."""
    try:
        slot = _open_submenu(item, timeout)
        name = item_name(item)
        _choose(0)
        send("wait 1500")
        return {"slot": slot, "selected": item, "name": name}
    except Failed:
        close_menu()
        raise


def examine(item: int, turns: int = 24) -> dict:
    """CHECK on the item, then turns it in the viewer and presses A at each angle until the game says something other
    than "Nothing important." (keys show their carving and get their real name). Closes the menu afterwards."""
    try:
        _open_submenu(item, 30)
        before = item_name(item)
        _choose(1)
        send("wait 1500")
        notes = []
        for turn in range(turns):
            send("press A 120", "wait 1500")
            text = message_text()
            if text and text != NOTHING_IMPORTANT:
                notes.append(text)
                send("press A 120", "wait 1000")
                break
            send("press A 120", "wait 500", f"press {'Left' if turn < 16 else 'Up'} 250", "wait 250")
        return {"item": item, "before": before, "after": item_name(item), "examined": examined(item), "said": notes}
    finally:
        close_menu()


ENEMIES, ENEMY_SIZE, ENEMY_SLOTS = 0xBE6464, 0x18C, 30
EQUIPPED_SLOT = 0xBE9849


def enemies(all_slots: bool = False) -> list[dict]:
    """Active enemy slots (0xBE6464, 30 x 0x18C: +0 flags bit 1 = active, +1 type, +0x34/+0x3C X/Z, +0x88 HP)."""
    import struct
    raw = peek(ENEMIES, ENEMY_SIZE * ENEMY_SLOTS)
    found = []
    for i in range(ENEMY_SLOTS):
        e = raw[i * ENEMY_SIZE:(i + 1) * ENEMY_SIZE]
        if e[0] & 1 or (all_slots and e[1]):
            found.append({"slot": i, "type": e[1], "flags": e[0], "hp": struct.unpack_from("<h", e, 0x88)[0],
                          "x": struct.unpack_from("<i", e, 0x34)[0], "z": struct.unpack_from("<i", e, 0x3C)[0]})
    return found


def angle_to(x: int, z: int, tx: int, tz: int) -> int:
    """Facing that points from (x, z) at (tx, tz): direction (cos θ, −sin θ), 4096 units per turn."""
    import math
    return round(math.atan2(-(tz - z), tx - x) / (2 * math.pi) * 4096) % 4096


def hp() -> int:
    return int.from_bytes(peek(0xBE636C, 2), "little", signed=True)


def equipped() -> int:
    """Item id of the equipped weapon (0 when none): 0xBE9849 is the 1-based slot (live, bounty B114)."""
    slot = peek(EQUIPPED_SLOT, 1)[0]
    return peek(INVENTORY_SLOTS + (slot - 1) * 2, 1)[0] if 1 <= slot <= 8 else 0


def fire_at(slot: int, shots: int = 1, gap_ms: int = 900) -> list[dict]:
    """Turns to face the enemy in `slot` where the player stands, aims (RT held) and fires `shots` times (A), reading the
    enemy's HP after each shot. Returns one record per shot: the HP before/after and whether it changed."""
    import struct
    x, _, z = pos()
    target = next((e for e in enemies(True) if e["slot"] == slot), None)
    if target is None:
        raise Failed(f"no enemy in slot {slot}")
    send(f"place {x} {z} {angle_to(x, z, target['x'], target['z'])}", "wait 200", "hold RT", "wait 600")
    shots_out = []
    try:
        for _ in range(shots):
            before = next((e["hp"] for e in enemies(True) if e["slot"] == slot), None)
            send("press A 120", f"wait {gap_ms}")
            after = next((e["hp"] for e in enemies(True) if e["slot"] == slot), None)
            shots_out.append({"before": before, "after": after, "damage": None if before is None or after is None else before - after})
            if after is not None and after <= 0:
                break
    finally:
        send("release RT")
    return shots_out


COMBINE_CURSOR = 0xAE9F25  # second grid cursor while combining (8 + 2·slot; live 2026-10-14)


def combine(first: int, second: int, timeout: float = 30) -> dict:
    """COMBN: opens the submenu on `first`, chooses COMBN, moves the combine cursor to `second` (checked) and confirms;
    a "Will you mix ...?" prompt is answered yes. Returns the inventory before/after. Closes the menu afterwards."""
    import time
    before = inventory()
    try:
        _open_submenu(first, timeout)
        _choose(2)
        if not wait_for(lambda: peek(MENU_INPUT + 1, 1)[0] == 4, 3):
            raise Failed("combine mode didn't start")
        slots = [i for i, _, _ in before]
        own = slots.index(first)
        candidates = [i for i, item in enumerate(slots) if item == second and i != own]
        if not candidates:
            raise Failed(f"no second {second} to combine with")
        target = 8 + 2 * candidates[0]
        end = time.time() + timeout
        while (cursor := peek(COMBINE_CURSOR, 1)[0]) != target:
            if time.time() > end:
                raise Failed(f"combine cursor never reached {target} (at {cursor})")
            key = "Down" if (cursor & ~2) < (target & ~2) else "Up" if (cursor & ~2) > (target & ~2) else "Right"
            send(f"press {key} 100")
            wait_for(lambda: peek(COMBINE_CURSOR, 1)[0] != cursor, 1.0)
        send("press A 120")
        said, end = [], time.time() + 6
        while time.time() < end:
            state = message_state()
            if state == PROMPT:
                said.append(message_text())
                answer(True)
            elif state in (PAGE, WAITING):
                said += dismiss()
            elif state:
                send("wait 150")  # still typing
            elif said:
                break
            else:
                send("wait 200")
        return {"first": first, "second": second, "before": before, "after": inventory(), "said": said}
    finally:
        close_menu()


def status_band() -> int:
    """Opens the status screen, reads the ECG band the game draws (0xAE9F2F: 0 Danger, 1 Caution, 2-3 Fine, 4 poison;
    only updated while the screen is drawn) and closes it again."""
    if not in_control():
        raise Failed("not in control: the status screen won't open")
    send("press Y")
    if not wait_for(status_open, 10):
        raise Failed("the status screen didn't open")
    send("wait 1200")
    band = peek(0xAE9F2F, 1)[0]
    close_menu()
    return band


PLAYER_STATUS, POISON_TIMER = 0xBE63C0, 0xBE6458  # player +0xDC (0x02 poison, 0x20 Yawn poison), +0x174


def igt() -> int:
    """In-game timer, frames at 30 fps (0xD22730)."""
    return int.from_bytes(peek(0xD22730, 4), "little")
