//! MegaCrit.Sts2.Core.Models.ModelDb: the members that the game does by reflection. The rest of the class is translated.

use super::canon;
use crate::r#gen::kinds::*;
use crate::r#gen::state::ModelId;
use crate::rt::*;

/// C#: ModelDb.Get(Type)
pub fn get__kind(type_: Kind, c: &mut Combat) -> AbstractModel {
    canon::model(type_, c)
}

/// C#: ModelDb.Get<T>()
pub fn get__0(k_t: Kind, c: &mut Combat) -> Obj {
    canon::model(k_t, c)
}

/// C#: ModelDb.GetByIdOrNull<T>(id)
pub fn get_by_id_or_null(k_t: Kind, id: ModelId, c: &mut Combat) -> Obj {
    let o = canon::by_id(&id, c);
    if !o.is_null() && !is_subclass(kind_of(o, c), k_t) {
        panic!("ModelDb.GetByIdOrNull: {:?} is not a {:?}", o, k_t);
    }
    o
}

/// C#: ModelDb.GetId(Type)
pub fn get_id__kind(type_: Kind, _c: &mut Combat) -> ModelId {
    canon::id_of(type_)
}

/// C#: ModelDb.GetId<T>()
pub fn get_id__0(k_t: Kind, _c: &mut Combat) -> ModelId {
    canon::id_of(k_t)
}

/// C#: ModelDb.Contains(Type)
pub fn contains(type_: Kind, c: &mut Combat) -> bool {
    !canon::by_id(&canon::id_of(type_), c).is_null()
}
