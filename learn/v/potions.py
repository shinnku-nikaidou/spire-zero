"""How often V would still throw a potion away: the measure `MacroBrain.Offered` waits on (docs: macro-brain/search §1).

`Offered` keeps a potion's discard out of the options unless it makes room for a potion on the screen: V rated a held
potion below the empty slot, and took the discard wherever it was offered. This asks a V the same question on decisions
it was not trained on: of those that hold a potion, the share where V values the run higher with one of its potions
gone. While the share is above LIMIT the rule still decides; under it the rule changes next to nothing, and it is
deleted: the code, its tests and its paragraph in the book.

`v.train` puts the share in every net's report (`potion_below_empty`). For a net file and decision logs:

    uv run python -m v.potions --value ../weights/all/NAME.json --logs L1,L2 --encoder "SIM encode"
"""

import json
from typing import Annotated

import torch
import typer

import common
from common import HEAD, LearnError
from v import data, export
from v.net import Batch, Net, values

script = common.app()

LIMIT = 0.01  # the share under which Offered goes
DECISIONS = 20000  # at most this many decisions are asked, spread evenly over those that hold a potion
BATCH = 512


def below_empty(valued, samples: list[dict]) -> dict:
    """Of the samples that hold a potion (DECISIONS of them at most), the share V values higher without one of their
    potions; `valued` gives V's value of each sample of a list. A potion is taken out of the tokens, which is all its
    discard changes in what V reads: the belt's size stays."""
    holding = [s for s in samples if s["tokens"]["potions"]]
    asked = holding[:: max(1, -(-len(holding) // DECISIONS))]
    without = []
    for i, s in enumerate(asked):
        for potion in sorted(set(s["tokens"]["potions"])):
            left = list(s["tokens"]["potions"])
            left.remove(potion)
            without.append((i, s | {"tokens": s["tokens"] | {"potions": left}}))
    kept = valued(asked)
    thrown = {i for (i, _), v in zip(without, valued([s for _, s in without]), strict=True) if v > kept[i]}
    return {"decisions": len(asked), "share": len(thrown) / len(asked) if asked else None}


def by(model, make):
    """`valued` for below_empty: the model's values, in batches that `make` builds from samples."""

    def valued(part):
        with torch.no_grad():
            return [float(v) for k in range(0, len(part), BATCH) for v in values(model, make(part[k : k + BATCH]))]

    return valued


@script.command(help=__doc__)
def main(
    value: Annotated[str, typer.Option(help="the V's net file")],
    logs: Annotated[str, typer.Option(help="decision logs, comma-separated")],
    encoder: Annotated[str, typer.Option(help="how to start the encoder that reads the runs: \"SIM encode\"")],
) -> None:
    net = export.read(value)
    floors, tables = net["floors"], net["vocab"]
    model = Net({t: len(ids) for t, ids in tables.items()}, floors)
    model.load_state_dict(export.weights(net, model))
    model.eval()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    mean = torch.tensor(net["state"]["mean"], dtype=torch.float64)
    std = torch.tensor(net["state"]["std"], dtype=torch.float64)
    reader = common.Encoder(encoder)
    samples = []
    for log in logs.split(","):
        runs = {m[1] for m in map(HEAD.match, open(log)) if m}
        lines = data.macro_lines(log, runs)
        read = reader.runs([line for _, line in lines], log)
        samples += [data.sample(run, r["run"], floors) for (run, _), r in zip(lines, read, strict=True) if r]
    reader.close()
    if not samples:
        raise LearnError(f"no macro decision in {logs}")
    found = below_empty(by(model, lambda part: Batch(part, index, mean, std, floors, "cpu")), samples)
    print(json.dumps(found | {"limit": LIMIT, "offered_goes": found["share"] is not None and found["share"] < LIMIT}))


if __name__ == "__main__":
    common.run(script)
