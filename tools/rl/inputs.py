r"""What each bit of the game's held-input word does (dev-only RL tooling), measured from the owner's captures: for
each bit, the player's speed along their facing, sideways, and the turn rate while it is held alone or with others.
That identifies forward/back/turn/run/aim from the data and gives the movement speeds a navigation policy needs.

    python -I tools/rl/inputs.py <capture.zip> [...]
"""
from __future__ import annotations

import math
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import captures  # noqa: E402

TURN = 4096  # facing units per full turn


def analyse(paths: list[Path]) -> dict:
    stats: dict[int, dict[str, list[float]]] = defaultdict(lambda: defaultdict(list))
    combos: dict[int, int] = defaultdict(int)
    for path in paths:
        previous = None
        for f in captures.frames(path):
            if previous is not None and f.room == previous.room and 0 < f.at_ms - previous.at_ms <= 250 \
                    and previous.flags & 0xFF00 == 0 and previous.hp > 0:
                dt = (f.at_ms - previous.at_ms) / 1000
                theta = previous.yaw * 2 * math.pi / TURN
                fx, fz = math.cos(theta), -math.sin(theta)  # facing (cos θ, −sin θ)
                dx, dz = f.x - previous.x, f.z - previous.z
                forward = (dx * fx + dz * fz) / dt
                side = (dx * -fz + dz * fx) / dt
                turn = ((f.yaw - previous.yaw + TURN // 2) % TURN - TURN // 2) / dt
                held = previous.held
                combos[held] += 1
                for bit in range(16):
                    key = 1 << bit
                    if held & key:
                        stats[key]["forward"].append(forward)
                        stats[key]["side"].append(side)
                        stats[key]["turn"].append(turn)
                        if held == key:
                            stats[key]["alone"].append(forward)
                            stats[key]["aloneTurn"].append(turn)
                if held == 0:
                    stats[0]["forward"].append(forward)
                    stats[0]["turn"].append(turn)
            previous = f

    def median(values: list[float]) -> float | None:
        return round(sorted(values)[len(values) // 2], 1) if values else None

    out = {}
    for key, s in sorted(stats.items()):
        out[hex(key)] = {"frames": len(s["forward"]), "forwardSpeed": median(s["forward"]),
                         "turnRate": median(s["turn"]), "aloneFrames": len(s["alone"]),
                         "aloneForward": median(s["alone"]), "aloneTurn": median(s["aloneTurn"])}
    out["commonCombos"] = {hex(k): v for k, v in sorted(combos.items(), key=lambda kv: -kv[1])[:15]}
    return out


if __name__ == "__main__":
    import json
    print(json.dumps(analyse([Path(a) for a in sys.argv[1:]]), indent=1))
