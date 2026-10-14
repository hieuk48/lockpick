r"""Item pickup verification (dev-only autoplay): stands at every item's pickup zone (from the room files), picks the
item up, and records what arrived in the inventory and which item flag cleared — checking the room index's
item/flag pairs against the game itself, per character. Never saves; SAVE/ stays read-only.

    python -I tools/autoplay/pickup.py <out-dir> <save-backup-dir> [jill|chris ...] [--rooms 105,106]
        [--from-save <file.dat> --tag <phase>]

Writes <out-dir>/pickup-<character>[-<phase>].jsonl. Live check (2026-10-14): Jill's Emblem in 105 — facing 1024 at
the zone centre, "Will you take the EMBLEM?", A → inventory 0x011F (item 31) and item flag 42 cleared, as indexed.
"""
from __future__ import annotations

import json
import math
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import ap  # noqa: E402
import rdt_map  # noqa: E402
import doors  # noqa: E402
import survey  # noqa: E402

INVENTORY, ITEM_FLAGS = 0xBE9944, 0xBE989C
KEEP_SLOTS = 3  # starting knife / handgun / spray stay; everything picked up is cleared before the next item
FACINGS = (0, 1024, 2048, 3072)
INTERACTING = 0x800 | ap.MESSAGE | 0x0400 | 0x0100


def inventory() -> list[int]:
    raw = ap.peek(INVENTORY, 16)
    return [struct.unpack_from("<H", raw, i * 2)[0] for i in range(8)]


def cleared_flags(before: bytes, after: bytes) -> list[int]:
    """Item-flag numbers (flag n = bit 31 − n % 32 of u32 word n / 32) that went from 1 (in the world) to 0."""
    found = []
    for word in range(len(before) // 4):
        b, a = struct.unpack_from("<I", before, word * 4)[0], struct.unpack_from("<I", after, word * 4)[0]
        for bit in range(32):
            if b >> bit & 1 and not a >> bit & 1:
                found.append(word * 32 + 31 - bit)
    return sorted(found)


# Facing: angle 0 walks along +X, 1024 along −Z (live 2026-10-14), i.e. direction (cos θ, −sin θ), θ = angle·2π/4096.
# The action press probes the point 600 units straight ahead (check_action_object 0x41C150; entries with flag 0x40
# test the player's own position instead).
REACH = 600
ANGLES = tuple(range(0, 4096, 512))


def direction(angle: int) -> tuple[float, float]:
    theta = angle * 2 * math.pi / 4096
    return math.cos(theta), -math.sin(theta)


ACTIONS, ACTIONS_TAIL, PICKUP_ACTION = 0xD91AA0, 0xD91BC0, 4
PLAYER_RADIUS = 120


def active_items() -> list[dict]:
    """The item pickups the game has armed in this room right now: g_RoomActionTable (0xD91AA0, 12-byte entries up to
    the tail pointer at 0xD91BC0; decomp), action 4 = set_key_flag (item pickup); +6 item flag; +8 → the item record
    (x, z, width, depth, then the item id at +8)."""
    table, tail = ap.peek(ACTIONS, 288), struct.unpack("<I", ap.peek(ACTIONS_TAIL, 4))[0]
    count = (tail - ACTIONS) // 12 + 1 if ACTIONS <= tail < ACTIONS + 288 else 0
    found = []
    for i in range(count):
        entry = table[i * 12:(i + 1) * 12]
        if entry[0] != PICKUP_ACTION:
            continue
        record = ap.peek(struct.unpack_from("<I", entry, 8)[0], 0x0C)
        x, z, w, d = struct.unpack_from("<hhHH", record, 0)
        found.append({"item": record[8], "flag": struct.unpack_from("<H", entry, 6)[0], "zone": [x, z, w, d],
                      "entryFlags": entry[1]})
    return found


def spots(item: dict, room: rdt_map.Room, limit: int = 10) -> list[tuple[int, int, int]]:
    """Where to stand and which way to face so the action probe (600 units ahead) lands inside the zone. Targets are
    spread over the zone, not just its centre — furniture often covers most of a zone (105's emblem zone is mostly
    inside the fireplace) — and standing spots inside walls or furniture are skipped: a press there can set off
    scenery (2026-10-14: standing inside the dining-room clock moved the clock). Targets nearest the zone's centre
    first, at most one spot per facing. Entries with flag 0x40 test the player's own position: stand in the zone."""
    x, z, w, d = item["zone"]
    cx, cz = x + w / 2, z + d / 2
    targets = sorted(((x + w * fx, z + d * fz) for fx in (0.5, 0.3, 0.7, 0.15, 0.85) for fz in (0.5, 0.3, 0.7, 0.15, 0.85)),
                     key=lambda t: (t[0] - cx) ** 2 + (t[1] - cz) ** 2)
    found, used = [], set()
    for tx, tz in targets:
        for a in ANGLES:
            if a in used:
                continue
            if item.get("entryFlags", 0) & 0x40:
                sx, sz = tx, tz
            else:
                dx, dz = direction(a)
                sx, sz = tx - dx * REACH, tz - dz * REACH
            if not room.blocked(round(sx), round(sz), PLAYER_RADIUS):
                found.append((round(sx), round(sz), a, obstructed(room, sx, sz, tx, tz)))
                used.add(a)
        if len(found) >= limit:
            break
    return sorted(found, key=lambda f: f[3])  # clear lines of reach first


def obstructed(room: rdt_map.Room, sx: float, sz: float, tx: float, tz: float) -> bool:
    """True when the line from the player to the probe target passes through blocking collision — the item is behind
    or inside scenery (a clock, a fireplace, a painting's wall)."""
    steps = max(1, int(math.hypot(tx - sx, tz - sz) // 50))
    return any(room.blocked(round(sx + (tx - sx) * i / steps), round(sz + (tz - sz) * i / steps), 0) for i in range(1, steps + 1))


def try_pickup(item: dict, room: rdt_map.Room) -> dict:
    result: dict = {"triggered": None, "prompts": 0}
    ap.send(f"inv-clear {KEEP_SLOTS}")
    inv_before, flags_before = inventory(), ap.peek(ITEM_FLAGS, 32)
    candidates = spots(item, room)
    result["clearSpots"] = sum(1 for c in candidates if not c[3])
    if not candidates:
        result["note"] = "every spot around the zone is inside collision (not reachable in this state)"
        return result
    for x, z, angle, through in candidates:
        camera = room.camera_for(x, z)
        locked = ap.flags() & 0x100000  # a cutscene holds the camera: don't cut
        cut = [f"camera {camera}", "wait 600"] if camera is not None and not locked else []
        ap.send(f"place {x} {z} {angle}", "wait 300", *cut, "press A 150", "wait 900")
        if ap.flags() & INTERACTING:
            result["triggered"] = [x, z, angle]
            result["throughScenery"] = through
            break
    if result["triggered"] is not None:
        # Examine text, "Will you take …?" (Yes is the default), document pages: A until control returns.
        for press in range(40):  # documents can run to many pages, and some loop: cancel (X) closes them
            if not ap.flags() & INTERACTING:
                break
            ap.send(f"press {'X' if press >= 10 and press % 2 else 'A'} 150", "wait 900")
            result["prompts"] += 1
        ap.settle(30)
    inv_after, flags_after = inventory(), ap.peek(ITEM_FLAGS, 32)
    result["gained"] = [v for v in inv_after if v and v not in inv_before]
    result["gainedItems"] = [v & 0xFF for v in result["gained"]]
    result["clearedFlags"] = cleared_flags(flags_before, flags_after)
    result["itemMatches"] = item["item"] in result["gainedItems"]
    result["flagMatches"] = item["flag"] in result["clearedFlags"]
    return result


def enter(room: str, character: str, table: list[dict]) -> None:
    record = doors.record_to(table, room, character)
    for _ in range(3):
        ap.send("door " + record.hex(), "wait 800", timeout=60)
        for _ in range(25):
            now = ap.state().get("room")
            if now == room:
                ap.send("heal")
                ap.settle(60)
                ap.send("freeze-enemies")  # sweeps don't need the room's enemies acting (count 0 until the next room)
                return
            if now and now[1:] == room[1:] and int(now[0]) == int(room[0]) + 5:
                raise ap.Failed(f"redirected to {now}")
            ap.send("wait 300")
        ap.settle(15)
    raise ap.Failed(f"did not arrive in {room}")


def run(character: str, out: Path, backup: Path, table: list[dict], only: set[str] | None) -> None:
    results = out / f"pickup-{character}{survey.START['tag']}.jsonl"
    done = {(e["room"], e["item"], e["flag"]) for e in map(json.loads, results.read_text(encoding="utf-8").splitlines())
            if not e.get("error")} if results.exists() else set()
    logfile = (out / f"pickup-{character}{survey.START['tag']}.log").open("a", encoding="utf-8")

    def log(text: str) -> None:
        line = f"{time.strftime('%H:%M:%S')} {text}"
        print(line, flush=True)
        logfile.write(line + "\n")
        logfile.flush()

    seen, items = set(), []
    for it in doors.items(survey.INSTALL):
        key = (it["room"], it["item"], it["flag"])
        if it["character"] != character or key in seen or key in done or not survey.room_file_ok(it["room"], character):
            continue
        if only is not None and it["room"] not in only:
            continue
        seen.add(key)
        items.append(it)
    log(f"{len(items)} item zones for {character}")
    survey.fresh_game(character, log)
    current, armed = None, []
    for it in items:
        entry = {"room": it["room"], "character": character, "item": it["item"], "quantity": it["quantity"],
                 "flag": it["flag"], "zone": it["zone"], "at": time.strftime("%Y-%m-%dT%H:%M:%S")}
        if it["zone"][2] <= 16 and it["zone"][3] <= 16:
            entry["note"] = "no pickup zone (placed by a script event)"
        else:
            for attempt in range(2):
                try:
                    s = ap.state()
                    if s.get("gameplay") != "True" or ap.flags() & ap.DEAD:
                        survey.fresh_game(character, log)
                        current = None
                    if current != it["room"]:
                        enter(it["room"], character, table)
                        current = it["room"]
                        armed = active_items()
                    live = [a for a in armed if a["item"] == it["item"] and a["flag"] == it["flag"]]
                    if not live:
                        entry["note"] = "not armed in this state (the room script hasn't placed it, or it was taken)"
                    elif live[0]["zone"][2] == 0:
                        entry["note"] = "armed without a zone (an event hands it over)"
                    else:
                        entry["liveZone"] = live[0]["zone"]
                        entry.update(try_pickup({**it, "zone": live[0]["zone"], "entryFlags": live[0]["entryFlags"]},
                                                rdt_map.load(survey.INSTALL, it["room"], character)))
                    entry.pop("error", None)
                    break
                except (ap.Failed, KeyError, ValueError, OSError) as e:
                    entry["error"] = str(e)[-300:]
                    current = None
                    try:
                        survey.fresh_game(character, log)
                    except ap.Failed as e2:
                        log(f"restart failed: {e2}; restarting the server")
                        ap.serve()
                        survey.fresh_game(character, log)
        status = entry.get("note") or entry.get("error", "")[-80:] or (
            f"item {'OK' if entry['itemMatches'] else 'got ' + str(entry['gainedItems'])}, flag "
            f"{'OK' if entry['flagMatches'] else 'cleared ' + str(entry['clearedFlags'])}"
            if entry.get("triggered") is not None else "no pickup prompt")
        log(f"{it['room']} item {it['item']} flag {it['flag']}: {status}")
        with results.open("a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
    logfile.close()


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    out, backup = Path(argv[0]), Path(argv[1])
    only = set(argv[argv.index("--rooms") + 1].upper().split(",")) if "--rooms" in argv else None
    characters = [a for a in argv[2:] if a in ("jill", "chris")] or ["jill", "chris"]
    if "--from-save" in argv:
        survey.START.update(save=Path(argv[argv.index("--from-save") + 1]), backup=backup,
                            tag="-" + (argv[argv.index("--tag") + 1] if "--tag" in argv else "phase"))
    out.mkdir(parents=True, exist_ok=True)
    table = doors.build(survey.INSTALL)
    survey.protect_saves()
    ap.serve()
    ap.send(f"record {out / 'captures'}")
    try:
        for character in characters:
            run(character, out, backup, table, only)
    finally:
        ap.send("record stop", check=False)
        survey.check_saves(backup, print)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
