r"""Route player (dev-only autoplay): plays story steps the way a player would — enter a room through a door, pick an
item up, use an item at its spot — using the room scripts to find where things are, and checking every step against
memory. The first route is the dining-room shield key (Jill): emblem → music notes → piano → gold emblem → emblem
back in the slot → gold emblem at the fireplace → shield key.

    python -I tools/autoplay/route.py <out-dir> <save-backup-dir> [shield-key]

Never saves; SAVE/ stays read-only. Item-use spots: a room action "flag set" (handler 7) with words (9, item − 0x1B, 1)
raises the item's use bit (bank 9, 0xD213A0) while the player stands there; USE in the menu checks it.
"""
from __future__ import annotations

import json
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import ap  # noqa: E402
import doors  # noqa: E402
import pickup  # noqa: E402
import rdt_map  # noqa: E402
import scd  # noqa: E402
import survey  # noqa: E402

ITEM_USE = 0xD213A0
STORY_A, STORY_B = 0xBE98C0, 0xBE9854


def flag(bank: int, n: int) -> bool:
    raw = ap.peek(bank + (n // 32) * 4, 4)
    return bool(struct.unpack("<I", raw)[0] & (0x80000000 >> (n % 32)))


def use_spots(room: str, character: str, item: int) -> list[list[int]]:
    """Zones where the room raises the use bit of `item`."""
    listing = scd.read_room(rdt_map.path_for(survey.INSTALL, room, character), scd.Names())
    return [st["zone"] for part in ("init", "main") for st in listing[part]
            if st["kind"] == "action" and st["handler"] == 7 and st["words"][:2] == [9, item - 0x1B]]


class Route:
    def __init__(self, character: str, log) -> None:
        self.character, self.log = character, log
        self.table = doors.build(survey.INSTALL)
        self.room: str | None = None

    def enter(self, room: str) -> None:
        pickup.enter(room, self.character, self.table)
        self.room = room
        self.log(f"in {room}: {ap.state()}")

    def take(self, item: int) -> bool:
        armed = [a for a in pickup.active_items() if a["item"] == item]
        if not armed:
            self.log(f"  {item}: not armed here")
            return False
        result = pickup.try_pickup({**armed[0]}, rdt_map.load(survey.INSTALL, self.room, self.character), clear_inventory=False)
        ok = item in result.get("gainedItems", []) or bool(result.get("clearedFlags"))
        self.log(f"  take {item}: {'ok' if ok else 'FAILED'} {result.get('clearedFlags')}")
        return ok

    def push(self, x: int, z: int, angle: int, then_face: int | None = None) -> tuple[int, int]:
        """Stands at (x, z) facing `angle` and walks forward until the player stops — pushing whatever is in the way.
        A push shows as progress into where the object stood (2026-10-14: 10F's crate never raised flag 0x40)."""
        room = rdt_map.load(survey.INSTALL, self.room, self.character)
        camera = room.camera_for(x, z)
        ap.send(f"place {x} {z} {angle}", "wait 400", *([f"camera {camera}", "wait 600"] if camera is not None else []))
        last = None
        for _ in range(12):
            ap.send("press LUp 1500")
            now = ap.pos()
            if now == last:
                break
            last = now
        self.log(f"  pushed from ({x},{z}) facing {angle}; stopped at {last}")
        if then_face is not None and last:
            ap.send(f"place {last[0]} {last[2]} {then_face}", "wait 300")
        return (last[0], last[2]) if last else (x, z)

    def take_here(self, item: int) -> bool:
        """Presses A where the player stands (after a push put them in front of the item) and answers the prompts."""
        before = ap.peek(0xBE9944, 16)
        ap.send("press A 150", "wait 1000")
        for _ in range(10):
            if not ap.flags() & (0x800 | 0x8000 | 0x100):
                break
            ap.send("press A 150", "wait 900")
        ok = item in [b for b in ap.peek(0xBE9944, 16)[::2]] and before != ap.peek(0xBE9944, 16)
        self.log(f"  take {item} here: {'ok' if ok else 'FAILED'}")
        return ok

    def use(self, item: int) -> bool:
        room = rdt_map.load(survey.INSTALL, self.room, self.character)
        for zone in use_spots(self.room, self.character, item):
            for x, z, angle, _ in pickup.spots({"zone": zone}, room):
                ap.send(f"place {x} {z} {angle}", "wait 500")
                if flag(ITEM_USE, item - 0x1B):
                    self.log(f"  at the spot for {item} ({x},{z} facing {angle}); using it")
                    info = ap.use_item(item)
                    ap.settle(120)
                    self.log(f"  used {item}: {info}; now {ap.state()}")
                    return True
        self.log(f"  use {item}: no spot raised its use bit")
        return False


def shield_key(r: Route) -> list[tuple[str, bool]]:
    checks = []
    r.enter("105")
    checks.append(("take the Emblem", r.take(31)))
    r.enter("10F")
    # A crate stands in front of the shelf with the music notes: push it east, then face the shelf (+Z).
    r.push(15600, 11148, 0, then_face=3072)
    checks.append(("take the Music notes", r.take_here(35)))
    checks.append(("play the piano (use Music notes)", r.use(35)))
    checks.append(("take the Gold emblem", r.take(32)))
    checks.append(("put the Emblem in the slot (B167)", r.use(31) and flag(STORY_B, 167)))
    r.enter("105")
    checks.append(("gold emblem on the dining-room wall (B171)", r.use(32) and flag(STORY_B, 171)))
    ap.settle(120)
    checks.append(("take the Shield key", r.take(53)))
    return checks


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    out, backup = Path(argv[0]), Path(argv[1])
    out.mkdir(parents=True, exist_ok=True)
    logfile = (out / "route.log").open("a", encoding="utf-8")

    def log(text: str) -> None:
        line = f"{time.strftime('%H:%M:%S')} {text}"
        print(line, flush=True)
        logfile.write(line + "\n")
        logfile.flush()

    survey.protect_saves()
    ap.serve()
    ap.send(f"record {out / 'captures'}")
    try:
        log(f"new game in {ap.new_game('jill'):.0f} s")
        r = Route("jill", log)
        checks = shield_key(r)
        for name, ok in checks:
            log(f"{'PASS' if ok else 'FAIL'}  {name}")
        (out / "route-shield-key.json").write_text(json.dumps(checks, indent=1) + "\n", encoding="utf-8")
    finally:
        ap.send("record stop", check=False)
        survey.check_saves(backup, print)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
