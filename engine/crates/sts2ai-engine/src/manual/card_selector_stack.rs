//! MegaCrit.Sts2.Core.CardSelection.CardSelectorStack: who answers a player's card prompts. In the simulator a
//! session's selector is at the bottom of the player's stack for the whole combat; the game itself puts one selector
//! on top of it, for the time Whispering Earring plays cards (whispering_earring.rs). The stack is therefore not kept
//! as objects: there is always a selector, the engine's (i_card_selector.rs), which knows of that one case.

use crate::rt::*;

/// C#: CardSelectorStack.GetSelector. Never null here; the stack itself stands for the selector.
pub fn get_selector(me: Obj, _player: Obj, _c: &mut Combat) -> Obj {
    me
}

/// C#: CardSelectorStack.PushSelector. The one caller is Whispering Earring, whose time on the stack is told by its
/// member (whispering_earring.rs); what is given back is the scope, which nothing reads.
pub fn push_selector(_me: Obj, _player: Obj, _selector: Obj, c: &mut Combat) -> Obj {
    assert!(
        c.x.vakuu > 0,
        "CardSelectorStack.PushSelector outside Whispering Earring"
    );
    Obj::NULL
}
