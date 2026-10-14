r"""Replays Lockpick capture zips (dev-only RL tooling): the memory log (memory-N.bin, docs: RegionLog.cs) frame by
frame, with the fields behaviour cloning needs decoded from the gameState region — the player's state, the enemies
around them, and the game's own input words (what the player pressed).

    python -I tools/rl/captures.py <capture.zip> [...]     print a per-capture summary (frames, rate, rooms, inputs)

Captures are the owner's own recordings (%LOCALAPPDATA%\Lockpick\captures); they are read in memory, never extracted.
"""
from __future__ import annotations

import struct
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

GAME_STATE = 0xBE41C0  # capture region "gameState" (0x587C bytes)

# Addresses (docs/MEMORY_MAP.md; enemy slot layout verified live 2026-10-14 by the dev combat tests).
STATE_FLAGS, PLAYER = 0xBE41C0, 0xBE62E4
STAGE, ROOM, CAMERA = 0xBE9820, 0xBE9821, 0xBE9822
INPUT_HELD, INPUT_PRESSED = 0xBE9840, 0xBE9842  # the game's remapped input words (decomp; checked by inputs.py)
ENEMIES, ENEMY_SIZE, ENEMY_SLOTS = 0xBE6464, 0x18C, 30
CHARACTER, HP, MAX_HP = 0xBE62E5, 0xBE636C, 0xBE6459


@dataclass
class Frame:
    at_ms: int
    room: str
    camera: int
    character: int
    x: int
    y: int
    z: int
    yaw: int
    hp: int
    max_hp: int
    flags: int
    held: int
    pressed: int
    enemies: list[tuple[int, int, int, int]]  # (type, x, z, hp) of active, living enemies


def _frames(payload: bytes) -> Iterator[tuple[int, dict[str, bytearray]]]:
    view = memoryview(payload)
    if bytes(view[:6]) != b"LPKMEM" or view[6] != 1:
        raise ValueError("not a Lockpick memory log")
    pos = 7
    (count,) = struct.unpack_from("<H", view, pos)
    pos += 2
    regions = []
    for _ in range(count):
        length = view[pos]
        name = bytes(view[pos + 1:pos + 1 + length]).decode("utf-8", "replace")
        pos += 1 + length
        address, size = struct.unpack_from("<QI", view, pos)
        pos += 12
        regions.append((name, address, size))
    state = {name: bytearray(size) for name, _, size in regions}
    names = [name for name, _, _ in regions]
    while pos < len(view):
        kind = view[pos]
        (at_ms,) = struct.unpack_from("<q", view, pos + 1)
        pos += 9
        if kind == 0:
            for name, _, size in regions:
                present = view[pos]
                pos += 1
                if present:
                    state[name][:] = view[pos:pos + size]
                    pos += size
        elif kind == 1:
            (runs,) = struct.unpack_from("<H", view, pos)
            pos += 2
            for _ in range(runs):
                index, offset, length = struct.unpack_from("<HIH", view, pos)
                pos += 8
                state[names[index]][offset:offset + length] = view[pos:pos + length]
                pos += length
        else:
            raise ValueError(f"unknown record kind {kind}")
        yield at_ms, state


def frames(zip_path: Path) -> Iterator[Frame]:
    """Every recorded poll of one capture, decoded (the region log only stores polls where something changed)."""
    with zipfile.ZipFile(zip_path) as archive:
        members = sorted(n for n in archive.namelist() if n.startswith("memory-") and n.endswith(".bin"))
        for member in members:
            for at_ms, state in _frames(archive.read(member)):
                g = state.get("gameState")
                if g is None:
                    continue

                def u8(a: int) -> int:
                    return g[a - GAME_STATE]

                def i16(a: int) -> int:
                    return struct.unpack_from("<h", g, a - GAME_STATE)[0]

                def u16(a: int) -> int:
                    return struct.unpack_from("<H", g, a - GAME_STATE)[0]

                def i32(a: int) -> int:
                    return struct.unpack_from("<i", g, a - GAME_STATE)[0]

                enemies = []
                for i in range(ENEMY_SLOTS):
                    base = ENEMIES + i * ENEMY_SIZE
                    if u8(base) & 1 and (u8(base + 1) >= 0x20 or i16(base + 0x88) > 0):
                        enemies.append((u8(base + 1), i32(base + 0x34), i32(base + 0x3C), i16(base + 0x88)))
                yield Frame(at_ms, f"{u8(STAGE) + 1}{u8(ROOM):02X}", u8(CAMERA), u8(CHARACTER) & 3,
                            i32(PLAYER + 0x34), i32(PLAYER + 0x38), i32(PLAYER + 0x3C), i16(PLAYER + 0x74),
                            i16(HP), u8(MAX_HP), struct.unpack_from("<I", g, 0)[0], u16(INPUT_HELD),
                            u16(INPUT_PRESSED), enemies)


def summary(zip_path: Path) -> dict:
    count, first, last, rooms, held_bits, gaps = 0, None, None, set(), 0, []
    previous = None
    for f in frames(zip_path):
        count += 1
        first = f.at_ms if first is None else first
        if previous is not None:
            gaps.append(f.at_ms - previous)
        previous = last = f.at_ms
        rooms.add(f.room)
        held_bits |= f.held
    gaps.sort()
    return {"capture": zip_path.name, "frames": count, "minutes": round(((last or 0) - (first or 0)) / 60000, 1),
            "medianGapMs": gaps[len(gaps) // 2] if gaps else None, "rooms": len(rooms), "heldBitsSeen": hex(held_bits)}


if __name__ == "__main__":
    for arg in sys.argv[1:]:
        print(summary(Path(arg)))
