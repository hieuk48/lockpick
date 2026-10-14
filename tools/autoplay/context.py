r"""Grounded vision questions (dev-only autoplay): what the game's memory and Lockpick's tables already know, passed to
the vision model with the screenshot, so it describes what it sees in the light of facts instead of guessing.

    from context import ask_grounded
    ask_grounded(shot, "What is happening?")

The facts come from live memory (room, camera, character, HP, position, enemy slots, cutscene/message/menu flags) and
from data/games/re1 (room names, item and enemy names, what the room files place in the room). The model is told
they are reliable and asked to point out anything in the picture that disagrees — a mismatch is a finding.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import ap  # noqa: E402

DATA = Path(__file__).resolve().parents[2] / "data" / "games" / "re1"
_names = json.loads((DATA / "names.json").read_text(encoding="utf-8"))
_rooms = json.loads((DATA / "rooms.json").read_text(encoding="utf-8"))
_index = json.loads((DATA / "room-index.json").read_text(encoding="utf-8"))
ENEMY_BASE, ENEMY_STRIDE, ENEMY_HP = 0xBE6464, 0x18C, 0x88


def _enemy(t: int) -> str:
    return _names["enemies"].get(str(t), f"enemy type {t}")


def _item(i: int) -> str:
    return _names["items"].get(str(i), f"item {i}")


def facts() -> dict:
    s = ap.state()
    room = s.get("room", "?")
    character = (s.get("char") or "?").lower()
    flags = ap.flags()
    raw = ap.peek(ENEMY_BASE, ENEMY_STRIDE * 8)
    enemies = []
    for i in range(8):
        if raw[i * ENEMY_STRIDE]:
            hp = int.from_bytes(raw[i * ENEMY_STRIDE + ENEMY_HP:i * ENEMY_STRIDE + ENEMY_HP + 2], "little", signed=True)
            enemies.append(f"{_enemy(raw[i * ENEMY_STRIDE + 1])} ({hp} HP)")
    info = _rooms["rooms"].get(room, {})
    who = (character, "both")
    items = [_item(e["item"]) for e in _index["items"] if e["room"] == room and e["player"] in who]
    doors = sorted({e["target"] for e in _index["doors"] if e["room"] == room and e["player"] in who})
    status = [name for bit, name in ((0x100000, "a cutscene is playing (camera locked)"), (0x20000, "a voice line is playing"),
                                     (0x8000, "a message box is open"), (0x800, "the item viewer is open"),
                                     (0x80, "a door transition is running"), (0x01000000, "the player is dead"))
              if flags & bit]
    return {"room": room, "roomName": info.get("name"), "area": info.get("area") or _rooms["areas"].get(room[:1]),
            "camera": ap.peek(0xBE9822, 1)[0], "character": character.title(), "hp": s.get("hp"), "position": s.get("pos"),
            "enemies": enemies, "itemsPlacedByRoomFiles": items, "doorsLeadTo": doors,
            "status": status or ["the player has control"]}


def prompt(question: str, f: dict) -> str:
    lines = [f"Known game state (read from the game's memory and data tables — treat as reliable):",
             f"- Room {f['room']}" + (f" ({f['roomName']})" if f["roomName"] else "") + (f", {f['area']}" if f["area"] else "")
             + f", camera {f['camera']}",
             f"- Player: {f['character']}, HP {f['hp']}, position {f['position']}",
             f"- Active enemies: {', '.join(f['enemies']) or 'none'}",
             f"- Items the room files can place here (some only after events, some already taken): "
             f"{', '.join(f['itemsPlacedByRoomFiles']) or 'none'}",
             f"- Doors lead to rooms: {', '.join(f['doorsLeadTo']) or 'none'}",
             f"- Status: {'; '.join(f['status'])}",
             "Use these facts to interpret the image. Do not invent creatures, items or menus that the facts and the image "
             "don't support. If the image clearly contradicts a fact, say 'MISMATCH:' and what differs.",
             "", question]
    return "\n".join(lines)


def ask_grounded(image: Path, question: str, max_tokens: int = 250) -> tuple[str, dict]:
    f = facts()
    return ap.describe(image, prompt(question, f), max_tokens), f
