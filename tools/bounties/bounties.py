"""Capture bounty tooling.

data/bounties.json is the source of truth; docs/CAPTURE_BOUNTIES.md is generated from it.

    python tools/bounties/bounties.py render [--check]          regenerate (or verify) the Markdown list
    python tools/bounties/bounties.py analyze CAPTURE...        report what captures show (zip or trace .txt)
    python tools/bounties/bounties.py apply CAPTURE... [--source NAME]
                                                                record findings in bounties.json, then render
    python tools/bounties/bounties.py triage --body-env VAR --out report.md
                                                                (CI) download zips linked in an issue body, analyze

Only unambiguous evidence checks a bounty off automatically (e.g. Rebecca's character id together with her
inventory). Captures that record trainer memory writes never verify anything automatically.
Everything else is recorded as "evidence — needs review" so a maintainer confirms it.
Captures are untrusted input: zip members are read in memory by exact name with size limits, never extracted.
"""
from __future__ import annotations

import argparse
import datetime as dt
import io
import json
import os
import re
import struct
import sys
import urllib.request
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "data" / "bounties.json"
DOC = ROOT / "docs" / "CAPTURE_BOUNTIES.md"

STATUS_ICON = {"done": "✅", "partial": "🟡", "needed": "⬜", "blocked": "🛠️"}

# Item ids already identified from live captures (docs/MEMORY_MAP.md); anything else is reported as new.
KNOWN_ITEM_IDS = {0x01, 0x02, 0x03, 0x05, 0x07, 0x08, 0x0B, 0x0E, 0x11, 0x1F, 0x21, 0x23, 0x26, 0x29, 0x2D, 0x2F, 0x41,
                  0x44, 0x47}

MAX_MEMBER_BYTES = 64 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024
MAX_DOWNLOAD_BYTES = 100 * 1024 * 1024
MEMBER_PATTERN = re.compile(r"^(?:[^/]+/)?(trace-\d+\.txt|events-\d+\.txt|memory-\d+\.bin|markers\.jsonl|capture-info\.json)$")
MAX_REPORTED_MARKERS = 50
PROFILES = ROOT / "data" / "games" / "re1" / "profiles"
# Values that change continuously; a new state is only recorded when something else changes.
CONTEXT_FIELDS = {"gameTimer", "positionX", "positionY", "positionZ", "yaw", "_ms"}  # _ms: memory-log timestamp
# Enemy type ids as used by BioRand (MIT) for RE1 RDT enemy entries; names are hints until verified by captures.
ENEMY_NAMES = {0: "Zombie", 1: "Zombie (naked)", 2: "Cerberus (dog)", 3: "Web spinner", 4: "Black Tiger", 5: "Crow",
               6: "Hunter", 7: "Wasp", 8: "Plant 42", 9: "Chimera", 10: "Snake (Yawn)", 11: "Neptune / shark",
               12: "Tyrant", 13: "Yawn", 16: "Tyrant (final)", 17: "Zombie (researcher)", 18: "Yawn (2nd)"}
CONFIRMED_ENEMY_TYPES = {0, 2, 5, 17}  # identified by behavior in live captures (docs/MEMORY_MAP.md)
ENEMY_GROUPS = {"enemies_zombie": {0, 1, 17}, "enemies_dog_crow": {2, 5}, "enemies_hunter_chimera": {6, 9},
                "enemies_spider_wasp_shark": {3, 7, 11}, "enemies_boss": {4, 8, 10, 12, 13, 16, 18}}
FLAG_GROUPS = {"lock_flags": ("flagLocks",), "scenario_flags": ("flagScenarioA", "flagScenarioB"),
               "room_flags": ("flagRoom",), "room_item_flags": ("flagRoomItems",), "enemy_flags": ("flagEnemies",)}
ATTACHMENT_PATTERN = re.compile(r"https://github\.com/(?:user-attachments/files|[\w.-]+/[\w.-]+/files)/\d+/[\w.%-]+\.zip")
TRACE_LINE = re.compile(r"^\[(\d\d:\d\d:\d\d\.\d{3})\] (.*?)(?: \| (.*))?$")
PAIR = re.compile(r"(\w+)=0x([0-9A-Fa-f]+)")

HEADER = """# Capture bounties

<!-- Generated from data/bounties.json by tools/bounties/bounties.py — edit the JSON, then run `render`. -->

> **Unpaid, captures only, no bots** — see the rules below.

## Bounty rules

1. **Bounties are unpaid.** No money, crypto or other payment is ever offered or owed. The reward is your name in
   Lockpick's in-app credits (Settings → Credits) and in CONTRIBUTORS.md, if you want it.
2. **Bounties are captures, never code changes.** A bounty is completed only by a capture zip of you playing the
   game (Debug → Start capture). Patches, pull requests or "proposed fixes" don't complete bounties and are never
   rewarded. Code contributions are welcome separately under the normal [contributing guide](../CONTRIBUTING.md).
3. **No automated or AI-generated replies.** Comments, issues or pull requests produced by bots or AI tools are hidden
   as spam and the account may be blocked and reported. Payment requests are ignored.

## How it works

Everything Lockpick needs to see the real game do, grouped by the release that needs it. Each bounty is a short
thing to do in-game while a capture is running (**Debug → Start capture**, see [CAPTURE.md](CAPTURE.md)). One capture
can cover many bounties — just play and send the zip with a
[Capture submission](https://github.com/mscrnt/lockpick/issues/new?template=capture-submission.yml) issue.

Captures record the game's whole state memory, so a capture can complete any bounty below except the ones marked
🛠️ (they need data from outside the game's memory, such as the window position or screenshots).

Submitted captures are analyzed automatically: unambiguous results (for example Rebecca's character id appearing)
are checked off, and everything else is marked as evidence for a maintainer to review. Open bounties for the next
release are also GitHub issues with the [`bounty` label](https://github.com/mscrnt/lockpick/issues?q=is%3Aissue+label%3Abounty).

Status: ✅ verified from a capture · 🟡 partly verified / evidence to review · ⬜ needed · 🛠️ needs a Lockpick feature first

Unless noted, bounties are for the supported build (GOG, USA, 1.0 hotfix4).
"""


# ----------------------------------------------------------------------------------------------------------------
# Data and rendering
# ----------------------------------------------------------------------------------------------------------------

def load_data(path: Path = DATA) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def save_data(data: dict, path: Path = DATA) -> None:
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def render(data: dict) -> str:
    out = [HEADER]
    for release in data["releases"]:
        out.append(f"\n---\n\n## {release['release']} — {release['title']}\n")
        out.append("| ID | Do this in-game | Verifies | Status |")
        out.append("|---|---|---|---|")
        for b in release["bounties"]:
            status = STATUS_ICON[b["status"]]
            if b.get("evidence"):
                status += " " + "; ".join(b["evidence"])
            out.append(f"| {b['id']} | {b['task']} | {b['verifies']} | {status} |")
    return "\n".join(out) + "\n"


def all_bounties(data: dict):
    for release in data["releases"]:
        yield from release["bounties"]


# ----------------------------------------------------------------------------------------------------------------
# Capture parsing
# ----------------------------------------------------------------------------------------------------------------

def signed(value: int, bits: int) -> int:
    value &= (1 << bits) - 1
    return value - (1 << bits) if value >> (bits - 1) else value


@dataclass
class Session:
    name: str
    states: list[dict[str, int]] = field(default_factory=list)  # full raw state after each trace line
    events: list[str] = field(default_factory=list)


@dataclass
class Capture:
    name: str
    sessions: list[Session] = field(default_factory=list)
    builds: list[dict] = field(default_factory=list)
    # Number of trainer memory writes recorded in the capture (0.2+ captures record this; older ones can't write).
    trainer_writes: int = 0
    # Moments the player marked while capturing (markers.jsonl): number, at, room, gameSeconds, note.
    markers: list[dict] = field(default_factory=list)


def load_profile_fields(profile_id: str | None) -> list[dict]:
    """Fields of a version profile (comments allowed), as dicts with name/address/type/mask."""
    for path in sorted(PROFILES.glob("*.json")):
        profile = json.loads(re.sub(r"^\s*//.*$", "", path.read_text(encoding="utf-8"), flags=re.M))
        if profile_id in (None, profile["id"]):
            return [{"name": f["name"], "address": int(f["address"], 16), "type": f["type"],
                     "mask": int(f["mask"], 16) if f.get("mask") else None} for f in profile["fields"]]
    return []


FORMATS = {"u8": "<B", "u16": "<H", "i16": "<H", "u32": "<I", "i32": "<I"}  # raw unsigned, like the trace


def parse_memory(name: str, payload: bytes, fields: list[dict]) -> Session:
    """Replays a memory-N.bin region log and decodes every profile field that lies inside a recorded region."""
    session = Session(name)
    view = memoryview(payload)
    if bytes(view[:6]) != b"LPKMEM" or view[6] != 1:
        raise ValueError(f"{name} is not a Lockpick memory log")
    pos = 7
    (count,) = struct.unpack_from("<H", view, pos)
    pos += 2
    regions = []
    for _ in range(count):
        length = view[pos]
        region_name = bytes(view[pos + 1:pos + 1 + length]).decode("utf-8", "replace")
        pos += 1 + length
        address, size = struct.unpack_from("<QI", view, pos)
        pos += 12
        if size > MAX_MEMBER_BYTES:
            raise ValueError("memory region larger than the analysis limits")
        regions.append((region_name, address, size))
    state = [bytearray(size) for _, _, size in regions]
    present = [False] * len(regions)
    located = []
    for f in fields:
        width = struct.calcsize(FORMATS[f["type"]])
        for index, (_, address, size) in enumerate(regions):
            if address <= f["address"] and f["address"] + width <= address + size:
                located.append((f, index, f["address"] - address))
                break
    last_key = None
    while pos < len(view):
        kind = view[pos]
        (at_ms,) = struct.unpack_from("<q", view, pos + 1)  # Unix milliseconds
        pos += 9
        if kind == 0:
            for index, (_, _, size) in enumerate(regions):
                flag = view[pos]
                pos += 1
                if flag:
                    state[index][:] = view[pos:pos + size]
                    present[index] = True
                    pos += size
        elif kind == 1:
            (runs,) = struct.unpack_from("<H", view, pos)
            pos += 2
            for _ in range(runs):
                index, offset, length = struct.unpack_from("<HIH", view, pos)
                pos += 8
                if index >= len(regions) or offset + length > regions[index][2]:
                    raise ValueError("corrupt memory log")
                state[index][offset:offset + length] = view[pos:pos + length]
                pos += length
        else:
            raise ValueError("corrupt memory log")
        values = {"_ms": at_ms}
        for f, index, offset in located:
            if present[index]:
                (v,) = struct.unpack_from(FORMATS[f["type"]], state[index], offset)
                values[f["name"]] = v & f["mask"] if f["mask"] is not None else v
        key = tuple(v for k, v in sorted(values.items()) if k not in CONTEXT_FIELDS)
        if key != last_key:
            session.states.append(values)
            last_key = key
    return session


def parse_trace(name: str, text: str) -> Session:
    session = Session(name)
    state: dict[str, int] = {}
    for line in text.splitlines():
        m = TRACE_LINE.match(line.strip())
        if not m:
            continue
        for key, value in PAIR.findall(m.group(2)):
            state[key] = int(value, 16)
        context = m.group(3) or ""
        if (t := re.search(r"timer=0x([0-9A-Fa-f]+)", context)) is not None:
            state["gameTimer"] = int(t.group(1), 16)
        if (p := re.search(r"pos=0x([0-9A-Fa-f]+),0x([0-9A-Fa-f]+)", context)) is not None:
            state["positionX"], state["positionZ"] = int(p.group(1), 16), int(p.group(2), 16)
        session.states.append(dict(state))
    return session


def read_capture(path: Path) -> Capture:
    if path.suffix.lower() != ".zip":
        return Capture(path.name, [parse_trace(path.name, path.read_text(encoding="utf-8", errors="replace"))])
    return read_capture_zip(path.name, path.read_bytes())


def parse_markers(text: str) -> list[dict]:
    """Player markers (one JSON object per line); malformed lines are skipped, notes are single short lines."""
    markers = []
    for line in text.splitlines():
        try:
            m = json.loads(line)
        except json.JSONDecodeError:
            continue
        if not isinstance(m, dict) or not isinstance(m.get("number"), int):
            continue
        seconds = m.get("gameSeconds")
        markers.append({
            "number": m["number"],
            "at": str(m.get("at", ""))[11:19],  # HH:mm:ss of the ISO timestamp
            "atFull": str(m.get("at", ""))[:40],
            "room": str(m["room"])[:8] if m.get("room") else None,
            "gameSeconds": float(seconds) if isinstance(seconds, (int, float)) else None,
            "note": " ".join(str(m.get("note", "")).split())[:200],
            "kind": m.get("kind") if m.get("kind") in ("marker", "answer", "skipped", "auto") else "marker",
            "changes": [str(c)[:200] for c in m.get("changes") or [] if isinstance(c, str)][:12],
        })
    return markers


def read_capture_zip(name: str, payload: bytes) -> Capture:
    capture = Capture(name)
    with zipfile.ZipFile(io.BytesIO(payload)) as archive:
        members = [i for i in archive.infolist() if MEMBER_PATTERN.match(i.filename)]
        if sum(i.file_size for i in members) > MAX_TOTAL_BYTES or any(i.file_size > MAX_MEMBER_BYTES for i in members):
            raise ValueError("capture is larger than the analysis limits")
        traces, events, memory = {}, {}, {}
        for info in members:
            base = info.filename.rsplit("/", 1)[-1]
            if base.startswith("memory-"):
                memory[base.replace("memory-", "trace-").replace(".bin", ".txt")] = archive.read(info)
                continue
            text = archive.read(info).decode("utf-8", errors="replace")
            if base == "capture-info.json":
                try:
                    info_json = json.loads(text)
                    capture.builds = info_json.get("Builds", [])
                    capture.trainer_writes = int(info_json.get("TrainerWrites", 0) or 0)
                except (json.JSONDecodeError, AttributeError, TypeError, ValueError):
                    capture.builds = []
            elif base == "markers.jsonl":
                capture.markers = parse_markers(text)
            elif base.startswith("trace-"):
                traces[base] = text
            elif base.startswith("events-"):
                events[base.replace("events-", "trace-")] = text
        profile_id = next((b.get("ProfileId") for b in capture.builds if b.get("ProfileId")), None)
        fields = load_profile_fields(profile_id) if memory else []
        for trace_name in sorted(set(traces) | set(memory), key=lambda n: int(re.sub(r"\D", "", n) or 0)):
            # The memory log holds every value (including ones the trace didn't list); prefer it when present.
            if trace_name in memory and fields:
                session = parse_memory(f"{name}/{trace_name.replace('trace-', 'memory-').replace('.txt', '.bin')}", memory[trace_name], fields)
            else:
                session = parse_trace(f"{name}/{trace_name}", traces.get(trace_name, ""))
            session.events = [l for l in events.get(trace_name, "").splitlines() if l.strip()]
            capture.sessions.append(session)
    return capture


# ----------------------------------------------------------------------------------------------------------------
# Detection
# ----------------------------------------------------------------------------------------------------------------

@dataclass
class Finding:
    detector: str
    text: str
    auto: bool  # True: unambiguous, may check the bounty off; False: evidence for review


ROOM_SETTLE_FRAMES = 30  # one second of game time after entering a room


def in_game(s: dict[str, int]) -> bool:
    return s.get("mainMenu", 1) == 0 and s.get("maxHp", 0) > 0 and signed(s.get("hp", 0), 16) > 0


def detect(capture: Capture) -> tuple[dict[str, Finding], dict[str, object]]:
    """Returns findings keyed by detector name, plus general facts for the report."""
    found: dict[str, Finding] = {}
    rooms: dict[int, set[int]] = {}
    enemies: dict[int, set[str]] = {}
    kills: dict[int, int] = {}
    flag_changes: dict[str, set[str]] = {}
    item_ids: set[int] = set()
    facts: dict[str, object] = {}

    def add(detector: str, text: str, auto: bool) -> None:
        found.setdefault(detector, Finding(detector, text, auto))

    restarts = max(0, len(capture.sessions) - 1)
    for session in capture.sessions:
        prev: dict[str, int] | None = None
        died = False
        equipped: set[int] = set()
        last_room = None
        room_changes = 0
        entered_at = 0
        for s in session.states:
            game = in_game(s)
            char = s.get("character", 0) & 3
            hp = signed(s.get("hp", 0), 16)
            if game:
                rooms.setdefault(s.get("stage", 0), set()).add(s.get("room", 0))
                if char == 0:
                    add("chris_game", f"Chris game (character 0, max HP {s.get('maxHp')})", True)
                elif char == 1:
                    add("jill_game", f"Jill game (character 1, max HP {s.get('maxHp')})", True)
                if char == 3:
                    # Both signals appear together in real play; one alone could be a memory edit, so review it.
                    both = s.get("inventoryPtr") == 0xBE9950
                    add("rebecca", "character id 3 with Rebecca's inventory (0xBE9950)" if both else "character id 3 (Rebecca)", both)
                if s.get("stage") == 1:
                    add("second_floor", f"stage index 1 (room 2{s.get('room', 0):02X})", True)
                room = (s.get("stage"), s.get("room"))
                if last_room is not None and room != last_room:
                    room_changes += 1
                if room != last_room or prev is None or not in_game(prev):
                    entered_at = s.get("gameTimer", 0)
                last_room = room
                for i in range(8):
                    if (v := s.get(f"inv{i}")) is not None and v & 0xFF:
                        item_ids.add(v & 0xFF)
                for key, v in s.items():
                    if key.startswith("box") and v & 0xFF:
                        item_ids.add(v & 0xFF)
                if (slot := s.get("equippedSlot")) is not None and slot > 0:
                    equipped.add(slot)
                slots = [s.get(f"inv{i}", 0) & 0xFF for i in range(8)]
                limit = 8 if char == 1 else 6
                if all(slots[:limit]):
                    add("full_inventory", f"all {limit} inventory slots filled", False)
            if s.get("inventoryPtr") == 0xBE9950 and char != 3:
                add("rebecca", "inventory pointer at Rebecca's inventory (0xBE9950)", False)

            # Enemy slots hold leftovers from the previous room for a few frames after a room loads.
            settled = game and s.get("gameTimer", 0) - entered_at >= ROOM_SETTLE_FRAMES
            if settled:
                room_id = f"{s.get('stage', 0) + 1}{s.get('room', 0):02X}"
                for slot in range(30):
                    if f"enemy{slot}Flags" not in s:
                        break
                    if s[f"enemy{slot}Flags"] & 1 and signed(s.get(f"enemy{slot}Hp", 0), 16) > 0:
                        enemy_type = s.get(f"enemy{slot}Type", -1)
                        enemies.setdefault(enemy_type, set()).add(room_id)
            if prev is not None:
                prev_hp = signed(prev.get("hp", 0), 16)
                playing = game and in_game(prev)
                same_room = (s.get("stage"), s.get("room")) == (prev.get("stage"), prev.get("room"))
                for slot in range(30 if playing and same_room and settled else 0):
                    flags, hp_key = f"enemy{slot}Flags", f"enemy{slot}Hp"
                    if flags not in s or flags not in prev:
                        break
                    before, after = signed(prev.get(hp_key, 0), 16), signed(s.get(hp_key, 0), 16)
                    if prev[flags] & 1 and before > 0 and after <= 0:
                        enemy_type = s.get(f"enemy{slot}Type", -1)
                        kills[enemy_type] = kills.get(enemy_type, 0) + 1
                for detector, prefixes in FLAG_GROUPS.items():
                    for key in s:
                        if playing and key.startswith(prefixes) and key in prev and s[key] != prev[key]:
                            flag_changes.setdefault(detector, set()).add(key)
                if prev.get("maxHp", 0) > 0 and prev_hp > 0 and hp <= 0:
                    died = True
                    add("death", f"HP {prev_hp} → {hp}", True)
                if died and game and s.get("gameTimer", 1) == 0:
                    add("new_game_after_death", "new game started after a death", True)
                if game and in_game(prev) and hp > prev_hp and s.get("gameTimer", 0) != 0:
                    add("healing", f"HP {prev_hp} → {hp}", False)
                old_t, new_t = prev.get("gameTimer"), s.get("gameTimer")
                if game and old_t is not None and new_t is not None and new_t < old_t and new_t >= 60:
                    add("save_loaded", f"timer restored {old_t / 30:.0f}s → {new_t / 30:.0f}s", True)
                for i in range(8 if playing else 0):
                    key = f"inv{i}"
                    if key in s and key in prev and (s[key] & 0xFF) == (prev[key] & 0xFF) and s[key] & 0xFF:
                        before, after = prev[key] >> 8, s[key] >> 8
                        if after == before - 1 and s.get("equippedSlot") == i + 1:
                            add("fire_and_reload", f"weapon id 0x{s[key] & 0xFF:02X} fired (slot {i + 1})", False)
                for key in [k for k in s if k.startswith("box")] if playing else []:
                    i = int(key[3:])
                    if key in prev and s[key] != prev[key]:
                        add("item_box_changed", f"item box entry {i + 1}: 0x{prev[key]:04X} → 0x{s[key]:04X}", False)
            prev = s
        if room_changes >= 2:
            add("room_changes", f"{room_changes} room changes", True)
        if len(equipped) >= 3:
            add("weapon_switching", f"equipped slots used: {sorted(equipped)}", False)
        if any("Save loaded" in e or "New game started" in e for e in session.events):
            restarts += 1
    if restarts:
        add("resets", f"{restarts} restart(s) / reload(s)", False)

    by_label: dict[str, list[str]] = {}
    for stage, room_set in sorted(rooms.items()):
        label = "rooms_stage_6_7" if stage in (5, 6) else f"rooms_stage_{stage + 1}"
        by_label.setdefault(label, []).extend(f"{stage + 1}{r:02X}" for r in sorted(room_set))
    for label, ids in by_label.items():
        found[label] = Finding(label, " ".join(ids), False)

    for build in capture.builds:
        if not build.get("ProfileId"):
            add("other_build", f"unsupported build {build.get('FileName')} {build.get('FileSize')} bytes, SHA-256 {str(build.get('Sha256'))[:16]}…", False)

    def enemy_name(t: int) -> str:
        if t not in ENEMY_NAMES:
            return f"type {t}"
        return f"type {t} ({ENEMY_NAMES[t]})" if t in CONFIRMED_ENEMY_TYPES else f"type {t} ({ENEMY_NAMES[t]}?)"

    for detector, types in ENEMY_GROUPS.items():
        seen = sorted(t for t in enemies if t in types)
        if seen:
            add(detector, "; ".join(f"{enemy_name(t)} in {' '.join(sorted(enemies[t]))}" for t in seen), False)
    if kills:
        add("enemy_killed", ", ".join(f"{enemy_name(t)} ×{n}" for t, n in sorted(kills.items())), False)
    for detector, keys in flag_changes.items():
        add(detector, f"{len(keys)} flag word(s) changed: {', '.join(sorted(keys)[:8])}{' …' if len(keys) > 8 else ''}", False)

    if capture.trainer_writes:
        for key, finding in list(found.items()):
            found[key] = Finding(finding.detector, finding.text + " (capture includes trainer writes)", False)

    facts["rooms"] = {stage + 1: sorted(r) for stage, r in sorted(rooms.items())}
    facts["item_ids"] = sorted(item_ids)
    facts["new_item_ids"] = sorted(item_ids - KNOWN_ITEM_IDS)
    facts["enemies"] = {enemy_name(t): sorted(rs) for t, rs in sorted(enemies.items())}
    facts["kills"] = {enemy_name(t): n for t, n in sorted(kills.items())}
    facts["builds"] = capture.builds
    facts["sessions"] = len(capture.sessions)
    facts["samples"] = sum(len(s.states) for s in capture.sessions)
    return found, facts


def match(data: dict, findings: dict[str, Finding]) -> list[tuple[dict, Finding]]:
    return [(b, findings[b["detector"]]) for b in all_bounties(data) if b.get("detector") in findings]


# ----------------------------------------------------------------------------------------------------------------
# Report and apply
# ----------------------------------------------------------------------------------------------------------------

def report(data: dict, capture: Capture, findings: dict[str, Finding], facts: dict[str, object]) -> str:
    lines = [f"### Lockpick capture analysis — `{capture.name}`", ""]
    builds = facts["builds"] or []
    for b in builds:
        state = f"supported (`{b.get('ProfileId')}`)" if b.get("ProfileId") else "**not supported yet** — thanks, the fingerprint helps"
        lines.append(f"- Game build: `{b.get('FileName')}` {b.get('FileSize')} bytes, SHA-256 `{str(b.get('Sha256'))[:16]}…` — {state}")
    lines.append(f"- {facts['sessions']} game session(s), {facts['samples']:,} recorded changes")
    matched = match(data, findings)
    auto = [(b, f) for b, f in matched if f.auto and b["status"] != "done"]
    review = [(b, f) for b, f in matched if not f.auto and b["status"] != "done"]
    already = [(b, f) for b, f in matched if b["status"] == "done"]
    if auto:
        lines += ["", "**Verified automatically:**"] + [f"- {b['id']} — {f.text}" for b, f in auto]
    if review:
        lines += ["", "**Evidence for a maintainer to review:**"] + [f"- {b['id']} — {f.text}" for b, f in review]
    if already:
        lines += ["", "Also seen (already verified): " + ", ".join(sorted({b["id"] for b, _ in already}))]
    rooms = facts["rooms"] or {}
    if rooms:
        lines += ["", "**Rooms seen:** " + "; ".join(f"stage {s}: " + " ".join(f"{s}{r:02X}" for r in rs) for s, rs in rooms.items())]
    if facts.get("enemies"):
        lines.append("**Enemies seen:** " + "; ".join(f"{t} in {' '.join(rs)}" for t, rs in facts["enemies"].items()))
    if facts.get("kills"):
        lines.append("**Enemies killed:** " + ", ".join(f"{t} ×{n}" for t, n in facts["kills"].items()))
    if facts["item_ids"]:
        new = ", ".join(f"0x{i:02X}" for i in facts["new_item_ids"]) or "none"
        lines.append(f"**Item ids seen:** {', '.join(f'0x{i:02X}' for i in facts['item_ids'])} (new: {new})")
    if capture.markers:
        autos = [m for m in capture.markers if m["kind"] == "auto"]
        if autos:
            lines += ["", f"_{len(autos)} story change(s) were put down to the player's actions (see the timeline)._"]
        shown = [m for m in capture.markers if m["kind"] != "auto"]
        lines += ["", "**Markers:**"] if shown else []
        for m in shown[:MAX_REPORTED_MARKERS]:
            where = [m["at"]]
            if m["room"]:
                where.append(f"room {m['room']}")
            if m["gameSeconds"] is not None:
                t = int(m["gameSeconds"])
                where.append(f"game {t // 3600}:{t // 60 % 60:02}:{t % 60:02}")
            # Notes are player text: shown as code so they can't mention people, link or format the comment.
            note = m["note"].replace("`", "'")
            kind = {"answer": "answered: ", "skipped": "asked, skipped"}.get(m["kind"], "")
            asked = f" (asked about: {'; '.join(m['changes'])})" if m["changes"] else ""
            detail = kind + (f"`{note}`" if note else "") + asked
            lines.append(f"- #{m['number']} {' · '.join(where)}" + (f" — {detail}" if detail else ""))
        if len(shown) > MAX_REPORTED_MARKERS:
            lines.append(f"- … and {len(shown) - MAX_REPORTED_MARKERS} more")
    if not matched and not rooms:
        reason = ("it only contains the title screen or attract-mode demos — start or load a game while capturing"
                  if facts["samples"] else "no game data was recorded — was the game running while capturing?")
        lines += ["", f"No bounty evidence found: {reason}."]
    lines += ["", "_Automated summary. A maintainer confirms results before bounties are checked off. "
              "Bounties are unpaid; the reward is an in-app credit if you want it._"]
    return "\n".join(lines) + "\n"


def apply(data: dict, findings: dict[str, Finding], source: str, today: str) -> list[str]:
    changes = []
    for bounty, finding in match(data, findings):
        evidence = bounty.setdefault("evidence", [])
        if bounty["detector"].startswith("rooms_stage"):
            seen = set()
            for e in evidence:
                seen.update(re.findall(r"\b[1-7][0-9A-F]{2}\b", e))
            seen.update(finding.text.split())
            merged = "rooms seen: " + " ".join(sorted(seen, key=lambda r: int(r, 16)))
            bounty["evidence"] = [merged]
            if bounty["status"] == "needed":
                bounty["status"] = "partial"
            changes.append(f"{bounty['id']}: {merged}")
            continue
        if bounty["status"] == "done":
            continue
        if finding.auto:
            bounty["status"] = "done"
            bounty["evidence"] = [f"{today} ({source}): {finding.text}"]
            changes.append(f"{bounty['id']}: verified — {finding.text}")
        else:
            note = f"evidence in {source}: {finding.text} — needs review"
            if note not in evidence:
                evidence.append(note)
            if bounty["status"] == "needed":
                bounty["status"] = "partial"
            changes.append(f"{bounty['id']}: {note}")
    return changes


# ----------------------------------------------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------------------------------------------

def cmd_render(args) -> int:
    text = render(load_data())
    if args.check:
        current = DOC.read_text(encoding="utf-8") if DOC.exists() else ""
        if current.replace("\r\n", "\n") != text:
            print("docs/CAPTURE_BOUNTIES.md is out of date: run `python tools/bounties/bounties.py render`", file=sys.stderr)
            return 1
        print("docs/CAPTURE_BOUNTIES.md is up to date")
        return 0
    DOC.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {DOC.relative_to(ROOT)}")
    return 0


def cmd_analyze(args) -> int:
    data = load_data()
    for path in args.captures:
        capture = read_capture(Path(path))
        findings, facts = detect(capture)
        print(report(data, capture, findings, facts))
    return 0


def cmd_apply(args) -> int:
    data = load_data()
    today = dt.date.today().isoformat()
    for path in args.captures:
        capture = read_capture(Path(path))
        findings, _ = detect(capture)
        for change in apply(data, findings, args.source or capture.name, today):
            print(change)
    save_data(data)
    DOC.write_text(render(data), encoding="utf-8", newline="\n")
    return 0


def cmd_triage(args) -> int:
    body = os.environ.get(args.body_env, "")
    urls = list(dict.fromkeys(ATTACHMENT_PATTERN.findall(body)))
    data = load_data()
    parts = []
    if not urls:
        parts.append("### Lockpick capture analysis\n\nNo capture zip was found in this issue. Drag the "
                     "`LockpickCapture-*.zip` file into the issue (edit it) and it will be analyzed automatically.\n")
    for url in urls[:5]:
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "lockpick-capture-triage"})
            with urllib.request.urlopen(request, timeout=60) as response:  # noqa: S310 - URL host validated above
                payload = response.read(MAX_DOWNLOAD_BYTES + 1)
            if len(payload) > MAX_DOWNLOAD_BYTES:
                raise ValueError("zip is larger than 100 MB")
            capture = read_capture_zip(url.rsplit("/", 1)[-1], payload)
            findings, facts = detect(capture)
            parts.append(report(data, capture, findings, facts))
        except (OSError, ValueError, zipfile.BadZipFile) as error:
            parts.append(f"### Lockpick capture analysis\n\nCouldn't analyze `{url.rsplit('/', 1)[-1]}`: {error}\n")
    marker = "<!-- lockpick-capture-triage: " + " ".join(urls) + " -->"
    Path(args.out).write_text(marker + "\n" + "\n".join(parts), encoding="utf-8")
    return 0


def main(argv: list[str] | None = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")  # Windows consoles default to a legacy code page
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("render")
    p.add_argument("--check", action="store_true")
    p.set_defaults(func=cmd_render)
    p = sub.add_parser("analyze")
    p.add_argument("captures", nargs="+")
    p.set_defaults(func=cmd_analyze)
    p = sub.add_parser("apply")
    p.add_argument("captures", nargs="+")
    p.add_argument("--source")
    p.set_defaults(func=cmd_apply)
    p = sub.add_parser("triage")
    p.add_argument("--body-env", required=True)
    p.add_argument("--out", required=True)
    p.set_defaults(func=cmd_triage)
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
