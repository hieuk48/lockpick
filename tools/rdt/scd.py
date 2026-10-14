r"""Room-script reader (RDT init/main SCD), read-only: lists what each room's scripts do — items placed, doors, room
actions, events started, flags set, messages, cutscene cuts, player moved — each with the conditions that guard it
(the if/else nesting), so "why isn't this item there", "when does this door send me back" and "what does this key
spot want" can be answered from the player's own files.

    python tools/rdt/scd.py "G:\GOG Games\Resident Evil" 105 [jill|chris] [--all]     # one room, readable listing
    python tools/rdt/scd.py "G:\GOG Games\Resident Evil" --json out.json [jill|chris]  # every room, structured

Operand layouts: ecruells/resident-evil-pc-decomp docs/SCD_COMMAND_OPCODES.md (facts checked against the files);
opcode sizes from rdt_index (BioRand). Conditions: a `bit_test` passes when the bit's state matches `expect`
(0 = set, 1 = clear); every condition after an `if` must pass for its body to run; `else` runs when they don't.
Event scripts (RDT +0x68, a separate state machine) are listed by index only.
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import rdt_index  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "data" / "games" / "re1"
BANKS = {0: "flagScenarioA", 1: "flagScenarioB", 2: "flagLocks", 3: "flagEnemies", 4: "sys", 5: "stateFlags",
         6: "messageFlags", 7: "flagRoomItems", 8: "flagRoom", 9: "itemUse"}
HANDLERS = {0: "none", 1: "door", 2: "message", 3: "key prompt", 4: "item pickup", 5: "door (x side)", 6: "door (z side)",
            7: "flag set", 8: "item box", 9: "event", 10: "noop", 11: "effect", 12: "typewriter?", 14: "?", 16: "?"}
COMPARE = {0: "==", 1: ">", 2: ">=", 3: "<", 4: "<=", 5: "!="}
CONDITIONS = {0x04, 0x06, 0x07, 0x10, 0x11, 0x1A, 0x1D, 0x22, 0x2C, 0x36, 0x3C, 0x3F, 0x38, 0x50}


class Names:
    def __init__(self) -> None:
        names = json.loads((DATA / "names.json").read_text(encoding="utf-8"))
        self.items = names["items"]
        self.enemies = names["enemies"]
        self.labels = {}
        for l in json.loads((DATA / "flag-labels.json").read_text(encoding="utf-8"))["labels"]:
            base = l["bank"].rstrip("0123456789")
            word = int(l["bank"][len(base):] or 0)
            self.labels[(base, word * 32 + 31 - (int(l["mask"], 16).bit_length() - 1))] = l["label"]

    def item(self, i: int) -> str:
        return self.items.get(str(i), f"item {i}")

    def flag(self, bank: int, flag: int) -> str:
        name = BANKS.get(bank, f"bank{bank}")
        label = self.labels.get((name, flag))
        return f"{name}[{flag}]" + (f" ({label})" if label else "")


def describe(op: int, b: bytes, names: Names) -> dict:
    """One command as {kind, text, …}; kind groups what the tools look for."""
    if op == 0x04:
        return {"kind": "cond", "bank": b[1], "flag": b[2], "set": b[3] == 0,
                "text": f"{names.flag(b[1], b[2])} is {'set' if b[3] == 0 else 'clear'}"}
    if op == 0x05:
        mode = {0: "set", 1: "clear", 2: "toggle"}.get(b[3], f"mode {b[3]}")
        return {"kind": "flag", "bank": b[1], "flag": b[2], "mode": mode, "text": f"{mode} {names.flag(b[1], b[2])}"}
    if op == 0x06:
        return {"kind": "cond", "text": f"state byte {b[1]} {COMPARE.get(b[2], '?')} {b[3]}"}
    if op == 0x07:
        return {"kind": "cond", "text": f"state word {b[2]} {COMPARE.get(b[3], '?')} {struct.unpack_from('<H', b, 4)[0]}"}
    if op == 0x08:
        return {"kind": "state", "text": f"state byte {b[1]} = {b[2]}"}
    if op == 0x09:
        return {"kind": "camera", "text": f"camera locked to {b[1]}"}
    if op == 0x0B:
        return {"kind": "message", "message": b[1], "text": f"show message {b[1]}"}
    if op == 0x0C:
        dest = b[0x0F]
        return {"kind": "door", "to": dest, "lock": b[0x0E], "key": b[0x18],
                "text": f"door to {(dest >> 5) or '·'}{dest & 0x1F:02X}" + (f" (lock {b[0x0E]:#x}, key {names.item(b[0x18])})"
                                                                         if b[0x0E] & 0x80 and b[0x0E] != 0xFF else "")}
    if op == 0x0D:
        x, z, w, d = struct.unpack_from("<4H", b, 2)
        h = b[0x0A]
        words = struct.unpack_from("<3H", b, 0x0C)
        return {"kind": "action", "handler": h, "zone": [x, z, w, d], "words": list(words), "slot": b[1],
                "text": f"room action {HANDLERS.get(h, h)} at ({x},{z},{w}x{d}) words {words}"}
    if op in (0x10, 0x11, 0x1A, 0x1D):
        what = {0x10: "item used is", 0x11: "item picked up is", 0x1A: "inventory has", 0x1D: "equipped item is"}[op]
        return {"kind": "cond", "item": b[1], "text": f"{what} {names.item(b[1])}"}
    if op == 0x2C:
        return {"kind": "cond", "item": b[1], "text": f"remove {names.item(b[1])} from the inventory (if held)"}
    if op == 0x14:
        return {"kind": "event", "script": b[3], "slot": b[2], "text": f"start event script {b[3]} (slot {b[2]})"}
    if op == 0x18:
        x, z, w, d = struct.unpack_from("<hhHH", b, 2)
        return {"kind": "item", "item": b[0x0A], "quantity": b[0x0B], "flag": b[0x16], "zone": [x, z, w, d],
                "text": f"place {names.item(b[0x0A])} ×{b[0x0B]} (item flag {b[0x16]}) at ({x},{z},{w}x{d})"}
    if op == 0x1B:
        guard = "" if b[3] == 255 else f", gone once flagEnemies[{b[3]}] is set"
        return {"kind": "enemy", "type": b[1], "text": f"spawn {names.enemies.get(str(b[1]), b[1])}{guard}"}
    if op == 0x1F:
        x, y, z = struct.unpack_from("<3h", b, 4)
        params = struct.unpack_from("<8H", b, 0x0C)
        return {"kind": "object", "slot": b[1], "pos": [x, y, z], "params": list(params),
                "text": f"object {b[1]} at ({x},{y},{z}) params {params}"}
    if op == 0x30:
        z, w, x, y = struct.unpack_from("<4H", b, 4)
        return {"kind": "boundary", "list": b[1], "index": b[2], "text": f"collision record {b[1]}/{b[2]} set to {x},{y},{z},{w}"}
    if op == 0x20:
        x, y, z = struct.unpack_from("<3h", b, 8)
        return {"kind": "player", "text": f"move player to ({x},{y},{z}) facing {struct.unpack_from('<h', b, 4)[0]}"}
    if op == 0x29:
        return {"kind": "fmv", "text": f"play FMV {b[1]}"}
    if op == 0x2D:
        return {"kind": "gotitem", "text": f"hand over the item of room action {b[1]}"}
    if op == 0x3C:
        return {"kind": "cond", "text": f"player within {struct.unpack_from('<H', b, 4)[0]} of target {b[2]}"}
    if op == 0x3F:
        return {"kind": "cond", "text": f"player facing {struct.unpack_from('<H', b, 2)[0]}–{struct.unpack_from('<H', b, 4)[0]}"}
    return {"kind": "other", "text": f"op {op:#04x} {b[1:].hex()}"}


def read_script(data: bytes, start: int, end: int, names: Names) -> list[dict]:
    """Statements of one script block with their guards (list of condition texts, negated in else branches)."""
    out: list[dict] = []
    frames: list[dict] = []  # {"conds": [...], "end": addr, "negated": bool, "collecting": bool}
    pos = start
    while pos < end:
        while frames and pos >= frames[-1]["end"]:
            frames.pop()
        op = data[pos]
        size = rdt_index.SIZES[op] if op < len(rdt_index.SIZES) else 0
        if size == 0 or pos + size > end:
            out.append({"kind": "error", "text": f"unknown opcode {op:#04x} at {pos:#x}", "guards": []})
            break
        b = data[pos:pos + size]
        if op == 0x01:
            frames.append({"conds": [], "end": pos + 2 + b[1], "negated": False, "collecting": True})
        elif op == 0x02 and frames:
            f = frames[-1]
            f.update(negated=True, collecting=False, end=pos + b[1])
        elif op == 0x03 and frames:
            frames.pop()
        elif op == 0x00:
            pass
        else:
            d = describe(op, b, names)
            if d["kind"] == "cond" and frames and frames[-1]["collecting"]:
                frames[-1]["conds"].append(d)
            else:
                if frames:
                    frames[-1]["collecting"] = False
                guards = []
                for f in frames:
                    text = " and ".join(c["text"] for c in f["conds"]) or "(no condition)"
                    guards.append(("NOT (" + text + ")") if f["negated"] else text)
                d["guards"] = guards
                d["at"] = pos
                out.append(d)
        pos += size
    return out


def read_room(path: Path, names: Names) -> dict:
    data = path.read_bytes()
    result = {}
    for label, index in (("init", 6), ("main", 7)):
        ptr = struct.unpack_from("<19I", data, 0x48)[index]
        statements = []
        while (length := struct.unpack_from("<H", data, ptr)[0]) != 0:
            statements += read_script(data, ptr + 2, ptr + length, names)
            ptr += length
        result[label] = statements
    return result


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install = Path(argv[0])
    character = next((a for a in argv if a in ("jill", "chris")), "jill")
    variant = "1" if character == "jill" else "0"
    usa = install / "USA" if (install / "USA").is_dir() else install
    names = Names()
    if "--json" in argv:
        out = {}
        for path in sorted(usa.glob(f"Stage*/ROOM*{variant}.RDT")):
            if path.stat().st_size >= 0x94:
                out[path.name[4:7].upper()] = read_room(path, names)
        Path(argv[argv.index("--json") + 1]).write_text(json.dumps(out, ensure_ascii=False, indent=0) + "\n", encoding="utf-8")
        print(f"wrote {len(out)} rooms")
        return 0
    room = argv[1].upper()
    listing = read_room(usa / f"Stage{room[0]}" / f"ROOM{room}{variant}.RDT", names)
    for label, statements in listing.items():
        print(f"== {label} script")
        for s in statements:
            if s["kind"] == "other" and "--all" not in argv:
                continue
            guard = "  if " + " / ".join(s["guards"]) + ":" if s["guards"] else ""
            print(f"{guard}\n      {s['text']}" if guard else f"  {s['text']}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
