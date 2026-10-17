r"""B206 follow-up (dev only): the clock while a message is on screen, in the item viewer, and after Back (options?).

    python tools/autoplay/clock2.py
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ap  # noqa: E402
import clock  # noqa: E402


def main() -> None:
    saves = clock.protect_saves()
    try:
        ap.serve()
        print(f"new game in {ap.new_game('chris'):.0f} s")
        clock.sample("standing in control", 3)

        ap._open_submenu(1, 30)  # the knife
        ap._choose(1)            # CHECK: the item viewer
        ap.send("wait 1500")
        clock.sample("item viewer (CHECK)", 4)
        ap.send("press A 120", "wait 1500")
        print("message:", ap.message_text(), "| active:", ap.message_active())
        clock.sample("message on screen", 4)
        ap.close_menu()
        clock.back_to_control()

        ap.send("press Back 120", "wait 1500")
        ap.shot("clock-back.png", 480)
        seen = ap.look("Is an OPTIONS or configuration screen shown? Answer yes or no, then name what is shown.")
        print("after Back:", seen)
        entry = clock.sample("after Back (options?)", 4)
        entry["screen"] = seen
        clock.back_to_control()
    finally:
        try:
            ap.quit_server()
        finally:
            clock.release_saves(saves)
            out = clock.OUT.with_name(clock.OUT.stem + "-b.json")
            out.write_text(json.dumps({"results": clock.results}, indent=1), encoding="utf-8")
            print("wrote", out)


if __name__ == "__main__":
    main()
