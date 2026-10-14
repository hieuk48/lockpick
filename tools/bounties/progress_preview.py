"""The progress tracker's view of a capture's last moment (mirrors Re1ProgressTracker in the RE1 adapter).

    python tools/bounties/progress_preview.py LockpickCapture-*.zip [--missing]

Per area: items taken, documents and maps, rooms explored, enemies defeated; then the named story events so far.
--missing lists what's left. Used to check the app's numbers against a played session.
"""
from __future__ import annotations

import json
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import bounties  # noqa: E402
import timeline  # noqa: E402

VISIT_TABLE = [(0, 32), (32, 31), (63, 19), (82, 18), (100, 24)]


def bit(state: dict, bank: str, flag: int) -> bool:
    return bool(state.get(f"{bank}{flag // 32}", 0) >> (31 - flag % 32) & 1)


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    missing = "--missing" in argv
    path = next(a for a in argv if not a.startswith("--"))
    session = bounties.parse_memory("m", zipfile.ZipFile(path).read("memory-1.bin"),
                                    bounties.load_profile_fields("re1-gog-usa-hotfix4"))
    state = next(s for s in reversed(session.states) if bounties.in_game(s))
    names = timeline.Names()
    rooms = json.loads((timeline.DATA / "rooms.json").read_text(encoding="utf-8"))
    player = "jill" if state.get("character", 0) & 3 == 1 else "chris"
    mine = lambda u: u["player"] in (player, "both")  # noqa: E731
    first = lambda entries: list({e["flag"]: e for e in reversed(entries)}.values())  # noqa: E731
    items = first([u for u in names.index["items"] if mine(u) and u["flag"] is not None and u["item"] != 49])  # lockpick: handed over
    enemies = first([u for u in names.index["enemies"] if mine(u) and u["flag"] not in (None, 255) and u["type"] < 32])
    t = state.get("gameTimer", 0) // 30
    print(f"{player.title()} at {t // 60}:{t % 60:02} in {state.get('stage', 0) + 1}{state.get('room', 0):02X}")
    for stage, area in sorted(rooms["areas"].items()):
        rows = []
        pick = [(names.item(u["item"]), u["room"], not bit(state, "flagRoomItems", u["flag"])) for u in items if u["room"][0] == stage and u["item"] < 0x4E]
        docs = [(names.item(u["item"]), u["room"], not bit(state, "flagRoomItems", u["flag"])) for u in items if u["room"][0] == stage and u["item"] >= 0x4E]
        kills = [(names.enemy(u["type"]), u["room"], bit(state, "flagEnemies", u["flag"])) for u in enemies if u["room"][0] == stage]
        visits = []
        if "1" <= stage <= "5":
            base, count = VISIT_TABLE[int(stage) - 1]
            visits = [(f"{stage}{r:02X}", f"{stage}{r:02X}", bit(state, "flagRoom", base + r)) for r in range(count)]
        for title, group in (("items", pick), ("documents/maps", docs), ("rooms explored", visits), ("enemies defeated", kills)):
            if group:
                rows.append(f"{title} {sum(d for _, _, d in group)}/{len(group)}")
                if missing:
                    left = [f"{n} ({r})" for n, r, d in group if not d]
                    rows[-1] += ("  left: " + ", ".join(left[:12]) + (" …" if len(left) > 12 else "")) if left else ""
        if rows:
            print(f"- {area}: " + ("\n    " if missing else "; ").join(rows))
    story = [label for (bank, mask), label in timeline.KNOWN_FLAGS.items()
             if bank.startswith("flagScenario") and state.get(bank, 0) & mask]
    print(f"story so far: {len(story)} named events")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
