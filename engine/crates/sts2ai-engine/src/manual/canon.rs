//! The canonical models (the game's ModelDb): one object per model class, built once.

use crate::r#gen::state::Arenas;

pub fn build() -> Arenas {
    Arenas::default()
}
