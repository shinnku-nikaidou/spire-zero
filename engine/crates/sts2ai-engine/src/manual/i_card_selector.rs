//! MegaCrit.Sts2.Core.TestSupport.ICardSelector: the one the game asks at a card prompt. The simulator's
//! (PromptSelector.cs) holds the game where it asked until the driver answers. Here a prompt with no answer yet stops
//! the step (`Stop::Choice`) with what was asked left in `Extra::asked`; world.rs runs the step again from the state
//! before it with the answers so far, and the prompt then gets its answer from `Choices`.

use crate::r#gen as g;
use crate::r#gen::kinds::*;
use crate::prompt::Asked;
use crate::rt::*;

/// C#: ICardSelector.GetSelectedCards. An answer is places in `options`, the game's own list.
pub fn get_selected_cards(
    _me: Obj,
    options: Vec<CardModel>,
    min_select: i32,
    max_select: i32,
    c: &mut Combat,
) -> R<Vec<CardModel>> {
    // The game's own selector is on top of the stack (whispering_earring.rs): it answers, and the simulator's is not asked.
    if c.x.vakuu > 0 {
        return g::models_relics::vakuu_card_selector::get_selected_cards(
            Obj::NULL,
            options,
            min_select,
            max_select,
            c,
        );
    }
    // A hook whose action the executor started at once asks here and waits (manual/hook_player_choice_context.rs).
    let asking = c.x.hooks.asking;
    if !asking.is_null() {
        return super::hook_player_choice_context::ask(asking, options, min_select, max_select, c);
    }
    let number = c.x.prompts;
    c.x.prompts += 1;
    if let Some(picks) = c.choices.take() {
        return Ok(picks.iter().map(|&i| options[i as usize]).collect());
    }
    c.x.asked = Some(asked(options, min_select, max_select, number, c));
    Err(Stop::Choice)
}

/// What a prompt asks, for the world to show: the running CardSelectCmd entry's reasons and the game's options.
pub fn asked(options: Vec<CardModel>, min_select: i32, max_select: i32, number: i32, c: &mut Combat) -> Asked {
    let entry =
        c.x.entry
            .clone()
            .expect("a card prompt outside the CardSelectCmd entries");
    Asked {
        entry: entry.name,
        pile: entry.pile,
        source: entry.source,
        cancelable: entry.cancelable,
        prompt_key: entry.prompt_key,
        enchantment: entry.enchantment,
        enchantment_amount: entry.enchantment_amount,
        cards: options,
        min_select,
        max_select,
        number,
    }
}
