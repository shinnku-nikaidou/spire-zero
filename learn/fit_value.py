"""Fit V's weights (docs: learning §2) from self-play: each macro decision's features against what its run reached.

The decision logs (`run --log`) carry V's features on every macro decision; the run records (`run --records`) carry
each run's outcome, joined by run id. While no run wins, every P(win) target is 0, so the target is the floor the run
reached. The fit is ridge regression on standardized features, each run weighing the same however many decisions it
has, with every fifth run (by run id) held out. The output is a weights file for `run --value`, in the weights history
by default (docs: learning §6).

    uv run python fit_value.py --logs a.jsonl,b.jsonl --records a.jsonl,b.jsonl --name v1
"""

import argparse
import json
import math
import warnings

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402

from weights import out_path  # noqa: E402


def read_lines(paths: str):
    for path in paths.split(","):
        with open(path) as f:
            for line in f:
                if line.strip():
                    yield json.loads(line)


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def fit(x: torch.Tensor, y: torch.Tensor, w: torch.Tensor, ridge: float):
    """Weighted ridge on standardized columns; the intercept is not penalized. Returns (coefficients, intercept)."""
    mean = (w[:, None] * x).sum(0) / w.sum()
    sd = ((w[:, None] * (x - mean) ** 2).sum(0) / w.sum()).sqrt().clamp_min(1e-9)
    z = (x - mean) / sd
    y_mean = (w * y).sum() / w.sum()
    a = z.T @ (w[:, None] * z) + ridge * w.sum() * torch.eye(x.shape[1], dtype=x.dtype)
    beta = torch.linalg.solve(a, z.T @ (w * (y - y_mean)))
    coef = beta / sd
    return coef, float(y_mean - coef @ mean)


def r2(x, y, w, coef, intercept) -> float:
    pred = x @ coef + intercept
    y_mean = (w * y).sum() / w.sum()
    return float(1 - (w * (y - pred) ** 2).sum() / (w * (y - y_mean) ** 2).sum())


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--logs", required=True, help="decision logs, comma-separated")
    p.add_argument("--records", required=True, help="run records, comma-separated")
    p.add_argument("--name", required=True, help="the weights' name, as the champion will show it")
    p.add_argument("--out", help="where the file goes (default: weights/history/NAME.json)")
    p.add_argument("--ridge", type=float, default=0.01, help="penalty per unit of sample weight, on standardized features")
    args = p.parse_args()
    out_file = out_path(args.name, args.out)

    runs = {r["run_id"]: r for r in read_lines(args.records) if r.get("run_id")}
    if any(r["outcome"] == "won" for r in runs.values()):
        print("note: some runs won; the target is still the floor reached")
    rows = [e for e in read_lines(args.logs) if "features" in e and e["run"] in runs]
    names = sorted({k for e in rows for k in e["features"]})
    per_run = {}
    for e in rows:
        per_run[e["run"]] = per_run.get(e["run"], 0) + 1

    def tensors(test: bool):
        part = [e for e in rows if held_out(e["run"]) == test]
        x = torch.tensor([[e["features"].get(n, 0.0) for n in names] for e in part], dtype=torch.float64)
        y = torch.tensor([float(runs[e["run"]]["floor"]) for e in part], dtype=torch.float64)
        w = torch.tensor([1.0 / per_run[e["run"]] for e in part], dtype=torch.float64)
        return x, y, w

    (x, y, w), (xt, yt, wt) = tensors(False), tensors(True)
    coef, intercept = fit(x, y, w, args.ridge)
    floor = names.index("floor")
    floor_coef, floor_intercept = fit(x[:, [floor]], y, w, args.ridge)
    report = {
        "runs": len({e["run"] for e in rows}),
        "held_out_runs": len({e["run"] for e in rows if held_out(e["run"])}),
        "samples": len(rows),
        "ridge": args.ridge,
        "r2_train": r2(x, y, w, coef, intercept),
        "r2_held_out": r2(xt, yt, wt, coef, intercept),
        "r2_held_out_floor_only": r2(xt[:, [floor]], yt, wt, floor_coef, floor_intercept),
    }
    weights = {n: float(c) for n, c in zip(names, coef)}
    out = {"name": args.name, "target": "floor reached", "fit": report, "intercept": intercept, "weights": weights}
    with open(out_file, "w") as f:
        json.dump(out, f, indent=2)
        f.write("\n")

    print(json.dumps(report))
    for n in names:
        print(f"  {n:16} {weights[n]:+.4f}")
    assert all(math.isfinite(v) for v in weights.values())


if __name__ == "__main__":
    main()
