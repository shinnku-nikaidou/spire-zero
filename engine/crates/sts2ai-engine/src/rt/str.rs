//! C# strings: immutable, compared by content, possibly null. `Str` is Copy: text made at run time is interned.

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::collections::HashSet;
use std::sync::{Mutex, OnceLock};

#[derive(Copy, Clone, Default)]
pub struct Str(Option<&'static str>);

fn intern(s: String) -> &'static str {
    static TABLE: OnceLock<Mutex<HashSet<&'static str>>> = OnceLock::new();
    let mut table = TABLE.get_or_init(Default::default).lock().unwrap();
    if let Some(known) = table.get(s.as_str()) {
        return known;
    }
    let leaked: &'static str = Box::leak(s.into_boxed_str());
    table.insert(leaked);
    leaked
}

impl Str {
    pub const NULL: Str = Str(None);
    pub const EMPTY: Str = Str(Some(""));

    pub const fn lit(s: &'static str) -> Str {
        Str(Some(s))
    }
    pub fn from_string(s: String) -> Str {
        Str(Some(intern(s)))
    }
    pub fn is_null(self) -> bool {
        self.0.is_none()
    }
    /// The text. Panics on null, where C# throws NullReferenceException.
    pub fn as_str(self) -> &'static str {
        self.0.expect("null string")
    }
    pub fn is_null_or_empty(self) -> bool {
        self.0.is_none_or(|s| s.is_empty())
    }
    /// string.IsNullOrWhiteSpace: null, empty, or only white space (char.IsWhiteSpace).
    pub fn is_null_or_white_space(self) -> bool {
        self.0.is_none_or(|s| s.chars().all(char::is_whitespace))
    }
    /// string.Concat: null counts as empty.
    pub fn concat(parts: &[Str]) -> Str {
        let mut out = String::new();
        for p in parts {
            out.push_str(p.0.unwrap_or(""));
        }
        Str::from_string(out)
    }
    pub fn join(sep: Str, parts: &[Str]) -> Str {
        let texts: Vec<&str> = parts.iter().map(|p| p.0.unwrap_or("")).collect();
        Str::from_string(texts.join(sep.0.unwrap_or("")))
    }
    /// The length in UTF-16 code units, as C#'s Length.
    pub fn len16(self) -> i32 {
        self.as_str().encode_utf16().count() as i32
    }
    pub fn char_at(self, i: i32) -> char {
        let unit = self.as_str().encode_utf16().nth(i as usize).expect("index outside the string");
        char::from_u32(unit as u32).expect("half of a surrogate pair")
    }
    pub fn contains(self, other: Str) -> bool {
        self.as_str().contains(other.as_str())
    }
    pub fn starts_with(self, other: Str) -> bool {
        self.as_str().starts_with(other.as_str())
    }
    pub fn ends_with(self, other: Str) -> bool {
        self.as_str().ends_with(other.as_str())
    }
    pub fn to_lower(self) -> Str {
        Str::from_string(self.as_str().to_lowercase())
    }
    pub fn to_upper(self) -> Str {
        Str::from_string(self.as_str().to_uppercase())
    }
    pub fn trim(self) -> Str {
        Str::from_string(self.as_str().trim().to_string())
    }
    /// Substring(start, length), in UTF-16 code units; a length of i32::MAX means to the end.
    pub fn substring(self, start: i32, len: i32) -> Str {
        let units: Vec<u16> = self.as_str().encode_utf16().collect();
        let end = if len == i32::MAX { units.len() } else { (start + len) as usize };
        Str::from_string(String::from_utf16(&units[start as usize..end]).expect("split surrogate pair"))
    }
    pub fn index_of(self, other: Str) -> i32 {
        match self.as_str().find(other.as_str()) {
            Some(byte) => self.as_str()[..byte].encode_utf16().count() as i32,
            None => -1,
        }
    }
    pub fn replace(self, from: Str, to: Str) -> Str {
        Str::from_string(self.as_str().replace(from.as_str(), to.as_str()))
    }
    pub fn split(self, sep: char) -> Vec<Str> {
        self.as_str().split(sep).map(|s| Str::from_string(s.to_string())).collect()
    }
    /// Ordinal comparison: -1, 0 or 1. Null sorts first.
    pub fn compare(self, other: Str) -> i32 {
        match (self.0, other.0) {
            (None, None) => 0,
            (None, Some(_)) => -1,
            (Some(_), None) => 1,
            (Some(a), Some(b)) => {
                let o = a.encode_utf16().cmp(b.encode_utf16());
                o as i32
            }
        }
    }
}

impl PartialEq for Str {
    fn eq(&self, other: &Str) -> bool {
        self.0 == other.0
    }
}
impl Eq for Str {}
impl std::hash::Hash for Str {
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        self.0.hash(state)
    }
}
impl PartialOrd for Str {
    fn partial_cmp(&self, other: &Str) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}
impl Ord for Str {
    fn cmp(&self, other: &Str) -> std::cmp::Ordering {
        self.compare(*other).cmp(&0)
    }
}
impl std::fmt::Debug for Str {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self.0 {
            Some(s) => write!(f, "{s:?}"),
            None => write!(f, "null"),
        }
    }
}
impl std::fmt::Display for Str {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.0.unwrap_or(""))
    }
}
impl Serialize for Str {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        self.0.serialize(s)
    }
}
impl<'de> Deserialize<'de> for Str {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Str, D::Error> {
        Ok(match Option::<String>::deserialize(d)? {
            Some(s) => Str::from_string(s),
            None => Str::NULL,
        })
    }
}
