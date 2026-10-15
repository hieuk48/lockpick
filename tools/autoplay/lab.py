r"""Enemy lab (dev harness only, never shipped): live checks of what the code can't settle, with the player and an enemy in front of
them both on screen (ARENAS: 108's second leg by default, the owner's pick, or 11A). A fresh boot reaches it in ~20 s:
new game (opening skipped), a door into the room, then the player is teleported to the spot and the view cut to its
camera (a placed player keeps the old camera otherwise).

    python -I tools/autoplay/lab.py <out-dir> hp|killflags|aim|attacks [--types 0,2] [--character jill|chris] [--arena 108|11A]

    hp         each type's starting HP: spawned once, then started afresh (its init re-run) many times
    killflags  a zombie spawned with an unused kill flag (17, then 250), killed with the handgun: which flagEnemies
               bit sets, whether the enemy-slot fields read as expected, and whether it is placed again on reload
    aim        the player's aim byte (+0x176) when aiming level, up and down, and which aims hit each type
    attacks    each type let loose on the player: every HP loss, with the enemy's state at the time and any poison

Results: <out-dir>/lab-<experiment>-<character>.json.
"""
from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import ap  # noqa: E402
import doors  # noqa: E402
import spawn  # noqa: E402
import survey  # noqa: E402

# Where the player stands with an enemy in front of them, both on screen: the player is teleported there and the view
# cut to the camera the room's own zones give that spot (owner: teleport, don't walk).
ARENAS = {
    # 108, the L-corridor (owner's pick): the player in the foreground of camera 2 on the second leg, facing back to the
    # first corner, where the enemy stands (or nearer, for close shots); camera 2 looks down the leg at the corner. The
    # dogs' trigger lines only react to the player (the first dog's across the first leg, x 14500 and 19420; the
    # second's across this leg, z 8360 and 13080), and the player never crosses them; until then both dogs stay dormant.
    # Camera 2 holds for the player anywhere on this leg up to z 20500 except near z 19000 (camera 3), live 2026-10-14.
    "108": {"room": "108", "via": "107", "player": (24000, 15500, 1024), "camera": 2,
            "enemy": (23000, 3800), "near": (24000, 12000)},
    # 11A, the outdoor U behind the mansion: midway up the first leg, camera 1 (its dog is held).
    "11A": {"room": "11A", "via": None, "player": (4000, 7500, 3072), "camera": 1,
            "enemy": (4000, 12500), "near": (4000, 10600)},
}
SLOT = 2
ENEMIES, SIZE = 0xBE6464, 0x18C
ENEMY_FLAGS = 0xBE987C             # flagEnemies, 8 words
PLAYER = 0xBE62E4
AIM = PLAYER + 0x176               # weaponAimFlags (decomp; bits 0x20/0x40/0x80)
STATUS = 0xBE63C0                  # player status: 0x02 poison
TYPES = [0, 1, 17, 2, 3, 5, 6, 7, 9, 10, 15]


def alive() -> bool:
    return "ResidentEvil.exe" in subprocess.run(["tasklist", "/FI", "IMAGENAME eq ResidentEvil.exe"],
                                                capture_output=True, text=True).stdout


def slot_bytes(slot: int, offset: int, length: int) -> bytes:
    return ap.peek(ENEMIES + slot * SIZE + offset, length)


def enemy(slot: int = SLOT) -> dict | None:
    return next((e for e in ap.enemies(True) if e["slot"] == slot), None)


def bank(address: int, words: int) -> set[int]:
    raw = ap.peek(address, words * 4)
    return {w * 32 + (31 - b) for w in range(words) for b in range(32)
            if int.from_bytes(raw[w * 4:w * 4 + 4], "little") >> b & 1}


class Lab:
    def __init__(self, out: Path, character: str = "jill", log=print, arena: str = "108"):
        self.out, self.character, self.log = out, character, log
        self.a = ARENAS[arena]
        self.dog_hold: tuple[str, ...] = ()

    def arena(self) -> None:
        """Fresh boot to the arena spot with the arena's camera; the room's awake enemies held."""
        a = self.a
        survey.protect_saves()
        ap.serve()
        self.log(f"new game in {ap.new_game(self.character):.0f} s")
        record = doors.record_to(doors.build(survey.INSTALL), a["room"], self.character, a["via"])
        ap.send("door " + record.hex())
        ap.wait_for(lambda: ap.state().get("room") == a["room"] and ap.in_control(), 30)
        native = [e for e in ap.enemies() if e["slot"] != SLOT and abs(e["x"]) < 30000 and abs(e["z"]) < 30000]
        self.dog_hold = tuple(spawn.hold_command(e["type"], e["slot"]) for e in native)
        for e in native:
            ap.send(f"enemy-state {e['slot']} {spawn.HOLD_STATE[e['type']]}" if e["type"] in spawn.HOLD_STATE
                    else f"enemy-pin {e['slot']}")
        self.stand()
        ap.equip_now(2, 15, log=self.log)   # a new game starts with nothing in hand
        self.stand()
        time.sleep(0.6)
        self.log(f"arena: {ap.state()['pos']}, camera {ap.peek(0xBE9822, 1)[0]}")

    def stand(self) -> None:
        """The player on the arena spot, facing the enemy's side, with the arena's camera."""
        x, z, facing = self.a["player"]
        ap.send(f"place {x} {z} {facing}", f"camera {self.a['camera']}", "wait 400")

    def ensure(self) -> None:
        if not alive() or ap.state().get("room") != self.a["room"] or not ap.in_control():
            self.arena()

    def spawn(self, kind: int, at: tuple[int, int] | None = None, hold: bool = True, kill_flag: int = 0xFF,
              pin: bool = False, position_only: bool = False) -> dict | None:
        self.ensure()
        self.stand()
        ap.send("heal")
        x, z = at or self.a["enemy"]
        r = spawn.spawn(kind, x, z, slot=SLOT, hold=hold, kill_flag=kill_flag, extra=self.dog_hold, log=self.log, pin=pin)
        self.stand()
        return r["enemy"]

    def shot(self, aim: str = "", settle: float = 0.8) -> None:
        """Raise the gun (with up/down held for an aimed shot), fire once, lower it."""
        if ap.equipped() == 0:
            raise ap.Failed("nothing equipped: the shot would not fire")
        ap.send("hold RT" + (f"+{aim}" if aim else ""), "wait 1300", "press A 120", f"wait {int(settle * 1000)}",
                "release RT" + (f"+{aim}" if aim else ""), "wait 300")

    # --- experiments ------------------------------------------------------------------------------------------------

    def hp(self, types: list[int], samples: int = 16) -> list[dict]:
        rows = []
        for kind in types:
            row = {"type": kind, "name": spawn.scd.Names().enemies.get(str(kind)), "hp": []}
            try:
                if not self.spawn(kind, hold=True):
                    raise ap.Failed("not spawned")
                row["hp"].append(enemy()["hp"])
                behaviour = spawn.templates()[kind][2]
                for _ in range(samples - 1):
                    # Started afresh (its init rolls the HP again) and held again from its first frames.
                    p = ap.pos()
                    hold = spawn.HOLD_STATE.get(kind, "pin")
                    ap.send(f"enemy-pin {SLOT} off",
                            f"enemy-wake {SLOT} {self.a['enemy'][0]} {self.a['enemy'][1]} "
                            f"{ap.angle_to(*self.a['enemy'], p[0], p[2])} {behaviour:X} --hold {hold}")
                    row["hp"].append(enemy()["hp"])
                row["range"] = [min(row["hp"]), max(row["hp"])]
            except Exception as e:  # noqa: BLE001
                row["error"] = str(e)[-300:]
            self.log(f"type {kind}: {row.get('range')} {row['hp']} {row.get('error', '')}")
            rows.append(row)
        return rows

    def killflags(self) -> list[dict]:
        rows = []
        for flag in (17, 250):
            row = {"killFlag": flag}
            e = self.spawn(0, at=self.a["near"], hold=True, kill_flag=flag)
            raw = slot_bytes(SLOT, 0, 0x18C)
            row["spawned"] = {"enemy": e, "count": ap.peek(0xBE41E2, 1)[0], "flagsByte": raw[0], "type": raw[1],
                              "hp": int.from_bytes(raw[0x88:0x8A], "little", signed=True),
                              "deathEventId": raw[0x163], "slotNibble": raw[0x161]}
            before = bank(ENEMY_FLAGS, 8)
            # Still held: a held enemy takes hits, and a lethal one puts it in its death state, whose code sets the flag.
            # Held in a do-nothing state its AI never sets the aim-height bits a shot needs (0xE0 of the first byte;
            # it stays 0x01), so they are set here.
            ap.send(f"enemy-hp {SLOT} 1", f"enemy-targetable {SLOT}")
            for _ in range(3):
                self.shot()
                if (enemy() or {}).get("hp", 1) <= 0:
                    break
            time.sleep(4)          # the death animation sets the flag at its end
            after = bank(ENEMY_FLAGS, 8)
            row.update(hpAfter=(enemy() or {}).get("hp"), set=sorted(after - before), cleared=sorted(before - after))
            # Placed again? The same enemy_set (same kill flag) on a reload.
            again = self.spawn(0, at=self.a["near"], hold=True, kill_flag=flag)
            row["placedAgainWithFlagSet"] = bool(again and again["type"] == 0 and (again["x"] or again["z"]))
            self.log(f"kill flag {flag}: {row}")
            rows.append(row)
        return rows

    def aim(self, types: list[int]) -> list[dict]:
        rows = []
        readings = {}
        for name, key in (("level", ""), ("up", "LUp"), ("down", "LDown")):
            ap.send("hold RT" + (f"+{key}" if key else ""), "wait 1300")
            readings[name] = {"aim": ap.peek(AIM, 1)[0], "flags": ap.peek(PLAYER, 1)[0]}
            ap.send("release RT" + (f"+{key}" if key else ""), "wait 400")
        rows.append({"player": readings})
        self.log(f"player aim bytes: {readings}")
        for kind in types:
            row = {"type": kind, "name": spawn.scd.Names().enemies.get(str(kind)), "shots": []}
            try:
                # Only its position pinned: its AI runs and sets the aim-height bits (0xE0 of the first byte) a shot
                # needs, as it would in play (a full pin rewrites its AI state every millisecond, and spiders and
                # snakes never noticed the player under it: flags stayed 0x01, 2026-10-14).
                e = self.spawn(kind, at=self.a["near"], hold=True, position_only=True)
                end = time.time() + 4    # time to notice the player
                while time.time() < end and not slot_bytes(SLOT, 0, 1)[0] & 0xE0:
                    time.sleep(0.2)
                for name, key in (("level", ""), ("up", "LUp"), ("down", "LDown")):
                    ap.send(f"enemy-hp {SLOT} 3000", f"enemy-clear-hit {SLOT}")
                    flags = slot_bytes(SLOT, 0, 1)[0]
                    self.shot(key)
                    hp = (enemy() or {}).get("hp")
                    row["shots"].append({"aim": name, "enemyFlags": flags, "hit": hp is not None and hp < 3000,
                                         "damage": None if hp is None else 3000 - hp})
            except Exception as e:  # noqa: BLE001
                row["error"] = str(e)[-300:]
            self.log(f"type {kind}: {row['shots']} {row.get('error', '')}")
            rows.append(row)
        return rows

    # --- reactions: what each weapon's hit does to each enemy (it stays on its spot; its AI and reactions run) --------

    def at_distance(self, d: int) -> tuple[int, int]:
        """The spot `d` units in front of the player (the arena's player faces the enemy side)."""
        import math
        x, z, facing = self.a["player"]
        theta = facing * 2 * math.pi / 4096
        return int(x + math.cos(theta) * d), int(z - math.sin(theta) * d)

    def trace(self, seconds: float, every: float = 0.1) -> list[list[int]]:
        """Samples, changes only: [ms, health, state, ignore, action, action state, behaviour, hit state, flags,
        animation]."""
        out, last, t0 = [], None, time.time()
        while time.time() - t0 < seconds:
            b = slot_bytes(SLOT, 0, 0xC0)
            row = [int.from_bytes(b[0x88:0x8A], "little", signed=True), b[0x84], b[0x85], b[0x86], b[0x87], b[2],
                   b[0x8A], b[0], b[0xBD]]
            if row != last:
                out.append([int((time.time() - t0) * 1000)] + row)
                last = row
            time.sleep(every)
        return out

    def fire_until_hit(self, hp_before: int) -> str | None:
        """Level, then down, then up: the first aim whose shot lands (HP changed or hit state set)."""
        for name, key in (("level", ""), ("down", "LDown"), ("up", "LUp")):
            self.shot(key, settle=0.15)
            e = enemy()
            if e and (e["hp"] != hp_before or slot_bytes(SLOT, 0x8A, 1)[0]):
                return name
            ap.send(f"enemy-clear-hit {SLOT}")
        return None

    def clear_enemy_flag(self, flag: int) -> None:
        byte = ENEMY_FLAGS + flag // 32 * 4 + (31 - flag % 32) // 8
        ap.send(f"state-apply {byte:X}:00:{1 << ((31 - flag % 32) % 8):02X}")

    def reactions(self, types: list[int], weapons: list[tuple[int, int]]) -> list[dict]:
        rows = []
        for kind in types:
            for weapon, quantity in weapons:
                row = {"type": kind, "name": spawn.scd.Names().enemies.get(str(kind)), "weapon": weapon}
                try:
                    self.ensure()
                    ap.equip_now(weapon, quantity, log=self.log)
                    near = self.at_distance(900 if weapon == 1 else 3500)
                    for phase, hp, seconds in (("hit", 3000, 4), ("lethal", 1, 8)):
                        self.clear_enemy_flag(251)
                        self.spawn(kind, at=near, hold=True, position_only=True, kill_flag=251)
                        time.sleep(1.0)    # aware of the player: its aim-height bits set
                        ap.send(f"enemy-hp {SLOT} {hp}", f"enemy-clear-hit {SLOT}", "heal")
                        aim = self.fire_until_hit(hp)
                        trace = self.trace(seconds)
                        row[phase] = {"aim": aim, "damage": hp - trace[0][1] if trace and aim else None,
                                      "end": trace[-1] if trace else None, "trace": trace,
                                      "killFlagSet": 251 in bank(ENEMY_FLAGS, 8)}
                    self.clear_enemy_flag(251)
                except Exception as e:  # noqa: BLE001
                    row["error"] = str(e)[-300:]
                ap.send("heal", check=False)
                summary = {k: (row[k]["aim"], row[k]["damage"], row[k]["end"], row[k]["killFlagSet"])
                           for k in ("hit", "lethal") if k in row}
                self.log(f"type {kind} weapon {weapon}: {summary} {row.get('error', '')}")
                rows.append(row)
                self.save_partial("reactions", rows)
        return rows

    def poise(self, types: list[int], rounds: int = 3, shots: int = 14) -> list[dict]:
        """Handgun hits on a zombie (position pinned, HP 3000) until it falls, then whether it gets back up."""
        rows = []
        self.ensure()
        ap.equip_now(2, 99, log=self.log)
        for kind in types:
            for n in range(rounds):
                row = {"type": kind, "round": n, "shots": []}
                try:
                    self.spawn(kind, at=self.at_distance(3500), hold=True, position_only=True)
                    time.sleep(1.0)
                    ap.send(f"enemy-hp {SLOT} 3000")
                    for i in range(shots):
                        self.shot(settle=0.6)
                        b = slot_bytes(SLOT, 0, 0x90)
                        row["shots"].append({"n": i + 1, "hp": int.from_bytes(b[0x88:0x8A], "little", signed=True),
                                             "state": list(b[0x84:0x88]), "behaviour": b[2]})
                        if b[0x86] in (4, 7) or b[0x84] == 3:   # knocked down / bend down / dying
                            row["fellAt"] = i + 1
                            break
                    row["after"] = self.trace(10)
                except Exception as e:  # noqa: BLE001
                    row["error"] = str(e)[-300:]
                self.log(f"type {kind} round {n}: fell at {row.get('fellAt')}, end {(row.get('after') or [None])[-1]}")
                rows.append(row)
                self.save_partial("poise", rows)
        return rows

    def special(self, types: list[int]) -> list[dict]:
        """The code's distance and aim rules, live: the shotgun inside 3000 aimed up/down/level and at 3500 (zombie
        head kill), the Colt Python aimed level/up/down, the shotgun at 8500 and 9500 (10 HP back beyond 9000)."""
        cases = [(3, 30, d, aim) for d, aim in ((2500, "up"), (2500, "down"), (2500, "level"), (3500, "down"),
                                                  (8500, "level"), (9500, "level"))]
        cases += [(5, 30, 5000, aim) for aim in ("level", "up", "down")]
        keys = {"level": "", "up": "LUp", "down": "LDown"}
        rows = []
        for kind in types:
            for weapon, quantity, d, aim in cases:
                row = {"type": kind, "weapon": weapon, "distance": d, "aim": aim}
                try:
                    self.ensure()
                    ap.equip_now(weapon, quantity, log=self.log)
                    self.spawn(kind, at=self.at_distance(d), hold=True, pin=True)
                    time.sleep(1.0)
                    ap.send(f"enemy-hp {SLOT} 3000", f"enemy-clear-hit {SLOT}", f"enemy-targetable {SLOT}", "heal")
                    self.shot(keys[aim], settle=0.3)
                    hp = (enemy() or {}).get("hp")
                    row.update(hp=hp, damage=None if hp is None else 3000 - hp, headKill=hp == -300)
                except Exception as e:  # noqa: BLE001
                    row["error"] = str(e)[-300:]
                self.log(f"type {kind} weapon {weapon} at {d} aimed {aim}: {row.get('damage')} {row.get('error', '')}")
                rows.append(row)
                self.save_partial("special", rows)
        return rows

    def deaths(self, types: list[int], start_hps: tuple[int, ...] = (1, 24, 48), seconds: float = 40) -> list[dict]:
        """Each type let loose on a player at low HP (Danger/Caution edges) until the player dies or time runs out:
        the HP before the killing hit and what the enemy was doing. Several enemies treat a low-HP player specially
        (decomp: grabs that clamp the player at 1 HP, a dog finisher below a threshold, crow/snake/plant death moves),
        so a death is not just the sum of hits. A death is a game over: a fresh boot follows."""
        rows = []
        for kind in types:
            for start in start_hps:
                row = {"type": kind, "name": spawn.scd.Names().enemies.get(str(kind)), "startHp": start, "hps": []}
                try:
                    self.ensure()
                    if not self.spawn(kind, at=self.at_distance(800 if kind == 15 else 3500), hold=False):
                        raise ap.Failed("not spawned")
                    ap.send(f"hp {start}")
                    last, end = start, time.time() + seconds
                    while time.time() < end:
                        now = ap.hp()
                        if now != last:
                            row["hps"].append({"hp": now, "enemyState": list(slot_bytes(SLOT, 0x84, 4)),
                                               "playerState": list(ap.peek(PLAYER + 0x84, 4))})
                            last = now
                        if now <= 0 or ap.flags() & 0x01000000:      # dead, or the death fade running
                            row["died"] = True
                            row["killedFrom"] = row["hps"][-2]["hp"] if len(row["hps"]) > 1 else start
                            row["killingMove"] = row["hps"][-1]
                            time.sleep(2)
                            break
                        time.sleep(0.03)
                    row.setdefault("died", False)
                    row["endHp"] = last
                except Exception as e:  # noqa: BLE001
                    row["error"] = str(e)[-300:]
                self.log(f"type {kind} from HP {start}: died {row.get('died')} (from {row.get('killedFrom')}), "
                         f"HPs {[h['hp'] for h in row['hps']]} {row.get('error', '')}")
                rows.append(row)
                self.save_partial("deaths", rows)
                if row.get("died"):
                    self.arena()     # game over: a fresh boot
        return rows

    def save_partial(self, name: str, rows: list[dict]) -> None:
        (self.out / f"lab-{name}-{self.character}.json").write_text(json.dumps(rows, indent=1) + "\n", encoding="utf-8")

    def attacks(self, types: list[int], seconds: float = 45, wanted: int = 6) -> list[dict]:
        rows = []
        full = None
        for kind in types:
            row = {"type": kind, "name": spawn.scd.Names().enemies.get(str(kind)), "hits": []}
            try:
                if not self.spawn(kind, at=self.at_distance(800 if kind == 15 else 3500), hold=False):
                    raise ap.Failed("not spawned")
                ap.send("heal")
                full = ap.hp()
                end = time.time() + seconds
                last = full
                while time.time() < end and len(row["hits"]) < wanted:
                    now = ap.hp()
                    if now < last:
                        state = slot_bytes(SLOT, 0x84, 4)
                        row["hits"].append({"loss": last - now, "enemyState": list(state),
                                            "poison": bool(ap.peek(STATUS, 1)[0] & 0x02),
                                            "playerState": list(ap.peek(PLAYER + 0x84, 4))})
                        if now < full // 2:
                            ap.send("heal")
                            now = ap.hp()
                    last = now
                    time.sleep(0.05)
            except Exception as e:  # noqa: BLE001
                row["error"] = str(e)[-300:]
            ap.send("heal", check=False)
            self.log(f"type {kind}: {[h['loss'] for h in row['hits']]} {row.get('error', '')}")
            rows.append(row)
        return rows


if __name__ == "__main__":
    args = sys.argv[1:]
    out, experiment = Path(args[0]), args[1]
    out.mkdir(parents=True, exist_ok=True)
    character = args[args.index("--character") + 1] if "--character" in args else "jill"
    types = [int(t) for t in args[args.index("--types") + 1].split(",")] if "--types" in args else TYPES
    lab = Lab(out, character, arena=args[args.index("--arena") + 1] if "--arena" in args else "108")
    lab.arena()
    weapons = [(1, 0), (2, 99), (3, 30), (5, 30), (6, 200), (7, 30), (8, 30), (9, 30), (10, 30)]
    if "--weapons" in args:
        picked = [int(w) for w in args[args.index("--weapons") + 1].split(",")]
        weapons = [w for w in weapons if w[0] in picked]
    zombies = [t for t in types if t in (0, 1, 17)] or [0, 1, 17]
    walkers = [t for t in types if t in (0, 1, 17, 2)] or [0, 1, 17, 2]
    result = {"hp": lambda: lab.hp(types), "killflags": lab.killflags, "aim": lambda: lab.aim(types),
              "attacks": lambda: lab.attacks(types), "reactions": lambda: lab.reactions(types, weapons),
              "poise": lambda: lab.poise(zombies), "special": lambda: lab.special(walkers),
              "deaths": lambda: lab.deaths(types)}[experiment]()
    (out / f"lab-{experiment}-{character}.json").write_text(json.dumps(result, indent=1) + "\n", encoding="utf-8")
