//! MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet: the one member that waits.

use crate::r#gen::kinds::*;
use crate::rt::*;

/// C#: ActionQueueSet.BecameEmpty: here the queues are empty when the call that ran them returns.
pub fn became_empty(_me: ActionQueueSet, _c: &mut Combat) -> R<()> {
    Ok(())
}
