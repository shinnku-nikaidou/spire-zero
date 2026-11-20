//! A rule written by hand, among a turn's ends (the user, 2026-11-19: 「肯定得回合內對單角色輸出最多+debuff最多並且自己扣血最少，
//! （假設結算完畢是擁有相同卡組和順序包括狀態卡，相同的敵方buff）」, then 「這個優化得作用在全部的effort裡面」;
//! plans/2026-11-19-2103-turn-dominance.md): of the ends of one turn's search at the next turn's start, after the enemies'
//! turn and the draw, two that are the same in all but each enemy's HP, the amounts of each enemy's debuffs, the
//! player's HP and each ally's HP are compared. One with no enemy's HP higher, no debuff's amount lower and neither the
//! player's HP nor an ally's lower than the other's, and one of them better, dominates it, and the turn's search drops
//! the other (`turn.rs`, `Dfs::value`): no line goes on from it and the pick never takes it. The beam's turns and the
//! solver's go through it, at every effort.
//!
//! An ally's HP is compared as the player's is (the user, 2026-11-20: 「加進規則」): Wuppy takes the enemies' hits in
//! the player's place and keeps what is left of its HP from fight to fight, in its relic's counter. With its HP part of
//! what must be the same, a turn that left out Neutralize was never compared with the one that played it, Wuppy having
//! taken 9 where it would have taken 6.
//!
//! What must be the same (`Shape::rest`) is the state key's observation with the numbers compared taken out, and the
//! intents' damage too (the enemy's move, its Strength, its Weak and the player's powers make it), with the full hidden
//! key: every pile, the draw pile in its order, status cards and curses with the rest; every power of the player and
//! every buff of the enemies with its amount; the potions; the allies but their HP; the enemies themselves, so a line
//! that killed one is not compared with one that did not; the random streams; each enemy's move to come and its moves
//! so far.
//!
//! Not strict, by the user's word (「遺物計數 不算，不然數不來了，代碼的時候寫個註釋，註明其實不是特別嚴格」): the relics'
//! counters are left out of what must be the same, so that two lines that played another number of attacks are compared
//! all the same. A counter one further is not always better: Pen Nib doubles every tenth attack, and one further moves
//! that double onto another attack.
//!
//! A debuff is a power the game declares one (`PowerModel.Type`, `observe::enemy_debuffs`), compared by its amount (none
//! is 0) where the amount is not negative and the power shows nothing more (no stacks, card, detail or stolen card).
//! Otherwise it is part of what must be the same: Shrink's negative amount is for good, not less. Strength is a buff,
//! negative too, so it must be the same.
//!
//! What it cannot see is what the numbers it compares bring later (the plan, 2.2): an enemy with less HP dies sooner to a
//! hit the player cannot aim, and some deaths make the others stronger (Kaiser Crab's arms); Dark's evoke and Memento
//! Mori hit the enemy with the least HP; Frog Knight's Beetle Charge comes under half its HP; more poison wakes a
//! sleeping enemy sooner (Lagavulin Matriarch, Slumbering Beetle) and brings Terror Eel's Shriek sooner; Meat on the
//! Bone and Red Skull turn on the player at half HP. The user takes those (「99.5%的情況不需要」) but for one fight: the
//! Queen's goes without the rule (「女王戰 不要这条规则」, 「其餘需要」). While the Torch Head Amalgam lives the Queen only
//! buffs it, and with less HP it dies sooner to a hit the player did not mean for it, and then she attacks.
//!
//! Not compared here: the ends of the combat (the pick takes the best of those, and `bound.rs` prunes by them), and the
//! ends a cycle's cash-out makes (`Loops::run_found`), which come seldom: keeping them drops nothing the rule would keep.

use std::collections::HashMap;

use super::state::{State, Status};
use super::turn::mix;
use crate::protocol;
use crate::rt::Str;

/// The encounter that goes without the rule (the user: 「女王戰 不要这条规则」): the Queen is in no other.
const WITHOUT: &str = "QUEEN_BOSS";

/// An end of a turn at the next turn's start, as the rule reads it.
#[derive(Clone, Debug)]
pub struct Shape {
    /// The hash of everything that must be the same.
    rest: u64,
    hp: i32,
    /// Each ally's HP, in the allies' order.
    allies: Vec<i32>,
    /// Each enemy's HP, and its debuffs compared by amount (id, amount), sorted by id.
    enemies: Vec<(i32, Vec<(Str, i32)>)>,
}

/// The shape of a state where a turn's line ended, if the rule compares it: a combat going on, not the Queen's.
pub fn shape(state: &mut State) -> Option<Shape> {
    if state.status != Status::InProgress || state.obs.encounter.is_some_and(|e| e.as_str() == WITHOUT) {
        return None;
    }
    let debuffs = state.enemy_debuffs();
    let mut obs = (*state.obs).clone();
    let hp = std::mem::take(&mut obs.player.hp);
    let allies = obs.allies.iter_mut().map(|ally| std::mem::take(&mut ally.hp)).collect();
    // Not strict, by the user's word: see the module's comment.
    for relic in &mut obs.relics {
        relic.counter = None;
    }
    let mut enemies = Vec::with_capacity(obs.enemies.len());
    for (at, enemy) in obs.enemies.iter_mut().enumerate() {
        let declared = debuffs.get(at).map_or(&[][..], Vec::as_slice);
        let mut compared = Vec::new();
        enemy.powers.retain(|p| {
            let by_amount = declared.iter().any(|d| d.as_str() == p.id.as_str())
                && p.amount >= 0
                && p.stacks.is_none()
                && p.card.is_none()
                && p.detail.is_none()
                && p.stolen.is_none();
            if by_amount {
                compared.push((p.id, p.amount));
            }
            !by_amount
        });
        compared.sort_by(|a, b| a.0.as_str().cmp(b.0.as_str()));
        for intent in enemy.intents.iter_mut().flatten() {
            intent.damage = None;
        }
        enemies.push((std::mem::take(&mut enemy.hp), compared));
    }
    Some(Shape { rest: mix(protocol::state_key(&obs), state.hidden_key), hp, allies, enemies })
}

/// Whether `a` dominates `b`, two shapes with the same rest: no worse anywhere, and better somewhere.
fn dominates(a: &Shape, b: &Shape) -> bool {
    if a.enemies.len() != b.enemies.len() || a.allies.len() != b.allies.len() || a.hp < b.hp {
        return false;
    }
    let amount = |debuffs: &[(Str, i32)], id: &str| debuffs.iter().find(|(d, _)| d.as_str() == id).map_or(0, |(_, n)| *n);
    let mut better = a.hp > b.hp;
    for (a_hp, b_hp) in a.allies.iter().zip(&b.allies) {
        if a_hp < b_hp {
            return false;
        }
        better |= a_hp > b_hp;
    }
    for ((a_hp, a_debuffs), (b_hp, b_debuffs)) in a.enemies.iter().zip(&b.enemies) {
        if a_hp > b_hp {
            return false;
        }
        better |= a_hp < b_hp;
        for (id, _) in a_debuffs.iter().chain(b_debuffs) {
            let (x, y) = (amount(a_debuffs, id.as_str()), amount(b_debuffs, id.as_str()));
            if x < y {
                return false;
            }
            better |= x > y;
        }
    }
    better
}

/// Of a turn's ends by their shapes (None: not compared), the ones another dominates.
pub fn dominated(shapes: &[Option<&Shape>]) -> Vec<bool> {
    let mut out = vec![false; shapes.len()];
    let mut groups: HashMap<u64, Vec<usize>> = HashMap::new();
    for (i, shape) in shapes.iter().enumerate() {
        if let Some(shape) = shape {
            groups.entry(shape.rest).or_default().push(i);
        }
    }
    for group in groups.values().filter(|group| group.len() > 1) {
        for &b in group {
            out[b] = group.iter().any(|&a| a != b && dominates(shapes[a].expect("a shape"), shapes[b].expect("a shape")));
        }
    }
    out
}
