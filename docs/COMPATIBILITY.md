# Compatibility

Lockpick reads memory only from builds it can identify **exactly**. Anything else is reported as
unsupported and never opened.

## Supported

| Game | Store / build | Executable | Size | SHA-256 | Profile | Status |
|---|---|---|---|---|---|---|
| Resident Evil (1996) | GOG, USA data, "1.0 hotfix4" (GOG build 59172465945261369) | `ResidentEvil.exe` | 1,004,112 | `205BD6FE…CAEEE4A78` | `re1-gog-usa-hotfix4` | Live-verified (Chris and Jill games, Rebecca swap) |
| Biohazard (1996) | Japanese PC 1.01: MediaKite/Sourcenext patched to 1.01, or GOG/Steam's Japanese version with the 1.01 exe, usually with **Classic REbirth** | `Biohazard.exe` | 962,560 | `25BE34D4…D23A85302` | `re1-jpn-101` | **Preview**: every address found in this exe's own code (static), not yet checked in play; state and runs work, cheats stay off |

The Japanese 1.01 profile was made by `tools/re1/port_profile.py` from the GOG one: for each address Lockpick reads,
the code that uses it in the GOG exe was found again in the 1.01 exe (`tools/re1/xmap.py`), and the sites agree on the
new address (e.g. stage 114 of 122 sites, HP 150 of 154, the timer 6 of 7, no disagreement). The game-state block moved
by `+0x4EED0`, the system globals by `−0x279920`, the room action table by `−0x258F00` — the same layout. The RE1
autosplitter's "JapaneseREbirth" addresses (module size 10027008 = `0x990000`) agree. It becomes "live-verified" after a
probe trace on that build (`dotnet run --project tools/Lockpick.Probe -- trace`).

## speedrun.com boards (Resident Evil, `residentevil`, read 2026-10-17)

| Board | What the rules allow | Lockpick |
|---|---|---|
| PC | Japanese MediaKite (or a reprint) with Classic REbirth (recommended) or the 1.7/1.8 patch; GOG and Steam allowed, may use REbirth. LiveSplit's IGT script on screen, no REbirth quickturn / tactical reload / debug menu, no mods | GOG English hotfix4: supported. Japanese 1.01 (+ REbirth): preview. GOG Japanese unpatched, Steam: not yet (need their executables) |
| EMU | PS1 versions on DuckStation (fixed settings) or PSXjin / BizHawk; Director's Cut is the fast version, PAL banned | Not yet: needs emulator support (reading the console's RAM inside the emulator) and a PS1 disc image to research |
| PlayStation, PSN, Sega Saturn | Real consoles (PS1–PS5, PSP/Vita, Saturn) | Out of reach: nothing runs on the PC to read |
| `re1ce` (category extensions), `re123` | Same versions; REbirth required on PC there | As PC |

The leaderboard-run checks (Runs → Leaderboard run) know both PC builds: on GOG English every file is compared with
the release; on the Japanese game the exe and REbirth are checked by hash (every REbirth release 1.0.0–1.1.4,
`data/games/re1/install/rebirth.json`) and REbirth's banned options are read from its `config.ini`.

## Known other builds (not supported yet)

| Build | How to recognize | Notes |
|---|---|---|
| GOG Japanese (`Biohazard.exe`, unpatched) | ModuleMemorySize 77144064 | Same layout as 1.01 per the autosplitter; needs its own exe for a port |
| Steam (app 4249100) | REbirth 1.1.4 ships a launcher for it and puts the game in `japanese\` | Not researched; executables unknown |
| Earlier GOG English revision | SizeOfImage `0x49B3000` (from the RE1 autosplitter) | Addresses differ slightly; needs its own profile |
| Other versions REbirth 1.1.4 lists | European 1.00c, Asia 1.00, UK/USA 1.00, German, French, PowerVR UK/US and Japanese 1.00, PC Gamer demo | Not used on the leaderboard as far as known |
| HD Remaster (2015), REmake | — | Different games; out of scope |

To request support, run `lockpick-probe identify "<path to exe>"` and open an "Unsupported game build" issue.

## Environment notes

- **Privileges:** Lockpick must run at the same privilege level as the game. If the game was started
  elevated (e.g. by the GOG installer's "Launch" button), restart the game normally or run Lockpick as
  administrator. Lockpick itself never requests elevation.
- **GOG DirectX wrapper:** windowed size is controlled by `dxcfg.ini` (`display=WIDTHxHEIGHT@HZ`,
  `presentation=windowed`, `aspect=enabled` keeps 4:3). Example for a 16:9 window on an ultrawide:
  `display=1920x1080@60`. Lockpick does not edit game files.
- **Focus:** the game stops rendering when it is not the active window (original behavior).
- **Multiple monitors / DPI:** Lockpick is per-monitor DPI aware and has a compact layout for narrow or
  portrait windows (< 760 px wide).
- **Classic REbirth:** replaces the DirectDraw wrapper (`ddraw.dll`) and keeps its own settings (`config.ini`) and
  saves (`savedata\`); Lockpick reads both and leaves the display settings to REbirth's own setup. It runs only on
  the Japanese game (on GOG: the Japanese version with the 1.01 exe).

## Platform

Windows 10 (1803+) or 11, x64. The app is 64-bit and reads the 32-bit game through WOW64.
High-resolution polling uses `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` (falls back to the default timer).
