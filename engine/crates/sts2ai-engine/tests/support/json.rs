//! JSON read into a tree that keeps what the bytes say: a number as its text (`8` and `8.0` differ), an object's
//! members in their order.

#[derive(Clone, Debug, PartialEq)]
pub enum Json {
    Null,
    Bool(bool),
    /// The number's text as written.
    Number(String),
    Text(String),
    Array(Vec<Json>),
    Object(Vec<(String, Json)>),
}

impl Json {
    pub fn get(&self, name: &str) -> Option<&Json> {
        match self {
            Json::Object(members) => members.iter().find(|(key, _)| key == name).map(|(_, value)| value),
            _ => None,
        }
    }

    pub fn member(&self, name: &str) -> &Json {
        self.get(name).unwrap_or_else(|| panic!("no member {name} in {}", self.brief()))
    }

    pub fn items(&self) -> &[Json] {
        match self {
            Json::Array(items) => items,
            _ => panic!("not an array: {}", self.brief()),
        }
    }

    pub fn text(&self) -> &str {
        match self {
            Json::Text(text) => text,
            _ => panic!("not a string: {}", self.brief()),
        }
    }

    pub fn number(&self) -> &str {
        match self {
            Json::Number(text) => text,
            _ => panic!("not a number: {}", self.brief()),
        }
    }

    /// The value in a few characters, for a message.
    pub fn brief(&self) -> String {
        match self {
            Json::Null => "null".into(),
            Json::Bool(b) => b.to_string(),
            Json::Number(text) => text.clone(),
            Json::Text(text) => format!("{text:?}"),
            Json::Array(items) => format!("[{} items]", items.len()),
            Json::Object(members) => format!("{{{}}}", members.iter().map(|(key, _)| key.as_str()).collect::<Vec<_>>().join(",")),
        }
    }
}

/// The first place two trees differ, as ".path[3].to: expected != actual"; None for equal trees.
pub fn difference(expected: &Json, actual: &Json, path: &str) -> Option<String> {
    match (expected, actual) {
        (Json::Object(a), Json::Object(b)) => {
            for (at, (key, value)) in a.iter().enumerate() {
                match b.get(at) {
                    Some((other, other_value)) if other == key => {
                        if let Some(found) = difference(value, other_value, &format!("{path}.{key}")) {
                            return Some(found);
                        }
                    }
                    Some((other, _)) => {
                        return Some(match b.iter().any(|(k, _)| k == key) {
                            true => format!("{path}.{key}: member {at} of the object, where the other has {other}"),
                            false => format!("{path}.{key}: {} != nothing", value.brief()),
                        });
                    }
                    None => return Some(format!("{path}.{key}: {} != nothing", value.brief())),
                }
            }
            b.get(a.len())
                .map(|(key, value)| format!("{path}.{key}: nothing != {}", value.brief()))
        }
        (Json::Array(a), Json::Array(b)) => {
            for (at, (x, y)) in a.iter().zip(b).enumerate() {
                if let Some(found) = difference(x, y, &format!("{path}[{at}]")) {
                    return Some(found);
                }
            }
            (a.len() != b.len()).then(|| format!("{path}.length: {} != {}", a.len(), b.len()))
        }
        (a, b) if a == b => None,
        (a, b) => Some(format!("{path}: {} != {}", a.brief(), b.brief())),
    }
}

pub fn parse(text: &str) -> Result<Json, String> {
    let mut reader = Reader {
        bytes: text.as_bytes(),
        at: 0,
    };
    let value = reader.value()?;
    reader.space();
    if reader.at != reader.bytes.len() {
        return Err(format!("text after the value at byte {}", reader.at));
    }
    Ok(value)
}

struct Reader<'a> {
    bytes: &'a [u8],
    at: usize,
}

impl Reader<'_> {
    fn space(&mut self) {
        while matches!(self.bytes.get(self.at), Some(b' ' | b'\t' | b'\n' | b'\r')) {
            self.at += 1;
        }
    }

    fn expect(&mut self, byte: u8) -> Result<(), String> {
        if self.bytes.get(self.at) != Some(&byte) {
            return Err(format!("no {:?} at byte {}", byte as char, self.at));
        }
        self.at += 1;
        Ok(())
    }

    fn word(&mut self, word: &str, value: Json) -> Result<Json, String> {
        if !self.bytes[self.at..].starts_with(word.as_bytes()) {
            return Err(format!("not a value at byte {}", self.at));
        }
        self.at += word.len();
        Ok(value)
    }

    fn value(&mut self) -> Result<Json, String> {
        self.space();
        match self.bytes.get(self.at) {
            Some(b'{') => {
                self.at += 1;
                let mut members = Vec::new();
                self.space();
                if self.bytes.get(self.at) == Some(&b'}') {
                    self.at += 1;
                    return Ok(Json::Object(members));
                }
                loop {
                    self.space();
                    let key = self.string()?;
                    self.space();
                    self.expect(b':')?;
                    members.push((key, self.value()?));
                    self.space();
                    match self.bytes.get(self.at) {
                        Some(b',') => self.at += 1,
                        Some(b'}') => {
                            self.at += 1;
                            return Ok(Json::Object(members));
                        }
                        _ => return Err(format!("an object does not go on at byte {}", self.at)),
                    }
                }
            }
            Some(b'[') => {
                self.at += 1;
                let mut items = Vec::new();
                self.space();
                if self.bytes.get(self.at) == Some(&b']') {
                    self.at += 1;
                    return Ok(Json::Array(items));
                }
                loop {
                    items.push(self.value()?);
                    self.space();
                    match self.bytes.get(self.at) {
                        Some(b',') => self.at += 1,
                        Some(b']') => {
                            self.at += 1;
                            return Ok(Json::Array(items));
                        }
                        _ => return Err(format!("an array does not go on at byte {}", self.at)),
                    }
                }
            }
            Some(b'"') => Ok(Json::Text(self.string()?)),
            Some(b't') => self.word("true", Json::Bool(true)),
            Some(b'f') => self.word("false", Json::Bool(false)),
            Some(b'n') => self.word("null", Json::Null),
            Some(b'-' | b'0'..=b'9') => {
                let from = self.at;
                while matches!(self.bytes.get(self.at), Some(b'-' | b'+' | b'.' | b'e' | b'E' | b'0'..=b'9')) {
                    self.at += 1;
                }
                Ok(Json::Number(String::from_utf8_lossy(&self.bytes[from..self.at]).into_owned()))
            }
            _ => Err(format!("not a value at byte {}", self.at)),
        }
    }

    fn hex4(&mut self) -> Result<u16, String> {
        let digits = self.bytes.get(self.at..self.at + 4).ok_or("a \\u escape cut short")?;
        let text = std::str::from_utf8(digits).map_err(|e| e.to_string())?;
        self.at += 4;
        u16::from_str_radix(text, 16).map_err(|_| format!("not four hex digits: {text}"))
    }

    fn string(&mut self) -> Result<String, String> {
        self.expect(b'"')?;
        let mut out: Vec<u8> = Vec::new();
        loop {
            match self.bytes.get(self.at) {
                None => return Err("a string with no end".into()),
                Some(b'"') => {
                    self.at += 1;
                    return String::from_utf8(out).map_err(|e| e.to_string());
                }
                Some(b'\\') => {
                    self.at += 1;
                    let escape = *self.bytes.get(self.at).ok_or("an escape cut short")?;
                    self.at += 1;
                    let ch = match escape {
                        b'"' => '"',
                        b'\\' => '\\',
                        b'/' => '/',
                        b'b' => '\u{8}',
                        b'f' => '\u{c}',
                        b'n' => '\n',
                        b'r' => '\r',
                        b't' => '\t',
                        b'u' => {
                            let unit = self.hex4()?;
                            let units = if (0xD800..0xDC00).contains(&unit) {
                                self.expect(b'\\')?;
                                self.expect(b'u')?;
                                vec![unit, self.hex4()?]
                            } else {
                                vec![unit]
                            };
                            char::decode_utf16(units)
                                .next()
                                .unwrap()
                                .map_err(|_| "half of a surrogate pair".to_string())?
                        }
                        other => return Err(format!("no escape \\{}", other as char)),
                    };
                    out.extend_from_slice(ch.encode_utf8(&mut [0; 4]).as_bytes());
                }
                Some(&byte) => {
                    out.push(byte);
                    self.at += 1;
                }
            }
        }
    }
}
