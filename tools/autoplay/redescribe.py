r"""Re-describes survey rooms whose vision note is missing or 'black', from the saved screenshot (dev-only).

    python -I tools/autoplay/redescribe.py <run-dir>
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import ap  # noqa: E402
import survey  # noqa: E402


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    fixed = 0
    for path in sorted((Path(argv[0]) / "survey").glob("survey-*.jsonl")):
        rows = [json.loads(l) for l in path.read_text(encoding="utf-8").splitlines() if l.strip()]
        for row in rows:
            seen = (row.get("seen") or "").strip().lower().strip(".")
            if row.get("shot") and Path(row["shot"]).exists() and seen in ("", "black"):
                row["seen"] = ap.describe(Path(row["shot"]), survey.DESCRIBE)
                fixed += 1
        path.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in rows), encoding="utf-8")
    print(f"re-described {fixed} rooms")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
