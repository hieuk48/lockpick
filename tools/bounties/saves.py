"""Reads RE1 PC save files (savedatN.dat, 2690 bytes) offline: character, room, inventory, item box, progress, story.

    python tools/bounties/saves.py <folder or .dat files> [--json out.json]

A save is a copy of the game-state block: save offset + 0xBE9620 = memory address for offsets 0x04–0x41B (stage, room
and camera at 0xBE9820, every flag bank, the item box at 0xBE98E4, the inventory at 0xBE9944) — found by matching a
save written during a live capture against the memory recorded at that moment (2026-10-13). The character is the low
bit of 0xBE9829 (offset 0x209: Chris 0, Jill 1; bit 0x4 is set in the underground caves and the lab — lead from the
RE1aio autosplitter's characterID, checked against 75 saves); byte 0x212 agrees (0x2F, the ink ribbon a save costs,
for Chris; 0 for Jill).
Read-only: save files are only read.
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import bounties  # noqa: E402
import progress_preview  # noqa: E402
import timeline  # noqa: E402

SAVE_SIZE = 2690
BLOCK = 0xBE9620
BLOCK_END = BLOCK + 0x41C
CHARACTER_BYTE = 0x209  # 0xBE9829: bit 0 = Jill, bit 0x4 = underground/lab section


def read_save(path: Path) -> dict | None:
    data = path.read_bytes()
    if len(data) != SAVE_SIZE or data[:2] != b"SC":
        return None
    state = {}
    for f in bounties.load_profile_fields("re1-gog-usa-hotfix4"):
        width = struct.calcsize(bounties.FORMATS[f["type"]])
        if BLOCK + 4 <= f["address"] and f["address"] + width <= BLOCK_END:
            (v,) = struct.unpack_from(bounties.FORMATS[f["type"]], data, f["address"] - BLOCK)
            state[f["name"]] = v & f["mask"] if f["mask"] is not None else v
    state["character"] = data[CHARACTER_BYTE] & 1
    return state


def describe(path: Path, state: dict, names: timeline.Names) -> dict:
    player = "jill" if state["character"] == 1 else "chris"
    room = f"{state.get('stage', 0) + 1}{state.get('room', 0):02X}"
    slots = 8 if player == "jill" else 6
    inventory = [names.item(v & 0xFF, v >> 8) for i in range(slots) if (v := state.get(f"inv{i}", 0)) & 0xFF]
    box = [names.item(v & 0xFF, v >> 8) for i in range(48) if (v := state.get(f"box{i}", 0)) & 0xFF]
    explored = sum(progress_preview.bit(state, "flagRoom", base + r) for base, count in progress_preview.VISIT_TABLE for r in range(count))
    story = [label for (bank, mask), label in timeline.KNOWN_FLAGS.items()
             if bank.startswith("flagScenario") and state.get(bank, 0) & mask]
    return {
        "file": path.name, "character": player.title(), "room": names.room(room) if hasattr(names, "room") else room,
        "inventory": inventory, "box": box, "roomsExplored": explored, "namedStoryEvents": len(story),
        "lastStory": story[-3:],
    }


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    out = None
    if "--json" in argv:
        at = argv.index("--json")
        out = Path(argv[at + 1])
        argv = argv[:at] + argv[at + 2:]
    paths = []
    for a in argv:
        p = Path(a)
        paths += sorted(p.rglob("*.dat")) if p.is_dir() else [p]
    names = timeline.Names()
    rows = []
    for path in paths:
        if (state := read_save(path)) is not None:
            rows.append(describe(path, state, names))
    for r in rows:
        print(f"{r['file']:42} {r['character']:5} {r['room']:34} rooms {r['roomsExplored']:3}  story {r['namedStoryEvents']:3}  "
              f"inv: {', '.join(r['inventory'])}")
    if out:
        out.write_text(json.dumps(rows, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
