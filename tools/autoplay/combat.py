r"""Combat measurements (dev-only autoplay): damage per shot for each weapon, and the damage each enemy type deals the
player — read from memory (enemy slots at 0xBE6464, player HP 0xBE636C), never guessed from the screen.

    python -I tools/autoplay/combat.py <out-dir> weapons|enemies [jill|chris]

Weapons: the weapon is added with `give` (loaded ammo = its quantity) and equipped through the menu (checked with
the equipped slot); before every shot the player stands on clear floor away from the enemy and faces it, aims (RT)
and fires (A), and the enemy's HP is read after the shot. Enemies: the player stands near the enemy and HP is sampled
every 100 ms; each drop is one hit.
"""
from __future__ import annotations

import json
import math
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import ap  # noqa: E402
import rdt_map  # noqa: E402
import route  # noqa: E402
import survey  # noqa: E402

PLAYER_RADIUS = 450


def near_door(room: rdt_map.Room, x: int, z: int, margin: int = 400) -> bool:
    """Within `margin` of a door zone: A there (a shot that didn't fire is a press of the action key) can open it."""
    for door in room.doors:
        dx, dz, w, d = door["zone"]
        if dx - margin <= x <= dx + w + margin and dz - margin <= z <= dz + d + margin:
            return True
    return False


def floor_spots(room: rdt_map.Room, step: int = 500) -> list[tuple[int, int]]:
    """Points on the room's floor: inside some camera's coverage quad, clear of blocking collision and away from
    doors."""
    quads = [s["quad"] for s in room.switches if s["header"]]
    if not quads:
        return []
    xs = [x for q in quads for x, _ in q]
    zs = [z for q in quads for _, z in q]
    spots = []
    for x in range(min(xs), max(xs) + 1, step):
        for z in range(min(zs), max(zs) + 1, step):
            inside = any(rdt_map.point_in_quad(x, z, q) for q in quads)
            if inside and not room.blocked(x, z, PLAYER_RADIUS) and not near_door(room, x, z):
                spots.append((x, z))
    return spots


def distance(ax: float, az: float, bx: float, bz: float) -> float:
    return math.hypot(ax - bx, az - bz)


def stand_off(room: rdt_map.Room, enemy: dict, near: int = 3500, far: int = 6000) -> tuple[int, int] | None:
    """A floor spot `near`..`far` from the enemy (closest to `near`), facing it; the camera cuts to show the player."""
    floor = floor_spots(room, 300)
    spots = [(x, z) for x, z in floor if near <= distance(x, z, enemy["x"], enemy["z"]) <= far] or floor
    if not spots:
        return None
    x, z = min(spots, key=lambda s: abs(distance(s[0], s[1], enemy["x"], enemy["z"]) - near))
    camera = room.camera_for(x, z)
    locked = ap.flags() & 0x100000  # a cutscene holds the camera: the harness refuses cuts then
    ap.send(f"place {x} {z} {ap.angle_to(x, z, enemy['x'], enemy['z'])}", "wait 300",
            *([f"camera {camera}", "wait 400"] if camera is not None and not locked else []))
    return x, z


def reachable(room: rdt_map.Room, enemy: dict, reach: int) -> bool:
    """Some clear floor spot lies within `reach` of the enemy (a dormant Yawn sits outside the room's floor)."""
    return any(distance(x, z, enemy["x"], enemy["z"]) <= reach for x, z in floor_spots(room, 300))


def live_enemy() -> dict | None:
    alive = [e for e in ap.enemies() if e["hp"] > 0]
    return alive[0] if alive else None


def equip(weapon: int, quantity: int, log, room: rdt_map.Room | None = None) -> bool:
    if weapon not in [i for i, _, _ in ap.inventory()]:
        ap.send(f"give {weapon} {quantity}")
    if ap.equipped() != weapon:
        enemy = live_enemy()
        if room is not None and enemy is not None:
            # Open the menu well away from the enemy: a zombie next to the player grabs before the menu opens.
            stand_off(room, enemy, 6000, 40000)
        ap.use_item(weapon)
        ap.close_menu()
    ok = ap.equipped() == weapon
    log(f"  equipped {ap.item_name(weapon, shown=False)}: {'ok' if ok else 'FAILED (equipped ' + str(ap.equipped()) + ')'}")
    return ok


def ammo() -> int | None:
    return dict((i, q) for i, q, _ in ap.inventory()).get(ap.equipped())


def player_xz() -> tuple[int, int] | None:
    p = ap.pos()
    return (p[0], p[2]) if p else None


RANGES = 0x4BB648  # u32 per weapon (id − 1), Chris row then Jill/Rebecca row (+10): shots beyond it miss or don't fire


def weapon_range(weapon: int) -> int:
    row = 10 if ap.peek(0xBE62E5, 1)[0] & 1 else 0
    return int.from_bytes(ap.peek(RANGES + (row + weapon - 1) * 4, 4), "little")


def freeze() -> int:
    """Stops the room's enemies (the harness sets the enemy count to 0); returns the count to thaw with."""
    out = ap.send("freeze-enemies")
    return int(out.split("count was ")[1].split(")")[0])


def shoot(room: rdt_map.Room, shots: int, log, settle_ms: int = 1500) -> list[dict]:
    """Fires up to `shots` times at the first live enemy. Raising a gun takes ~1.2 s — long enough for a zombie at
    firing range to grab the player (owner, 2026-10-14) — and the game doesn't take a new position while the player
    aims, so: stand at the firing spot facing the enemy, freeze the room's enemies, raise the gun, thaw them and fire
    at once. The enemy's HP is read `settle_ms` later. A shot that used no ammo is retried once; the knife uses none,
    so its swings count as fired."""
    results = []
    for _ in range(shots):
        enemy = live_enemy()
        if enemy is None or not ap.in_control():
            break
        ap.send("heal")
        weapon = ap.equipped() or 2
        reach = weapon_range(weapon)
        before_ammo = ammo()
        count = 0
        for attempt in range(2):
            now = next((e for e in ap.enemies(True) if e["slot"] == enemy["slot"]), enemy)
            stand_off(room, now, max(300, int(reach * 0.6)), max(500, int(reach * 0.85)))
            count = freeze() or count  # 0 when already frozen: keep the count to thaw with
            try:
                ap.send("hold RT", "wait 1300")
            finally:
                if count:
                    ap.send(f"thaw-enemies {count}")
            # Enemies run only for the shot (rounds that fly get time to land), then stay frozen while it's read.
            ap.send("wait 50", "press A 120", f"wait {900 if weapon in (7, 8, 9, 10) else 150}")
            count = freeze() or count
            ap.send("wait 600", "release RT", "wait 200")
            if weapon == 1 or ammo() != before_ammo:
                break
        after = next((e for e in ap.enemies(True) if e["slot"] == enemy["slot"]), None)
        if count and ap.peek(0xBE41E2, 1)[0] == 0:
            ap.send(f"thaw-enemies {count}")
        where = player_xz()
        record = {"type": enemy["type"], "slot": enemy["slot"], "before": enemy["hp"], "after": after and after["hp"],
                  "damage": None if after is None else enemy["hp"] - after["hp"],
                  "killed": bool(after and after["hp"] <= 0), "ammoUsed": None if before_ammo is None or ammo() is None
                  else before_ammo - ammo(), "distance": where and round(distance(*where, enemy["x"], enemy["z"]))}
        results.append(record)
        log(f"    shot: {record}")
        ap.send("heal")
    return results


WEAPONS = [  # (weapon id, loaded quantity)
    (2, 15),   # Beretta
    (3, 7),    # Shotgun
    (5, 6),    # Colt Python
    (7, 6),    # Bazooka (explosive)
    (8, 6),    # Bazooka (acid)
    (9, 6),    # Bazooka (flame)
    (6, 100),  # Flamethrower
    (10, 4),   # Rocket launcher
    (1, 0),    # Combat knife
]


def weapons(character: str, rooms: list[str], out: Path, log, shots: int = 3) -> list[dict]:
    """Every weapon against the rooms' enemies in turn (a stationary, high-HP target is best: Plant 42 in 303, Yawn in
    210); when the target dies the next room is entered."""
    r = route.Route(character, log)
    results = []
    pending = list(rooms)
    room = None
    for weapon, quantity in WEAPONS:
        if room is None or live_enemy() is None:
            room = None
            while pending:
                r.enter(pending.pop(0), freeze=False)
                if live_enemy():
                    room = rdt_map.load(survey.INSTALL, r.room, character)
                    break
            if room is None:
                log("  out of rooms with live enemies")
                break
        ap.send("inv-clear 3")
        if not equip(weapon, quantity, log, room):
            results.append({"weapon": weapon, "error": "not equipped"})
            continue
        fired = shoot(room, shots, log)
        results.append({"weapon": weapon, "name": ap.item_name(weapon, shown=False), "room": r.room, "shots": fired})
        (out / f"weapons-{character}.json").write_text(json.dumps(results, indent=1) + "\n", encoding="utf-8")
    return results


def enemy_hits(character: str, rooms: list[str], out: Path | None, log, seconds: float = 25) -> list[dict]:
    """In each room: stand ~1500 from the first live enemy, sample HP every 100 ms for `seconds`, record each drop."""
    r = route.Route(character, log)
    results = []
    for name in rooms:
        r.enter(name, freeze=False)
        enemy = live_enemy()
        if enemy is None:
            log(f"  {name}: no live enemy")
            continue
        room = rdt_map.load(survey.INSTALL, name, character)
        ap.send("heal")
        stand_off(room, enemy, 1200, 2500)
        hits, last, end, quiet = [], ap.hp(), time.time() + seconds, time.time()
        while time.time() < end:
            if time.time() - quiet > 5 and ap.in_control():
                # Many enemies wait for the player to come close or cross a trigger: walk up to the nearest one.
                target = live_enemy()
                if target:
                    stand_off(room, target, 400, 900)
                    ap.send("press LUp 600")
                quiet = time.time()
            now = ap.hp()
            if now < last:
                where = player_xz() or (0, 0)
                nearest = min(ap.enemies(), key=lambda e: distance(*where, e["x"], e["z"]), default=None)
                hits.append({"damage": last - now, "hp": now, "nearest": nearest and nearest["type"]})
                quiet = time.time()
                if now < 35:
                    ap.send("heal")
                    now = ap.hp()
            last = now
            time.sleep(0.1)
        results.append({"room": name, "type": enemy["type"], "hits": hits})
        log(f"  {name}: enemy type {enemy['type']}: {[h['damage'] for h in hits]}")
        if out is not None:
            (out / f"enemy-hits-{character}.json").write_text(json.dumps(results, indent=1) + "\n", encoding="utf-8")
    return results
