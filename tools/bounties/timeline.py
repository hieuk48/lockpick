"""Readable timeline of a Lockpick capture: what the player did, when and where.

    python tools/bounties/timeline.py LockpickCapture-*.zip [--out timeline.md]

Decodes the capture's memory log with the profile fields, then names what changed using
data/games/re1/room-index.json (which item / enemy / door each game flag belongs to, generated from the game's room
files by tools/rdt/rdt_index.py) and data/games/re1/names.json. Lines are game time (m:ss), room, then the event.
Heuristic parts (item use vs. combine, damage grouping) say what they saw rather than guess.
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import bounties  # noqa: E402

DATA = bounties.ROOT / "data" / "games" / "re1"
SETTLE_FRAMES = bounties.ROOM_SETTLE_FRAMES
WEAPONS = set(range(1, 11))
INK_RIBBON = 47
CHARACTERS = {0: "Chris", 1: "Jill", 3: "Rebecca"}

# Flag bits whose meaning was confirmed in live sessions (data/games/re1/flag-labels.json).
# A label holds only in the games it was confirmed in ("characters"; none: both): the same flag can be another scene in
# the other character's game. An empty label hides the flag there (starting state).
FLAG_LABELS = json.loads((DATA / "flag-labels.json").read_text(encoding="utf-8"))["labels"]


def known_flag(key: str, mask: int, player: str) -> str | None:
    matches = [e for e in FLAG_LABELS if e["bank"] == key and int(e["mask"], 16) == mask]
    for e in matches:
        if player in e.get("characters", []):
            return e["label"]
    for e in matches:
        if "characters" not in e:
            return e["label"]
    return None
FLAG_BANKS = ("flagScenarioA", "flagScenarioB", "flagRoom")

# Damage per hit by enemy type and attack, from SHiiDO's RE1 damage chart (speedrun.com guides; JP PC, new game).
# Every value seen in GOG captures so far matches it, so a hit's damage names the attack.
ATTACKS = {
    0: {10: "bite", 6: "ankle bite", 5: "vomit"}, 1: {10: "bite", 6: "ankle bite", 5: "vomit"},
    17: {10: "bite", 6: "ankle bite", 5: "vomit"},
    2: {12: "jump attack"}, 5: {6: "peck", 3: "peck"}, 15: {2: "slap"},
}


def flag_index(word: int, bit: int) -> int:
    """Game flag number: bit 31 of the bank's first word is flag 0."""
    return word * 32 + (31 - bit)


def flag_is_set(state: dict, flag: int, bank: str = "flagRoomItems") -> bool:
    return bool(state.get(f"{bank}{flag // 32}", 0) >> (31 - flag % 32) & 1)


def changed_bits(before: int, after: int):
    diff = before ^ after
    for bit in range(32):
        if diff >> bit & 1:
            yield bit, bool(after >> bit & 1)


class Names:
    def __init__(self) -> None:
        names = json.loads((DATA / "names.json").read_text(encoding="utf-8"))
        self.items = {int(k): v for k, v in names["items"].items()} | {int(k): v for k, v in names["maps"].items()}
        self.items_observed = set(names["itemsObserved"])
        self.items_uncertain = {int(k) for k in names.get("itemsUncertain", {})}
        self.enemies = {int(k): v for k, v in names["enemies"].items()}
        self.rooms = json.loads((DATA / "rooms.json").read_text(encoding="utf-8"))["rooms"]
        index_path = DATA / "room-index.json"
        self.index = json.loads(index_path.read_text(encoding="utf-8")) if index_path.exists() else {"items": [], "enemies": [], "doors": []}

    def item(self, item_id: int, quantity: int | None = None) -> str:
        name = self.items.get(item_id)
        if name is None:
            name = f"document/map 0x{item_id:02X}" if item_id >= 0x4E else f"item 0x{item_id:02X}"
        elif item_id not in self.items_observed or item_id in self.items_uncertain:
            name += "?"
        return f"{name} ×{quantity}" if quantity and quantity > 1 else name

    def room(self, room_id: str) -> str:
        """'105 Dining room' where the room is identified, else '10C (Mansion 1F)'."""
        info = self.rooms.get(room_id, {})
        return f"{room_id} {info['name']}" if "name" in info else f"{room_id} ({info.get('area', '?')})"

    def enemy(self, enemy_type: int) -> str:
        return self.enemies.get(enemy_type, f"enemy type {enemy_type}")

    def lookup(self, kind: str, flag: int, room: str, player: str, key: str = "flag") -> list[dict]:
        entries = [e for e in self.index[kind] if e.get(key) == flag and e["player"] in (player, "both")]
        here = [e for e in entries if e["room"] == room or e.get("target") == room]
        return here or entries


def inventory(state: dict, prefix: str, count: int) -> Counter:
    totals: Counter = Counter()
    for i in range(count):
        v = state.get(f"{prefix}{i}", 0)
        if v & 0xFF:
            totals[v & 0xFF] += max(v >> 8, 1)
    return totals


def ids(state: dict, prefix: str, count: int) -> set[int]:
    return {state.get(f"{prefix}{i}", 0) & 0xFF for i in range(count)} - {0}


class Timeline:
    def __init__(self, names: Names) -> None:
        self.names = names
        self.lines: list[tuple[int | None, str]] = []  # (wall ms, text)
        self.segment_start: int | None = None

    def emit(self, state: dict, text: str) -> None:
        t = state.get("gameTimer", 0) // 30
        room = f"{state.get('stage', 0) + 1}{state.get('room', 0):02X}"
        self.lines.append((state.get("_ms"), f"`{t // 60:3}:{t % 60:02}` {room} — {text}"))

    def header(self, state: dict | None, text: str) -> None:
        self.lines.append((state.get("_ms") if state else None, f"\n**{text}**\n"))

    def run(self, session: bounties.Session) -> None:
        names = self.names
        prev = None
        entered = 0
        down: dict[int, int] = {}
        fired: Counter = Counter()
        damage = None  # (first state, hp before, hits)
        pushing = None
        for position, s in enumerate(session.states):
            if not bounties.in_game(s):
                if prev is not None:
                    self.flush_fired(prev, fired)
                    self.flush_damage(damage)
                    damage = None
                    hp = bounties.signed(s.get("hp", 0), 16)
                    self.emit(prev, "died" if hp <= 0 and s.get("mainMenu") == 0 else "left the game (title screen / load)")
                prev = None
                continue
            char = s.get("character", 0) & 3
            player = "jill" if char == 1 else "chris"
            room = f"{s.get('stage', 0) + 1}{s.get('room', 0):02X}"
            t = s.get("gameTimer", 0)
            if prev is not None and t + 30 < prev.get("gameTimer", 0):
                self.flush_fired(prev, fired)
                self.flush_damage(damage)
                damage = None
                prev = None
            if prev is None and self.segment_start is not None and all(
                    "— start" in text or "— entered " in text or "— left the game" in text or text.startswith("\n**")
                    for _, text in self.lines[self.segment_start:]):
                del self.lines[self.segment_start:]  # only a flash of old values while the next game loaded
            if prev is None:
                self.segment_start = len(self.lines)
                kind = "New game" if t < 60 else f"Loaded save / continued at {t // 1800}:{t // 30 % 60:02}"
                self.header(s, f"{kind} as {CHARACTERS.get(char, f'character {char}')} (HP {bounties.signed(s.get('hp', 0), 16)}/{s.get('maxHp')})")
                entered = t
                down = {}
                self.listed_room = False
                prev = s
                self.emit(s, f"start in {names.room(room)}")
                continue
            prev_room = f"{prev.get('stage', 0) + 1}{prev.get('room', 0):02X}"
            if room != prev_room:
                self.flush_fired(prev, fired)
                self.flush_damage(damage)
                damage = None
                entered = t
                down = {}
                self.listed_room = False
                self.emit(s, f"entered {names.room(room)} from {prev_room}")
            settled = t - entered >= SETTLE_FRAMES
            if settled and entered is not None and not self.listed_room:
                self.listed_room = True
                present = Counter(names.enemy(s.get(f"enemy{i}Type", -1)) for i in range(8)
                                  if s.get(f"enemy{i}Flags", 0) & 1 and bounties.signed(s.get(f"enemy{i}Hp", 0), 16) > 0)
                if present:
                    self.emit(s, "here: " + ", ".join(f"{n}× {e}" if n > 1 else e for e, n in present.most_common()))

            # Pickups, kills and unlocks recorded in the game's flag banks.
            picked: set[int] = set()
            returned: set[int] = set()
            for word in range(8):
                key = f"flagRoomItems{word}"
                for bit, now_set in changed_bits(prev.get(key, 0), s.get(key, 0)):
                    flag = flag_index(word, bit)
                    found = names.lookup("items", flag, room, player)
                    if now_set and found:
                        returned.add(found[0]["item"])
                        self.emit(s, f"put {names.item(found[0]['item'])} back in its place")
                    if not now_set:
                        what = names.item(found[0]["item"], found[0]["quantity"]) if found else f"item flag {flag}"
                        if found:
                            picked.add(found[0]["item"])
                        verb = "read/took" if found and found[0]["item"] >= 0x4E else "picked up"
                        self.emit(s, f"{verb} {what}" + (f" (placed in room {found[0]['room']})" if found and found[0]["room"] != room else ""))
            for word in range(2):
                key = f"flagLocks{word}"
                for bit, now_set in changed_bits(prev.get(key, 0), s.get(key, 0)):
                    flag = flag_index(word, bit)
                    doors = names.lookup("doors", flag, room, player, key="lockFlag")
                    where = ", ".join(sorted({f"{d['room']}→{d['target']}" for d in doors})) or f"lock flag {flag}"
                    keys = sorted({names.item(d["key"]) for d in doors if d.get("key")})
                    self.emit(s, f"{'unlocked' if now_set else 'locked'} door {where}" + (f" ({', '.join(keys)} lock)" if keys else ""))
            for bank in FLAG_BANKS:
                for word in range(8):
                    key = f"{bank}{word}"
                    if key not in s:
                        continue
                    for bit, now_set in changed_bits(prev.get(key, 0), s.get(key, 0)):
                        label = known_flag(key, 1 << bit, player)
                        if label and now_set:
                            self.emit(s, label)

            # Enemies: falls (HP to 0 or below) and getting back up, once the room has settled.
            if settled:
                for slot in range(8):
                    if not s.get(f"enemy{slot}Flags", 0) & 1:
                        continue
                    hp = bounties.signed(s.get(f"enemy{slot}Hp", 0), 16)
                    before = bounties.signed(prev.get(f"enemy{slot}Hp", 0), 16)
                    etype = s.get(f"enemy{slot}Type", -1)
                    if before > 0 >= hp and slot not in down:
                        down[slot] = etype
                        self.emit(s, f"killed {names.enemy(etype)}")
                    elif hp > 0 and slot in down:
                        del down[slot]
                        self.emit(s, f"{names.enemy(etype)} got back up (HP {hp})")

            # Player HP: damage is grouped per attack burst; heals are reported with the item used.
            hp, php = bounties.signed(s.get("hp", 0), 16), bounties.signed(prev.get("hp", 0), 16)
            if hp < php:
                attack = self.attack(s, php - hp)
                if damage and t - damage[0].get("gameTimer", 0) < 150:  # same attack burst (5 s)
                    damage[2] = hp
                    damage[3][attack] += 1
                else:
                    self.flush_damage(damage)
                    damage = [s, php, hp, Counter({attack: 1})]

            # Inventory and item box.
            # An item about to be put back in its place (its pickup flag sets a frame later) isn't "used".
            upcoming = session.states[position + 1:position + 6]
            returning = {e["item"] for e in names.index["items"] if e["room"] == room and e["player"] in (player, "both")
                         and any(flag_is_set(u, e["flag"]) and not flag_is_set(s, e["flag"]) for u in upcoming)}
            self.inventory_changes(prev, s, fired, hp - php, picked | returned | returning, 8 if char == 1 else 6)

            # Pushing an object sets stateFlags bit 0x40 (statue in 202, stairs in 107).
            sf, psf = s.get("stateFlags", 0), prev.get("stateFlags", 0)
            if sf & 0x40 and not psf & 0x40:
                pushing = s
            elif psf & 0x40 and not sf & 0x40 and pushing is not None:
                seconds = (s.get("_ms", 0) - pushing.get("_ms", 0)) / 1000
                if seconds >= 0.5:
                    self.emit(pushing, f"pushed something ({seconds:.1f} s)")
                pushing = None
            prev = s
        if prev is not None:
            self.flush_fired(prev, fired)
            self.flush_damage(damage)

    def flush_damage(self, damage) -> None:
        if damage:
            first, before, after, attacks = damage
            what = ", ".join(f"{a} ×{n}" if n > 1 else a for a, n in attacks.most_common())
            self.emit(first, f"took damage: HP {before} → {after} ({what})")

    def attack(self, state: dict, amount: int) -> str:
        """Names a hit from its damage and the enemies in the room ('Zombie ankle bite')."""
        present = {state.get(f"enemy{i}Type", -1) for i in range(8) if state.get(f"enemy{i}Flags", 0) & 1}
        matches = sorted({(self.names.enemy(e), ATTACKS[e][amount]) for e in present if amount in ATTACKS.get(e, {})})
        if len(matches) == 1:
            enemy, attack = matches[0]
            return f"{enemy.split(' (')[0]} {attack} ({amount})"
        return f"{amount} HP"

    def flush_fired(self, state: dict, fired: Counter) -> None:
        for weapon, rounds in fired.items():
            self.emit(state, f"fired {self.names.item(weapon)} ×{rounds}")
        fired.clear()

    def inventory_changes(self, prev: dict, s: dict, fired: Counter, hp_change: int, picked: set[int], slots: int) -> None:
        names = self.names
        inv_b, inv_a = inventory(prev, "inv", slots), inventory(s, "inv", slots)
        box_b, box_a = inventory(prev, "box", 48), inventory(s, "box", 48)
        if inv_b == inv_a and box_b == box_a:
            return
        gone = {i for i in inv_b if i not in inv_a}
        new = {i for i in inv_a if i not in inv_b}
        to_box = {i for i in gone if box_a.get(i, 0) > box_b.get(i, 0)}
        from_box = {i for i in new if box_b.get(i, 0) > box_a.get(i, 0)}
        for i in sorted(to_box):
            self.emit(s, f"put {names.item(i, inv_b[i] if inv_b[i] > 1 else None)} in the item box")
        for i in sorted(from_box):
            self.emit(s, f"took {names.item(i, inv_a[i] if inv_a[i] > 1 else None)} from the item box")
        gone -= to_box
        new -= from_box
        # Quantity changes of items still held.
        reload_ammo: set[int] = set()
        for i in sorted(i for i in set(inv_b) & set(inv_a) if i in WEAPONS and inv_a[i] > inv_b[i]):
            used = sorted(j for j in gone if j not in WEAPONS) or sorted(
                j for j in inv_b if j not in WEAPONS and inv_a.get(j, 0) < inv_b[j] and box_a.get(j, 0) <= box_b.get(j, 0))
            if i in picked:
                continue
            self.emit(s, f"reloaded {names.item(i)} to {inv_a[i]}" + (f" with {names.item(used[0])}" if used else ""))
            reload_ammo |= set(used[:1])
        gone -= reload_ammo
        for i in sorted(set(inv_b) & set(inv_a)):
            delta = inv_a[i] - inv_b[i]
            box_delta = box_a.get(i, 0) - box_b.get(i, 0)
            if i in reload_ammo or i in picked or (i in WEAPONS and delta > 0):
                continue
            if delta and box_delta == -delta:
                self.emit(s, f"{'took' if delta > 0 else 'put'} {names.item(i, abs(delta))} {'from' if delta > 0 else 'in'} the item box")
            elif delta < 0 and i in WEAPONS:
                fired[i] += -delta
            elif delta < 0 and i == INK_RIBBON:
                self.emit(s, f"saved the game (used an ink ribbon, {inv_a[i]} left)")
            elif delta < 0 and hp_change > 0:
                self.emit(s, f"used {names.item(i)}: HP +{hp_change}")
            elif delta != 0:
                self.emit(s, f"{names.item(i)}: {inv_b[i]} → {inv_a[i]}")
        new -= picked
        gone -= picked  # put back in its place (reported from the item flag)
        if gone and new:
            self.emit(s, f"combined {' + '.join(names.item(i) for i in sorted(gone))} → {', '.join(names.item(i, inv_a[i] if inv_a[i] > 1 else None) for i in sorted(new))}")
            return
        for i in sorted(gone):
            if i == INK_RIBBON:
                self.emit(s, "saved the game (used the last ink ribbon)")
            elif hp_change > 0:
                self.emit(s, f"used {names.item(i)}: HP +{hp_change}")
            elif i in WEAPONS:
                self.emit(s, f"{names.item(i)} left the inventory")
            else:
                self.emit(s, f"used {names.item(i)}")
        for i in sorted(new - picked):
            if not any(e["item"] == i for e in names.index["items"]):
                self.emit(s, f"received {names.item(i, inv_a[i] if inv_a[i] > 1 else None)}")


def render(capture: bounties.Capture, names: Names) -> str:
    timeline = Timeline(names)
    for session in capture.sessions:
        timeline.run(session)
    for m in capture.markers:
        try:
            ms = int(datetime.fromisoformat(m["atFull"]).timestamp() * 1000) if m.get("atFull") else None
        except ValueError:
            ms = None
        note = f": {m['note']}" if m["note"] else ""
        where = f"{m['at']}{' room ' + m['room'] if m['room'] else ''}"
        if m["kind"] == "auto":
            timeline.lines.append((ms, f"🔗 {where} — {m['note']} → {'; '.join(m['changes'])}"))
        elif m["kind"] in ("answer", "skipped"):
            answer = f"**{m['note']}**" if m["note"] else "(skipped)"
            timeline.lines.append((ms, f"❓ **Question #{m['number']}** {where} — {answer} — about: {'; '.join(m['changes'])}"))
        else:
            timeline.lines.append((ms, f"🚩 **Marker #{m['number']}** {where}{note}"))
    ordered = sorted(enumerate(timeline.lines), key=lambda x: (x[1][0] is None, x[1][0] or 0, x[0]))
    out = [f"# Timeline — {capture.name}", ""]
    out += [text for _, (_, text) in ordered]
    out += ["", "_Names marked ? are not yet confirmed in play. Times are game time (m:ss)._"]
    return "\n".join(out) + "\n"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("capture", type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args(argv)
    capture = bounties.read_capture(args.capture)
    text = render(capture, Names())
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
