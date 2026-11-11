"""V's inputs: the token tables, the run's numbers and the ONNX graph's inputs by name.

What a run is read as is not here: the simulator's code turns a run into its tokens and numbers (RunNet.Tokens,
RunNet.State), in play and for the trainer alike (common.Encoder), so that a run is read one way only. v/net.py's Batch
looks the tokens up in the tables and pads them, as RunNet does for one run.

The tokens of a run: `cards`, each [id, upgrade level, modifiers, the modifiers' amounts, [damage, block]]; the bags
`relics`, `potions`, `context` and `map`; `relic_values` and `relic_status`, in the relics' order; and `held`, the cards
the relics keep.
"""

TABLES = ["cards", "modifiers", "relics", "potions", "context", "map", "relic_status"]
BAGS = {"relics": "relic_mask", "potions": "potion_mask", "context": "context_mask", "map": "map_mask"}  # and masks
ACTS = 3  # the run's acts, indexes 0 to 2
# The run's numbers the net reads, by the encoder's names for them (RunNet.StateNames); a net file standardizes them.
STATE = ["floor", "hp", "max_hp", "gold", "ascension", "potion_slots", *(f"auto_win_{a}" for a in range(ACTS))]
# The ONNX graph's inputs, in order (RunNet feeds them by these names).
INPUTS = [
    "cards", "ups", "card_mask", "mods", "mod_mask", "mod_amounts", "numbers",
    *(x for bag in BAGS.items() for x in bag), "state", "relic_values", "relic_status", "held", "held_mask",
]  # fmt: skip
