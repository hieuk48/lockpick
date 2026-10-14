"""Builds the subtitles pack (re1/en/voice.json) for the separate lockpick-subtitles repository.

    python tools/voice/export_pack.py "G:\\GOG Games\\Resident Evil" <lockpick-subtitles checkout>

Takes merged.jsonl from compare.py (local research folder) and writes one entry per voice file that has speech:
id, kind (dialogue / scream), duration, text, timed segments (from a Whisper pass) and the file's SHA-256, so the app
can check the pack matches the player's game files. The pack is published separately because the dialogue belongs to
Capcom; it is never committed to the Lockpick repository.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from datetime import date
from pathlib import Path

RESEARCH = Path(os.environ.get("LOCALAPPDATA", ".")) / "Lockpick" / "research" / "voice"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("install", type=Path)
    parser.add_argument("pack", type=Path, help="lockpick-subtitles checkout")
    parser.add_argument("--research", type=Path, default=RESEARCH)
    args = parser.parse_args(argv)

    merged = [json.loads(line) for line in (args.research / "merged.jsonl").read_text(encoding="utf-8").splitlines() if line]
    timed = {}
    for name in ("transcripts-beam5.jsonl", "transcripts.jsonl"):
        path = args.research / name
        if path.exists():
            for e in map(json.loads, path.read_text(encoding="utf-8").splitlines()):
                timed.setdefault(e["file"], {})[e["text"]] = e

    voice_dir = (args.install / "USA" / "Voice") if (args.install / "USA").is_dir() else args.install / "Voice"
    lines = []
    for e in merged:
        if not e["file"].startswith("Voice/") or e["confidence"] in ("no speech", "uncertain"):
            continue
        name = e["file"].split("/", 1)[1]
        wav = voice_dir / name
        source = timed.get(e["file"], {}).get(e["text"])  # the Whisper pass whose wording was chosen, if any
        any_pass = next(iter(timed.get(e["file"], {}).values()), {})
        segments = [{"start": s["start"], "end": s["end"], "text": s["text"]} for s in (source or {}).get("segments", [])]
        lines.append({
            "id": name.rsplit(".", 1)[0].lower(),
            "kind": "scream" if e["confidence"] == "scream" else "dialogue",
            "seconds": any_pass.get("seconds"),
            "text": e["text"],
            "segments": segments if e["confidence"] != "scream" else [],
            "sha256": hashlib.sha256(wav.read_bytes()).hexdigest() if wav.exists() else None,
        })

    pack = {
        "format": 1,
        "version": date.today().isoformat(),
        "game": "Resident Evil (1996), PC, USA English voice-over (USA\\Voice\\*.wav)",
        "method": "Machine transcription: Whisper large-v3 (greedy and beam search, prompted with character names) "
                  "cross-checked with NVIDIA Canary-Qwen 2.5B; lines where at least two passes agree. Not yet reviewed by ear.",
        "notice": "Fan transcription for accessibility. The dialogue belongs to Capcom and is not covered by Lockpick's license.",
        "lines": lines,
    }
    out = args.pack / "re1" / "en" / "voice.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(pack, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {out}: {len(lines)} lines ({sum(l['kind'] == 'scream' for l in lines)} screams)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
