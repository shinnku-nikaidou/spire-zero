//! MegaCrit.Sts2.Core.Entities.Cards.CardPile: its ContentsChanged event. Events are presentation in the engine
//! (DESIGN.md), and this one has a subscriber that is game code: the table of the combat's cards
//! (NetCombatCardDb.StartCombat) gives a card its id when it first shows up in a pile it subscribed to. The table keeps
//! its subscriptions as state (`_subscriptions`: the piles), so who is subscribed is read there.

use crate::r#gen as g;
use crate::r#gen::kinds::*;
use crate::r#gen::state::NetCombatCardDbData;
use crate::rt::*;

/// C#: CardPile.InvokeContentsChanged.
pub fn invoke_contents_changed(me: CardPile, c: &mut Combat) {
    let table = c.s.net_combat_card_db__instance;
    if NetCombatCardDbData::of(table, c)
        ._subscriptions
        .iter()
        .any(|s| s.pile == me)
    {
        g::game_actions_multiplayer::net_combat_card_db::on_pile_contents_changed(table, me, c);
    }
}
