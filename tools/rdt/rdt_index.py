"""Builds data/games/re1/room-index.json from the game's own room files (RDT), read-only.

The index records only ids and numbers: per room and character, the item pickups (item id, quantity, pickup flag),
enemies (type, kill flag) and doors (target room, lock flag, key item). It lets the bounty analyzer turn flag changes
in a capture into "picked up item 33 in room 105". No game data (models, text, scripts) is copied.

Usage:
    python tools/rdt/rdt_index.py "G:\\GOG Games\\Resident Evil"     # writes data/games/re1/room-index.json
    python tools/rdt/rdt_index.py <install> --dump 105                # prints what the room files of 105 contain

Format notes (RE1 PC): header + u32 offsets[19] at 0x48; offsets[6]/[7] are the init/main script chains of
[u16 length incl. itself][bytecode], ended by a u16 0. Opcode sizes and field layouts follow IntelOrca's
biohazard-utils / BioRand (MIT); see docs/RESEARCH.md. Walking every retail RDT with this table ends each script
exactly on its length (no desync), which is the check `--check` repeats.
"""
from __future__ import annotations

import argparse
import json
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "data" / "games" / "re1" / "room-index.json"

# Bytes per opcode, including the opcode byte (0x26 and 0x2E never occur in retail data).
SIZES = [
    2, 2, 2, 2, 4, 4, 4, 6, 4, 2, 2, 4, 26, 18, 2, 8,          # 00-0F
    2, 2, 10, 4, 4, 2, 2, 10, 26, 4, 2, 22, 6, 2, 4, 28,       # 10-1F
    14, 14, 4, 2, 4, 4, 0, 2, 6, 2, 12, 4, 2, 4, 0, 4,         # 20-2F
    12, 4, 4, 2, 8, 4, 4, 4, 4, 2, 4, 6, 6, 12, 2, 6,          # 30-3F
    16, 4, 4, 4, 2, 2, 44, 14, 2, 2, 2, 2, 4, 2, 4, 2,         # 40-4F
    2,                                                          # 50
]
OP_DOOR, OP_ITEM, OP_ENEMY, OP_SET = 0x0C, 0x18, 0x1B, 0x05
# Flag groups of `set`, as matched against live captures: 0 = story bank A (flagScenarioA), 1 = story bank B
# (flagScenarioB). Other groups are per-room or not mapped yet.
STORY_GROUPS = {0: "A", 1: "B"}
NOT_PERSISTED = 255
PLAYERS = {"0": "chris", "1": "jill"}
ROOM_FILE = re.compile(r"^ROOM([1-7])([0-9A-F]{2})([01])\.RDT$", re.I)


def scripts(data: bytes) -> list[tuple[int, int]]:
    offsets = struct.unpack_from("<19I", data, 0x48)
    spans = []
    for index in (6, 7):
        pos = offsets[index]
        while (length := struct.unpack_from("<H", data, pos)[0]) != 0:
            spans.append((pos + 2, pos + length))
            pos += length
    return spans


def opcodes(data: bytes):
    for start, end in scripts(data):
        pos = start
        while pos < end:
            op = data[pos]
            size = SIZES[op] if op < len(SIZES) else 0
            if size == 0 or pos + size > end:
                raise ValueError(f"unknown opcode 0x{op:02X} or overrun at 0x{pos:X}")
            yield op, data[pos:pos + size]
            pos += size


def read_room(path: Path, stage: int) -> dict:
    data = path.read_bytes()
    items, enemies, doors, story = [], [], [], []
    for op, b in opcodes(data):
        if op == OP_ITEM:
            items.append({"item": b[0x0A], "quantity": b[0x0B], "flag": b[0x16]})
        elif op == OP_ENEMY:
            enemies.append({"type": b[1], "flag": None if b[3] == NOT_PERSISTED else b[3]})
        elif op == OP_SET and b[1] in STORY_GROUPS:
            story.append({"bank": STORY_GROUPS[b[1]], "flag": b[2], "value": b[3]})
        elif op == OP_DOOR:
            lock, target, key = b[0x0E], b[0x0F], b[0x18]
            target_stage = target >> 5 or stage
            doors.append({
                "target": f"{target_stage}{target & 0x1F:02X}",
                "lockFlag": lock & 0x3F if lock & 0x80 and lock != 0xFF else None,
                "key": key if 0 < key < 254 else None,
            })
    return {"items": items, "enemies": enemies, "doors": doors, "story": story}


def build(install: Path) -> dict:
    usa = install / "USA" if (install / "USA").is_dir() else install
    rooms: dict[str, dict[str, dict]] = {}
    for path in sorted(usa.glob("Stage*/ROOM*.RDT")):
        m = ROOM_FILE.match(path.name)
        if not m or path.stat().st_size < 0x94:
            continue  # unused 4-byte room slots
        stage, room, variant = m.groups()
        rooms.setdefault(f"{stage}{room.upper()}", {})[PLAYERS[variant]] = read_room(path, int(stage))
    if not rooms:
        raise SystemExit(f"no room files under {usa}")

    def merged(kind: str) -> list[dict]:
        out = []
        for room_id, variants in sorted(rooms.items()):
            seen: dict[str, dict] = {}
            for player, content in variants.items():
                for entry in content[kind]:
                    key = json.dumps(entry, sort_keys=True)
                    if key in seen:
                        seen[key]["player"] = "both"
                    else:
                        seen[key] = {"room": room_id, "player": player, **entry}
            out.extend(seen.values())
        return out

    return {
        "comment": "Generated by tools/rdt/rdt_index.py from the game's room files: ids and flag numbers only. "
                   "player = which character's room file; flags are indices into the ITEM / ENEMY / LOCK flag banks "
                   "(bit 31 of the first word = index 0); story = flags set by the room's script in bank A or B.",
        "items": merged("items"),
        "enemies": merged("enemies"),
        "doors": merged("doors"),
        # Story flags the room's own (init/main) script sets — cutscene/event scripts are not parsed yet.
        "story": merged("story"),
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("install", type=Path, help="game folder (contains USA\\Stage1..7)")
    parser.add_argument("--dump", metavar="ROOM", help="print one room (e.g. 105) instead of writing the index")
    parser.add_argument("--out", type=Path, default=OUT)
    args = parser.parse_args(argv)
    index = build(args.install)
    if args.dump:
        for kind in ("items", "enemies", "doors", "story"):
            for entry in index[kind]:
                if entry["room"] == args.dump.upper():
                    print(kind[:-1], json.dumps(entry))
        return 0
    text = json.dumps(index, indent=1)
    # One entry per line keeps diffs readable.
    text = re.sub(r"\{\n\s+", "{", text)
    text = re.sub(r",\n\s+(?=\")", ", ", text)
    text = re.sub(r"\n\s+\}", "}", text)
    args.out.write_text(text + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {args.out.relative_to(ROOT) if args.out.is_relative_to(ROOT) else args.out}: "
          f"{len(index['items'])} items, {len(index['enemies'])} enemies, {len(index['doors'])} doors, "
          f"{len(index['story'])} story-flag sets")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
