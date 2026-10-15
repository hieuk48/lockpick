r"""The dev-only test suite: everything the 0.2–0.7 work needs verified live, in one run, written to JSON. Every step
reads the game's state from memory (message state, menus, inventory, enemy slots, flag banks) before it presses
anything; setups use the dev harness (door warps, give, hp, story flags) instead of replaying the story.

    python -I tools/autoplay/suite.py <out-dir> <save-backup-dir> [test ...] [--character jill|chris]

Tests: timer, bands, healing, combine, full-inventory, poison, yawn-poison, weapons, enemy-hits, kills, doors,
send-back, cutscenes. Each test starts its own new game (a fresh state) and writes <out>/<test>-<character>.json as it
goes; a failed step is logged and the test moves on. SAVE/ stays read-only; the saves are checked at the end.
"""
from __future__ import annotations

import json
import struct
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import ap  # noqa: E402
import combat  # noqa: E402
import doors  # noqa: E402
import pickup  # noqa: E402
import rdt_map  # noqa: E402
import route  # noqa: E402
import survey  # noqa: E402

DATA = Path(__file__).resolve().parents[2] / "data" / "games" / "re1"
MAX_HP = {"jill": 96, "chris": 140}
STORY_A, STORY_B, ENEMY_FLAGS, LOCKS = 0xBE98C0, 0xBE9854, 0xBE987C, 0xBE9874
DAMAGE_TABLE = 0x4BB698  # first-run weapon damage: 12-byte records, i16 at +6, index (weapon − 1) + type·10


class Context:
    def __init__(self, out: Path, character: str, name: str, first_type: int = 0) -> None:
        self.out, self.character, self.name, self.first_type = out, character, name, first_type
        self.results: list[dict] = []
        self.logfile = (out / "suite.log").open("a", encoding="utf-8")
        self.route = route.Route(character, self.log)

    def log(self, text: str) -> None:
        line = f"{time.strftime('%H:%M:%S')} [{self.name}/{self.character}] {text}"
        print(line, flush=True)
        self.logfile.write(line + "\n")
        self.logfile.flush()

    def add(self, record: dict) -> None:
        self.results.append(record)
        (self.out / f"{self.name}-{self.character}.json").write_text(json.dumps(self.results, indent=1) + "\n",
                                                                     encoding="utf-8")
        self.log(json.dumps(record)[:400])

    def fresh(self) -> None:
        """A new game for this character (the server restarts the game: a clean state)."""
        ap.serve()
        self.log(f"new game in {ap.new_game(self.character):.0f} s")
        self.route = route.Route(self.character, self.log)

    def recover(self) -> None:
        """Back to control by reading the state: messages closed, prompts answered no, menus closed, cutscenes run
        out; a new game when that doesn't work (or the game/server is gone)."""
        try:
            end = time.time() + 40
            while time.time() < end and not ap.in_control():
                state = ap.message_state()
                if state == ap.PROMPT:
                    ap.answer(False)
                elif state in (ap.PAGE, ap.WAITING):
                    ap.dismiss()
                elif ap.status_open():
                    ap.close_menu()
                else:
                    ap.settle(10)
            if ap.in_control():
                return
        except ap.Failed:
            pass
        try:
            shot = self.out / "shots" / f"stuck-{time.strftime('%H%M%S')}.png"
            shot.parent.mkdir(exist_ok=True)
            ap.send(f"shot {shot} 640", check=False)
            self.log(f"stuck: {ap.state()} flags {ap.flags():#x} message {ap.message_state()} {ap.message_text()!r} "
                     f"status screen {ap.status_open()} shot {shot}")
        except Exception:  # noqa: BLE001 - the game or the server may be gone
            pass
        self.log("no control after recovery: starting a new game")
        self.fresh()

    def step(self, what: str, action) -> dict | None:
        try:
            if not ap.in_control():
                self.recover()
            return action()
        except Exception as error:  # noqa: BLE001 - one failed step must not end the test
            self.add({"step": what, "error": f"{type(error).__name__}: {error}"[:600]})
            self.log(traceback.format_exc()[-800:])
            try:
                self.recover()
            except Exception:  # noqa: BLE001
                self.log(traceback.format_exc()[-400:])
            return None


def flag_set(bank: int, n: int) -> bool:
    return bool(struct.unpack("<I", ap.peek(bank + n // 32 * 4, 4))[0] & (0x80000000 >> (n % 32)))


def bank_flags(bank: int, words: int) -> set[int]:
    return route.set_flags(ap.peek(bank, words * 4))


def table_damage(weapon: int, enemy_type: int) -> int:
    return struct.unpack("<h", ap.peek(DAMAGE_TABLE + ((weapon - 1) + enemy_type * 10) * 12 + 6, 2))[0]


# --- timer, health bands -------------------------------------------------------------------------------------------

def t_timer(c: Context) -> None:
    c.fresh()
    a, t0 = ap.igt(), time.time()
    time.sleep(5)
    b, t1 = ap.igt(), time.time()
    c.add({"igtFrames": b - a, "seconds": round(t1 - t0, 2), "framesPerSecond": round((b - a) / (t1 - t0), 2)})


def t_bands(c: Context) -> None:
    """Status-screen ECG band at HP values around each boundary: (HP − 1) / (max >> 2) per the decomp."""
    c.fresh()
    top = MAX_HP[c.character]
    q = top >> 2
    for hp in sorted({1, q, q + 1, 2 * q, 2 * q + 1, 3 * q, 3 * q + 1, top}):
        def one(hp=hp):
            ap.send(f"hp {hp}")
            band = ap.status_band()
            c.add({"hp": hp, "band": band, "expected": (hp - 1) // q})
        c.step(f"band at {hp}", one)
    ap.send("heal")


# --- healing, combining, full inventory ---------------------------------------------------------------------------

HEALING = [68, 71, 74, 70, 72, 73, 75, 65, 67, 69]  # green, GG, GGG, GR, GB, GRB, GGB, spray, red, blue


def t_healing(c: Context) -> None:
    c.fresh()
    for item in HEALING:
        def one(item=item):
            ap.send("inv-clear 3", f"give {item}", "hp 10")
            before = ap.hp()
            info = ap.use_item(item)
            said = ap.dismiss() if ap.message_active() else []
            ap.close_menu()
            c.add({"item": item, "name": ap.item_name(item, shown=False), "hpBefore": before, "hpAfter": ap.hp(),
                   "healed": ap.hp() - before, "consumed": item not in [i for i, _, _ in ap.inventory()],
                   "said": said, "menu": info})
        c.step(f"heal {item}", one)
    ap.send("heal")


COMBINATIONS = [(68, 68, 71), (67, 68, 70), (68, 69, 72), (71, 68, 74), (71, 69, 75), (70, 69, 73), (72, 67, 73),
                (72, 68, 75), (11, 2, None), (74, 68, None)]  # clip + Beretta reloads; GGG takes nothing more


def t_combine(c: Context) -> None:
    c.fresh()
    for first, second, expected in COMBINATIONS:
        def one(first=first, second=second, expected=expected):
            ap.send("inv-clear 3")
            ap.send(f"give {first} {15 if first == 11 else 1}")
            if second != 2:
                ap.send(f"give {second}")
            result = ap.combine(first, second)
            after = [i for i, _, _ in result["after"]]
            c.add({"first": first, "second": second, "expected": expected, "after": result["after"],
                   "made": expected in after if expected else None, "said": result["said"]})
        c.step(f"combine {first}+{second}", one)


def armed(room: str, item: int | None = None) -> list[dict]:
    return [a for a in pickup.active_items() if item is None or a["item"] == item]


def t_full_inventory(c: Context) -> None:
    """Fill every slot, then try a pickup (expect "You can't carry any more items." and the item left in the room); a
    stackable ammo pickup into a matching stack should still be taken."""
    c.fresh()
    index = json.loads((DATA / "room-index.json").read_text(encoding="utf-8"))["items"]
    mine = [e for e in index if e["player"] in (c.character, "both") and e["room"][0] in "12"]
    limit = 8 if c.character == "jill" else 6
    for kind, wanted in (("single item", lambda e: e["item"] in (31, 39, 41, 63, 64)), ("ammo into a stack", lambda e: e["item"] == 11)):
        def one(kind=kind, wanted=wanted):
            for entry in [e for e in mine if wanted(e)]:
                c.route.enter(entry["room"])
                target = armed(entry["room"], entry["item"])
                if not target:
                    continue
                ap.send("inv-clear 3")
                if kind == "ammo into a stack":
                    ap.send("give 11 5")
                while len(ap.inventory()) < limit:
                    ap.send("give 68")
                before = ap.inventory()
                result = pickup.try_pickup({**target[0]}, rdt_map.load(survey.INSTALL, entry["room"], c.character),
                                           clear_inventory=False)
                c.add({"case": kind, "room": entry["room"], "item": entry["item"], "slots": limit, "before": before,
                       "after": ap.inventory(), "message": ap.message_text(), "result": result})
                return
            c.add({"case": kind, "error": "no armed pickup found"})
        c.step(kind, one)


# --- poison ----------------------------------------------------------------------------------------------------------

def wait_for_status(c: Context, mask: int, seconds: float) -> dict:
    """Stands near the room's first live enemy and lets it attack until the player's status has `mask`; logs hits."""
    room = rdt_map.load(survey.INSTALL, c.route.room, c.character)
    hits, last, end = [], ap.hp(), time.time() + seconds
    enemy = combat.live_enemy()
    if enemy:
        combat.stand_off(room, enemy, 900, 1800)
    while time.time() < end:
        now = ap.hp()
        if now < last:
            hits.append({"damage": last - now, "status": ap.peek(ap.PLAYER_STATUS, 1)[0], "igt": ap.igt()})
        last = now
        if ap.peek(ap.PLAYER_STATUS, 1)[0] & mask:
            return {"poisoned": True, "hits": hits, "status": ap.peek(ap.PLAYER_STATUS, 1)[0]}
        if now < 40:
            ap.send("heal")
            last = ap.hp()
        enemy = combat.live_enemy()
        if enemy and combat.distance(*(combat.player_xz() or (0, 0)), enemy["x"], enemy["z"]) > 2500:
            combat.stand_off(room, enemy, 900, 1800)
        time.sleep(0.1)
    return {"poisoned": False, "hits": hits, "status": ap.peek(ap.PLAYER_STATUS, 1)[0]}


def poison_ticks(seconds: float = 14) -> list[dict]:
    """HP drops while poisoned, with the in-game frame count between them (expected: −2 every 121 frames)."""
    ticks, last, end = [], ap.hp(), time.time() + seconds
    while time.time() < end:
        now = ap.hp()
        if now != last:
            ticks.append({"hp": now, "change": now - last, "igt": ap.igt()})
            last = now
        time.sleep(0.05)
    return ticks


def cure(c: Context, item: int) -> dict:
    ap.send(f"give {item}")
    before = ap.peek(ap.PLAYER_STATUS, 1)[0]
    ap.use_item(item)
    said = ap.dismiss() if ap.message_active() else []
    ap.close_menu()
    return {"item": item, "statusBefore": before, "statusAfter": ap.peek(ap.PLAYER_STATUS, 1)[0], "said": said}


def t_poison(c: Context) -> None:
    """Wasps (408): the poison bit (0x02) — the adders in 301 and Yawn never struck after a warp (2026-10-14) — then the
    ticks (−2 HP per poison-timer wrap), the poison band, the serum (shouldn't cure) and the blue herb."""
    c.fresh()

    def run():
        before = ap.peek(ap.PLAYER_STATUS, 1)[0]
        c.route.enter("408", freeze=False)
        got = wait_for_status(c, 0x02, 150)
        c.add({"step": "poisoned", "statusBefore": before, **got})
        if not got["poisoned"]:
            return
        ap.send("freeze-enemies")
        c.add({"step": "ticks", "ticks": poison_ticks()})
        c.add({"step": "band while poisoned", "band": ap.status_band()})
        c.add({"step": "serum", **cure(c, 66)})
        c.add({"step": "blue herb", **cure(c, 69)})
    c.step("wasp poison", run)


def t_yawn_poison(c: Context) -> None:
    """Yawn (210): Yawn poison (0x20, story B 0x43) — the blue herb shouldn't cure it, the serum should."""
    c.fresh()

    def run():
        c.route.enter("210", freeze=False)
        got = wait_for_status(c, 0x20, 150)
        c.add({"step": "bitten", **got, "B67": flag_set(STORY_B, 0x43)})
        if not got["poisoned"]:
            return
        c.route.enter("20E")
        ap.send("freeze-enemies")
        c.add({"step": "ticks", "ticks": poison_ticks()})
        c.add({"step": "blue herb", **cure(c, 69)})
        c.add({"step": "serum", **cure(c, 66), "B67": flag_set(STORY_B, 0x43)})
    c.step("yawn poison", run)


# --- combat ------------------------------------------------------------------------------------------------------------

ZOMBIE_ROOMS = {"jill": ["20E", "201", "202", "101", "107", "10B", "10E", "111", "401", "407", "503", "50C", "702", "716"],
                "chris": ["20E", "201", "202", "101", "103", "107", "10B", "10E", "111", "401", "407", "503", "50C", "702"]}
WEAPON_ORDER = [(2, 15), (1, 0), (6, 100), (3, 7), (5, 6), (7, 6), (8, 6), (9, 6), (10, 4)]


# Black Tiger (204 HP), Tyrant (220), Yawn 2 (300), Plant 42 (140): the magnum, bazooka and rocket kill zombies
# outright with special deaths (HP set to −300). A killing hit still measures (HP after = before − damage) unless it
# is one of those. Yawn in 210 sits outside the room's floor until it wakes, so it can't be shot from a warp.
HEAVY_ROOMS = ["30C", "513", "70C", "40C", "30B", "601"]


def t_weapons(c: Context) -> None:
    """One hit per weapon on an enemy that survives it — zombies for the light weapons, high-HP enemies for the magnum,
    bazooka and rocket (no zombie survives them) — compared with the game's damage table for that enemy type."""
    c.fresh()
    pools = {"light": list(ZOMBIE_ROOMS[c.character]), "heavy": list(HEAVY_ROOMS)}
    room = None
    for weapon, quantity in WEAPON_ORDER:
        def one(weapon=weapon, quantity=quantity):
            nonlocal room
            pool = pools["heavy" if weapon in (5, 7, 8, 9, 10) else "light"]
            for _ in range(len(pool) + 1):
                target = combat.live_enemy()
                heavy = weapon in (5, 7, 8, 9, 10)
                survives = target is not None and (heavy or target["hp"] > table_damage(weapon, target["type"]))
                suitable = target is not None and (heavy or target["type"] in (0, 1, 17))
                reach = int(combat.weapon_range(weapon) * 0.9)
                if room is not None and survives and suitable and combat.reachable(room, target, reach):
                    break
                if not pool:
                    raise ap.Failed("no enemy left that survives this weapon")
                c.route.enter(pool.pop(0), freeze=False)
                room = rdt_map.load(survey.INSTALL, c.route.room, c.character)
            ap.send("inv-clear 2")
            if not combat.equip(weapon, quantity, c.log, room):
                raise ap.Failed("not equipped")
            shots = combat.shoot(room, 2 if weapon != 1 else 3, c.log)
            types = sorted({s["type"] for s in shots})
            c.add({"weapon": weapon, "name": ap.item_name(weapon, shown=False), "room": c.route.room,
                   "table": {t: table_damage(weapon, t) for t in types}, "shots": shots,
                   "measured": [s["damage"] for s in shots if s["damage"]]})
        c.step(f"weapon {weapon}", one)


ENEMY_ROOMS = [("20E", 0), ("10A", 17), ("108", 2), ("117", 5), ("30B", 6), ("301", 10), ("408", 7), ("404", 3),
               ("50F", 9), ("40C", 8), ("30C", 4), ("210", 13), ("513", 12), ("40E", 11), ("70C", 18), ("505", 1)]


def t_enemy_hits(c: Context) -> None:
    c.fresh()
    for room, kind in ENEMY_ROOMS:
        c.step(f"hits in {room}", lambda room=room, kind=kind: c.add(
            {"expectedType": kind, **(combat.enemy_hits(c.character, [room], None, c.log, 25) or [{}])[0]}))


def t_kills(c: Context) -> None:
    """Rocket launcher (900) on each enemy type's room: HP after, and which kill flags (flagEnemies) turned on."""
    c.fresh()
    for room, kind in ENEMY_ROOMS:
        def one(room=room, kind=kind):
            c.route.enter(room, freeze=False)
            plan = rdt_map.load(survey.INSTALL, room, c.character)
            before = bank_flags(ENEMY_FLAGS, 8)
            ap.send("inv-clear 2")
            combat.equip(10, 8, c.log, plan)
            types = [e["type"] for e in ap.enemies()]
            shots = combat.shoot(plan, 8, c.log)
            ap.send("wait 2000")
            c.add({"room": room, "expectedType": kind, "types": types, "shots": shots,
                   "left": [(e["type"], e["hp"]) for e in ap.enemies()],
                   "killFlagsSet": sorted(bank_flags(ENEMY_FLAGS, 8) - before)})
        c.step(f"kills in {room}", one)


# --- doors, send-back, cutscenes ---------------------------------------------------------------------------------------

def t_doors(c: Context, plan: Path, only_locks: set[int] | None = None) -> None:
    """One door per lock (the mining plan): give the key(s), examine any key that shows a generic name, open the door,
    record what the game says, the lock bits set and where the player ends up."""
    c.fresh()
    locks = json.loads(plan.read_text(encoding="utf-8"))["byLock"][c.character]
    if only_locks:
        locks = [lock for lock in locks if lock["lockFlag"] in only_locks]
    for lock in locks:
        test = lock.get("test")
        if not test:
            continue

        def one(lock=lock, test=test):
            c.route.enter(test["room"])
            ap.send("inv-clear 3")
            for key in lock.get("keyIds") or []:
                # Jill opens the Sword-key locks with her lockpick (item 49): with the Sword key the game says
                # "It's locked. Use the lockpick to open the door." (2026-10-14).
                key = 49 if c.character == "jill" and key == 51 else key
                if 1 <= key <= 0x4D:
                    ap.send(f"give {key}")
                    if ap.examined(key) is False:
                        ap.examine(key)
            locks_before = bank_flags(LOCKS, 2)
            ok = c.route.open_door(test["to"])
            c.add({"lock": lock["lockFlag"], "kind": lock.get("kind"), "room": test["room"], "to": test["to"],
                   "keys": lock.get("keyIds"), "opened": ok, "now": ap.state().get("room"),
                   "lockBitsSet": sorted(bank_flags(LOCKS, 2) - locks_before), "inventory": ap.inventory()})
        c.step(f"lock {lock['lockFlag']}", one)


SEND_BACK = [
    {"name": "front door, first try (FMV 5)", "chars": ["jill", "chris"], "room": "106", "flags": [("B", 4, 0)],
     "zone": [14600, 800, 5200, 2200], "fmvSkip": True},
    {"name": "front door, afterwards", "chars": ["jill", "chris"], "room": "106", "flags": [("B", 4, 1)],
     "zone": [14600, 800, 5200, 2200]},
    {"name": "search the hall first (106)", "chars": ["jill"], "room": "106", "flags": [("B", 2, 1), ("B", 3, 0)],
     "doors": ["107", "111", "105"]},
    {"name": "search the hall first (203)", "chars": ["jill"], "room": "203", "flags": [("B", 5, 0)],
     "doors": ["211", "204", "202"]},
    {"name": "get the serum (20D)", "chars": ["chris"], "room": "20D", "flags": [("B", 95, 1), ("B", 94, 0)],
     "doors": ["20E"]},
    {"name": "can't leave Barry (513)", "chars": ["jill"], "room": "513", "flags": [("B", 91, 1)], "doors": ["514"]},
    {"name": "elevator power (511)", "chars": ["jill", "chris"], "room": "511", "flags": [("A", 45, 0)], "doors": None},
    {"name": "hole in 307", "chars": ["jill", "chris"], "room": "307", "flags": [("B", 71, 0)], "doors": None},
    {"name": "no need downstairs (303)", "chars": ["jill", "chris"], "room": "303", "flags": [], "doors": ["51D"]},
]


def watch(c: Context, seconds: float = 90, skip_fmv_after: float | None = None) -> dict:
    """Follows whatever plays until the player has control: cutscene bits seen, every message (read, then closed),
    FMV time; fast-forwards only real cutscenes. With skip_fmv_after, presses A that long into an FMV."""
    seen, said, fmv_start, fmv_frames, calm, end = 0, [], None, None, 0, time.time() + seconds
    pointer = ap.message_pointer()
    while time.time() < end:
        f = ap.flags()
        seen |= f & (ap.CUTSCENE | 0x40000)
        if f & 0x40000 and fmv_start is None:
            fmv_start = time.time()
        if fmv_start and not f & 0x40000 and fmv_frames is None:
            fmv_frames = round((time.time() - fmv_start) * 30)
        if skip_fmv_after and fmv_start and f & 0x40000 and time.time() - fmv_start > skip_fmv_after:
            ap.send("press A 120")
            skip_fmv_after = None
        if ap.message_pointer() != pointer and ap.message_active():
            pointer = ap.message_pointer()
        state = ap.message_state()
        if state == ap.PROMPT:
            said.append(ap.message_text() + " (prompt: answered no)")
            ap.answer(False)
        elif state in (ap.PAGE, ap.WAITING):
            said += ap.dismiss()
        if ap.in_control():
            calm += 1
            if calm >= 4:
                break
        else:
            calm = 0
        ap.send("speed 4" if f & ap.CUTSCENE and not f & 0x40000 else "speed normal", "wait 250")
    ap.send("speed normal")
    return {"cutsceneBits": hex(seen), "said": said, "fmvSeconds": None if fmv_start is None else
            round((fmv_frames or round((time.time() - fmv_start) * 30)) / 30, 1), "inControl": ap.in_control()}


def set_flags(flags: list[tuple[str, int, int]]) -> None:
    for bank, n, on in flags:
        ap.send(f"flag {bank} {n} {on}")


def t_send_back(c: Context) -> None:
    c.fresh()
    for case in SEND_BACK:
        if c.character not in case["chars"]:
            continue

        def one(case=case):
            set_flags(case["flags"])
            c.route.enter(case["room"])
            result = {"case": case["name"], "room": case["room"], "flags": case["flags"]}
            if case.get("zone"):
                plan = rdt_map.load(survey.INSTALL, case["room"], c.character)
                zx, zz, zw, zd = case["zone"]
                # Inside the zone first (the front door's zone runs along the wall; from outside, the probe can reach a
                # painting's zone first — 2026-10-14), facing each way, then the usual spots around it.
                inside = [(int(zx + zw * fx), int(zz + zd / 2), a, False) for fx in (0.5, 0.3, 0.7) for a in (1024, 3072, 0, 2048)]
                for x, z, angle, _ in inside + pickup.spots({"zone": case["zone"]}, plan):
                    ap.send(f"place {x} {z} {angle}", "wait 400")
                    if not ap.in_control():
                        continue
                    ap.send("press A 150", "wait 600")
                    if not ap.in_control():
                        result.update(watch(c, 120, 4 if case.get("fmvSkip") else None))
                        break
            for target in case.get("doors") or []:
                before_room = c.route.room
                c.route.open_door(target)
                result.setdefault("doors", {})[target] = {"now": ap.state().get("room"), "said": ap.message_text()}
                if ap.state().get("room") != before_room:
                    c.route.enter(case["room"])
            if case.get("doors") is None and not case.get("zone"):
                result["note"] = "room entered; the blocked spot is a message zone — see the scripts"
            c.add(result)
        c.step(case["name"], one)


def flag_ref(text: str) -> tuple[str, int] | None:
    for bank, prefix in (("A", "flagScenarioA["), ("B", "flagScenarioB[")):
        if text.startswith(prefix):
            return bank, int(text[len(prefix):-1])
    return None


def t_cutscenes(c: Context, plan: Path, limit: int | None = None) -> None:
    """Each planned cutscene: set/clear its story flags, warp in, follow it (messages read, FMV timed), then check the
    flags it should set. Conditions that aren't story flags (room-script sys bits, state bytes) are left as they are and
    recorded."""
    entries = [e for e in json.loads(plan.read_text(encoding="utf-8"))["testPlan"] if c.character in e["characters"]]
    if limit:
        entries = entries[:limit]
    c.fresh()
    table = doors.build(survey.INSTALL)
    for entry in entries:
        def one(entry=entry):
            if not ap.in_control():
                c.fresh()
            sets = [(*flag_ref(f), 1) for f in entry.get("set", []) if flag_ref(f)]
            clears = [(*flag_ref(f), 0) for f in entry.get("clear", []) if flag_ref(f)]
            set_flags(sets + clears)
            a_before, b_before = bank_flags(STORY_A, 4), bank_flags(STORY_B, 8)
            record = doors.record_to(table, entry["room"], c.character)
            ap.send("door " + record.hex(), "wait 800", timeout=60)
            if not ap.wait_for(lambda: ap.state().get("room") == entry["room"], 10):
                ap.send("door " + record.hex(), "wait 800", timeout=60)
            got = watch(c, 150)
            a_after, b_after = bank_flags(STORY_A, 4), bank_flags(STORY_B, 8)
            expected = [flag_ref(f) for f in entry["expect"].get("flagsSet", []) if flag_ref(f)]
            c.add({"room": entry["room"], "event": entry.get("event"), "now": ap.state().get("room"), **got,
                   "setA": sorted(a_after - a_before), "setB": sorted(b_after - b_before),
                   "clearedB": sorted(b_before - b_after),
                   "expectedFlags": [f"{b}{n}" for b, n in expected],
                   "expectedMet": all(n in (a_after if b == "A" else b_after) for b, n in expected),
                   "otherConditions": entry.get("otherConditions", [])})
            ap.send("heal")
        c.step(f"cutscene {entry['room']}/{entry.get('event')}", one)


MATRIX_HP = 3000
# The shotgun and magnum can kill a zombie outright whatever its HP (HP set to −300): they go last.
MATRIX_ORDER = [(2, 15), (1, 0), (6, 100), (7, 6), (8, 6), (9, 6), (10, 4), (3, 7), (5, 6)]


def firing_lane(plan: rdt_map.Room, length: int = 1600) -> tuple[int, int, int] | None:
    """A floor spot and facing with clear floor straight ahead for `length` (where the target gets locked). The spot
    and every point of the lane must be well inside the room: clear of collision and inside a camera's area with a
    600-unit margin all round (a spot on the room's edge let the player and the target slip out of the room)."""
    import math
    quads = [s["quad"] for s in plan.switches if s["header"]]

    def inside(x: float, z: float) -> bool:
        return all(any(rdt_map.point_in_quad(int(x + ox), int(z + oz), q) for q in quads)
                   for ox, oz in ((0, 0), (600, 0), (-600, 0), (0, 600), (0, -600))) and not plan.blocked(int(x), int(z), 450)

    best = None
    for x, z in combat.floor_spots(plan, 400):
        if not inside(x, z) or combat.near_door(plan, x, z, 900):
            continue
        for angle in range(0, 4096, 512):
            theta = angle * 2 * math.pi / 4096
            dx, dz = math.cos(theta), -math.sin(theta)
            if all(inside(x + dx * d, z + dz * d) for d in range(200, length + 1, 200)):
                return x, z, angle
    return best


def t_matrix(c: Context) -> None:
    """Every weapon against each enemy type, on one trapped enemy (owner's idea, 2026-10-14). The enemy stays where the
    game put it — moving it by memory left the game's hit check behind — but the room's enemies are frozen except for
    the instant of each shot, and its HP is set to MATRIX_HP before every shot, so the HP after it is the damage. The
    player stands at 60% of the weapon's reach facing it to fire, and well away from it while switching weapons and
    while the room runs a second after each shot (a frozen enemy ignores a second hit until it has reacted to the
    first). Compared with the game's first-run damage table for that type."""
    c.fresh()
    for room_id, kind in ENEMY_ROOMS:
        def one(room_id=room_id, kind=kind):
            c.route.enter(room_id, freeze=False)
            plan = rdt_map.load(survey.INSTALL, room_id, c.character)
            target = next((e for e in ap.enemies() if e["type"] == kind and e["hp"] > 0), None) or combat.live_enemy()
            if target is None:
                c.add({"room": room_id, "expectedType": kind, "error": "no live enemy"})
                return
            slot = target["slot"]
            count = combat.freeze()

            def now() -> dict:
                return next((e for e in ap.enemies(True) if e["slot"] == slot), target)

            def away() -> None:
                combat.stand_off(plan, now(), 6000, 40000)

            def shot(weapon: int) -> dict:
                nonlocal count
                reach = combat.weapon_range(weapon)
                ap.send("heal", f"enemy-hp {slot} {MATRIX_HP}")
                spot = combat.stand_off(plan, now(), max(250, int(reach * 0.5)), max(450, int(reach * 0.75)))
                where = combat.player_xz()
                d = where and round(combat.distance(*where, now()["x"], now()["z"]))
                before = combat.ammo()
                ap.send("hold RT", "wait 1300")
                if count:
                    ap.send(f"thaw-enemies {count}")
                ap.send("wait 50", "press A 120", f"wait {900 if weapon in (7, 8, 9, 10) else 150}")
                count = combat.freeze() or count
                ap.send("wait 400", "release RT")
                hp = now()["hp"]
                away()
                if count:
                    ap.send(f"thaw-enemies {count}", "wait 1000")
                    count = combat.freeze() or count
                return {"damage": MATRIX_HP - hp, "fired": weapon == 1 or combat.ammo() != before, "distance": d,
                        "spot": spot}

            rows = []
            try:
                for weapon, quantity in MATRIX_ORDER:
                    away()
                    ap.send("inv-clear 2")
                    if not combat.equip(weapon, quantity, c.log):
                        rows.append({"weapon": weapon, "error": "not equipped"})
                        continue
                    hits = [shot(weapon) for _ in range(2)]
                    table = table_damage(weapon, target["type"])
                    rows.append({"weapon": weapon, "table": table, "hits": hits})
                    c.log(f"    {room_id} type {target['type']} weapon {weapon}: {[h['damage'] for h in hits]} "
                          f"at {[h['distance'] for h in hits]} (table {table})")
                    if now()["hp"] <= 0:
                        rows.append({"note": "the target died (a head kill sets HP to −300)"})
                        break
                # Then finish it (HP 1, one Beretta round): the kill flag it sets (flagEnemies), for bounty B503.
                if now()["hp"] > 0:
                    killed_before = bank_flags(ENEMY_FLAGS, 8)
                    away()
                    ap.send("inv-clear 2")
                    combat.equip(2, 15, c.log)
                    ap.send(f"enemy-hp {slot} 1", "heal")
                    combat.stand_off(plan, now(), 700, 1000)
                    ap.send("hold RT", "wait 1300")
                    if count:
                        ap.send(f"thaw-enemies {count}")
                    ap.send("wait 50", "press A 120", "wait 2500")
                    count = combat.freeze() or count
                    ap.send("release RT")
                    rows.append({"kill": {"hp": now()["hp"], "killFlagsSet": sorted(bank_flags(ENEMY_FLAGS, 8) - killed_before)}})
            finally:
                if count and ap.peek(0xBE41E2, 1)[0] == 0:
                    ap.send(f"thaw-enemies {count}", check=False)
            c.add({"room": room_id, "type": target["type"], "slot": slot, "weapons": rows})
        c.step(f"matrix {room_id}", one)


STANDIN_ROOM = "20E"   # the one zombie that stays put and takes every shot reliably (202 crashed; 20E, live 2026-10-14)
STANDIN_TYPES = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19]


def t_standin(c: Context) -> None:
    """The full weapon × enemy-type damage matrix in one room (owner's idea, 2026-10-14). The room's enemies stay
    frozen except for the instant of each shot (the hit check only sees counted enemies, so the shot itself needs them
    running); one zombie stands in for each enemy type: its type byte is set to the type for that instant only, then
    put back — the damage comes from the weapon table row for the target's type (decomp apply_weapon_damage), and
    the other type's AI runs for a few frames at most. Before each shot its HP is set to MATRIX_HP and its hit state
    cleared, so the HP after it is the damage. Jill's handgun adds 3 (7 to dogs) on top of the table (decomp
    weapon post-hit; matches 12 vs 9 and 27 vs 20 live). Measures the table per type, not each enemy's own reactions."""
    plan = rdt_map.load(survey.INSTALL, STANDIN_ROOM, c.character)
    setup: dict = {}

    def prepare() -> None:
        """(Re)enters the room and freezes it with a zombie to stand in — again after a type crashed the game (the
        crow's AI on a zombie did, 2026-10-14)."""
        if setup and ap.state().get("room") == STANDIN_ROOM and ap.peek(0xBE41E2, 1)[0] == 0:
            return
        if not ap.in_control() or ap.state().get("gameplay") != "True":
            c.fresh()
        c.route.enter(STANDIN_ROOM, freeze=False)
        zombie = next((e for e in ap.enemies() if e["type"] in (0, 1, 17) and e["hp"] > 0 and (e["x"] or e["z"])), None)
        if zombie is None:
            raise ap.Failed("no zombie to stand in")
        setup.update(target=zombie, slot=zombie["slot"], original=zombie["type"], count=combat.freeze())
        ap.send(f"enemy-targetable {zombie['slot']}", f"enemy-snapshot {zombie['slot']}")

    def me() -> dict:
        return next((e for e in ap.enemies(True) if e["slot"] == setup["slot"]), setup["target"])

    c.fresh()
    types = [t for t in STANDIN_TYPES if t >= c.first_type]
    try:
        for kind in types:
            def one(kind=kind):
                prepare()
                slot, original = setup["slot"], setup["original"]
                count = setup["count"]
                rows = []
                for weapon, quantity in MATRIX_ORDER:
                    ap.send("inv-clear 2")
                    if not combat.equip(weapon, quantity, c.log):
                        rows.append({"weapon": weapon, "error": "not equipped"})
                        continue
                    reach = combat.weapon_range(weapon)
                    hits = []
                    for _ in range(2):
                        # Back to the clean copy taken at setup (another type's AI can scramble the stand-in).
                        ap.send(f"enemy-restore {slot}", "heal", f"enemy-hp {slot} {MATRIX_HP}", f"enemy-clear-hit {slot}")
                        combat.stand_off(plan, me(), max(300, int(reach * 0.7)), max(500, int(reach * 0.9)))
                        # A shot only takes an enemy whose flags (0xE0) allow the player's aim height; a zombie sets
                        # them once it notices the player (idle 0x41, aware 0xF1 — live 2026-10-14), which means
                        # letting it close in. The harness sets them on the frozen target instead.
                        ap.send(f"enemy-targetable {slot}")
                        where = combat.player_xz()
                        before = combat.ammo()
                        ap.send("hold RT", "wait 1300", f"enemy-type {slot} {kind}")
                        if count:
                            ap.send(f"thaw-enemies {count}")
                        ap.send("wait 50", "press A 120", f"wait {900 if weapon in (6, 7, 8, 9, 10) else 150}")
                        count = combat.freeze() or count
                        ap.send(f"enemy-type {slot} {original}", "wait 300", "release RT", "wait 200")
                        hp = me()["hp"]
                        ap.send(f"enemy-restore {slot}")
                        hits.append({"damage": MATRIX_HP - hp, "fired": weapon == 1 or combat.ammo() != before,
                                     "distance": where and round(combat.distance(*where, me()["x"], me()["z"]))})
                    table = table_damage(weapon, kind)
                    rows.append({"weapon": weapon, "table": table, "hits": hits})
                    c.log(f"    type {kind} weapon {weapon}: {[h['damage'] for h in hits]} (table {table})")
                setup["count"] = count
                c.add({"type": kind, "standIn": original, "room": STANDIN_ROOM, "weapons": rows})
            c.step(f"stand-in type {kind}", one)
    finally:
        if setup:
            try:
                ap.send(f"enemy-type {setup['slot']} {setup['original']}", check=False)
                if setup["count"] and ap.peek(0xBE41E2, 1)[0] == 0:
                    ap.send(f"thaw-enemies {setup['count']}", check=False)
            except ap.Failed:
                pass


FIXTURE_ROOMS = ["100", "118", "106", "307", "30E", "403", "50E"]


def t_fixtures(c: Context) -> None:
    """Each item box (room action handler 8) and typewriter (handler 16) zone in the save rooms: press A there and record
    what the game does — the screen it opens, or its message (a save prompt is answered no; SAVE/ is read-only)."""
    import scd
    c.fresh()
    for name in FIXTURE_ROOMS:
        listing = scd.read_room(rdt_map.path_for(survey.INSTALL, name, c.character), scd.Names())
        zones = [(st["handler"], st["zone"]) for part in ("init", "main") for st in listing[part]
                 if st["kind"] == "action" and st["handler"] in (8, 16)]
        for handler, zone in zones:
            def one(handler=handler, zone=zone, name=name):
                c.route.enter(name)
                plan = rdt_map.load(survey.INSTALL, name, c.character)
                result = {"room": name, "handler": handler, "zone": zone, "said": [], "opened": None}
                for x, z, angle, _ in pickup.spots({"zone": zone}, plan):
                    camera = plan.camera_for(x, z)
                    ap.send(f"place {x} {z} {angle}", "wait 300", *([f"camera {camera}", "wait 400"] if camera is not None else []))
                    if not ap.in_control():
                        continue
                    ap.send("press A 150", "wait 1500")
                    if ap.in_control():
                        continue
                    end = time.time() + 15
                    while time.time() < end and not ap.in_control():
                        state = ap.message_state()
                        if state == ap.PROMPT:
                            result["said"].append(ap.message_text())
                            ap.answer(False)
                        elif state in (ap.PAGE, ap.WAITING):
                            result["said"] += ap.dismiss()
                        elif state:
                            ap.send("wait 150")
                        else:
                            result["opened"] = result["opened"] or {"flags": hex(ap.flags()), "menu": ap.peek(ap.MENU_INPUT, 4).hex()}
                            ap.send("wait 800")
                            if not ap.in_control() and not ap.message_active():
                                ap.send("press X 120", "wait 800")  # leave the item box screen (nothing to read there)
                    result["at"] = [x, z, angle]
                    break
                c.add(result)
            c.step(f"fixture {name}/{handler}", one)


COMMUNITY = Path.home() / "AppData" / "Local" / "Lockpick" / "research" / "saves" / "community"
SAVE_CASES = [  # (file, what to check) — community practice saves, borrowed into slot 1 and restored after
    ("Chris Saves/100 Chris SAVE REBECCA.dat", "rebecca"),
    ("Chris Saves/Chris Lab Escape.dat", "self-destruct"),
    ("Chris Saves/Chris Yawn1.dat", "yawn"),
]


def t_saves(c: Context, backup: Path) -> None:
    """Loads community saves for what a new game can't reach: Rebecca (player id 3, model id 0xBE9829, inventory pointer
    0xD22768 → 0xBE9950), the lab self-destruct counter (0xBE983C, running while 0xBE41C7 & 0x08) and Yawn's poison."""
    import library
    try:
        for name, what in SAVE_CASES:
            def one(name=name, what=what):
                ap.serve()
                library.borrow(COMMUNITY / name, backup)
                seconds = library.load_slot(1)
                pointer = int.from_bytes(ap.peek(0xD22768, 4), "little")
                record = {"save": name, "check": what, "loadSeconds": round(seconds), "state": ap.state(),
                          "playerId": ap.peek(0xBE62E5, 1)[0], "modelId": ap.peek(0xBE9829, 1)[0],
                          "inventoryPointer": hex(pointer), "inventory": ap.inventory()}
                if what == "self-destruct":
                    samples = []
                    for _ in range(6):
                        samples.append({"counter": int.from_bytes(ap.peek(0xBE983C, 2), "little", signed=True),
                                        "running": bool(ap.peek(0xBE41C7, 1)[0] & 0x08), "igt": ap.igt()})
                        time.sleep(2)
                    record["samples"] = samples
                if what == "yawn":
                    c.route.room = ap.state().get("room")
                    got = wait_for_status(c, 0x20, 150)
                    record["bitten"] = {**got, "B67": flag_set(STORY_B, 0x43)}
                    if got["poisoned"]:
                        ap.send("freeze-enemies")
                        record["ticks"] = poison_ticks(10)
                        record["blueHerb"] = cure(c, 69)
                        record["serum"] = {**cure(c, 66), "B67": flag_set(STORY_B, 0x43)}
                c.add(record)
            c.step(name, one)
    finally:
        ok = library.restore_slot1(backup)
        c.add({"step": "slot 1 restored", "hashMatches": ok})


TESTS = {
    "timer": t_timer, "bands": t_bands, "healing": t_healing, "combine": t_combine, "full-inventory": t_full_inventory,
    "poison": t_poison, "yawn-poison": t_yawn_poison, "weapons": t_weapons, "enemy-hits": t_enemy_hits,
    "kills": t_kills, "send-back": t_send_back, "fixtures": t_fixtures, "matrix": t_matrix, "standin": t_standin,
}


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    out, backup = Path(argv[0]), Path(argv[1])
    rest = argv[2:]
    characters = [rest[rest.index("--character") + 1]] if "--character" in rest else ["jill", "chris"]
    plans = Path(rest[rest.index("--plans") + 1]) if "--plans" in rest else None
    names = [a for a in rest if a in TESTS or a in ("doors", "cutscenes", "saves")] or [*TESTS, "doors", "cutscenes"]
    out.mkdir(parents=True, exist_ok=True)
    survey.protect_saves()
    try:
        for character in characters:
            for name in names:
                c = Context(out, character, name,
                            int(rest[rest.index("--from-type") + 1]) if "--from-type" in rest else 0)
                c.log("start")
                try:
                    if name == "doors":
                        only = {int(v) for v in rest[rest.index("--locks") + 1].split(",")} if "--locks" in rest else None
                        t_doors(c, plans / "key-doors.json", only)
                    elif name == "cutscenes":
                        t_cutscenes(c, plans / "cutscene-events.json")
                    elif name == "saves":
                        t_saves(c, backup)
                    else:
                        TESTS[name](c)
                except Exception as error:  # noqa: BLE001
                    c.add({"test": name, "error": f"{type(error).__name__}: {error}"[:600]})
                    c.log(traceback.format_exc()[-1200:])
                c.log("done")
    finally:
        ap.send("speed normal", check=False)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
