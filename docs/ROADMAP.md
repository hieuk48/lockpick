# Lockpick Roadmap

Each release must be functional, tested and a meaningful improvement. A release is **not** advanced
because its code compiles: game-specific acceptance criteria require validation against the real GOG
executable, and unverified work stays marked as such.

Status legend: ✅ done and verified · 🟡 done, partially verified · ⬜ not started · ⛔ blocked

| Version | Milestone | Status |
|---|---|---|
| 0.1.0 | Game detection, memory reading, application foundation | ✅ released 2026-10-12 |
| 0.1.1 | Installer + auto-update, first-time setup, in-app capture (Debug page) | ✅ released 2026-10-13 |
| 0.1.2 | In-app credits, capture bounties, community setup | ✅ released 2026-10-13 (first update path 0.1.1 → 0.1.2) |
| 0.1.3 | Bounty automation, capture fixes from live testing (compact recordings, mid-session start) | ✅ released 2026-10-13 |
| 0.1.4 | Captures record the game-state memory; enemy and flag detection; grouped reads | ✅ released 2026-10-13 |
| 0.1.5 | Research tooling: capture markers, ask mode, room index, capture timeline, subtitles pack | ✅ released 2026-10-13 |
| 0.1.6 | Progress tracker (read-only): items left, documents, maps, rooms explored, kills, story progress | 🟡 built; needs the live check |
| 0.2.0 | Core trainer functionality and memory modification | ⬜ |
| 0.3.0 | Real-time overlay and customizable HUD | ⬜ |
| 0.4.0 | Room, camera, item and enemy database | ⬜ |
| 0.5.0 | Object highlighting and ESP | ⬜ |
| 0.6.0 | Advanced trainer and gameplay manipulation | ⬜ |
| 0.7.0 | Contextual warnings and route guidance | ⬜ |
| 0.8.0 | Speedrunning and challenge-run suite | ⬜ |
| 0.9.0 | Optional AI, optimization, compatibility testing | ⬜ |
| 1.0.0 | Stable RE1 release | ⬜ |

Game-specific work depends on live captures; what each release still needs captured is tracked in
[CAPTURE_BOUNTIES.md](CAPTURE_BOUNTIES.md).

RE2 and RE3 adapters are deliberately out of scope until RE1 reaches 1.0, but the adapter contract
(`IGameAdapter`, profile JSON) is designed so they can be added without restructuring.

---

## 0.1.0 — Foundation (done)

Goal: identify Resident Evil 1 (GOG), attach safely and read-only, expose live verified game state.

### Definition of done

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | The application builds successfully | ✅ | `dotnet build -c Release` with warnings-as-errors; CI workflow `ci.yml` |
| 2 | The desktop interface launches | ✅ | Launched and screenshotted on the dev machine (wide and portrait layouts) |
| 3 | Game detection works | ✅ | Running `ResidentEvil.exe` found by name and full path; GOG install located via registry |
| 4 | Executable fingerprinting works | ✅ | SHA-256 + PE header of the real GOG exe matched profile `re1-gog-usa-hotfix4` |
| 5 | Process attachment is safe | ✅ | Read-only handle (`PROCESS_VM_READ`, query, synchronize); in-memory PE header and 9 code anchors verified before any read; reads confined to the image range |
| 6 | Verified memory values are readable and correctly displayed | ✅ | Stage, room, camera, HP, max HP, character, timer, position, main-menu flag observed live in Chris and Jill games (Jill: id 1, max HP 96, Jill RDT bit). Rebecca id remains static-only (documented). |
| 7 | Game-state changes update the interface | ✅ | Live: camera cuts, door transitions, damage, death, return to title, restart |
| 8 | The application safely handles game termination | ✅ | Live: game closed → `Detached: Game process exited`; relaunch → re-attached automatically |
| 9 | Unsupported builds are rejected | ✅ | Hash mismatch never opens the process (unit tests); elevated game reports a clear access message (observed live) |
| 10 | Mock adapter tests pass | ✅ | 70+ tests incl. mock adapter, fake memory, live-trace fixture playback, Win32 integration |
| 11 | Known limitations are documented | ✅ | README, COMPATIBILITY, this file |

**Release decision:** all criteria met; tagged `v0.1.0` on 2026-10-12 after a live Jill session confirmed
character id 1, max HP 96 and the Jill RDT bit.

### Follow-ups (0.1.x, non-blocking)

- [x] Live-verify Jill (character id 1, max HP 96, Jill RDT bit).
- [ ] Live-verify the Rebecca partner swap (character id 3).
- [x] Live-verify loading a save from the title screen (session rule holds; see RESEARCH.md §7).
- [ ] Push to GitHub so `release.yml` publishes the tagged build.

---

## 0.1.5 — Research tooling (released)

Built and used in live sessions 6–8 (2026-10-13); see CHANGELOG `[Unreleased]`.

- Capture markers: Ctrl+Shift+M (configurable) or the Debug page; notes saved in the capture.
- Ask mode: pauses the game and asks about events Lockpick can't explain; predicts rooms, maps, documents,
  room-script events and action-driven events, so it asks only about cutscenes, surprises and switch puzzles
  (session 8: 10 questions in ~90 minutes). ~85 story/puzzle events named so far (`data/games/re1/flag-labels.json`).
- Room index from the game's room files (`tools/rdt`): items, enemies, doors and story flags per room — the core of
  the 0.4.0 world database, as a research tool.
- Capture timeline (`tools/bounties/timeline.py`): rooms, pickups, documents, kills, attacks by name, pushes, item
  use, saves, markers and answers.
- Voice-over transcription (`tools/voice`) and the separate subtitles pack (data for 0.3.0).

---

## 0.1.6 — Progress tracker (next)

Pulled forward from 0.7.0 (owner's decision, 2026-10-13): read-only and Challenge-safe, and it needs no new memory
research — the room index, the flag banks and the named flags already hold everything.

Acceptance criteria:
- A Progress page: per area and per room, the items still there vs taken (room index + pickup flags), documents and
  maps collected, rooms explored (the map's own flags, all five stages), enemies killed for good (kill flags).
- Story progress from the named flags (what has happened, in game order), unnamed flags shown as "unnamed event".
- Character-aware (Jill's and Chris's room files differ) and correct for the mansion revisited (6xx/7xx).
- Updates live while playing; works from a loaded save (flags, not history).
- Verified live against a playthrough: counts match what the player saw; no writes, no extra process access.

---

## 0.2.0 — Core trainer

Goal: deliberate, validated memory modification in Trainer Mode, with Challenge Mode enforcing no writes.

Acceptance criteria:
- Separate write-capable handle requested only when Trainer Mode is enabled (`PROCESS_VM_WRITE | PROCESS_VM_OPERATION`); Challenge Mode never opens one.
- Every write goes through a typed, profile-declared writable field with bounds (e.g. HP 1..maxHp) and is read back to confirm.
- Trainer action log (persisted) with timestamp, field, old → new value.
- Features: set/lock HP, infinite ammo, inventory viewer/editor (8 slots, id + quantity at `0xBE9944`), item box viewer.
  Inventory layout, ammo semantics and equipped slot are already runtime-verified (read side); the item box is not.
- Item id table (from biorand, MIT, with attribution) verified against the in-game inventory — about 60 ids
  confirmed in play so far (`data/games/re1/names.json`; the bazooka ids 7/8 corrected from play). The item box is
  runtime-verified.
- Hotkeys (global, configurable) with no effect when the game is not attached.
- Run record notes whether any write occurred (`RunInvalidated` event).
- **Save tools** (writes only save files, never the process; always a backup first and an exact restore, never in
  Challenge Mode): save reader (done as `tools/bounties/saves.py`: a save is a copy of the state block — offset +
  `0xBE9620`), a **scene library / practice mode** (put any practice save into a spare slot: Plant 42, Lab Tyrant,
  Rebecca…), and a **save editor** (inventory, item box, story flags) once it is known whether saves carry a checksum.
- All writable addresses runtime-verified on the GOG build; tests with fake memory for every write path.

## 0.3.0 — Overlay and HUD

- Transparent, click-through overlay window tracking the game window (position, size, DPI).
- Game-window placement helper (move/resize the game window to a chosen monitor/position).
- Configurable HUD widgets (HP, room, timer, events) with < 2% game frame-time impact (measured).
- HUD data that is already understood (read-only):
  - HP status as the game rates it — Jill Fine 73–96 / Caution 25–48 / Danger 0–24, Chris 106 / 36 / 0
    thresholds (speedrun damage chart, matches the GOG build);
  - poisoned indicator (poison shows as −2 HP every 4 s; a status flag would be better — research);
  - enemy health in the current room (enemy slots: type, HP, active);
  - what just happened: an event feed in plain words (picked up the Emblem, Zombie ankle bite (6), first visit to
    room 11D, Barry gives Jill the lockpick) — the capture timeline's logic in the app;
  - current room and area by name (areas from the game's room table; names as they are confirmed).
- Subtitles for the voice-over (accessibility): optional download of the separate
  [lockpick-subtitles](https://github.com/mscrnt/lockpick-subtitles) pack (off by default; checked against the
  player's voice files by SHA-256), shown in the overlay while a line plays. Needs: which voice file is playing
  (captures / cutscene scripts).
- Keep the game running while the player uses Lockpick on another screen (the game pauses and blacks out when
  inactive): opt-in in-process focus patch (`0x44126B`, MEMORY_MAP "Focus and input") — isolated, reversible, read
  back, never in Challenge Mode. **Chosen by the owner (2026-10-13).**

## Developer tooling (never shipped)

Automated play is for the owner's development and testing only — never in the app, its installer or a release:
players play the game themselves; Lockpick enhances their play.

- **Automated testing harness** (`tools/Lockpick.Autoplay` + `tools/autoplay`, 2026-10-14) — **working:**
  - virtual Xbox 360 controller (ViGEmBus); menus driven by a local vision model's screen labels; new games for
    either character;
  - **enter any room** through a real door record (the game's own door handoff, fade and arrival spot);
  - **fast-forward cutscenes** (tick interval; every effect kept); control detected from the state flags;
  - **stand at a spot** (e.g. an item's pickup zone) and **heal**; read-only `peek`; captures of whole runs;
  - `scene.py <character> <room>`: load a practice save at the right story stage, then enter the room — any part of
    the game with either character.
- **First overnight run (2026-10-14):** every door-reachable room entered from a new game for both characters (162
  of 165 door targets each; the other 3 are stub room files that crash the game), phase surveys from practice saves
  (guardhouse 35, return mansion 58, caves 17, lab 22 rooms — all entered, both characters; 588 room visits in all),
  and 75 saves loaded with Lockpick's live room/character matching the offline save decode (73 of 75; the other two,
  a pair of practice saves at the same spot in 513 with camera 5, crash the game on load even at normal speed). Results, captures and an atlas stay local (`%LOCALAPPDATA%\Lockpick\research\night-20261014`).
- Next: regression scripts (expected flags/items per scene, run after changes to addresses or event rules), item
  pickups at their zones (camera re-cut after placement), capture bounties that need precise actions, why the first
  door handoff after a save load is ignored.
- Unattended runs — **done 2026-10-14:** `--background` applies the focus patch inside the harness (dev runs only)
  and blanks the keyboard map; the game ran, read the virtual pad and rendered behind other windows. Memory input
  (`0x497BF7`) isn't needed for testing. During runs `SAVE/*.dat` are read-only and hash-checked after every step.
- Vision labels — **working 2026-10-14:** describes screenshots and labels menus; not reliable for reading the
  status screen (invents item lists), so item names were checked against the game's own name table instead.

## 0.4.0 — World database

- RDT parser for the user's own installation (no assets redistributed): cameras, doors (`door_aot_set`),
  items (`item_aot_set`), enemies (`SceEmSet`). **Done as a research tool** (`tools/rdt`, every retail room file,
  committed as `room-index.json` with ids and numbers only); still to do: port to C# for in-app use, and cameras.
- **Event-script parser** (RDT `offsets[8]`): cutscene scripts set the story flags room scripts don't, hand over items
  (Barry's lockpick, Richard's radio) and play voice lines — parsing them names events without asking and tells the
  subtitles which line plays when.
- Room/camera names database in `data/games/re1/` (JSON), keyed by room id — areas done (`rooms.json`), names so far
  for rooms identified in play; the rest via ask mode / community.
- Rooms-visited and map/document flags: done (the game's table at `0x4D31E0`; maps = item + 46, documents = item + 35).
- Enemy array reader (30 × 0x18C at `0xBE6464`) runtime-verified — types, HP and active flag behave as expected in
  every capture (static anchor still needed); most enemy types identified in play.
- On-screen text: which message is shown (pointer at `0xD226A4`, text buffer recorded in captures) and the room files'
  MSG section decoded locally — for a text log and examine/pickup messages.

## 0.5.0 — Highlighting and ESP

- World-to-screen projection using RDT camera data; overlay markers for items, doors, enemies.

## 0.6.0 — Advanced trainer

- Teleport, movement speed, enemy freeze/health, event-flag editor, game speed.
- The event-flag editor shows the named flags (story, puzzles, locks, items, rooms) instead of raw bits, with
  story-state presets ("start at the courtyard").
- Give or remove any item; put pickups back into the world (set their pickup bit), reset puzzles (clear their named
  flags), respawn enemies (clear kill flags), reset the map's explored rooms; teleport (room + position).
- Challenge setups (knife-only, no herbs, start with nothing) and a light item randomizer, built on the same writes.

## 0.7.0 — Contextual guidance

- Missed-item warnings, puzzle-state tracking, route guidance, warnings under 100 ms.
- **Progress tracker** (read-only, Challenge-safe — can ship before 0.7, it needs no new memory research): per room
  and area, items still there vs taken (room index + item flags), documents and maps collected, rooms explored (the
  map's own flags), enemies killed for good, and story/puzzle progress from the named flags.
- Timed events: countdowns the game keeps silently (Richard's serum errand, 15 min; later the self-destruct).
- Missed-item warning when leaving a room or area with items left (from the same data).

## Event candidates found in live traces

- ~~`GameLoaded` / new game~~ — implemented (timer discontinuities), tested against both live fixtures.
- Worked out in the research tools (timeline / ask mode), to move into the app's `EventDetector`: item picked up,
  put back, used, received, combined, loaded; document/map taken; first visit to a room; door/desk unlocked; enemy
  killed (kill flags) and getting back up; attack by name (damage per hit); poison ticks; object pushed; named story
  and puzzle events.
- `PlayerDied`: `stateFlags & 0x01000000` with HP ≤ 0 — needs a gated raw-field path.
- `GameSaved`: from the save files (#37); ink ribbon use is visible in the inventory (Chris).

## 0.8.0 — Speedrun and challenges

- Run timer, LiveSplit integration, autosplits, no-damage/no-heal/knife-only challenges, PB tracking,
  trainer-modification detection (records, not anti-cheat claims).
- Autosplits from the named story flags and room transitions, matching the community's segment templates (Yawn 1,
  Mansion 1, Courtyard 1, … End — speedrun.com); in-game time from the game timer; the capture timeline as a run
  review (where time went, damage taken, items used).

## 0.9.0 — Optional AI, performance, compatibility

- Optional ONNX inference (< 200 ms, CPU fallback) only where deterministic data is unavailable.
- Additional builds (other GOG revisions, languages) as fingerprinted profiles.
- Community labelling: ask-mode answers and `auto` markers from submitted captures become suggested flag labels
  (reviewed before merging), like the bounty automation does for evidence.

## 1.0.0 — Stable RE1

- All features verified on supported builds; documentation complete; signed releases.

---

## Backlog / ideas

- Lockpick window: remember size/position per monitor; "compact" always-on-top mode for a side screen.
- Music track lead (`0xD226A0`: changes when enemies burst in) — a "danger" cue for the HUD if confirmed.
- Ask mode with a controller button instead of the keyboard (the owner plays with a DualSense).
- Debug: one-click "export support bundle" (log + recording + fingerprint, no game files).
- Investigate why the game window cannot be dragged (window style `0x14C80000` has a caption; the 1997 window procedure likely swallows the move).
