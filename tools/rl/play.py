r"""Runs a behaviour-cloning policy in the game (dev-only RL tooling, never shipped): about 10 times a second it reads
the same state the dataset uses from memory (room, position, facing, HP, enemies, the room's collision), asks the
policy which buttons to hold, and holds them on the virtual pad. The goal is a door: the policy should walk the player
to it. Stops on arrival in the target room, on a timeout, or when the player loses control (message, cutscene, death).

    "%LOCALAPPDATA%\Lockpick\research\.venv\Scripts\python.exe" tools/rl/play.py <model.pt> <target-room> [seconds]

Needs the dev harness running (tools/autoplay ap.serve()) with the player in a room that has a door to the target.
"""
from __future__ import annotations

import json
import math
import sys
import time
from pathlib import Path

import torch

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "autoplay"))
import ap  # noqa: E402
import dataset  # noqa: E402
import train_bc  # noqa: E402


def state(target: str, last: tuple[int, ...]) -> tuple[list[float] | None, dict]:
    s = ap.state()
    room, pos = s.get("room"), ap.pos()
    character = ap.peek(0xBE62E5, 1)[0] & 3
    plan = dataset.room_map(room, character) if room else None
    if plan is None or pos is None:
        return None, s
    doors = [d for d in plan.doors if d["to"] == target]
    if not doors:
        return None, s
    x, z = pos[0], pos[2]
    yaw = int.from_bytes(ap.peek(0xBE6358, 2), "little", signed=True)
    gx, gz, gw, gd = doors[0]["zone"]
    features = list(dataset.local(x, z, yaw, gx + gw / 2, gz + gd / 2))
    features += dataset.rays(plan, x, z, yaw)
    near = sorted(ap.enemies(), key=lambda e: math.hypot(e["x"] - x, e["z"] - z))
    near = [e for e in near if e["hp"] > 0 or e["type"] >= 0x20][:dataset.ENEMIES]
    for k in range(dataset.ENEMIES):
        if k < len(near):
            e = near[k]
            features += [1.0, *dataset.local(x, z, yaw, e["x"], e["z"]), e["type"] / 64, min(e["hp"], 300) / 300]
        else:
            features += [0.0] * 6
    hp, top = ap.hp(), ap.peek(0xBE6459, 1)[0]
    features += [hp / max(top, 1), ap.peek(0xBE9822, 1)[0] / 16]
    features += [float(last[0] == 1), float(last[0] == 2), float(last[1] == 1), float(last[1] == 2), *map(float, last[2:])]
    return features, s


def main(argv: list[str]) -> int:
    model_path, target = Path(argv[0]), argv[1].upper()
    seconds = float(argv[2]) if len(argv) > 2 else 60
    saved = torch.load(model_path, weights_only=False)
    policy = train_bc.Policy(saved["features"])
    policy.load_state_dict(saved["state"])
    policy.eval()
    mean, std = saved["mean"], saved["std"]
    held: set[str] = set()
    last = (0, 0, 0, 0, 0)
    log, start = [], time.time()
    try:
        while time.time() - start < seconds:
            if not ap.in_control():
                outcome = "lost control"
                break
            features, s = state(target, last)
            if s.get("room") == target:
                outcome = "arrived"
                break
            if features is None:
                outcome = f"no door to {target} from {s.get('room')}"
                break
            with torch.no_grad():
                logits = policy((torch.tensor([features]) - mean) / std)
            action = tuple(int(l.argmax(1)) for l in logits)
            want = set(dataset.encode(*action)) - {"A"}  # the action key stays with the test (doors, items)
            for button in held - want:
                ap.send(f"release {button}")
            for button in want - held:
                ap.send(f"hold {button}")
            held, last = want, action
            log.append({"t": round(time.time() - start, 2), "action": action, "pos": ap.pos()})
            time.sleep(0.08)
        else:
            outcome = "timeout"
    finally:
        for button in held:
            ap.send(f"release {button}", check=False)
    print(json.dumps({"outcome": outcome, "seconds": round(time.time() - start, 1), "steps": len(log),
                      "end": ap.state()}, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
