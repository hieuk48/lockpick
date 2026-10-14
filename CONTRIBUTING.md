# Contributing to Lockpick

Thanks for your interest! There are three good ways to help.

## 1. Capture play sessions (no coding)

The most valuable help right now. Lockpick only trusts what it has seen the real game do.

1. Install Lockpick ([Lockpick-win-Setup.exe](https://github.com/mscrnt/lockpick/releases/latest/download/Lockpick-win-Setup.exe)).
2. Pick something from the [bounty list](docs/CAPTURE_BOUNTIES.md) (or the open
   [`bounty` issues](https://github.com/mscrnt/lockpick/issues?q=is%3Aissue+is%3Aopen+label%3Abounty)) — or just play.
3. **Debug → Start capture**, play, **Stop and save**, and send the zip with a
   [Capture submission](https://github.com/mscrnt/lockpick/issues/new?template=capture-submission.yml).

Details and privacy: [docs/CAPTURE.md](docs/CAPTURE.md).

### Bounty rules

1. **Bounties are unpaid.** No money, crypto or other payment is ever offered or owed. The reward is your name in
   Lockpick's in-app credits (Settings → Credits) and in CONTRIBUTORS.md, if you want it.
2. **Bounties are captures, never code changes.** A bounty is completed only by a capture zip of you playing the
   game (Debug → Start capture). Patches, pull requests or "proposed fixes" don't complete bounties and are never
   rewarded. Code contributions are welcome separately under the normal [contributing guide](#3-code).
3. **No automated or AI-generated replies.** Comments, issues or pull requests produced by bots or AI tools are hidden
   as spam and the account may be blocked and reported. Payment requests are ignored.


## 2. Report builds, bugs and ideas

- Unsupported game version: open an **Unsupported game build** issue with the fingerprint (Settings → Check build).
- Bugs: open a **Bug report** (the Debug page shows the log file location).
- Questions and ideas: [Discussions](https://github.com/mscrnt/lockpick/discussions).

## 3. Code

Code is never a bounty and is never paid; it's volunteer work like everything else here.
Please write your own changes — AI-generated or automated pull requests are closed.

How pull requests are handled:
- `main` is protected: changes from contributors arrive as pull requests, need passing CI (build, tests, data checks)
  and the maintainer's review before they can be merged. CI for outside contributors runs only after the maintainer
  approves it.
- Pull requests that ask for payment (wallet addresses, payout or "bounty claim" requests) are closed automatically
  and labelled `spam`; there is nothing to pay.
- Pull requests that delete large parts of the documentation or data, or don't match their description, are closed.


- Read [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) (build, test, verifying addresses) and
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (structure and decisions).
- Work is planned on the [Lockpick Roadmap board](https://github.com/users/mscrnt/projects/8) and by milestone
  (0.2.0 → 1.0.0); acceptance criteria are in [docs/ROADMAP.md](docs/ROADMAP.md).
- Ground rules: never guess memory addresses (evidence in the profile, verified against the real game), keep
  game-specific code in the game adapter, no game files or assets in the repository, and keep
  `dotnet build -c Release` warning-free and `dotnet test` green.

Lockpick is free and unpaid work all round. With your permission, contributors are credited in the app
(Settings → Credits) and in [CONTRIBUTORS.md](CONTRIBUTORS.md).
