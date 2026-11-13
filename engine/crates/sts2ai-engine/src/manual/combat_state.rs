//! MegaCrit.Sts2.Core.Combat.CombatState: the one member that waits.

use crate::r#gen as g;
use crate::r#gen::kinds::*;
use crate::rt::*;

/// C#: CombatState.GetCreatureAsync. The game waits for a creature that another player's game has yet to make; with
/// one player a creature that is not there will never be.
pub fn get_creature_async(
    me: CombatState,
    combat_id: Option<u32>,
    _timeout_sec: f64,
    c: &mut Combat,
) -> R<Creature> {
    if combat_id.is_none() {
        return Ok(Obj::NULL);
    }
    Ok(g::combat::combat_state::get_creature(me, combat_id, c))
}
