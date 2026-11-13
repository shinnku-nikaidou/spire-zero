//! `Dec`: a decimal number with the semantics of .NET's `System.Decimal` (.NET 9).
//!
//! The game's code computes with `decimal` and then truncates, rounds, compares and prints
//! the results, so code translated from it needs the same value *and* the same scale that
//! .NET produces, bit for bit. This file follows the algorithms of the .NET runtime's
//! `Decimal.DecCalc.cs` (dotnet/runtime, MIT license): a 96-bit unsigned mantissa, a scale
//! of 0..=28 decimal digits and a sign, with a zero that keeps its sign and scale.
//!
//! Where an exact integer formula gives the same answer as the runtime's word-by-word code
//! (the division steps, comparison, rounding to a number of decimals) the formula is used,
//! on `u128`. The parts whose result depends on the order of the steps are kept step for
//! step: the rescaling of an oversized sum or product, the digit loop of the division, the
//! conversions from `double` and `float`, and the long division of the remainder, which in
//! .NET 9 is not always the true remainder (see `mod_full`).
//!
//! Checked against .NET 9 itself on more than twenty million cases (operand pairs through
//! every operator, `Math` function and cast, doubles, floats, strings): no bit differs.

use std::cmp::Ordering;
use std::fmt;
use std::hash::{Hash, Hasher};
use std::ops::{
    Add, AddAssign, Div, DivAssign, Mul, MulAssign, Neg, Rem, RemAssign, Sub, SubAssign,
};

use serde::de::{self, Visitor};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

/// Sign bit of the flags word, as in .NET.
const SIGN_MASK: u32 = 0x8000_0000;
/// Scale byte of the flags word, as in .NET.
const SCALE_MASK: u32 = 0x00FF_0000;
const SCALE_SHIFT: u32 = 16;
/// The largest scale a decimal can have.
const MAX_SCALE: i32 = 28;
/// The largest mantissa: 2^96 - 1.
const MAX96: u128 = (1 << 96) - 1;

/// 10^0 ..= 10^28.
const POW10: [u128; 29] = {
    let mut t = [1u128; 29];
    let mut i = 1;
    while i < 29 {
        t[i] = t[i - 1] * 10;
        i += 1;
    }
    t
};

/// 10^0 ..= 10^19.
const POW10_U64: [u64; 20] = {
    let mut t = [1u64; 20];
    let mut i = 1;
    while i < 20 {
        t[i] = t[i - 1] * 10;
        i += 1;
    }
    t
};

/// 10^0 ..= 10^9.
const POW10_U32: [u32; 10] = {
    let mut t = [1u32; 10];
    let mut i = 1;
    while i < 10 {
        t[i] = t[i - 1] * 10;
        i += 1;
    }
    t
};

/// The largest mantissa that can still be multiplied by 10^n (n = 0..=9) within 96 bits.
const MAX96_DIV_POW10: [u128; 10] = {
    let mut t = [MAX96; 10];
    let mut i = 1;
    while i < 10 {
        t[i] = t[i - 1] / 10;
        i += 1;
    }
    t
};

/// 10^0 ..= 10^28 as doubles (each literal is the nearest double, as in .NET's table).
const POW10_F64: [f64; 29] = [
    1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16,
    1e17, 1e18, 1e19, 1e20, 1e21, 1e22, 1e23, 1e24, 1e25, 1e26, 1e27, 1e28,
];

/// A decimal number: `mantissa / 10^scale`, negated when the sign is set.
///
/// The parts are .NET's: 64 low bits and 32 high bits of the mantissa, and a flags word
/// holding the scale in bits 16..24 and the sign in bit 31. The high bits and the flags
/// share one `u64`, so a `Dec` travels in two registers.
#[derive(Copy, Clone)]
pub struct Dec {
    /// The low 64 bits of the mantissa.
    lo: u64,
    /// The high 32 bits of the mantissa in the low half, the flags word in the high half.
    top: u64,
}

/// How `Dec::round_to` settles a value between two candidates (.NET's `MidpointRounding`).
#[derive(Copy, Clone, PartialEq, Eq, Debug)]
pub enum MidpointRounding {
    /// Nearest; a tie goes to the even neighbour.
    ToEven,
    /// Nearest; a tie goes away from zero.
    AwayFromZero,
    /// Toward zero (truncation).
    ToZero,
    /// Downward.
    ToNegativeInfinity,
    /// Upward.
    ToPositiveInfinity,
}

/// The two ways decimal arithmetic fails; the operators panic with these, where .NET throws.
#[derive(Copy, Clone, PartialEq, Eq, Debug)]
pub enum DecError {
    /// .NET's `OverflowException`.
    Overflow,
    /// .NET's `DivideByZeroException`.
    DivideByZero,
}

impl fmt::Display for DecError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            DecError::Overflow => "Value was either too large or too small for a Decimal.",
            DecError::DivideByZero => "Attempted to divide by zero.",
        })
    }
}

impl std::error::Error for DecError {}

#[cold]
#[inline(never)]
#[track_caller]
fn fail(e: DecError) -> ! {
    panic!("{e}")
}

#[cold]
#[inline(never)]
#[track_caller]
fn cast_overflow(target: &str) -> ! {
    panic!("Value was either too large or too small for {target}.")
}

// ---------------------------------------------------------------------------------------
// Construction and accessors
// ---------------------------------------------------------------------------------------

impl Dec {
    /// `0m`
    pub const ZERO: Dec = Dec::new(0, 0, 0);
    /// `1m`
    pub const ONE: Dec = Dec::new(1, 0, 0);
    /// `-1m`
    pub const MINUS_ONE: Dec = Dec::new(1, 0, SIGN_MASK);
    /// `decimal.MaxValue`: 79228162514264337593543950335
    pub const MAX_VALUE: Dec = Dec::new(u64::MAX, u32::MAX, 0);
    /// `decimal.MinValue`: -79228162514264337593543950335
    pub const MIN_VALUE: Dec = Dec::new(u64::MAX, u32::MAX, SIGN_MASK);

    #[inline]
    const fn new(lo: u64, hi: u32, flags: u32) -> Dec {
        Dec {
            lo,
            top: (flags as u64) << 32 | hi as u64,
        }
    }

    /// The high 32 bits of the mantissa.
    #[inline]
    const fn hi(self) -> u32 {
        self.top as u32
    }

    /// The flags word: the scale in bits 16..24, the sign in bit 31.
    #[inline]
    const fn flags(self) -> u32 {
        (self.top >> 32) as u32
    }

    #[inline]
    const fn raw(mantissa: u128, flags: u32) -> Dec {
        Dec::new(mantissa as u64, (mantissa >> 64) as u32, flags)
    }

    /// A C# literal: `1.5m` is `Dec::lit(15, 1)`, `8m` is `Dec::lit(8, 0)`, `-0.25m` is
    /// `Dec::lit(-25, 2)`. The scale is kept as given.
    pub const fn lit(mantissa: i128, scale: u8) -> Dec {
        let m = mantissa.unsigned_abs();
        assert!(m <= MAX96, "decimal literal out of range");
        assert!(scale <= MAX_SCALE as u8, "decimal scale out of range");
        let sign = if mantissa < 0 { SIGN_MASK } else { 0 };
        Dec::raw(m, sign | ((scale as u32) << SCALE_SHIFT))
    }

    /// C#'s `new decimal(lo, mid, hi, isNegative, scale)`: the mantissa's three 32-bit
    /// words, low first. A zero made this way keeps the sign and scale given.
    pub const fn from_parts(lo: u32, mid: u32, hi: u32, negative: bool, scale: u8) -> Dec {
        assert!(scale <= MAX_SCALE as u8, "decimal scale out of range");
        let sign = if negative { SIGN_MASK } else { 0 };
        Dec::new(
            (lo as u64) | ((mid as u64) << 32),
            hi,
            sign | ((scale as u32) << SCALE_SHIFT),
        )
    }

    /// C#'s `decimal.GetBits`: the mantissa's low, middle and high words, then the flags
    /// word (scale in bits 16..24, sign in bit 31).
    pub const fn get_bits(self) -> [i32; 4] {
        [
            self.lo as u32 as i32,
            (self.lo >> 32) as u32 as i32,
            self.hi() as i32,
            self.flags() as i32,
        ]
    }

    /// C# `(decimal)int`.
    pub const fn from_i32(v: i32) -> Dec {
        Dec::from_i64(v as i64)
    }

    /// C# `(decimal)long`.
    pub const fn from_i64(v: i64) -> Dec {
        Dec::new(v.unsigned_abs(), 0, if v < 0 { SIGN_MASK } else { 0 })
    }

    /// C# `(decimal)uint`.
    pub const fn from_u32(v: u32) -> Dec {
        Dec::new(v as u64, 0, 0)
    }

    /// C# `(decimal)ulong`.
    pub const fn from_u64(v: u64) -> Dec {
        Dec::new(v, 0, 0)
    }

    /// The number of decimal digits after the point, 0..=28.
    #[inline]
    pub const fn scale(self) -> u8 {
        (self.flags() >> SCALE_SHIFT) as u8
    }

    /// True when the sign is set; a zero can carry it too (it still equals zero).
    #[inline]
    pub const fn is_negative(self) -> bool {
        self.flags() & SIGN_MASK != 0
    }

    /// The 96-bit unsigned mantissa: the value is `mantissa / 10^scale`, with the sign.
    #[inline]
    pub const fn mantissa(self) -> u128 {
        ((self.hi() as u128) << 64) | self.lo as u128
    }

    /// True for a zero of any sign and scale.
    #[inline]
    pub const fn is_zero(self) -> bool {
        self.lo == 0 && self.hi() == 0
    }
}

impl Default for Dec {
    fn default() -> Dec {
        Dec::ZERO
    }
}

impl From<i32> for Dec {
    fn from(v: i32) -> Dec {
        Dec::from_i32(v)
    }
}

// ---------------------------------------------------------------------------------------
// Wide helpers
// ---------------------------------------------------------------------------------------

/// Quotient and remainder, on 64-bit registers when both operands fit.
#[inline]
fn div_rem(n: u128, d: u128) -> (u128, u128) {
    if (n >> 64) == 0 && (d >> 64) == 0 {
        let (n, d) = (n as u64, d as u64);
        ((n / d) as u128, (n % d) as u128)
    } else {
        (n / d, n % d)
    }
}

/// The full product of two numbers below 2^96, as (low 128 bits, high 64 bits).
#[inline]
fn mul_wide(a: u128, b: u128) -> (u128, u64) {
    let (a0, a1) = (a as u64 as u128, (a >> 64) as u64 as u128);
    let (b0, b1) = (b as u64 as u128, (b >> 64) as u64 as u128);
    let low = a0 * b0;
    let mid = a0 * b1 + a1 * b0;
    let high = a1 * b1;
    let (lo, carry) = low.overflowing_add(mid << 64);
    (lo, (high + (mid >> 64) + carry as u128) as u64)
}

/// A 192-bit number as six 32-bit words, low first, with the index of its top non-zero word.
#[inline]
fn to_words(lo: u128, hi: u64) -> ([u32; 6], usize) {
    let w = [
        lo as u32,
        (lo >> 32) as u32,
        (lo >> 64) as u32,
        (lo >> 96) as u32,
        hi as u32,
        (hi >> 32) as u32,
    ];
    let mut top = 5;
    while top > 0 && w[top] == 0 {
        top -= 1;
    }
    (w, top)
}

#[inline]
fn low96(w: &[u32; 6]) -> u128 {
    w[0] as u128 | (w[1] as u128) << 32 | (w[2] as u128) << 64
}

/// `DecCalc.ScaleResult`: brings a result of up to 192 bits and a scale of up to 56 back
/// into 96 bits and a scale of at most 28, dividing by powers of ten and rounding half to
/// even on everything dropped. `hi_res` is the index of the top non-zero word. Returns the
/// new scale; the value is left in the low three words.
fn scale_result(res: &mut [u32; 6], mut hi_res: usize, mut scale: i32) -> Result<i32, DecError> {
    // A lower bound on the powers of ten to drop so that the upper 96 bits become zero.
    let mut new_scale: i32 = 0;
    if hi_res > 2 {
        new_scale = hi_res as i32 * 32 - 64 - 1;
        new_scale -= res[hi_res].leading_zeros() as i32;
        // Bit position times log10(2) (77/256), plus one.
        new_scale = ((new_scale * 77) >> 8) + 1;
        if new_scale > scale {
            return Err(DecError::Overflow);
        }
    }
    // And enough to bring the scale into range.
    if new_scale < scale - MAX_SCALE {
        new_scale = scale - MAX_SCALE;
    }
    if new_scale == 0 {
        return Ok(scale);
    }

    scale -= new_scale;
    let mut sticky: u32 = 0;
    let mut remainder: u32 = 0;
    loop {
        // Everything dropped before the last division only matters as "non-zero".
        sticky |= remainder;

        let power: u32 = if new_scale >= 9 {
            POW10_U32[9]
        } else {
            POW10_U32[new_scale as usize]
        };
        let top = res[hi_res];
        let quotient = top / power;
        remainder = top % power;
        for i in (0..hi_res).rev() {
            let num = res[i] as u64 | (remainder as u64) << 32;
            res[i] = (num / power as u64) as u32;
            remainder = (num % power as u64) as u32;
        }
        res[hi_res] = quotient;
        if quotient == 0 && hi_res != 0 {
            hi_res -= 1;
        }

        new_scale -= 9;
        if new_scale > 0 {
            continue;
        }

        // The estimate can be one power of ten short.
        if hi_res > 2 {
            if scale == 0 {
                return Err(DecError::Overflow);
            }
            new_scale = 1;
            scale -= 1;
            continue;
        }

        // Round on the last remainder: up when over half, or at half when odd or sticky.
        let half = power >> 1;
        if half <= remainder && (half < remainder || ((res[0] & 1) | sticky) != 0) {
            res[0] = res[0].wrapping_add(1);
            if res[0] == 0 {
                let mut cur = 0;
                loop {
                    cur += 1;
                    res[cur] = res[cur].wrapping_add(1);
                    if res[cur] != 0 {
                        break;
                    }
                }
                if cur > 2 {
                    // The carry left the 96 bits: drop one more digit.
                    if scale == 0 {
                        return Err(DecError::Overflow);
                    }
                    hi_res = cur;
                    sticky = 0;
                    remainder = 0;
                    new_scale = 1;
                    scale -= 1;
                    continue;
                }
            }
        }
        return Ok(scale);
    }
}

// ---------------------------------------------------------------------------------------
// Addition and subtraction (DecCalc.DecAddSub)
// ---------------------------------------------------------------------------------------

/// Adds or subtracts two mantissas of the same scale. `sign` true means the magnitudes
/// are subtracted; `flags` carries the scale and the sign of the left operand.
#[inline]
fn aligned_add(a: u128, b: u128, mut flags: u32, sign: bool) -> Result<Dec, DecError> {
    if sign {
        if a >= b {
            return Ok(Dec::raw(a - b, flags));
        }
        // The result went negative: flip the sign.
        return Ok(Dec::raw(b - a, flags ^ SIGN_MASK));
    }
    let sum = a + b;
    if sum <= MAX96 {
        return Ok(Dec::raw(sum, flags));
    }
    // The sum carried above 96 bits: divide by 10 and drop one digit of scale, rounding
    // half to even.
    if flags & SCALE_MASK == 0 {
        return Err(DecError::Overflow);
    }
    flags -= 1 << SCALE_SHIFT;
    let mut q = sum / 10;
    let r = (sum % 10) as u32;
    if r >= 5 && (r > 5 || q & 1 != 0) {
        q += 1;
    }
    Ok(Dec::raw(q, flags))
}

#[inline]
fn add_sub(d1: Dec, d2: Dec, subtract: bool) -> Result<Dec, DecError> {
    let xorflags = d1.flags() ^ d2.flags();
    // True when the magnitudes are to be subtracted.
    let sign = subtract ^ (xorflags & SIGN_MASK != 0);

    if xorflags & SCALE_MASK == 0 {
        // Same scale. The common case fits in 64 bits.
        if d1.hi() | d2.hi() == 0 {
            if sign {
                return Ok(if d1.lo >= d2.lo {
                    Dec::new(d1.lo - d2.lo, 0, d1.flags())
                } else {
                    Dec::new(d2.lo - d1.lo, 0, d1.flags() ^ SIGN_MASK)
                });
            }
            let (lo, carry) = d1.lo.overflowing_add(d2.lo);
            return Ok(Dec::new(lo, carry as u32, d1.flags()));
        }
        return aligned_add(d1.mantissa(), d2.mantissa(), d1.flags(), sign);
    }
    add_sub_unaligned(d1, d2, sign)
}

fn add_sub_unaligned(d1: Dec, d2: Dec, sign: bool) -> Result<Dec, DecError> {
    let s1 = d1.scale() as i32;
    let s2 = d2.scale() as i32;

    // `a` is the operand with the smaller scale: it is multiplied by 10^diff to match `b`.
    // The result has the larger scale and, for now, the sign of the left operand.
    let (a, b, diff, flags);
    if s2 > s1 {
        a = d1.mantissa();
        b = d2;
        diff = (s2 - s1) as usize;
        flags = (d2.flags() & SCALE_MASK) | (d1.flags() & SIGN_MASK);
    } else {
        a = d2.mantissa();
        b = d1;
        diff = (s1 - s2) as usize;
        flags = if sign {
            d1.flags() ^ SIGN_MASK
        } else {
            d1.flags()
        };
    }

    if a == 0 {
        // The operand to scale is zero: the other one is the result, mantissa and scale.
        let mut sign_flags = flags & SIGN_MASK;
        if sign {
            sign_flags ^= SIGN_MASK;
        }
        return Ok(Dec::new(
            b.lo,
            b.hi(),
            (b.flags() & SCALE_MASK) | sign_flags,
        ));
    }

    let (plo, phi) = if (a >> 64) == 0 && diff <= 19 {
        (a * POW10_U64[diff] as u128, 0u64)
    } else {
        mul_wide(a, POW10[diff])
    };
    let bm = b.mantissa();
    if phi == 0 && plo <= MAX96 {
        return aligned_add(plo, bm, flags, sign);
    }

    // The scaled operand needs more than 96 bits (so it is larger than `b`): finish in
    // 192 bits, then drop digits until the result fits.
    let (rlo, rhi) = if sign {
        let (l, borrow) = plo.overflowing_sub(bm);
        (l, phi - borrow as u64)
    } else {
        let (l, carry) = plo.overflowing_add(bm);
        (l, phi + carry as u64)
    };
    if rhi == 0 && rlo <= MAX96 {
        return Ok(Dec::raw(rlo, flags));
    }
    let (mut w, top) = to_words(rlo, rhi);
    let scale = scale_result(&mut w, top, ((flags & SCALE_MASK) >> SCALE_SHIFT) as i32)?;
    Ok(Dec::raw(
        low96(&w),
        (flags & !SCALE_MASK) | (scale as u32) << SCALE_SHIFT,
    ))
}

// ---------------------------------------------------------------------------------------
// Multiplication (DecCalc.VarDecMul)
// ---------------------------------------------------------------------------------------

#[inline]
fn mul(d1: Dec, d2: Dec) -> Result<Dec, DecError> {
    // The common case: both mantissas fit in 32 bits and the scales add up to at most 28.
    // A zero product keeps its sign and scale here.
    let flags_sum = (d1.flags() & SCALE_MASK) + (d2.flags() & SCALE_MASK);
    if d1.hi() | d2.hi() == 0
        && (d1.lo | d2.lo) >> 32 == 0
        && flags_sum <= (MAX_SCALE as u32) << SCALE_SHIFT
    {
        let sign = (d1.flags() ^ d2.flags()) & SIGN_MASK;
        return Ok(Dec::new(d1.lo * d2.lo, 0, sign | flags_sum));
    }
    mul_wide_or_rescaled(d1, d2)
}

fn mul_wide_or_rescaled(d1: Dec, d2: Dec) -> Result<Dec, DecError> {
    let mut scale = d1.scale() as i32 + d2.scale() as i32;
    let sign = (d1.flags() ^ d2.flags()) & SIGN_MASK;

    if d1.hi() | d2.hi() == 0 {
        if (d1.lo | d2.lo) >> 32 == 0 {
            // Both mantissas fit in 32 bits, the scale is over 28: divide the 64-bit
            // product by the power of ten in excess, rounding half to even.
            let mut low64 = d1.lo * d2.lo;
            if scale > MAX_SCALE {
                // Dividing by more than 10^19 leaves less than a half.
                if scale > MAX_SCALE + 19 {
                    return Ok(Dec::ZERO);
                }
                let mut power = POW10_U64[(scale - MAX_SCALE) as usize];
                let remainder = low64 % power;
                low64 /= power;
                power >>= 1;
                if remainder >= power && (remainder > power || low64 & 1 != 0) {
                    low64 += 1;
                }
                scale = MAX_SCALE;
            }
            return Ok(Dec::new(low64, 0, sign | (scale as u32) << SCALE_SHIFT));
        }
        let product = d1.lo as u128 * d2.lo as u128;
        if product <= MAX96 && scale <= MAX_SCALE {
            // On this path a zero product is the plain zero, without sign or scale.
            if product == 0 {
                return Ok(Dec::ZERO);
            }
            return Ok(Dec::raw(product, sign | (scale as u32) << SCALE_SHIFT));
        }
    }

    let (plo, phi) = mul_wide(d1.mantissa(), d2.mantissa());
    if plo == 0 && phi == 0 {
        return Ok(Dec::ZERO);
    }
    let (mut w, top) = to_words(plo, phi);
    if top > 2 || scale > MAX_SCALE {
        scale = scale_result(&mut w, top, scale)?;
    }
    Ok(Dec::raw(low96(&w), sign | (scale as u32) << SCALE_SHIFT))
}

// ---------------------------------------------------------------------------------------
// Division (DecCalc.VarDecDiv)
// ---------------------------------------------------------------------------------------

/// `DecCalc.SearchScale`: the largest power of ten, at most 9, that the quotient can be
/// multiplied by within 96 bits and a scale of 28.
#[inline]
fn search_scale(quo: u128, scale: i32) -> Result<i32, DecError> {
    let mut cur: i32 = if scale > MAX_SCALE - 9 {
        MAX_SCALE - scale
    } else {
        9
    };
    while cur > 0 && quo > MAX96_DIV_POW10[cur as usize] {
        cur -= 1;
    }
    // A quotient that cannot take nine more digits must at least reach a scale of zero.
    if cur < 9 && cur + scale < 0 {
        return Err(DecError::Overflow);
    }
    Ok(cur)
}

/// `DecCalc.OverflowUnscale`: the quotient went over 96 bits by one bit; divide it by 10,
/// drop one digit of scale and round half to even (`sticky`: a remainder is left over).
#[cold]
fn overflow_unscale(quo: &mut u128, scale: i32, sticky: bool) -> Result<i32, DecError> {
    let scale = scale - 1;
    if scale < 0 {
        return Err(DecError::Overflow);
    }
    let mut q = *quo / 10;
    let r = (*quo % 10) as u32;
    if r > 5 || (r == 5 && (sticky || q & 1 != 0)) {
        q += 1;
    }
    *quo = q;
    Ok(scale)
}

/// `DecCalc.Unscale`: takes trailing zeros off the mantissa, lowering the scale, by 10^8,
/// 10^4, 10^2 and 10^1.
#[inline]
fn unscale(m: u128, scale: i32) -> (u128, i32) {
    if (m >> 64) == 0 {
        let (m, scale) = unscale64(m as u64, scale);
        return (m as u128, scale);
    }
    let mut m = m;
    let mut scale = scale;
    while m as u8 == 0 && scale >= 8 && m.is_multiple_of(100_000_000) {
        m /= 100_000_000;
        scale -= 8;
    }
    // Once the mantissa fits in 64 bits the rest is the same on the narrow path (the
    // 10^8 step there finds nothing more to do).
    if (m >> 64) == 0 {
        let (m, scale) = unscale64(m as u64, scale);
        return (m as u128, scale);
    }
    if m & 0xF == 0 && scale >= 4 && m.is_multiple_of(10_000) {
        m /= 10_000;
        scale -= 4;
    }
    if m & 3 == 0 && scale >= 2 && m.is_multiple_of(100) {
        m /= 100;
        scale -= 2;
    }
    if m & 1 == 0 && scale >= 1 && m.is_multiple_of(10) {
        m /= 10;
        scale -= 1;
    }
    (m, scale)
}

#[inline]
fn unscale64(mut m: u64, mut scale: i32) -> (u64, i32) {
    while m as u8 == 0 && scale >= 8 && m.is_multiple_of(100_000_000) {
        m /= 100_000_000;
        scale -= 8;
    }
    if m & 0xF == 0 && scale >= 4 && m.is_multiple_of(10_000) {
        m /= 10_000;
        scale -= 4;
    }
    if m & 3 == 0 && scale >= 2 && m.is_multiple_of(100) {
        m /= 100;
        scale -= 2;
    }
    if m & 1 == 0 && scale >= 1 && m.is_multiple_of(10) {
        m /= 10;
        scale -= 1;
    }
    (m, scale)
}

fn div(d1: Dec, d2: Dec) -> Result<Dec, DecError> {
    let den = d2.mantissa();
    if den == 0 {
        return Err(DecError::DivideByZero);
    }
    let sign = (d1.flags() ^ d2.flags()) & SIGN_MASK;
    // The natural scale of the quotient; it can start negative.
    let mut scale = d1.scale() as i32 - d2.scale() as i32;
    let (mut quo, mut rem) = div_rem(d1.mantissa(), den);
    // Trailing zeros are taken off only if a remainder ever showed up.
    let mut has_remainder = false;

    loop {
        let cur: i32;
        if rem == 0 {
            if scale >= 0 {
                break;
            }
            // Exact, but the scale is still negative: multiply it out.
            cur = (-scale).min(9);
        } else {
            has_remainder = true;
            // Add up to nine more digits to the quotient, as many as still fit.
            cur = if scale == MAX_SCALE {
                0
            } else {
                search_scale(quo, scale)?
            };
            if cur == 0 {
                // No room for more digits: round half to even on the remainder.
                let twice = rem << 1;
                if twice > den || (twice == den && quo & 1 != 0) {
                    quo += 1;
                    if quo > MAX96 {
                        scale = overflow_unscale(&mut quo, scale, true)?;
                    }
                }
                break;
            }
        }

        let power = POW10_U32[cur as usize] as u128;
        scale += cur;
        quo *= power;
        if quo > MAX96 {
            return Err(DecError::Overflow);
        }
        let (digits, r) = div_rem(rem * power, den);
        rem = r;
        quo += digits;
        if quo > MAX96 {
            scale = overflow_unscale(&mut quo, scale, rem != 0)?;
            break;
        }
    }

    if has_remainder {
        (quo, scale) = unscale(quo, scale);
    }
    Ok(Dec::raw(quo, sign | (scale as u32) << SCALE_SHIFT))
}

// ---------------------------------------------------------------------------------------
// Remainder (DecCalc.VarDecMod)
// ---------------------------------------------------------------------------------------

/// Compares `m1 / 10^s1` with `m2 / 10^s2`.
#[inline]
fn cmp_magnitude(m1: u128, s1: u8, m2: u128, s2: u8) -> Ordering {
    if s1 == s2 {
        return m1.cmp(&m2);
    }
    if s1 < s2 {
        // Past 128 bits the scaled side is certainly the larger one.
        match m1.checked_mul(POW10[(s2 - s1) as usize]) {
            Some(v) => v.cmp(&m2),
            None => Ordering::Greater,
        }
    } else {
        match m2.checked_mul(POW10[(s1 - s2) as usize]) {
            Some(v) => m1.cmp(&v),
            None => Ordering::Less,
        }
    }
}

/// `DecCalc.Div96By64` (the portable path .NET 9 runs): divides the three words at
/// `buf[at..at + 3]` by `den`, leaves the remainder in the low two and returns the quotient.
fn div96_by64(buf: &mut [u32; 7], at: usize, den: u64) -> u32 {
    let low64 = buf[at] as u64 | (buf[at + 1] as u64) << 32;
    let num2 = buf[at + 2];
    let set_low64 = |buf: &mut [u32; 7], v: u64| {
        buf[at] = v as u32;
        buf[at + 1] = (v >> 32) as u32;
    };
    if num2 == 0 {
        if low64 < den {
            return 0;
        }
        set_low64(buf, low64 % den);
        return (low64 / den) as u32;
    }

    let den_high = (den >> 32) as u32;
    let mut quo: u32;
    let mut num: u64;
    if num2 >= den_high {
        // The divide would overflow: take a quotient of 2^32 and correct downward.
        num = low64.wrapping_sub(den << 32);
        quo = 0;
        loop {
            quo = quo.wrapping_sub(1);
            num = num.wrapping_add(den);
            if num < den {
                break;
            }
        }
        set_low64(buf, num);
        return quo;
    }

    let num64 = buf[at + 1] as u64 | (num2 as u64) << 32;
    if num64 < den_high as u64 {
        return 0;
    }
    quo = (num64 / den_high as u64) as u32;
    let rem_high = (num64 as u32).wrapping_sub(quo.wrapping_mul(den_high));
    num = buf[at] as u64 | (rem_high as u64) << 32;

    // The full remainder: dividend - quotient * divisor.
    let prod = quo as u64 * (den as u32) as u64;
    num = num.wrapping_sub(prod);
    if num > !prod {
        // It went negative: add the divisor back.
        loop {
            quo = quo.wrapping_sub(1);
            num = num.wrapping_add(den);
            if num < den {
                break;
            }
        }
    }
    set_low64(buf, num);
    quo
}

/// `DecCalc.Div128By96` (the portable path .NET 9 runs): divides the four words at
/// `buf[at..at + 4]` by the normalized 96-bit `den`, leaves the remainder in the low three
/// and returns the quotient. It expects the top word of the dividend to be below the top
/// word of the divisor. `mod_full` can call it with the two equal; the quotient then does
/// not fit in 32 bits, and what comes out of the wrapped arithmetic is what .NET returns,
/// so the arithmetic here wraps the same way.
fn div128_by96(buf: &mut [u32; 7], at: usize, den: &[u32; 3]) -> u32 {
    let dividend = buf[at + 2] as u64 | (buf[at + 3] as u64) << 32;
    let den_top = den[2];
    if dividend < den_top as u64 {
        return 0;
    }
    let mut quo = (dividend / den_top as u64) as u32;
    let mut remainder = (dividend as u32).wrapping_sub(quo.wrapping_mul(den_top));

    // The full remainder: dividend - quotient * divisor.
    let mut prod1 = quo as u64 * den[0] as u64;
    let mut prod2 = quo as u64 * den[1] as u64;
    prod2 = prod2.wrapping_add(prod1 >> 32);
    prod1 = (prod1 as u32) as u64 | prod2 << 32;
    prod2 >>= 32;

    let mut num = buf[at] as u64 | (buf[at + 1] as u64) << 32;
    num = num.wrapping_sub(prod1);
    remainder = remainder.wrapping_sub(prod2 as u32);

    // Propagate the borrows.
    let positive = if num > !prod1 {
        remainder = remainder.wrapping_sub(1);
        remainder < !(prod2 as u32)
    } else {
        remainder <= !(prod2 as u32)
    };
    if !positive {
        // It went negative: add the divisor back until it carries out.
        let den_low = den[0] as u64 | (den[1] as u64) << 32;
        loop {
            quo = quo.wrapping_sub(1);
            num = num.wrapping_add(den_low);
            remainder = remainder.wrapping_add(den_top);
            if num < den_low {
                let before = remainder;
                remainder = remainder.wrapping_add(1);
                if before < den_top {
                    break;
                }
            }
            if remainder < den_top {
                break;
            }
        }
    }

    buf[at] = num as u32;
    buf[at + 1] = (num >> 32) as u32;
    buf[at + 2] = remainder;
    quo
}

/// `DecCalc.VarDecModFull`: `(m1 * 10^-scale) mod m2` by long division, 32 bits at a
/// time, for a divisor wider than 32 bits. Kept word for word, because .NET 9's result is
/// not always the true remainder: when the divisor's mantissa is wider than 64 bits and the
/// dividend, brought to the divisor's scale, is wider than 96 bits, the division step can
/// be handed a case it does not expect (see `div128_by96`). So
/// `decimal.MaxValue % 1.0000000000000000000000000000m` is `0.2774645161159622109968728064`
/// in .NET 9 and here, not 0. Everywhere else the result is the true remainder.
fn mod_full(m1: u128, m2: u128, mut scale: i32) -> u128 {
    let d2_high = (m2 >> 64) as u32;
    let d2_mid = (m2 >> 32) as u32;
    // Normalize: shift both so that the divisor's top bit is set.
    let shift = (if d2_high == 0 { d2_mid } else { d2_high }).leading_zeros();

    let mut buf = [0u32; 7];
    let low64 = (m1 as u64) << shift;
    let mid64 = ((m1 >> 32) as u64) >> (32 - shift);
    buf[0] = low64 as u32;
    buf[1] = (low64 >> 32) as u32;
    buf[2] = mid64 as u32;
    buf[3] = (mid64 >> 32) as u32;

    // Scale the dividend up, to as much as 221 bits. `high` is its top word.
    let mut high = 3;
    while scale < 0 {
        let power = if scale <= -9 {
            POW10_U32[9]
        } else {
            POW10_U32[(-scale) as usize]
        } as u64;
        let mut tmp64 = buf[0] as u64 * power;
        buf[0] = tmp64 as u32;
        for word in buf.iter_mut().take(high + 1).skip(1) {
            tmp64 >>= 32;
            tmp64 += *word as u64 * power;
            *word = tmp64 as u32;
        }
        // The top bit of the dividend must stay clear.
        if tmp64 > i32::MAX as u64 {
            high += 1;
            buf[high] = (tmp64 >> 32) as u32;
        }
        scale += 9;
    }

    if d2_high == 0 {
        let divisor = (m2 as u64) << shift;
        for at in (0..=high - 2).rev() {
            div96_by64(&mut buf, at, divisor);
        }
        let low = buf[0] as u64 | (buf[1] as u64) << 32;
        return (low >> shift) as u128;
    }

    let den_low = (m2 as u64) << shift;
    let den = [
        den_low as u32,
        (den_low >> 32) as u32,
        (((m2 >> 32) as u64) >> (32 - shift)) as u32,
    ];
    for at in (0..=high - 3).rev() {
        div128_by96(&mut buf, at, &den);
    }
    let low = buf[0] as u64 | (buf[1] as u64) << 32;
    let low64 = (low >> shift).wrapping_add(((buf[2] as u64) << (32 - shift)) << 32);
    low64 as u128 | ((buf[2] >> shift) as u128) << 64
}

fn rem(d1: Dec, d2: Dec) -> Result<Dec, DecError> {
    let mut m2 = d2.mantissa();
    if m2 == 0 {
        return Err(DecError::DivideByZero);
    }
    let mut m1 = d1.mantissa();
    if m1 == 0 {
        return Ok(d1);
    }
    let (s1, s2) = (d1.scale(), d2.scale());
    // The result has the dividend's sign and the larger scale...
    let flags = (d1.flags() & SIGN_MASK) | (s1.max(s2) as u32) << SCALE_SHIFT;
    match cmp_magnitude(m1, s1, m2, s2) {
        // ...except a dividend smaller than the divisor, which is returned as it stands.
        Ordering::Less => return Ok(d1),
        Ordering::Equal => return Ok(Dec::raw(0, flags)),
        Ordering::Greater => {}
    }

    let mut scale = s1 as i32 - s2 as i32;
    if scale > 0 {
        // The divisor scaled up to the dividend's scale stays below the dividend.
        m2 *= POW10[scale as usize];
        scale = 0;
    }

    loop {
        if scale < 0 {
            // Scale the dividend up toward the divisor's scale, as far as 96 bits allow.
            loop {
                let mut cur = (-scale).min(9);
                while cur > 0 && m1 > MAX96_DIV_POW10[cur as usize] {
                    cur -= 1;
                }
                if cur == 0 {
                    break;
                }
                scale += cur;
                m1 *= POW10_U32[cur as usize] as u128;
                if cur != 9 || scale >= 0 {
                    break;
                }
            }
        }

        if (m1 >> 64) == 0 {
            // Here the scales match and the divisor is below the dividend.
            return Ok(Dec::raw((m1 as u64 % m2 as u64) as u128, flags));
        }
        if (m2 >> 32) == 0 {
            // A 32-bit divisor: reduce, then keep scaling what is left.
            m1 %= m2;
        } else {
            return Ok(Dec::raw(mod_full(m1, m2, scale), flags));
        }
        if scale >= 0 {
            break;
        }
    }
    Ok(Dec::raw(m1, flags))
}

// ---------------------------------------------------------------------------------------
// Checked arithmetic and the operators
// ---------------------------------------------------------------------------------------

impl Dec {
    /// `self + rhs`, or the error .NET would throw.
    #[inline]
    pub fn try_add(self, rhs: Dec) -> Result<Dec, DecError> {
        add_sub(self, rhs, false)
    }

    /// `self - rhs`, or the error .NET would throw.
    #[inline]
    pub fn try_sub(self, rhs: Dec) -> Result<Dec, DecError> {
        add_sub(self, rhs, true)
    }

    /// `self * rhs`, or the error .NET would throw.
    #[inline]
    pub fn try_mul(self, rhs: Dec) -> Result<Dec, DecError> {
        mul(self, rhs)
    }

    /// `self / rhs`, or the error .NET would throw.
    #[inline]
    pub fn try_div(self, rhs: Dec) -> Result<Dec, DecError> {
        div(self, rhs)
    }

    /// `self % rhs`, or the error .NET would throw.
    #[inline]
    pub fn try_rem(self, rhs: Dec) -> Result<Dec, DecError> {
        rem(self, rhs)
    }
}

macro_rules! binary_op {
    ($trait:ident, $method:ident, $assign_trait:ident, $assign_method:ident, $try:ident) => {
        impl $trait for Dec {
            type Output = Dec;
            #[inline]
            #[track_caller]
            fn $method(self, rhs: Dec) -> Dec {
                match self.$try(rhs) {
                    Ok(v) => v,
                    Err(e) => fail(e),
                }
            }
        }
        impl $assign_trait for Dec {
            #[inline]
            #[track_caller]
            fn $assign_method(&mut self, rhs: Dec) {
                *self = match self.$try(rhs) {
                    Ok(v) => v,
                    Err(e) => fail(e),
                };
            }
        }
    };
}

binary_op!(Add, add, AddAssign, add_assign, try_add);
binary_op!(Sub, sub, SubAssign, sub_assign, try_sub);
binary_op!(Mul, mul, MulAssign, mul_assign, try_mul);
binary_op!(Div, div, DivAssign, div_assign, try_div);
binary_op!(Rem, rem, RemAssign, rem_assign, try_rem);

impl Neg for Dec {
    type Output = Dec;
    /// Flips the sign, of a zero too (as `decimal.Negate` does).
    #[inline]
    fn neg(self) -> Dec {
        Dec::new(self.lo, self.hi(), self.flags() ^ SIGN_MASK)
    }
}

// ---------------------------------------------------------------------------------------
// Comparison and hashing (DecCalc.VarDecCmp)
// ---------------------------------------------------------------------------------------

impl Ord for Dec {
    fn cmp(&self, other: &Dec) -> Ordering {
        // Zeros are equal whatever their sign and scale.
        if other.is_zero() {
            return if self.is_zero() {
                Ordering::Equal
            } else if self.is_negative() {
                Ordering::Less
            } else {
                Ordering::Greater
            };
        }
        if self.is_zero() {
            return if other.is_negative() {
                Ordering::Greater
            } else {
                Ordering::Less
            };
        }
        match (self.is_negative(), other.is_negative()) {
            (true, false) => Ordering::Less,
            (false, true) => Ordering::Greater,
            (negative, _) => {
                let magnitude = cmp_magnitude(
                    self.mantissa(),
                    self.scale(),
                    other.mantissa(),
                    other.scale(),
                );
                if negative {
                    magnitude.reverse()
                } else {
                    magnitude
                }
            }
        }
    }
}

impl PartialOrd for Dec {
    #[inline]
    fn partial_cmp(&self, other: &Dec) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl PartialEq for Dec {
    #[inline]
    fn eq(&self, other: &Dec) -> bool {
        if self.flags() == other.flags() {
            return self.lo == other.lo && self.hi() == other.hi();
        }
        self.cmp(other) == Ordering::Equal
    }
}

impl Eq for Dec {}

impl Hash for Dec {
    /// Equal values hash alike: trailing zeros and the sign of a zero do not count.
    fn hash<H: Hasher>(&self, state: &mut H) {
        if self.is_zero() {
            state.write_u8(0);
            return;
        }
        let (m, scale) = unscale(self.mantissa(), self.scale() as i32);
        state.write_u8(if self.is_negative() { 2 } else { 1 });
        state.write_u8(scale as u8);
        state.write_u64(m as u64);
        state.write_u32((m >> 64) as u32);
    }
}

// ---------------------------------------------------------------------------------------
// Rounding (DecCalc.InternalRound) and the Math functions
// ---------------------------------------------------------------------------------------

impl Dec {
    /// Drops the last `digits` digits (1..=scale) of the mantissa, rounding by `mode`.
    /// The sign stays, also on a result of zero.
    #[inline]
    fn drop_digits(self, digits: u32, mode: MidpointRounding) -> Dec {
        let negative = self.is_negative();
        let flags = self.flags() - (digits << SCALE_SHIFT);
        if self.hi() == 0 && digits <= 19 {
            let power = POW10_U64[digits as usize];
            let q = self.lo / power;
            let r = self.lo % power;
            // A power of ten is even, so comparing with half of it is exact.
            let half = power / 2;
            let up = match mode {
                MidpointRounding::ToEven => r > half || (r == half && q & 1 != 0),
                MidpointRounding::AwayFromZero => r >= half,
                MidpointRounding::ToZero => false,
                MidpointRounding::ToNegativeInfinity => r != 0 && negative,
                MidpointRounding::ToPositiveInfinity => r != 0 && !negative,
            };
            return Dec::new(q + up as u64, 0, flags);
        }
        let power = POW10[digits as usize];
        let (q, r) = div_rem(self.mantissa(), power);
        let half = power / 2;
        let up = match mode {
            MidpointRounding::ToEven => r > half || (r == half && q & 1 != 0),
            MidpointRounding::AwayFromZero => r >= half,
            MidpointRounding::ToZero => false,
            MidpointRounding::ToNegativeInfinity => r != 0 && negative,
            MidpointRounding::ToPositiveInfinity => r != 0 && !negative,
        };
        Dec::raw(q + up as u128, flags)
    }

    /// `Math.Floor(d)`: the largest whole number not above it, at scale 0.
    #[inline]
    pub fn floor(self) -> Dec {
        match self.scale() {
            0 => self,
            s => self.drop_digits(s as u32, MidpointRounding::ToNegativeInfinity),
        }
    }

    /// `Math.Ceiling(d)`: the smallest whole number not below it, at scale 0.
    #[inline]
    pub fn ceiling(self) -> Dec {
        match self.scale() {
            0 => self,
            s => self.drop_digits(s as u32, MidpointRounding::ToPositiveInfinity),
        }
    }

    /// `Math.Truncate(d)`: the whole part, at scale 0.
    #[inline]
    pub fn truncate(self) -> Dec {
        match self.scale() {
            0 => self,
            s => self.drop_digits(s as u32, MidpointRounding::ToZero),
        }
    }

    /// `Math.Round(d)`: to a whole number, a tie to the even one.
    #[inline]
    pub fn round(self) -> Dec {
        self.round_to(0, MidpointRounding::ToEven)
    }

    /// `Math.Round(d, decimals, mode)`. A value with no more than `decimals` digits is
    /// returned as it is (its scale is not raised). Panics when `decimals` is outside
    /// 0..=28, where .NET throws `ArgumentOutOfRangeException`.
    #[inline]
    #[track_caller]
    pub fn round_to(self, decimals: i32, mode: MidpointRounding) -> Dec {
        if !(0..=MAX_SCALE).contains(&decimals) {
            panic!("Decimal can only round to between 0 and 28 digits of precision.");
        }
        let digits = self.scale() as i32 - decimals;
        if digits > 0 {
            self.drop_digits(digits as u32, mode)
        } else {
            self
        }
    }

    /// `Math.Abs(d)`.
    #[inline]
    pub fn abs(self) -> Dec {
        Dec::new(self.lo, self.hi(), self.flags() & !SIGN_MASK)
    }

    /// `Math.Sign(d)`: -1, 0 or 1.
    #[inline]
    pub fn sign(self) -> i32 {
        if self.is_zero() {
            0
        } else if self.is_negative() {
            -1
        } else {
            1
        }
    }

    /// `Math.Min(self, other)`. Of two equal values the second is returned, as in .NET
    /// (it shows in the scale: `Math.Min(1.0m, 1.00m)` is `1.00`). `Ord::min` and
    /// `std::cmp::min` keep Rust's own rule, the first of the two.
    #[inline]
    pub fn min(self, other: Dec) -> Dec {
        if Ord::cmp(&self, &other) == Ordering::Less {
            self
        } else {
            other
        }
    }

    /// `Math.Max(self, other)`. Of two equal values the first is returned, as in .NET
    /// (`Ord::max` and `std::cmp::max` return the second).
    #[inline]
    pub fn max(self, other: Dec) -> Dec {
        if Ord::cmp(&self, &other) != Ordering::Less {
            self
        } else {
            other
        }
    }
}

// ---------------------------------------------------------------------------------------
// Conversions to and from the primitive numbers
// ---------------------------------------------------------------------------------------

impl Dec {
    /// C# `(int)d` without the panic: truncates toward zero; `None` where .NET throws.
    pub fn try_to_i32(self) -> Option<i32> {
        let t = self.truncate();
        if t.hi() == 0 && t.lo >> 32 == 0 {
            let i = t.lo as u32 as i32;
            if !t.is_negative() {
                if i >= 0 {
                    return Some(i);
                }
            } else {
                let i = i.wrapping_neg();
                if i <= 0 {
                    return Some(i);
                }
            }
        }
        None
    }

    /// C# `(long)d` without the panic.
    pub fn try_to_i64(self) -> Option<i64> {
        let t = self.truncate();
        if t.hi() == 0 {
            let l = t.lo as i64;
            if !t.is_negative() {
                if l >= 0 {
                    return Some(l);
                }
            } else {
                let l = l.wrapping_neg();
                if l <= 0 {
                    return Some(l);
                }
            }
        }
        None
    }

    /// C# `(uint)d` without the panic. A negative value that truncates to zero is zero.
    pub fn try_to_u32(self) -> Option<u32> {
        let t = self.truncate();
        if t.hi() == 0 && t.lo >> 32 == 0 && (!t.is_negative() || t.lo == 0) {
            return Some(t.lo as u32);
        }
        None
    }

    /// C# `(ulong)d` without the panic.
    pub fn try_to_u64(self) -> Option<u64> {
        let t = self.truncate();
        if t.hi() == 0 && (!t.is_negative() || t.lo == 0) {
            return Some(t.lo);
        }
        None
    }

    /// C# `(int)d`: truncates toward zero; panics where .NET throws `OverflowException`.
    #[inline]
    #[track_caller]
    pub fn to_i32(self) -> i32 {
        match self.try_to_i32() {
            Some(v) => v,
            None => cast_overflow("an Int32"),
        }
    }

    /// C# `(long)d`.
    #[inline]
    #[track_caller]
    pub fn to_i64(self) -> i64 {
        match self.try_to_i64() {
            Some(v) => v,
            None => cast_overflow("an Int64"),
        }
    }

    /// C# `(uint)d`.
    #[inline]
    #[track_caller]
    pub fn to_u32(self) -> u32 {
        match self.try_to_u32() {
            Some(v) => v,
            None => cast_overflow("a UInt32"),
        }
    }

    /// C# `(ulong)d`.
    #[inline]
    #[track_caller]
    pub fn to_u64(self) -> u64 {
        match self.try_to_u64() {
            Some(v) => v,
            None => cast_overflow("a UInt64"),
        }
    }

    /// C# `(double)d` (`VarR8FromDec`): the mantissa as a double, divided by the power of
    /// ten. Two roundings, as in .NET 9; a negative zero gives `-0.0`.
    pub fn to_f64(self) -> f64 {
        // 2^64 as a double.
        const TWO_TO_64: f64 = 18446744073709551616.0;
        let dbl =
            (self.lo as f64 + self.hi() as f64 * TWO_TO_64) / POW10_F64[self.scale() as usize];
        if self.is_negative() { -dbl } else { dbl }
    }

    /// C# `(float)d`: the double of `to_f64`, narrowed.
    pub fn to_f32(self) -> f32 {
        self.to_f64() as f32
    }

    /// C# `(decimal)double` without the panic (`VarDecFromR8`): the value rounded to 15
    /// significant digits, trailing zeros removed; `None` where .NET throws
    /// `OverflowException` (too large, infinite or NaN). Tiny values and `-0.0` give `0`.
    pub fn try_from_f64(input: f64) -> Option<Dec> {
        // The binary exponent of the value, as a fraction in [0.5, 1) times 2^exp.
        // 10^28 is just over 2^93, so anything below 2^-94 rounds to zero.
        let exp = ((input.to_bits() >> 52) & 0x7FF) as i32 - 1022;
        if exp < -94 {
            return Some(Dec::ZERO);
        }
        if exp > 96 {
            return None;
        }
        let mut flags = 0u32;
        let mut dbl = input;
        if input < 0.0 {
            dbl = -input;
            flags = SIGN_MASK;
        }

        // Scale to a 15-digit integer: the power of ten comes from the binary exponent
        // times log10(2) (19728 / 65536), and can be one short.
        let mut power = 14 - ((exp * 19728) >> 16);
        if power >= 0 {
            if power > MAX_SCALE {
                power = MAX_SCALE;
            }
            dbl *= POW10_F64[power as usize];
        } else if power != -1 || dbl >= 1e15 {
            dbl /= POW10_F64[(-power) as usize];
        } else {
            power = 0;
        }
        if dbl < 1e14 && power < MAX_SCALE {
            dbl *= 10.0;
            power += 1;
        }

        // Round to an integer, a tie to even.
        let mut mant = dbl.round_ties_even() as i64 as u64;
        if mant == 0 {
            return Some(Dec::ZERO);
        }

        if power < 0 {
            // A large value: put the powers of ten back; this can pass 96 bits.
            let m = mant as u128 * POW10[(-power) as usize];
            if m > MAX96 {
                return None;
            }
            return Some(Dec::raw(m, flags));
        }

        // Take out trailing zeros, no more than the scale and no more than 14.
        let mut lmax = power.min(14);
        if mant as u8 == 0 && lmax >= 8 && mant.is_multiple_of(100_000_000) {
            mant /= 100_000_000;
            power -= 8;
            lmax -= 8;
        }
        if mant & 0xF == 0 && lmax >= 4 && mant.is_multiple_of(10_000) {
            mant /= 10_000;
            power -= 4;
            lmax -= 4;
        }
        if mant & 3 == 0 && lmax >= 2 && mant.is_multiple_of(100) {
            mant /= 100;
            power -= 2;
            lmax -= 2;
        }
        if mant & 1 == 0 && lmax >= 1 && mant.is_multiple_of(10) {
            mant /= 10;
            power -= 1;
        }
        Some(Dec::new(mant, 0, flags | (power as u32) << SCALE_SHIFT))
    }

    /// C# `(decimal)float` without the panic (`VarDecFromR4`): the value rounded to 7
    /// significant digits, trailing zeros removed.
    pub fn try_from_f32(input: f32) -> Option<Dec> {
        let exp = ((input.to_bits() >> 23) & 0xFF) as i32 - 126;
        if exp < -94 {
            return Some(Dec::ZERO);
        }
        if exp > 96 {
            return None;
        }
        let mut flags = 0u32;
        let mut input = input;
        if input < 0.0 {
            input = -input;
            flags = SIGN_MASK;
        }

        // Scale to a 7-digit integer, in double arithmetic.
        let mut dbl = input as f64;
        let mut power = 6 - ((exp * 19728) >> 16);
        if power >= 0 {
            if power > MAX_SCALE {
                power = MAX_SCALE;
            }
            dbl *= POW10_F64[power as usize];
        } else if power != -1 || dbl >= 1e7 {
            dbl /= POW10_F64[(-power) as usize];
        } else {
            power = 0;
        }
        if dbl < 1e6 && power < MAX_SCALE {
            dbl *= 10.0;
            power += 1;
        }

        let mut mant = dbl.round_ties_even() as i32 as u32;
        if mant == 0 {
            return Some(Dec::ZERO);
        }

        if power < 0 {
            let m = mant as u128 * POW10[(-power) as usize];
            if m > MAX96 {
                return None;
            }
            return Some(Dec::raw(m, flags));
        }

        // Take out trailing zeros, no more than the scale and no more than 6.
        let mut lmax = power.min(6);
        if mant & 0xF == 0 && lmax >= 4 && mant.is_multiple_of(10_000) {
            mant /= 10_000;
            power -= 4;
            lmax -= 4;
        }
        if mant & 3 == 0 && lmax >= 2 && mant.is_multiple_of(100) {
            mant /= 100;
            power -= 2;
            lmax -= 2;
        }
        if mant & 1 == 0 && lmax >= 1 && mant.is_multiple_of(10) {
            mant /= 10;
            power -= 1;
        }
        Some(Dec::new(
            mant as u64,
            0,
            flags | (power as u32) << SCALE_SHIFT,
        ))
    }

    /// C# `(decimal)double`: panics where .NET throws `OverflowException`.
    #[track_caller]
    pub fn from_f64(v: f64) -> Dec {
        match Dec::try_from_f64(v) {
            Some(d) => d,
            None => fail(DecError::Overflow),
        }
    }

    /// C# `(decimal)float`: panics where .NET throws `OverflowException`.
    #[track_caller]
    pub fn from_f32(v: f32) -> Dec {
        match Dec::try_from_f32(v) {
            Some(d) => d,
            None => fail(DecError::Overflow),
        }
    }
}

// ---------------------------------------------------------------------------------------
// Text: ToString(InvariantCulture) and Parse
// ---------------------------------------------------------------------------------------

/// The text of a decimal, held on the stack: at most a sign, 29 digits and a point.
struct Text {
    buf: [u8; 32],
    len: usize,
}

impl Text {
    #[inline]
    fn as_str(&self) -> &str {
        // Only ASCII digits, '.' and '-' are ever written.
        std::str::from_utf8(&self.buf[..self.len]).unwrap_or("")
    }
}

impl Dec {
    /// The digits as .NET prints them: every digit of the scale is shown, trailing zeros
    /// included. With `with_sign`, a minus comes first for a negative value that is not zero.
    fn text(self, with_sign: bool) -> Text {
        // The mantissa's digits, lowest first.
        let mut digits = [0u8; 29];
        let mut n = 0;
        if self.hi() == 0 {
            let mut v = self.lo;
            while v != 0 {
                digits[n] = (v % 10) as u8;
                v /= 10;
                n += 1;
            }
        } else {
            const E18: u128 = 1_000_000_000_000_000_000;
            let m = self.mantissa();
            let mut low = (m % E18) as u64;
            let mut high = (m / E18) as u64;
            for _ in 0..18 {
                digits[n] = (low % 10) as u8;
                low /= 10;
                n += 1;
            }
            while high != 0 {
                digits[n] = (high % 10) as u8;
                high /= 10;
                n += 1;
            }
        }

        let scale = self.scale() as usize;
        let mut out = Text {
            buf: [0; 32],
            len: 0,
        };
        if with_sign && n != 0 && self.is_negative() {
            out.buf[0] = b'-';
            out.len = 1;
        }
        // At least one digit before the point.
        let total = n.max(scale + 1);
        for i in (0..total).rev() {
            out.buf[out.len] = b'0' + if i < n { digits[i] } else { 0 };
            out.len += 1;
            if i == scale && scale != 0 {
                out.buf[out.len] = b'.';
                out.len += 1;
            }
        }
        out
    }

    /// C# `decimal.Parse(s, CultureInfo.InvariantCulture)` for plain numbers: an optional
    /// leading `+` or `-`, digits, and an optional `.` with more digits. The scale is the
    /// number of digits written after the point (`"1.50"` is `1.50`). More digits than a
    /// decimal holds are rounded half to even, as .NET does. `None` for anything else
    /// (spaces, group separators, exponents, a trailing sign) and for a value out of range.
    pub fn parse(s: &str) -> Option<Dec> {
        let bytes = s.as_bytes();
        let mut i = 0;
        let mut negative = false;
        if let Some(&c) = bytes.first()
            && (c == b'-' || c == b'+')
        {
            negative = c == b'-';
            i = 1;
        }

        // .NET's number buffer: up to 30 significant digits (leading zeros are skipped,
        // trailing zeros are kept), where the point falls relative to them, and whether
        // any digit beyond the buffer is non-zero.
        const MAX_DIGITS: usize = 30;
        let mut digits = [0u8; MAX_DIGITS + 1];
        let mut count: usize = 0;
        let mut exponent: i64 = 0;
        let mut seen_digit = false;
        let mut seen_point = false;
        let mut has_non_zero_tail = false;
        while i < bytes.len() {
            let c = bytes[i];
            if c.is_ascii_digit() {
                seen_digit = true;
                if c != b'0' || count != 0 {
                    if count < MAX_DIGITS {
                        digits[count] = c;
                    } else if c != b'0' {
                        has_non_zero_tail = true;
                    }
                    if !seen_point {
                        exponent += 1;
                    }
                    count += 1;
                } else if seen_point {
                    exponent -= 1;
                }
            } else if c == b'.' && !seen_point {
                seen_point = true;
            } else {
                return None;
            }
            i += 1;
        }
        if !seen_digit {
            return None;
        }
        let stored = count.min(MAX_DIGITS);
        // A zero byte ends the digits, as in the runtime's buffer.
        let digit = |p: usize| if p < stored { digits[p] } else { 0 };
        let sign = if negative { SIGN_MASK } else { 0 };

        // Number.TryNumberToDecimal.
        if stored == 0 {
            // A zero keeps the sign and the scale written.
            let scale = (-exponent).clamp(0, MAX_SCALE as i64) as u32;
            return Some(Dec::new(0, 0, sign | scale << SCALE_SHIFT));
        }
        if exponent > 29 {
            return None;
        }
        let mut e = exponent as i32;
        let mut p = 0;
        let mut c = digit(p);
        let mut low64: u64 = 0;
        while e > -MAX_SCALE {
            e -= 1;
            low64 = low64 * 10 + (c - b'0') as u64;
            p += 1;
            c = digit(p);
            if low64 >= u64::MAX / 10 {
                break;
            }
            if c == 0 {
                while e > 0 {
                    e -= 1;
                    low64 *= 10;
                    if low64 >= u64::MAX / 10 {
                        break;
                    }
                }
                break;
            }
        }

        const TENTH_HI: u32 = u32::MAX / 10;
        const TENTH_LO: u64 = 0x9999_9999_9999_9999;
        let mut high: u32 = 0;
        while (e > 0 || (c != 0 && e > -MAX_SCALE))
            && (high < TENTH_HI
                || (high == TENTH_HI && (low64 < TENTH_LO || (low64 == TENTH_LO && c <= b'5'))))
        {
            // Times ten, plus the next digit if there is one.
            let mut m = (((high as u128) << 64) | low64 as u128) * 10;
            if c != 0 {
                m += (c - b'0') as u128;
                p += 1;
                c = digit(p);
            }
            low64 = m as u64;
            high = (m >> 64) as u32;
            e -= 1;
        }

        // Round on the first digit left out: half to even, looking at the whole tail.
        if c >= b'5' {
            let mut round_up = true;
            if c == b'5' && low64 & 1 == 0 {
                p += 1;
                c = digit(p);
                let mut zero_tail = !has_non_zero_tail;
                while c != 0 && zero_tail {
                    zero_tail &= c == b'0';
                    p += 1;
                    c = digit(p);
                }
                round_up = !zero_tail;
            }
            if round_up {
                low64 = low64.wrapping_add(1);
                if low64 == 0 {
                    high = high.wrapping_add(1);
                    if high == 0 {
                        // Rounded past 2^96 - 1: one digit less.
                        low64 = 0x9999_9999_9999_999A;
                        high = TENTH_HI;
                        e += 1;
                    }
                }
            }
        }

        if e > 0 {
            return None;
        }
        if e <= -29 {
            // Too small to hold: a zero at the largest scale.
            return Some(Dec::new(0, 0, sign | (MAX_SCALE as u32) << SCALE_SHIFT));
        }
        Some(Dec::new(low64, high, sign | ((-e) as u32) << SCALE_SHIFT))
    }
}

impl fmt::Display for Dec {
    /// What `ToString(CultureInfo.InvariantCulture)` prints: `12.0`, `0.3333`, `-5`; a
    /// zero never shows a sign.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let text = self.text(false);
        f.pad_integral(!self.is_negative() || self.is_zero(), "", text.as_str())
    }
}

impl fmt::Debug for Dec {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Display::fmt(self, f)
    }
}

// ---------------------------------------------------------------------------------------
// serde
// ---------------------------------------------------------------------------------------

impl Serialize for Dec {
    /// As the string `Display` prints, so the scale survives.
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(self.text(true).as_str())
    }
}

impl<'de> Deserialize<'de> for Dec {
    /// From a string (`"1.50"`), or from a number: an integer exactly, a float through its
    /// shortest decimal text (`1.5` reads as `1.5`, `2.0` as `2`).
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Dec, D::Error> {
        struct DecVisitor;

        impl Visitor<'_> for DecVisitor {
            type Value = Dec;

            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("a decimal number, as a string or a number")
            }

            fn visit_str<E: de::Error>(self, v: &str) -> Result<Dec, E> {
                Dec::parse(v).ok_or_else(|| E::custom(format_args!("invalid decimal: {v:?}")))
            }

            fn visit_i64<E: de::Error>(self, v: i64) -> Result<Dec, E> {
                Ok(Dec::from_i64(v))
            }

            fn visit_u64<E: de::Error>(self, v: u64) -> Result<Dec, E> {
                Ok(Dec::from_u64(v))
            }

            fn visit_i128<E: de::Error>(self, v: i128) -> Result<Dec, E> {
                if v.unsigned_abs() > MAX96 {
                    return Err(E::custom(format_args!("decimal out of range: {v}")));
                }
                Ok(Dec::lit(v, 0))
            }

            fn visit_u128<E: de::Error>(self, v: u128) -> Result<Dec, E> {
                if v > MAX96 {
                    return Err(E::custom(format_args!("decimal out of range: {v}")));
                }
                Ok(Dec::raw(v, 0))
            }

            fn visit_f64<E: de::Error>(self, v: f64) -> Result<Dec, E> {
                // Rust prints a finite double in plain decimal digits, never an exponent.
                let parsed = if v.is_finite() {
                    Dec::parse(&v.to_string())
                } else {
                    None
                };
                parsed.ok_or_else(|| E::custom(format_args!("decimal out of range: {v}")))
            }
        }

        deserializer.deserialize_any(DecVisitor)
    }
}

// ---------------------------------------------------------------------------------------
// Tests. The tables hold what .NET 9's System.Decimal gives for the same input.
// ---------------------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    /// A decimal from its text; `-0.0` is the zero with its sign set.
    fn d(text: &str) -> Dec {
        Dec::parse(text).unwrap()
    }

    /// The text, with the minus kept on a zero whose sign is set.
    fn s(value: Dec) -> String {
        if value.is_zero() && value.is_negative() {
            format!("-{value}")
        } else {
            value.to_string()
        }
    }

    fn res(result: Result<Dec, DecError>) -> String {
        match result {
            Ok(value) => s(value),
            Err(e) => format!("{e:?}"),
        }
    }

    fn opt<T: ToString>(value: Option<T>) -> String {
        value.map_or("Overflow".to_string(), |v| v.to_string())
    }

    /// (operation, left, right, result)
    #[rustfmt::skip]
    const BINARY: &[(&str, &str, &str, &str)] = &[
        // sums: the larger scale wins, a zero keeps a sign and a scale
        ("+", "1.5", "0.25", "1.75"),
        ("+", "1.0", "1.00", "2.00"),
        ("+", "-1.0", "1.0", "-0.0"),
        ("-", "1.0", "1.0", "0.0"),
        ("-", "5", "7.5", "-2.5"),
        ("+", "0", "-0.00", "-0.00"),
        ("+", "5", "0.00", "5.00"),
        // sums that pass 96 bits: digits are dropped, half to even
        ("+", "79228162514264337593543950335", "1", "Overflow"),
        ("+", "79228162514264337593543950335", "0.4", "79228162514264337593543950335"),
        ("+", "79228162514264337593543950335", "0.5", "Overflow"),
        ("+", "79228162514264337593543950334", "0.5", "79228162514264337593543950334"),
        ("+", "79228162514264337593543950333", "0.5", "79228162514264337593543950334"),
        ("+", "7922816251426433759354395033.5", "7922816251426433759354395033.5", "15845632502852867518708790067"),
        ("-", "100", "0.0000000000000000000000000001", "100.00000000000000000000000000"),
        ("+", "123456789012345678901234.5678", "0.00000000000000000000000005", "123456789012345678901234.56780"),
        // products: the scales add up
        ("*", "8", "1.5", "12.0"),
        ("*", "0.75", "0.25", "0.1875"),
        ("*", "-2", "3", "-6"),
        ("*", "0", "-5.0", "-0.0"),
        ("*", "0.0", "12345678901234567890", "0"),
        ("*", "1.0000000000000000000000000001", "1.0000000000000000000000000001", "1.0000000000000000000000000002"),
        // products past scale 28 or 96 bits
        ("*", "0.0000000000000001", "0.0000000000000001", "0.0000000000000000000000000000"),
        ("*", "0.00000000000000025", "0.000000000001", "0.0000000000000000000000000002"),
        ("*", "0.00000000000000035", "0.000000000001", "0.0000000000000000000000000004"),
        ("*", "0.0000000000000000000000000001", "0.0000000000000000000000000001", "0"),
        ("*", "79228162514264337593543950335", "1.1", "Overflow"),
        ("*", "79228162514264337593543950335", "0.1", "7922816251426433759354395033.5"),
        ("*", "79228162514264337593543950335", "0.9999999999999999999999999999", "79228162514264337593543950327"),
        // quotients: as many digits as fit; trailing zeros go when the division was not exact
        ("/", "1", "3", "0.3333333333333333333333333333"),
        ("/", "2", "3", "0.6666666666666666666666666667"),
        ("/", "10", "4", "2.5"),
        ("/", "100", "10", "10"),
        ("/", "6", "2.0", "3"),
        ("/", "6.0", "2", "3.0"),
        ("/", "22", "7", "3.1428571428571428571428571429"),
        ("/", "0", "-5", "-0"),
        ("/", "1", "0", "DivideByZero"),
        ("/", "1", "0.0000000000000000000000000001", "10000000000000000000000000000"),
        ("/", "79228162514264337593543950335", "0.1", "Overflow"),
        ("/", "55459713759985036315480765235", "7", "7922816251426433759354395034"),
        ("/", "1", "79228162514264337593543950335", "0"),
        ("/", "0.0000000000000000000000000003", "2", "0.0000000000000000000000000002"),
        ("/", "0.0000000000000000000000000005", "2", "0.0000000000000000000000000002"),
        ("/", "12345678901234567890.12345", "0.000000987654321987654321", "12499999874843750115314446.135"),
        // remainders: the dividend's sign, the larger scale; a smaller dividend comes back as it is
        ("%", "7", "3", "1"),
        ("%", "-7", "3", "-1"),
        ("%", "7", "-3", "1"),
        ("%", "1.50", "1", "0.50"),
        ("%", "0.5", "1.00", "0.5"),
        ("%", "-4", "2", "-0"),
        ("%", "5.5", "0.25", "0.00"),
        ("%", "1", "0.3", "0.1"),
        ("%", "1", "0", "DivideByZero"),
        ("%", "123456789012345678901234567", "0.000000001234567", "0.000000000260873"),
        // a wide divisor under a dividend scaled past 96 bits: .NET 9's own results, not the
        // true remainders (0 and 0.3719252076201164536561358168)
        ("%", "79228162514264337593543950335", "1.0000000000000000000000000000", "0.2774645161159622109968728064"),
        ("%", "79228162514264337593543950335", "0.6666666666666666666666666667", "0.3490813540371441077816642958"),
        // of two equal values max is the first and min the second
        ("max", "1.0", "1.00", "1.0"),
        ("min", "1.0", "1.00", "1.00"),
        ("min", "-3", "2.5", "-3"),
        ("cmp", "1.0", "1.00", "0"),
        ("cmp", "-0.0", "0", "0"),
        ("cmp", "0.3333333333333333333333333333", "0.3333333333333333333333333334", "-1"),
        ("cmp", "79228162514264337593543950335", "0.0000000000000000000000000001", "1"),
        ("cmp", "-2", "-3", "1"),
    ];

    #[test]
    fn binary_operations() {
        for &(op, a, b, want) in BINARY {
            let (x, y) = (d(a), d(b));
            let got = match op {
                "+" => res(x.try_add(y)),
                "-" => res(x.try_sub(y)),
                "*" => res(x.try_mul(y)),
                "/" => res(x.try_div(y)),
                "%" => res(x.try_rem(y)),
                "min" => s(x.min(y)),
                "max" => s(x.max(y)),
                "cmp" => (x.cmp(&y) as i32).to_string(),
                _ => unreachable!(),
            };
            assert_eq!(got, want, "{a} {op} {b}");
        }
    }

    /// (function, argument, result)
    #[rustfmt::skip]
    const UNARY: &[(&str, &str, &str)] = &[
        ("floor", "2.5", "2"),
        ("floor", "-2.5", "-3"),
        ("floor", "-0.0", "-0"),
        ("floor", "-0.1", "-1"),
        ("floor", "79228162514264337593543950.335", "79228162514264337593543950"),
        ("ceiling", "2.1", "3"),
        ("ceiling", "-2.9", "-2"),
        ("ceiling", "-0.5", "-0"),
        ("ceiling", "0.0000000000000000000000000001", "1"),
        ("truncate", "2.9", "2"),
        ("truncate", "-2.9", "-2"),
        ("truncate", "-0.9", "-0"),
        ("round", "0.5", "0"),
        ("round", "1.5", "2"),
        ("round", "2.5", "2"),
        ("round", "-0.5", "-0"),
        ("round", "2.5000000000000000000000000001", "3"),
        ("neg", "0", "-0"),
        ("neg", "-1.50", "1.50"),
        ("abs", "-0.0", "0.0"),
        ("sign", "-0.0", "0"),
        ("sign", "-7.25", "-1"),
        // casts truncate toward zero and fail outside the range
        ("i32", "12.0", "12"),
        ("i32", "-12.9", "-12"),
        ("i32", "2147483647.9", "2147483647"),
        ("i32", "2147483648", "Overflow"),
        ("i32", "-2147483648.9", "-2147483648"),
        ("i32", "-2147483649", "Overflow"),
        ("i32", "-0.9", "0"),
        ("i64", "9223372036854775807.9", "9223372036854775807"),
        ("i64", "9223372036854775808", "Overflow"),
        ("i64", "-9223372036854775808.9", "-9223372036854775808"),
        ("i64", "-9223372036854775809", "Overflow"),
        ("u32", "4294967295.9", "4294967295"),
        ("u32", "4294967296", "Overflow"),
        ("u32", "-0.9", "0"),
        ("u32", "-1", "Overflow"),
        ("u64", "18446744073709551615.9", "18446744073709551615"),
        ("u64", "18446744073709551616", "Overflow"),
        ("u64", "-1", "Overflow"),
        // the bits of the double and of the float
        ("f64", "0.1", "3fb999999999999a"),
        ("f64", "0.3333333333333333333333333333", "3fd5555555555556"),
        ("f64", "79228162514264337593543950335", "45f0000000000000"),
        ("f64", "-0.0", "8000000000000000"),
        ("f64", "-123456789.123456789", "c19d6f34547e6b75"),
        ("f64", "0.0000000000000000000000000001", "3a1fb0f6be50601a"),
        ("f32", "0.1", "3dcccccd"),
        ("f32", "16777217", "4b800000"),
    ];

    #[test]
    fn unary_operations() {
        for &(op, a, want) in UNARY {
            let x = d(a);
            let got = match op {
                "floor" => s(x.floor()),
                "ceiling" => s(x.ceiling()),
                "truncate" => s(x.truncate()),
                "round" => s(x.round()),
                "neg" => s(-x),
                "abs" => s(x.abs()),
                "sign" => x.sign().to_string(),
                "i32" => opt(x.try_to_i32()),
                "i64" => opt(x.try_to_i64()),
                "u32" => opt(x.try_to_u32()),
                "u64" => opt(x.try_to_u64()),
                "f64" => format!("{:016x}", x.to_f64().to_bits()),
                "f32" => format!("{:08x}", x.to_f32().to_bits()),
                _ => unreachable!(),
            };
            assert_eq!(got, want, "{op} {a}");
        }
    }

    /// (value, decimals, the result by ToEven, AwayFromZero, ToZero, ToNegativeInfinity,
    /// ToPositiveInfinity)
    #[rustfmt::skip]
    const ROUND_TO: &[(&str, i32, [&str; 5])] = &[
        ("2.675", 2, ["2.68", "2.68", "2.67", "2.67", "2.68"]),
        ("2.665", 2, ["2.66", "2.67", "2.66", "2.66", "2.67"]),
        ("-2.665", 2, ["-2.66", "-2.67", "-2.66", "-2.67", "-2.66"]),
        ("-2.5", 0, ["-2", "-3", "-2", "-3", "-2"]),
        ("-0.00004", 4, ["-0.0000", "-0.0000", "-0.0000", "-0.0001", "-0.0000"]),
        ("1.5", 5, ["1.5", "1.5", "1.5", "1.5", "1.5"]),
        ("0.5000000000000000000000000001", 0, ["1", "1", "0", "0", "1"]),
        ("7922816251426433759354395.0335", 3, ["7922816251426433759354395.034", "7922816251426433759354395.034", "7922816251426433759354395.033", "7922816251426433759354395.033", "7922816251426433759354395.034"]),
    ];

    #[test]
    fn round_to() {
        const MODES: [MidpointRounding; 5] = [
            MidpointRounding::ToEven,
            MidpointRounding::AwayFromZero,
            MidpointRounding::ToZero,
            MidpointRounding::ToNegativeInfinity,
            MidpointRounding::ToPositiveInfinity,
        ];
        for &(a, decimals, want) in ROUND_TO {
            for (mode, want) in MODES.iter().zip(want) {
                assert_eq!(
                    s(d(a).round_to(decimals, *mode)),
                    want,
                    "{a} {decimals} {mode:?}"
                );
            }
        }
    }

    /// (the bits of a double, `(decimal)` of it)
    #[rustfmt::skip]
    const FROM_F64: &[(u64, &str)] = &[
        (0x3FB999999999999A, "0.1"),
        (0x3FD5555555555555, "0.333333333333333"),              // 1 / 3.0
        (0x3FE5555555555555, "0.666666666666667"),              // 2 / 3.0
        (0x3FD3333333333334, "0.3"),                            // 0.1 + 0.2
        (0x407B2FFFFFFFFFFF, "435"),                            // 4.35 * 100
        (0x3E7AD7F29ABCAF48, "0.0000001"),                      // 1e-7
        (0x419D6F34547E6B75, "123456789.123457"),               // 123456789.123456789
        (0x42DC12218377DE60, "123456789012346"),                // 123456789012345.5
        (0x42DC12218377DE20, "123456789012344"),                // 123456789012344.5
        (0x4340000000000000, "9007199254740990"),               // 2^53
        (0x45EFFFFFFFFFFFFC, "79228162514264300000000000000"),  // 7.92281625142643e28
        (0x45F0000000000000, "Overflow"),                       // 2^96
        (0x3A1FB0F6BE506019, "0.0000000000000000000000000001"), // 1e-28
        (0x3A33CE9A36F23C10, "0.0000000000000000000000000002"), // 2.5e-28
        (0x3A0FB0F6BE506019, "0"),                              // 5e-29
        (0x3A10299C8F23DF17, "0.0000000000000000000000000001"), // 5.1e-29
        (0x8000000000000000, "0"),                              // -0.0
        (0xBFF8000000000000, "-1.5"),
        (0xFFF8000000000000, "Overflow"),                       // NaN
        (0x7FF0000000000000, "Overflow"),                       // infinity
    ];

    /// (the bits of a float, `(decimal)` of it)
    #[rustfmt::skip]
    const FROM_F32: &[(u32, &str)] = &[
        (0x3DCCCCCD, "0.1"),
        (0x3EAAAAAB, "0.3333333"),                      // 1 / 3.0f
        (0x3F9AE148, "1.21"),                           // 1.1f * 1.1f
        (0x4B800000, "16777220"),                       // 2^24
        (0x4996B43C, "1234568"),                        // 1234567.5
        (0x4996B444, "1234568"),                        // 1234568.5
        (0x6F7FFFFF, "79228160000000000000000000000"),  // the largest float below 2^96
        (0x6F813F39, "Overflow"),                       // 8e28
        (0x0F4AD2F8, "0"),                              // 1e-29
        (0xFFC00000, "Overflow"),                       // NaN
    ];

    #[test]
    fn from_floats() {
        for &(bits, want) in FROM_F64 {
            let got = Dec::try_from_f64(f64::from_bits(bits)).map_or("Overflow".to_string(), s);
            assert_eq!(got, want, "double {bits:#018x}");
        }
        for &(bits, want) in FROM_F32 {
            let got = Dec::try_from_f32(f32::from_bits(bits)).map_or("Overflow".to_string(), s);
            assert_eq!(got, want, "float {bits:#010x}");
        }
        assert_eq!(s(Dec::from_f64(0.1) * Dec::from_f32(2.5)), "0.25");
    }

    /// (text, what `decimal.Parse` makes of it)
    #[rustfmt::skip]
    const PARSE: &[(&str, &str)] = &[
        ("1.50", "1.50"),
        ("-0.00", "-0.00"),
        ("000.0", "0.0"),
        (".5", "0.5"),
        ("5.", "5"),
        ("+1", "1"),
        ("-.25", "-0.25"),
        // more digits than fit: half to even on the whole tail
        ("0.00000000000000000000000000001", "0.0000000000000000000000000000"),
        ("0.00000000000000000000000000015", "0.0000000000000000000000000002"),
        ("0.00000000000000000000000000025", "0.0000000000000000000000000002"),
        ("0.000000000000000000000000000250001", "0.0000000000000000000000000003"),
        ("0.1234567890123456789012345678949", "0.1234567890123456789012345679"),
        ("79228162514264337593543950335.4", "79228162514264337593543950335"),
        ("79228162514264337593543950335.5", "None"),
        ("79228162514264337593543950336", "None"),
        ("7.92281625142643375935439503355", "7.922816251426433759354395034"),
        ("0.00000000184467440737095516155", "0.0000000018446744073709551616"),
        ("", "None"),
        (".", "None"),
        ("-", "None"),
        ("1.2.3", "None"),
        ("1e5", "None"),
        // these .NET takes by default; this parser is for plain numbers only
        (" 1", "None"),
        ("1,000", "None"),
        ("5-", "None"),
    ];

    #[test]
    fn parse() {
        for &(text, want) in PARSE {
            assert_eq!(
                Dec::parse(text).map_or("None".to_string(), s),
                want,
                "{text:?}"
            );
        }
    }

    #[test]
    fn construction() {
        assert_eq!(s(Dec::lit(15, 1)), "1.5");
        assert_eq!(s(Dec::lit(8, 0)), "8");
        assert_eq!(s(Dec::lit(-25, 2)), "-0.25");
        assert_eq!(s(Dec::lit(0, 3)), "0.000");
        assert_eq!(Dec::lit(15, 1).get_bits(), [15, 0, 0, 0x0001_0000]);
        assert_eq!(
            (-Dec::lit(0, 1)).get_bits(),
            [0, 0, 0, 0x8001_0000_u32 as i32]
        );
        assert_eq!(s(Dec::from_parts(0, 0, 0, true, 2)), "-0.00");
        assert_eq!(
            Dec::from_parts(1, 2, 3, true, 28).get_bits(),
            [1, 2, 3, 0x801C_0000_u32 as i32]
        );
        assert_eq!(s(Dec::from_i32(i32::MIN)), "-2147483648");
        assert_eq!(s(Dec::from_i64(i64::MIN)), "-9223372036854775808");
        assert_eq!(s(Dec::from_u32(u32::MAX)), "4294967295");
        assert_eq!(s(Dec::from_u64(u64::MAX)), "18446744073709551615");
        assert_eq!(s(Dec::from(-7)), "-7");
        assert_eq!(s(Dec::default()), "0");
        assert_eq!(s(Dec::ZERO), "0");
        assert_eq!(s(Dec::ONE + Dec::MINUS_ONE), "0");
        assert_eq!(s(Dec::MAX_VALUE), "79228162514264337593543950335");
        assert_eq!(s(Dec::MIN_VALUE), "-79228162514264337593543950335");
        let x = Dec::lit(-12345, 3);
        assert_eq!(
            (x.mantissa(), x.scale(), x.is_negative(), x.is_zero()),
            (12345, 3, true, false)
        );
    }

    #[test]
    fn operators_as_the_game_uses_them() {
        // (int)(8m * 1.5m), Math.Floor(x / 3m), Math.Ceiling(x / 3m)
        assert_eq!((Dec::lit(8, 0) * Dec::lit(15, 1)).to_i32(), 12);
        assert_eq!(s((Dec::from(10) / Dec::lit(3, 0)).floor()), "3");
        assert_eq!(s((Dec::from(10) / Dec::lit(3, 0)).ceiling()), "4");
        assert_eq!((Dec::lit(75, 2) * Dec::from(9)).to_i32(), 6);
        assert_eq!((Dec::lit(-75, 2) * Dec::from(9)).to_i64(), -6);
        assert_eq!(Dec::lit(65, 1).to_u32(), 6);
        assert_eq!(Dec::lit(65, 1).to_u64(), 6);
        assert_eq!(Dec::lit(25, 2).to_f64(), 0.25);
        assert_eq!(Dec::lit(25, 2).to_f32(), 0.25);

        let mut x = Dec::lit(10, 0);
        x += Dec::lit(25, 1);
        assert_eq!(s(x), "12.5");
        x -= Dec::ONE;
        assert_eq!(s(x), "11.5");
        x *= Dec::lit(20, 1);
        assert_eq!(s(x), "23.00");
        x /= Dec::lit(4, 0);
        assert_eq!(s(x), "5.75");
        x %= Dec::lit(5, 1);
        assert_eq!(s(x), "0.25");
        assert_eq!(s(-x), "-0.25");
        assert_eq!(s(x - x), "0.00");
    }

    #[test]
    fn ordering_equality_and_hashing() {
        assert!(d("1.0") == d("1.00"));
        assert!(d("-0.0") == Dec::ZERO);
        assert!(d("1.5") != d("1.50001"));
        assert!(d("-1") < d("0.5") && d("0.5") <= d("0.50"));
        assert!(d("2") > d("1.99") && d("2") >= d("2.0"));
        assert_eq!(d("7.5").clamp(d("1"), d("5.0")), d("5"));

        let mut values = vec![d("3"), d("-2.5"), d("0.1"), d("-10"), d("2.99")];
        values.sort();
        assert_eq!(format!("{values:?}"), "[-10, -2.5, 0.1, 2.99, 3]");

        // Equal values are one key, whatever their scale or the sign of a zero:
        // 1 (five ways), 10^20 (two ways), 0 (two ways) and -1.
        let texts = [
            "1",
            "1.0",
            "1.000",
            "100000000000000000000",
            "100000000000000000000.00",
        ];
        let set: HashSet<Dec> = texts
            .iter()
            .map(|text| d(text))
            .chain([d("0"), d("-0.00"), d("-1")])
            .chain([
                Dec::lit(100_000_000_000_000_000_000_000, 23),
                Dec::lit(1_000_000_000_000, 12),
            ])
            .collect();
        assert_eq!(set.len(), 4);
    }

    #[test]
    fn text() {
        assert_eq!(d("12.0").to_string(), "12.0");
        assert_eq!(d("-0.0").to_string(), "0.0");
        assert_eq!(d("-0.050").to_string(), "-0.050");
        assert_eq!(format!("{:?}", d("0.3333333333")), "0.3333333333");
        assert_eq!(
            Dec::lit(1, 28).to_string(),
            "0.0000000000000000000000000001"
        );
        let wide = Dec::from_parts(u32::MAX, u32::MAX, u32::MAX, true, 28);
        assert_eq!(wide.to_string(), "-7.9228162514264337593543950335");
        let padded = format!(
            "[{:>7}] [{:<7}] [{:+}] [{:07}]",
            d("-1.50"),
            d("2.5"),
            d("3"),
            d("-4.25")
        );
        assert_eq!(padded, "[  -1.50] [2.5    ] [+3] [-004.25]");
    }

    #[test]
    fn serde() {
        #[derive(Serialize, Deserialize, PartialEq, Debug)]
        struct Row {
            hp: Dec,
            ratio: Dec,
        }

        let row = Row {
            hp: d("12.0"),
            ratio: d("-0.75"),
        };
        let json = serde_json::to_string(&row).unwrap();
        assert_eq!(json, r#"{"hp":"12.0","ratio":"-0.75"}"#);
        let back: Row = serde_json::from_str(&json).unwrap();
        assert_eq!(
            (s(back.hp), s(back.ratio)),
            ("12.0".to_string(), "-0.75".to_string())
        );
        assert_eq!(serde_json::to_string(&d("-0.0")).unwrap(), r#""0.0""#);

        // Numbers: an integer exactly, a float through its shortest decimal text.
        let read = |json: &str| serde_json::from_str::<Dec>(json).map(s).map_err(|_| ());
        assert_eq!(read("\"1.50\""), Ok("1.50".to_string()));
        assert_eq!(read("12"), Ok("12".to_string()));
        assert_eq!(read("-7"), Ok("-7".to_string()));
        assert_eq!(
            read("18446744073709551615"),
            Ok("18446744073709551615".to_string())
        );
        assert_eq!(read("1.5"), Ok("1.5".to_string()));
        assert_eq!(read("1.50"), Ok("1.5".to_string()));
        assert_eq!(read("2.0"), Ok("2".to_string()));
        assert_eq!(read("0.1"), Ok("0.1".to_string()));
        assert_eq!(read("1e-7"), Ok("0.0000001".to_string()));
        assert_eq!(read("1e30"), Err(()));
        assert_eq!(read("\"abc\""), Err(()));
        assert_eq!(read("true"), Err(()));
    }

    #[test]
    #[should_panic(expected = "Value was either too large or too small for a Decimal.")]
    fn add_overflow_panics() {
        let _ = Dec::MAX_VALUE + Dec::ONE;
    }

    #[test]
    #[should_panic(expected = "Value was either too large or too small for a Decimal.")]
    fn mul_assign_overflow_panics() {
        let mut x = Dec::MAX_VALUE;
        x *= Dec::lit(11, 1);
    }

    #[test]
    #[should_panic(expected = "Attempted to divide by zero.")]
    fn div_by_zero_panics() {
        let _ = Dec::ONE / Dec::lit(0, 2);
    }

    #[test]
    #[should_panic(expected = "Attempted to divide by zero.")]
    fn rem_by_zero_panics() {
        let _ = Dec::ONE % Dec::ZERO;
    }

    #[test]
    #[should_panic(expected = "Value was either too large or too small for an Int32.")]
    fn cast_overflow_panics() {
        let _ = Dec::lit(2_147_483_648, 0).to_i32();
    }

    #[test]
    #[should_panic(expected = "Value was either too large or too small for a Decimal.")]
    fn from_nan_panics() {
        let _ = Dec::from_f64(f64::NAN);
    }

    #[test]
    #[should_panic(expected = "between 0 and 28")]
    fn round_to_bad_digits_panics() {
        let _ = Dec::ONE.round_to(29, MidpointRounding::ToEven);
    }
}
