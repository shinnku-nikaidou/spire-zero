//! MegaCrit.Sts2.Core.CardSelection.CardSelectorStack: who answers a player's card prompts. In the game a player
//! answers on the screen: the stack is empty, and `CardSelectCmd` pauses the action or hook that asked
//! (`SignalPlayerChoiceBegun`) before it asks the screen; the simulator's selector is the local one, which answers in
//! the screen's place. The game itself puts one selector on the stack, for the time Whispering Earring plays cards
//! (whispering_earring.rs), and that one answers at once. The stack is therefore not kept as objects: the engine's
//! one selector (i_card_selector.rs) stands for both, and knows of that one case.

use crate::rt::*;

/// C#: CardSelectorStack.GetSelector: the game's own selector while Whispering Earring plays cards (the stack itself
/// stands for it), otherwise none.
pub fn get_selector(me: Obj, _player: Obj, c: &mut Combat) -> Obj {
    if c.x.vakuu > 0 { me } else { Obj::NULL }
}

/// C#: CardSelectorStack.LocalSelector: the engine's selector, which answers where the player's screen does.
pub fn local_selector(me: Obj, _c: &mut Combat) -> Obj {
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
