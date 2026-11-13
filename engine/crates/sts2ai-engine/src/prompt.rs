//! A card prompt in the middle of an action, as the simulator shows it: `CardChoices.Canonical` of
//! src/Sts2Ai.GameBridge/CardChoices.cs, `ChoiceContexts.Read` and `ChoiceContexts.Bounds` of ChoiceContexts.cs, and
//! the part of `PromptSelector.GetSelectedCards` (src/Sts2Ai.SimWorker/Simulation/PromptSelector.cs) that makes the
//! choice of what the game asked.
//!
//! The rules side (world.rs) stops an action where the game's selector is asked, and says what was asked (`Asked`).
//! This file turns that into the protocol's `CardChoice`, and gives for each option its place in the game's own list,
//! which is how an answer ("pick:0,2", places in the canonical order) goes back to the game's code.

use crate::r#gen as g;
use crate::r#gen::enums::*;
use crate::r#gen::kinds::*;
use crate::observe;
use crate::protocol::{CardChoice, CardView, ChoiceContext, ModifierView, card_key, snake};
use crate::rt::{self, Combat, Str};

/// What the game asked at a card prompt: the arguments of the outermost `CardSelectCmd.From*` call that
/// `ChoiceContexts.Read` reads, and what the selector was given (`ICardSelector.GetSelectedCards`).
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Asked {
    /// The outermost entry's name: "FromHand", "FromCombatPile", "FromChooseACardScreen", ..
    pub entry: Str,
    /// The type of the entry's CardPile argument, for an entry that has one.
    pub pile: Option<PileType>,
    /// The entry's `source` argument, or else its PlayerChoiceContext's LastInvolvedModel; null for neither.
    pub source: AbstractModel,
    /// The entry's prefs' Cancelable, or else whether its `canSkip` argument is true.
    pub cancelable: bool,
    /// The entry's prefs' Prompt as "table.key" (`LocTable`, `LocEntryKey`); null for an entry with no prefs.
    pub prompt_key: Str,
    /// The entry's EnchantmentModel argument, with its `amount` argument; null for an entry with none.
    pub enchantment: EnchantmentModel,
    pub enchantment_amount: i32,
    /// The options the selector was given, in the game's order.
    pub cards: Vec<CardModel>,
    /// How many the game asked the selector for.
    pub min_select: i32,
    pub max_select: i32,
    /// How many prompts the selector was asked before this one, since the session of the world began
    /// (`PromptSelector.Prompts`): the seed of the tie-break between cards that look the same.
    pub number: i32,
}

/// C#: ChoiceContexts.Read. Why the game asks.
pub fn context(asked: &Asked, c: &mut Combat) -> ChoiceContext {
    ChoiceContext {
        purpose: match asked.pile {
            Some(pile) => Str::from_string(format!("{}_pile", snake(rt::to_s(pile, c).as_str()))),
            None => Str::from_string(snake(
                asked
                    .entry
                    .as_str()
                    .strip_prefix("From")
                    .expect("a CardSelectCmd entry is named From.."),
            )),
        },
        source: if rt::is_null(&asked.source) {
            None
        } else {
            Some(g::models::abstract_model::id(asked.source, c).entry)
        },
        cancelable: asked.cancelable,
        in_combat: {
            let manager = g::combat::combat_manager::instance(c);
            g::combat::combat_manager::is_in_progress(manager, c)
        },
        prompt_key: if asked.prompt_key.is_null() { None } else { Some(asked.prompt_key) },
        enchantment: if rt::is_null(&asked.enchantment) {
            None
        } else {
            Some(ModifierView {
                id: g::models::abstract_model::id(asked.enchantment, c).entry,
                amount: asked.enchantment_amount,
                extra: None,
            })
        },
    }
}

/// C#: ChoiceContexts.Bounds. How many cards the answer may have, as the prompt's screen allows, given what the game
/// asks the selector for.
pub fn bounds(context: &ChoiceContext, min_select: i32, max_select: i32, options: i32) -> (i32, i32) {
    let max = max_select.min(options);
    let min = if context.cancelable {
        0
    } else if context.purpose.as_str() == "choose_a_card_screen" {
        1
    } else {
        min_select
    };
    (min.min(max), max)
}

/// C#: CardChoices.Canonical, with the context and the bounds PromptSelector.GetSelectedCards gives it. The prompt
/// with its options sorted by card key, and for each canonical index the index into the game's list. Cards with one
/// key are put in an order drawn from the prompt's number, never the game's order, which can show hidden state.
pub fn canonical(asked: &Asked, c: &mut Combat) -> (CardChoice, Vec<usize>) {
    let context = context(asked, c);
    let (min, max) = bounds(&context, asked.min_select, asked.max_select, asked.cards.len() as i32);
    let deck = context.purpose.as_str().starts_with("deck_");
    let mut tie_break = NetRandom::new(asked.number);
    let mut options: Vec<(Vec<u8>, i32, usize, CardView)> = Vec::with_capacity(asked.cards.len());
    for (index, card) in asked.cards.iter().enumerate() {
        let view = if deck {
            observe::deck_card(*card, c)
        } else {
            observe::card(*card, false, c)
        };
        options.push((card_key(&view), tie_break.next(), index, view));
    }
    // C#: OrderBy(key, StringComparer.Ordinal).ThenBy(tie), a stable sort. A key is ASCII: its bytes order as its text.
    options.sort_by(|a, b| a.0.cmp(&b.0).then(a.1.cmp(&b.1)));
    let game_index = options.iter().map(|option| option.2).collect();
    let choice = CardChoice {
        options: options.into_iter().map(|option| option.3).collect(),
        min,
        max,
        context,
    };
    (choice, game_index)
}

/// C#: `new Random(seed)` and its `Next()`: .NET's generator for a given seed (the subtractive generator of
/// Net5CompatSeedImpl), which the simulator's tie-break draws from.
pub struct NetRandom {
    seed_array: [i32; 56],
    inext: usize,
    inextp: usize,
}

impl NetRandom {
    pub fn new(seed: i32) -> NetRandom {
        let mut seed_array = [0i32; 56];
        let subtraction = if seed == i32::MIN { i32::MAX } else { seed.abs() };
        let mut mj = 161803398 - subtraction;
        seed_array[55] = mj;
        let mut mk = 1;
        let mut ii = 0;
        for _ in 1..55 {
            ii += 21;
            if ii >= 55 {
                ii -= 55;
            }
            seed_array[ii] = mk;
            mk = mj - mk;
            if mk < 0 {
                mk += i32::MAX;
            }
            mj = seed_array[ii];
        }
        for _ in 1..5 {
            for i in 1..56 {
                let mut n = i + 30;
                if n >= 55 {
                    n -= 55;
                }
                seed_array[i] = seed_array[i].wrapping_sub(seed_array[1 + n]);
                if seed_array[i] < 0 {
                    seed_array[i] += i32::MAX;
                }
            }
        }
        NetRandom {
            seed_array,
            inext: 0,
            inextp: 21,
        }
    }

    /// C#: Random.Next(). A number in [0, int.MaxValue).
    #[allow(clippy::should_implement_trait)]
    pub fn next(&mut self) -> i32 {
        self.inext += 1;
        if self.inext >= 56 {
            self.inext = 1;
        }
        self.inextp += 1;
        if self.inextp >= 56 {
            self.inextp = 1;
        }
        let mut value = self.seed_array[self.inext].wrapping_sub(self.seed_array[self.inextp]);
        if value == i32::MAX {
            value -= 1;
        }
        if value < 0 {
            value += i32::MAX;
        }
        self.seed_array[self.inext] = value;
        value
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The first numbers of `new Random(seed)` on .NET 9, by seed.
    #[test]
    fn net_random_is_dotnets() {
        let expected: [(i32, [i32; 6]); 8] = [
            (0, [1559595546, 1755192844, 1649316166, 1198642031, 442452829, 1200195957]),
            (1, [534011718, 237820880, 1002897798, 1657007234, 1412011072, 929393559]),
            (2, [1655911537, 867932563, 356479430, 2115372437, 234085668, 658591161]),
            (7, [822959691, 1871007331, 1419354884, 112231158, 786909589, 1452062818]),
            (12345, [143337951, 150666398, 1663795458, 1097663221, 1712597933, 1776631026]),
            (i32::MAX, [1559595546, 1755192844, 1649316172, 1198642031, 442452829, 1200195955]),
            (-5, [726643700, 610783965, 564707973, 1342984399, 995276750, 1993667614]),
            (i32::MIN, [1559595546, 1755192844, 1649316172, 1198642031, 442452829, 1200195955]),
        ];
        for (seed, numbers) in expected {
            let mut random = NetRandom::new(seed);
            let drawn: Vec<i32> = (0..6).map(|_| random.next()).collect();
            assert_eq!(drawn, numbers, "seed {seed}");
        }
    }

    #[test]
    fn bounds_are_the_screens() {
        let context = |purpose: &'static str, cancelable| ChoiceContext {
            purpose: Str::lit(purpose),
            cancelable,
            ..Default::default()
        };
        assert_eq!(bounds(&context("hand", false), 1, 1, 5), (1, 1));
        assert_eq!(bounds(&context("hand", false), 2, 2, 1), (1, 1));
        assert_eq!(bounds(&context("hand", true), 1, 3, 5), (0, 3));
        assert_eq!(bounds(&context("choose_a_card_screen", false), 0, 1, 3), (1, 1));
        assert_eq!(bounds(&context("choose_a_card_screen", true), 0, 1, 3), (0, 1));
    }
}
