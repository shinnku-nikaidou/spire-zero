//! MegaCrit.Sts2.Core.Modding.ModHelper: what mods add to the game. The engine is the game with no mods.

use crate::r#gen::kinds::Kind;
use crate::rt::*;

/// C#: ModHelper.ConcatModelsFromMods: a pool's models and the ones mods add to that pool.
pub fn concat_models_from_mods(
    _model_type: Kind,
    _pool_model: Obj,
    pool: Vec<Obj>,
    _c: &mut Combat,
) -> Vec<Obj> {
    pool
}
