//! The actions search tries at a state, in its order (C#: PimcBrain.Actions, PimcBrain.Ordered, PromptAnswers,
//! CardChoice.Answers).

use super::state::{Action, State};
use crate::protocol::{self, CardChoice, CombatObservation};

/// C#: PimcBrain.Foul. The Foul Potion is never thrown in a fight: the user's rule (2026-11-16).
pub const FOUL: &str = "FOUL_POTION";

/// C#: PimcBrain.Actions. Where the player can play, the plays (`ordered`); where a card prompt waits, its answers, as
/// many as `most` nodes can try (`answers`), and whether answers were left out.
pub fn actions(state: &mut State, most: i64) -> (Vec<Action>, bool) {
    let obs = state.obs.clone();
    match &obs.prompt {
        Some(prompt) => answers(prompt, most),
        None => (ordered(&state.legal(), &obs), false),
    }
}

/// C#: PimcBrain.Ordered. Ending the turn first (the fallback a spent budget leaves), then cards in hand order, then
/// potions; of the actions that are the same, only the first: the same card by its card key, or the same potion, at
/// the same target. Never the Foul Potion.
pub fn ordered(legal: &[Action], obs: &CombatObservation) -> Vec<Action> {
    let rank = |a: &Action| match a {
        Action::EndTurn => 0,
        Action::Play { .. } => 1,
        _ => 2,
    };
    let mut sorted: Vec<&Action> = legal.iter().collect();
    // A stable sort, as LINQ's OrderBy.
    sorted.sort_by_key(|a| rank(a));
    let mut seen: Vec<Same> = Vec::new();
    let mut out = Vec::new();
    for action in sorted {
        if let Action::Potion { slot, .. } = action
            && obs.potions[*slot as usize].is_some_and(|p| p.as_str() == FOUL)
        {
            continue;
        }
        let same = same(action, obs);
        if seen.contains(&same) {
            continue;
        }
        seen.push(same);
        out.push(action.clone());
    }
    out
}

/// C#: PimcBrain.Same. What makes an action the one it is to search.
#[derive(PartialEq)]
enum Same {
    Play(Vec<u8>, Option<u32>),
    Potion(Option<&'static str>, Option<u32>),
    Other(Action),
}

fn same(action: &Action, obs: &CombatObservation) -> Same {
    match action {
        Action::Play { card, target } => {
            let card = obs
                .hand
                .iter()
                .find(|c| c.uid == Some(*card))
                .unwrap_or_else(|| panic!("{} plays a card not in the hand", action.text()));
            Same::Play(protocol::card_key(card), *target)
        }
        Action::Potion { slot, target } => Same::Potion(obs.potions[*slot as usize].map(|p| p.as_str()), *target),
        other => Same::Other(other.clone()),
    }
}

/// C#: PromptAnswers.Of. The prompt's answers in its own order (`CardChoice.Answers`), at most `most` and at least one,
/// and whether answers were left out.
pub fn answers(prompt: &CardChoice, most: i64) -> (Vec<Action>, bool) {
    let most = most.max(1);
    // One more than asked tells whether any were left out; there is none more than int.MaxValue.
    let take = if most >= i32::MAX as i64 { most } else { most + 1 };
    let mut out = Vec::new();
    every_answer(prompt, &mut |picks| {
        out.push(Action::pick(picks));
        (out.len() as i64) < take
    });
    let cut = out.len() as i64 > most;
    if cut {
        out.truncate(most as usize);
    }
    (out, cut)
}

/// C#: CardChoice.Answers. Every answer the prompt takes, once per choice of cards the player can tell apart (k cards of
/// a group are its first k), the fewest picks first, then in the options' order. `take` gets each, and says whether
/// to go on.
fn every_answer(prompt: &CardChoice, take: &mut dyn FnMut(Vec<i32>) -> bool) {
    // C#: CardChoice.Groups: the options by card key, in the order each key first shows, each group's indexes in order.
    let mut keys: Vec<Vec<u8>> = Vec::new();
    let mut groups: Vec<Vec<i32>> = Vec::new();
    for (i, option) in prompt.options.iter().enumerate() {
        let key = protocol::card_key(option);
        match keys.iter().position(|k| *k == key) {
            Some(g) => groups[g].push(i as i32),
            None => {
                keys.push(key);
                groups.push(vec![i as i32]);
            }
        }
    }
    let most = prompt.max.min(prompt.options.len() as i32);
    let mut size = prompt.min;
    while size <= most {
        if !choose(&groups, 0, size, &mut Vec::new(), take) {
            return;
        }
        size += 1;
    }
}

/// C#: CardChoice.Choose. Every choice of `size` cards from the groups at `from` and after, after `picked`; false once
/// `take` says to stop.
fn choose(groups: &[Vec<i32>], from: usize, size: i32, picked: &mut Vec<i32>, take: &mut dyn FnMut(Vec<i32>) -> bool) -> bool {
    if size == 0 {
        return take(picked.clone());
    }
    for g in from..groups.len() {
        let mut k = size.min(groups[g].len() as i32);
        while k >= 1 {
            let at = picked.len();
            picked.extend_from_slice(&groups[g][..k as usize]);
            let go_on = choose(groups, g + 1, size - k, picked, take);
            picked.truncate(at);
            if !go_on {
                return false;
            }
            k -= 1;
        }
    }
    true
}
