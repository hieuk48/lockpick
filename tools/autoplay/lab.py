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
        time.sleep(0.6)
        self.log(f"arena: {ap.state()['pos']}, camera {ap.peek(0xBE9822, 1)[0]}")

    def stand(self) -> None:
        """The player on the arena spot, facing the enemy's side, with the arena's camera."""
        x, z, facing = self.a["player"]
        ap.send(f"place {x} {z} {facing}", f"camera {self.a['camera']}", "wait 400")

    def ensure(self) -> None:
        if not alive() or ap.state().get("room") != self.a["room"] or not ap.in_control():
            self.arena()

    def spawn(self, kind: int, at: tuple[int, int] | None = None, hold: bool = True, kill_flag: int = 0xFF) -> dict | None:
        self.ensure()
        self.stand()
        ap.send("heal")
        x, z = at or self.a["enemy"]
        r = spawn.spawn(kind, x, z, slot=SLOT, hold=hold, kill_flag=kill_flag, extra=self.dog_hold, log=self.log)
        self.stand()
        return r["enemy"]

    def shot(self, aim: str = "", settle: float = 0.8) -> None:
        """Raise the gun (with up/down held for an aimed shot), fire once, lower it."""
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
                    ap.send(f"enemy-pin {SLOT} off", spawn.hold_command(kind, SLOT),
                            f"enemy-wake {SLOT} {self.a['enemy'][0]} {self.a['enemy'][1]} "
                            f"{ap.angle_to(*self.a['enemy'], p[0], p[2])} {behaviour:X}")
                    ap.wait_for(lambda: f" {SLOT}" not in ap.send("enemy-armed").split("waiting:")[-1] + " ", 5)
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
            ap.send(f"enemy-pin {SLOT} off", f"enemy-hp {SLOT} 1", f"enemy-state {SLOT} 1")
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
                for name, key in (("level", ""), ("up", "LUp"), ("down", "LDown")):
                    e = self.spawn(kind, at=self.a["near"], hold=True)
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

    def attacks(self, types: list[int], seconds: float = 45, wanted: int = 6) -> list[dict]:
        rows = []
        full = None
        for kind in types:
            row = {"type": kind, "name": spawn.scd.Names().enemies.get(str(kind)), "hits": []}
            try:
                if not self.spawn(kind, hold=False):
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
    result = {"hp": lambda: lab.hp(types), "killflags": lab.killflags, "aim": lambda: lab.aim(types),
              "attacks": lambda: lab.attacks(types)}[experiment]()
    (out / f"lab-{experiment}-{character}.json").write_text(json.dumps(result, indent=1) + "\n", encoding="utf-8")
