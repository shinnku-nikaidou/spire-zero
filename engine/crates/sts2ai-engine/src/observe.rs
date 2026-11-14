//! What the player sees of a combat, read off the combat's objects: `Observe` of src/Sts2Ai.GameBridge/Observe.cs,
//! member by member in its order, so that reading the two side by side is checking this one. Every value of the game
//! is asked of the game's own translated code (`gen`); a private field Observe.cs reads by reflection is read from the
//! generated state.
//!
//! The translated getters take the combat mutably and fill the game's lazy caches as the C# ones do (a card's cost
//! object, its keyword set). Nothing else is written: what Observe.cs makes and lets the collector take (an upgrade
//! preview, a fresh copy of a card) is made here in a copy of the combat that is dropped.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::manual as m;
use crate::protocol::{
    CardView, CombatObservation, CreatureView, IntentView, ModifierView, OrbView, PlayerView, PowerView, RelicView, RunChange, TinkerView,
    sort_by_key,
};
use crate::rt::{self, Combat, Dec, Obj, Str};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

/// C#: `model.Id.Entry`.
fn entry(model: AbstractModel, c: &mut Combat) -> Str {
    g::models::abstract_model::id(model, c).entry
}

/// C#: `value.ToString()` of an enum.
fn name<T: rt::ToS>(value: T, c: &mut Combat) -> Str {
    rt::to_s(value, c)
}

/// C#: `foreach (var (name, v) in vars)`. A DynamicVarSet enumerates its dictionary.
fn enumerate(vars: DynamicVarSet, c: &mut Combat) -> Vec<(Str, DynamicVar)> {
    let dict = &DynamicVarSetData::of(vars, c)._vars;
    dict.keys().into_iter().zip(dict.values()).collect()
}

/// C#: `v is CalculatedVar`.
fn is_calculated(v: DynamicVar, c: &mut Combat) -> bool {
    rt::is(v, Kind::CalculatedVar, c)
}

/// C#: Observe.Run with previews false, as far as a combat changes the run: the members `sz_world_run` gives
/// (EngineWorld.RunChange).
pub fn run(player: Player, c: &mut Combat) -> RunChange {
    let me = g::entities_players::player::creature(player, c);
    RunChange {
        hp: g::entities_creatures::creature::current_hp(me, c),
        max_hp: g::entities_creatures::creature::max_hp(me, c),
        gold: g::entities_players::player::gold(player, c),
        deck: {
            let deck = g::entities_players::player::deck(player, c);
            let mut cards: Vec<CardView> = g::entities_cards::card_pile::cards(deck, c)
                .into_iter()
                .map(|x| card(x, false, c))
                .collect();
            sort_by_key(&mut cards);
            cards
        },
        relics: g::entities_players::player::relics(player, c)
            .into_iter()
            .map(|r| relic(r, c))
            .collect(),
        potions: potions(player, c),
    }
}

/// C#: `[.. player.PotionSlots.Select(p => p?.Id.Entry)]`.
fn potions(player: Player, c: &mut Combat) -> Vec<Option<Str>> {
    let slots = g::entities_players::player::potion_slots(player, c);
    slots
        .into_iter()
        .map(|p| if rt::is_null(&p) { None } else { Some(entry(p, c)) })
        .collect()
}

/// C#: Observe.Combat.
pub fn combat(st: CombatState, player: Player, c: &mut Combat) -> CombatObservation {
    seen(st, player, c).0
}

/// The observation, and beside it the draw pile's cards as their card keys in the pile's own order: the observation
/// lists the pile sorted, and the hidden key (keys.rs) tells draw piles apart by their order. The cards are read once
/// for both.
pub fn seen(st: CombatState, player: Player, c: &mut Combat) -> (CombatObservation, Vec<Vec<u8>>) {
    let mut draw_order = Vec::new();
    let pcs = g::entities_players::player::player_combat_state(player, c);
    if rt::is_null(&pcs) {
        panic!("player has no combat state");
    }
    let me = g::entities_players::player::creature(player, c);
    let targets = vec![me];
    let arrows = g::entities_players::player_combat_state::arrows(pcs, c);
    let encounter = g::combat::combat_state::encounter(st, c);
    let run_state = g::combat::combat_state::run_state(st, c);
    let observation = CombatObservation {
        encounter: if rt::is_null(&encounter) { None } else { Some(entry(encounter, c)) },
        room: if rt::is_null(&encounter) {
            None
        } else {
            let room = g::dispatch::encounter_model::room_type(encounter, c);
            Some(name(room, c))
        },
        act: g::dispatch::i_run_state::current_act_index(run_state, c),
        ascension: g::dispatch::i_run_state::ascension_level(run_state, c),
        round: g::combat::combat_state::round_number(st, c),
        player: PlayerView {
            uid: g::entities_creatures::creature::combat_id(me, c),
            character: {
                let character = g::entities_players::player::character(player, c);
                entry(character, c)
            },
            hp: g::entities_creatures::creature::current_hp(me, c),
            max_hp: g::entities_creatures::creature::max_hp(me, c),
            block: g::entities_creatures::creature::block(me, c),
            energy: g::entities_players::player_combat_state::energy(pcs, c),
            max_energy: g::entities_players::player_combat_state::max_energy(pcs, c),
            stars: g::entities_players::player_combat_state::stars(pcs, c),
            gold: g::entities_players::player::gold(player, c),
            powers: powers(me, c),
            orb_slots: {
                let queue = g::entities_players::player_combat_state::orb_queue(pcs, c);
                g::entities_orbs::orb_queue::capacity(queue, c)
            },
            orbs: {
                let queue = g::entities_players::player_combat_state::orb_queue(pcs, c);
                let orbs = g::entities_orbs::orb_queue::orbs(queue, c);
                orbs.into_iter()
                    .map(|o| OrbView {
                        id: entry(o, c),
                        passive: g::dispatch::orb_model::passive_val(o, c),
                        evoke: g::dispatch::orb_model::evoke_val(o, c),
                    })
                    .collect()
            },
            arrows: if arrows.is_empty() {
                None
            } else {
                Some(
                    arrows
                        .iter()
                        .filter(|a| g::entities_fool_arrows::arrow_slot::status(**a, c) == ArrowStatus::Retrieved)
                        .count() as i32,
                )
            },
            arrow_slots: if arrows.is_empty() {
                None
            } else {
                Some(
                    arrows
                        .iter()
                        .filter(|a| g::entities_fool_arrows::arrow_slot::status(**a, c) != ArrowStatus::Destroyed)
                        .count() as i32,
                )
            },
            melodies: melodies(st, player, c),
        },
        hand: {
            let pile = g::entities_players::player_combat_state::hand(pcs, c);
            g::entities_cards::card_pile::cards(pile, c)
                .into_iter()
                .map(|x| card(x, true, c))
                .collect()
        },
        draw: {
            let pile = g::entities_players::player_combat_state::draw_pile(pcs, c);
            let mut cards: Vec<CardView> = g::entities_cards::card_pile::cards(pile, c)
                .into_iter()
                .map(|x| card(x, false, c))
                .collect();
            draw_order = sort_by_key(&mut cards);
            cards
        },
        discard: {
            let pile = g::entities_players::player_combat_state::discard_pile(pcs, c);
            g::entities_cards::card_pile::cards(pile, c)
                .into_iter()
                .map(|x| card(x, false, c))
                .collect()
        },
        exhaust: {
            let pile = g::entities_players::player_combat_state::exhaust_pile(pcs, c);
            g::entities_cards::card_pile::cards(pile, c)
                .into_iter()
                .map(|x| card(x, false, c))
                .collect()
        },
        potions: potions(player, c),
        relics: g::entities_players::player::relics(player, c)
            .into_iter()
            .map(|r| relic(r, c))
            .collect(),
        allies: {
            let allies = g::combat::combat_state::allies(st, c);
            allies.into_iter().filter(|x| *x != me).map(|x| creature(x, &targets, c)).collect()
        },
        enemies: {
            let enemies = g::combat::combat_state::enemies(st, c);
            enemies
                .into_iter()
                .map(|x| CreatureView {
                    arrows: stuck(x, me, c),
                    ..creature(x, &targets, c)
                })
                .collect()
        },
        prompt: None,
    };
    (observation, draw_order)
}

/// C#: Observe.Card. A card as the player sees it; with its uid only if asked (cards in hand).
pub fn card(card: CardModel, with_uid: bool, c: &mut Combat) -> CardView {
    let cost = g::models::card_model::energy_cost(card, c);
    let x = g::entities_cards::card_energy_cost::costs_x(cost, c);
    CardView {
        id: entry(card, c),
        uid: if with_uid {
            let db = g::game_actions_multiplayer::net_combat_card_db::instance(c);
            Some(g::game_actions_multiplayer::net_combat_card_db::get_card_id(db, card, c))
        } else {
            None
        },
        up: {
            let level = g::models::card_model::current_upgrade_level(card, c);
            (level > 0).then_some(level)
        },
        cost: if x {
            None
        } else {
            Some(g::entities_cards::card_energy_cost::get_with_modifiers(cost, CostModifiers::All, c))
        },
        x_cost: x.then_some(true),
        stars: if g::dispatch::card_model::canonical_star_cost(card, c) >= 0 {
            Some(g::models::card_model::get_star_cost_with_modifiers(card, c))
        } else {
            None
        },
        enchant: {
            let e = g::models::card_model::enchantment(card, c);
            if rt::is_null(&e) {
                None
            } else {
                Some(ModifierView {
                    id: entry(e, c),
                    amount: g::models::enchantment_model::amount(e, c),
                    extra: if rt::is(e, Kind::Momentum, c) {
                        let extra = g::dispatch::enchantment_model::enchant_damage_additive(e, Dec::ZERO, ValueProp::Move, c).to_i32();
                        (extra > 0).then_some(extra)
                    } else {
                        None
                    },
                })
            }
        },
        affliction: {
            let a = g::models::card_model::affliction(card, c);
            if rt::is_null(&a) {
                None
            } else {
                Some(ModifierView {
                    id: entry(a, c),
                    amount: g::models::affliction_model::amount(a, c),
                    extra: None,
                })
            }
        },
        tinker: if rt::is(card, Kind::MadScience, c) {
            let type_ = g::models_cards::mad_science::tinker_time_type(card, c);
            let rider = g::models_cards::mad_science::tinker_time_rider(card, c);
            Some(TinkerView {
                type_: name(type_, c),
                rider: name(rider, c),
            })
        } else {
            None
        },
        keywords: {
            let keywords = rt::IntoVec::into_vec(g::models::card_model::keywords(card, c));
            if keywords.is_empty() {
                None
            } else {
                let mut names: Vec<Str> = keywords.into_iter().map(|k| name(k, c)).collect();
                names.sort_by(|a, b| a.compare(*b).cmp(&0));
                Some(names)
            }
        },
        this_turn: this_turn(card, c),
        vars: {
            let vars = vars(card, c);
            (!vars.is_empty()).then_some(vars)
        },
        upgraded: None,
        lasting: if x { None } else { lasting(card, c) },
        stars_lasting: if g::dispatch::card_model::canonical_star_cost(card, c) >= 0 && !g::dispatch::card_model::has_star_cost_x(card, c) {
            stars_lasting(card, c)
        } else {
            None
        },
        replay: {
            let replay = g::models::card_model::get_enchanted_replay_count(card, c);
            (replay > 0).then_some(replay)
        },
        spent: {
            let e = g::models::card_model::enchantment(card, c);
            (!rt::is_null(&e) && g::models::enchantment_model::status(e, c) == EnchantmentStatus::Disabled).then_some(true)
        },
        calc: {
            let state = g::models::card_model::combat_state(card, c);
            if rt::is_null(&state) {
                None
            } else {
                let vars = g::models::card_model::dynamic_vars(card, c);
                let values = g::localization_dynamic_vars::dynamic_var_set::values(vars, c);
                let first = values.into_iter().find(|v| is_calculated(*v, c));
                first.map(|v| g::localization_dynamic_vars::calculated_var::calculate(v, Obj::NULL, c))
            }
        },
        grown: grown(card, c),
    }
}

/// C#: Observe.MelodyTags.
const MELODY_TAGS: [CardTag; 3] = [CardTag::Wah, CardTag::Cho, CardTag::Ki];

/// C#: Observe.Melodies. The melodies the player's card plays of this turn made, as the game counts them. The history
/// is in order, so only this turn's end of it is read.
fn melodies(st: CombatState, player: Player, c: &mut Combat) -> Option<Vec<Str>> {
    let manager = g::combat::combat_manager::instance(c);
    let history = g::combat::combat_manager::history(manager, c);
    let entries = g::combat_history::combat_history::entries(history, c);
    let mut played = 0;
    for e in entries.into_iter().rev() {
        if !g::combat_history::combat_history_entry::happened_this_turn(e, st, c) {
            break;
        }
        if !rt::is(e, Kind::CardPlayFinishedEntry, c) {
            continue;
        }
        let play = g::combat_history_entries::card_play_finished_entry::card_play(e, c);
        if g::entities_cards::card_play::player(play, c) != player {
            continue;
        }
        let played_card = g::entities_cards::card_play::card(play, c);
        let tags = g::dispatch::card_model::tags(played_card, c);
        for (t, tag) in MELODY_TAGS.iter().enumerate() {
            if tags.contains(tag) {
                played |= 1 << t;
            }
        }
    }
    if played == 0 {
        return None;
    }
    Some(
        MELODY_TAGS
            .iter()
            .enumerate()
            .filter(|(t, _)| (played >> t) & 1 == 1)
            .map(|(_, tag)| name(*tag, c))
            .collect(),
    )
}

/// C#: Observe.Stuck. How many of the player's arrows are stuck in the creature.
fn stuck(creature: Creature, me: Creature, c: &mut Combat) -> Option<i32> {
    let mut stuck = 0;
    for power in g::entities_creatures::creature::powers(creature, c) {
        if rt::is(power, Kind::ImpaledPower, c) && g::models::power_model::applier(power, c) == me {
            stuck += g::models_powers::impaled_power::arrows(power, c).len() as i32;
        }
    }
    (stuck > 0).then_some(stuck)
}

thread_local! {
    /// C#: Observe.FreshVars. The numbers of a fresh copy of a card at an upgrade level, by the card's id and the
    /// level: what the game's code makes of them, the same in every combat.
    static FRESH_VARS: RefCell<HashMap<(Str, Str, i32), Rc<Vec<(Str, Dec)>>>> = RefCell::new(HashMap::new());
}

/// C#: the value FreshVars keeps for a key. `CardModel.FromSerializable(new SerializableCard { Id, CurrentUpgradeLevel,
/// FloorAddedToDeck = 1 })`, which for such a save is: the mutable copy of the id's canonical card, the floor, and the
/// upgrades one by one. The copy is made in a copy of the combat, as the C# one is left to the collector.
fn fresh_vars(id: ModelId, up: i32, c: &Combat) -> Vec<(Str, Dec)> {
    let mut scratch = c.clone();
    let c = &mut scratch;
    let canonical = m::model_db::get_by_id_or_null(Kind::CardModel, id, c);
    if rt::is_null(&canonical) {
        panic!("no card {} in the game's models, to make a fresh copy of", id.entry);
    }
    let copy = g::models::card_model::to_mutable(canonical, c);
    g::models::card_model::set_floor_added_to_deck(copy, Some(1), c);
    g::dispatch::card_model::after_deserialized(copy, c);
    if !rt::is(copy, Kind::DeprecatedCard, c) {
        for _ in 0..up {
            g::models::card_model::upgrade_internal(copy, c);
            g::models::card_model::finalize_upgrade_internal(copy, c);
        }
    }
    let vars = g::models::card_model::dynamic_vars(copy, c);
    let mut fresh = Vec::new();
    for (name, v) in enumerate(vars, c) {
        if !is_calculated(v, c) {
            fresh.push((name, g::localization_dynamic_vars::dynamic_var::base_value(v, c)));
        }
    }
    fresh
}

/// C#: Observe.Grown. How far the card's numbers other than its damage and block, which are read as they are, have
/// moved from those of a fresh copy at its upgrade level. A calculated number is left out as in `vars`.
fn grown(card: CardModel, c: &mut Combat) -> Option<Dec> {
    let id = g::models::abstract_model::id(card, c);
    let up = g::models::card_model::current_upgrade_level(card, c);
    let key = (id.category, id.entry, up);
    let known = FRESH_VARS.with(|known| known.borrow().get(&key).cloned());
    let fresh = match known {
        Some(fresh) => fresh,
        None => {
            let fresh = Rc::new(fresh_vars(id, up, c));
            FRESH_VARS.with(|known| known.borrow_mut().insert(key, fresh.clone()));
            fresh
        }
    };
    let mut moved = Dec::ZERO;
    let vars = g::models::card_model::dynamic_vars(card, c);
    for (name, v) in enumerate(vars, c) {
        if is_calculated(v, c) || matches!(name.as_str(), "Damage" | "OstyDamage" | "Block") {
            continue;
        }
        if let Some((_, was)) = fresh.iter().find(|(fresh_name, _)| *fresh_name == name) {
            moved += g::localization_dynamic_vars::dynamic_var::base_value(v, c) - *was;
        }
    }
    if moved == Dec::ZERO { None } else { Some(moved) }
}

/// C#: Observe.Lasting. The energy cost without the card's changes that end when it is played or when the turn ends.
/// The game keeps them apart only inside the cost.
fn lasting(card: CardModel, c: &mut Combat) -> Option<i32> {
    let cost = g::models::card_model::energy_cost(card, c);
    if !g::entities_cards::card_energy_cost::has_local_modifiers(cost, c) {
        return None;
    }
    let mut lasting = g::entities_cards::card_energy_cost::get_with_modifiers(cost, CostModifiers::None, c);
    if lasting < 0 {
        return None;
    }
    // C#: the private field CardEnergyCost._localModifiers.
    for modifier in CardEnergyCostData::of(cost, c)._local_modifiers.clone() {
        if g::entities_cards::local_cost_modifier::expiration(modifier, c) == LocalCostModifierExpiration::EndOfCombat {
            lasting = g::entities_cards::local_cost_modifier::modify(modifier, lasting, c);
        }
    }
    let gone = lasting - g::entities_cards::card_energy_cost::get_with_modifiers(cost, CostModifiers::Local, c);
    if gone == 0 {
        None
    } else {
        Some((g::entities_cards::card_energy_cost::get_with_modifiers(cost, CostModifiers::All, c) + gone).max(0))
    }
}

/// C#: Observe.StarsLasting. The same as `lasting` for the star cost.
fn stars_lasting(card: CardModel, c: &mut Combat) -> Option<i32> {
    // C#: the private field CardModel._temporaryStarCosts.
    let temporary = CardModelData::of(card, c)._temporary_star_costs.clone();
    if temporary.is_empty() {
        return None;
    }
    let mut kept = Obj::NULL;
    for cost in temporary.iter().rev() {
        if !g::entities_cards::temporary_card_cost::clears_when_turn_ends(*cost, c)
            && !g::entities_cards::temporary_card_cost::clears_when_card_is_played(*cost, c)
        {
            kept = *cost;
            break;
        }
    }
    let lasting = if rt::is_null(&kept) {
        g::models::card_model::base_star_cost(card, c)
    } else {
        g::entities_cards::temporary_card_cost::cost(kept, c)
    };
    let gone = lasting - g::dispatch::card_model::current_star_cost(card, c);
    if gone == 0 {
        None
    } else {
        Some((g::models::card_model::get_star_cost_with_modifiers(card, c) + gone).max(0))
    }
}

/// C#: Observe.Detail. How far the power's numbers have moved from its canonical ones. One moves at a time, Slow's two
/// summed.
fn detail(power: PowerModel, c: &mut Combat) -> Option<Dec> {
    let vars = g::models::power_model::dynamic_vars(power, c);
    if g::localization_dynamic_vars::dynamic_var_set::count(vars, c) == 0 {
        return None;
    }
    // C#: the non-public property PowerModel.CanonicalInstance.
    let canonical = g::models::power_model::canonical_instance(power, c);
    if rt::is_null(&canonical) {
        return None;
    }
    let mut moved: Option<Dec> = None;
    for (name, v) in enumerate(vars, c) {
        if is_calculated(v, c) {
            continue;
        }
        let canonical_vars = g::models::power_model::dynamic_vars(canonical, c);
        let (found, was) = g::localization_dynamic_vars::dynamic_var_set::try_get_value(canonical_vars, name, c);
        if !found {
            continue;
        }
        let was = g::localization_dynamic_vars::dynamic_var::base_value(was, c);
        let now = g::localization_dynamic_vars::dynamic_var::base_value(v, c);
        if was != now {
            moved = Some(moved.unwrap_or(Dec::ZERO) + now - was);
        }
    }
    moved
}

/// C#: Observe.Spent. A relic's own flag of an effect spent for this combat or this turn: a private field the game
/// names so, on the relics that have one. Observe.cs finds the fields by reflection (the relic class's non-public bool
/// fields named ...ThisCombat or ...ThisTurn); here they are the fields of the generated state, by class, and
/// tests/reflection.rs holds this list against the schema.
fn spent(relic: RelicModel, c: &mut Combat) -> Option<bool> {
    let flag = match &RelicModelData::of(relic, c).ext {
        RelicModelExt::ArtOfWar(d) => d._any_attacks_played_this_turn,
        RelicModelExt::BurningSticks(d) => d._was_used_this_combat,
        RelicModelExt::CentennialPuzzle(d) => d._used_this_combat,
        RelicModelExt::ConductorsBaton(d) => d._triggered_this_turn,
        RelicModelExt::DemonTongue(d) => d._triggered_this_turn,
        RelicModelExt::LavaLamp(d) => d._took_damage_this_combat,
        RelicModelExt::MiniRegent(d) => d._used_this_turn,
        RelicModelExt::MusicBox(d) => d._was_used_this_turn,
        RelicModelExt::PaelsEye(d) => d._used_this_combat,
        RelicModelExt::Permafrost(d) => d._activated_this_combat,
        RelicModelExt::Regalite(d) => d._used_this_turn,
        RelicModelExt::RuinedHelmet(d) => d._used_this_combat,
        RelicModelExt::ThrowingAxe(d) => d._used_this_combat,
        RelicModelExt::Vambrace(d) => d._block_gained_this_combat,
        _ => false,
    };
    flag.then_some(true)
}

/// C#: Observe.ThisTurn. The keywords an effect gave the card for this turn only, beside its own.
fn this_turn(card: CardModel, c: &mut Combat) -> Option<Vec<Str>> {
    let mut kept = Vec::new();
    if g::models::card_model::should_retain_this_turn(card, c) && !g::models::card_model::keywords(card, c).contains(&CardKeyword::Retain) {
        kept.push(Str::lit("Retain"));
    }
    if g::models::card_model::is_sly_this_turn(card, c) && !g::models::card_model::keywords(card, c).contains(&CardKeyword::Sly) {
        kept.push(Str::lit("Sly"));
    }
    (!kept.is_empty()).then_some(kept)
}

/// C#: Observe.DeckCard. A card of the deck: `card` with the upgrade preview the deck view shows, if it can be
/// upgraded.
pub fn deck_card(deck_card: CardModel, c: &mut Combat) -> CardView {
    let view = card(deck_card, false, c);
    let upgraded = if g::models::card_model::is_upgradable(deck_card, c) {
        Some(Box::new(upgrade_preview(deck_card, c)))
    } else {
        None
    };
    CardView { upgraded, ..view }
}

/// C#: `Card(UpgradePreview(card))`. The card once more upgraded, made as the game's upgrade preview makes it, on a
/// clone outside the run's cards. The clone is made in a copy of the combat, as the C# one is left to the collector.
fn upgrade_preview(deck_card: CardModel, c: &Combat) -> CardView {
    let mut scratch = c.clone();
    let c = &mut scratch;
    let preview = g::models::abstract_model::mutable_clone(deck_card, c);
    g::models::card_model::upgrade_internal(preview, c);
    g::models::card_model::finalize_upgrade_internal(preview, c);
    card(preview, false, c)
}

/// C#: Observe.Vars. The card's numbers, but for calculated ones: the card shows those as their base number plus
/// their extra number times a count from the state, and both parts are listed. Sorted by name, as they are written.
fn vars(card: CardModel, c: &mut Combat) -> Vec<(Str, Dec)> {
    let set = g::models::card_model::dynamic_vars(card, c);
    let mut vars = Vec::new();
    for (name, v) in enumerate(set, c) {
        if !is_calculated(v, c) {
            vars.push((name, g::localization_dynamic_vars::dynamic_var::base_value(v, c)));
        }
    }
    vars.sort_by(|a, b| a.0.compare(b.0).cmp(&0));
    vars
}

/// C#: Observe.Creature.
fn creature(creature: Creature, targets: &[Creature], c: &mut Combat) -> CreatureView {
    let monster = g::entities_creatures::creature::monster(creature, c);
    CreatureView {
        uid: g::entities_creatures::creature::combat_id(creature, c),
        id: if rt::is_null(&monster) { None } else { Some(entry(monster, c)) },
        hp: g::entities_creatures::creature::current_hp(creature, c),
        max_hp: g::entities_creatures::creature::max_hp(creature, c),
        block: g::entities_creatures::creature::block(creature, c),
        powers: powers(creature, c),
        intents: if !rt::is_null(&monster) && g::entities_creatures::creature::side(creature, c) != CombatSide::Player {
            let next = g::models::monster_model::next_move(monster, c);
            Some(intents(next, creature, targets, c))
        } else {
            None
        },
        arrows: None,
    }
}

/// C#: Observe.Intents. The intents the move would show above its owner, aimed at the targets.
pub fn intents(next: MoveState, owner: Creature, targets: &[Creature], c: &mut Combat) -> Vec<IntentView> {
    let intents = g::monster_moves_monster_move_state_machine::move_state::intents(next, c);
    intents.into_iter().map(|i| intent(i, owner, targets, c)).collect()
}

/// C#: Observe.Intent.
fn intent(intent: AbstractIntent, owner: Creature, targets: &[Creature], c: &mut Combat) -> IntentView {
    let type_ = g::dispatch::abstract_intent::intent_type(intent, c);
    if rt::is(intent, Kind::AttackIntent, c) {
        IntentView {
            type_: name(type_, c),
            damage: Some(g::monster_moves_intents::attack_intent::get_single_damage(
                intent,
                targets.to_vec(),
                owner,
                c,
            )),
            hits: Some(g::dispatch::attack_intent::repeats(intent, c).max(1)),
        }
    } else {
        IntentView {
            type_: name(type_, c),
            damage: None,
            hits: None,
        }
    }
}

/// C#: Observe.Powers.
fn powers(creature: Creature, c: &mut Combat) -> Vec<PowerView> {
    let mut views = Vec::new();
    for p in g::entities_creatures::creature::powers(creature, c) {
        if !g::models::power_model::is_visible(p, c) {
            continue;
        }
        let display = g::dispatch::power_model::display_amount(p, c);
        let amount = g::models::power_model::amount(p, c);
        views.push(PowerView {
            id: entry(p, c),
            amount: display,
            stacks: (display != amount).then_some(amount),
            card: {
                let held = held_card(p, c);
                if rt::is_null(&held) { None } else { Some(card(held, false, c)) }
            },
            detail: detail(p, c),
            stolen: if rt::is(p, Kind::SwipePower, c) {
                let stolen = g::models_powers::swipe_power::stolen_card(p, c);
                if rt::is_null(&stolen) { None } else { Some(card(stolen, false, c)) }
            } else {
                None
            },
        });
    }
    views
}

/// C#: HeldCards.Get. A card that a power keeps in its own data, such as the card Nightmare adds copies of next turn.
/// HeldCards.cs finds it by reflection (the first field of type CardModel of the power's `_internalData`); here it is
/// that field of the generated state, by the data's class, and tests/reflection.rs holds this list against the
/// schema.
fn held_card(power: PowerModel, c: &mut Combat) -> CardModel {
    let data = PowerModelData::of(power, c)._internal_data;
    match data.tag() {
        TAG_CURL_UP_POWER_DATA => CurlUpPowerDataData::of(data, c).played_card,
        TAG_JUGGLING_POWER_DATA => JugglingPowerDataData::of(data, c).card_to_clone,
        TAG_NIGHTMARE_POWER_DATA => NightmarePowerDataData::of(data, c).selected_card,
        _ => Obj::NULL,
    }
}

/// C#: Observe.Relic.
pub fn relic(relic: RelicModel, c: &mut Combat) -> RelicView {
    RelicView {
        id: entry(relic, c),
        counter: if g::dispatch::relic_model::show_counter(relic, c) {
            Some(g::dispatch::relic_model::display_amount(relic, c))
        } else {
            None
        },
        status: {
            let status = g::models::relic_model::status(relic, c);
            if status != RelicStatus::Normal {
                Some(name(status, c))
            } else {
                None
            }
        },
        melted: g::models::relic_model::is_melted(relic, c).then_some(true),
        used_up: g::dispatch::relic_model::is_used_up(relic, c).then_some(true),
        cards: if rt::is(relic, Kind::PaelsTooth, c) {
            let kept = g::models_relics::paels_tooth::serializable_cards(relic, c);
            if kept.is_empty() {
                None
            } else {
                Some(kept.into_iter().map(|x| g::saves_runs::serializable_card::id(x, c).entry).collect())
            }
        } else {
            None
        },
        spent: spent(relic, c),
    }
}
