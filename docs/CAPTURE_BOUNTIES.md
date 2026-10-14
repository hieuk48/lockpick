# Capture bounties

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


---

## 0.1.x — Foundation

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B001 | New Game as **Chris**, walk around the main hall | Character id 0, HP/max HP 140, stage/room/camera, position, timer | ✅ 2026-10-12 |
| B002 | New Game as **Jill** | Character id 1, max HP 96, Jill room-file bit | ✅ 2026-10-12 |
| B003 | Walk through several doors and camera angles | Room/camera changes, door-transition timing | ✅ 2026-10-12 |
| B004 | Take damage until you die | HP decreases, death flag, HP below zero | ✅ 2026-10-12 (Chris and Jill) |
| B005 | After dying, return to the title and start a New Game | Title flag, session gating, new-game detection | ✅ 2026-10-12 |
| B006 | Load a save from the title screen | Save-load detection (timer restores) | ✅ 2026-10-12 |
| B007 | Play the **Rebecca** section (Chris scenario) | Character id 3, Rebecca's inventory pointer `0xBE9950` | ⬜ |
| B008 | Go upstairs from the main hall | Stage 2 = mansion 2F (room 203) | ✅ 2026-10-12 |

---

## 0.2.0 — Core trainer (inventory, items, ammo)

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B101 | Pick up items, fire the Beretta, reload | Inventory slots (id + quantity), rounds per shot, reload from bullets | ✅ 2026-10-12 (Jill) |
| B102 | Put items **into** and take them **out of** the item box | Item box layout `0xBE98E4` | ✅ 2026-10-13 (live session): put items in and took them out — entries are 2 bytes (id + quantity), swaps exchange box and inventory slots, the inventory compacts when an item leaves |
| B103 | Pick up, fire and reload the **shotgun** | Shotgun id, shell ammo id, capacity | ✅ 2026-10-13: shotgun (id 0x03) fired 7 → 0, one shell per shot; reload not yet observed (id 0x11 is not shotgun shells: it loads into the bazooka, see B105); 2026-10-13 live session 7 (Jill; owner: reloaded a bunch): shells are id 0x0C (BioRand: Shells); the shotgun (0x03) holds 7 and a reload takes exactly the missing rounds — 0→7 (shells 7→0), 5→7 (14→12, 12→10, 17→15) |
| B104 | Same for the **Colt Python / magnum** | Magnum + magnum rounds ids | ⬜ |
| B105 | Same for the **bazooka** with each round type (explosive, acid, flame) | Bazooka ids and round types | 🟡 2026-10-13 re-check of sessions 4-7: 0x07 is the bazooka with explosive rounds (as found by Forest; 201 damage per hit, 15 hits), 0x08 the bazooka with acid rounds (an empty 0x07 loaded with acid rounds 0x11; 95 damage, the speedrun chart's acid grenade). BioRand's names for 7/8 are swapped; 0x10 (explosive rounds) picked up but never loaded. Flame rounds not seen |
| B106 | Use the **flamethrower** (Chris) | Flamethrower id and fuel | ⬜ |
| B107 | Use the **rocket launcher** (ending) | Rocket launcher id | ⬜ |
| B108 | Use each healing item: green, red+green, blue herb, mixed herbs, first aid spray | Item ids, heal amounts, herb combining | 🟡 first aid spray (full heal) seen; 2026-10-13: id 0x44 consumed while HP went 54 → 86 (+32, Jill) — likely green herb (BioRand id 68); 2026-10-13 (live session 4, Jill, memory log): id 0x47 (made from two 0x44) consumed while HP went 71 → 96 (capped at max) — likely mixed herb (green + green); evidence in 2026-10-13 live session 7 (Jill, ask mode, mansion to guardhouse): HP 56 → 96 — needs review; 2026-10-13 re-check of sessions 4-7 (session 7): green herb +32; G+G used (+40, capped); red+green and red+green+blue mixed but not used. Still needed: red+green, blue herb, green+blue and three-herb mixes used; evidence in 2026-10-13 live session 8 (Jill, ask mode, guardhouse to mansion revisited): HP 64 → 96 — needs review |
| B109 | Get **poisoned** (snake/Yawn/spider) and cure it with a blue herb or serum | Status flags (player +0xDC) | 🟡 2026-10-13 re-check of sessions 4-7 (session 7): poisoned by Yawn in 210 — after a 10 HP bite, HP fell 2 at a time every few seconds (the chart's poison: -2 HP every 4 s) until Jill fainted and was rescued (story flags named). Cure with a blue herb or serum not yet seen |
| B110 | Combine items (herbs; anything else combinable) | Combine results and slot changes | ✅ 2026-10-13 (live session 4, Jill, memory log): 0x44 + 0x44 → 0x47 in one slot (herbs); empty 0x07 + 0x11 ×6 → 0x08 ×6 (loading a weapon with a different round type); 2026-10-13 (live session 5, Jill): empty Beretta (0x02 ×0) + clip (0x0B ×15) → Beretta ×15 in the Beretta's slot; the clip's slot empties; 2026-10-13 re-check of sessions 4-7 (session 7): green + green, red + green, then red+green + blue into the three-herb mix; acid rounds loaded into the bazooka again; 2026-10-13 live session 8 (Jill, ask mode): the whole V-Jolt recipe as combines — Water + UMB No.2 → NP-003, UMB No.4 + NP-003 → UMB No.7, UMB No.2 + UMB No.4 → Yellow-6, UMB No.7 + Yellow-6 → UMB No.13, UMB No.13 + NP-003 → V-Jolt (empty bottles returned) — confirming item ids 19–27; G+G+G mixed too |
| B111 | Pick up each **key item** (keys, crests, MO disk, emblems, etc.) | Key item ids → item name table | 🟡 2026-10-13 (live session 4, Jill, memory log): blue gem = id 0x21, wind crest = id 0x29 (both owner-confirmed): gem from the dining room 105, placed in the lion head in 10D, crest received; 2026-10-13 (live session 5: new Jill game, then new Chris game): id 0x2D received in the gallery 117 and used in 11A like the wind crest — likely another crest; 2026-10-13 re-check of sessions 4-7 (session 7, player's answers): gold emblem 0x20, music notes 0x23 (piano), moon crest 0x2C, sun crest 0x2E, shield key 0x35, armor key 0x34, lighter 0x30, serum 0x42 — each used where the player said. Square crank 0x1D picked up; MO disk, V-Jolt and lab keys not yet seen; 2026-10-13 live session 8 (Jill, ask mode): dorm keys 002 (0x39) and 003 (0x3A), control room key (0x3B) and red book (0x3E) used where they open things; V-Jolt chemistry items 0x13–0x1B |
| B112 | Save at a typewriter (a few times, different slots) | Save detection; ink ribbon use (Chris's game uses them, Jill's didn't) | ✅ 2026-10-13 (Jill): 3 saves with no inventory change — Jill's game used no ink ribbon; 2026-10-13 (live session 5: new Jill game, then new Chris game): ink ribbon = id 0x2F — Chris picked up 3, each of his two saves used one (3 → 2, 5 → 4) as savedat7/8.dat were written. Memory shows nothing else during a save, so saves are detected from the save files (issue #37) |
| B113 | Try to pick something up with a **full** inventory (Chris 6 / Jill 8 slots) | Slot limits per character | 🟡 2026-10-13 (live session 5, Jill): refused pickup with all 8 of Jill's slots full (blue gem, dining room 105): stateFlags 0x800 (interacting) + 0x8000 (message) with no inventory or room-item flag change. After combining the empty Beretta with a clip freed slot 8, the same action picked the gem up into it. Chris's 6-slot limit not yet observed; evidence in 2026-10-13 live session 7 (Jill, ask mode, mansion to guardhouse): all 8 inventory slots filled — needs review; evidence in 2026-10-13 live session 8 (Jill, ask mode, guardhouse to mansion revisited): all 8 inventory slots filled — needs review |
| B114 | Switch equipped weapon between several weapons | Equipped-slot behavior per weapon | ✅ 2026-10-13 (live session): equipped slot follows the weapon — knife 1, Beretta 2, shotgun 5 then 4 after the inventory shifted |

---

## 0.3.0 — Overlay and HUD

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B201 | Play windowed at a few window sizes and positions; alt-tab away and back | Game window tracking, focus handling | 🛠️ needs window geometry in captures |
| B202 | Play in fullscreen | Overlay behavior in fullscreen | 🛠️ |

---

## 0.4.0 — World database (rooms, cameras, items, enemies)

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B301 | Visit every reachable room in **stage 1** (mansion 1F) | Room ids, cameras per room, door connections | 🟡 rooms seen: 100 101 102 103 104 105 106 107 108 109 10A 10B 10C 10D 10E 10F 111 112 113 115 116 117 118 11A 11B |
| B302 | Visit every reachable room in **stage 2** (mansion 2F) | Same | 🟡 rooms seen: 201 202 203 204 205 206 207 208 209 20A 20B 20D 20E 20F 210 211 212 |
| B303 | Visit every reachable room in **stage 3** | Same; stage identity | 🟡 rooms seen: 300 301 302 304 |
| B304 | Visit every reachable room in **stage 4** | Same; stage identity | ✅ rooms seen: 400 401 402 403 404 405 406 407 408 409 40A 40B 40C 40D 40E 40F 410 411; 2026-10-13 live session 8 (Jill, ask mode): all 18 guardhouse rooms (400–411) — the game's own room table lists 18 for stage 4 |
| B305 | Visit every reachable room in **stage 5** | Same; stage identity | ⬜ |
| B306 | Visit every reachable room in **stages 6 and 7** | Same; which rooms are revisits | 🟡 rooms seen: 600 601 603 604 609 60A 60B 60C 610 614 618 619 61A 61B 700 707 70B 70C 713 71A 71B 71C; 2026-10-13 live session 8 (Jill, ask mode): mansion revisited — 14 rooms of stage 6 and 8 of stage 7 so far |
| B307 | Both scenarios start to finish (Chris and Jill) | Scenario room differences (ROOMxxx0 vs xxx1) | ⬜ |
| B310 | Encounter **zombies** (and let one get up again) | Enemy array `0xBE6464`, enemy type id, HP | ✅ 2026-10-13 (live session 4, Jill, memory log): enemy slots decode as flags/type/HP — zombies type 0 (rooms 101 103 10E 201 202) and type 17 (204 207); HP drops 12 per Beretta round, 201 per bazooka hit, slot HP set to -1 once a zombie is dead. A zombie getting up again not yet seen; 2026-10-13 (live session 5: new Jill game, then new Chris game): a zombie getting back up (owner + capture) — type 17 in 204 fell to HP -2, its kill flag was set, 7 s later it was active again with HP 1, bit Jill four times (6 HP each) and went down for good after one more round. The same thing shows in session 4 (204, -3 → 1); 2026-10-13 (live session 5): ankle bites (6 HP per hit, the damage chart's footbite) — Jill from the zombie that got back up with 1 HP (204), Chris from a zombie in 107; normal bites are 10 |
| B311 | Encounter **dogs (Cerberus)** and **crows** | Enemy type ids | ✅ 2026-10-13 (live session 5: new Jill game, then new Chris game): dogs are type 2 — two in 108 and one in 11A, all shot down (Jill); 2026-10-13 (live session 4, Jill, memory log): crows are type 5 — six idle in 117; in 212 they attacked (3 HP per peck) and three were shot down |
| B312 | Encounter **Hunters** and **Chimeras** | Enemy type ids | 🟡 2026-10-13 live session 8 (Jill, ask mode): Hunters are type 6 (introduction cutscene in 60A named by the player; 8 killed in the mansion revisited). Chimeras not yet seen |
| B313 | Encounter **spiders**, **wasps** and **sharks** | Enemy type ids | ✅ 2026-10-13 live session 8 (player confirmed): giant spiders are type 3 (two killed in 404; each bursts into small spiders that bite for 2), wasps type 7 (beehive passage 408), sharks type 11 in 40E (one big — Neptune — and two smaller) |
| B314 | Boss fights: **Yawn** (both), **Plant 42**, **Neptune**, **Black Tiger**, **Tyrant** (both) | Boss ids and HP | 🟡 2026-10-13 live session 7 (Jill, ask mode, owner's answers): Yawn is enemy type 13 (appeared in 210, player confirmed); its poison made Jill faint (story flags named). Plant 42, Neptune, Black Tiger, Tyrant not yet seen; 2026-10-13 live session 8 (Jill, ask mode): Yawn's second fight is type 18, killed in 70C (player); Plant 42 is type 8 in 40C after V-Jolt on its roots (type 14) in 40F. Neptune, Black Tiger and Tyrant not yet seen; 2026-10-13 live session 8 (player confirmed): Neptune (the big shark, type 11) encountered in 40E, not killed |

---

## 0.5.0 — Object highlighting and ESP

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B401 | Stand still in a few rooms for a few seconds per camera angle | Camera projection (world → screen) | 🛠️ needs synced screenshots in captures |
| B402 | Walk past items and doors in view of the camera | On-screen positions of objects | 🛠️ |

---

## 0.6.0 — Advanced trainer (flags, enemies, events)

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B501 | Unlock doors with keys and the lockpick | Lock flags bank `0xBE9874` | ✅ 2026-10-13 (live session 4, Jill, memory log): flagLocks0 gained one bit at each of four door unlocks (rooms 207, 206, 103 twice) with no item used — which bit is which door still needs mapping; evidence in 2026-10-13 live session 7 (Jill, ask mode, mansion to guardhouse): 1 flag word(s) changed: flagLocks0 — needs review; 2026-10-13 re-check of sessions 4-7: every lock flag change matched a door in the room index (keys: armor key in 102, the lockpick on Sword-key doors, doors unlocked from the inside) or a locked desk (102, 111) |
| B502 | Solve puzzles (piano, statues, armor room, crests, V-Jolt, computer/MO disk) | Scenario flag banks | 🟡 2026-10-13 (live session 4, Jill, memory log) and 2026-10-13 (live session 5: new Jill game, then new Chris game): blue gem chain — statue pushed in 202 (A0 bit 0x100000), gem (id 0x21) picked up in the dining room 105, placed in the lion head in 10D (A0 bit 0x10000), wind crest (id 0x29) received (A0 bit 0x40000), wind crest used in 11A (A3 bit 0x80000); 2026-10-13 (live session 5: new Jill game, then new Chris game): gallery in 117 — A0 bit 0x10000000 set, then id 0x2D picked up there and used in 11A (A3 bit 0x200000); likely the painting puzzle and its crest (unconfirmed); 2026-10-13 live session 7 (Jill, ask mode, owner's answers): each puzzle's flag named by the player — gallery (star crest), crests in the 11A door (star, sun, wind, moon), chemical in the pump (plant), piano → gold emblem and the emblem swap, clock → shield key, floor switch (sun crest), lighter on the fireplace (2F map), candles (bookcase), bug-case switch (aquarium), water gate, statue over the guardhouse hole. Still needed: armor room, V-Jolt, computer/MO disk; 2026-10-13 live session 8 (Jill, ask mode): V-Jolt mixed and used on the roots, bathtub drained (control-room key), the control room's water lever, the red book, the tombstone switch, pushing in 40D — all named or put down to the player's actions. Still needed: armor room, computer / MO disk |
| B503 | Kill each enemy type | Enemy death, kill flags | 🟡 2026-10-13 (live session 4, Jill, memory log): 13 zombie kills, each set exactly one new bit in flagEnemies (kill flags persist); crow kills set none; 2026-10-13 (live session 5: new Jill game, then new Chris game): 20 zombies and 3 dogs killed (Jill and Chris), each setting one flagEnemies bit — set when the enemy first falls, even if it gets back up; a new game clears them all. Other enemy types not yet killed; evidence in 2026-10-13 live session 7 (Jill, ask mode, mansion to guardhouse): type 0 (Zombie) ×13, type 2 (Cerberus (dog)) ×9, type 5 (Crow) ×3, type 15 ×6, type 17 (Zombie (researcher)) ×9 — needs review; 2026-10-13 re-check of sessions 4-7 (session 7): kills of zombies, researcher zombies, dogs, crows and mansion plants (6, by the chemical); Yawn and the later enemies not yet killed; 2026-10-13 live session 8 (Jill, ask mode): kills of Hunters, spiders, Plant 42's roots and Plant 42, and the second Yawn |
| B504 | Push/move objects (statues, shelves, boxes) | Object state | 🟡 2026-10-13 (live session 5: new Jill game, then new Chris game): statue push in room 202, seen three times (session 4, and both new games in session 5): stateFlags bit 0x40 is set while pushing (slow straight-line movement), then scenario flag A0 bit 0x100000 and stateFlags 0x100000 (camera cut) as the statue falls; it drops the blue gem in the dining room (105). Shelves/boxes not yet seen; 2026-10-13 (live session 5: new Jill game, then new Chris game): stairs in room 107 pushed to reach the map, by Jill and by Chris (owner + capture): push bit 0x40 during each push; picking up the map then cleared flagRoomItems4 bit 0x4000 and set flagRoom3 bit 0x8, with no inventory change. The pushed object's own position is not in the recorded memory (no word moves with the push), so object positions need another region; 2026-10-13 (live session 4, Jill, memory log): flagRoom0/1 are rooms-visited bits (bit 31 − room number, one word per stage), not object state; evidence in 2026-10-13 live session 7 (Jill, ask mode, mansion to guardhouse): 13 flag word(s) changed: flagRoom0, flagRoom1, flagRoom2, flagRoom3, flagRoom4, flagRoomItems0, flagRoomItems1, flagRoomItems2 … — needs review; 2026-10-13 re-check of sessions 4-7 (session 7): statue pushed over the hole in the guardhouse (400) — scenario flag named. Pushed objects' positions are not in the recorded memory; shelves/boxes not yet seen; evidence in 2026-10-13 live session 8 (Jill, ask mode, guardhouse to mansion revisited): 13 flag word(s) changed: flagRoom0, flagRoom1, flagRoom2, flagRoom3, flagRoom4, flagRoomItems0, flagRoomItems1, flagRoomItems2 … — needs review |

---

## 0.7.0 — Guidance and warnings

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B601 | Pick up items, leave the room and come back | Per-room item pickup flags `0xBE989C` | ✅ 2026-10-13 (live session 4, Jill, memory log): each of 9 pickups cleared exactly one bit in flagRoomItems at the moment the item entered the inventory, and the bits stayed cleared across rooms (1 = item still in the world) |
| B602 | Trigger partner/cutscene events (Barry, Rebecca, Richard, Wesker) | Event flags and cutscene detection | 🟡 2026-10-13 live session 7 (Jill, ask mode, owner's answers): partner/cutscene events named by the player — Barry (blood, Kenneth, the zombie, the ceiling-trap rescue, the lockpick), Wesker's disappearance, Richard (found poisoned, the serum errand, his radio), Forest, Yawn's poison and the rescue. Rebecca (Chris's scenario) still needed; 2026-10-13 live session 8 (player confirmed): Wesker appears in 405 shooting at the wasps; Barry arrives in 70C with the rope |
| B603 | Skip vs. watch cutscenes | Cutscene state | ⬜ |

---

## 0.8.0 — Speedrun and challenge suite

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B701 | Full **any%** run, Chris | Split points, final timer | ⬜ |
| B702 | Full **any%** run, Jill | Split points, final timer | ⬜ |
| B703 | Reach each **ending** and the results screen | End-of-game detection, final IGT, rank | ⬜ |
| B704 | Knife-only section | Weapon-use tracking | ⬜ |
| B705 | Resets mid-run (quit to title; restart the game) | Reset detection | ✅ quit to title and game restart seen; evidence in 2026-10-13 live session 7 (Jill, ask mode, mansion to guardhouse): 1 restart(s) / reload(s) — needs review; 2026-10-13 re-check of sessions 4-7: quit to title and a new game (session 5: Jill run, then Chris), loads and game restarts are all detected as separate segments; timers jumping back reset the event detector |

---

## 0.9.0 — Compatibility (other builds)

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B801 | Capture with this build running: GOG in **French** or **German** | Build fingerprint (and a profile, with a short play session) | ⬜ |
| B802 | Capture with this build running: Earlier GOG English revision (before hotfix4) | Build fingerprint (and a profile, with a short play session) | ⬜ |
| B803 | Capture with this build running: GOG **Japanese** (`Biohazard.exe`) | Build fingerprint (and a profile, with a short play session) | ⬜ |
| B804 | Capture with this build running: Sourcenext/Mediakite Japanese + **Classic REbirth** | Build fingerprint (and a profile, with a short play session) | ⬜ |
| B805 | Capture with this build running: Original 1996/1997 **CD release** (US or EU) | Build fingerprint (and a profile, with a short play session) | ⬜ |
| B806 | Capture with this build running: **Director's Cut** PC (if one exists for your region) | Build fingerprint (and a profile, with a short play session) | ⬜ |
| B807 | Capture with this build running: Windows 10 (not 11), different GPUs, multi-monitor/high-DPI setups | Build fingerprint (and a profile, with a short play session) | ⬜ |

---

## 1.0.0 — Stable release

| ID | Do this in-game | Verifies | Status |
|---|---|---|---|
| B901 | Full playthrough per supported build with every Lockpick feature on | Final regression pass | ⬜ |
