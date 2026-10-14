r"""When does each game flag first turn on? Orders practice saves by progress and finds, for every flag bit, the save
where it first appears — a story event happened between that save and the previous one (save names say where).

    python tools/bounties/flag_timeline.py <saves folder> [...] [--json out.json] [--unlabelled]

Saves are grouped into runs by name ("100 Chris …" = one route, "Chris …" another, same for Jill) and ordered by
rooms explored. Only flags that stay set in every later save of the run are reported (story progress); flags that
come and go are counted separately. Leads only: a label needs a capture or a second source.
"""
from __future__ import annotations

import json
import re
import struct
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import saves  # noqa: E402
import timeline  # noqa: E402

BLOCK = 0xBE9620
BANKS = {  # name: (address, bytes) — the saved flag banks (MEMORY_MAP; decomp SCENARIO_FLAGS.md)
    "flagScenarioA": (0xBE98C0, 16), "flagScenarioB": (0xBE9854, 32), "flagLocks": (0xBE9874, 8),
    "flagEnemies": (0xBE987C, 32), "flagRoomItems": (0xBE989C, 32), "flagRoom": (0xBE98D0, 20),
}


def bits(data: bytes, bank: str) -> set[int]:
    address, size = BANKS[bank]
    found = set()
    for word in range(size // 4):
        (value,) = struct.unpack_from("<I", data, address - BLOCK + word * 4)
        found |= {word * 32 + 31 - b for b in range(32) if value >> b & 1}
    return found


def run_of(name: str) -> str:
    return re.sub(r"\s.*", "", name.split(" ", 1)[1]) if name.startswith("100 ") else name.split(" ", 1)[0]


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    out = Path(argv[argv.index("--json") + 1]) if "--json" in argv else None
    folders = [a for i, a in enumerate(argv) if not a.startswith("--") and (i == 0 or argv[i - 1] != "--json")]
    names = timeline.Names()
    runs: dict[str, list[dict]] = defaultdict(list)
    for folder in folders:
        for path in sorted(Path(folder).rglob("*.dat")):
            state = saves.read_save(path)
            if state is None:
                continue
            data = path.read_bytes()
            info = saves.describe(path, state, names)
            key = ("100% " if path.stem.startswith("100 ") else "any% ") + info["character"]
            runs[key].append({"name": path.stem, "room": info["room"], "progress": info["roomsExplored"],
                              "bits": {bank: bits(data, bank) for bank in BANKS}})
    labelled = {(l["bank"].rstrip("0123456789"), int(l["bank"][len(l["bank"].rstrip("0123456789")):] or 0) * 32
                 + 31 - (int(l["mask"], 16).bit_length() - 1)) for l in
                json.loads((Path(__file__).resolve().parents[2] / "data/games/re1/flag-labels.json").read_text(encoding="utf-8"))["labels"]}
    report = []
    for run, rows in sorted(runs.items()):
        rows.sort(key=lambda r: r["progress"])
        print(f"\n== {run}: {len(rows)} saves")
        for bank in ("flagScenarioA", "flagScenarioB", "flagLocks"):
            first: dict[int, int] = {}
            flaky = 0
            for flag in set().union(*(r["bits"][bank] for r in rows)):
                seen = [flag in r["bits"][bank] for r in rows]
                start = seen.index(True)
                if all(seen[start:]):
                    first[flag] = start
                else:
                    flaky += 1
            for flag, start in sorted(first.items(), key=lambda kv: (kv[1], kv[0])):
                prev = rows[start - 1] if start else None
                entry = {"run": run, "bank": bank, "flag": flag, "labelled": (bank, flag) in labelled,
                         "after": f"{prev['name']} ({prev['room']})" if prev else "start of run",
                         "by": f"{rows[start]['name']} ({rows[start]['room']})"}
                report.append(entry)
                if "--unlabelled" in argv and entry["labelled"]:
                    continue
                print(f"  {bank}{flag // 32}:{flag:3} {'labelled ' if entry['labelled'] else 'NEW      '} between "
                      f"{entry['after']} and {entry['by']}")
            print(f"  ({bank}: {len(first)} progress flags, {flaky} that come and go)")
    if out:
        out.write_text(json.dumps(report, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
