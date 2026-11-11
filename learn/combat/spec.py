"""The combat net's inputs: the token tables, the sizes the net is built with and the
ONNX graph's inputs by name.

What a state is read as is not here: the simulator's code turns a round's start into its tokens (CombatTokens.Of), in
play and for the trainer alike (common.Encoder), so that a state is read one way only. combat/net.py's Batch looks the
tokens up in the tables and pads them, as CombatNet does for one state.

The tokens of a state: `cards`, each [id, upgrade level, pile, modifiers, cost, the modifiers' amounts, damage, its
NUMBERS]; `powers`, each [id, amount, stacks, the card it keeps or stole (or null), moved number]; `player`, eight
numbers; `enemies`, each its `id`, `scalars`, `powers` and `slots` of intents ([type, damage a hit, total]), those now
and then those of its last LAGS rounds; the bags `relics`, `potions` and `context`; `relic_values` and `relic_status`,
in the relics' order; `orbs` and `orb_values`; `allies`, each its `id`, `scalars` and `powers`; and `extra`, four numbers.
"""

LAGS = 3  # the earlier rounds of each enemy's intents the net reads (the encoder's lags: CombatTokens.Lags)
SLOTS = 1 + LAGS
MAX_ORBS = 10  # the places of the orb queue the net tells apart: later ones read as the last
PILES = ["hand", "draw", "discard", "exhaust"]
NUMBERS = 5  # a card's numbers beyond its cost and damage
TABLES = ["cards", "modifiers", "powers", "enemies", "intents", "relics", "potions", "context", "orbs", "allies", "relic_status"]
BAGS = {"relics": "relic_mask", "potions": "potion_mask", "context": "context_mask"}
# The ONNX graph's inputs, in order: CombatNet feeds them by these names.
INPUTS = [
    "cards",
    "ups",
    "piles",
    "mods",
    "mod_mask",
    "powers",
    "power_amounts",
    "power_mask",
    "player",
    "enemies",
    "enemy_scalars",
    "enemy_mask",
    "enemy_powers",
    "enemy_power_amounts",
    "enemy_power_mask",
    "intents",
    "intent_values",
    "intent_mask",
    *(x for bag in BAGS.items() for x in bag),
    "power_stacks",
    "power_cards",
    "power_card_mask",
    "enemy_power_stacks",
    "enemy_power_cards",
    "enemy_power_card_mask",
    "orbs",
    "orb_mask",
    "allies",
    "ally_scalars",
    "ally_mask",
    "ally_powers",
    "ally_power_amounts",
    "ally_power_mask",
    "extra",
    "costs",
    "mod_amounts",
    "relic_values",
    "relic_status",
    "damages",
    "orb_values",
    "numbers",
    "power_details",
    "enemy_power_details",
]
