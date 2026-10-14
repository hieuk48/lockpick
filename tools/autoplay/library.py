r"""Save-library test (dev-only autoplay): loads saves in the game and checks Lockpick's live reads against the
offline save decode (tools/bounties/saves.py) — room, character and inventory — then records the scene.

    python -I tools/autoplay/library.py <out-dir> <save-backup-dir> <folder-with-.dat> [...] [--own]

--own also loads the owner's 8 slots (no file changes). Other saves are borrowed into slot 1 (savedat1.dat): the
owner's copy is in the verified backup, the borrowed file gets the owner's own key/joystick settings (save offsets
0x800–0x91F and 0xA00–0xA81) so loading it can't change the controls, and slot 1 is restored with a hash check after
every save and at the end.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "bounties"))
import ap  # noqa: E402
import saves  # noqa: E402
import survey  # noqa: E402
import timeline  # noqa: E402

SLOT1 = survey.SAVE / "savedat1.dat"
SETTINGS = [(0x800, 0x920), (0xA00, 0xA82)]


def borrow(save: Path, backup: Path) -> None:
    data = bytearray(save.read_bytes())
    own = (backup / "savedat1.dat").read_bytes()
    for start, end in SETTINGS:
        data[start:end] = own[start:end]
    SLOT1.write_bytes(bytes(data))


def restore_slot1(backup: Path) -> bool:
    shutil.copy2(backup / "savedat1.dat", SLOT1)
    return hashlib.sha256(SLOT1.read_bytes()).digest() == hashlib.sha256((backup / "savedat1.dat").read_bytes()).digest()


def load_slot(slot: int) -> float:
    """From a fresh launch: title → LOAD GAME → slot → in control. Returns seconds."""
    t = time.time()
    ap.wait_screen({"title", "newload"}, 60, press="A")
    if ap.screen() != "newload":
        ap.send("press A")
        ap.wait_screen({"newload"}, 15)
    ap.send("press A")  # LOAD GAME is highlighted when saves exist
    ap.wait_screen({"loadmenu"}, 30)
    ap.send("wait 800", *["press Down" for _ in range(slot - 1)], "wait 400", "press A")
    ap.send("wait 3000")
    if not ap.settle(120):
        raise ap.Failed(f"no control after loading slot {slot}: {ap.state()}")
    return time.time() - t


def expected(path: Path, names) -> dict:
    state = saves.read_save(path)
    info = saves.describe(path, state, names)
    room = f"{state.get('stage', 0) + 1}{state.get('room', 0):02X}"
    return {"room": room, "character": info["character"], "inventory": info["inventory"]}


def run(entries: list[tuple[str, Path, int | None]], out: Path, backup: Path) -> None:
    names = timeline.Names()
    results = out / "library.jsonl"
    done = {json.loads(l)["name"] for l in results.read_text(encoding="utf-8").splitlines()} if results.exists() else set()
    logfile = (out / "library.log").open("a", encoding="utf-8")

    def log(text: str) -> None:
        line = f"{time.strftime('%H:%M:%S')} {text}"
        print(line, flush=True)
        logfile.write(line + "\n")
        logfile.flush()

    for name, path, slot in entries:
        if name in done:
            continue
        entry = {"name": name, "file": str(path), "at": time.strftime("%Y-%m-%dT%H:%M:%S")}
        try:
            want = expected(path, names)
            entry["expected"] = want
            if slot is None:
                borrow(path, backup)
                slot = 1
            ap.send("restart", "wait 2000", timeout=120)
            ap.send(f"mark library load {name}")
            entry["loadSeconds"] = round(load_slot(slot))
            s = ap.state()
            entry["live"] = {"room": s.get("room"), "character": s.get("char"), "hp": s.get("hp"), "pos": s.get("pos")}
            entry["roomMatches"] = s.get("room") == want["room"]
            entry["characterMatches"] = (s.get("char") or "").lower() == want["character"].lower()
            entry["enemies"] = survey.enemies()
            shot = ap.shot(f"library-{path.stem}.png", 480)
            kept = out / "shots" / shot.name
            kept.parent.mkdir(exist_ok=True)
            shutil.copy2(shot, kept)
            entry["shot"] = str(kept)
            entry["seen"] = ap.look(survey.DESCRIBE)
            log(f"{name}: room {s.get('room')} (save says {want['room']}) {s.get('char')} — "
                f"{'OK' if entry['roomMatches'] and entry['characterMatches'] else 'MISMATCH'}")
        except (ap.Failed, KeyError, ValueError, OSError) as e:
            entry["error"] = str(e)[-300:]
            log(f"{name}: ERROR {entry['error'][-160:]}")
        finally:
            if not restore_slot1(backup):
                log("!! slot 1 restore did not verify")
        with results.open("a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
        survey.check_saves(backup, log)
    logfile.close()


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    out, backup = Path(argv[0]), Path(argv[1])
    out.mkdir(parents=True, exist_ok=True)
    entries: list[tuple[str, Path, int | None]] = []
    if "--own" in argv:
        entries += [(f"own slot {n}", backup / f"savedat{n}.dat", n) for n in range(1, 9)]
    for folder in (a for a in argv[2:] if not a.startswith("--")):
        for path in sorted(Path(folder).rglob("*.dat")):
            if saves.read_save(path) is not None:
                entries.append((path.stem, path, None))
    if not ap.EXE.exists():
        raise SystemExit("build tools/Lockpick.Autoplay first")
    ap.serve()
    ap.send(f"record {out / 'captures'}")
    try:
        run(entries, out, backup)
    finally:
        ap.send("record stop", check=False)
        restore_slot1(backup)
        survey.check_saves(backup, print)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
