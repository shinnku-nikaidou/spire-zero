from conftest import make_view as view

from v import encode


def test_the_map_is_every_point_a_path_still_leads_to_with_its_floors_ahead():
    assert encode.tokens(view())["map"] == ["Monster+1", "Elite+1:quest", "RestSite+2", "Boss+3"]
    assert encode.tokens(view(visited=((0, 0), (1, 1))))["map"] == ["RestSite+1", "Boss+2"]


def test_before_any_visit_the_map_is_seen_from_the_start():
    assert encode.tokens(view(visited=()))["map"] == encode.tokens(view())["map"]


def test_at_the_boss_nothing_is_reachable():
    assert encode.tokens(view(visited=((0, 0), (0, 1), (0, 2), (0, 3))))["map"] == []


def test_cards_relics_potions_and_context_are_ids_only():
    t = encode.tokens(view())
    assert t["cards"] == [("STRIKE", encode.MAX_UP, ["enchant:SHARP"]), ("NEUTRALIZE", 0, ["affliction:HEX"])]
    assert t["relics"] == ["RING", "VASE:used"]
    assert t["potions"] == ["FIRE"]
    assert t["context"] == ["character:SILENT", "act:OVERGROWTH", "boss:B", "ancient:NEOW"]


def test_the_state_is_the_raw_numbers_in_their_order():
    assert dict(zip(encode.STATE, encode.state(view()), strict=True)) == {
        "floor": 1,
        "hp": 50,
        "max_hp": 70,
        "gold": 99,
        "ascension": 3,
        "potion_slots": 3,
    }


def test_the_route_is_the_players_point_then_the_reachable_ones_each_with_the_nodes_it_leads_to():
    assert encode.tokens(view())["route"] == [
        ("Ancient", 0, False, [1, 2]),
        ("Monster", 1, False, [3]),
        ("Elite", 1, True, [3]),
        ("RestSite", 2, False, [4]),
        ("Boss", 3, False, []),
    ]
    assert encode.tokens(view(visited=((0, 0), (1, 1))))["route"] == [
        ("Elite", 0, True, [1]),
        ("RestSite", 1, False, [2]),
        ("Boss", 2, False, []),
    ]
    assert encode.tokens(view(visited=((0, 0), (0, 1), (0, 2), (0, 3))))["route"] == [("Boss", 0, False, [])]
