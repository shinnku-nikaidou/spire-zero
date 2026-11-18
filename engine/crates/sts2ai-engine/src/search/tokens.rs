//! A combat state as the combat net reads it, and the graph's inputs of a batch of states (C#: CombatTokens.Of, then
//! CombatNet.Feed and Stack). The numbers are made as C# makes them, in doubles, scaled and capped, then narrowed to
//! floats where the graph takes them.

use super::state::History;
use crate::protocol::{CardView, CombatObservation, IntentView, PowerView, RelicView};
use crate::rt::Dec;

/// C#: CombatTokens.Cap.
const CAP: f64 = 10.0;

/// C#: RunNet.MaxUp.
const MAX_UP: i32 = 3;

/// C#: CombatTokens.Piles.
const PILES: usize = 4;

/// C#: CombatTokens.CardNumbers.
const CARD_NUMBERS: usize = 6;

/// C#: CombatTokens.EnemyScalars.
const ENEMY_SCALARS: usize = 6;

/// C#: Math.Clamp(x, -Cap, Cap).
fn capped(x: f64) -> f64 {
    clamp(x, -CAP, CAP)
}

/// C#: Math.Clamp of doubles.
pub fn clamp(x: f64, min: f64, max: f64) -> f64 {
    if x < min {
        min
    } else if x > max {
        max
    } else {
        x
    }
}

/// C#: (double) of a decimal.
fn dbl(d: Dec) -> f64 {
    d.to_f64()
}

/// C#: CombatCardToken.
pub struct CardToken {
    id: &'static str,
    up: i32,
    pile: usize,
    modifiers: Vec<String>,
    cost: f64,
    amounts: Vec<f64>,
    damage: f64,
    numbers: [f64; CARD_NUMBERS],
}

/// C#: PowerToken.
pub struct PowerToken {
    id: &'static str,
    amount: f64,
    stacks: f64,
    card: Option<&'static str>,
    detail: f64,
}

/// C#: IntentToken.
pub struct IntentToken {
    type_: &'static str,
    damage: f64,
    total: f64,
}

/// C#: EnemyTokens.
pub struct EnemyTokens {
    id: &'static str,
    scalars: [f64; ENEMY_SCALARS],
    powers: Vec<PowerToken>,
    slots: Vec<Vec<IntentToken>>,
}

/// C#: AllyTokens.
pub struct AllyTokens {
    id: &'static str,
    scalars: [f64; 4],
    powers: Vec<PowerToken>,
}

/// C#: CombatTokens.
pub struct Tokens {
    cards: Vec<CardToken>,
    powers: Vec<PowerToken>,
    player: Vec<f64>,
    enemies: Vec<EnemyTokens>,
    relics: Vec<String>,
    relic_values: Vec<[f64; 3]>,
    relic_status: Vec<&'static str>,
    potions: Vec<&'static str>,
    context: Vec<String>,
    orbs: Vec<&'static str>,
    orb_values: Vec<[f64; 2]>,
    allies: Vec<AllyTokens>,
    extra: Vec<f64>,
}

/// C#: CombatTokens.Of: in NoSL where `nosl`, its mark `env:NoSL` in the context; in SL none.
pub fn of(obs: &CombatObservation, history: &History, lags: usize, nosl: bool) -> Tokens {
    let pl = &obs.player;
    let enemies = obs
        .enemies
        .iter()
        .map(|e| {
            // C#: history.ToDictionary(h => h.Uid, h => h.Turns.Where(t => t.Round < obs.Round)).
            let turns: Vec<&Vec<IntentView>> = e
                .uid
                .and_then(|uid| history.iter().find(|h| h.uid == uid))
                .map(|h| h.turns.iter().filter(|t| t.round < obs.round).map(|t| &t.intents).collect())
                .unwrap_or_default();
            let mut slots = vec![intents(e.intents.as_deref().unwrap_or(&[]))];
            let mut lag = 1;
            while lag <= lags && lag <= turns.len() {
                slots.push(intents(turns[turns.len() - lag]));
                lag += 1;
            }
            let [a, b, c, d] = creature(e.hp, e.max_hp, e.block);
            EnemyTokens {
                id: e.id.map(|id| id.as_str()).unwrap_or("<none>"),
                scalars: [a, b, c, d, capped(turns.len() as f64 / 10.0), capped(e.arrows.unwrap_or(0) as f64 / 3.0)],
                powers: powers_of(&e.powers),
                slots,
            }
        })
        .collect();
    let mut cards = Vec::new();
    for (p, pile) in [&obs.hand, &obs.draw, &obs.discard, &obs.exhaust].into_iter().enumerate() {
        cards.extend(pile.iter().map(|c| card(c, p)));
    }
    let mut player = creature(pl.hp, pl.max_hp, pl.block).to_vec();
    player.extend([
        capped(pl.energy as f64 / 3.0),
        capped(pl.max_energy as f64 / 3.0),
        capped(pl.stars as f64 / 5.0),
        capped(obs.round as f64 / 10.0),
    ]);
    let mut context = vec![
        format!("character:{}", pl.character.as_str()),
        format!("encounter:{}", obs.encounter.map(|s| s.as_str()).unwrap_or("<none>")),
        format!("room:{}", obs.room.map(|s| s.as_str()).unwrap_or("<none>")),
        format!("act:{}", obs.act),
    ];
    // C#: ModeTokens.
    if let Some(mode) = obs.mode {
        context.push(format!("mode:{}", mode.as_str()));
    }
    if let Some(dominion) = obs.dominion {
        context.push(format!("dominion:{}", dominion.as_str()));
    }
    // C#: EnvTokens.
    if nosl {
        context.push("env:NoSL".to_string());
    }
    Tokens {
        cards,
        powers: powers_of(&pl.powers),
        player,
        enemies,
        relics: obs.relics.iter().map(relic_id).collect(),
        relic_values: obs.relics.iter().map(relic_values).collect(),
        relic_status: obs.relics.iter().map(relic_status).collect(),
        potions: obs.potions.iter().flatten().map(|p| p.as_str()).collect(),
        context,
        orbs: pl.orbs.iter().map(|o| o.id.as_str()).collect(),
        orb_values: pl.orbs.iter().map(|o| [capped(dbl(o.passive) / 10.0), capped(dbl(o.evoke) / 10.0)]).collect(),
        allies: obs
            .allies
            .iter()
            .filter(|a| a.hp > 0)
            .map(|a| AllyTokens {
                id: a.id.map(|id| id.as_str()).unwrap_or("<player>"),
                scalars: creature(a.hp, a.max_hp, a.block),
                powers: powers_of(&a.powers),
            })
            .collect(),
        extra: vec![
            obs.ascension as f64 / 10.0,
            pl.orb_slots as f64 / 10.0,
            capped(pl.gold as f64 / 100.0),
            obs.potions.len() as f64 / 10.0,
            capped(pl.arrows.unwrap_or(0) as f64 / 3.0),
            capped(pl.arrow_slots.unwrap_or(0) as f64 / 3.0),
        ],
    }
}

/// C#: CombatTokens.RelicId.
fn relic_id(relic: &RelicView) -> String {
    if relic.used_up == Some(true) { format!("{}:used", relic.id.as_str()) } else { relic.id.as_str().to_string() }
}

/// C#: CombatTokens.RelicValuesOf.
fn relic_values(relic: &RelicView) -> [f64; 3] {
    [
        relic.counter.map(|c| capped(c as f64 / 10.0)).unwrap_or(0.0),
        if relic.counter.is_some() { 1.0 } else { 0.0 },
        if relic.melted == Some(true) { 1.0 } else { 0.0 },
    ]
}

/// C#: CombatTokens.RelicStatusOf.
fn relic_status(relic: &RelicView) -> &'static str {
    match relic.status {
        Some(status) => status.as_str(),
        None if relic.spent == Some(true) => "Spent",
        None => "Normal",
    }
}

/// C#: CombatTokens.Modifiers.
fn modifiers(card: &CardView) -> Vec<String> {
    let mut tokens = Vec::new();
    if let Some(enchant) = &card.enchant {
        tokens.push(format!("enchant:{}", enchant.id.as_str()));
    }
    if let Some(affliction) = &card.affliction {
        tokens.push(format!("affliction:{}", affliction.id.as_str()));
    }
    for keyword in card.keywords.iter().flatten().chain(card.this_turn.iter().flatten()) {
        tokens.push(format!("keyword:{}", keyword.as_str()));
    }
    if let Some(tinker) = &card.tinker {
        tokens.push(format!("tinker:{}", tinker.type_.as_str()));
        tokens.push(format!("rider:{}", tinker.rider.as_str()));
    }
    if card.spent == Some(true) {
        tokens.push("spent".to_string());
    }
    tokens
}

/// C#: `vars.GetValueOrDefault(name)` of a card's vars.
fn var(card: &CardView, name: &str) -> Option<Dec> {
    card.vars.as_ref()?.iter().find(|(k, _)| k.as_str() == name).map(|(_, v)| *v)
}

/// C#: CombatTokens.Card.
fn card(card: &CardView, pile: usize) -> CardToken {
    let damage = var(card, "Damage").or_else(|| var(card, "OstyDamage")).map(dbl).unwrap_or(0.0);
    CardToken {
        id: card.id.as_str(),
        up: card.up.unwrap_or(0).min(MAX_UP),
        pile,
        modifiers: modifiers(card),
        cost: match card.cost {
            Some(cost) if cost > 0 => cost as f64 / 3.0,
            _ => 0.0,
        },
        amounts: [&card.enchant, &card.affliction].into_iter().flatten().map(|m| capped(m.amount as f64 / 10.0)).collect(),
        damage: capped(damage / 20.0),
        numbers: [
            capped(var(card, "Block").map(dbl).unwrap_or(0.0) / 20.0),
            capped(card.calc.map(dbl).unwrap_or(0.0) / 20.0),
            capped(card.replay.unwrap_or(0) as f64 / 2.0),
            capped(card.stars.unwrap_or(0) as f64 / 5.0),
            capped(card.enchant.as_ref().and_then(|e| e.extra).unwrap_or(0) as f64 / 20.0),
            capped(card.grown.map(dbl).unwrap_or(0.0) / 10.0),
        ],
    }
}

/// C#: CombatTokens.PowersOf.
fn powers_of(powers: &[PowerView]) -> Vec<PowerToken> {
    powers
        .iter()
        .map(|p| PowerToken {
            id: p.id.as_str(),
            amount: capped(p.amount as f64 / 10.0),
            stacks: p.stacks.map(|s| capped(s as f64 / 10.0)).unwrap_or(0.0),
            card: p.card.as_ref().or(p.stolen.as_ref()).map(|c| c.id.as_str()),
            detail: capped(p.detail.map(dbl).unwrap_or(0.0) / 20.0),
        })
        .collect()
}

/// C#: CombatTokens.Intents.
fn intents(intents: &[IntentView]) -> Vec<IntentToken> {
    intents
        .iter()
        .map(|i| {
            let damage = i.damage.unwrap_or(0);
            IntentToken {
                type_: i.type_.as_str(),
                damage: capped(damage as f64 / 10.0),
                total: capped(damage.wrapping_mul(i.hits.unwrap_or(1)) as f64 / 20.0),
            }
        })
        .collect()
}

/// C#: CombatTokens.Creature.
fn creature(hp: i32, max_hp: i32, block: i32) -> [f64; 4] {
    [
        capped(hp as f64 / max_hp.max(1) as f64),
        capped(hp as f64 / 100.0),
        capped(max_hp as f64 / 100.0),
        capped(block as f64 / 20.0),
    ]
}

// ---------------------------------------------------------------------------------------------------------------------
// The graph's inputs
// ---------------------------------------------------------------------------------------------------------------------

/// One input of the graph: its name, its shape and its numbers in row-major order.
pub struct Input {
    pub name: &'static str,
    pub dims: Vec<usize>,
    pub data: Data,
}

pub enum Data {
    F32(Vec<f32>),
    I64(Vec<i64>),
}

/// An input of one state, filled as C#'s DenseTensor is: zeros, then the elements set.
struct One<T> {
    dims: Vec<usize>,
    data: Vec<T>,
}

impl<T: Copy + Default> One<T> {
    fn new(dims: &[usize]) -> One<T> {
        One {
            dims: dims.to_vec(),
            data: vec![T::default(); dims.iter().product()],
        }
    }

    fn set(&mut self, at: &[usize], value: T) {
        let mut i = 0;
        for (d, &a) in at.iter().enumerate() {
            debug_assert!(a < self.dims[d]);
            i = i * self.dims[d] + a;
        }
        self.data[i] = value;
    }
}

fn f32s(name: &'static str, one: One<f32>) -> Input {
    Input {
        name,
        dims: one.dims,
        data: Data::F32(one.data),
    }
}

fn i64s(name: &'static str, one: One<i64>) -> Input {
    Input {
        name,
        dims: one.dims,
        data: Data::I64(one.data),
    }
}

/// A token table's entry of an id: 0 for an id the table does not list (C#: CombatNet.Index).
pub trait Index {
    fn index(&self, table: &str, id: &str) -> i64;
}

/// C#: CombatNet.Feed. The graph's inputs of one state's tokens, each a batch of one.
pub fn feed(t: &Tokens, lags: usize, ix: &dyn Index) -> Vec<Input> {
    let mut inputs = Vec::with_capacity(48);
    cards(&t.cards, ix, &mut inputs);
    powers("powers", "power_amounts", "power_mask", &t.powers, ix, &mut inputs);
    power_numbers(&t.powers, ix, &mut inputs);
    inputs.push(row("player", &t.player));
    enemies(&t.enemies, lags, ix, &mut inputs);
    bag("relics", "relic_mask", t.relics.iter().map(|s| s.as_str()), ix, &mut inputs);
    // C#: CombatNet.RelicInputs.
    let n = t.relic_values.len().max(1);
    let mut numbers = One::<f32>::new(&[1, n, 3]);
    let mut rows = One::<i64>::new(&[1, n]);
    for (k, values) in t.relic_values.iter().enumerate() {
        for (j, v) in values.iter().enumerate() {
            numbers.set(&[0, k, j], *v as f32);
        }
        rows.set(&[0, k], ix.index("relic_status", t.relic_status[k]));
    }
    inputs.push(f32s("relic_values", numbers));
    inputs.push(i64s("relic_status", rows));
    bag("potions", "potion_mask", t.potions.iter().copied(), ix, &mut inputs);
    bag("context", "context_mask", t.context.iter().map(|s| s.as_str()), ix, &mut inputs);
    bag("orbs", "orb_mask", t.orbs.iter().copied(), ix, &mut inputs);
    let mut orb_values = One::<f32>::new(&[1, t.orbs.len().max(1), 2]);
    for (k, values) in t.orb_values.iter().enumerate() {
        for (j, v) in values.iter().enumerate() {
            orb_values.set(&[0, k, j], *v as f32);
        }
    }
    inputs.push(f32s("orb_values", orb_values));
    allies(&t.allies, ix, &mut inputs);
    inputs.push(row("extra", &t.extra));
    inputs
}

fn cards(cards: &[CardToken], ix: &dyn Index, inputs: &mut Vec<Input>) {
    let n = cards.len().max(1);
    let m = cards.iter().map(|c| c.modifiers.len()).max().unwrap_or(0).max(1);
    let mut ids = One::<i64>::new(&[1, n]);
    let mut ups = One::<f32>::new(&[1, n]);
    let mut costs = One::<f32>::new(&[1, n]);
    let mut damages = One::<f32>::new(&[1, n]);
    let mut piles = One::<f32>::new(&[1, n, PILES]);
    let mut mods = One::<i64>::new(&[1, n, m]);
    let mut mod_mask = One::<f32>::new(&[1, n, m]);
    let mut mod_amounts = One::<f32>::new(&[1, n, m]);
    let mut numbers = One::<f32>::new(&[1, n, CARD_NUMBERS]);
    for (k, card) in cards.iter().enumerate() {
        ids.set(&[0, k], ix.index("cards", card.id));
        ups.set(&[0, k], card.up as f32);
        costs.set(&[0, k], card.cost as f32);
        piles.set(&[0, k, card.pile], 1.0);
        damages.set(&[0, k], card.damage as f32);
        for (j, modifier) in card.modifiers.iter().enumerate() {
            mods.set(&[0, k, j], ix.index("modifiers", modifier));
            mod_mask.set(&[0, k, j], 1.0);
        }
        for (j, amount) in card.amounts.iter().enumerate() {
            mod_amounts.set(&[0, k, j], *amount as f32);
        }
        for (i, number) in card.numbers.iter().enumerate() {
            numbers.set(&[0, k, i], *number as f32);
        }
    }
    inputs.push(i64s("cards", ids));
    inputs.push(f32s("ups", ups));
    inputs.push(f32s("costs", costs));
    inputs.push(f32s("piles", piles));
    inputs.push(i64s("mods", mods));
    inputs.push(f32s("mod_mask", mod_mask));
    inputs.push(f32s("mod_amounts", mod_amounts));
    inputs.push(f32s("damages", damages));
    inputs.push(f32s("numbers", numbers));
}

fn enemies(enemies: &[EnemyTokens], lags: usize, ix: &dyn Index, inputs: &mut Vec<Input>) {
    let slots = 1 + lags;
    let e_n = enemies.len().max(1);
    let q_n = enemies.iter().map(|e| e.powers.len()).max().unwrap_or(0).max(1);
    let i_n = enemies.iter().flat_map(|e| e.slots.iter().map(|s| s.len())).max().unwrap_or(0).max(1);
    let mut ids = One::<i64>::new(&[1, e_n]);
    let mut scalars = One::<f32>::new(&[1, e_n, ENEMY_SCALARS]);
    let mut mask = One::<f32>::new(&[1, e_n]);
    let mut powers = One::<i64>::new(&[1, e_n, q_n]);
    let mut power_amounts = One::<f32>::new(&[1, e_n, q_n]);
    let mut power_mask = One::<f32>::new(&[1, e_n, q_n]);
    let mut power_stacks = One::<f32>::new(&[1, e_n, q_n]);
    let mut power_cards = One::<i64>::new(&[1, e_n, q_n]);
    let mut power_card_mask = One::<f32>::new(&[1, e_n, q_n]);
    let mut power_details = One::<f32>::new(&[1, e_n, q_n]);
    let mut intent_ids = One::<i64>::new(&[1, e_n, slots, i_n]);
    let mut intent_values = One::<f32>::new(&[1, e_n, slots, i_n, 2]);
    let mut intent_mask = One::<f32>::new(&[1, e_n, slots, i_n]);
    for (k, e) in enemies.iter().enumerate() {
        ids.set(&[0, k], ix.index("enemies", e.id));
        mask.set(&[0, k], 1.0);
        for (i, s) in e.scalars.iter().enumerate() {
            scalars.set(&[0, k, i], *s as f32);
        }
        for (j, power) in e.powers.iter().enumerate() {
            powers.set(&[0, k, j], ix.index("powers", power.id));
            power_amounts.set(&[0, k, j], power.amount as f32);
            power_mask.set(&[0, k, j], 1.0);
            power_stacks.set(&[0, k, j], power.stacks as f32);
            power_details.set(&[0, k, j], power.detail as f32);
            let Some(card) = power.card else {
                continue;
            };
            power_cards.set(&[0, k, j], ix.index("cards", card));
            power_card_mask.set(&[0, k, j], 1.0);
        }
        for (s, slot) in e.slots.iter().enumerate() {
            for (j, intent) in slot.iter().enumerate() {
                intent_ids.set(&[0, k, s, j], ix.index("intents", intent.type_));
                intent_mask.set(&[0, k, s, j], 1.0);
                intent_values.set(&[0, k, s, j, 0], intent.damage as f32);
                intent_values.set(&[0, k, s, j, 1], intent.total as f32);
            }
        }
    }
    inputs.push(i64s("enemies", ids));
    inputs.push(f32s("enemy_scalars", scalars));
    inputs.push(f32s("enemy_mask", mask));
    inputs.push(i64s("enemy_powers", powers));
    inputs.push(f32s("enemy_power_amounts", power_amounts));
    inputs.push(f32s("enemy_power_mask", power_mask));
    inputs.push(f32s("enemy_power_stacks", power_stacks));
    inputs.push(i64s("enemy_power_cards", power_cards));
    inputs.push(f32s("enemy_power_card_mask", power_card_mask));
    inputs.push(f32s("enemy_power_details", power_details));
    inputs.push(i64s("intents", intent_ids));
    inputs.push(f32s("intent_values", intent_values));
    inputs.push(f32s("intent_mask", intent_mask));
}

/// C#: CombatNet.PowerNumbers.
fn power_numbers(powers: &[PowerToken], ix: &dyn Index, inputs: &mut Vec<Input>) {
    let n = powers.len().max(1);
    let mut stacks = One::<f32>::new(&[1, n]);
    let mut cards = One::<i64>::new(&[1, n]);
    let mut mask = One::<f32>::new(&[1, n]);
    let mut details = One::<f32>::new(&[1, n]);
    for (k, power) in powers.iter().enumerate() {
        stacks.set(&[0, k], power.stacks as f32);
        details.set(&[0, k], power.detail as f32);
        let Some(card) = power.card else {
            continue;
        };
        cards.set(&[0, k], ix.index("cards", card));
        mask.set(&[0, k], 1.0);
    }
    inputs.push(f32s("power_stacks", stacks));
    inputs.push(i64s("power_cards", cards));
    inputs.push(f32s("power_card_mask", mask));
    inputs.push(f32s("power_details", details));
}

fn allies(allies: &[AllyTokens], ix: &dyn Index, inputs: &mut Vec<Input>) {
    let n = allies.len().max(1);
    let q = allies.iter().map(|a| a.powers.len()).max().unwrap_or(0).max(1);
    let mut ids = One::<i64>::new(&[1, n]);
    let mut scalars = One::<f32>::new(&[1, n, 4]);
    let mut mask = One::<f32>::new(&[1, n]);
    let mut powers = One::<i64>::new(&[1, n, q]);
    let mut amounts = One::<f32>::new(&[1, n, q]);
    let mut power_mask = One::<f32>::new(&[1, n, q]);
    for (k, a) in allies.iter().enumerate() {
        ids.set(&[0, k], ix.index("allies", a.id));
        for (i, s) in a.scalars.iter().enumerate() {
            scalars.set(&[0, k, i], *s as f32);
        }
        mask.set(&[0, k], 1.0);
        for (j, power) in a.powers.iter().enumerate() {
            powers.set(&[0, k, j], ix.index("powers", power.id));
            amounts.set(&[0, k, j], power.amount as f32);
            power_mask.set(&[0, k, j], 1.0);
        }
    }
    inputs.push(i64s("allies", ids));
    inputs.push(f32s("ally_scalars", scalars));
    inputs.push(f32s("ally_mask", mask));
    inputs.push(i64s("ally_powers", powers));
    inputs.push(f32s("ally_power_amounts", amounts));
    inputs.push(f32s("ally_power_mask", power_mask));
}

/// C#: CombatNet.Row.
fn row(name: &'static str, numbers: &[f64]) -> Input {
    let mut one = One::<f32>::new(&[1, numbers.len()]);
    for (i, n) in numbers.iter().enumerate() {
        one.set(&[0, i], *n as f32);
    }
    f32s(name, one)
}

/// C#: CombatNet.Powers.
fn powers(ids: &'static str, amounts: &'static str, mask: &'static str, powers: &[PowerToken], ix: &dyn Index, inputs: &mut Vec<Input>) {
    let n = powers.len().max(1);
    let mut index = One::<i64>::new(&[1, n]);
    let mut amount = One::<f32>::new(&[1, n]);
    let mut present = One::<f32>::new(&[1, n]);
    for (k, power) in powers.iter().enumerate() {
        index.set(&[0, k], ix.index("powers", power.id));
        amount.set(&[0, k], power.amount as f32);
        present.set(&[0, k], 1.0);
    }
    inputs.push(i64s(ids, index));
    inputs.push(f32s(amounts, amount));
    inputs.push(f32s(mask, present));
}

/// C#: CombatNet.Bag.
fn bag<'a>(table: &'static str, mask: &'static str, ids: impl ExactSizeIterator<Item = &'a str>, ix: &dyn Index, inputs: &mut Vec<Input>) {
    let n = ids.len().max(1);
    let mut index = One::<i64>::new(&[1, n]);
    let mut present = One::<f32>::new(&[1, n]);
    for (k, id) in ids.enumerate() {
        index.set(&[0, k], ix.index(table, id));
        present.set(&[0, k], 1.0);
    }
    inputs.push(i64s(table, index));
    inputs.push(f32s(mask, present));
}

/// C#: CombatNet.Stack and Stacked. Several states' inputs as one batch: every input padded with zeros to its widest
/// state in each dimension, a row a state.
pub fn stack(mut states: Vec<Vec<Input>>) -> Vec<Input> {
    if states.len() == 1 {
        return states.pop().expect("one state");
    }
    let count = states[0].len();
    let mut batch = Vec::with_capacity(count);
    for k in 0..count {
        let first = &states[0][k];
        let rank = first.dims.len();
        let mut dims = vec![0; rank];
        dims[0] = states.len();
        for state in &states {
            for d in 1..rank {
                dims[d] = dims[d].max(state[k].dims[d]);
            }
        }
        let data = match &first.data {
            Data::F32(_) => Data::F32(stacked(&states, k, &dims, |data| match data {
                Data::F32(v) => v,
                Data::I64(_) => panic!("input {} is not all floats", first.name),
            })),
            Data::I64(_) => Data::I64(stacked(&states, k, &dims, |data| match data {
                Data::I64(v) => v,
                Data::F32(_) => panic!("input {} is not all integers", first.name),
            })),
        };
        batch.push(Input {
            name: first.name,
            dims,
            data,
        });
    }
    batch
}

/// C#: CombatNet.Stacked. Input `k` of each state, each a batch of one, as one tensor with a row a state.
fn stacked<T: Copy + Default>(states: &[Vec<Input>], k: usize, dims: &[usize], numbers: impl Fn(&Data) -> &Vec<T>) -> Vec<T> {
    let rank = dims.len();
    let mut strides = vec![1; rank];
    for d in (0..rank - 1).rev() {
        strides[d] = strides[d + 1] * dims[d + 1];
    }
    let mut out = vec![T::default(); dims.iter().product()];
    for (b, state) in states.iter().enumerate() {
        let part = &state[k];
        let from = numbers(&part.data);
        let mut part_strides = vec![1; rank];
        for d in (0..rank - 1).rev() {
            part_strides[d] = part_strides[d + 1] * part.dims[d + 1];
        }
        for (i, &value) in from.iter().enumerate() {
            // The element's place in its state, dimension by dimension, is its place in the state's row of the batch.
            let mut rest = i;
            let mut at = b * strides[0];
            for d in 1..rank {
                let index = rest / part_strides[d];
                rest -= index * part_strides[d];
                at += index * strides[d];
            }
            out[at] = value;
        }
    }
    out
}
