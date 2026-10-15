r"""Spawn or swap an enemy in the current room (dev harness only, never shipped): the room is reloaded with one extra
enemy_set run right after its own init script (RoomScriptHook.cs), so the game loads that enemy's model, animations
and AI the way it does for enemies that belong there. The record is built from a real enemy_set of that type in the
player's room files (its behaviour and collision values), placed where asked, with no kill flag.

    python -I tools/autoplay/spawn.py <type> [x z] [--slot 2] [--hold]

Enemy sounds come from a fixed per-room table, so a type that never appears in the room may be silent.
"""
from __future__ import annotations

import struct
import sys
import time
from functools import lru_cache
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import ap  # noqa: E402
import doors  # noqa: E402
import rdt_map  # noqa: E402
import scd  # noqa: E402
import survey  # noqa: E402

# A do-nothing entry in each type's state table (decomp): the enemy stays drawn and hittable but doesn't act. Types
# without one (crows, wasps and snakes have none: their tables are init/run/damaged/death) are pinned instead.
HOLD_STATE = {0: 4, 1: 4, 17: 4, 2: 4}


def hold_command(kind: int, slot: int) -> str:
    """The harness command that arms `slot` to be held from its next set-up's first frames (returns at once, so every
    slot of one room load is caught)."""
    return f"enemy-arm {slot} {HOLD_STATE[kind] if kind in HOLD_STATE else 'pin'}"


@lru_cache(maxsize=1)
def templates() -> dict[int, bytes]:
    """One real enemy_set per type from the install's room files: the first that is neither dormant (behaviour bit
    0x80, scene-held) nor parked off the map."""
    usa = survey.INSTALL / "USA" if (survey.INSTALL / "USA").is_dir() else survey.INSTALL
    names, found = scd.Names(), {}
    for path in sorted(usa.glob("Stage*/ROOM*.RDT")):
        data = path.read_bytes()
        if len(data) < 0x94:
            continue
        ptr = struct.unpack_from("<19I", data, 0x48)[6]
        while (length := struct.unpack_from("<H", data, ptr)[0]) != 0:
            for s in scd.read_script(data, ptr + 2, ptr + length, names):
                if s.get("kind") == "enemy":
                    b = data[s["at"]:s["at"] + 0x16]
                    x = struct.unpack_from("<h", b, 0x0C)[0]
                    normal = not b[2] & 0x80 and abs(x) < 30000
                    if b[1] not in found or (normal and not found[b[1]][1]):
                        found[b[1]] = (b, normal)
            ptr += length
    return {t: b for t, (b, _) in found.items()}


def enemy_set(kind: int, slot: int, x: int, z: int, angle: int, kill_flag: int = 0xFF) -> bytes:
    """kill_flag: flagEnemies index the enemy sets when it dies and that keeps it from being placed again (0xFF: none,
    always placed)."""
    b = bytearray(templates()[kind])
    b[3] = kill_flag
    b[4] = 1                         # always initialised
    struct.pack_into("<H", b, 0x08, angle & 0xFFFF)
    struct.pack_into("<h", b, 0x0C, x)
    struct.pack_into("<h", b, 0x10, z)
    b[0x12] = (b[0x12] & 0xF0) | slot
    return bytes(b)


def script(*records: bytes) -> bytes:
    body = b"".join(records) + b"\x00\x00"
    return struct.pack("<H", len(body) + 2) + body + b"\x00\x00"


def reload_room(log=print, then: tuple[str, ...] = ()) -> None:
    """Re-enters the current room at the player's spot and facing (a door record into it, arrival rewritten); `then`
    are harness commands sent with the door, run as soon as it is handed over (e.g. enemy-hold)."""
    room, character = ap.state()["room"], ap.state()["char"].lower()
    x, _, z = ap.pos()
    yaw = int.from_bytes(ap.peek(0xBE6358, 2), "little", signed=True)
    record = bytearray(doors.record_to(doors.build(survey.INSTALL), room, character))
    struct.pack_into("<4h", record, 0x0E, x, 0, z, yaw)
    record[0x0B], record[0x0C] = ap.peek(0xBE9822, 1)[0], 0
    ap.send("door " + record.hex(), *then)
    ap.wait_for(lambda: ap.flags() & 0x04000000, 5)
    end, since = time.time() + 30, None
    while time.time() < end:
        ready = ap.state().get("room") == room and ap.in_control()
        since = (since or time.time()) if ready else None
        if since and time.time() - since >= 1.0:
            # The arrival keeps the door's entry camera; cut to the one the room's zones give the player's spot.
            camera = rdt_map.load(survey.INSTALL, room, character).camera_for(x, z)
            if camera is not None and camera != ap.peek(0xBE9822, 1)[0]:
                ap.send(f"camera {camera}", "wait 300")
            return
        time.sleep(0.2)
    raise ap.Failed(f"no control after reloading {room}: {ap.state()}")


def spawn(kind: int, x: int | None = None, z: int | None = None, slot: int = 2, hold: bool = False, log=print,
          kill_flag: int = 0xFF, extra: tuple[str, ...] = ()) -> dict:
    """Reloads the room with `kind` added in `slot` at (x, z) facing the player (default: 2000 units in front)."""
    p = ap.pos()
    if x is None or z is None:
        import math
        yaw = int.from_bytes(ap.peek(0xBE6358, 2), "little", signed=True)
        theta = yaw * 2 * math.pi / 4096
        x, z = int(p[0] + math.cos(theta) * 2000), int(p[2] - math.sin(theta) * 2000)
    record = enemy_set(kind, slot, x, z, ap.angle_to(x, z, p[0], p[2]), kill_flag)
    holding = hold
    # Armed before the door: the harness catches each enemy's first frames, before it can move.
    ap.send(f"enemy-pin {slot} off", *((hold_command(kind, slot),) if holding else ()), *extra,
            "room-script " + script(record).hex())
    try:
        reload_room(log)
    finally:
        ap.send("room-script off", check=False)
    enemy = next((e for e in ap.enemies() if e["slot"] == slot), None)
    log(f"  spawned {scd.Names().enemies.get(str(kind), kind)} in slot {slot} at ({x}, {z}): {enemy}")
    return {"type": kind, "slot": slot, "at": [x, z], "enemy": enemy, "held": holding}


if __name__ == "__main__":
    args = sys.argv[1:]
    slot = int(args[args.index("--slot") + 1]) if "--slot" in args else 2
    nums = [int(a) for a in args if a.lstrip("-").isdigit() and a != str(slot)]
    print(spawn(nums[0], *(nums[1:3] if len(nums) >= 3 else (None, None)), slot=slot, hold="--hold" in args))
