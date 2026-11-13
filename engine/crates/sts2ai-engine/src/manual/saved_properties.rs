//! SavedProperties.FromInternal and FillInternal. The game reads and writes a model's [SavedProperty] properties by
//! reflection; here port has written the table of them with the code for each (gen/saved.rs), and these two walk it
//! the way the game's code walks the properties.
use crate::r#gen as g;
use crate::r#gen::kinds::*;
use crate::r#gen::state::*;
use crate::rt::*;

/// The value of a saved property, as one of the kinds SavedProperties keeps. An enum is its number.
pub enum Saved {
    Null,
    Int(i32),
    Ints(Vec<i32>),
    Bool(bool),
    Str(Str),
    ModelId(ModelId),
    Card(SerializableCard),
    Cards(Vec<SerializableCard>),
}

impl Saved {
    pub fn of_text(s: Str) -> Saved {
        if s.is_null() { Saved::Null } else { Saved::Str(s) }
    }
    pub fn of_id(id: ModelId) -> Saved {
        if is_null(&id) { Saved::Null } else { Saved::ModelId(id) }
    }
    pub fn of_card(card: SerializableCard) -> Saved {
        if is_null(&card) { Saved::Null } else { Saved::Card(card) }
    }
    pub fn int(self) -> i32 {
        match self {
            Saved::Int(v) => v,
            _ => panic!("a saved property of another type than its value"),
        }
    }
    pub fn ints(self) -> Vec<i32> {
        match self {
            Saved::Ints(v) => v,
            _ => panic!("a saved property of another type than its value"),
        }
    }
    pub fn bool(self) -> bool {
        match self {
            Saved::Bool(v) => v,
            _ => panic!("a saved property of another type than its value"),
        }
    }
    pub fn str(self) -> Str {
        match self {
            Saved::Str(v) => v,
            _ => panic!("a saved property of another type than its value"),
        }
    }
    pub fn id(self) -> ModelId {
        match self {
            Saved::ModelId(v) => v,
            _ => panic!("a saved property of another type than its value"),
        }
    }
    pub fn card(self) -> SerializableCard {
        match self {
            Saved::Card(v) => v,
            _ => panic!("a saved property of another type than its value"),
        }
    }
    pub fn cards(self) -> Vec<SerializableCard> {
        match self {
            Saved::Cards(v) => v,
            _ => panic!("a saved property of another type than its value"),
        }
    }
}

/// One saved property of a class: its name, its attribute's condition (SerializationCondition's number), and the code
/// that reads and writes it.
pub struct Prop {
    pub name: &'static str,
    pub condition: i32,
    pub get: fn(Obj, &mut Combat) -> Saved,
    pub set: fn(Obj, Saved, &mut Combat),
}

/// C#: SerializationConditionExtensions.ShouldSerialize, for the two conditions the game's properties carry.
fn should_serialize(prop: &Prop, value: &Saved) -> bool {
    match prop.condition {
        // AlwaysSave
        0 => true,
        // SaveIfNotTypeDefault: not the default of the property's type (0, false, null).
        2 => !matches!(value, Saved::Null | Saved::Int(0) | Saved::Bool(false)),
        other => panic!("{}: the serialization condition {other}, which no property of the game carried", prop.name),
    }
}

/// C#: SavedProperties.FromInternal
pub fn from_internal(model: Obj, _id: ModelId, c: &mut Combat) -> SavedProperties {
    let saved = g::saves_runs::saved_properties::new(c);
    for prop in g::saved::props(kind_of(model, c)) {
        let value = (prop.get)(model, c);
        if !should_serialize(prop, &value) {
            continue;
        }
        let name = Str::lit(prop.name);
        let d = SavedPropertiesData::of_mut(saved, c);
        match value {
            // The game logs a warning and saves nothing.
            Saved::Null => {}
            Saved::Int(value) => {
                d.ints__set = true;
                d.ints.push(SavedPropertiesSavedProperty { name, value });
            }
            Saved::Ints(value) => {
                d.int_arrays__set = true;
                d.int_arrays.push(SavedPropertiesSavedProperty { name, value });
            }
            Saved::ModelId(value) => {
                d.model_ids__set = true;
                d.model_ids.push(SavedPropertiesSavedProperty { name, value });
            }
            Saved::Bool(value) => {
                d.bools__set = true;
                d.bools.push(SavedPropertiesSavedProperty { name, value });
            }
            Saved::Str(value) => {
                d.strings__set = true;
                d.strings.push(SavedPropertiesSavedProperty { name, value });
            }
            Saved::Card(value) => {
                d.cards__set = true;
                d.cards.push(SavedPropertiesSavedProperty { name, value });
            }
            Saved::Cards(value) => {
                d.card_arrays__set = true;
                d.card_arrays.push(SavedPropertiesSavedProperty { name, value });
            }
        }
    }
    if !g::saves_runs::saved_properties::any(saved, c) {
        return Obj::NULL;
    }
    saved
}

/// C#: SavedProperties.FillInternal. The game looks each name up among the model's properties and skips one it does
/// not find; the names here are those FromInternal wrote for the same class.
pub fn fill_internal(me: SavedProperties, model: Obj, c: &mut Combat) {
    let props = g::saved::props(kind_of(model, c));
    let set = |name: Str, value: Saved, c: &mut Combat| {
        if let Some(prop) = props.iter().find(|p| Str::lit(p.name) == name) {
            (prop.set)(model, value, c);
        }
    };
    let d = SavedPropertiesData::of(me, c).clone();
    for p in d.ints {
        set(p.name, Saved::Int(p.value), c);
    }
    for p in d.int_arrays {
        set(p.name, Saved::Ints(p.value), c);
    }
    for p in d.bools {
        set(p.name, Saved::Bool(p.value), c);
    }
    for p in d.model_ids {
        set(p.name, Saved::ModelId(p.value), c);
    }
    for p in d.cards {
        set(p.name, Saved::Card(p.value), c);
    }
    for p in d.card_arrays {
        set(p.name, Saved::Cards(p.value), c);
    }
    for p in d.strings {
        set(p.name, Saved::Str(p.value), c);
    }
}
