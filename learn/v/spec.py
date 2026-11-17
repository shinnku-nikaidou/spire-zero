"""V's inputs: the token tables, the run's numbers and the ONNX graph's inputs by name.

What a run is read as is not here: the simulator's code turns a run into its tokens and numbers (RunNet.Tokens,
RunNet.State), in play and for the trainer alike (common.Encoder), so that a run is read one way only. v/net.py's Batch
looks the tokens up in the tables and pads them, as RunNet does for one run.

The tokens of a run: `cards`, each [id, upgrade level, modifiers, the modifiers' amounts, [damage, block, growth]]; the
bags `relics`, `potions`, `context` and `map`; `relic_values` and `relic_status`, in the relics' order; `held`, the cards
the relics keep; and `pending`, only in NoSL's afterstate of an event option whose result its button does not show
(RunNet.PendingOf): the option's `key`, the `cards` it names (as the deck's) with `cards_owned`, the `relics` with
`relic_values`, `relic_status` and `relics_owned`, the `potions` with `potions_owned`, and its `numbers` with
`number_values`.
"""

TABLES = ["cards", "modifiers", "relics", "potions", "context", "map", "relic_status", "pending", "event_vars"]
BAGS = {"relics": "relic_mask", "potions": "potion_mask", "context": "context_mask", "map": "map_mask"}  # and masks
ACTS = 3  # the run's acts, indexes 0 to 2
# The run's numbers the net reads, by the encoder's names for them (RunNet.StateNames); a net file standardizes them.
STATE = ["floor", "hp", "max_hp", "gold", "ascension", "potion_slots", *(f"auto_win_{a}" for a in range(ACTS))]
CARDS = ["cards", "ups", "card_mask", "mods", "mod_mask", "mod_amounts", "numbers"]  # a list of cards' inputs
PEND = "pend_"  # the prefix of an event option's inputs but its key's
# An event option picked and not yet played out: its key, its cards, relics, potions and numbers (RunNet.PendingInputs).
PENDING = [
    "pending", "pending_mask",
    *(PEND + x for x in CARDS), PEND + "cards_owned",
    PEND + "relics", PEND + "relic_mask", PEND + "relic_values", PEND + "relic_status", PEND + "relics_owned",
    PEND + "potions", PEND + "potion_mask", PEND + "potions_owned",
    PEND + "vars", PEND + "var_mask", PEND + "var_values",
]  # fmt: skip
# The ONNX graph's inputs, in order (RunNet feeds them by these names).
INPUTS = [
    *CARDS, *(x for bag in BAGS.items() for x in bag), "state", "relic_values", "relic_status", "held", "held_mask",
    *PENDING,
]  # fmt: skip
