# RE1 Memory Map — GOG `ResidentEvil.exe` (USA, 1.0 hotfix4)

Build: 1,004,112 bytes, SHA-256 `205BD6FED6E080A38823958E5C3306CACC86A18FF9A2758179E8D33CAEEE4A78`,
x86, PE timestamp `0x33FD5643` (1997-08-22), image base `0x400000`, SizeOfImage `0x49B2000`,
DllCharacteristics `0` (no ASLR). All addresses are absolute virtual addresses for **this build only**.

The machine-readable source of truth is `data/games/re1/profiles/re1-gog-usa-hotfix4.json`.

Verification: **runtime** = observed behaving as described in the live game (2026-10-12 sessions) and
confirmed statically · **static** = confirmed by disassembling this executable · **unverified** = lead
from a third-party source, diagnostics only.

## Globals used by Lockpick

| Address | Type | Name | Meaning | Verification |
|---|---|---|---|---|
| `0xBE9820` | u8 | stage | Stage, 0-based (0 = STAGE1) | runtime |
| `0xBE9821` | u8 | room | Room within stage (two hex digits of `ROOMsRRp.RDT`) | runtime |
| `0xBE9822` | u8 | camera | Current camera (cut) | runtime |
| `0xBE636C` | i16 | hp | Player HP (player entity +0x88); goes negative at death | runtime |
| `0xBE6459` | u8 | maxHp | Player max HP (entity +0x175); 0 until a game starts; Chris 140, Jill 96 | runtime |
| `0xBE62E5` | u8 & 3 | character | 0 Chris, 1 Jill, 3 Rebecca (entity +0x01; bit 0x08 = alt costume) | runtime (Chris, Jill; Rebecca static) |
| `0xD22730` | u32 | gameTimer | In-game time, frames at 30/s; frozen in menus and door transitions | runtime |
| `0xD22777` | u8 | mainMenu | 1 while the title/main menu is shown | runtime |
| `0xBE41C0` | u32 | stateFlags | Main state bitfield (diagnostics only, see below) | static |
| `0xBE6318` | i32 | positionX | Player X (entity +0x34) | runtime |
| `0xBE631C` | i32 | positionY | Player Y (entity +0x38) | static (0 on flat floors) |
| `0xBE6320` | i32 | positionZ | Player Z (entity +0x3C) | runtime |
| `0xBE6358` | i16 | yaw | Facing (entity +0x74), units unconfirmed | unverified |
| `0xBE9944` + 2·i | u16 | inv0..inv7 | Inventory slot: low byte item id, high byte quantity (weapons: loaded rounds). 6 slots (Chris/Rebecca), 8 (Jill); compacts when an item is used | runtime |
| `0xBE9849` | u8 | equippedSlot | Equipped slot, 1-based (0 = none) | runtime |
| `0xD22768` | u32 | inventoryPtr | Active inventory (`0xBE9944`; `0xBE9950` for Rebecca) | runtime (Jill) |
| `0xBE98E4` + 2·i | u16 | box0..box3 | Item box entries: low byte id, high byte quantity (48 entries total; first 4 read) | runtime |
| `0xBE41E2` | u8 | enemyCount | Active enemy count | unverified (changed 2→0→2 across rooms 106/105/104 live) |

### Code anchors (checked in the live process before reading)

| Address | Bytes | Instruction | Purpose |
|---|---|---|---|
| `0x477DA4` | `A0 20 98 BE 00` | `mov al,[0xBE9820]` | room loader reads stage |
| `0x477DBB` | `A0 21 98 BE 00` | `mov al,[0xBE9821]` | room loader reads room |
| `0x462A4C` | `A0 22 98 BE 00` | `mov al,[0xBE9822]` | background loader reads camera |
| `0x4810D1` | `FF 05 30 27 D2 00` | `inc dword [0xD22730]` | frame loop increments timer |
| `0x40615E` | `66 83 3D 6C 63 BE 00 00` | `cmp word [0xBE636C],0` | death check reads HP |
| `0x401285` | `66 0F B6 35 59 64 BE 00` | `movzx si,byte [0xBE6459]` | heal clamp reads max HP |
| `0x4817B1` | `88 15 E5 62 BE 00` | `mov [0xBE62E5],dl` | new game stores character |
| `0x480B53` | `81 0D C0 41 BE 00 00 00 00 02` | `or dword [0xBE41C0],0x2000000` | game start sets a state flag |
| `0x40582E` | `A1 18 63 BE 00` | `mov eax,[0xBE6318]` | player X read |

### Key static evidence

- `0x4C44F8` holds the template `./usa/stage /room    .rdt`. The room loader at `0x477D90` indexes the
  hex table `0123456789abcdef` (`0x4C44E0`) with `stage+1`, the high and low nibble of `room`, and writes a
  `0`/`1` suffix from `stateFlags & 0x00800000` (Jill's RDT variant). This confirms stage/room semantics
  and the 0-based stage.
- `.\usa\stageS\rcSRRC.pak` (`0x4C2078`) is filled at `0x462A06` from stage, room and **camera**
  (`0xBE9822`); the install has `RC1000..RC1005.pak` for room 00, cameras 0–5.
- The timer is zeroed on New Game (`0x48090A`), restored from the save copy `0xBE9844` on load
  (`0x430695`), and the results screen divides the saved copy by `0x1A5E0` (1 h at 30/s) and `0x708` (1 min).

### Item ids observed live (behavioral identification)

| Id | Behavior observed | Likely item |
|---|---|---|
| `0x01` | Starting item, slot 1, equipped as melee | Knife |
| `0x02` | Quantity drops by 1 per shot, refilled to 15 on reload | Beretta |
| `0x0B` | Consumed by Beretta reloads; item box starts with two stacks of 15 | Handgun bullets |
| `0x41` | Consumed while HP went 46 → 96 (full heal) | First aid spray |
| `0x03` | Quantity drops by 1 per shot (7 → 0), equipped as a weapon | Shotgun |
| `0x0C` | Loaded into the shotgun: a reload takes exactly the missing rounds (capacity 7) | **Shotgun shells** |
| `0x44` | Consumed while HP rose 54 → 86 (+32, Jill) | Green herb? (BioRand id 68) |
| `0x07` | Picked up with 6 rounds by Forest, −1 per shot, 201 damage per hit | **Bazooka, explosive rounds** (BioRand says acid: swapped) |
| `0x11` | Loaded into an empty `0x07` (×6), which became `0x08` ×6 — not shotgun shells | **Acid rounds** |
| `0x08` | `0x07` after loading `0x11` rounds; 95 damage per hit (the speedrun chart's acid grenade) | **Bazooka, acid rounds** |
| `0x47` | Made from two `0x44`; consumed while HP went 71 → 96 (max) | Mixed herb (green + green)? |
| `0x21` | Dropped by the statue pushed in 202, picked up in 105, placed in the lion head in 10D (scenario flag A0 `0x10000`) | **Blue gem** (owner-confirmed) |
| `0x29` | Received from the lion head in 10D for the blue gem | **Wind crest** (owner-confirmed) |
| `0x1F`, `0x23`, `0x26` | Picked up / stored in the item box (0x23 has quantity 0: a key item?) | unknown |
| `0x2D` | Received in the gallery (117) after a puzzle flag, used in 11A like the wind crest | A crest? |
| `0x05` | In the attract-mode demo inventory, quantity 5 | unknown |
| `0x0E` | In the attract-mode demo inventory, quantity 144 | unknown (ammo-like quantity) |
| `0x2F` | Chris picked up 3; each typewriter save used one (3 → 2, 5 → 4) | **Ink ribbon** (owner + capture) |

Names are not shown in the UI until an item table is verified (0.2/0.4).

Stage/room note: walking up the main-hall stairs goes to `stage=1, room=0x03` (room 203): stage 2 = mansion 2F.

## `stateFlags` (`0xBE41C0`) observed values

| Situation | Value(s) |
|---|---|
| Fresh boot / title | `0x00000000`, `0x40000000`, `0x80000000` |
| Free movement, room 106 | `0x8011000C`, `0x8013000C` |
| Free movement, room 105 | `0x80000000` |
| Menu open/close | `0x80400000 → 0x60400000 → 0x80008000 → 0x60008000 → 0x40008000 → 0x40000000` |
| Pushing an object | `0x40` set while pushing (statue in 202) |
| Examining / picking up | `0x800` while interacting, plus `0x8000` while the message shows; a pickup refused with a full inventory shows the same bits with no inventory change |
| Door transition | `0x8201000C → 0x6601000C → 0x46010000 → 0x46000000 → 0x42000000 → 0x82000000` |
| Death | `0x8100000C` (`0x01000000` set when HP reached −10) |
| Jill game, main hall | `0x4080000C` (`0x00800000` = Jill RDT variant) |

Bits with support: `0x00800000` Jill RDT variant (static + live), `0x01000000` player dead (static + live),
`0x04000000` door transition (live), `0x00008000` menu (live). `0x02000000` is **not** a gameplay flag.

## Capture regions (recorded verbatim by captures)

| Region | Address | Length | Contents |
|---|---|---|---|
| `gameState` | `0xBE41C0` | `0x587C` | State flags, player entity, 30 enemy slots, save card (stage/room/camera, inventory, item box, flag banks) |
| `system` | `0xD21380` | `0x1400` | Item-use flags, game timer, inventory pointer, main-menu flag and nearby menu state |

## Diagnostic fields (unverified — decoded from captures to verify them)

| Fields | Address | Meaning (decomp / Mod SDK layout) |
|---|---|---|
| `enemy0..7Flags/Type/Hp` | `0xBE6464 + 0x18C·i` (+0, +1, +0x88) | Enemy slot: active bit, type id, HP |
| `flagScenarioB0..7` | `0xBE9854` (32 B) | Scenario flag bank 1 |
| `flagLocks0..1` | `0xBE9874` (8 B) | Door lock flags |
| `flagEnemies0..7` | `0xBE987C` (32 B) | Enemy flags |
| `flagRoomItems0..7` | `0xBE989C` (32 B) | Per-room item pickup flags |
| `flagScenarioA0..3` | `0xBE98C0` (16 B) | Scenario flag bank 0 |
| `flagRoom0..4` | `0xBE98D0` (20 B) | Room flags |
| `box4..47` | `0xBE98EC`… | Remaining item-box entries (layout verified on entries 1–4) |

The flag banks tile `0xBE9854`–`0xBE98E4` exactly and end where the item box starts — consistent with the layout.

### Observed in live session 4 (2026-10-13, Jill, memory log) — behavior only, no code anchors yet

- **Enemy slots** decode consistently: zombies type 0 and 17, crows type 5; HP drops 12 per Beretta round and 201 per
  bazooka hit, and a dead enemy's HP is set to −1 (crows −50). For about one frame after a room loads the slots still
  hold the previous room's values (types 34, 37, 38 appeared only then) — readers must let the room settle.
  Type 15 is the mansion plant: six of them in 10C (HP 1 each), slapping for 2 HP — the damage chart's value.
- **`flagEnemies`**: each of 13 zombie kills set exactly one new bit; killing crows set none.
- **`flagRoomItems`**: each pickup cleared exactly one bit as the item entered the inventory (1 = still in the world).
- **`flagRoom` bank** (the map's flags, flag *n* = bit 31 − *n* % 32 of word *n* / 32):
  - flags 0–123: rooms visited, one per room: flag = base[stage] + room. **Static:** the table is in the exe at
    `0x4D31E0` (bases `00 20 3F 52 64` = 0, 32, 63, 82, 100) followed at `0x4D31E8` by the rooms per stage
    (`20 1F 13 12 18` = 32, 31, 19, 18, 24 — 124 in all), read by the map code (`mov cl,[eax+0x4D31E0]` at
    `0x487755`, `cmp [eax+0x4D31E8],bl` at `0x48773B`). Confirmed live for stages 1–4; stages 6/7 (the mansion
    revisited) use 1/2. The map's own room numbers count: 107's back room is map room 11D (flag 29), the
    piano room's secret room 11E (30), the room behind the 2F bookcase 21D (61);
  - 124–129 maps taken: flag = map item id + 46 (124 = 1F map, item 78; 125 = 2F map, 79; 126 = garden map, 80);
  - 130+ documents taken: flag = document item id + 35 (130 researcher's will, item 95; 132 keeper's diary, 97;
    145 the book about mixing herbs, 110).
- **`flagLocks0`**: one bit set per door unlocked (4 doors).
- **Statue push (202)**, seen three times (session 4 and both new games in session 5): `stateFlags` bit `0x40` is set
  while pushing; when the statue falls, `flagScenarioA0` bit `0x100000` and `stateFlags` `0x100000` (camera cut) are set.
  It drops the blue gem in the dining room.
- **Stairs push (107)** to reach the map, by both characters: push bit `0x40` per push; taking the map cleared
  `flagRoomItems4` bit `0x4000` and set `flagRoom3` bit `0x8` (maps don't enter the inventory). No word in the
  recorded regions moves with a pushed object, so object positions live elsewhere.
- **Damage per hit** matches SHiiDO's RE1 damage chart (speedrun.com, JP PC new game) in every case seen: zombie
  bite 10, ankle bite 6 (also from a zombie that got back up with 1 HP), crow peck 6 then 3 per hit, mansion plant
  slap 2. The timeline names attacks from it.
- **Zombie getting up:** HP ≤ 0, kill flag set, then active again with HP 1 (204, both sessions).
- **Room 206** (Jill): the timer paused 4.5 s, `flagRoom4` bit `0x4000` set and `flagRoomItems7` bit `0x100` cleared, with
  no inventory change — probably reading a document (not identified).
- **Enemy types confirmed by behavior:** 0 and 17 zombies, 2 dogs (Cerberus), 5 crows. Dog kills set `flagEnemies` bits
  like zombie kills; a new game clears every flag bank.

## Not yet accounted for (sessions 4–5, 2026-10-13)

What changes during play but isn't explained yet — candidates for markers in future captures.

- **Scenario flags** (`flagScenarioA/B`): two ask-mode sessions (Jill, new game → the guardhouse save room) named
  about 70 story and puzzle events — Barry's scenes, the ceiling trap, the gallery, crests, the piano and emblems,
  Richard and the serum, Yawn, the aquarium, the water gate, the guardhouse statue — in
  `data/games/re1/flag-labels.json` with their source. Later areas are still unknown; ask-mode captures name them.
- **Room scripts set story flags:** `set` group 0 writes bank A (`flagScenarioA`), group 1 bank B (`flagScenarioB`)
  — about 35 confirmed labels match a `set` in the room where the player was. `tools/rdt` records them
  (`room-index.json` → `story`). Cutscene/event scripts (`offsets[8]`) set the rest and are not parsed yet.
- **`flagLocks`** also covers locked desks (flags 30, 31: desks opened with Jill's lockpick in 102 and 111).
- **Kill flag 255** (`flagEnemies7` bit `0x1`) is set when an enemy whose room script says "not persisted" dies.
- **Jill's lockpick** is given by Barry in the main hall (`flagScenarioA3` bit `0x8`) and never appears in the
  inventory or item box; it opens the Sword-key doors (107→108, 103→10E).
- **Unidentified memory** (changes during play): `0xBE41C6–0xBE41DC` (state block next to `stateFlags`; `0xBE41DC`
  toggles 0/2 at every room load), `0xBE5684–0xBE62DE` (arrays with a 0x84-byte stride — objects/effects, mostly
  updated at room loads; the pushed stairs/statue positions were not found in the recorded regions),
  `0xBE92CC–0xBE9318`, `0xBE9616` (counts 0→1→2→3→0 through each door transition), `0xBE9682–0xBE981F` (written
  once at new game), `0xBE9823–0xBE9852` (save-card fields after stage/room/camera, very active), `0xBE995C–0xBE9A3B`
  (224-byte table, single bytes change at a few moments — door/map state?), and the system block `0xD213A1–0xD21503`
  (arrays with a 0x24 stride).

## Save files (`SAVE\savedatN.dat`)

2690 bytes (`0xA82`), starting `53 43 13 01`. **Static (2026-10-13):** written by `0x493926`–`0x493A06`
(`fwrite 0xA82`), read by `0x493D04`; **no checksum** or transform (the loader only checks section sizes). Layout:
`0x000`–`0x7FF` = memory `0xBE9620`–`0xBE9E1F` verbatim; `0x800` key map (32 VK codes); `0x820` keyboard→pad table;
`0x8A0` SideWinder table; `0x920` copy of `0xBE995C` (0xE0 bytes); `0xA00`/`0xA01` two bytes; `0xA02` joystick
table. Inside the block: save count `0x228` (`0xBE9848`), character also at `0x22B` (`0xBE984B`). After a load the
game ORs `0xBE41C0 |= 0x10000000` (and `0x800000` for a non-Chris character). Save offset + `0xBE9620` = memory address (found 2026-10-13 by matching a save written during a live capture against the memory
recorded 48 ms earlier) — so stage/room/camera, every flag bank, the item box and the inventory decode with the
profile's addresses. The character is the low bit of `0xBE9829` (save offset `0x209`; field `saveCharacter`):
0 Chris, 1 Jill in all 75 saves checked; bit `0x4` is set only in the underground caves and the lab (the autosplitter's
"Jill's character ID changes in a few spots"). Byte `0x212` agrees (`0x2F`, the ink ribbon a save costs, for Chris). `tools/bounties/saves.py` reads saves offline (character, room, inventory, box, progress, story); it
decoded 67 community practice saves and the owner's 8 into the rooms their names describe.

## Focus and input (static analysis 2026-10-13; live-tested 2026-10-14 with the dev harness)

| Address | What | Evidence |
|---|---|---|
| `0x441170` | Window procedure (class registered at `0x441CBB`) | `C7 44 24 70 70 11 44 00` at `0x441C71` |
| `0x4BCB2C` (u32) | The only "focused" flag (decomp `g_bWindowFocused`); image value 1 | written only by `WM_ACTIVATE` at `0x44126B` (0) / `0x4412A0` (1); `WM_ACTIVATE` also pauses sounds (`0x480640`) |
| `0x441E9D`–`0x441EA3` | Main loop skips `main_loop` (`0x428EB0`: input, game, render) while the flag is 0 — the focus-loss pause | `39 1D 2C CB 4B 00 0F 84 C8 FE FF FF` |
| `0x497BA0` | `ReadPadBoth`: keyboard (+ joystick #0) → PSX button word; returns 0 if `[0x4D460C]` | ends `A1 18 40 AC 00 C3 CC×8` at `0x497BF2` |
| `0x4202F0` | Keyboard poll: `GetAsyncKeyState` over the 32-key map at `0xAC4030` (copied from `0x4D4730`); GOG edits left only map indices 0–3, 5, 10, 11, 13, 27–31 active | `GetAsyncKeyState` works unfocused |
| `0xBF0A04` / `0xBF0A08` (u32) | Raw held / newly-pressed PSX word per tick (`0x44E000`) | rewritten every tick; **live 2026-10-14:** `0xBF0A04` showed the bit of every virtual-controller input pressed, on the title screen and in gameplay |
| `0xBE9840` / `0xBE9842` (u16) | `key_on` / `key_trg` (remapped per control type `[0xBE9A5C]&3`) | rewritten every tick |

PSX button word: `0x1000` up, `0x4000` down, `0x8000` left, `0x2000` right, `0x80` action/confirm, `0x40`
cancel/run, `0x08` aim, `0x800` status, `0x900` options. Default keys (`0x4D4730`): arrows, V, X (aim), C, Z (status),
A (options), Ctrl, Enter, Space, Esc. The owner's machine has a remapped table (registry `Key Def` and every save's
`0x800`): A = up, Up arrow = down, Down arrow = options — why simulated arrow keys did nothing useful.

Controller (live 2026-10-14, virtual Xbox 360 pad, default joystick table): A action `0x80`, X cancel/run `0x40`,
Y status `0x800`, Back options `0x900`, RT aim `0x08`, d-pad and left stick movement; B, Start, LB, RB, LT and the
stick clicks unmapped. **The game finds controllers only at startup** — a pad plugged in later is ignored. The keyboard
map at `0xAC4030` is rewritten ~2 s after launch with the player's table (and presumably on save load).

Planned patches (Trainer Mode, opt-in, reversible, read back, never in Challenge Mode):
- **Keep running unfocused:** `0x44126B` `C7 05 2C CB 4B 00 00 00 00 00` → `E9 C6 00 00 00 90 90 90 90 90`
  (`WM_ACTIVATE` goes straight to `DefWindowProcA`; the flag stays 1, sounds aren't paused). Data-only alternative:
  write 1 to `0x4BCB2C` after each deactivation. **Live 2026-10-14 (dev harness `--background`):** with the patch,
  the flag set to 1 and the key map blanked, the game ran with another window in front, read every controller input
  and rendered normally (PrintWindow screenshots of gameplay); restoring the bytes on exit worked. Risks: keys typed in other windows reach the game
  (`GetAsyncKeyState`) — zero the key map at `0xAC4030` while unfocused or set `0x4D460C`; DirectSound mutes unfocused
  buffers (no `GLOBALFOCUS`, `0x41EBBD`); GOG's ddraw wrapper may still black out the window (unanalysed).
- **Input from Lockpick:** `0x497BF7` `C3 CC CC CC CC CC` → `0D 00 00 00 00 C3` (`or eax, imm32; ret`): the u32 at
  `0x497BF8` becomes a button mask OR-ed into every read — triggers, menus and movement follow naturally. Hold ≥ 2
  ticks, release ≥ 2 ticks.

## Leads (unverified)

| Address | Observation (live, 2026-10-13) | Lead |
|---|---|---|
| `0xD226A0` (u8) | Changes on room entry and when enemies appear (e.g. `0x08 → 0x20` as the dogs burst into 108, back after the kill) | Current music track |
| `0xD226A4` (u32) | Set to `0xD91AF4` from the first message shown onwards | Pointer to the on-screen text buffer — recorded as capture region `textBuffer` (`0xD91A00`, 1 KB) to find the message shown |

Room files keep their text in the MSG section (`offsets[11]`: u16 offset table, then text in the game's own encoding
with control codes), so a message number plus the player's install is enough to show the text without recording it.

## Structures (leads for later milestones)

From the decomp (GPL-3.0; facts only, no code used) and the RE1 Mod SDK layout, consistent with each other:

| Address | Description | Verification |
|---|---|---|
| `0xBE62E4` | Player entity (0x180 bytes) | partially (fields above) |
| `0xBE6464` | Enemy array, 30 × 0x18C; slot active when `+0x00 & 1` | unverified |
| `0xBE9950` | Rebecca's inventory | unverified |
| `0xBE98E4` | Item box, 48 × `{id, qty}` | unverified |
| `0xBE9844` | Saved copy of the game timer | static |
| `0xBE9620` | Save block ("bio card"), 0x41C bytes | unverified |
| `0xBE98C0`… | Scenario/flag banks (see RESEARCH.md) | unverified |

Entity layout (shared player/enemy up to +0x8D): `+0x00` flags, `+0x01` id, `+0x34/38/3C` int32 X/Y/Z,
`+0x74` i16 yaw, `+0x88` i16 HP, `+0xDC` status flags (poison), `+0x175` max HP (player).
