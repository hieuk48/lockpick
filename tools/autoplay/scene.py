r"""Jump to any part of the game with either character (dev-only autoplay).

    python -I tools/autoplay/scene.py jill 30F [--save <file.dat>] [--saves <folder>] [--backup <dir>]

Saves default to %LOCALAPPDATA%\Lockpick\research\saves\community (practice saves; never committed).

Picks a save at the right story stage for the room (same character; the room's stage, or the nearest progress for
early/return mansion via scenario flag 0), borrows slot 1 to load it (owner's file restored and hash-checked right
after the load), then enters the room through a real door record and leaves the game running with the harness
server, so it can be played or tested from there. Never saves; SAVE/ stays read-only.
"""
from __future__ import annotations

import os
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "bounties"))
import ap  # noqa: E402
import doors  # noqa: E402
import library  # noqa: E402
import saves  # noqa: E402
import survey  # noqa: E402

RETURN_MANSION = (0xBE98C0 - 0xBE9620, 0x80000000)  # scenario flag 0: stage 1/2 doors load stages 6/7


def candidates(folder: Path, character: str) -> list[dict]:
    found = []
    for path in sorted(folder.rglob("*.dat")):
        state = saves.read_save(path)
        if state is None or ("jill" if state["character"] else "chris") != character:
            continue
        data = path.read_bytes()
        (word,) = struct.unpack_from("<I", data, RETURN_MANSION[0])
        found.append({"path": path, "stage": state.get("stage", 0) + 1, "room": f"{state.get('stage', 0) + 1}{state.get('room', 0):02X}",
                      "returned": bool(word & RETURN_MANSION[1]),
                      "progress": sum(bin(b).count("1") for b in data[0x2B0:0x2C4])})  # flagRoom visits
    return found


def pick(folder: Path, character: str, room: str) -> dict:
    stage = int(room[0])
    options = candidates(folder, character)
    if stage in (1, 2):
        options = [o for o in options if not o["returned"]]
    elif stage in (6, 7):
        options = [o for o in options if o["returned"]]
    exact = [o for o in options if o["room"] == room]
    same = [o for o in options if o["stage"] == stage]
    pool = exact or same or options
    if not pool:
        raise SystemExit(f"no {character} save fits room {room}")
    # A save in the room itself; else the earliest save in the room's stage (first-visit state); else the earliest
    # save with the right mansion state.
    return min(pool, key=lambda o: o["progress"])


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    character, room = argv[0].lower(), argv[1].upper()
    if not survey.room_file_ok(room, character):
        raise SystemExit(f"room {room} has no room file for {character} (a stub that crashes the game)")
    opt = lambda name, default: Path(argv[argv.index(name) + 1]) if name in argv else default  # noqa: E731
    night = Path(os.environ["LOCALAPPDATA"]) / "Lockpick" / "research" / "night-20261014"
    folder = opt("--saves", Path(os.environ["LOCALAPPDATA"]) / "Lockpick" / "research" / "saves" / "community")
    backup = opt("--backup", night / "save-backup")
    save = {"path": opt("--save", None)} if "--save" in argv else pick(folder, character, room)
    print(f"save: {save['path'].name} ({save.get('room', '?')})")
    survey.protect_saves()
    library.borrow(save["path"], backup)
    try:
        ap.serve()
        library.load_slot(1)
    finally:
        print("slot 1 restored:", library.restore_slot1(backup))
    survey.check_saves(backup, print)
    if ap.state().get("room") != room:
        table = doors.build(survey.INSTALL)
        ap.send("door " + doors.record_to(table, room, character).hex(), "wait 800")
        ap.settle(60)
    print(ap.state())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
