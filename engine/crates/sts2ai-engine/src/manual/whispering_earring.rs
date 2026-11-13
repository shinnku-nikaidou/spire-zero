//! MegaCrit.Sts2.Core.Models.Relics.WhisperingEarring: the relic plays the first turn's cards itself, and for as long
//! as it does, the game's own VakuuCardSelector is on top of the owner's selector stack and answers the card prompts
//! (`using (CardSelectorStack.PushSelector(Owner, new VakuuCardSelector()))`). The member is the game's code; port
//! has it say here that it runs (engine/port.toml, [enter]), and that is the time the selector is on the stack.

use crate::rt::*;

pub fn enter(_name: &'static str, c: &mut Combat) {
    c.x.vakuu += 1;
}

pub fn leave(c: &mut Combat) {
    c.x.vakuu -= 1;
}
