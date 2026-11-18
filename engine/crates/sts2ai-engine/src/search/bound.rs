//! The SL planner's one rule written by hand, for speed (C#: LifeBound, where its reasons and the user's words are): a
//! line stops at a state that cannot end the combat with as much HP plus max HP (`reach`) as a win found with the same
//! potions at its end does.

use std::collections::HashMap;

use crate::protocol::CombatObservation;
use crate::rt::Str;

/// By the potions held at a win's end (`potions`), the most HP plus max HP a win found ends the combat with.
pub type Bounds = HashMap<String, i32>;

/// C#: LifeBound.Potions. The potions a state holds, as the rule tells states apart: their ids, sorted (ordinal),
/// comma-separated.
pub fn potions(belt: &[Option<Str>]) -> String {
    let mut held: Vec<&str> = belt.iter().flatten().map(|p| p.as_str()).collect();
    held.sort_unstable();
    held.join(",")
}

/// C#: LifeBound.Note. A win found raises the bound of the potions it ends with to its HP plus max HP; true if it did.
pub fn note(bounds: &mut Bounds, won: bool, potions: &str, life: i32) -> bool {
    if !won || bounds.get(potions).is_some_and(|&bound| bound >= life) {
        return false;
    }
    bounds.insert(potions.to_string(), life);
    true
}

/// C#: LifeBound.Below.
pub fn below(bounds: &Bounds, potions: &str, reach: i32) -> bool {
    bounds.get(potions).is_some_and(|&bound| reach < bound)
}

/// C#: LifeBound.Reach. The most HP plus max HP the player can end the combat with from this state; `i32::MAX` where
/// a gain has no amount to count.
pub fn reach(obs: &CombatObservation) -> i32 {
    let relics: Vec<&str> = obs
        .relics
        .iter()
        .filter(|r| r.status.is_none_or(|s| s.as_str() != "Disabled"))
        .map(|r| r.id.as_str())
        .collect();
    if relics.iter().any(|r| matches!(*r, "BOOK_REPAIR_KNIFE" | "BOOK_OF_FIVE_RINGS" | "DRAGON_FRUIT" | "DARKSTONE_PERIAPT")) {
        return i32::MAX;
    }
    let potions: Vec<&str> = obs.potions.iter().flatten().map(|p| p.as_str()).collect();
    let mut max_hp: i64 = obs.player.max_hp as i64
        + relics.iter().filter(|r| **r == "CHOSEN_CHEESE").count() as i64
        + 5 * potions.iter().filter(|p| **p == "FRUIT_JUICE").count() as i64;
    // Heals by a share of the max HP, counted at the end's, the most it can be.
    let mut shares: Vec<i64> = Vec::new();
    let mut heal: i64 = 0;
    for card in obs.hand.iter().chain(&obs.draw).chain(&obs.discard).chain(&obs.exhaust) {
        let id = card.id.as_str();
        if !matches!(id, "NOT_YET" | "FEED" | "ALCHEMIZE") {
            continue;
        }
        if !card.keywords.as_ref().is_some_and(|k| k.iter().any(|k| k.as_str() == "Exhaust")) {
            return i32::MAX;
        }
        let plays = 1 + card.replay.unwrap_or(0) as i64;
        let up = card.up.is_some_and(|u| u > 0);
        // C#: `card.Vars?.GetValueOrDefault(name) is > 0 and var v ? v : fallback`, then `(long)` of the decimal.
        let var = |name: &str, fallback: i64| -> i64 {
            card.vars
                .as_ref()
                .and_then(|vars| vars.iter().find(|(k, _)| k.as_str() == name).map(|(_, v)| *v))
                .filter(|v| v.sign() > 0)
                .map(|v| v.to_i64())
                .unwrap_or(fallback)
        };
        match id {
            "NOT_YET" => heal += plays * var("Heal", if up { 13 } else { 10 }),
            "FEED" => max_hp += plays * var("MaxHp", if up { 4 } else { 3 }),
            _ => shares.extend(std::iter::repeat_n(20, plays as usize)),
        }
    }
    for relic in &relics {
        match *relic {
            "BURNING_BLOOD" => heal += 6,
            "BLACK_BLOOD" | "MEAT_ON_THE_BONE" => heal += 12,
            "LIZARD_TAIL" => shares.push(50),
            _ => {}
        }
    }
    for potion in &potions {
        match *potion {
            "BLOOD_POTION" => shares.push(20),
            "AMBERGRIS" => shares.push(50),
            "FAIRY_IN_A_BOTTLE" => shares.push(30),
            "REGEN_POTION" => heal += 15,
            "ENTROPIC_BREW" => shares.extend(std::iter::repeat_n(20, obs.potions.len())),
            _ => {}
        }
    }
    for power in &obs.player.powers {
        if power.id.as_str() == "REGEN_POWER" && power.amount > 0 {
            heal += power.amount as i64 * (power.amount as i64 + 1) / 2;
        }
    }
    heal += shares.iter().map(|share| (max_hp * share + 99) / 100).sum::<i64>();
    (i32::MAX as i64 - 1).min(max_hp.min(obs.player.hp as i64 + heal) + max_hp) as i32
}
