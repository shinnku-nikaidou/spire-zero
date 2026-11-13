//! MegaCrit.Sts2.Core.Commands.CardSelectCmd: the entries every card prompt goes through are the game's own code;
//! port has each of them say here that it runs (engine/port.toml, [enter]). Only an entry's arguments say why the
//! game asks, so the outermost entry's are kept while it runs, as the simulator's ChoiceContexts.cs keeps them.

use crate::r#gen as g;
use crate::r#gen::enums::PileType;
use crate::r#gen::kinds::*;
use crate::r#gen::state::CardSelectorPrefs;
use crate::rt::*;

/// What the outermost running entry was called with (ChoiceContexts.Read).
#[derive(Clone, Debug, Default)]
pub struct Entry {
    pub name: Str,
    pub pile: Option<PileType>,
    pub source: AbstractModel,
    pub cancelable: bool,
    pub prompt_key: Str,
    pub enchantment: EnchantmentModel,
    pub enchantment_amount: i32,
}

/// An entry starts. An entry called by another does not count: the outermost one says why.
#[allow(clippy::too_many_arguments)]
pub fn enter(
    name: &'static str,
    pile: CardPile,
    source: AbstractModel,
    context: PlayerChoiceContext,
    prefs: Option<CardSelectorPrefs>,
    enchantment: EnchantmentModel,
    amount: Option<i32>,
    can_skip: Option<bool>,
    c: &mut Combat,
) {
    c.x.entries += 1;
    if c.x.entries > 1 {
        return;
    }
    let source = if !source.is_null() {
        source
    } else if !context.is_null() {
        g::game_actions_multiplayer::player_choice_context::last_involved_model(context, c)
    } else {
        Obj::NULL
    };
    let entry = Entry {
        name: Str::lit(name),
        pile: if pile.is_null() {
            None
        } else {
            Some(g::entities_cards::card_pile::type_(pile, c))
        },
        source,
        cancelable: match &prefs {
            Some(prefs) => prefs.cancelable,
            None => can_skip == Some(true),
        },
        prompt_key: match &prefs {
            Some(prefs) if !prefs.prompt.loc_table.is_null() => Str::concat(&[
                prefs.prompt.loc_table.clone(),
                Str::lit("."),
                prefs.prompt.loc_entry_key.clone(),
            ]),
            _ => Str::NULL,
        },
        enchantment,
        enchantment_amount: amount.unwrap_or(0),
    };
    c.x.entry = Some(entry);
}

/// An entry ends, by its return or by a stop.
pub fn leave(c: &mut Combat) {
    c.x.entries -= 1;
    if c.x.entries == 0 {
        c.x.entry = None;
    }
}
