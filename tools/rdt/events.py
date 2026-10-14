r"""Event-script reader (RDT +0x68), read-only: what each room event — cutscenes, "go back" scenes, item hand-overs —
does, step by step: which entity it drives, animations and walks, the room-script commands it runs inline (flags,
messages, player moves, items), waits, loops, and which other events it starts.

    python tools/rdt/events.py "G:\GOG Games\Resident Evil" 106 [jill|chris] [--event 4]

The event machine reads the same byte differently in each state (ecruells/resident-evil-pc-decomp
docs/SCD_SCRIPT_SYSTEM.md): state 0 commands, state 1 animation, state 2 movement; 0xF6–0xFF are control flow in every
state. Inline blocks (0x06) and single commands (0x07) are ordinary room-script commands, decoded by scd.py.
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import scd  # noqa: E402

ENTITY = {0: "player", 1: "enemy", 2: "object", 3: "item model"}
# Widths as verified against the original by the decomp's own event disassembler (tools/evt_disasm.py) — its doc table
# differs in places (0x05, 0x83–0x85, 0x88, 0x0B, 0xF8).
STATE0 = {0x00: 1, 0x01: 1, 0x02: 1, 0x03: 1, 0x04: 3, 0x05: 4, 0x08: 2, 0x09: 2}
STATE1 = {0x00: 1, 0x80: 1, 0x8B: 1, 0x82: 1, 0x83: 7, 0x84: 4, 0x85: 4, 0x86: 1, 0x87: 4, 0x88: 4, 0x89: 2, 0x8A: 2}
STATE2 = {0x00: 1, 0x01: 1, 0x02: 1, 0x03: 1, 0x04: 1, 0x05: 4, 0x06: 4, 0x07: 8, 0x08: 3, 0x09: 2, 0x0A: 4, 0x0B: 8}
CONTROL = {0xF6: 1, 0xF7: 1, 0xF8: 4, 0xF9: 3, 0xFA: 4, 0xFB: 1, 0xFD: 1, 0xFE: 1, 0xFF: 1}  # 0xFC: jump by p[1]


def table(data: bytes) -> list[int]:
    base = struct.unpack_from("<I", data, 0x68)[0]
    starts = []
    if not base:
        return starts
    pos = base
    while pos + 4 <= len(data):
        (offset,) = struct.unpack_from("<I", data, pos)
        if offset == 0:
            break
        starts.append(base + offset)
        pos += 4
    return starts


def read_event(data: bytes, start: int, end: int, names: scd.Names, limit: int = 400) -> list[dict]:
    steps: list[dict] = []
    pos, state, entity = start, 0, "player"
    while pos < end and len(steps) < limit:
        op = data[pos]
        if op == 0xFC:
            steps.append({"kind": "control", "text": "call"})
            pos += max(data[pos + 1], 2)
            continue
        if op in CONTROL:
            size = CONTROL[op]
            text = {0xF6: "wait for the message to close", 0xF7: "wait for the message to close",
                    0xF8: f"wait {struct.unpack_from('<h', data, pos + 1)[0] if pos + 3 <= end else '?'} frames",
                    0xF9: "(wait counter)", 0xFA: f"repeat {struct.unpack_from('<h', data, pos + 2)[0]} times:",
                    0xFB: "end repeat", 0xFC: "call", 0xFD: "run the called command", 0xFE: "next frame",
                    0xFF: "end of event"}[op]
            steps.append({"kind": "control", "text": text})
            if op == 0xFF:
                break
            pos += size
            continue
        if state == 0:
            if op in (0x06, 0x07):
                length = data[pos + 1]
                if length < 2 or pos + length > end:
                    steps.append({"kind": "error", "text": f"bad block length at {pos:#x}"})
                    break
                body_end = pos + length
                # Inline blocks (0x06) end with a 1-byte block end, which room-script sizes count as 2; a single
                # command (0x07) is exactly its payload (its last byte may be a 0 operand).
                if op == 0x06 and data[body_end - 1] == 0x00:
                    body_end -= 1
                for st in scd.read_script(data, pos + 2, body_end, names):
                    st["entity"] = entity
                    steps.append(st)
                pos += length
                continue
            size = STATE0.get(op)
            if size is None:
                steps.append({"kind": "error", "text": f"unknown state-0 opcode {op:#04x} at {pos:#x}"})
                break
            if op == 0x01:
                state = 1
            elif op in (0x02, 0x03):
                state = 2
            elif op == 0x04:
                entity = f"{ENTITY.get(data[pos + 1], data[pos + 1])} {data[pos + 2]}" if data[pos + 1] else "player"
                steps.append({"kind": "entity", "text": f"act on {entity}"})
            elif op == 0x05:
                steps.append({"kind": "event", "text": f"start event {data[pos + 2]} in slot {data[pos + 1]}"})
            elif op == 0x08:
                steps.append({"kind": "event", "text": f"continue as event {data[pos + 1]}"})
            elif op == 0x09:
                steps.append({"kind": "event", "text": f"stop the event in slot {data[pos + 1]}"})
            pos += size
        elif state == 1:
            size = (10 if data[pos + 1] & 0x0F else 2) if op == 0x81 else STATE1.get(op)
            if size is None:
                steps.append({"kind": "error", "text": f"unknown state-1 opcode {op:#04x} at {pos:#x}"})
                break
            if op in (0x80, 0x8B):
                state = 0
            elif op in (0x84, 0x85, 0x83):
                steps.append({"kind": "anim", "entity": entity, "text": f"{entity}: play animation"})
            pos += size
        else:
            size = STATE2.get(op)
            if size is None:
                steps.append({"kind": "error", "text": f"unknown state-2 opcode {op:#04x} at {pos:#x}"})
                break
            if op == 0x01:
                state = 0
            elif op == 0x07:
                x, y, z = struct.unpack_from("<3h", data, pos + 2)
                steps.append({"kind": "move", "entity": entity, "text": f"{entity}: placed at ({x},{y},{z})"})
            elif op == 0x0A and data[pos + 1] == 3:
                steps.append({"kind": "health", "entity": entity,
                              "text": f"{entity}: health set to {struct.unpack_from('<h', data, pos + 2)[0]}"})
            pos += size
    return steps


def read_room(path: Path, names: scd.Names) -> list[list[dict]]:
    data = path.read_bytes()
    starts = table(data)
    bounds = sorted(set(starts)) + [len(data)]
    events = []
    for s in starts:
        end = next(b for b in bounds if b > s)
        events.append(read_event(data, s, end, names))
    return events


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install, room = Path(argv[0]), argv[1].upper()
    character = next((a for a in argv if a in ("jill", "chris")), "jill")
    only = int(argv[argv.index("--event") + 1]) if "--event" in argv else None
    usa = install / "USA" if (install / "USA").is_dir() else install
    path = usa / f"Stage{room[0]}" / f"ROOM{room}{'1' if character == 'jill' else '0'}.RDT"
    for i, steps in enumerate(read_room(path, scd.Names())):
        if only is not None and i != only:
            continue
        interesting = [s for s in steps if s["kind"] not in ("control", "anim", "other") or s["text"] == "end of event"]
        print(f"== event {i}: {len(steps)} steps")
        for s in interesting[:40]:
            guard = f"  [if {' / '.join(s['guards'])}]" if s.get("guards") else ""
            print(f"   {s['text']}{guard}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
