# Runs and splits

Lockpick times runs the way the RE1 leaderboard does and splits by itself, so a run needs nothing but the game and
Lockpick (LiveSplit can still run next to it). This page says how it works and what is and isn't verified yet.

## Timing

- **In-game time** is the game's own clock (`0xD22730`, 30 ticks a second; docs/MEMORY_MAP.md), the time
  speedrun.com uses for RE1 PC. A run started by a new game uses the clock as it is (it starts at 0); a run started
  from a save (Runs → "Also start when I load a save") counts from the save's time.
- **Real time** runs alongside, from the start of the attempt.
- When the clock stops (menus, doors, cutscenes) is still to be measured (capture bounty B206); Lockpick shows the
  clock as the game keeps it either way.

## An attempt

| Event | What Lockpick does |
|---|---|
| New game (the in-game clock restarts) | Starts an attempt (the previous one, if still running, is kept as reset) |
| A loaded save | Starts an attempt only when "Also start when I load a save" is on; otherwise the run goes on |
| A split's trigger | Records the split's in-game and real time and moves to the next split |
| The ending | Records the last split; the attempt is finished |
| A death (HP below 0) | The attempt is over (died) |
| The title menu | The attempt is over (back to the title) |
| The game closes | The attempt is over |
| Lockpick closes | The attempt is kept as it stands (Lockpick was closed) |
| Split / undo / skip / reset keys | As in LiveSplit (Settings → Hotkeys); any time, also during a run |

Only the current split's trigger is checked, so the same room can end two different splits (e.g. entering 300 ends
"Mansion 1" and, later, "Mansion 2").

Every attempt is kept on the PC in `%LOCALAPPDATA%\Lockpick\runs\<split set>.jsonl` (one JSON line each): split
times (in-game and real), result, damage taken, whether it started from a save and whether the trainer was on or
used. Attempts are numbered on from the ones kept for the same splits, across sessions.

**For a submission or a challenge post**, Runs → Attempts → *Copy run summary* gives the latest attempt in a few
lines (category, result and in-game time, every split, the game build and `ResidentEvil.exe`'s SHA-256, whether
Lockpick's trainer was off), and *Save run record…* writes the same attempt as a JSON file with Lockpick's
version. Whether a leaderboard accepts Lockpick's timing is up to its moderators; the RE1 rules ask for LiveSplit's
autosplitter today, which Lockpick can follow (below).

A **personal best** is the fastest finished attempt from a new game with every split timed and no trainer;
**best segments** come from every attempt without the trainer.

## Split triggers

| Trigger | Fires when |
|---|---|
| Enter room *R* from *F* | The player goes from room *F* into room *R* |
| Enter room *R* from another stage | The player enters *R* from a room of another stage |
| Get item *I* (in room *R*) | One more of item *I* is held than before, in room *R* when given (items there are several of) |
| The ending | RE1: the state flags drop to 0 at the heliport (the final cutscene) |
| Split key | Only the split key |

## Built-in RE1 split sets

`data/games/re1/splits.json`:

- **Community segments** for Jill and Chris, bad and good ending, No Major Glitches and Major Glitches (8 sets) —
  the split names of the community's LiveSplit files on speedrun.com (King Henry / Rebecca Piano / Chemical Skip ›
  Yawn 1 › Mansion 1 › Courtyard 1 › Boxes › Guardhouse › Courtyard 2 › Yawn 2 › Basement › Mansion 2 › Courtyard 3 ›
  Black Tiger › Underground › Loud Room › Tyrant (Lab Tyrant › Orgy Room) › End) with the room transitions of the
  community autosplitter's segmented splits.
- **Key-item routes**, Jill and Chris Any% NMG — the community's key-item splits, in their order; the batteries
  are tied to the attic (719) and the lab (501).

None is verified yet: their split points come from the community autosplitter, not from runs seen by Lockpick
(capture bounties B701, B702 for full runs, B703 for the ending). Until then the app says so, and the split key
fixes a missed split.

## LiveSplit files

- **Import** (Runs → Import LiveSplit file…) reads a `.lss` file — the community's from speedrun.com, or the
  player's own. Each split takes the trigger of the same name from the built-in set that fits the file best (same
  character, most names in common, then the same category: Jill's "Basement" differs between the endings); a name
  Lockpick doesn't know becomes a split-key split. The file's own personal best and best segments are used for
  comparison until there are attempts in Lockpick. Imported files are read again each time Lockpick starts.
- **Save as LiveSplit file…** writes the splits with the player's best times (in-game and real), best segments and
  every attempt, so the file opens in LiveSplit with their history — and can go with a leaderboard submission.

Reading is defensive (no DTDs or external entities, size and split limits): these are files from the internet.

## LiveSplit

Lockpick talks to LiveSplit through LiveSplit's own TCP server (in LiveSplit: Control → Start TCP Server; port 16834,
Runs → LiveSplit):

- **Follow** — for leaderboard runs, which must be timed by LiveSplit's own autosplitter: Lockpick doesn't split by
  itself; LiveSplit's splits, undos and resets move Lockpick's run (its records, overlay and OBS page), so nothing is
  split twice. Lockpick still starts an attempt on a new game and ends it on a death or the title menu.
- **Drive** — with no autosplitter: Lockpick's splits start, split, undo, skip and reset LiveSplit, and LiveSplit's
  game time is set from the game's clock (Lockpick pauses LiveSplit's own game-time count and sets it every
  quarter second), so the runner keeps their LiveSplit layout and comparisons.

Checked against a stand-in for LiveSplit's server (tests); a check with LiveSplit itself is capture bounty B208.

## speedrun.com

RE1's leaderboard on speedrun.com (game `m1zklz10`, PC category `mke5yl9k`; `data/games/re1/speedrun-com.json`) has
four subcategories — character, ending, glitches, restriction — the same cut as Lockpick's split sets. Its runs are
timed by in-game time but the board keeps that time in its `realtime` field, so Lockpick's IGT compares directly.

- **Leaderboard** (Runs → Leaderboard, off until turned on): the board for the chosen splits (picked from their
  name, changeable and remembered per split set), the world record, the top five, where your best here would place
  and your own run on the board. Read from speedrun.com's public API (no account), once an hour per board or on
  Refresh; the overlay's splits widget adds the world record. Data is CC BY-NC 4.0 and credited on the page.
- **Account** (Settings → speedrun.com): your name marks your runs on the boards (no key needed). Your **API key**
  is optional and only needed to submit runs from Lockpick. Because a key lets an app act as you, Lockpick:
  refuses anything that isn't shaped like a key before going online; checks the key with speedrun.com (`GET
  /profile`) before keeping it; keeps it only in `%LOCALAPPDATA%\Lockpick\secrets`, encrypted with Windows DPAPI
  for your Windows account (another user or another PC can't read it) — never in settings.json, logs, captures or
  Lockpick's own API; sends it only to `https://www.speedrun.com` and follows no redirects (so it can't be carried
  elsewhere); clears the box the moment you press Save and never shows it again. *Remove key* deletes it from the
  PC; making a new key on speedrun.com stops the old one working anywhere.
- **Submit** (Runs → Submit to speedrun.com, with a key saved): a finished attempt from a new game with the trainer
  off, on the board picked in the Leaderboard card, with its in-game time (editable: it must match the end screen in
  the video), the date, the video link (RE1 requires one) and the run summary as the comment. Nothing is sent until
  the player presses Submit, reads what will be sent and presses it again; the run goes in unverified for the
  moderators (`POST /runs`; the time in the board's `realtime` field, as RE1 keeps it). Attempts with the trainer on
  or started from a save are refused before anything is sent. The form shows the PC rules (title start, final IGT on
  the end screen, LiveSplit's IGT script on screen, no tabbing out).
- The autosplitter LiveSplit's registry lists for RE1 (LiveSplit.AutoSplitters → deserteagle417's `RE1aio.asl`,
  last changed 2024-07-01 for the GOG English build) is byte for byte the one Lockpick's built-in split points come
  from (checked 2026-10-16).

## On the overlay and elsewhere

- The **Run splits** widget (Settings → Overlay) shows the category, a few splits around the current one and the
  last, each with its time and its difference to the personal best (green ahead, red behind, gold for a best
  segment), the run's clock and the personal best.
- Starts, splits and ends are in the event log (and in captures).
- With the API on, `GET /api/v1/run` serves the run and `/overlay/splits` is an OBS browser source with the same
  splits for the stream (docs/API.md); `POST /api/v1/run/split` (and undo, skip, reset) let a Stream Deck or a bot
  press the run keys.
