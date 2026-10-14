r"""Room survey (dev-only autoplay): enters every room through a real door record, fast-forwards cutscenes, and
records what is there, per character, from a new game. Never saves; the SAVE folder is checked after every room and
restored from the verified backup if anything changed.

    python -I tools/autoplay/survey.py <out-dir> <save-backup-dir> [jill|chris ...] [--rooms 105,106]

Writes <out-dir>/survey-<character>.jsonl (one line per room) and a Lockpick capture of the whole run.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import ap  # noqa: E402
import doors  # noqa: E402

INSTALL = Path(r"G:\GOG Games\Resident Evil")
SAVE = INSTALL / "SAVE"
ENEMY_BASE, ENEMY_STRIDE, ENEMY_HP = 0xBE6464, 0x18C, 0x88
DESCRIBE = ("Describe this Resident Evil room screenshot in at most 30 words: the kind of room, any creatures or "
            "people visible, and any items on the floor or furniture.")


def write_save(path: Path, data: bytes) -> None:
    """Writes a save file the harness owns the change to; save files stay read-only otherwise, so the game itself
    can never overwrite one during a run."""
    if path.exists():
        path.chmod(0o666)
    path.write_bytes(data)
    path.chmod(0o444)


def protect_saves() -> None:
    for path in SAVE.glob("*.dat"):
        path.chmod(0o444)


def check_saves(backup: Path, log) -> None:
    """Restores the SAVE folder from the backup if any file differs (the survey must never save)."""
    manifest = {}
    for line in (backup / "manifest.sha256").read_text(encoding="utf-8-sig").splitlines():
        if line.strip():
            digest, name = line.split(maxsplit=1)
            manifest[name.strip()] = digest.upper()
    for name, digest in manifest.items():
        path = SAVE / name
        if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest().upper() != digest:
            write_save(path, (backup / name).read_bytes())
            ok = hashlib.sha256(path.read_bytes()).hexdigest().upper() == digest
            log(f"!! SAVE/{name} changed during the run — restored from backup ({'verified' if ok else 'RESTORE FAILED'})")
    for path in SAVE.iterdir():
        if path.name not in manifest and path.suffix.lower() == ".dat":
            log(f"!! new file SAVE/{path.name} appeared during the run (left in place)")


def enemies() -> list[dict]:
    raw = ap.peek(ENEMY_BASE, ENEMY_STRIDE * 8)
    found = []
    for i in range(8):
        flags, kind = raw[i * ENEMY_STRIDE], raw[i * ENEMY_STRIDE + 1]
        hp = struct.unpack_from("<h", raw, i * ENEMY_STRIDE + ENEMY_HP)[0]
        if flags:
            found.append({"slot": i, "type": kind, "hp": hp, "flags": flags})
    return found


def fresh_game(character: str, log) -> None:
    for attempt in range(3):
        try:
            ap.send("restart", "wait 2000", timeout=120)
            took = ap.new_game(character)
            log(f"new game ({character}) in control after {took:.0f} s")
            return
        except ap.Failed as e:
            log(f"new game attempt {attempt + 1} failed: {str(e)[-200:]}")
    raise ap.Failed("could not start a new game")


def visit(room: str, character: str, table: list[dict], out: Path, log) -> dict:
    """Enters one room through a door record and records what is there."""
    entry = {"room": room, "character": character, "at": time.strftime("%Y-%m-%dT%H:%M:%S")}
    try:
        s = ap.state()
        if s.get("gameplay") != "True" or ap.flags() & ap.DEAD or s.get("hp", "0").startswith(("0/", "-")):
            log("player dead or game not in play — new game")
            fresh_game(character, log)
        ap.send(f"mark survey {character} enter {room}")
        record = doors.record_to(table, room, character)
        ap.send("door " + record.hex(), "wait 800", timeout=60)
        for _ in range(60):
            now = ap.state().get("room")
            if now == room:
                break
            # Scenario flag 0 remaps stages 1/2 to their return versions 6/7 (room_transition_load).
            if now and now[1:] == room[1:] and int(now[0]) == int(room[0]) + 5:
                entry["redirectedTo"] = now
                raise ap.Failed(f"redirected to {now} (stage-variant flag set in this game)")
            ap.send("wait 300")
        else:
            raise ap.Failed(f"did not arrive; now {ap.state()}")
        entry["entered"] = True
        ap.send("heal")
        entry["control"] = ap.settle(90)
        s = ap.state()
        entry.update({"hp": s.get("hp"), "pos": s.get("pos"), "now": s.get("room"),
                      "flags": f"{ap.flags():08X}", "enemies": enemies()})
        for _ in range(2):
            shot = ap.shot(f"survey-{character}-{room}.png", 480)
            entry["seen"] = ap.look(DESCRIBE)
            if entry["seen"].strip().lower().strip(".") != "black":
                break
            ap.send("wait 1500")  # a fade was still running
        kept = out / "shots" / shot.name
        kept.parent.mkdir(exist_ok=True)
        shutil.copy2(shot, kept)
        entry["shot"] = str(kept)
        ap.send("heal")
        entry.pop("error", None)
        log(f"{room}: control={entry['control']} hp={entry['hp']} enemies={[e['type'] for e in entry['enemies']]} — {entry['seen'][:120]}")
    except (ap.Failed, KeyError, ValueError, OSError) as e:
        entry["error"] = str(e)[-300:]
    return entry


def survey(character: str, out: Path, backup: Path, table: list[dict], only: set[str] | None) -> None:
    results = out / f"survey-{character}.jsonl"
    done = {e["room"] for e in map(json.loads, results.read_text(encoding="utf-8").splitlines())
            if not e.get("error")} if results.exists() else set()
    logfile = (out / f"survey-{character}.log").open("a", encoding="utf-8")

    def log(text: str) -> None:
        line = f"{time.strftime('%H:%M:%S')} {text}"
        print(line, flush=True)
        logfile.write(line + "\n")
        logfile.flush()

    rooms = sorted({d["to"] for d in table if d["character"] == character}, key=lambda r: (r[0], r[1:]))
    rooms = [r for r in rooms if (only is None or r in only) and r not in done
             and any(INSTALL.joinpath("USA", f"Stage{r[0]}", f"ROOM{r}{'1' if character == 'jill' else '0'}.RDT").exists() for _ in [0])]
    log(f"surveying {len(rooms)} rooms for {character}")
    fresh_game(character, log)
    for room in rooms:
        for attempt in range(2):
            entry = visit(room, character, table, out, log)
            if not entry.get("error"):
                break
            log(f"{room}: {entry['error'][-160:]}")
            try:
                fresh_game(character, log)
            except ap.Failed as e2:
                log(f"restart failed: {e2}; restarting the server")
                ap.serve()
                ap.send(f"record {out / 'captures'}")
                fresh_game(character, log)
        with results.open("a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
        check_saves(backup, log)
    logfile.close()


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    out, backup = Path(argv[0]), Path(argv[1])
    only = None
    if "--rooms" in argv:
        only = set(argv[argv.index("--rooms") + 1].upper().split(","))
    characters = [a for a in argv[2:] if a in ("jill", "chris")] or ["jill", "chris"]
    out.mkdir(parents=True, exist_ok=True)
    table = doors.build(INSTALL)
    protect_saves()
    ap.serve()
    ap.send(f"record {out / 'captures'}")
    try:
        for character in characters:
            survey(character, out, backup, table, only)
    finally:
        ap.send("record stop", check=False)
        check_saves(backup, print)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
