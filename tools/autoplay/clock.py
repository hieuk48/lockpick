r"""When the in-game clock runs (capture bounty B206), measured with the dev-only autoplay harness on GOG English.

    python tools/autoplay/clock.py [chris|jill]

For each state the clock (0xD22730, frames at 30/s) is sampled every 100 ms against real time: standing in control,
the status screen, the item box screen, an examine message, the options screen (Start), a door transition, and the
opening cutscene at normal speed. Saves are made read-only for the run and checked against their hashes afterwards.
Writes %LOCALAPPDATA%\Lockpick\research\clock-<date>.json. Never shipped.
"""
from __future__ import annotations

import hashlib
import json
import os
import stat
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ap  # noqa: E402
import doors  # noqa: E402

INSTALL = Path(r"G:\GOG Games\Resident Evil")
SAVE = INSTALL / "SAVE"
OUT = Path(os.environ["LOCALAPPDATA"]) / "Lockpick" / "research" / f"clock-{time.strftime('%Y%m%d-%H%M%S')}.json"
results: list[dict] = []


def sample(label: str, seconds: float, until=None) -> dict:
    """Clock and real time every 100 ms for `seconds` (or until `until()` is true, at most `seconds`)."""
    points, start = [], time.time()
    while time.time() - start < seconds:
        points.append((round(time.time() - start, 2), ap.igt()))
        if until and until():
            break
        time.sleep(0.1)
    real = points[-1][0] - points[0][0]
    frames = points[-1][1] - points[0][1]
    # Stretches where the clock stood still (at least 0.5 s).
    still, run_start = [], None
    for (t0, a), (t1, b) in zip(points, points[1:]):
        if a == b:
            run_start = t0 if run_start is None else run_start
        elif run_start is not None:
            if t0 - run_start >= 0.5:
                still.append([run_start, t0])
            run_start = None
    if run_start is not None and points[-1][0] - run_start >= 0.5:
        still.append([run_start, points[-1][0]])
    entry = {"state": label, "realSeconds": round(real, 2), "clockFrames": frames,
             "clockSeconds": round(frames / 30, 2), "rate": round(frames / 30 / real, 2) if real else None, "stoodStill": still}
    results.append(entry)
    print(f"{label:28} real {real:6.2f} s  clock {frames / 30:6.2f} s  rate {entry['rate']}  still {still}")
    return entry


def protect_saves() -> dict[str, str]:
    hashes = {}
    for f in SAVE.glob("*.dat"):
        hashes[f.name] = hashlib.sha256(f.read_bytes()).hexdigest()
        os.chmod(f, stat.S_IREAD)
    return hashes


def release_saves(before: dict[str, str]) -> None:
    for f in SAVE.glob("*.dat"):
        os.chmod(f, stat.S_IREAD | stat.S_IWRITE)
        if before.get(f.name) != hashlib.sha256(f.read_bytes()).hexdigest():
            print(f"!! {f.name} changed during the run")
    print("saves checked:", len(before))


def back_to_control() -> None:
    for _ in range(10):
        if ap.in_control():
            return
        if ap.message_active():
            ap.dismiss()
        else:
            ap.send("press X 120", "wait 600")
    ap.settle(10)


def main(character: str) -> None:
    saves = protect_saves()
    try:
        ap.serve()
        print(f"new game in {ap.new_game(character):.0f} s")
        sample("standing in control", 5)

        ap.send("press Y")
        ap.wait_for(ap.status_open, 10)
        ap.send("wait 800")
        sample("status screen", 5)
        ap.close_menu()
        ap.settle(5)

        ap.send("open-box")
        ap.wait_for(lambda: ap.box_screen() is not None, 10)
        ap.send("wait 800")
        sample("item box screen", 5)
        back_to_control()

        ap.send("press Start 120", "wait 1500")
        ap.shot("clock-start.png", 480)
        options = ap.look("Is an OPTIONS or configuration screen shown? Answer yes or no, then name what is shown.")
        print("after Start:", options)
        entry = sample("after Start (options?)", 5)
        entry["screen"] = options
        back_to_control()

        ap.send("press Y")
        ap.wait_for(ap.status_open, 10)
        ap.close_menu()
        ap.settle(5)
        try:
            ap.examine(1, turns=1)  # the knife: CHECK shows it in the viewer, then a message
        except ap.Failed as e:
            print("examine:", e)
        back_to_control()

        record = doors.record_to(doors.build(INSTALL), "106", character, "105")
        ap.send("door " + record.hex())
        sample("door 105 -> 106 (to control)", 15, until=lambda: ap.state().get("room") == "106" and ap.in_control())
        sample("standing in control again", 3)

        # The opening at normal speed, from New Game (clock 0) to control.
        ap.serve()
        ap.send(f"new-game {character} 120")
        sample("opening cutscene (normal speed)", 240, until=lambda: ap.state().get("room") == "105" and ap.in_control())
    finally:
        try:
            ap.quit_server()
        finally:
            release_saves(saves)
            OUT.write_text(json.dumps({"character": character, "results": results}, indent=1), encoding="utf-8")
            print("wrote", OUT)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "chris")
