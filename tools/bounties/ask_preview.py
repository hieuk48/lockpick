"""What would ask mode ask about in a capture? Replays a capture's memory log through the same rules as the app.

    python tools/bounties/ask_preview.py LockpickCapture-*.zip [more captures] [--no-labels]

--no-labels ignores the confirmed labels, to see how much the rules alone would explain in unplayed parts.

Mirrors Re1ChangeExplainer (src/Lockpick.GameAdapter.RE1): flag changes explained by the room index (pickups, kills,
doors, story flags set by room scripts), the rooms-visited table, maps and documents, items handed over and the
confirmed labels are not asked about. Prints what is left, so rules and labels can be checked against past runs.
Keep the two in sync (tests on both sides use the same cases).
"""
from __future__ import annotations

import json
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import bounties  # noqa: E402
import timeline  # noqa: E402

BANKS = [("flagRoomItems", 8), ("flagEnemies", 8), ("flagLocks", 2), ("flagRoom", 5), ("flagScenarioA", 4), ("flagScenarioB", 8)]
VISIT_BASE = [0, 32, 63, 82, 100]  # the game's table at 0x4D31E0
FIRST_MAP, FIRST_DOCUMENT = 124, 130


def same_room(a: str, b: str) -> bool:
    return a == b or (a[1:] == b[1:] and (a[0], b[0]) in {("1", "6"), ("6", "1"), ("2", "7"), ("7", "2")})


class Explainer:
    def __init__(self, labels: bool = True) -> None:
        self.names = timeline.Names()
        index = self.names.index
        self.story = index.get("story", [])
        self.labels = timeline.KNOWN_FLAGS if labels else {}

    def uses(self, kind: str, flag: int, room: str, player: str, key: str = "flag") -> list[dict]:
        return self.names.lookup(kind, flag, room, player, key)

    def explained(self, bank: str, word: int, mask: int, flag: int, now_set: bool, room: str, player: str) -> bool:
        if (f"{bank}{word}", mask) in self.labels:
            return True
        if bank == "flagRoomItems":
            return bool(self.uses("items", flag, room, player))
        if bank == "flagEnemies":
            return now_set and (flag == 255 or bool(self.uses("enemies", flag, room, player)))
        if bank == "flagLocks":
            return True  # doors from the index; anything else is a locked desk or box
        if bank == "flagRoom":
            return now_set
        if bank.startswith("flagScenario"):
            return any(u["flag"] == flag and u["bank"] == bank[-1] and same_room(u["room"], room)
                       and u["player"] in (player, "both") for u in self.story)
        return False

    def analyze(self, prev: dict, cur: dict) -> tuple[list[tuple[str, bool]], list[str]]:
        """(unexplained changes as (text, attributable to an action), player actions) for one poll."""
        if not (bounties.in_game(prev) and bounties.in_game(cur)) or cur.get("gameTimer", 0) < prev.get("gameTimer", 0):
            return [], []
        room = f"{cur.get('stage', 0) + 1}{cur.get('room', 0):02X}"
        player = "jill" if cur.get("character", 0) & 3 == 1 else "chris"
        found, actions, picked = [], [], set()
        for bank, words in BANKS:
            for word in range(words):
                key = f"{bank}{word}"
                if key not in prev or key not in cur:
                    continue
                for bit, now_set in timeline.changed_bits(prev[key], cur[key]):
                    flag = timeline.flag_index(word, bit)
                    story = bank.startswith("flagScenario")
                    if bank == "flagRoomItems" and not now_set:
                        picked |= {u["item"] for u in self.uses("items", flag, room, player)}
                    if self.explained(bank, word, 1 << bit, flag, now_set, room, player):
                        if not story and not (bank == "flagRoom" and flag < FIRST_MAP):
                            actions.append(f"{key} 0x{1 << bit:X}")
                    elif story and not now_set:
                        pass  # a state set earlier ends
                    else:
                        found.append((f"{key} bit 0x{1 << bit:08X} (flag {flag}) {'set' if now_set else 'cleared'} in {room}",
                                      story or bank == "flagRoomItems"))
        inventory = lambda s: {s.get(f"inv{i}", 0) & 0xFF for i in range(8)} - {0}  # noqa: E731
        box = lambda s: {s.get(f"box{i}", 0) & 0xFF for i in range(48)} - {0}  # noqa: E731
        gone = {i for i in inventory(prev) - inventory(cur) if i not in box(cur) or i in box(prev)}
        came = inventory(cur) - inventory(prev) - picked - box(prev)
        actions += [f"used {i}" for i in gone] + [f"received {i}" for i in came]
        if bounties.signed(cur.get("hp", 0), 16) < bounties.signed(prev.get("hp", 0), 16):
            actions.append("took damage")
        if (cur.get("stateFlags", 0) ^ prev.get("stateFlags", 0)) & 0x40:
            actions.append("pushing")
        if (cur.get("stage"), cur.get("room")) == (prev.get("stage"), prev.get("room")):
            active = lambda s: {s.get(f"enemy{i}Type") for i in range(8)  # noqa: E731
                                if s.get(f"enemy{i}Flags", 0) & 1 and bounties.signed(s.get(f"enemy{i}Hp", 0), 16) > 0}
            actions += [f"enemy {t} appears" for t in active(cur) - active(prev)]
        return found, actions


def main(paths: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    explainer = Explainer(labels="--no-labels" not in paths)
    verbose = "-v" in paths
    paths = [p for p in paths if p not in ("--no-labels", "-v")]
    total = 0
    for path in paths:
        session = bounties.parse_memory("m", zipfile.ZipFile(path).read("memory-1.bin"),
                                        bounties.load_profile_fields("re1-gog-usa-hotfix4"))
        pending, actions, asked, attributed, seen = [], [], [], [], set()
        prev = None
        for s in session.states:
            if prev is not None:
                found, acts = explainer.analyze(prev, s)
                actions += [(s.get("_ms", 0), a) for a in acts]
                for text, by_action in found:
                    key = text.split(" in ")[0]
                    if key not in seen:
                        seen.add(key)
                        pending.append((s.get("_ms", 0), s.get("gameTimer", 0) // 30, text, by_action))
            prev = s
        for ms, t, text, by_action in pending:  # the window looks both ways, as the app does before asking
            cause = next((a for at, a in actions if abs(at - ms) <= 5000), None) if by_action else None
            (attributed if cause else asked).append(f"{t // 60:3}:{t % 60:02} {text}" + (f"  <- {cause}" if cause else ""))
        print(f"== {Path(path).name}: {len(asked)} would be asked, {len(attributed)} attributed to an action")
        print("\n".join(asked))
        if verbose:
            print("-- attributed:\n" + "\n".join(attributed))
        total += len(asked)
    print(f"total {total}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
