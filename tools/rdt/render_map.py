r"""Draws every room as a top-down map from the player's own room files (read-only; output stays local):
collision (walls, furniture, the clock — blocking dark, floor/soft zones light), walk zones, cameras and where they
look, camera switch zones, doors with their targets and arrival spots, items with their names and pickup zones.

    python tools/rdt/render_map.py "G:\GOG Games\Resident Evil" <out.html> [jill|chris] [--rooms 105,106]

One self-contained HTML page with an SVG per room (no game assets: shapes and numbers only).
"""
from __future__ import annotations

import html
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import rdt_map  # noqa: E402

DATA = Path(__file__).resolve().parents[2] / "data" / "games" / "re1"
SIZE = 520  # px per room view


def svg_room(r: rdt_map.Room, names: dict) -> str:
    xs, zs = [], []
    for b in r.boxes:
        if b["x1"] - b["x0"] < 60000 and b["z1"] - b["z0"] < 60000:
            xs += [b["x0"], b["x1"]]
            zs += [b["z0"], b["z1"]]
    for d in r.doors:
        xs += [d["zone"][0], d["zone"][0] + d["zone"][2]]
        zs += [d["zone"][1], d["zone"][1] + d["zone"][3]]
    if not xs:
        return "<p>(no geometry)</p>"
    x0, x1, z0, z1 = min(xs), max(xs), min(zs), max(zs)
    span = max(x1 - x0, z1 - z0, 1)
    scale = (SIZE - 20) / span

    def px(x: float, z: float) -> tuple[float, float]:
        # top-down: +X right, +Z up
        return 10 + (x - x0) * scale, SIZE - 10 - (z - z0) * scale

    out = [f'<svg viewBox="0 0 {SIZE} {SIZE}" width="{SIZE}" height="{SIZE}" class="room">']
    for wx1, wz1, wx2, wz2 in r.walk:
        a, b = px(wx1, wz2), px(wx2, wz1)
        out.append(f'<rect x="{a[0]:.1f}" y="{a[1]:.1f}" width="{b[0]-a[0]:.1f}" height="{b[1]-a[1]:.1f}" class="walk"/>')
    for s in r.switches:
        if s["header"]:
            continue
        pts = " ".join(f"{px(x, z)[0]:.1f},{px(x, z)[1]:.1f}" for x, z in s["quad"])
        out.append(f'<polygon points="{pts}" class="switch"><title>camera {s["from"]} → {s["to"]}</title></polygon>')
    for b in r.boxes:
        cls = "block" if b["blocks"] else "soft"
        a, c = px(b["x0"], b["z1"]), px(b["x1"], b["z0"])
        tip = f'collision {b["x0"]}..{b["x1"]} x {b["z0"]}..{b["z1"]} shape {b["shape"]} flags {b["flags"]:#x}'
        if b["shape"] == 3:
            out.append(f'<ellipse cx="{(a[0]+c[0])/2:.1f}" cy="{(a[1]+c[1])/2:.1f}" rx="{(c[0]-a[0])/2:.1f}" '
                       f'ry="{(c[1]-a[1])/2:.1f}" class="{cls}"><title>{tip}</title></ellipse>')
        else:
            out.append(f'<rect x="{a[0]:.1f}" y="{a[1]:.1f}" width="{c[0]-a[0]:.1f}" height="{c[1]-a[1]:.1f}" '
                       f'class="{cls}"><title>{tip}</title></rect>')
    for d in r.doors:
        x, z, w, dd = d["zone"]
        a, c = px(x, z + dd), px(x + w, z)
        out.append(f'<rect x="{a[0]:.1f}" y="{a[1]:.1f}" width="{c[0]-a[0]:.1f}" height="{c[1]-a[1]:.1f}" class="door">'
                   f'<title>door to {d["to"]} (lock {d["lock"]:#x})</title></rect>')
        out.append(f'<text x="{(a[0]+c[0])/2:.1f}" y="{(a[1]+c[1])/2:.1f}" class="doorlabel">{d["to"]}</text>')
    for it in r.items:
        x, z, w, dd = it["zone"]
        if w <= 16 and dd <= 16:
            continue
        a, c = px(x, z + dd), px(x + w, z)
        name = html.escape(names["items"].get(str(it["item"]), f"item {it['item']}"))
        out.append(f'<rect x="{a[0]:.1f}" y="{a[1]:.1f}" width="{c[0]-a[0]:.1f}" height="{c[1]-a[1]:.1f}" class="item">'
                   f'<title>{name} ×{it["quantity"]} (flag {it["flag"]})</title></rect>')
        out.append(f'<text x="{(a[0]+c[0])/2:.1f}" y="{a[1]-2:.1f}" class="itemlabel">{name}</text>')
    for o in r.objects:
        x, _, z = o["pos"]
        w, d = max(o["params"][6], 200), max(o["params"][7], 200)  # approximate footprint (params 6/7, unverified)
        a, c = px(x - w / 2, z + d / 2), px(x + w / 2, z - d / 2)
        cond = html.escape(" / ".join(o["guards"]) or "always")
        out.append(f'<rect x="{a[0]:.1f}" y="{a[1]:.1f}" width="{c[0]-a[0]:.1f}" height="{c[1]-a[1]:.1f}" class="object">'
                   f'<title>object {o["slot"]} at ({x},{z}) — placed when: {cond}</title></rect>')
    for cam in r.cameras:
        (fx, _, fz), (tx, _, tz) = cam["from"], cam["to"]
        a, b = px(fx, fz), px(tx, tz)
        out.append(f'<line x1="{a[0]:.1f}" y1="{a[1]:.1f}" x2="{b[0]:.1f}" y2="{b[1]:.1f}" class="camline"/>'
                   f'<circle cx="{a[0]:.1f}" cy="{a[1]:.1f}" r="4" class="cam"><title>camera {cam["id"]}</title></circle>'
                   f'<text x="{a[0]+5:.1f}" y="{a[1]-4:.1f}" class="camlabel">{cam["id"]}</text>')
    out.append("</svg>")
    return "\n".join(out)


PAGE = """<!doctype html><html><head><meta charset="utf-8"><title>Room maps</title>
<meta name="viewport" content="width=device-width, initial-scale=1"><style>
:root{--bg:#f7f6f2;--fg:#1d1d1b;--muted:#6b6a64;--block:#3b3a36;--soft:#c9c4b5;--walk:#e4efe2;--door:#2f8f4e;--item:#c8961e;--cam:#2a6fdb;--switch:#2a6fdb22}
@media (prefers-color-scheme: dark){:root:not([data-theme="light"]){--bg:#171716;--fg:#ecebe6;--muted:#a5a39b;--block:#d9d6cc;--soft:#5d5a52;--walk:#22301f;--door:#57c27a;--item:#e9b949;--cam:#6aa2ff;--switch:#6aa2ff22}}
body{background:var(--bg);color:var(--fg);font:14px/1.4 system-ui,sans-serif;margin:0;padding:16px}
h1{font-size:20px;margin:0 0 4px}.legend{color:var(--muted);margin-bottom:16px}
.grid{display:flex;flex-wrap:wrap;gap:16px}.card{max-width:100%}.card h2{font-size:15px;margin:0 0 4px}
svg.room{max-width:100%;height:auto;background:color-mix(in srgb,var(--bg) 85%,var(--fg));border-radius:6px}
.block{fill:var(--block);opacity:.85}.soft{fill:var(--soft);opacity:.5}.walk{fill:var(--walk)}
.switch{fill:var(--switch);stroke:var(--cam);stroke-width:.5;stroke-dasharray:3 2}
.door{fill:none;stroke:var(--door);stroke-width:2}.doorlabel{fill:var(--door);font-size:11px;text-anchor:middle}
.item{fill:var(--item);opacity:.35;stroke:var(--item)}.itemlabel{fill:var(--item);font-size:10px;text-anchor:middle}
.object{fill:#9b59b6;opacity:.45;stroke:#9b59b6;stroke-dasharray:2 2}.cam{fill:var(--cam)}.camline{stroke:var(--cam);stroke-width:1;opacity:.6}.camlabel{fill:var(--cam);font-size:10px}
</style></head><body><h1>Room maps</h1><div class="legend">Top-down, +X right, +Z up. Dark = blocking collision,
light = floor/soft zones, green = doors (target room), gold = item pickup zones, blue = cameras (looking along the
line) and their switch zones, purple dashed = objects placed by the room script (approximate size; hover for when). Hover for details. Built from the local install.</div><div class="grid">
{cards}</div></body></html>"""


def main(argv: list[str]) -> int:
    install, out = Path(argv[0]), Path(argv[1])
    character = next((a for a in argv[2:] if a in rdt_map.VARIANTS), "jill")
    only = set(argv[argv.index("--rooms") + 1].upper().split(",")) if "--rooms" in argv else None
    names = json.loads((DATA / "names.json").read_text(encoding="utf-8"))
    rooms = json.loads((DATA / "rooms.json").read_text(encoding="utf-8"))["rooms"]
    usa = install / "USA" if (install / "USA").is_dir() else install
    cards = []
    for path in sorted(usa.glob(f"Stage*/ROOM*{rdt_map.VARIANTS[character]}.RDT")):
        room = path.name[4:7].upper()
        if path.stat().st_size < 0x94 or (only and room not in only):
            continue
        try:
            r = rdt_map.load(install, room, character)
        except (ValueError, IndexError, KeyError) as e:
            cards.append(f'<div class="card"><h2>{room}</h2><p>could not read: {html.escape(str(e))}</p></div>')
            continue
        title = room + (f" — {html.escape(rooms[room]['name'])}" if rooms.get(room, {}).get("name") else "")
        cards.append(f'<div class="card"><h2>{title}</h2>{svg_room(r, names)}</div>')
    out.write_text(PAGE.replace("{cards}", "\n".join(cards)), encoding="utf-8")
    print(f"wrote {out} ({len(cards)} rooms)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
