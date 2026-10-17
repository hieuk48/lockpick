r"""Writes the SHA-256 of every file an untouched GOG install of Resident Evil ships (sha256sum format), for the
leaderboard check that the game is unmodified (docs/RUNS.md). Run against a fresh install only.

    python tools/re1/install_manifest.py "G:\GOG Games\Resident Evil" data/games/re1/install/gog-hotfix4.sha256

The Japanese release (GOG's Japanese installer, unpacked with innoextract --gog so nothing is installed), only the
files the game runs on:

    python tools/re1/install_manifest.py <unpacked> data/games/re1/install/gog-jpn-hotfix4.sha256 ^
        --only Biohazard.exe JPN ddraw.dll dinput.dll dxcfg.exe goginput.ini

Files that change in normal play or setup are left out: saves, the display settings and their backup, screenshots,
GOG's log and uninstaller data.
"""
from __future__ import annotations

import hashlib
import sys
from pathlib import Path

SKIP_DIRS = {"save"}
SKIP_FILES = {"dxcfg.ini", "dxcfg.ini.bak", "goglog.ini", "unins000.dat", "unins000.ini", "unins000.msg", "webcache.zip"}
SKIP_SUFFIXES = {".bmp"}


def main(argv: list[str]) -> int:
    install, out = Path(argv[0]), Path(argv[1])
    only = {name.lower() for name in argv[argv.index("--only") + 1:]} if "--only" in argv else None
    lines = []
    for path in sorted(install.rglob("*"), key=lambda p: str(p.relative_to(install)).lower()):
        rel = path.relative_to(install)
        if only is not None and rel.parts[0].lower() not in only:
            continue
        if not path.is_file() or rel.parts[0].lower() in SKIP_DIRS or rel.name.lower() in SKIP_FILES or path.suffix.lower() in SKIP_SUFFIXES:
            continue
        digest = hashlib.sha256(path.read_bytes()).hexdigest().upper()
        lines.append(f"{digest}  {rel.as_posix()}")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {len(lines)} files to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
