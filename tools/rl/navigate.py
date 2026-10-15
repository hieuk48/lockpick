r"""Walks the player to a door the way a player would (dev-only, never shipped): plans a path over the room's
collision (the player's own room files), then steers with the tank controls — turn until facing the next waypoint,
walk or run forward — checking the real position and facing in memory every step. Behaviour cloning on raw buttons
mostly learned "keep pressing what you pressed" (2026-10-14: without the previous buttons as input it was near
chance), so walking is planned; cloning and RL are for what needs the player's timing.

    python -I tools/rl/navigate.py <target-room> [--run] [--seconds 60]

Needs the dev harness running with the player in a room that has a door to the target.
"""
from __future__ import annotations

import heapq
import math
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "autoplay"))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import ap  # noqa: E402
import rdt_map  # noqa: E402
import survey  # noqa: E402

CELL = 250
RADIUS = 400          # keep this far from blocking collision (the player's body)
TURN_UNITS = 4096
# Pad input that turns the player toward increasing / decreasing facing (yaw). Facing (cos θ, −sin θ): increasing
# yaw turns clockwise on a Z-up map. Checked live by `calibrate_turn()` before the first walk.
TURN_PLUS, TURN_MINUS = "LRight", "LLeft"


def quads(room: rdt_map.Room) -> list:
    return [s["quad"] for s in room.switches if s["header"]]


def walkable(room: rdt_map.Room, x: float, z: float) -> bool:
    return any(rdt_map.point_in_quad(int(x), int(z), q) for q in quads(room)) and not room.blocked(int(x), int(z), RADIUS)


def plan(room: rdt_map.Room, start: tuple[int, int], goal: tuple[int, int]) -> list[tuple[int, int]] | None:
    """A* over a CELL grid of walkable points; the goal cell may sit in the door zone (walls around doors)."""
    def cell(p):
        return round(p[0] / CELL), round(p[1] / CELL)

    s, g = cell(start), cell(goal)
    frontier = [(0.0, s)]
    came: dict = {s: None}
    cost = {s: 0.0}
    while frontier:
        _, c = heapq.heappop(frontier)
        if c == g or math.hypot(c[0] - g[0], c[1] - g[1]) <= 2:
            path = []
            while c is not None:
                path.append((c[0] * CELL, c[1] * CELL))
                c = came[c]
            return list(reversed(path)) + [goal]
        for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
            n = (c[0] + dx, c[1] + dz)
            if n not in cost or cost[c] + math.hypot(dx, dz) < cost[n]:
                near_end = math.hypot(n[0] - g[0], n[1] - g[1]) <= 2 or math.hypot(n[0] - s[0], n[1] - s[1]) <= 2
                if not walkable(room, n[0] * CELL, n[1] * CELL) and not near_end:
                    continue
                cost[n] = cost[c] + math.hypot(dx, dz)
                came[n] = c
                heapq.heappush(frontier, (cost[n] + math.hypot(n[0] - g[0], n[1] - g[1]), n))
        if len(cost) > 40000:
            break
    return None


def plan_widest(room: rdt_map.Room, start: tuple[int, int], goal: tuple[int, int]) -> list[tuple[int, int]] | None:
    """The path with the widest clearance that exists: 400, then 300, then 200 units from collision (105's chairs
    leave gaps narrower than 400 around the dining table)."""
    global RADIUS
    for radius in (400, 300, 200):
        RADIUS = radius
        path = plan(room, start, goal)
        if path:
            return path
    return None


def yaw() -> int:
    return int.from_bytes(ap.peek(0xBE6358, 2), "little", signed=True) % TURN_UNITS


def heading_error(x: int, z: int, tx: int, tz: int) -> int:
    """Facing units to turn (positive = increase yaw) to face (tx, tz)."""
    want = ap.angle_to(x, z, tx, tz)
    return (want - yaw() + TURN_UNITS // 2) % TURN_UNITS - TURN_UNITS // 2


def calibrate_turn() -> tuple[str, str]:
    """Which stick direction increases the facing (the game's turn bits are named the other way round in the
    captures' raw word, so this is checked rather than assumed)."""
    before = yaw()
    ap.send("press LRight 300", "wait 200")
    delta = (yaw() - before + TURN_UNITS // 2) % TURN_UNITS - TURN_UNITS // 2
    return ("LRight", "LLeft") if delta > 0 else ("LLeft", "LRight")


def walk_to(target: str, run: bool = False, seconds: float = 60, log=print) -> dict:
    global TURN_PLUS, TURN_MINUS
    character = "jill" if ap.peek(0xBE62E5, 1)[0] & 3 == 1 else "chris"
    room_id = ap.state().get("room")
    room = rdt_map.load(survey.INSTALL, room_id, character)
    door = next((d for d in room.doors if d["to"] == target), None)
    if door is None:
        return {"outcome": f"no door to {target} in {room_id}"}
    gx, gz, gw, gd = door["zone"]
    goal = (int(gx + gw / 2), int(gz + gd / 2))
    p = ap.pos()
    path = plan_widest(room, (p[0], p[2]), goal)
    if path is None:
        return {"outcome": "no path"}
    TURN_PLUS, TURN_MINUS = calibrate_turn()
    log(f"path of {len(path)} points to {target}; turn+ = {TURN_PLUS}")
    start, i, held = time.time(), 1, set()
    try:
        while time.time() - start < seconds:
            if ap.state().get("room") == target:
                return {"outcome": "arrived", "seconds": round(time.time() - start, 1), "path": len(path)}
            if not ap.in_control():
                return {"outcome": "lost control", "state": ap.state()}
            p = ap.pos()
            x, z = p[0], p[2]
            while i < len(path) - 1 and math.hypot(path[i][0] - x, path[i][1] - z) < 500:
                i += 1
            tx, tz = path[i]
            err = heading_error(x, z, tx, tz)
            want = set()
            if abs(err) > 160:
                want.add(TURN_PLUS if err > 0 else TURN_MINUS)
            if abs(err) < 600:
                want.add("LUp")
                if run:
                    want.add("X")
            if i == len(path) - 1 and math.hypot(tx - x, tz - z) < 700 and abs(err) < 300:
                # At the door: press the action key (doors open on A).
                for b in held:
                    ap.send(f"release {b}")
                held = set()
                ap.send("press A 150", "wait 1500")
                continue
            for b in held - want:
                ap.send(f"release {b}")
            for b in want - held:
                ap.send(f"hold {b}")
            held = want
            time.sleep(0.05)
        return {"outcome": "timeout", "pos": ap.pos(), "waypoint": i, "path": len(path)}
    finally:
        for b in held:
            ap.send(f"release {b}", check=False)


if __name__ == "__main__":
    args = sys.argv[1:]
    print(walk_to(args[0].upper(), "--run" in args, float(args[args.index("--seconds") + 1]) if "--seconds" in args else 60))
