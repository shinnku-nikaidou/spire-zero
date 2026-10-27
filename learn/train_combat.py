"""Train the combat net (docs: learning §1), the combat search's leaf: from the start of a round, how the combat ends.

The decision logs (`run --log`) carry the observation on every combat decision; the run records (`run --records`)
carry how each combat ended, joined by run and by the combats' order. A sample is the first decision of a round, the
state a search leaf stands in, with the public record of the enemies' moves before it (Harness's history, rebuilt from
the earlier rounds' observations as Dispatcher builds it). The net reads the cards in each pile, the player, each
enemy with its current intents and those of its last rounds, the relics, the potions and the encounter; its head gives
the chance of each ending, a death or the HP share left in one of the buckets, and the potions left after a win. A
fifth of the runs (by run id; --fold picks which) is held out, for early stopping and the report. The output is a net
file for `--combat-net`, in the weights history by default (docs: learning §6): the net as ONNX, with what the combat
side needs to feed it, and test states it checks on load.

    uv run python train_combat.py --logs a.jsonl,b.jsonl --records a.jsonl,b.jsonl --name c1
"""

import argparse
import base64
import io
import json
import math
import random
import re
import warnings

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402
from torch import nn  # noqa: E402

from train_value import MAX_UP, padded, read_lines  # noqa: E402
from weights import out_path  # noqa: E402

PILES = ["hand", "draw", "discard", "exhaust"]
TABLES = ["cards", "modifiers", "powers", "enemies", "intents", "relics", "potions", "context"]
BAGS = ["relics", "potions", "context"]
# The ONNX graph's inputs, in order (the combat side feeds them by these names).
INPUTS = ["cards", "ups", "costs", "piles", "mods", "mod_mask", "powers", "power_amounts", "power_mask", "player",
          "enemies", "enemy_scalars", "enemy_mask", "enemy_powers", "enemy_power_amounts", "enemy_power_mask",
          "intents", "intent_values", "intent_mask", "relics", "relic_mask", "potions", "potion_mask", "context",
          "context_mask"]
# LeafValue's default weights: what a combat's end is worth, for the report.
WIN_BASE, PER_HP, PER_POTION = 0.2, 0.6, 0.02
# Every scaled number the net reads is capped at ±CAP: a few states show huge ones (an erupting WATERFALL_GIANT's HP).
CAP = 10.0
HEAD = re.compile(r'\{"run":"(\w+)","decision_id":\d+,"env":"\w+","surface":"(\w+)"')


def combats(paths: str):
    """Per run, its combats in order, each the list of its combat decisions. A combat closes at the next macro
    decision, or when the round goes back, as Dispatcher's history does."""
    runs = {}
    for path in paths.split(","):
        with open(path) as f:
            for line in f:
                m = HEAD.match(line)
                if not m:
                    continue
                run, surface = m.groups()
                blocks = runs.setdefault(run, [[]])
                if surface == "card_choice":
                    continue
                if surface != "combat":
                    if blocks[-1]:
                        blocks.append([])
                    continue
                try:
                    e = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if blocks[-1] and e["obs"]["round"] < blocks[-1][-1]["obs"]["round"]:
                    blocks.append([])
                blocks[-1].append(e)
    return {run: [b for b in blocks if b] for run, blocks in runs.items()}


def witness(history: dict, obs: dict):
    """As Dispatcher.Witness, on a round's first decision: each living enemy's intents, by uid."""
    for enemy in obs["enemies"]:
        if enemy.get("uid") is None or enemy.get("intents") is None or enemy["hp"] <= 0:
            continue
        history.setdefault(enemy["uid"], {"uid": enemy["uid"], "id": enemy.get("id"), "turns": []})["turns"].append(
            {"round": obs["round"], "intents": enemy["intents"]})


def card_token(c: dict, pile: int):
    mods = []
    if c.get("enchant"):
        mods.append("enchant:" + c["enchant"]["id"])
    if c.get("affliction"):
        mods.append("affliction:" + c["affliction"]["id"])
    cost = c.get("cost")
    return (c["id"], min(c.get("up") or 0, MAX_UP), pile, cost / 3 if cost is not None and cost > 0 else 0.0, mods)


def cap(x: float) -> float:
    return max(-CAP, min(CAP, x))


def intent_token(i: dict):
    """An intent: its type, its damage per hit over 10 and its total damage over 20 (0 for no attack)."""
    damage = i.get("damage") or 0
    hits = i["hits"] if i.get("hits") is not None else 1
    return (i["type"], cap(damage / 10), cap(damage * hits / 20))


def power_tokens(powers: list):
    return [(p["id"], cap(p["amount"] / 10)) for p in powers]


def tokens(obs: dict, history: list, lags: int, rounds: bool):
    """The net's tokens of a round's start (the combat side reads it the same way). History is the record of the
    rounds before this one; an enemy's slots are its intents now, then those of its last rounds, most recent first."""
    cards = [card_token(c, p) for p, pile in enumerate(PILES) for c in obs[pile]]
    pl = obs["player"]
    max_hp = max(1, pl["max_hp"])
    player = [cap(x) for x in (pl["hp"] / max_hp, pl["hp"] / 100, pl["max_hp"] / 100, pl["block"] / 20, pl["energy"] / 3,
                               pl["max_energy"] / 3, pl["stars"] / 5, obs["round"] / 10 if rounds else 0.0)]
    past = {h["uid"]: h["turns"] for h in history}
    enemies = []
    for e in obs["enemies"]:
        if e["hp"] <= 0:
            continue
        turns = past.get(e.get("uid"), [])
        slots = [e.get("intents") or []] + [t["intents"] for t in reversed(turns[-lags:])] if lags else [e.get("intents") or []]
        enemy_max = max(1, e["max_hp"])
        enemies.append({
            "id": e.get("id") or "<none>",
            "scalars": [cap(x) for x in (e["hp"] / enemy_max, e["hp"] / 100, e["max_hp"] / 100, e["block"] / 20,
                                         len(turns) / 10 if rounds else 0.0)],
            "powers": power_tokens(e["powers"]),
            "slots": [[intent_token(i) for i in s] for s in slots],
        })
    relics = [r["id"] + (":used" if r.get("used_up") else "") for r in obs["relics"]]
    potions = [p for p in obs["potions"] if p is not None]
    context = ["character:" + pl["character"], "encounter:" + (obs.get("encounter") or "<none>"),
               "room:" + (obs.get("room") or "<none>"), f"act:{obs['act']}"]
    return {"cards": cards, "powers": power_tokens(pl["powers"]), "player": player,
            "enemies": enemies, "relics": relics, "potions": potions, "context": context}


def outcome(end: dict, buckets: int):
    """The class of a combat's end: 0 for a death, else 1 + the bucket of the HP share left."""
    if end["died"]:
        return 0
    return 1 + min(buckets - 1, int(end["hp"] / max(1, end["max_hp"]) * buckets))


def end_value(end: dict):
    """What LeafValue makes of a combat's end."""
    return 0.0 if end["died"] else WIN_BASE + PER_HP * end["hp"] / max(1, end["max_hp"]) + PER_POTION * end["potions"]


def leaf0(obs: dict):
    """LeafValue.Of with its default weights on a state in progress: the hand-made leaf, for the report."""
    pl = obs["player"]
    max_hp = max(1, pl["max_hp"])

    def amount(powers, pid):
        return next((p["amount"] for p in powers if p["id"] == pid), 0)

    enemies = []
    for e in obs["enemies"]:
        if e["hp"] <= 0:
            continue
        poison = amount(e["powers"], "POISON_POWER")
        hits = sum((i.get("damage") or 0) * (i.get("hits") or 1) for i in e.get("intents") or [])
        enemies.append((max(0, e["hp"] + e["block"] - min(e["hp"], 0.8 * poison * (poison + 1) / 2)), max(3, hits)))
    dps = max(1, 10 + 2 * amount(pl["powers"], "STRENGTH_POWER"))
    turns = taken = 0.0
    for hp, hits in sorted(enemies, key=lambda x: -x[1] / max(1, x[0])):
        turns += hp / dps
        taken += hits * turns
    share = (pl["hp"] - 0.5 * max(0, taken - pl["block"])) / max_hp
    win = WIN_BASE + PER_HP * share if share >= 0 else WIN_BASE * math.exp(PER_HP * share / WIN_BASE)
    return win + PER_POTION * sum(1 for p in obs["potions"] if p is not None)


def vocab(samples, min_count: int):
    """Per table, the ids seen at least min_count times in these samples; entry 0 reads every other id."""
    counts = {t: {} for t in TABLES}

    def count(t, i):
        counts[t][i] = counts[t].get(i, 0) + 1

    for s in samples:
        tok = s["tokens"]
        for card_id, _, _, _, mods in tok["cards"]:
            count("cards", card_id)
            for m in mods:
                count("modifiers", m)
        for pid, _ in tok["powers"]:
            count("powers", pid)
        for e in tok["enemies"]:
            count("enemies", e["id"])
            for pid, _ in e["powers"]:
                count("powers", pid)
            for slot in e["slots"]:
                for kind, _, _ in slot:
                    count("intents", kind)
        for t in BAGS:
            for i in tok[t]:
                count(t, i)
    return {t: ["<unk>"] + sorted(i for i, n in c.items() if n >= min_count) for t, c in counts.items()}


class Batch:
    """Samples as tensors, padded with their masks, and their targets."""

    def __init__(self, samples, index, slots, device):
        n = len(samples)
        toks = [s["tokens"] for s in samples]
        width = max(1, max(len(t["cards"]) for t in toks))
        cards, ups, costs = torch.zeros(n, width, dtype=torch.long), torch.zeros(n, width), torch.zeros(n, width)
        piles = torch.zeros(n, width, len(PILES))
        mods, mod_mask = torch.zeros(n, width, 2, dtype=torch.long), torch.zeros(n, width, 2)
        for b, t in enumerate(toks):
            for k, (card_id, up, pile, cost, ms) in enumerate(t["cards"]):
                cards[b, k], ups[b, k], costs[b, k], piles[b, k, pile] = index["cards"].get(card_id, 0), up, cost, 1
                for j, m in enumerate(ms[:2]):
                    mods[b, k, j], mod_mask[b, k, j] = index["modifiers"].get(m, 0), 1
        powers, power_amounts, power_mask = self.powers([t["powers"] for t in toks], index)
        player = torch.tensor([t["player"] for t in toks])
        e_n = max(1, max(len(t["enemies"]) for t in toks))
        q_n = max(1, max((len(e["powers"]) for t in toks for e in t["enemies"]), default=0))
        i_n = max(1, max((len(s) for t in toks for e in t["enemies"] for s in e["slots"]), default=0))
        enemies, enemy_mask = torch.zeros(n, e_n, dtype=torch.long), torch.zeros(n, e_n)
        enemy_scalars = torch.zeros(n, e_n, 5)
        enemy_powers, enemy_power_amounts = torch.zeros(n, e_n, q_n, dtype=torch.long), torch.zeros(n, e_n, q_n)
        enemy_power_mask = torch.zeros(n, e_n, q_n)
        intents, intent_values = torch.zeros(n, e_n, slots, i_n, dtype=torch.long), torch.zeros(n, e_n, slots, i_n, 2)
        intent_mask = torch.zeros(n, e_n, slots, i_n)
        for b, t in enumerate(toks):
            for k, e in enumerate(t["enemies"]):
                enemies[b, k], enemy_mask[b, k] = index["enemies"].get(e["id"], 0), 1
                enemy_scalars[b, k] = torch.tensor(e["scalars"])
                for j, (pid, amount) in enumerate(e["powers"]):
                    enemy_powers[b, k, j], enemy_power_amounts[b, k, j], enemy_power_mask[b, k, j] = index["powers"].get(pid, 0), amount, 1
                for s, slot in enumerate(e["slots"][:slots]):
                    for j, (kind, damage, total) in enumerate(slot):
                        intents[b, k, s, j], intent_mask[b, k, s, j] = index["intents"].get(kind, 0), 1
                        intent_values[b, k, s, j] = torch.tensor([damage, total])
        self.tensors = [cards, ups, costs, piles, mods, mod_mask, powers, power_amounts, power_mask, player,
                        enemies, enemy_scalars, enemy_mask, enemy_powers, enemy_power_amounts, enemy_power_mask,
                        intents, intent_values, intent_mask]
        self.tensors = [x.to(device) for x in self.tensors]
        for table in BAGS:
            self.tensors.extend(padded([[index[table].get(i, 0) for i in t[table]] for t in toks], device))
        self.target = torch.tensor([s["class"] for s in samples], dtype=torch.long, device=device)
        self.potions = torch.tensor([s["end"]["potions"] for s in samples], dtype=torch.float, device=device)

    @staticmethod
    def powers(rows, index):
        width = max(1, max(len(r) for r in rows))
        ids, amounts, mask = torch.zeros(len(rows), width, dtype=torch.long), torch.zeros(len(rows), width), torch.zeros(len(rows), width)
        for b, r in enumerate(rows):
            for k, (pid, amount) in enumerate(r):
                ids[b, k], amounts[b, k], mask[b, k] = index["powers"].get(pid, 0), amount, 1
        return ids, amounts, mask

    def inputs(self):
        """The net's inputs, in the order of INPUTS."""
        return tuple(self.tensors)


def mlp(*sizes):
    layers = []
    for a, b in zip(sizes, sizes[1:]):
        layers += [nn.Linear(a, b), nn.ReLU()]
    return nn.Sequential(*layers)


class Net(nn.Module):
    def __init__(self, sizes, slots, buckets, n_player=8, dim=32, hidden=64, body=256, sum_scale=1 / 8):
        super().__init__()
        self.sum_scale = sum_scale
        self.cards = nn.Embedding(sizes["cards"], dim)
        self.card_up = nn.Embedding(sizes["cards"], dim)
        self.modifiers = nn.Embedding(sizes["modifiers"], dim)
        self.pile = nn.Parameter(torch.randn(len(PILES), dim) * 0.1)
        self.cost = nn.Parameter(torch.randn(dim) * 0.1)
        self.powers = nn.Embedding(sizes["powers"], dim)
        self.power_amount = nn.Embedding(sizes["powers"], dim)
        self.enemies = nn.Embedding(sizes["enemies"], dim)
        self.intents = nn.Embedding(sizes["intents"], dim)
        self.intent_damage = nn.Embedding(sizes["intents"], dim)
        self.intent_total = nn.Embedding(sizes["intents"], dim)
        self.slot = nn.Parameter(torch.randn(slots, dim) * 0.1)
        self.relics = nn.Embedding(sizes["relics"], dim)
        self.potions = nn.Embedding(sizes["potions"], dim)
        self.context = nn.Embedding(sizes["context"], dim)
        for m in self.modules():
            if isinstance(m, nn.Embedding):
                nn.init.normal_(m.weight, std=0.1)
        self.card_phi = mlp(dim, hidden, hidden)
        self.power_phi = mlp(dim, hidden)
        self.enemy_scalars = nn.Linear(5, dim)
        self.enemy_phi = mlp(dim + hidden + slots * dim, 2 * hidden, 2 * hidden)
        self.body = mlp(len(PILES) * hidden + n_player + hidden + 2 * hidden + 3 * dim, body, body)
        self.head = nn.Linear(body, 1 + buckets)
        self.potion_head = nn.Linear(body, 1)

    def power_sum(self, ids, amounts, mask):
        x = self.powers(ids) + amounts[..., None] * self.power_amount(ids)
        return (self.power_phi(x) * mask[..., None]).sum(-2)

    def forward(self, cards, ups, costs, piles, mods, mod_mask, powers, power_amounts, power_mask, player,
                enemies, enemy_scalars, enemy_mask, enemy_powers, enemy_power_amounts, enemy_power_mask,
                intents, intent_values, intent_mask, relics, relic_mask, potions, potion_mask, context, context_mask):
        """Per sample, the logits of the ending classes and the potions left after a win (inputs as INPUTS names them)."""
        x = (self.cards(cards) + ups[..., None] * self.card_up(cards) + costs[..., None] * self.cost
             + (self.modifiers(mods) * mod_mask[..., None]).sum(2) + piles @ self.pile)
        h = self.card_phi(x)
        per_pile = torch.bmm(piles.transpose(1, 2), h).flatten(1) * self.sum_scale
        i = (self.intents(intents) + intent_values[..., 0:1] * self.intent_damage(intents)
             + intent_values[..., 1:2] * self.intent_total(intents))
        slots = ((i * intent_mask[..., None]).sum(3) + self.slot).flatten(2)
        e = torch.cat([self.enemies(enemies) + self.enemy_scalars(enemy_scalars),
                       self.power_sum(enemy_powers, enemy_power_amounts, enemy_power_mask), slots], 2)
        foes = (self.enemy_phi(e) * enemy_mask[..., None]).sum(1)
        parts = [per_pile, player, self.power_sum(powers, power_amounts, power_mask), foes]
        for table, ids, mask in ((self.relics, relics, relic_mask), (self.potions, potions, potion_mask), (self.context, context, context_mask)):
            parts.append((table(ids) * mask[..., None]).sum(1))
        z = self.body(torch.cat(parts, 1))
        return self.head(z), self.potion_head(z)[:, 0]


class Outcome(nn.Module):
    """The net as ONNX holds it: the chance of each ending, and the potions left after a win."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        logits, potions = self.net(*inputs)
        return torch.softmax(logits, 1), potions


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    axes = {"cards": {0: "batch", 1: "cards"}, "ups": {0: "batch", 1: "cards"}, "costs": {0: "batch", 1: "cards"},
            "piles": {0: "batch", 1: "cards"}, "mods": {0: "batch", 1: "cards"}, "mod_mask": {0: "batch", 1: "cards"},
            "player": {0: "batch"}, "outcome": {0: "batch"}, "potions_left": {0: "batch"}}
    for name in ("powers", "power_amounts", "power_mask"):
        axes[name] = {0: "batch", 1: "powers"}
    for name in ("enemies", "enemy_scalars", "enemy_mask"):
        axes[name] = {0: "batch", 1: "enemies"}
    for name in ("enemy_powers", "enemy_power_amounts", "enemy_power_mask"):
        axes[name] = {0: "batch", 1: "enemies", 2: "enemy_powers"}
    for name in ("intents", "intent_values", "intent_mask"):
        axes[name] = {0: "batch", 1: "enemies", 3: "intents"}
    for t, mask in (("relics", "relic_mask"), ("potions", "potion_mask"), ("context", "context_mask")):
        axes[t] = axes[mask] = {0: "batch", 1: t}
    out = io.BytesIO()
    torch.onnx.export(Outcome(model), example.inputs(), out, dynamo=False, input_names=INPUTS,
                      output_names=["outcome", "potions_left"], dynamic_axes=axes)
    return out.getvalue()


def values(probs: torch.Tensor, potions: torch.Tensor, buckets: int):
    """What LeafValue's weights make of the predicted ending: each bucket at its middle, potions after a win."""
    mids = WIN_BASE + PER_HP * (torch.arange(buckets, device=probs.device) + 0.5) / buckets
    return (probs[:, 1:] * mids).sum(1) + (1 - probs[:, 0]) * PER_POTION * potions


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--logs", required=True, help="decision logs, comma-separated")
    p.add_argument("--records", required=True, help="run records, comma-separated")
    p.add_argument("--name", required=True, help="the net's name, as the champion will show it")
    p.add_argument("--out", help="where the file goes (default: weights/history/NAME.json)")
    p.add_argument("--buckets", type=int, default=20, help="buckets of the HP share left after a win")
    p.add_argument("--lags", type=int, default=3, help="earlier rounds of each enemy's intents the net reads")
    p.add_argument("--no-round", action="store_true", help="hide the round and how many rounds each enemy has shown")
    p.add_argument("--min-count", type=int, default=3, help="ids seen fewer times read as unknown")
    p.add_argument("--epochs", type=int, default=60)
    p.add_argument("--patience", type=int, default=6, help="epochs without a better held-out loss before stopping")
    p.add_argument("--batch", type=int, default=256)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--weight-decay", type=float, default=1e-4)
    p.add_argument("--potion-weight", type=float, default=0.1, help="weight of the potions' squared error in the loss")
    p.add_argument("--fold", type=int, default=0, choices=range(5), help="which fifth of the runs (by run id) is held out")
    p.add_argument("--seed", type=int, default=0)
    args = p.parse_args()
    out_file = out_path(args.name, args.out)
    torch.manual_seed(args.seed)
    random.seed(args.seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"
    rounds = not args.no_round

    runs = {r["run_id"]: r for r in read_lines(args.records) if r.get("run_id") and not r.get("failure")}
    samples, skipped = [], 0
    for run, blocks in combats(args.logs).items():
        r = runs.get(run)
        if r is None:
            continue
        fights = r["combats"]
        if len(blocks) != len(fights) or any(b[0]["obs"]["encounter"] != f["encounter"] for b, f in zip(blocks, fights)):
            skipped += 1
            continue
        for block, fight in zip(blocks, fights):
            if "end" not in fight:
                continue
            history, last = {}, -1
            for e in block:
                obs = e["obs"]
                if obs["round"] == last:
                    continue
                last = obs["round"]
                past = [{"uid": h["uid"], "id": h["id"], "turns": list(h["turns"])} for h in history.values()]
                samples.append({"run": run, "obs": obs, "history": past, "tokens": tokens(obs, past, args.lags, rounds),
                                "end": fight["end"], "class": outcome(fight["end"], args.buckets), "value": end_value(fight["end"])})
                witness(history, obs)
    def held_out(run_id: str) -> bool:
        return int(run_id, 16) % 5 == args.fold

    train = [s for s in samples if not held_out(s["run"])]
    test = [s for s in samples if held_out(s["run"])]
    if not train or not test:
        raise SystemExit(f"{len(train)} train and {len(test)} held-out samples: too few runs")
    print(f"{len(samples)} round starts from {len({s['run'] for s in samples})} runs; {skipped} runs skipped, their combats not matching the record", flush=True)

    tables = vocab(train, args.min_count)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    slots = 1 + args.lags

    def batches(part, shuffle):
        order = list(range(len(part)))
        if shuffle:
            random.shuffle(order)
        for i in range(0, len(order), args.batch):
            yield Batch([part[k] for k in order[i:i + args.batch]], index, slots, device)

    def loss_of(logits, potions, b: Batch):
        won = (b.target > 0).float()
        return nn.functional.cross_entropy(logits, b.target, reduction="none") + args.potion_weight * won * (potions - b.potions) ** 2

    test_batches = list(batches(test, False))

    def evaluate(model):
        """The held-out loss, the cross-entropy of each held-out sample, and the net's value of each."""
        total, ces, rows = 0.0, [], []
        with torch.no_grad():
            for b in test_batches:
                logits, potions = model(*b.inputs())
                total += float(loss_of(logits, potions, b).sum())
                ces.append(nn.functional.cross_entropy(logits, b.target, reduction="none"))
                rows.append(values(torch.softmax(logits, 1), potions, args.buckets))
        return total / len(test), torch.cat(ces).double().cpu(), torch.cat(rows).double().cpu()

    model = Net({t: len(ids) for t, ids in tables.items()}, slots, args.buckets).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    best, best_state, stale = math.inf, None, 0
    for epoch in range(args.epochs):
        model.train()
        for b in batches(train, True):
            loss = loss_of(*model(*b.inputs()), b).mean()
            opt.zero_grad()
            loss.backward()
            opt.step()
        model.eval()
        held, _, _ = evaluate(model)
        print(f"epoch {epoch}: held-out loss {held:.4f}", flush=True)
        if held < best - 1e-4:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= args.patience:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: held-out loss against a prior that knows only the training endings, and how well the net's value
    # and LeafValue's predict what the combat's end was worth.
    held, ces, predicted = evaluate(model)
    ce = float(ces.mean())
    # By room, since a monster's move order should matter most in the long fights of elites and bosses.
    rooms = [s["obs"].get("room") or "<none>" for s in test]
    ce_by_room = {r: float(ces[[k for k, x in enumerate(rooms) if x == r]].mean()) for r in sorted(set(rooms))}
    counts = torch.bincount(torch.tensor([s["class"] for s in train]), minlength=1 + args.buckets).double() + 0.5
    prior = counts / counts.sum()
    ce_prior = -float(sum(math.log(prior[s["class"]]) for s in test)) / len(test)
    actual = torch.tensor([s["value"] for s in test], dtype=torch.float64)
    hand = torch.tensor([leaf0(s["obs"]) for s in test], dtype=torch.float64)
    died = torch.tensor([s["class"] == 0 for s in test], dtype=torch.float64)

    def corr(a, b):
        a, b = a - a.mean(), b - b.mean()
        return float((a * b).sum() / (a.norm() * b.norm()).clamp_min(1e-12))

    report = {
        "runs": len({s["run"] for s in samples}),
        "held_out_runs": len({s["run"] for s in test}),
        "samples": len(samples),
        "held_out_samples": len(test),
        "deaths": int(sum(1 for s in samples if s["class"] == 0)),
        "fold": args.fold,
        "lags": args.lags,
        "round": rounds,
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out_loss": held,
        "held_out_ce": ce,
        "held_out_ce_prior": ce_prior,
        "held_out_ce_by_room": ce_by_room,
        "held_out_samples_by_room": {r: rooms.count(r) for r in sorted(set(rooms))},
        "value_mse": float(((predicted - actual) ** 2).mean()),
        "value_corr": corr(predicted, actual),
        "value_mse_leaf0": float(((hand - actual) ** 2).mean()),
        "value_corr_leaf0": corr(hand, actual),
        "death_corr": corr(1 - predicted, died),
    }
    print(json.dumps(report))

    # Test states for the combat side's check on load: a few held-out round starts, as the CPU computes them.
    model = model.cpu()
    picks = random.Random(args.seed).sample(test, min(8, len(test)))
    with torch.no_grad():
        b = Batch(picks, index, slots, "cpu")
        logits, potions = model(*b.inputs())
        probs = torch.softmax(logits, 1)
    tests = [{"obs": s["obs"], "history": s["history"], "outcome": [float(v) for v in probs[k]], "potions": float(potions[k])}
             for k, s in enumerate(picks)]

    out = {"name": args.name, "buckets": args.buckets, "lags": args.lags, "round": rounds, "vocab": tables,
           "fit": report, "onnx": base64.b64encode(onnx_bytes(model, b)).decode(), "tests": tests}
    with open(out_file, "w") as f:
        json.dump(out, f)
        f.write("\n")


if __name__ == "__main__":
    main()
