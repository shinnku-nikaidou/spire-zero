//! The canonical models (the game's ModelDb): one object per model class. The game makes them by reflection when it
//! starts (ModelDb.Init, then InitIds); here they are made once per process from the list port wrote
//! (gen/model_table.rs), and every combat starts with them.

use crate::r#gen as g;
use crate::r#gen::kinds::{ALL_KINDS, Kind};
use crate::r#gen::model_table::{MODEL_KINDS, model_info, new_model};
use crate::r#gen::state::{AbstractModelData, ModelId};
use crate::rt::{Combat, Obj, Str};
use std::collections::HashMap;
use std::sync::OnceLock;

/// The canonical models: by class, and by id.
#[derive(Clone, Debug, Default)]
pub struct Table {
    /// Indexed by `Kind as usize`; null for a class that has none.
    pub by_kind: Vec<Obj>,
    pub by_id: HashMap<(Str, Str), Obj>,
}

struct Canon {
    /// A combat with the canonical models and nothing else.
    combat: Combat,
    table: Table,
}

static CANON: OnceLock<Canon> = OnceLock::new();

fn canon() -> &'static Canon {
    CANON.get_or_init(build)
}

fn build() -> Canon {
    let mut c = Combat::default();
    c.a.set_canon_build(true);
    c.x.canon_in_build = Some(Table { by_kind: vec![Obj::NULL; ALL_KINDS.len()], by_id: HashMap::new() });
    for kind in MODEL_KINDS {
        model(kind, &mut c);
    }
    // C#: ModelDb.InitIds.
    let made: Vec<Obj> = c.x.canon_in_build.as_ref().unwrap().by_kind.iter().copied().filter(|o| !o.is_null()).collect();
    for o in made {
        let info = model_info(g::kinds::kind_of(o, &c)).unwrap();
        let d = AbstractModelData::of_mut(o, &mut c);
        d.category_sorting_id = info.category_sorting_id;
        d.entry_sorting_id = info.entry_sorting_id;
    }
    c.a.set_canon_build(false);
    let table = c.x.canon_in_build.take().unwrap();
    Canon { combat: c, table }
}

/// A combat with the canonical models and nothing else: what every combat starts from.
pub fn combat() -> Combat {
    canon().combat.clone()
}

/// The id of a model class. C#: ModelDb.GetId(type).
pub fn id_of(kind: Kind) -> ModelId {
    let info = model_info(kind).unwrap_or_else(|| panic!("{kind:?} is not a model class"));
    ModelId { category: Str::lit(info.category), entry: Str::lit(info.entry) }
}

/// The canonical model of a class.
pub fn of(kind: Kind) -> Obj {
    let o = canon().table.by_kind[kind as usize];
    if o.is_null() {
        panic!("no canonical model of {kind:?}");
    }
    o
}

/// The canonical model of a class, for code that runs with a combat: while the canonical models are being built, one
/// that another needs is made at that moment.
pub fn model(kind: Kind, c: &mut Combat) -> Obj {
    let Some(table) = &c.x.canon_in_build else { return of(kind) };
    let known = table.by_kind[kind as usize];
    if !known.is_null() {
        return known;
    }
    let o = new_model(kind, c);
    let id = id_of(kind);
    let table = c.x.canon_in_build.as_mut().unwrap();
    table.by_kind[kind as usize] = o;
    table.by_id.insert((id.category, id.entry), o);
    o
}

/// The canonical model with an id, or null.
pub fn by_id(id: &ModelId, c: &Combat) -> Obj {
    let table = c.x.canon_in_build.as_ref().unwrap_or_else(|| &canon().table);
    table.by_id.get(&(id.category, id.entry)).copied().unwrap_or(Obj::NULL)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Every model class of the engine has its canonical model, with the id the game gives the class.
    #[test]
    fn every_model_class_is_made() {
        let c = combat();
        for kind in MODEL_KINDS {
            let o = of(kind);
            assert!(o.is_canon());
            assert_eq!(g::kinds::kind_of(o, &c), kind);
            assert_eq!(AbstractModelData::of(o, &c).id, id_of(kind));
        }
    }
}
