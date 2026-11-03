import game_ids


def test_each_table_starts_with_unknown_and_holds_each_id_once_in_order():
    for tables in (game_ids.combat_tables(), game_ids.macro_tables()):
        for ids in tables.values():
            assert ids[0] == "<unk>" and ids[1:] == sorted(set(ids[1:]))


def test_the_tables_hold_every_character_and_what_the_nets_read():
    combat, macro = game_ids.combat_tables(), game_ids.macro_tables()
    for character in ("IRONCLAD", "SILENT", "REGENT", "NECROBINDER", "DEFECT"):
        assert f"character:{character}" in macro["context"]
        assert f"STRIKE_{character}" in combat["cards"] and f"STRIKE_{character}" in macro["cards"]
    assert {i.split(":")[0] for i in combat["context"][1:]} == {"encounter", "character"}  # it says the room and the act
    assert "OSTY" in combat["allies"] and "PLASMA_ORB" in combat["orbs"] and "LIZARD_TAIL:used" in combat["relics"]
    assert "Elite+0:quest" in macro["map"] and "Boss+20" in macro["map"]


def test_no_table_holds_the_games_test_models():
    for tables in (game_ids.combat_tables(), game_ids.macro_tables()):
        assert not [i for ids in tables.values() for i in ids if "MOCK" in i]
