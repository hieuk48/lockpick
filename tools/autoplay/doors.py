r"""Door table for the dev-only autoplay harness: every door record in the player's room files (read-only).

    python tools/autoplay/doors.py "G:\GOG Games\Resident Evil" out.json

Each entry is a door opcode's 24-byte record as the game hands it to the room change (the opcode without its first two
bytes): +0x0B entry camera, +0x0C lock, +0x0D destination (stage digit << 5 | room), +0x0E arrival x/y/z/angle (i16),
+0x16 key item. `record_to(...)` picks a real door that leads to a room, so arrivals are spots the game itself uses.
The table is derived from the player's install at run time and kept out of the repository.
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import rdt_index  # noqa: E402

CHARACTERS = {"0": "chris", "1": "jill"}


def build(install: Path) -> list[dict]:
    usa = install / "USA" if (install / "USA").is_dir() else install
    doors = []
    for path in sorted(usa.glob("Stage*/ROOM*.RDT")):
        m = rdt_index.ROOM_FILE.match(path.name)
        if not m or path.stat().st_size < 0x94:
            continue
        stage, room, variant = m.groups()
        for index, (op, b) in enumerate(o for o in rdt_index.opcodes(path.read_bytes()) if o[0] == rdt_index.OP_DOOR):
            record = bytearray(b[2:26])
            dest = record[0x0D]
            dest_stage = dest >> 5 or int(stage)
            x, y, z, angle = struct.unpack_from("<4h", record, 0x0E)
            doors.append({
                "character": CHARACTERS[variant], "from": f"{stage}{room.upper()}", "index": index,
                "to": f"{dest_stage}{dest & 0x1F:02X}", "camera": record[0x0B] & 0x3F, "type": record[0x0A],
                "arrival": [x, y, z, angle], "lock": record[0x0C], "key": record[0x16], "record": record.hex(),
            })
    return doors


def record_to(doors: list[dict], target: str, character: str, via: str | None = None) -> bytes:
    """A door record that enters `target`, with an explicit-stage destination and no lock, ready for `door`."""
    options = [d for d in doors if d["to"] == target.upper() and d["character"] == character
               and (via is None or d["from"] == via.upper())]
    if not options:
        raise KeyError(f"no door leads to {target} for {character}")
    record = bytearray(bytes.fromhex(options[0]["record"]))
    record[0x0D] = (int(target[0]) << 5) | int(target[1:], 16)
    record[0x0C] = 0
    return bytes(record)


def main(argv: list[str]) -> int:
    doors = build(Path(argv[0]))
    Path(argv[1]).write_text(json.dumps(doors, indent=0) + "\n", encoding="utf-8")
    rooms = {(d["to"], d["character"]) for d in doors}
    print(f"{len(doors)} doors; {len({r for r, _ in rooms})} rooms reachable through a door")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))


def items(install: Path) -> list[dict]:
    """Every item pickup zone (item_aot_set: x, z, width, depth at +2; item +0x0A, quantity +0x0B, flag +0x16), with
    the zone's centre — where the player stands to pick the item up."""
    usa = install / "USA" if (install / "USA").is_dir() else install
    found = []
    for path in sorted(usa.glob("Stage*/ROOM*.RDT")):
        m = rdt_index.ROOM_FILE.match(path.name)
        if not m or path.stat().st_size < 0x94:
            continue
        stage, room, variant = m.groups()
        for op, b in rdt_index.opcodes(path.read_bytes()):
            if op == rdt_index.OP_ITEM:
                x, z, w, d = struct.unpack_from("<hhHH", b, 2)
                found.append({"character": CHARACTERS[variant], "room": f"{stage}{room.upper()}", "item": b[0x0A],
                              "quantity": b[0x0B], "flag": b[0x16], "zone": [x, z, w, d],
                              "stand": [x + w // 2, z + d // 2]})
    return found
