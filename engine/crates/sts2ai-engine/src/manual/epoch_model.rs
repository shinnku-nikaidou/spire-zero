//! MegaCrit.Sts2.Core.Timeline.EpochModel: the id of an epoch class. The game makes one object of every epoch class
//! when it starts and keeps each one's Id by class; here the same table is made once, the first time it is asked.

use crate::r#gen as g;
use crate::r#gen::kinds::*;
use crate::r#gen::state::EpochModelData;
use crate::rt::*;
use std::collections::HashMap;
use std::sync::OnceLock;

fn ids() -> &'static HashMap<Kind, Str> {
    static IDS: OnceLock<HashMap<Kind, Str>> = OnceLock::new();
    IDS.get_or_init(|| {
        // An epoch's Id is a constant of its class: any object of the class says it.
        let mut scratch = super::canon::combat();
        let mut ids = HashMap::new();
        for &kind in ALL_KINDS.iter() {
            if kind != Kind::EpochModel && is_subclass(kind, Kind::EpochModel) {
                let epoch = EpochModelData::alloc(kind, &mut scratch);
                ids.insert(kind, g::dispatch::epoch_model::id(epoch, &mut scratch));
            }
        }
        ids
    })
}

/// C#: EpochModel.GetId<T>.
pub fn get_id__0(k_t: Kind, _c: &mut Combat) -> Str {
    *ids()
        .get(&k_t)
        .unwrap_or_else(|| panic!("{k_t:?} is no epoch class"))
}

/// C#: EpochModel.GetId(Type).
pub fn get_id__kind(t: Kind, c: &mut Combat) -> Str {
    get_id__0(t, c)
}
