"""f's net (plans/2026-11-21-1655-fight-score-f.md 2.2): of a run and each potion asked about, the HP the potion will
save in the fight where it is drunk, f, which a fight's score counts (HP + max HP + f of each potion kept).

It reads a run as V does (V's body, v/net.py) with all of V's inputs but the HP and max HP: their standardized numbers
read as 0, their mean, whatever they are. Were f to read them, it could fall by more than the HP rises, and a fight's
score with it. Each potion asked is the body's output with the potion's row, through one layer, then softplus: f is
never below 0, so keeping a potion is never worse than throwing it away. Its unit is HP.
"""

import sts2_batch
import torch
from torch import nn

from v import spec
from v.net import Body

INPUTS = [*spec.INPUTS, "asked"]  # the ONNX graph's inputs, in order: V's, then the potions asked about
SCALE = 100.0  # the head gives HP / SCALE
BLIND = ("hp", "max_hp")  # the run's numbers f does not read


class Batch:
    """Samples of V's (v/data.py) holding a potion, as tensors: V's inputs, the potions asked (the ones held, in the
    belt's order, `saves`) and what each saved (v/saves.py; unknown ones masked out), and the samples' weights."""

    def __init__(self, samples, index, mean, std, device):
        feed = {name: t.to(device) for name, t in sts2_batch.v_inputs([s["tokens"] for s in samples], index).items()}
        x = torch.tensor([s["state"] for s in samples], dtype=torch.float64)
        feed["state"] = ((x - mean) / std).float().to(device)
        saves = [s["saves"] for s in samples]
        width = max(1, max(len(x) for x in saves))
        potions = index["potions"]
        feed["asked"] = torch.tensor([[potions.get(p, 0) for p, _ in x] + [0] * (width - len(x)) for x in saves]).to(device)
        self.feed = feed
        self.saved = torch.tensor([[v or 0.0 for _, v in x] + [0.0] * (width - len(x)) for x in saves]).to(device)
        self.known = torch.tensor([[float(v is not None) for _, v in x] + [0.0] * (width - len(x)) for x in saves]).to(device)
        self.weight = torch.tensor([s["weight"] for s in samples]).to(device)

    def inputs(self):
        """The net's inputs, in the order of INPUTS."""
        return tuple(self.feed[name] for name in INPUTS)


class Net(Body):
    def __init__(self, sizes: dict[str, int]):
        super().__init__(sizes)
        d, h = self.DIM, self.HIDDEN
        self.head = nn.Sequential(nn.Linear(self.BODY + d, h), nn.ReLU(), nn.Linear(h, 1))
        self.register_buffer("seen", torch.tensor([float(n not in BLIND) for n in spec.STATE]))

    def forward(self, *inputs):
        """Each asked potion's f in HP (inputs as INPUTS)."""
        *run, asked = inputs
        at = spec.INPUTS.index("state")
        run[at] = run[at] * self.seen
        z = self.encoded(*run)
        x = torch.cat([z[:, None, :].expand(-1, asked.shape[1], -1), self.bags["potions"](asked)], 2)
        return nn.functional.softplus(self.head(x)[..., 0]) * SCALE


def loss(f, b: Batch):
    """Per sample, the squared error of f over the potions whose saving is known, in SCALE HP."""
    return (((f - b.saved) / SCALE) ** 2 * b.known).sum(1)
