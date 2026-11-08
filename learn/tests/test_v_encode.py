from conftest import make_view as view

import game_ids
from v import encode


def test_the_map_is_every_point_a_path_still_leads_to_with_its_floors_ahead():
    assert encode.tokens(view())["map"] == ["Monster+1", "Elite+1:quest", "RestSite+2", "Boss+3"]
    assert encode.tokens(view(visited=((0, 0), (1, 1))))["map"] == ["RestSite+1", "Boss+2"]


def test_a_map_move_reads_the_point_entered_and_only_then():
    after = encode.entered(view(), "map:1,1")
    assert encode.tokens(after)["map"] == ["Elite+0:quest", "RestSite+1", "Boss+2"]
    assert encode.tokens(view(visited=((0, 0), (1, 1))))["map"] == ["RestSite+1", "Boss+2"]


def test_before_any_visit_the_map_is_seen_from_the_start():
    assert encode.tokens(view(visited=()))["map"] == encode.tokens(view())["map"]


def test_at_the_boss_nothing_is_reachable():
    assert encode.tokens(view(visited=((0, 0), (0, 1), (0, 2), (0, 3))))["map"] == []


def test_cards_are_ids_and_their_modifiers_amounts_and_relics_potions_and_context_ids_only():
    t = encode.tokens(view())
    assert t["cards"] == [
        ("STRIKE", encode.MAX_UP, ["enchant:SHARP"], [0.2], (0.0, 0.0)),
        ("NEUTRALIZE", 0, ["affliction:HEX"], [0.1], (0.0, 0.0)),
    ]
    assert t["relics"] == ["RING", "VASE:used"]
    assert t["potions"] == ["FIRE"]
    assert t["context"] == ["character:SILENT", "act:OVERGROWTH", "boss:B", "ancient:NEOW"]


def test_relics_read_their_counters_statuses_and_the_cards_they_keep_as_the_combat_net_reads_them():
    v = view()
    v["relics"] = [
        {"id": "RING", "counter": 25, "status": "Disabled"},
        {"id": "VASE", "used_up": True, "melted": True},
        {"id": "PAELS_TOOTH", "counter": 0, "cards": ["STRIKE", "DASH"]},
        {"id": "PUZZLE", "spent": True},
    ]
    t = encode.tokens(v)
    assert t["relics"] == ["RING", "VASE:used", "PAELS_TOOTH", "PUZZLE"]
    assert t["relic_values"] == [[2.5, 1.0, 0.0], [0.0, 0.0, 1.0], [0.0, 1.0, 0.0], [0.0, 0.0, 0.0]]
    assert t["relic_status"] == ["Disabled", "Normal", "Normal", "Spent"]
    assert t["held"] == ["STRIKE", "DASH"]


def test_a_card_reads_its_keywords_and_what_tinker_time_made_of_it():
    v = view()
    v["deck"] = [{"id": "MAD_SCIENCE", "keywords": ["Ethereal"], "tinker": {"type": "Skill", "rider": "Wisdom"}}]
    [card] = encode.tokens(v)["cards"]
    assert card == ("MAD_SCIENCE", 0, ["keyword:Ethereal", "tinker:Skill", "rider:Wisdom"], [], (0.0, 0.0))
    assert all(m in game_ids.macro_tables()["modifiers"] for m in card[2])


def test_a_card_reads_the_numbers_a_run_grows_on_it():
    v = view()
    v["deck"] = [
        {"id": "DEFEND_SILENT", "enchant": {"id": "GOOPY", "amount": 5}, "keywords": ["Exhaust"], "vars": {"Block": 5}},
        {"id": "THE_SCYTHE", "vars": {"Damage": 23, "Increase": 5}},
        {"id": "GENETIC_ALGORITHM", "up": 1, "vars": {"Block": 260}},
        {"id": "FETCH", "vars": {"OstyDamage": 6}},
    ]
    goopy, scythe, genetic, fetch = encode.tokens(v)["cards"]
    assert goopy == ("DEFEND_SILENT", 0, ["enchant:GOOPY", "keyword:Exhaust"], [0.5], (0.0, 0.25))
    assert scythe[3:] == ([], (1.15, 0.0))
    assert genetic[4] == (0.0, encode.CAP)
    assert fetch[4] == (0.3, 0.0)


def test_the_state_is_the_raw_numbers_in_their_order():
    assert dict(zip(encode.STATE, encode.state(view()), strict=True)) == {
        "floor": 1,
        "hp": 50,
        "max_hp": 70,
        "gold": 99,
        "ascension": 3,
        "potion_slots": 3,
        "auto_win_0": 0,
        "auto_win_1": 0,
        "auto_win_2": 0,
    }
    # The game's rule: the acts whose plain monster fights are won at their start.
    assert encode.state(view() | {"auto_win": [0, 1]})[-3:] == [1.0, 1.0, 0.0]
