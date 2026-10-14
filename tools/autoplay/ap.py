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


def screen() -> str:
    return look(SCREENS).strip().lower().strip(".")


def wait_screen(wanted: set[str], timeout: float = 60, press: str | None = None) -> str:
    """Polls the screen class until it is one of `wanted`, optionally pressing an input between polls."""
    import time
    end = time.time() + timeout
    while time.time() < end:
        kind = screen()
        if kind in wanted:
            return kind
        if press:
            send(f"press {press}")
        send("wait 700")
    raise Failed(f"screen never became {wanted}")


def new_game(character: str = "chris", fast_ms: int = 4) -> float:
    """From a fresh launch to player control in a new game; returns seconds taken. Never touches save files."""
    import time
    t = time.time()
    wait_screen({"title", "newload"}, 60, press="A")
    if screen() != "newload":
        send("press A")
        wait_screen({"newload"}, 15)
    # LOAD GAME is highlighted when saves exist; Up selects NEW GAME.
    send("press Up", "wait 400", "press A")
    wait_screen({"card"}, 30)
    question = "Whose name is printed on the front ID card: answer exactly chris, jill or none."
    name = ""
    for attempt in range(8):
        send("wait 1000")
        name = look(question).strip().lower()
        if character in name:
            break
        if name in ("chris", "jill") and attempt % 2 == 0:
            send("press Right", "wait 1000")
    else:
        raise Failed(f"could not select {character} (card shows {name})")
    send("press A", "wait 1500")
    if fast_ms == 0:
        return time.time() - t
    send(f"speed {fast_ms}")
    send("wait 3000")
    if settle(240, fast_ms) and state().get("char", "").lower() == character:
        return time.time() - t
    raise Failed(f"no control after the intro: {state()}")


def peek(address: int, length: int) -> bytes:
    out = send(f"peek {address:X} {length}")
    return bytes.fromhex(out.split(": ")[-1].split()[0])


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
    while time.time() < end:
        if in_control():
            calm += 1
            if calm >= 3:
                send("speed normal")
                return True
            send("wait 300")
            continue
        calm = 0
        send(f"speed {fast_ms}", "press A 120", "wait 250")
    send("speed normal")
    return False
