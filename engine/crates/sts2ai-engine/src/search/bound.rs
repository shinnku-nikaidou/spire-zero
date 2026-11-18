//! The SL planner's one rule written by hand, for speed (the user, 2026-11-18: 「max下引入手寫剪枝規則，如果一個枝葉算下去
//! hp+max hp已經低於目前最好的hp+max hp，就剪掉…這是必要的為了性能上的犧牲」; then 「適用於全部網絡，然後注意藥水，藥水作為
//! hash的key的一部分，得相同的藥水討論才有意義」): a line stops at a state that cannot end the combat with as much HP plus
//! max HP (`reach`) as a win found with the same potions at its end does (`note`). Every effort prunes so, the beam and
//! the solver. A line the pick would take for a relic's counter, say, can go so; a proof is a proof under this rule.
//!
//! Ported line for line from the C# brain (plans/2026-11-18-2132-sl-search-rust.md, B and C); the C# search was deleted
//! on 2026-11-19 at the user's word (C5), and the `C#:` names below are where each piece came from, in git's history.

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

/// C#: LifeBound.Reach. The most HP plus max HP the player can end the combat with from this state: the HP now, raised by
/// every heal the state still holds but never past the max HP, plus the max HP, raised by every gain it holds;
/// `i32::MAX` where a gain has no amount to count. These are every heal and gain the game has on the player in a fight or
/// at its end (v0.113.1, each `CreatureCmd.Heal` and `GainMaxHp`; the user, 2026-11-18: 「都記上」, 「蜥蜴尾巴就加50%」,
/// 「再生確實需要加上（雖然也是藥），但藥用了之後還是會加血，其他的也一樣」):
/// - after a win: Burning Blood heals 6, Black Blood 12, Meat on the Bone 12 (at half HP or less); Chosen Cheese gives 1
///   max HP;
/// - Lizard Tail, unused, heals 50 % of max HP on dying;
/// - potions held: Blood Potion heals 20 % of max HP, Ambergris 50 %, Fairy in a Bottle 30 % (on dying), Regen Potion 15
///   (Regen 5, one less each turn); Fruit Juice gives 5 max HP; Entropic Brew fills the belt with random potions, each
///   of which can be a Blood Potion;
/// - Regen on the player heals its amount, one less each turn;
/// - cards in any pile, each as often as it plays (once, and its own Replay): Not Yet heals 10 (13 upgraded), Feed gives
///   3 max HP (4 upgraded) on a kill, Alchemize makes a random potion. One that does not exhaust plays again and again
///   (the user: 「如果这张卡没有消耗…这条规则直接不适用」; Souls Power, from the Grave of the Forgotten, takes Exhaust off a
///   card): no bound;
/// - no amount to count, so no bound: Book Repair Knife (3 a kill by Doom), Book of Five Rings (20 each fifth card added
///   to the deck), Dragon Fruit (1 max HP a gain of gold), Darkstone Periapt (6 max HP a curse added to the deck).
///
/// Before combat (Blood Vial, Bath Towel, Pantograph and the like) it has healed already. Demon Tongue heals back what the
/// player's own turn took, never past the HP before. Other effects that play a card again (Burst, Echo Form, Duplication
/// and the like) are not counted.
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
