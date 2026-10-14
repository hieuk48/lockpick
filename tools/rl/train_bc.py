r"""Trains a behaviour-cloning policy on dataset.py's output (dev-only RL tooling): a small MLP with one head per
button group (move, turn, run, aim, action). One whole capture is held out, so the scores show how well it does on play
it never saw; the baseline is always predicting the most common choice.

    "%LOCALAPPDATA%\Lockpick\research\.venv\Scripts\python.exe" tools/rl/train_bc.py <data.npz> <model.pt> [--holdout NAME]
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
import torch
from torch import nn

HEADS = [("move", 3), ("turn", 3), ("run", 2), ("aim", 2), ("act", 2)]


class Policy(nn.Module):
    def __init__(self, features: int, hidden: int = 256) -> None:
        super().__init__()
        self.body = nn.Sequential(nn.Linear(features, hidden), nn.ReLU(), nn.Linear(hidden, hidden), nn.ReLU(),
                                  nn.Linear(hidden, hidden), nn.ReLU())
        self.heads = nn.ModuleList(nn.Linear(hidden, n) for _, n in HEADS)

    def forward(self, x: torch.Tensor) -> list[torch.Tensor]:
        h = self.body(x)
        return [head(h) for head in self.heads]


def main(argv: list[str]) -> int:
    data, out = Path(argv[0]), Path(argv[1])
    holdout = argv[argv.index("--holdout") + 1] if "--holdout" in argv else None
    d = np.load(data)
    X, Y, sources = d["X"], d["Y"], d["sources"]
    names = sorted(set(sources))
    holdout = holdout or names[-1]
    test = sources == holdout
    device = "cuda" if torch.cuda.is_available() else "cpu"
    xt, yt = torch.tensor(X[~test], device=device), torch.tensor(Y[~test], device=device)
    xv, yv = torch.tensor(X[test], device=device), torch.tensor(Y[test], device=device)
    mean, std = xt.mean(0), xt.std(0) + 1e-6
    model = Policy(X.shape[1]).to(device)
    optim = torch.optim.AdamW(model.parameters(), lr=1e-3, weight_decay=1e-4)
    # Class weights: the rare choices (turning, back, aiming) matter as much as walking straight.
    weights = []
    for k, (_, n) in enumerate(HEADS):
        counts = torch.bincount(yt[:, k], minlength=n).float() + 1
        weights.append((counts.sum() / counts / n).clamp(max=10))
    losses = [nn.CrossEntropyLoss(weight=w) for w in weights]
    for epoch in range(40):
        model.train()
        order = torch.randperm(len(xt), device=device)
        for start in range(0, len(xt), 1024):
            idx = order[start:start + 1024]
            logits = model((xt[idx] - mean) / std)
            loss = sum(fn(l, yt[idx, k]) for k, (fn, l) in enumerate(zip(losses, logits)))
            optim.zero_grad()
            loss.backward()
            optim.step()
    model.eval()
    report = {"holdout": holdout, "trainRows": int((~test).sum()), "testRows": int(test.sum()), "heads": {}}
    with torch.no_grad():
        logits = model((xv - mean) / std)
        for k, (name, n) in enumerate(HEADS):
            pred = logits[k].argmax(1)
            truth = yv[:, k]
            majority = torch.bincount(yt[:, k], minlength=n).argmax()
            per_class = {int(c): round(float((pred[truth == c] == c).float().mean()), 3)
                         for c in range(n) if (truth == c).any()}
            report["heads"][name] = {"accuracy": round(float((pred == truth).float().mean()), 3),
                                     "baseline": round(float((truth == majority).float().mean()), 3),
                                     "recallPerChoice": per_class}
    torch.save({"state": model.state_dict(), "mean": mean.cpu(), "std": std.cpu(), "features": X.shape[1]}, out)
    print(json.dumps(report, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
