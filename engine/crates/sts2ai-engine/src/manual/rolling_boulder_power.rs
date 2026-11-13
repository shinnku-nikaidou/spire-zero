//! MegaCrit.Sts2.Core.Models.Powers.RollingBoulderPower: its damage. The shipped game rolls the boulder across the
//! screen and damages each enemy as the boulder reaches it; the game's test mode deals one hit to all enemies instead,
//! where every enemy loses its HP before the first one's hooks run. The simulator keeps the shipped rule by hitting
//! the enemies one by one in list order (its RuleFidelity), and so does the engine.

use crate::r#gen as g;
use crate::r#gen::enums::ValueProp;
use crate::r#gen::kinds::*;
use crate::rt::*;

/// C#: RollingBoulderPower.DoDamage.
pub fn do_damage(
    me: RollingBoulderPower,
    choice_context: PlayerChoiceContext,
    targets: Vec<Creature>,
    c: &mut Combat,
) -> R<Vec<DamageResult>> {
    let mut results = Vec::new();
    for group in if targets.len() <= 1 {
        vec![targets]
    } else {
        targets.into_iter().map(|t| vec![t]).collect()
    } {
        results.extend(
            g::commands::creature_cmd::damage__player_choice_context_vec_creature_dec_value_prop_creature(
                choice_context,
                group,
                Dec::from_i32(g::models::power_model::amount(me, c)),
                ValueProp::Unpowered,
                g::models::power_model::owner(me, c),
                c,
            )?,
        );
    }
    Ok(results)
}
