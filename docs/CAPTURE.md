# Help Lockpick learn the game (capture a play session)

Lockpick only trusts what it has seen the real game do. Recorded play sessions are how new values (items, ammo,
item box, Rebecca, more rooms and game builds) get verified. Capture is built into Lockpick.

## Steps

1. Install Lockpick: **[Lockpick-win-Setup.exe](https://github.com/mscrnt/lockpick/releases/latest/download/Lockpick-win-Setup.exe)**
   (Windows SmartScreen may warn because the build isn't code-signed yet: *More info → Run anyway*).
2. Start Resident Evil normally (GOG Galaxy or the desktop shortcut — not "as administrator").
3. In Lockpick open **Debug** and click **Start capture**. The card turns red while recording.
4. Play. Resets, deaths, the title screen and game restarts are fine — recording continues.
5. Click **Stop and save**. A `LockpickCapture-*.zip` is created; **Open captures folder** shows it.
6. Send the zip to the maintainers (or attach it to a GitHub issue).

If you close Lockpick while capturing, the capture is saved automatically.

## Ask mode (Lockpick asks what just happened)

Tick **Ask me about unknown events** on the Debug page (it is off every time Lockpick starts). While capturing,
Lockpick checks each change against what it knows about the game — every item, enemy and door in every room, and the
events already identified. When something unknown happens (usually a cutscene or a scripted event), it waits until
you're back in control, **pauses the game**, and asks what happened, showing the room, time and what changed. Type a
short answer ("Barry's cutscene in the dining room") and press Enter, or Skip; the game resumes either way.

Each thing is asked about once per capture. Answers are saved in the capture next to the changes they explain, so the
event can be named for everyone. Pausing suspends the game process (nothing in it is changed); if the game runs as
administrator it can't be paused, and the question appears while it keeps running.

## Markers (tell Lockpick what just happened)

Markers tie a moment in the capture to what happened on screen, which turns guesses into facts ("this flag is the
statue falling"). While capturing:

- press **Ctrl+Shift+M** in the game (the key can be changed in **Settings → Capture markers**; avoid F9, which closes
  Resident Evil), or type a note on the Debug page and press **Add marker**;
- each marker records the time, room and game time; describe it on the Debug page whenever convenient — e.g.
  "pushed the statue", "picked up the map", "zombie got back up".

Markers are saved in `markers.jsonl` inside the zip and listed in the analysis report. Notes are whatever you type, so
don't put personal information in them.

## What is recorded

- The game's own state memory: its main state block (~22 KB: player, all enemies, inventory, item box, puzzle and
  event flags, rooms) and a small system block (timer, menus), recorded 10 times per second as changes only. Because
  the whole block is kept, captures can be re-checked later for things Lockpick doesn't understand yet.
- A readable trace of the values Lockpick already knows, 30 times per second (changes only).
- The game executable's fingerprint (file name, size, SHA-256, PE header fields), so unsupported builds can be added.
- The game window as it changes: where and how big it is, its monitor and DPI, windowed / borderless / exclusive
  fullscreen, and whether it's in front or minimized (`window-N.jsonl`, and a line in the events) — no window titles.
- Markers by themselves at each run start, split, run end and challenge fail (when runs or a challenge are on).
- Lockpick version and OS version.

A 20-minute capture is about 1–2 MB zipped (about 7 MB for two hours), well within GitHub's attachment limit.

Not recorded: screenshots, audio, input, anything outside the game's memory, file paths, computer or user names.

Capturing is read-only — it never writes to the game's memory or files, the same way a LiveSplit autosplitter works.
If you submit runs to a leaderboard, check that leaderboard's rules for external tools.

## Training captures

Tick **Training capture** on Debug → Capture before starting (off every time Lockpick starts) to help research on AI
that learns to play from people (#61). It records the same game data as a normal capture, but every frame (each tick of
the game's 30-a-second clock, polled twice as fast so none is missed) instead of ten times a second, and with the
buttons as the game read them: the raw button words (`0xBF0A04` held, `0xBF0A08` newly pressed), the game's own input
words (`0xBE9840`/`0xBE9842`, in the main state block) and the control type (`0xBE9A5C`). Nothing else: no screen,
sound, keystrokes outside the game, files or anything about you. It's about three times the size of a normal capture;
`capture-info.json` says `"Training": true`. `tools/rl/dataset.py` reads it like any capture.

## What to play

The **[bounty list](CAPTURE_BOUNTIES.md)** has everything still needed, by release — pick anything marked ⬜ and
mention the IDs (e.g. B102) when you send the zip. Send captures through a
[Capture submission](https://github.com/mscrnt/lockpick/issues/new?template=capture-submission.yml) issue.

## Especially useful (if it happens naturally)

- Using the item box (putting items in and taking them out)
- Playing as Rebecca
- Saving at a typewriter and loading the save from the title screen
- Many different items and weapons; speedruns cover a lot of ground quickly

There's no payment for captures or bounties, bounties are completed only by captures (never code changes), and
automated or AI-generated replies aren't accepted — see the [bounty rules](CAPTURE_BOUNTIES.md#bounty-rules). With your permission, you're credited by name in Lockpick's in-app
credits (Settings → Credits) and in [CONTRIBUTORS.md](../CONTRIBUTORS.md).
