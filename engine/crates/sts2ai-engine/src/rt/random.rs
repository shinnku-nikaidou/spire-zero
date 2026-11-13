//! The game's random number classes, bit for bit (game v0.113.0).
//!
//! | here | the game's C# |
//! |---|---|
//! | [`MegaRandom`] | `MegaCrit.Sts2.Core.Random.MegaRandom` (xoshiro256**) |
//! | [`RngState`] | `MegaCrit.Sts2.Core.Random.Rng` (a `MegaRandom` and a call counter) |
//! | [`SerializableRng`] | `MegaCrit.Sts2.Core.Saves.SerializableRng` |
//! | [`deterministic_hash_code`] | `StringHelper.GetDeterministicHashCode` |
//! | [`deterministic_hash_code_old`] | `StringHelper.GetDeterministicHashCodeOld` |
//! | [`xx_hash64`] | `System.IO.Hashing.XxHash64.HashToUInt64` |
//!
//! Every method draws the same numbers, moves the state the same way and bumps the counter the same
//! number of times as its C# original; where the C# throws `ArgumentOutOfRangeException`, the
//! method here panics (after the same side effects, e.g. `next_int(0)` has already bumped the
//! counter).
//!
//! C# overloads that share a name carry a suffix here (`_max`, `_range`); C# default arguments are
//! spelled out by the caller (`NextInt()` is `next_int(i32::MAX)`, `NextFloat()` is
//! `next_float(1.0)`, ...).
//!
//! Arithmetic notes, each one checked against the C# on .NET 9:
//! - integer arithmetic wraps (C# is unchecked by default);
//! - `f64 -> integer` casts only ever see in-range values on the non-Gaussian paths; out of range
//!   (possible in `next_gaussian_int`) both .NET 9 and Rust saturate, and NaN becomes 0;
//! - `Enumerable.Sum` over floats adds in `f64` and narrows to `f32` once at the end, so the
//!   weighted pick does too; everything after the sum is `f32` arithmetic;
//! - the Gaussian helpers call `log`, `cos` and `sin`. .NET takes them from the platform's C
//!   library, and so does Rust's `std`, so the results are the same bits on the same machine; they
//!   are the only methods here whose bits depend on anything but IEEE 754 arithmetic.

/// `MegaRandom._incrDouble`: 2^-53.
const INCR_DOUBLE: f64 = 1.1102230246251565E-16;

/// `MegaRandom._incrFloat`: 2^-24.
const INCR_FLOAT: f32 = 5.9604645E-08;

/// `MegaCrit.Sts2.Core.Saves.SerializableRng`: an `Rng` as the game saves it. The serde names are
/// the game's JSON property names (`counter`, `s0` .. `s3`).
#[derive(
    Copy, Clone, Debug, PartialEq, Eq, Hash, Default, serde::Serialize, serde::Deserialize,
)]
pub struct SerializableRng {
    pub counter: i32,
    #[serde(rename = "s0")]
    pub state0: u64,
    #[serde(rename = "s1")]
    pub state1: u64,
    #[serde(rename = "s2")]
    pub state2: u64,
    #[serde(rename = "s3")]
    pub state3: u64,
}

/// `MegaRandom`: xoshiro256** with the game's own ways of turning 64 random bits into values.
///
/// `MegaRandom::default()` is the all-zero state, which is not a state any seed gives (it only ever
/// draws zeros); the C# `new MegaRandom(0)` is `MegaRandom::new(0)`.
#[derive(
    Copy, Clone, Debug, PartialEq, Eq, Hash, Default, serde::Serialize, serde::Deserialize,
)]
pub struct MegaRandom {
    pub s0: u64,
    pub s1: u64,
    pub s2: u64,
    pub s3: u64,
}

impl MegaRandom {
    /// `MegaRandom(ulong seed)`.
    pub fn new(seed: u64) -> Self {
        let mut random = Self::default();
        random.reinitialise(seed);
        random
    }

    /// `MegaRandom(SerializableRng serializable)`.
    pub fn from_serializable(serializable: &SerializableRng) -> Self {
        let mut random = Self::default();
        random.reinitialise_from_serializable(serializable);
        random
    }

    /// `Splitmix64(ref ulong x)`: advances `x` and returns the next value.
    #[inline]
    pub fn splitmix64(x: &mut u64) -> u64 {
        *x = x.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut num = *x;
        num = (num ^ (num >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        num = (num ^ (num >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        num ^ (num >> 31)
    }

    /// `Reinitialise(ulong seed)`.
    pub fn reinitialise(&mut self, seed: u64) {
        let mut seed = seed;
        self.s0 = Self::splitmix64(&mut seed);
        self.s1 = Self::splitmix64(&mut seed);
        self.s2 = Self::splitmix64(&mut seed);
        self.s3 = Self::splitmix64(&mut seed);
    }

    /// `Reinitialise(SerializableRng serializable)`.
    pub fn reinitialise_from_serializable(&mut self, serializable: &SerializableRng) {
        self.s0 = serializable.state0;
        self.s1 = serializable.state1;
        self.s2 = serializable.state2;
        self.s3 = serializable.state3;
    }

    /// `FillSerializableState(SerializableRng rng)`: writes the four state words, leaves the
    /// counter alone.
    pub fn fill_serializable_state(&self, rng: &mut SerializableRng) {
        rng.state0 = self.s0;
        rng.state1 = self.s1;
        rng.state2 = self.s2;
        rng.state3 = self.s3;
    }

    /// `NextBytes(Span<byte> span)`: one draw per 8 bytes, written little-endian, and one more draw
    /// for a tail of 1..7 bytes (its low bytes are used). An empty span draws nothing.
    pub fn next_bytes(&mut self, span: &mut [u8]) {
        let (chunks, tail) = span.as_chunks_mut::<8>();
        for chunk in chunks {
            *chunk = self.next_ulong_inner().to_le_bytes();
        }
        if !tail.is_empty() {
            let bytes = self.next_ulong_inner().to_le_bytes();
            tail.copy_from_slice(&bytes[..tail.len()]);
        }
    }

    /// `NextULongInner()`: the next 64 bits of xoshiro256**.
    #[inline]
    fn next_ulong_inner(&mut self) -> u64 {
        let mut s0 = self.s0;
        let mut s1 = self.s1;
        let mut s2 = self.s2;
        let mut s3 = self.s3;
        let result = s1.wrapping_mul(5).rotate_left(7).wrapping_mul(9);
        let t = s1 << 17;
        s2 ^= s0;
        s3 ^= s1;
        s1 ^= s2;
        s0 ^= s3;
        s2 ^= t;
        s3 = s3.rotate_left(45);
        self.s0 = s0;
        self.s1 = s1;
        self.s2 = s2;
        self.s3 = s3;
        result
    }

    /// Not a C# member: the C# class has no parameterless `Next()`. Kept as a name for the
    /// parameterless integer draw, which in C# is `NextInt()`; this is [`MegaRandom::next_int`].
    #[inline]
    #[allow(clippy::should_implement_trait)] // the name the callers were given
    pub fn next(&mut self) -> i32 {
        self.next_int()
    }

    /// `Next(int maxValue)`: uniform in `[0, max_exclusive)`. Panics when `max_exclusive < 1`.
    #[inline]
    pub fn next_max(&mut self, max_exclusive: i32) -> i32 {
        if max_exclusive < 1 {
            panic!("maxValue must be > 0 (was {max_exclusive})");
        }
        self.next_inner_i32(max_exclusive)
    }

    /// `Next(int minValue, int maxValue)`: uniform in `[min_inclusive, max_exclusive)`. Panics when
    /// `min_inclusive >= max_exclusive`.
    #[inline]
    pub fn next_range(&mut self, min_inclusive: i32, max_exclusive: i32) -> i32 {
        if min_inclusive >= max_exclusive {
            panic!("maxValue must be > minValue (min {min_inclusive}, max {max_exclusive})");
        }
        let range = max_exclusive as i64 - min_inclusive as i64;
        if range <= i32::MAX as i64 {
            self.next_inner_i32(range as i32)
                .wrapping_add(min_inclusive)
        } else {
            self.next_inner_i64(range)
                .wrapping_add(min_inclusive as i64) as i32
        }
    }

    /// `NextDouble()`: uniform in `[0, 1)`, a multiple of 2^-53.
    #[inline]
    pub fn next_double(&mut self) -> f64 {
        (self.next_ulong_inner() >> 11) as f64 * INCR_DOUBLE
    }

    /// `NextInt()`: uniform in `[0, i32::MAX]`, the top 31 bits of one draw.
    #[inline]
    pub fn next_int(&mut self) -> i32 {
        (self.next_ulong_inner() >> 33) as i32
    }

    /// `NextUInt()`: the low 32 bits of one draw.
    #[inline]
    pub fn next_uint(&mut self) -> u32 {
        self.next_ulong_inner() as u32
    }

    /// `NextULong()`: one draw.
    #[inline]
    pub fn next_ulong(&mut self) -> u64 {
        self.next_ulong_inner()
    }

    /// `NextBool()`: the top bit of one draw.
    #[inline]
    pub fn next_bool(&mut self) -> bool {
        (self.next_ulong_inner() & 0x8000_0000_0000_0000) != 0
    }

    /// `NextFloat()`: uniform in `[0, 1)`, a multiple of 2^-24.
    #[inline]
    pub fn next_float(&mut self) -> f32 {
        (self.next_ulong_inner() >> 40) as f32 * INCR_FLOAT
    }

    /// `NextInner(int maxValue)`.
    #[inline]
    fn next_inner_i32(&mut self, max_value: i32) -> i32 {
        (self.next_double() * max_value as f64) as i32
    }

    /// `NextInner(long maxValue)`.
    #[inline]
    fn next_inner_i64(&mut self, max_value: i64) -> i64 {
        (self.next_double() * max_value as f64) as i64
    }
}

/// The state of one C# `Rng` object: its call counter and its `MegaRandom`.
///
/// `RngState::default()` is a zero counter over the all-zero `MegaRandom`; the C# `new Rng()`,
/// which seeds with 0, is `RngState::new(0)`.
#[derive(
    Copy, Clone, Debug, PartialEq, Eq, Hash, Default, serde::Serialize, serde::Deserialize,
)]
pub struct RngState {
    pub counter: i32,
    pub random: MegaRandom,
}

impl RngState {
    /// `Rng(ulong seed = 0uL)`.
    pub fn new(seed: u64) -> Self {
        Self {
            counter: 0,
            random: MegaRandom::new(seed),
        }
    }

    /// `Rng(ulong seed, string name)`: seeded with `seed + GetDeterministicHashCode(name)`.
    pub fn new_named(seed: u64, name: &str) -> Self {
        Self::new(seed.wrapping_add(deterministic_hash_code(name)))
    }

    /// `Rng(Player player, ModelId id, ulong mixin = 0uL)`, given what it reads from its arguments:
    /// `player.RunState.Rng.Seed`, `player.RunState.GetPlayerSlotIndex(player)` and `id.Entry`.
    pub fn new_for_player(
        run_seed: u64,
        player_slot_index: i32,
        id_entry: &str,
        mixin: u64,
    ) -> Self {
        let sum = (run_seed as i64)
            .wrapping_add(player_slot_index as i64)
            .wrapping_add(deterministic_hash_code(id_entry) as i64);
        Self::new((sum as u64).wrapping_add(mixin))
    }

    /// `Rng(SerializableRng serializable)`.
    pub fn from_serializable(serializable: &SerializableRng) -> Self {
        Self {
            counter: serializable.counter,
            random: MegaRandom::from_serializable(serializable),
        }
    }

    /// `LoadFromSerializable(SerializableRng serializable)`.
    pub fn load_from_serializable(&mut self, serializable: &SerializableRng) {
        self.counter = serializable.counter;
        self.random.reinitialise_from_serializable(serializable);
    }

    /// `ToSerializable()`.
    pub fn to_serializable(&self) -> SerializableRng {
        let mut serializable = SerializableRng {
            counter: self.counter,
            ..SerializableRng::default()
        };
        self.random.fill_serializable_state(&mut serializable);
        serializable
    }

    /// `CreateChaoticSeed()`: a seed that differs between processes (the C# reads the operating
    /// system's random source; here it comes from `std`'s randomly keyed hasher). Never
    /// reproducible, by design: `Rng.Chaotic` is `RngState::new(RngState::create_chaotic_seed())`.
    pub fn create_chaotic_seed() -> u64 {
        use std::hash::{BuildHasher, Hasher};
        std::collections::hash_map::RandomState::new()
            .build_hasher()
            .finish()
    }

    /// `_counter++`.
    #[inline]
    fn bump(&mut self) {
        self.counter = self.counter.wrapping_add(1);
    }

    /// `NextBool()`: true when `Next(2) == 0`. One count.
    #[inline]
    pub fn next_bool(&mut self) -> bool {
        self.bump();
        self.random.next_max(2) == 0
    }

    /// `NextInt(int maxExclusive = int.MaxValue)`. One count. Panics when `max_exclusive < 1`.
    #[inline]
    pub fn next_int(&mut self, max_exclusive: i32) -> i32 {
        self.bump();
        self.random.next_max(max_exclusive)
    }

    /// `NextInt(int minInclusive, int maxExclusive)`. One count. Panics when
    /// `min_inclusive >= max_exclusive`.
    #[inline]
    pub fn next_int_range(&mut self, min_inclusive: i32, max_exclusive: i32) -> i32 {
        if min_inclusive >= max_exclusive {
            panic!("Minimum must be lower than maximum (min {min_inclusive}, max {max_exclusive})");
        }
        self.bump();
        self.random.next_range(min_inclusive, max_exclusive)
    }

    /// `NextUnsignedInt(uint maxExclusive = uint.MaxValue)`. One count. Panics when
    /// `max_exclusive == 0`.
    #[inline]
    pub fn next_unsigned_int(&mut self, max_exclusive: u32) -> u32 {
        self.next_unsigned_int_range(0, max_exclusive)
    }

    /// `NextUnsignedInt(uint minInclusive, uint maxExclusive)`. One count. Panics when
    /// `min_inclusive >= max_exclusive`.
    pub fn next_unsigned_int_range(&mut self, min_inclusive: u32, max_exclusive: u32) -> u32 {
        if min_inclusive >= max_exclusive {
            panic!("Minimum must be lower than maximum (min {min_inclusive}, max {max_exclusive})");
        }
        self.bump();
        let num = self.random.next_double();
        let num2 = (max_exclusive - min_inclusive) as f64;
        let num3 = (num * num2) as u32;
        min_inclusive.wrapping_add(num3)
    }

    /// `NextUnsignedLong()`. One count.
    #[inline]
    pub fn next_unsigned_long(&mut self) -> u64 {
        self.bump();
        self.random.next_ulong()
    }

    /// `NextUnsignedLong(ulong maxExclusive)`: a whole draw when `max_exclusive` is `u64::MAX`,
    /// else the range overload from 0. One count. Panics when `max_exclusive == 0`.
    pub fn next_unsigned_long_max(&mut self, max_exclusive: u64) -> u64 {
        if max_exclusive == u64::MAX {
            return self.next_unsigned_long();
        }
        self.next_unsigned_long_range(0, max_exclusive)
    }

    /// `NextUnsignedLong(ulong minInclusive, ulong maxExclusive)`. One count. Panics when
    /// `min_inclusive >= max_exclusive`. (The width goes through `f64`, so wide ranges are coarse;
    /// that is the game's behaviour.)
    pub fn next_unsigned_long_range(&mut self, min_inclusive: u64, max_exclusive: u64) -> u64 {
        if min_inclusive >= max_exclusive {
            panic!("Minimum must be lower than maximum (min {min_inclusive}, max {max_exclusive})");
        }
        self.bump();
        let num = self.random.next_double();
        let num2 = (max_exclusive - min_inclusive) as f64;
        let num3 = (num * num2) as u64;
        min_inclusive.wrapping_add(num3)
    }

    /// `NextFloat(float max = 1f)`. One count. Panics when `0 > max`.
    #[inline]
    pub fn next_float(&mut self, max: f32) -> f32 {
        self.next_float_range(0.0, max)
    }

    /// `NextFloat(float min, float max)`. One count. Panics when `min > max`.
    pub fn next_float_range(&mut self, min: f32, max: f32) -> f32 {
        if min > max {
            panic!("Minimum must not be higher than maximum (min {min}, max {max})");
        }
        self.bump();
        // The width is an f32 subtraction; the rest is f64 until the final narrowing.
        let width = (max - min) as f64;
        (self.random.next_double() * width + min as f64) as f32
    }

    /// `NextDouble()`: uniform in `[0, 1)`. One count.
    #[inline]
    pub fn next_double(&mut self) -> f64 {
        self.bump();
        self.random.next_double()
    }

    /// `NextDouble(double min, double max)`. One count. Panics when `min > max`.
    pub fn next_double_range(&mut self, min: f64, max: f64) -> f64 {
        if min > max {
            panic!("Minimum must not be higher than maximum (min {min}, max {max})");
        }
        self.bump();
        self.random.next_double() * (max - min) + min
    }

    /// `NextGaussianFloat(float mean = 0f, float stdDev = 1f, float min = 0f, float max = 1f)`: the
    /// double helper on the widened arguments, narrowed.
    pub fn next_gaussian_float(&mut self, mean: f32, std_dev: f32, min: f32, max: f32) -> f32 {
        self.next_gaussian_double(mean as f64, std_dev as f64, min as f64, max as f64) as f32
    }

    /// `NextGaussianDouble(double mean = 0.0, double stdDev = 1.0, double min = 0.0,
    /// double max = 1.0)`: Box-Muller (cosine branch), redrawn until `mean + z * std_dev` lies in
    /// `[0, 1]`, then scaled to `[min, max]`. Two counts per try. Panics when `min > max` or `mean`
    /// is outside `[0, 1]`.
    #[allow(clippy::manual_range_contains)] // the C# comparisons, which a NaN passes
    pub fn next_gaussian_double(&mut self, mean: f64, std_dev: f64, min: f64, max: f64) -> f64 {
        if min > max {
            panic!("Minimum must not be higher than maximum (min {min}, max {max})");
        }
        if mean < 0.0 || mean > 1.0 {
            panic!("Mean must be within [0, 1] (was {mean})");
        }
        loop {
            let d = 1.0 - self.next_double();
            let num = 1.0 - self.next_double();
            let num2 = (-2.0 * d.ln()).sqrt();
            let d2 = std::f64::consts::PI * 2.0 * num;
            let num3 = num2 * d2.cos();
            let num4 = mean + num3 * std_dev;
            if num4 < 0.0 || num4 > 1.0 {
                continue;
            }
            return num4 * (max - min) + min;
        }
    }

    /// `NextGaussianInt(int mean, int stdDev, int min, int max)`: Box-Muller (sine branch), rounded
    /// half to even, redrawn until it lies in `[min, max]`. Two counts per try. Panics when
    /// `min > max` or `mean` is outside `[min, max]`.
    pub fn next_gaussian_int(&mut self, mean: i32, std_dev: i32, min: i32, max: i32) -> i32 {
        if min > max {
            panic!("Minimum must not be higher than maximum (min {min}, max {max})");
        }
        if mean < min || mean > max {
            panic!("Mean must be within [min, max] (mean {mean}, min {min}, max {max})");
        }
        loop {
            let d = 1.0 - self.next_double();
            let num = 1.0 - self.next_double();
            let num2 = (-2.0 * d.ln()).sqrt() * (std::f64::consts::PI * 2.0 * num).sin();
            let a = mean as f64 + std_dev as f64 * num2;
            // Math.Round rounds half to even; the cast saturates (as .NET 9 does).
            let num3 = a.round_ties_even() as i32;
            if num3 < min || num3 > max {
                continue;
            }
            return num3;
        }
    }

    /// `NextItem<T>(IEnumerable<T> items)`: the index C# picks out of `len` items. `None` for an
    /// empty list (the C# returns `default(T)`) with no count; else one count.
    pub fn next_item_index(&mut self, len: usize) -> Option<usize> {
        if len == 0 {
            return None;
        }
        let num = i32::try_from(len).expect("more items than a C# int counts");
        Some(self.next_int_range(0, num) as usize)
    }

    /// `WeightedNextItem<T>(IEnumerable<T> items, Func<T?, float> weightFetcher)`: the index
    /// picked, given each item's weight in order. Always one count (the draw is `NextFloat()`, made
    /// before the list is looked at, so an empty list counts too). `None` is the C# `default(T)`.
    pub fn weighted_next_item_index(&mut self, weights: &[f32]) -> Option<usize> {
        let rand_input = self.next_float(1.0);
        Self::weighted_item_index(rand_input, weights)
    }

    /// The static `WeightedNextItem<T>(float randInput, IEnumerable<T> items,
    /// Func<T, float> weightFetcher, T fallback)`: walks the weights, subtracting each from
    /// `rand_input * sum`, and picks the first item that brings it to zero or below. `None` is the
    /// C# `fallback`.
    pub fn weighted_item_index(rand_input: f32, weights: &[f32]) -> Option<usize> {
        // Enumerable.Sum over floats: added up in f64, narrowed once.
        let mut sum = 0.0f64;
        for &weight in weights {
            sum += weight as f64;
        }
        let num = sum as f32;
        let mut num2 = rand_input * num;
        for (index, &weight) in weights.iter().enumerate() {
            num2 -= weight;
            if num2 <= 0.0 {
                return Some(index);
            }
        }
        None
    }
}

/// `StringHelper.GetDeterministicHashCode(string str)`: XxHash64, with seed 0, of the string's
/// UTF-8 bytes.
pub fn deterministic_hash_code(s: &str) -> u64 {
    xx_hash64(s.as_bytes(), 0)
}

/// `StringHelper.GetDeterministicHashCodeOld(string str)`: two interleaved 33-multiplier hashes
/// over the string's UTF-16 code units (even positions, odd positions), in wrapping `int`
/// arithmetic.
pub fn deterministic_hash_code_old(s: &str) -> i32 {
    let mut num: i32 = 352654597;
    let mut num2: i32 = num;
    for (i, unit) in s.encode_utf16().enumerate() {
        if i % 2 == 0 {
            num = (num << 5).wrapping_add(num) ^ unit as i32;
        } else {
            num2 = (num2 << 5).wrapping_add(num2) ^ unit as i32;
        }
    }
    num.wrapping_add(num2.wrapping_mul(1566083941))
}

const XX_PRIME_1: u64 = 0x9E37_79B1_85EB_CA87;
const XX_PRIME_2: u64 = 0xC2B2_AE3D_27D4_EB4F;
const XX_PRIME_3: u64 = 0x1656_67B1_9E37_79F9;
const XX_PRIME_4: u64 = 0x85EB_CA77_C2B2_AE63;
const XX_PRIME_5: u64 = 0x27D4_EB2F_1656_67C5;

#[inline]
fn xx_round(acc: u64, input: u64) -> u64 {
    acc.wrapping_add(input.wrapping_mul(XX_PRIME_2))
        .rotate_left(31)
        .wrapping_mul(XX_PRIME_1)
}

#[inline]
fn xx_merge_round(acc: u64, value: u64) -> u64 {
    (acc ^ xx_round(0, value))
        .wrapping_mul(XX_PRIME_1)
        .wrapping_add(XX_PRIME_4)
}

#[inline]
fn read_u64_le(bytes: &[u8]) -> u64 {
    let mut word = [0u8; 8];
    word.copy_from_slice(&bytes[..8]);
    u64::from_le_bytes(word)
}

#[inline]
fn read_u32_le(bytes: &[u8]) -> u32 {
    let mut word = [0u8; 4];
    word.copy_from_slice(&bytes[..4]);
    u32::from_le_bytes(word)
}

/// `System.IO.Hashing.XxHash64.HashToUInt64(source, seed)`: the XXH64 algorithm.
pub fn xx_hash64(data: &[u8], seed: u64) -> u64 {
    let mut rest = data;
    let mut hash = if data.len() >= 32 {
        let mut v1 = seed.wrapping_add(XX_PRIME_1).wrapping_add(XX_PRIME_2);
        let mut v2 = seed.wrapping_add(XX_PRIME_2);
        let mut v3 = seed;
        let mut v4 = seed.wrapping_sub(XX_PRIME_1);
        while rest.len() >= 32 {
            v1 = xx_round(v1, read_u64_le(&rest[0..]));
            v2 = xx_round(v2, read_u64_le(&rest[8..]));
            v3 = xx_round(v3, read_u64_le(&rest[16..]));
            v4 = xx_round(v4, read_u64_le(&rest[24..]));
            rest = &rest[32..];
        }
        let mut merged = v1
            .rotate_left(1)
            .wrapping_add(v2.rotate_left(7))
            .wrapping_add(v3.rotate_left(12))
            .wrapping_add(v4.rotate_left(18));
        merged = xx_merge_round(merged, v1);
        merged = xx_merge_round(merged, v2);
        merged = xx_merge_round(merged, v3);
        xx_merge_round(merged, v4)
    } else {
        seed.wrapping_add(XX_PRIME_5)
    };
    hash = hash.wrapping_add(data.len() as u64);
    while rest.len() >= 8 {
        hash ^= xx_round(0, read_u64_le(rest));
        hash = hash
            .rotate_left(27)
            .wrapping_mul(XX_PRIME_1)
            .wrapping_add(XX_PRIME_4);
        rest = &rest[8..];
    }
    if rest.len() >= 4 {
        hash ^= (read_u32_le(rest) as u64).wrapping_mul(XX_PRIME_1);
        hash = hash
            .rotate_left(23)
            .wrapping_mul(XX_PRIME_2)
            .wrapping_add(XX_PRIME_3);
        rest = &rest[4..];
    }
    for &byte in rest {
        hash ^= (byte as u64).wrapping_mul(XX_PRIME_5);
        hash = hash.rotate_left(11).wrapping_mul(XX_PRIME_1);
    }
    hash ^= hash >> 33;
    hash = hash.wrapping_mul(XX_PRIME_2);
    hash ^= hash >> 29;
    hash = hash.wrapping_mul(XX_PRIME_3);
    hash ^= hash >> 32;
    hash
}

#[cfg(test)]
mod tests {
    //! Every expected value here was printed by the game's own C# (`MegaRandom.cs`, `Rng.cs` and
    //! the two `StringHelper` functions of v0.113.0, compiled standalone on .NET 9), never by this
    //! file. The Gaussian values are the only ones that also depend on the C library's `log`, `sin`
    //! and `cos`.

    use super::*;

    /// A `MegaRandom` the way the reference prints it: the four state words in hex.
    fn mstate(m: &MegaRandom) -> String {
        format!("{:016x} {:016x} {:016x} {:016x}", m.s0, m.s1, m.s2, m.s3)
    }

    /// An `Rng` the way the reference prints it: the state words, then the counter.
    fn rstate(r: &RngState) -> String {
        format!("{} {}", mstate(&r.random), r.counter)
    }

    /// One `NextGaussianInt` over the whole int range from a fresh `Rng(seed)`: the value and the
    /// counter.
    fn gaussian_int_once(seed: u64, mean: i32, std_dev: i32) -> (i32, i32) {
        let mut r = RngState::new(seed);
        let value = r.next_gaussian_int(mean, std_dev, i32::MIN, i32::MAX);
        (value, r.counter)
    }

    fn panics<T>(f: impl FnOnce() -> T) -> bool {
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)).is_err()
    }

    #[test]
    fn constants() {
        assert_eq!(INCR_DOUBLE.to_bits(), (1.0 / (1u64 << 53) as f64).to_bits());
        assert_eq!(INCR_FLOAT.to_bits(), (1.0 / (1u32 << 24) as f32).to_bits());
    }

    #[test]
    fn seeding() {
        assert_eq!(
            mstate(&MegaRandom::new(0)),
            "e220a8397b1dcdaf 6e789e6aa1b965f4 06c45d188009454f f88bb8a8724c81ec"
        );
        assert_eq!(
            mstate(&MegaRandom::new(1)),
            "910a2dec89025cc1 beeb8da1658eec67 f893a2eefb32555e 71c18690ee42c90b"
        );
        assert_eq!(
            mstate(&MegaRandom::new(12345)),
            "22118258a9d111a0 346edce5f713f8ed 1e9a57bc80e6721d 2d160e7e5c3f42ca"
        );
        assert_eq!(
            mstate(&MegaRandom::new(0x0123456789ABCDEF)),
            "157a3807a48faa9d d573529b34a1d093 2f90b72e996dccbe a2d419334c4667ec"
        );
        assert_eq!(
            mstate(&MegaRandom::new(u64::MAX)),
            "e4d971771b652c20 e99ff867dbf682c9 382ff84cb27281e9 6d1db36ccba982d2"
        );
        let mut x = 0;
        assert_eq!(MegaRandom::splitmix64(&mut x), 0xE220A8397B1DCDAF);
        assert_eq!(x, 0x9E3779B97F4A7C15);
        assert_eq!(MegaRandom::splitmix64(&mut x), 0x6E789E6AA1B965F4);
        assert_eq!(x, 0x3C6EF372FE94F82A);
    }

    #[test]
    fn mega_random_draws() {
        let mut m = MegaRandom::new(12345);
        assert_eq!(m.next_ulong(), 0xBE6A36374160D49B);
        assert_eq!(m.next_ulong(), 0x214AAA0637A688C6);
        assert_eq!(m.next_double().to_bits(), 0x3FEED3A2DBD32A9A);
        assert_eq!(m.next_double().to_bits(), 0x3FA8C009189D2DC0);
        assert_eq!(m.next_float().to_bits(), 0x3F0E2076);
        assert_eq!(m.next_float().to_bits(), 0x3C2EF300);
        assert_eq!(m.next_int(), 343119143);
        assert_eq!(m.next(), 635235301);
        assert_eq!(m.next_uint(), 753974582);
        assert_eq!(
            [(); 8].map(|_| m.next_bool()),
            [true, true, true, true, true, false, true, false]
        );
        assert_eq!(m.next_max(1), 0);
        assert_eq!(m.next_max(2), 1);
        assert_eq!(m.next_max(10), 4);
        assert_eq!(m.next_max(1000), 91);
        assert_eq!(m.next_max(i32::MAX), 711264200);
        assert_eq!(m.next_range(-5, 5), -4);
        assert_eq!(m.next_range(0, 1), 0);
        assert_eq!(m.next_range(i32::MIN, i32::MAX), -2058884077);
        assert_eq!(m.next_range(-1, i32::MAX), 61276059);
        assert_eq!(m.next_range(i32::MIN, 0), -2023618690);
        assert_eq!(m.next_range(i32::MIN, -1), -956665906);
        assert_eq!(m.next_range(2147483646, i32::MAX), 2147483646);
        assert_eq!(m.next_range(-1000000, 2000000000), 1985678713);
        let mut buf = [0u8; 19];
        m.next_bytes(&mut buf);
        assert_eq!(
            buf,
            [
                0xE9, 0x09, 0x5F, 0xE7, 0x25, 0xC1, 0xEE, 0xDE, 0x5E, 0xAB, 0x79, 0x80, 0x75, 0x24,
                0x80, 0x95, 0xDA, 0xE8, 0x88
            ]
        );
        let mut buf = [0u8; 8];
        m.next_bytes(&mut buf);
        assert_eq!(buf, [0x21, 0xA9, 0x71, 0xF9, 0xFD, 0x61, 0x64, 0xED]);
        let mut buf = [0u8; 3];
        m.next_bytes(&mut buf);
        assert_eq!(buf, [0x0A, 0x63, 0xF4]);
        m.next_bytes(&mut []);
        assert_eq!(
            mstate(&m),
            "5dfa326bce5abc6a b87f0d8dd2eb5cb7 878602194e63bfbd 2264338fa44c8094"
        );
    }

    #[test]
    fn rng_draws() {
        let mut r = RngState::new(42);
        assert_eq!(
            [(); 8].map(|_| r.next_bool()),
            [true, true, false, false, false, false, false, false]
        );
        assert_eq!(r.next_int(i32::MAX), 1635039032);
        assert_eq!(r.next_int(1), 0);
        assert_eq!(r.next_int(6), 4);
        assert_eq!(r.next_int(100), 29);
        assert_eq!(r.next_int_range(0, 3), 2);
        assert_eq!(r.next_int_range(-10, 10), -4);
        assert_eq!(r.next_int_range(i32::MIN, i32::MAX), 906882107);
        assert_eq!(r.next_int_range(i32::MIN, 1), -262492875);
        assert_eq!(r.next_unsigned_int(u32::MAX), 2648970654);
        assert_eq!(r.next_unsigned_int(1), 0);
        assert_eq!(r.next_unsigned_int(10), 7);
        assert_eq!(r.next_unsigned_int(2147483649), 1520051807);
        assert_eq!(r.next_unsigned_int_range(5, 6), 5);
        assert_eq!(r.next_unsigned_int_range(100, 200), 117);
        assert_eq!(r.next_unsigned_int_range(0, u32::MAX), 1873449034);
        assert_eq!(
            r.next_unsigned_int_range(u32::MAX - 1, u32::MAX),
            4294967294
        );
        assert_eq!(r.next_unsigned_long(), 0x5061938DA99C7AF0);
        assert_eq!(r.next_unsigned_long_max(u64::MAX), 0x7D3BABC0D1E23440);
        assert_eq!(r.next_unsigned_long_max(1), 0x0000000000000000);
        assert_eq!(r.next_unsigned_long_max(1000), 0x0000000000000315);
        assert_eq!(
            r.next_unsigned_long_max(0x8000000000000000),
            0x51498A27A79DE800
        );
        assert_eq!(r.next_unsigned_long_max(u64::MAX - 1), 0x3B38BD77133B0800);
        assert_eq!(r.next_unsigned_long_range(0, u64::MAX), 0x6A0DA881492D3800);
        assert_eq!(
            r.next_unsigned_long_range(0x8000000000000000, u64::MAX),
            0xCFB5A8E986A81400
        );
        assert_eq!(r.next_unsigned_long_range(5, 6), 0x0000000000000005);
        assert_eq!(
            r.next_unsigned_long_range(0x0020000000000000, 0x0040000000000003),
            0x003E3B7767CF95F4
        );
        assert_eq!(r.next_float(1.0).to_bits(), 0x3F506FA3);
        assert_eq!(r.next_float(0.0).to_bits(), 0x00000000);
        assert_eq!(r.next_float(100.0).to_bits(), 0x429CEAE7);
        assert_eq!(r.next_float(0.1).to_bits(), 0x3B92E513);
        assert_eq!(r.next_float_range(-1.0, 1.0).to_bits(), 0xBD8001F7);
        assert_eq!(r.next_float_range(-500.0, 500.0).to_bits(), 0x43EC0B81);
        assert_eq!(r.next_float_range(3.0, 3.0).to_bits(), 0x40400000);
        assert_eq!(r.next_float_range(0.9, 1.1).to_bits(), 0x3F852320);
        assert_eq!(r.next_float_range(f32::MIN, f32::MAX).to_bits(), 0x7F800000);
        assert_eq!(r.next_double().to_bits(), 0x3FEA2EAAFDD085F8);
        assert_eq!(r.next_double_range(-1.0, 1.0).to_bits(), 0x3FE97E9B67D46996);
        assert_eq!(
            r.next_double_range(0.001, 1000.0).to_bits(),
            0x405F699E6D0BB1A2
        );
        assert_eq!(r.next_double_range(3.0, 3.0).to_bits(), 0x4008000000000000);
        assert_eq!(
            rstate(&r),
            "d9fd7fa3f411ee0d d944e2475331e6c1 da927ee788fc2dc1 102d19d0550d5ef6 47"
        );
    }

    #[test]
    fn rng_constructors() {
        assert_eq!(
            rstate(&RngState::new(0)),
            "e220a8397b1dcdaf 6e789e6aa1b965f4 06c45d188009454f f88bb8a8724c81ec 0"
        );
        assert_eq!(
            rstate(&RngState::new_named(1, "shuffle")),
            "0eaa68f0b59bb210 047f8437884527e9 ae924b5da38e849c 82c583ab3ecb18b1 0"
        );
        assert_eq!(
            rstate(&RngState::new_named(u64::MAX, "combat_targets")),
            "ea48f37245c4ebd3 60318f0ac682f79e 17d7f9d0fa735982 c4974f866284ff6e 0"
        );
        assert_eq!(
            rstate(&RngState::new_for_player(
                123456789,
                0,
                "STRIKE_IRONCLAD",
                0
            )),
            "e4f66054e4a69bbb 0ed782c6179a1fd7 170eb760da54773d a0349818021629ac 0"
        );
        assert_eq!(
            rstate(&RngState::new_for_player(u64::MAX, 3, "NEOW", 77)),
            "52040b0097f965bc 6b11c9f01b58ee03 cf4747b3f1035a3a e6f56c3d2bc706b1 0"
        );
        assert_eq!(
            rstate(&RngState::new_for_player(5, -1, "", 0)),
            "d5871a3c12dbd9f7 097a6f63b3383fbf 4fd36125ae9e3fc4 fa2b17f24df655fa 0"
        );
    }

    #[test]
    fn hashes() {
        let cases: &[(&str, u64, i32)] = &[
            ("", 0xEF46DB3751D8E999, 757602046),
            ("a", 0xD24EC4F1A98C6E5B, -842352707),
            ("ab", 0x65F708CA92D04A61, 938310215),
            ("abc", 0x44BC2CF5AD770999, -320589142),
            ("abcd", 0xDE0327B0D25D92CC, 1800143382),
            ("abcde", 0x07E3670C0C8DC7EB, -1088829615),
            ("1234", 0xD8316E61D84F6BA4, -1413354602),
            ("ABCD1234", 0xABEF24E2DBD82E97, 90107518),
            ("shuffle", 0x2AD1D778C87C9714, -1986686621),
            ("combat_targets", 0x5FF62452AC6B3AC2, 1525931235),
            ("act_selection", 0x79A5D5299DC62D50, -761924139),
            ("STRIKE_IRONCLAD", 0xDB21A79FD0B44FB2, 1472510177),
            (
                "The quick brown fox jumps over the lazy dog",
                0x0B242D361FDA71BC,
                -1555361009,
            ),
            (
                "0123456789abcdef0123456789abcdef",
                0x642A94958E71E6C5,
                -1576894338,
            ),
            (
                "0123456789abcdef0123456789abcdef0123456789a",
                0xF412F01CB96001AE,
                1601950376,
            ),
            ("\u{706B}\u{7403}", 0xEC26FE02988C7235, 1443647052),
            ("na\u{EF}ve caf\u{E9}", 0xF72D341B0C4BD8A6, 1747658854),
            ("\u{1F600}", 0x9025B8ABAAE87B80, 970947761),
            ("a\u{1F600}b", 0xEE1927075E122C7A, -1739820826),
            ("\u{0}", 0xE934A84ADB052768, -842352738),
            (
                "\u{7F}\u{80}\u{7FF}\u{800}\u{FFFF}",
                0x96067FBA44958A37,
                1912727091,
            ),
        ];
        for &(s, hash, old) in cases {
            assert_eq!(deterministic_hash_code(s), hash, "{s:?}");
            assert_eq!(deterministic_hash_code_old(s), old, "{s:?}");
        }
        // Longer than the 1024-byte buffer the game starts with.
        let long: String = (0..2000u32)
            .map(|k| char::from(b'a' + (k * 7 % 26) as u8))
            .collect();
        assert_eq!(deterministic_hash_code(&long), 0xE8F7D64579865841);
        assert_eq!(deterministic_hash_code_old(&long), 49657104);
        let wide = "\u{9F8D}\u{1F600}".repeat(300);
        assert_eq!(deterministic_hash_code(&wide), 0x8C09210A88407040);
        assert_eq!(deterministic_hash_code_old(&wide), 1648037502);
        assert_eq!(xx_hash64(b"abc", 1), 0xBEA9CA8199328908);
        assert_eq!(
            xx_hash64(&[0xFF; 37], 0x1234_5678_9ABC_DEF0),
            0xCB14FAAD7D5FEA45
        );
        let ramp: Vec<u8> = (0..100).collect();
        assert_eq!(xx_hash64(&ramp, u64::MAX), 0x09A991A091C9F6D7);
    }

    #[test]
    fn item_picks() {
        // One pick from each list size 0..=40; -1 is the empty list's None.
        let mut r = RngState::new(7);
        let picks: Vec<i32> = (0..=40)
            .map(|n| r.next_item_index(n).map_or(-1, |k| k as i32))
            .collect();
        assert_eq!(
            picks,
            [
                -1, 0, 0, 2, 3, 4, 5, 0, 0, 3, 1, 5, 8, 12, 12, 6, 8, 4, 8, 2, 2, 3, 14, 15, 6, 18,
                3, 1, 16, 5, 14, 2, 4, 27, 33, 32, 15, 13, 26, 35, 39
            ]
        );
        assert_eq!(
            rstate(&r),
            "0fbe62c5087a4dd1 41af11e01a5bcd92 44bc6dda8adc4f2c 026feb0e1cc49ec5 40"
        );
    }

    #[test]
    fn weighted_picks() {
        let cases: &[(f32, &[f32], Option<usize>)] = &[
            (0.0, &[1.0, 2.0, 3.0], Some(0)),
            (0.5, &[1.0, 2.0, 3.0], Some(1)),
            (1.0, &[1.0, 2.0, 3.0], Some(2)),
            (0.99, &[0.1, 0.2, 0.3, 0.4], Some(3)),
            (1.0000001, &[1.0, 1.0], None),
            (0.5, &[], None),
            (0.5, &[0.0, 0.0], Some(0)),
            (-1.0, &[1.0], Some(0)),
            (2.0, &[1.0], None),
            (0.3, &[0.0, 1.0, 0.0], Some(1)),
            (0.5, &[-1.0, 2.0, 1.0], Some(1)),
            (0.75, &[1.0, 0.5, 1.0, 1.0, 0.5], Some(3)),
            (1.0, &[16777216.0, 1.0, 1.0, 1.0, 1.0], Some(4)),
            (
                0.99999994,
                &[100000000.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0],
                Some(0),
            ),
            (
                1.0,
                &[0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1],
                Some(9),
            ),
        ];
        for &(rand_input, weights, expected) in cases {
            assert_eq!(
                RngState::weighted_item_index(rand_input, weights),
                expected,
                "{rand_input} {weights:?}"
            );
        }
        let mut r = RngState::new(99);
        let weights = [1.0, 0.5, 1.0, 1.0, 0.5];
        let picks: Vec<i32> = (0..16)
            .map(|_| {
                r.weighted_next_item_index(&weights)
                    .map_or(-1, |k| k as i32)
            })
            .collect();
        assert_eq!(picks, [1, 2, 2, 3, 3, 0, 1, 0, 3, 2, 3, 0, 0, 0, 2, 2]);
        // An empty list still draws.
        assert_eq!(r.weighted_next_item_index(&[]), None);
        assert_eq!(
            rstate(&r),
            "ef457e53f15898bc c015b324b298f033 8bb5898f6027e281 6a15bd3b284c3bfb 17"
        );
    }

    #[test]
    fn gaussian_draws() {
        let mut r = RngState::new(2026);
        assert_eq!(
            [(); 12].map(|_| r.next_gaussian_int(6, 1, 6, 7)),
            [7, 7, 6, 6, 6, 6, 6, 7, 7, 7, 6, 6]
        );
        assert_eq!(
            [(); 12].map(|_| r.next_gaussian_int(7, 1, 6, 7)),
            [7, 7, 7, 6, 6, 7, 7, 6, 7, 7, 7, 7]
        );
        assert_eq!(
            [(); 12].map(|_| r.next_gaussian_int(12, 1, 10, 14)),
            [12, 13, 14, 12, 10, 13, 10, 12, 12, 12, 12, 13]
        );
        assert_eq!(
            [(); 12].map(|_| r.next_gaussian_int(100, 15, 55, 145)),
            [90, 110, 101, 135, 90, 96, 95, 66, 82, 98, 87, 96]
        );
        assert_eq!(r.counter, 154);
        let draw = |_| r.next_gaussian_double(0.0, 1.0, 0.0, 1.0).to_bits();
        assert_eq!(
            [(); 3].map(draw),
            [0x3FC6E432FBCD36B2, 0x3FA0C5BB9FE030D9, 0x3FB8F566E06554F5]
        );
        let draw = |_| r.next_gaussian_double(0.5, 0.2, -3.0, 3.0).to_bits();
        assert_eq!(
            [(); 3].map(draw),
            [0xBF8084EC980AED00, 0x3FFA628A83D1E7AC, 0x4004B60BD96926D4]
        );
        let draw = |_| r.next_gaussian_float(0.0, 1.0, -500.0, 500.0).to_bits();
        assert_eq!(
            [(); 4].map(draw),
            [0x408C9CB4, 0xC2F1B514, 0xC34800B9, 0xC3150F49]
        );
        let draw = |_| r.next_gaussian_float(0.0, 8.0, 0.0, 1.0).to_bits();
        assert_eq!(
            [(); 4].map(draw),
            [0x3F5E130A, 0x3EC63D09, 0x3F762C1C, 0x3EAEA85D]
        );
        let draw = |_| r.next_gaussian_float(0.0, 0.1, 0.0, 1.0).to_bits();
        assert_eq!(
            [(); 4].map(draw),
            [0x3E18E2D5, 0x3DF2B99F, 0x3DB4B1C4, 0x3C0D1B4B]
        );
        assert_eq!(
            rstate(&r),
            "1d5baf7817c5ab68 b62f24ef759f1aab 4f86e46d0e42b3b9 0af35aec06302c6e 426"
        );
        // The first try lands exactly on a half: Math.Round goes to the even neighbour.
        assert_eq!(gaussian_int_once(52030092, 0, i32::MAX), (842910726, 2));
        assert_eq!(gaussian_int_once(7917537, 0, i32::MAX), (-766143850, 2));
        assert_eq!(gaussian_int_once(17312999, 7, 123456789), (-314914752, 2));
        // Past the int range the cast saturates.
        assert_eq!(gaussian_int_once(0, 0, i32::MAX), (i32::MAX, 2));
        assert_eq!(gaussian_int_once(7, 0, i32::MAX), (i32::MIN, 2));
    }

    // (seed, folded results, state and counter) after 20,000 mixed calls.
    const MIX_RNG: [(u64, u64, &str); 3] = [
        (
            0,
            0xB91AF8614F15D8F5,
            "4fafcbe79237091a 098196b5c3349ab6 b50dc63fb6a1a1a9 be2834c36078add3 19973",
        ),
        (
            20261114,
            0x07833D5319F091D7,
            "01d1acdbfe789e82 70a5192e73d151f4 664dfd5dd7f3f819 2e0091809f07da8f 19967",
        ),
        (
            0xDEADBEEFCAFEBABE,
            0x42F418FD74003C12,
            "49a6854c3678ba73 70a79279f880a8f5 cce23859ca20621c a1c5dd3144f9c5e5 19978",
        ),
    ];
    // (seed, folded results, state) after 20,000 mixed calls.
    const MIX_MEGA: [(u64, u64, &str); 3] = [
        (
            0,
            0x2213AFC6924BFCE1,
            "cabe962e695e9daf 98cdcedce82be431 451577737e8acd53 ca7e4e228afab520",
        ),
        (
            20261114,
            0x91032D22DB6DE709,
            "c040ed2f65e356eb b0559e5b8df1c297 689271896b26ab3a 0226b3a36b1490de",
        ),
        (
            0xDEADBEEFCAFEBABE,
            0x769EF008B5B964EC,
            "5cc138ed1438d9d4 e641df5f8fd6cbd8 8b12b64e32a4df2f 9bc75eacd0b73786",
        ),
    ];

    /// The driver of the long mixes; the C# side ran the same one.
    struct Lcg(u64);

    impl Lcg {
        fn next(&mut self) -> u32 {
            self.0 = self
                .0
                .wrapping_mul(6364136223846793005)
                .wrapping_add(1442695040888963407);
            (self.0 >> 33) as u32
        }
    }

    const FOLD_START: u64 = 0xCBF2_9CE4_8422_2325;

    fn fold(acc: u64, value: u64) -> u64 {
        (acc ^ value).wrapping_mul(0x0000_0100_0000_01B3)
    }

    fn pick_value(pick: Option<usize>) -> u64 {
        pick.map_or(u64::MAX, |k| k as u64)
    }

    #[test]
    fn long_mix_rng() {
        for (seed, expected, state) in MIX_RNG {
            let mut r = RngState::new(seed);
            let mut g = Lcg(seed ^ 0x0005_DEEC_E66D);
            let mut acc = FOLD_START;
            for _ in 0..20_000 {
                let value = match g.next() % 16 {
                    0 => r.next_bool() as u64,
                    1 => {
                        let max = 1 + (g.next() % 1000) as i32;
                        r.next_int(max) as i64 as u64
                    }
                    2 => r.next_int(i32::MAX) as i64 as u64,
                    3 => {
                        let a = g.next() as i32 - 1_073_741_824;
                        let b = a + 1 + (g.next() % 5000) as i32;
                        r.next_int_range(a, b) as i64 as u64
                    }
                    4 => r.next_int_range(i32::MIN, i32::MAX) as i64 as u64,
                    5 => {
                        let max = 1 + g.next();
                        r.next_unsigned_int(max) as u64
                    }
                    6 => {
                        let a = g.next();
                        let b = a + 1 + g.next();
                        r.next_unsigned_int_range(a, b) as u64
                    }
                    7 => r.next_unsigned_long(),
                    8 => {
                        let hi = g.next() as u64;
                        let lo = g.next() as u64;
                        r.next_unsigned_long_max(((hi << 32) | lo) + 1)
                    }
                    9 => {
                        let a = (g.next() as u64) << 20;
                        let b = a + 1 + ((g.next() as u64) << 32);
                        r.next_unsigned_long_range(a, b)
                    }
                    10 => {
                        let max = (g.next() % 1000) as f32 * 0.25;
                        r.next_float(max).to_bits() as u64
                    }
                    11 => {
                        let a = (g.next() % 2001) as f32 - 1000.0;
                        let b = a + (g.next() % 500) as f32 * 0.5;
                        r.next_float_range(a, b).to_bits() as u64
                    }
                    12 => r.next_double().to_bits(),
                    13 => {
                        let a = (g.next() % 2001) as f64 - 1000.0;
                        let b = a + (g.next() % 500) as f64 / 3.0;
                        r.next_double_range(a, b).to_bits()
                    }
                    14 => pick_value(r.next_item_index((g.next() % 41) as usize)),
                    _ => {
                        let n = (g.next() % 9) as usize;
                        let mut weights = [0.0f32; 8];
                        for weight in &mut weights[..n] {
                            *weight = (g.next() % 9) as f32 * 0.125;
                        }
                        pick_value(r.weighted_next_item_index(&weights[..n]))
                    }
                };
                acc = fold(acc, value);
            }
            assert_eq!(
                (acc, rstate(&r).as_str()),
                (expected, state),
                "seed {seed:#x}"
            );
        }
    }

    #[test]
    fn long_mix_mega_random() {
        for (seed, expected, state) in MIX_MEGA {
            let mut m = MegaRandom::new(seed);
            let mut g = Lcg(seed ^ 0xB5AD_4ECE_DA1C_E2A9);
            let mut acc = FOLD_START;
            for _ in 0..20_000 {
                let value = match g.next() % 10 {
                    0 => m.next_ulong(),
                    1 => m.next_double().to_bits(),
                    2 => m.next_float().to_bits() as u64,
                    3 => m.next_int() as i64 as u64,
                    4 => m.next_uint() as u64,
                    5 => m.next_bool() as u64,
                    6 => {
                        let max = 1 + (g.next() >> 1) as i32;
                        m.next_max(max) as i64 as u64
                    }
                    7 => {
                        let a = g.next() as i32 - 1_073_741_824;
                        let b = a + 1 + (g.next() % 100_000) as i32;
                        m.next_range(a, b) as i64 as u64
                    }
                    8 => {
                        // Wider than i32::MAX: the long branch.
                        let a = i32::MIN + (g.next() >> 1) as i32;
                        let b = i32::MAX - (g.next() >> 1) as i32;
                        m.next_range(a, b) as i64 as u64
                    }
                    _ => {
                        let n = (g.next() % 20) as usize;
                        let mut buf = [0u8; 19];
                        m.next_bytes(&mut buf[..n]);
                        buf[..n]
                            .iter()
                            .fold(n as u64, |v, &x| v.wrapping_mul(257).wrapping_add(x as u64))
                    }
                };
                acc = fold(acc, value);
            }
            assert_eq!(
                (acc, mstate(&m).as_str()),
                (expected, state),
                "seed {seed:#x}"
            );
        }
    }

    #[test]
    fn serializable_round_trip() {
        let mut r = RngState::new(42);
        r.next_int(10);
        r.next_double();
        let saved = r.to_serializable();
        assert_eq!(saved.counter, 2);
        let words = [saved.state0, saved.state1, saved.state2, saved.state3];
        assert_eq!(words, [r.random.s0, r.random.s1, r.random.s2, r.random.s3]);
        assert_eq!(RngState::from_serializable(&saved), r);
        assert_eq!(MegaRandom::from_serializable(&saved), r.random);
        let mut other = RngState::new(999);
        other.load_from_serializable(&saved);
        assert_eq!(other, r);
        // FillSerializableState writes the four words and leaves the counter alone.
        let mut filled = SerializableRng {
            counter: 5,
            ..SerializableRng::default()
        };
        r.random.fill_serializable_state(&mut filled);
        assert_eq!(
            filled,
            SerializableRng {
                counter: 5,
                ..saved
            }
        );
    }

    #[test]
    fn counter_wraps() {
        let mut r = RngState {
            counter: i32::MAX,
            random: MegaRandom::new(1),
        };
        r.next_bool();
        assert_eq!(r.counter, i32::MIN);
    }

    #[test]
    fn panics_where_the_game_throws() {
        let mut m = MegaRandom::new(1);
        assert!(panics(|| m.next_max(0)));
        assert!(panics(|| m.next_max(-1)));
        assert!(panics(|| m.next_range(5, 5)));
        assert!(panics(|| m.next_range(6, 5)));
        assert_eq!(m, MegaRandom::new(1));

        let mut r = RngState::new(1);
        // NextInt(max) counts and then MegaRandom.Next throws; every other check comes before the
        // count.
        assert!(panics(|| r.next_int(0)));
        assert_eq!(r.counter, 1);
        assert!(panics(|| r.next_int_range(3, 3)));
        assert!(panics(|| r.next_unsigned_int(0)));
        assert!(panics(|| r.next_unsigned_int_range(6, 5)));
        assert!(panics(|| r.next_unsigned_long_max(0)));
        assert!(panics(|| r.next_unsigned_long_range(5, 5)));
        assert!(panics(|| r.next_float(-1.0)));
        assert!(panics(|| r.next_float_range(2.0, 1.0)));
        assert!(panics(|| r.next_double_range(2.0, 1.0)));
        assert!(panics(|| r.next_gaussian_double(-0.1, 1.0, 0.0, 1.0)));
        assert!(panics(|| r.next_gaussian_double(1.5, 1.0, 0.0, 1.0)));
        assert!(panics(|| r.next_gaussian_double(0.5, 1.0, 2.0, 1.0)));
        assert!(panics(|| r.next_gaussian_float(0.5, 1.0, 2.0, 1.0)));
        assert!(panics(|| r.next_gaussian_int(5, 1, 6, 7)));
        assert!(panics(|| r.next_gaussian_int(8, 1, 6, 7)));
        assert!(panics(|| r.next_gaussian_int(6, 1, 7, 6)));
        assert_eq!(
            r,
            RngState {
                counter: 1,
                random: MegaRandom::new(1)
            }
        );
    }

    #[test]
    fn nan_arguments_pass_the_checks() {
        // The C# checks are `min > max` and `mean < 0 || mean > 1`: a NaN gets through and comes
        // back out.
        let mut r = RngState::new(3);
        assert!(r.next_float(f32::NAN).is_nan());
        assert!(r.next_float_range(f32::NAN, 1.0).is_nan());
        assert!(r.next_double_range(f64::NAN, 1.0).is_nan());
        assert!(r.next_gaussian_double(f64::NAN, 1.0, 0.0, 1.0).is_nan());
        assert_eq!(r.counter, 5);
    }
}
