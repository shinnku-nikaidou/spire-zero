from v import encode


def point(col, row, kind, *nxt, quest=None):
    p = {"col": col, "row": row, "type": kind, "next": [{"col": c, "row": r} for c, r in nxt]}
    return p | ({"quest": quest} if quest else {})


# Start (0,0) -> (0,1) Monster or (1,1) Elite; both -> (0,2) RestSite -> (0,3) Boss; (2,1) leads nowhere from here.
MAP = [
    point(0, 0, "Ancient", (0, 1), (1, 1)),
    point(0, 1, "Monster", (0, 2)),
    point(1, 1, "Elite", (0, 2), quest=True),
    point(2, 1, "Shop", (0, 2)),
    point(0, 2, "RestSite", (0, 3)),
    point(0, 3, "Boss"),
]


def view(visited=((0, 0),), **over):
    v = {
        "character": "SILENT",
        "ascension": 3,
        "act": 0,
        "act_id": "OVERGROWTH",
        "floor": 1,
        "hp": 50,
        "max_hp": 70,
        "gold": 99,
        "deck": [
            {"id": "STRIKE", "up": 5, "enchant": {"id": "SHARP", "amount": 2}},
            {"id": "NEUTRALIZE", "affliction": {"id": "HEX"}},
        ],
        "relics": [{"id": "RING", "counter": 3}, {"id": "VASE", "used_up": True}],
        "potions": ["FIRE", None, None],
        "map": {"points": MAP, "visited": [{"col": c, "row": r} for c, r in visited], "boss": "B", "ancient": "NEOW"},
    }
    return v | over


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
