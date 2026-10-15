r"""Cutscene skips for the dev harness (never shipped): a scene's end is recorded once by watching it, then applied
instead of playing it — the saved-state bits it changes (0xBE9620–0xBE9E1F, what a save holds: flags, items, box) and
where it leaves the player. Files hold addresses, bits and positions only (no game text).

    python -I tools/autoplay/scenes.py record-opening jill|chris    watch the opening once, write scenes/opening-<c>.json
    python -I tools/autoplay/scenes.py check-opening jill|chris     boot with the skip; compare with the watched end

The opening is skipped before it starts: the harness stops the new game at the frame before the first room loads
(NewGame.cs) and applies the recorded end there, so the game loads straight into the room where the opening hands over
control.
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import ap  # noqa: E402
import survey  # noqa: E402

SCENES = Path(__file__).parent / "scenes"
BLOCK, SIZE = 0xBE9620, 0x800
# Not part of a scene's end: room and camera (set from the end spot), previous room, the message byte, a value that
# differs run to run (0xBE983A–3C), and the saved clock.
IGNORED = {0x201, 0x202, 0x203, 0x205, 0x21A, 0x21B, 0x21C, 0x224, 0x225, 0x226, 0x227}
START_ROOM = "105"  # both openings hand over control in the dining room


def block() -> bytes:
    return ap.peek(BLOCK, SIZE)


def end_spot() -> dict:
    x, y, z = ap.pos()
    return {"room": ap.state().get("room"), "camera": ap.peek(0xBE9822, 1)[0], "pos": [x, y, z],
            "yaw": int.from_bytes(ap.peek(0xBE6358, 2), "little", signed=True)}


def bits(before: bytes, after: bytes) -> list[list[str]]:
    """[address, bits to set, bits to clear] per changed byte, so the change applies on top of other progress."""
    return [[f"{BLOCK + i:X}", f"{after[i] & ~before[i] & 0xFF:02X}", f"{before[i] & ~after[i] & 0xFF:02X}"]
            for i in range(SIZE) if before[i] != after[i] and i not in IGNORED]


def wait_control(room: str, timeout: float = 300, fast_ms: int = 4) -> bool:
    if fast_ms:
        ap.send(f"speed {fast_ms}")
    try:
        end, since = time.time() + timeout, None
        while time.time() < end:
            ready = ap.state().get("room") == room and ap.in_control()
            since = (since or time.time()) if ready else None
            if since and time.time() - since >= 1.5:
                return True
            time.sleep(0.25)
        return False
    finally:
        if fast_ms:
            ap.send("speed normal", check=False)


def opening_file(character: str) -> Path:
    return SCENES / f"opening-{character}.json"


def record_opening(character: str) -> dict:
    survey.protect_saves()
    ap.serve()
    out = ap.send(f"new-game {character} 120 --dump")
    before = bytes.fromhex(out.split("block ", 1)[1].split()[0])
    if not wait_control(START_ROOM):
        raise ap.Failed(f"no control after the opening: {ap.state()}")
    scene = {"scene": f"opening ({character})", **end_spot(), "bits": bits(before, block())}
    SCENES.mkdir(exist_ok=True)
    opening_file(character).write_text(json.dumps(scene, indent=1) + "\n", encoding="utf-8")
    return scene


def check_opening(character: str) -> dict:
    """Boots with the skip and compares the saved block with the recorded end (bit by bit, ignored bytes aside)."""
    scene = json.loads(opening_file(character).read_text(encoding="utf-8"))
    survey.protect_saves()
    t = time.time()
    ap.serve()
    ap.send(f"new-game {character} 120 --skip-opening {opening_file(character)}")
    controlled = wait_control(scene["room"], 60, fast_ms=0)
    now = block()
    wrong = [b for b in scene["bits"]
             if (now[int(b[0], 16) - BLOCK] & int(b[1], 16)) != int(b[1], 16) or now[int(b[0], 16) - BLOCK] & int(b[2], 16)]
    return {"control": controlled, "seconds": round(time.time() - t, 1), "state": ap.state(), "flags": f"0x{ap.flags():08X}",
            "events": ap.events(), "bitsWrong": wrong, "inventory": ap.inventory()}


if __name__ == "__main__":
    command, who = sys.argv[1], sys.argv[2]
    result = record_opening(who) if command == "record-opening" else check_opening(who)
    print(json.dumps(result, indent=1))
