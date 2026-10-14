"""Compares transcription passes, merges them, and lists the lines that need a human look.

    python tools/voice/compare.py [--dir %LOCALAPPDATA%\\Lockpick\\research\\voice]

Reads every transcripts*.jsonl in the folder (e.g. Whisper greedy, Whisper beam search, Canary-Qwen) and compares
the passes line by line (word error rate after normalizing spelling variants). Writes, locally:

- merged.jsonl: one entry per file with the chosen text and a confidence: "agreed" (two passes match; the name-aware
  Whisper beam-search wording is preferred), "scream" (screams/grunts, captioned "[screams]"), "no speech", or
  "uncertain";
- review.md: the uncertain lines with every pass's version.
"""
from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path

DIR = Path(os.environ.get("LOCALAPPDATA", ".")) / "Lockpick" / "research" / "voice"
DISAGREE = 0.15  # word error rate between passes above which two versions differ
PREFERRED = ("beam5", "whisper", "canary")  # Whisper passes get the character names as a prompt

# Spellings that mean the same thing, so they don't count as disagreement.
SAME = [
    (r"\balright\b", "all right"), (r"\bgonna\b", "going to"), (r"\byou're\b", "you are"), (r"\bi'm\b", "i am"),
    (r"\bit's\b", "it is"), (r"\bthat's\b", "that is"), (r"\bdon't\b", "do not"), (r"\bcan't\b", "cannot"),
    (r"\bi've\b", "i have"), (r"\bany more\b", "anymore"), (r"\bjudgement\b", "judgment"),
    (r"\bs\.?\s*t\.?\s*a\.?\s*r\.?\s*s\b", "stars"), (r"\b(uh|um|ugh|ah|oh|hah|huh)\b", ""),
]
SCREAM = re.compile(r"^\W*(a+h*|a*a+g+h+|wha-?|o+h+|u+g+h+)\W*$", re.I)
# Whisper's well-known inventions on silence, music and screams.
INVENTED = re.compile(
    r"thanks? (you )?for watching|see you in the next video|production of|closed caption|subscribe|jingle bells|"
    r"paid product|amara\.org|^\W*the\W*$|^\W*transcript\W*$|^\W*\.+\W*$", re.I)


def words(text: str) -> list[str]:
    text = text.lower().replace("’", "'")
    for pattern, same in SAME:
        text = re.sub(pattern, same, text)
    return re.sub(r"[^a-z0-9' ]+", " ", text).split()


def wer(a: list[str], b: list[str]) -> float:
    """Word-level edit distance divided by the longer length."""
    if not a and not b:
        return 0.0
    row = list(range(len(b) + 1))
    for i, wa in enumerate(a, 1):
        prev, row[0] = row[0], i
        for j, wb in enumerate(b, 1):
            prev, row[j] = row[j], min(row[j] + 1, row[j - 1] + 1, prev + (wa != wb))
    return row[-1] / max(len(a), len(b))


def merge(texts: dict[str, str], order: list[str]) -> tuple[str, str]:
    real = {n: t for n, t in texts.items() if words(t) and not INVENTED.search(t)}
    # Among versions another pass agrees with, take the one closest to all the others (so one pass's extra words
    # don't slip in); ties go to the name-aware Whisper passes.
    agreeing = [n for n in order if n in real
                and any(m != n and wer(words(real[n]), words(real[m])) <= DISAGREE for m in real)]
    if agreeing:
        best = min(agreeing, key=lambda n: (sum(wer(words(real[n]), words(real[m])) for m in real if m != n), order.index(n)))
        return real[best], "agreed"
    if any(SCREAM.match(t) for t in texts.values()):
        return "[screams]", "scream"
    # Canary returns nothing (or just "Transcript") when there is no speech, where Whisper invents text.
    if not real or ("canary" in texts and len(words(texts["canary"])) <= 1):
        return "", "no speech"
    return next(real[n] for n in order if n in real), "uncertain"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dir", type=Path, default=DIR)
    args = parser.parse_args()
    passes = {}
    for path in sorted(args.dir.glob("transcripts*.jsonl")):
        name = path.stem.replace("transcripts", "").strip("-") or "whisper"
        passes[name] = {e["file"]: e for e in map(json.loads, path.read_text(encoding="utf-8").splitlines()) if e}
    order = [n for n in PREFERRED if n in passes] + [n for n in passes if n not in PREFERRED]
    files = sorted(set().union(*[set(p) for p in passes.values()]), key=str.lower)
    merged = []
    for f in files:
        texts = {name: passes[name][f]["text"] for name in order if f in passes[name]}
        text, confidence = merge(texts, order)
        merged.append({"file": f, "text": text, "confidence": confidence, "passes": texts})

    with (args.dir / "merged.jsonl").open("w", encoding="utf-8") as out:
        for entry in merged:
            out.write(json.dumps(entry, ensure_ascii=False) + "\n")
    uncertain = [e for e in merged if e["confidence"] == "uncertain"]
    counts = {c: sum(e["confidence"] == c for e in merged) for c in ("agreed", "scream", "no speech", "uncertain")}
    lines = ["# Transcripts to review", "", f"{len(files)} files, passes: {', '.join(order)}. {counts}", ""]
    for e in uncertain:
        lines.append(f"## {e['file']} — chosen: {e['text']}")
        lines += [f"- **{name}**: {text or '(nothing)'}" for name, text in e["passes"].items()] + [""]
    (args.dir / "review.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"{len(files)} files: {counts} -> {args.dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
