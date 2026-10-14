r"""Behaviour-cloning dataset from the owner's captures (dev-only RL tooling): one row per recorded frame in control,
state → the buttons the player held.

State (player frame: +forward, +left): the goal door — the door the player actually went through next (hindsight from
the trajectory) — as distance and direction; clearance to walls and furniture along 8 rays (the room's collision from the
player's own room files); the 3 nearest living enemies (direction, distance, type, HP); HP fraction; the room's camera.
Action, decoded from the game's held-input word (bits measured by inputs.py from the same captures and checked live
2026-10-14 — holding RT reads 0x0500): move (none, forward 0x11, back 0x24), turn (none, left 0x02, right 0x08), run
(0x8200), aim (0x0500, RT), action/fire (0x00C0, A; with aim held it fires). The previous frame's action is part of
the state (players hold a direction for many frames).

    "%LOCALAPPDATA%\Lockpick\research\.venv\Scripts\python.exe" tools/rl/dataset.py <out.npz> <capture.zip> [...]
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import captures  # noqa: E402
import rdt_map  # noqa: E402

INSTALL = Path(r"G:\GOG Games\Resident Evil")
RAYS = 8
RAY_MAX = 6000
ENEMIES = 3
BUSY = 0x80 | 0xFF00 | 0x10000 | 0x20000 | 0x40000 | 0x100000 | 0x01000000 | 0x04000000 | 0x20000000

MOVE = {0: "none", 1: "forward", 2: "back"}
TURN = {0: "none", 1: "left", 2: "right"}


def decode(held: int) -> tuple[int, int, int, int, int]:
    move = 1 if held & 0x01 else 2 if held & 0x04 else 0
    turn = 1 if held & 0x02 else 2 if held & 0x08 else 0
    return move, turn, int(bool(held & 0x0200)), int(bool(held & 0x0100)), int(bool(held & 0x0040))


def encode(move: int, turn: int, run: int, aim: int, act: int) -> list[str]:
    """Pad inputs for an action (the dev harness's names: LUp/LDown/LLeft/LRight, X run, RT aim, A action)."""
    pad = []
    pad += ["LUp"] if move == 1 else ["LDown"] if move == 2 else []
    pad += ["LLeft"] if turn == 1 else ["LRight"] if turn == 2 else []
    pad += ["X"] if run else []
    pad += ["RT"] if aim else []
    pad += ["A"] if act else []
    return pad


_rooms: dict[tuple[str, int], rdt_map.Room | None] = {}


def room_map(room: str, character: int) -> rdt_map.Room | None:
    key = (room, character)
    if key not in _rooms:
        try:
            _rooms[key] = rdt_map.load(INSTALL, room, "jill" if character == 1 else "chris")
        except Exception:  # noqa: BLE001 - stub or unreadable rooms have no map
            _rooms[key] = None
    return _rooms[key]


def rays(room: rdt_map.Room, x: int, z: int, yaw: int) -> list[float]:
    """Clearance along 8 directions around the facing, 0..1 of RAY_MAX (ray-marched against blocking collision)."""
    out = []
    for i in range(RAYS):
        theta = (yaw / 4096 + i / RAYS) * 2 * math.pi
        dx, dz = math.cos(theta), -math.sin(theta)
        d = 0
        while d < RAY_MAX and not room.blocked(int(x + dx * d), int(z + dz * d), 200):
            d += 250
        out.append(d / RAY_MAX)
    return out


def local(x: int, z: int, yaw: int, tx: float, tz: float) -> tuple[float, float, float]:
    """(distance/10000, cos, sin) of the target relative to the facing (+sin = to the player's left)."""
    theta = yaw * 2 * math.pi / 4096
    fx, fz = math.cos(theta), -math.sin(theta)
    dx, dz = tx - x, tz - z
    ahead, left = dx * fx + dz * fz, dx * -fz + dz * fx
    dist = math.hypot(dx, dz)
    return (dist / 10000, ahead / dist, left / dist) if dist else (0.0, 1.0, 0.0)


def rows(path: Path) -> tuple[list[list[float]], list[tuple[int, ...]], list[str]]:
    frames = list(captures.frames(path))
    # Hindsight goal: for every frame, the room the player entered next.
    next_room = [None] * len(frames)
    upcoming = None
    for i in range(len(frames) - 1, -1, -1):
        if i + 1 < len(frames) and frames[i + 1].room != frames[i].room:
            upcoming = frames[i + 1].room
        next_room[i] = upcoming
    X, Y, rooms = [], [], []
    previous = (0, 0, 0, 0, 0)
    for f, goal in zip(frames, next_room):
        last, previous = previous, decode(f.held)
        if f.flags & BUSY or f.hp <= 0 or goal is None:
            continue
        plan = room_map(f.room, f.character)
        if plan is None:
            continue
        doors = [d for d in plan.doors if d["to"] == goal]
        if not doors:
            continue
        gx, gz, gw, gd = doors[0]["zone"]
        features = list(local(f.x, f.z, f.yaw, gx + gw / 2, gz + gd / 2))
        features += rays(plan, f.x, f.z, f.yaw)
        near = sorted(f.enemies, key=lambda e: math.hypot(e[1] - f.x, e[2] - f.z))[:ENEMIES]
        for k in range(ENEMIES):
            if k < len(near):
                t, ex, ez, hp = near[k]
                features += [1.0, *local(f.x, f.z, f.yaw, ex, ez), t / 64, min(hp, 300) / 300]
            else:
                features += [0.0, 0.0, 0.0, 0.0, 0.0, 0.0]
        features += [f.hp / max(f.max_hp, 1), f.camera / 16]
        features += [float(last[0] == 1), float(last[0] == 2), float(last[1] == 1), float(last[1] == 2), *map(float, last[2:])]
        X.append(features)
        Y.append(decode(f.held))
        rooms.append(f.room)
    return X, Y, rooms


def main(argv: list[str]) -> int:
    out, paths = Path(argv[0]), [Path(a) for a in argv[1:]]
    X, Y, R, S = [], [], [], []
    for path in paths:
        x, y, r = rows(path)
        print(f"{path.name}: {len(x)} rows")
        X += x
        Y += y
        R += r
        S += [path.name] * len(x)
    np.savez_compressed(out, X=np.array(X, dtype=np.float32), Y=np.array(Y, dtype=np.int64), rooms=np.array(R),
                        sources=np.array(S))
    counts = np.array(Y)
    print(f"{len(X)} rows, {len(X[0]) if X else 0} features; move {np.bincount(counts[:, 0], minlength=3)} turn "
          f"{np.bincount(counts[:, 1], minlength=3)} run {counts[:, 2].mean():.2f} aim {counts[:, 3].mean():.2f} "
          f"act {counts[:, 4].mean():.2f}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
