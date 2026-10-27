"""Fit V's weights (docs: learning §2) from self-play: each macro decision's features against what its run reached.

The decision logs (`run --log`) carry V's features on every macro decision; the run records (`run --records`) carry
each run's outcome, joined by run id. While no run wins, every P(win) target is 0, so the target is the floor the run
reached. The fit is ridge regression on standardized features, each run weighing the same however many decisions it
has, with every fifth run (by run id) held out. The output is a weights file for `run --value`, in the weights history
by default (docs: learning §6).

    uv run python fit_value.py --logs a.jsonl,b.jsonl --records a.jsonl,b.jsonl --name v1
"""

import json
import math
import warnings
from typing import Annotated

import typer
from loguru import logger

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402

import common  # noqa: E402
from common import LearnError, read_lines  # noqa: E402
from weights import out_path  # noqa: E402

script = common.app()


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


@script.command(help=__doc__)
def main(
    logs: Annotated[str, typer.Option(help="decision logs, comma-separated")],
    records: Annotated[str, typer.Option(help="run records, comma-separated")],
    name: Annotated[str, typer.Option(help="the weights' name, as the champion will show it")],
    out: Annotated[str | None, typer.Option(help="where the file goes (default: weights/history/NAME.json)")] = None,
    ridge: Annotated[float, typer.Option(help="penalty per unit of sample weight, on standardized features")] = 0.01,
) -> None:
    out_file = out_path(name, out)

    runs = {r["run_id"]: r for r in read_lines(records) if r.get("run_id")}
    if any(r["outcome"] == "won" for r in runs.values()):
        logger.warning("some runs won; the target is still the floor reached")
    rows = [e for e in read_lines(logs) if "features" in e and e["run"] in runs]
    if not rows:
        raise LearnError("no macro decision with features in the logs joins a run of the records")
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
    coef, intercept = fit(x, y, w, ridge)
    floor = names.index("floor")
    floor_coef, floor_intercept = fit(x[:, [floor]], y, w, ridge)
    report = {
        "runs": len({e["run"] for e in rows}),
        "held_out_runs": len({e["run"] for e in rows if held_out(e["run"])}),
        "samples": len(rows),
        "ridge": ridge,
        "r2_train": r2(x, y, w, coef, intercept),
        "r2_held_out": r2(xt, yt, wt, coef, intercept),
        "r2_held_out_floor_only": r2(xt[:, [floor]], yt, wt, floor_coef, floor_intercept),
    }
    weights = {n: float(c) for n, c in zip(names, coef, strict=True)}
    if not all(math.isfinite(v) for v in weights.values()):
        raise LearnError(f"the fit is not finite: {weights}")
    with open(out_file, "w") as f:
        json.dump(
            {"name": name, "target": "floor reached", "fit": report, "intercept": intercept, "weights": weights},
            f,
            indent=2,
        )
        f.write("\n")
    logger.info("wrote {}", out_file)
    for n in names:
        logger.info("  {:16} {:+.4f}", n, weights[n])
    print(json.dumps(report))


if __name__ == "__main__":
    common.run(script)
