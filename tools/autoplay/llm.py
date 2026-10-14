"""Local LLM helper for the dev harness: digests long text (decompiled code, logs) so the agent reads a summary.

Set LOCKPICK_LLM_URL (OpenAI-compatible, e.g. http://host:8000/v1) and LOCKPICK_LLM_MODEL. Digests are leads only:
addresses and behaviour are confirmed against the game's own code before anything relies on them.
"""
from __future__ import annotations

import json
import os
import sys
import urllib.request

URL = (os.environ.get("LOCKPICK_LLM_URL") or os.environ.get("LOCKPICK_VISION_URL") or "").rstrip("/")
MODEL = os.environ.get("LOCKPICK_LLM_MODEL") or os.environ.get("LOCKPICK_VISION_MODEL") or "default"


def ask(prompt: str, max_tokens: int = 600) -> str:
    body = {"model": MODEL, "max_tokens": max_tokens, "temperature": 0,
            "chat_template_kwargs": {"enable_thinking": False},
            "messages": [{"role": "user", "content": prompt}]}
    req = urllib.request.Request(URL + "/chat/completions", json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)["choices"][0]["message"]["content"].strip()


def digest(text: str, question: str, max_tokens: int = 600) -> str:
    # ~24k tokens of input fits the 32k context with room for the answer.
    text = text[:90000]
    return ask(f"{question}\n\nBe terse and factual; quote addresses exactly as written; say 'not stated' rather than "
               f"guessing.\n\n-----\n{text}", max_tokens)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    question = sys.argv[1]
    text = "".join(open(p, encoding="utf-8", errors="replace").read() + "\n" for p in sys.argv[2:]) or sys.stdin.read()
    print(digest(text, question))
