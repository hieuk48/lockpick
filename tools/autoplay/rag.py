r"""Retrieval for the dev harness's vision questions and research digests, on the owner's local AI stack: notes are
embedded (TEI, e.g. Qwen3-Embedding), stored in Qdrant, and the closest ones are reranked (TEI reranker) before
they go into a prompt alongside the live memory facts (context.py).

    python -I tools/autoplay/rag.py index <run-dir>          # (re)build the 'lockpick' collection
    python -I tools/autoplay/rag.py ask "where is the shield key?"

Notes come from Lockpick's own data and the local research output: room atlas entries (room files + live survey per
phase + vision notes), flag labels, item and enemy names. Nothing leaves the LAN. Endpoints: LOCKPICK_EMBED_URL
(…:8557), LOCKPICK_RERANK_URL (…:8558), LOCKPICK_QDRANT_URL (…:6333).
"""
from __future__ import annotations

import json
import os
import sys
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "data" / "games" / "re1"
EMBED = os.environ.get("LOCKPICK_EMBED_URL", "").rstrip("/")
RERANK = os.environ.get("LOCKPICK_RERANK_URL", "").rstrip("/")
QDRANT = os.environ.get("LOCKPICK_QDRANT_URL", "").rstrip("/")
COLLECTION = "lockpick"


def _post(url: str, body: dict, method: str = "POST") -> object:
    req = urllib.request.Request(url, json.dumps(body).encode(), {"Content-Type": "application/json"}, method=method)
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.load(r)


def embed(texts: list[str]) -> list[list[float]]:
    vectors = []
    for i in range(0, len(texts), 32):
        vectors += _post(EMBED + "/embed", {"inputs": texts[i:i + 32]})
    return vectors


def notes(run: Path | None) -> list[dict]:
    names = json.loads((DATA / "names.json").read_text(encoding="utf-8"))
    rooms = json.loads((DATA / "rooms.json").read_text(encoding="utf-8"))
    labels = json.loads((DATA / "flag-labels.json").read_text(encoding="utf-8"))["labels"]
    found = [{"text": f"Story flag {l['bank']} mask {l['mask']}"
              + (f" ({', '.join(l['characters'])}'s game)" if l.get("characters") else "") + f": {l['label']}.", "kind": "flag"}
             for l in labels if l["label"]]
    found += [{"text": f"Item {i}: {n}.", "kind": "item"} for i, n in names["items"].items()]
    found += [{"text": f"Enemy type {i}: {n}.", "kind": "enemy"} for i, n in names["enemies"].items()]
    for room, info in rooms["rooms"].items():
        if info.get("name"):
            found.append({"text": f"Room {room} is the {info['name']} ({info.get('area', '')}).", "kind": "room", "room": room})
    research = run.parent if run else None
    # Room scripts (tools/rdt/scd.py --json): what each room places or starts, and under which conditions.
    for character in ("jill", "chris"):
        scripts = research / f"scripts-{character}.json" if research else None
        if scripts and scripts.exists():
            for room, listing in json.loads(scripts.read_text(encoding="utf-8")).items():
                facts = []
                for part in ("init", "main"):
                    for st in listing[part]:
                        if st["kind"] in ("item", "event", "flag", "door", "player", "enemy", "fmv") or (
                                st["kind"] == "action" and st.get("handler") in (3, 4, 7, 9)):
                            guard = (" when " + " / ".join(st["guards"])) if st["guards"] else ""
                            facts.append(st["text"] + guard)
                for i in range(0, len(facts), 6):  # small chunks rerank better than one long note
                    found.append({"text": f"Room {room} script ({character}): " + "; ".join(facts[i:i + 6]),
                                  "kind": "script", "room": room})
    # Pickup sweep results and flag-timeline windows, when present.
    for name in ("pickup-jill.jsonl", "pickup-chris.jsonl"):
        path = research / "pickup" / name if research else None
        if path and path.exists():
            for line in path.read_text(encoding="utf-8").splitlines():
                e = json.loads(line)
                item = names["items"].get(str(e["item"]), e["item"])
                outcome = e.get("note") or e.get("error") or ("picked up" + (" through scenery" if e.get("throughScenery") else "")
                                                               if e.get("itemMatches") else "no pickup prompt")
                found.append({"text": f"Room {e['room']} ({e['character']}, new game): {item} (item flag {e['flag']}) — {outcome}.",
                              "kind": "pickup", "room": e["room"]})
    timeline = research / "flag-timeline.md" if research else None
    if timeline and timeline.exists():
        for line in timeline.read_text(encoding="utf-8").splitlines():
            if line.startswith("- "):
                found.append({"text": "Flag timeline: " + line[2:].replace("`", ""), "kind": "timeline"})
    if run and (run / "atlas.md").exists():
        room, lines = None, []
        for line in (run / "atlas.md").read_text(encoding="utf-8").splitlines():
            if line.startswith("### "):
                if room and lines:
                    found.append({"text": f"Room {room}: " + " ".join(lines), "kind": "atlas", "room": room[:3]})
                room, lines = line[4:].strip(), []
            elif line.startswith("- ") and room:
                lines.append(line[2:].replace("*", ""))
        if room and lines:
            found.append({"text": f"Room {room}: " + " ".join(lines), "kind": "atlas", "room": room[:3]})
    return found


def index(run: Path | None) -> int:
    items = notes(run)
    vectors = embed([n["text"][:4000] for n in items])
    try:
        _post(f"{QDRANT}/collections/{COLLECTION}", {}, method="DELETE")
    except urllib.error.HTTPError:
        pass
    _post(f"{QDRANT}/collections/{COLLECTION}", {"vectors": {"size": len(vectors[0]), "distance": "Cosine"}}, method="PUT")
    points = [{"id": str(uuid.uuid5(uuid.NAMESPACE_URL, n["text"])), "vector": v, "payload": n} for n, v in zip(items, vectors)]
    for i in range(0, len(points), 256):
        _post(f"{QDRANT}/collections/{COLLECTION}/points?wait=true", {"points": points[i:i + 256]}, method="PUT")
    return len(points)


def retrieve(query: str, k: int = 4, room: str | None = None, pool: int = 24) -> list[str]:
    """The k most relevant notes for a query (vector search, then reranked). `room` boosts notes about that room."""
    if not (EMBED and QDRANT):
        return []
    vector = embed([query])[0]
    body: dict = {"vector": vector, "limit": pool, "with_payload": True}
    hits = _post(f"{QDRANT}/collections/{COLLECTION}/points/search", body)["result"]
    texts = [h["payload"]["text"] for h in hits]
    if room:
        texts = [t for t in texts if f"Room {room}" in t] + [t for t in texts if f"Room {room}" not in t]
    if RERANK and texts:
        ranked = _post(RERANK + "/rerank", {"query": query, "texts": [t[:2000] for t in texts]})
        texts = [texts[r["index"]] for r in sorted(ranked, key=lambda r: -r["score"])]
    return texts[:k]


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if argv[0] == "index":
        print(f"indexed {index(Path(argv[1]) if len(argv) > 1 else None)} notes into '{COLLECTION}'")
    elif argv[0] == "ask":
        for t in retrieve(" ".join(argv[1:])):
            print("-", t[:300])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
