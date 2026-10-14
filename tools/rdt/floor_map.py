r"""Whole-floor maps: places every room of a stage relative to the others from its door links, then draws all rooms'
collision on one sheet (output stays local).

    python tools/rdt/floor_map.py "G:\GOG Games\Resident Evil" <out.html> [jill|chris] [--stages 1,2]

Each door gives one link: its zone centre in room A and its arrival spot in room B are (about) the same place, so B's
origin sits at zone − arrival in A's coordinates. Rooms share one orientation (2026-10-14: stage 1's 26 loop checks
agreed to a median of 750 units — the door step); a least-squares fit over all links spreads the remaining error.
"""
from __future__ import annotations

import html
import json
import sys
from collections import defaultdict, deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import rdt_map  # noqa: E402

DATA = Path(__file__).resolve().parents[2] / "data" / "games" / "re1"


def links(rooms: dict[str, rdt_map.Room]) -> list[tuple[str, str, float, float]]:
    found = []
    for a, ra in rooms.items():
        for d in ra.doors:
            b = d["to"]
            if b in rooms and b != a:
                x, z, w, dd = d["zone"]
                bx, _, bz, _ = d["arrival"]
                found.append((a, b, x + w / 2 - bx, z + dd / 2 - bz))
    return found


def solve(rooms: dict[str, rdt_map.Room], root: str, iterations: int = 200) -> dict[str, tuple[float, float]]:
    """BFS for a start, then Gauss–Seidel least squares: each room moves to the mean of what its links say."""
    edges = links(rooms)
    adjacent = defaultdict(list)
    for a, b, ox, oz in edges:
        adjacent[a].append((b, ox, oz))
        adjacent[b].append((a, -ox, -oz))
    pos = {root: (0.0, 0.0)}
    queue = deque([root])
    while queue:
        a = queue.popleft()
        for b, ox, oz in adjacent[a]:
            if b not in pos:
                pos[b] = (pos[a][0] + ox, pos[a][1] + oz)
                queue.append(b)
    for _ in range(iterations):
        for room in pos:
            if room == root:
                continue
            votes = [(pos[n][0] - ox, pos[n][1] - oz) for n, ox, oz in adjacent[room] if n in pos]
            # adjacent holds (neighbour, neighbour - room offset), so room = neighbour - offset
            if votes:
                pos[room] = (sum(v[0] for v in votes) / len(votes), sum(v[1] for v in votes) / len(votes))
    return pos


def draw(rooms: dict[str, rdt_map.Room], pos: dict[str, tuple[float, float]], names: dict, size: int = 1100) -> str:
    shapes = []
    for room, (ox, oz) in pos.items():
        for b in rooms[room].boxes:
            if b["x1"] - b["x0"] < 40000 and b["z1"] - b["z0"] < 40000:
                shapes.append((room, b["x0"] + ox, b["z0"] + oz, b["x1"] + ox, b["z1"] + oz, b))
    if not shapes:
        return ""
    x0 = min(s[1] for s in shapes); z0 = min(s[2] for s in shapes)
    x1 = max(s[3] for s in shapes); z1 = max(s[4] for s in shapes)
    scale = (size - 20) / max(x1 - x0, z1 - z0, 1)
    height = (z1 - z0) * scale + 20

    def px(x: float, z: float) -> tuple[float, float]:
        return 10 + (x - x0) * scale, height - 10 - (z - z0) * scale

    out = [f'<svg viewBox="0 0 {size} {height:.0f}" width="{size}" class="floor">']
    for room, ax, az, bx, bz, b in shapes:
        a, c = px(ax, bz), px(bx, az)
        cls = "block" if b["blocks"] else "soft"
        out.append(f'<rect x="{a[0]:.1f}" y="{a[1]:.1f}" width="{c[0]-a[0]:.1f}" height="{c[1]-a[1]:.1f}" class="{cls}">'
                   f'<title>{room}</title></rect>')
    for room, (ox, oz) in pos.items():
        r = rooms[room]
        for it in r.items:
            x, z, w, d = it["zone"]
            if w > 16:
                p = px(x + w / 2 + ox, z + d / 2 + oz)
                name = html.escape(names["items"].get(str(it["item"]), f"item {it['item']}"))
                out.append(f'<circle cx="{p[0]:.1f}" cy="{p[1]:.1f}" r="3" class="item"><title>{room}: {name}</title></circle>')
        xs = [b["x0"] for b in r.boxes if b["x1"] - b["x0"] < 40000] or [0]
        zs = [b["z0"] for b in r.boxes if b["z1"] - b["z0"] < 40000] or [0]
        xe = [b["x1"] for b in r.boxes if b["x1"] - b["x0"] < 40000] or [0]
        ze = [b["z1"] for b in r.boxes if b["z1"] - b["z0"] < 40000] or [0]
        c = px((min(xs) + max(xe)) / 2 + ox, (min(zs) + max(ze)) / 2 + oz)
        out.append(f'<text x="{c[0]:.1f}" y="{c[1]:.1f}" class="roomlabel">{room}</text>')
    out.append("</svg>")
    return "\n".join(out)


PAGE = """<!doctype html><html><head><meta charset="utf-8"><title>Floor maps</title>
<meta name="viewport" content="width=device-width, initial-scale=1"><style>
:root{--bg:#f7f6f2;--fg:#1d1d1b;--muted:#6b6a64;--block:#3b3a36;--soft:#c9c4b5;--item:#c8961e;--label:#2a6fdb}
@media (prefers-color-scheme: dark){:root:not([data-theme="light"]){--bg:#171716;--fg:#ecebe6;--muted:#a5a39b;--block:#d9d6cc;--soft:#5d5a52;--item:#e9b949;--label:#6aa2ff}}
body{background:var(--bg);color:var(--fg);font:14px/1.4 system-ui,sans-serif;margin:0;padding:16px}
h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:20px 0 6px}.legend{color:var(--muted)}
svg.floor{max-width:100%;height:auto}.block{fill:var(--block);opacity:.8}.soft{fill:var(--soft);opacity:.45}
.item{fill:var(--item)}.roomlabel{fill:var(--label);font-size:12px;font-weight:600;text-anchor:middle}
</style></head><body><h1>Floor maps</h1><div class="legend">Rooms placed from their door links (least squares);
collision drawn in place, gold dots = item pickup spots. Positions are approximate (about a door step).</div>
{floors}</body></html>"""


def main(argv: list[str]) -> int:
    install, out = Path(argv[0]), Path(argv[1])
    character = next((a for a in argv[2:] if a in rdt_map.VARIANTS), "jill")
    stages = argv[argv.index("--stages") + 1].split(",") if "--stages" in argv else [str(s) for s in range(1, 8)]
    names = json.loads((DATA / "names.json").read_text(encoding="utf-8"))
    areas = json.loads((DATA / "rooms.json").read_text(encoding="utf-8"))["areas"]
    usa = install / "USA" if (install / "USA").is_dir() else install
    floors = []
    for stage in stages:
        rooms = {}
        for path in sorted(usa.glob(f"Stage{stage}/ROOM*{rdt_map.VARIANTS[character]}.RDT")):
            if path.stat().st_size >= 0x94:
                try:
                    rooms[path.name[4:7].upper()] = rdt_map.load(install, path.name[4:7], character)
                except (ValueError, IndexError, KeyError):
                    pass
        if not rooms:
            continue
        # Several disconnected groups per stage are possible; place each from its own root.
        remaining = dict(rooms)
        parts = []
        while remaining:
            root = sorted(remaining)[0]
            pos = solve(remaining, root)
            parts.append(draw(rooms, pos, names))
            for room in pos:
                remaining.pop(room, None)
        floors.append(f"<h2>Stage {stage} — {html.escape(areas.get(stage, ''))}</h2>" + "\n".join(parts))
    out.write_text(PAGE.replace("{floors}", "\n".join(floors)), encoding="utf-8")
    print(f"wrote {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
