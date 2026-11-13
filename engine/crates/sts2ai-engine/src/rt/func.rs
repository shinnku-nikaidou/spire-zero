//! C# delegates: a function value that can be stored, copied and null. The combat is always the last argument.

use super::Combat;
use std::sync::Arc;

macro_rules! delegate {
    ($name:ident $(, $arg:ident : $ty:ident)*) => {
        pub struct $name<$($ty,)* R>(Option<Arc<dyn Fn($($ty,)* &mut Combat) -> R + Send + Sync>>);

        impl<$($ty,)* R> $name<$($ty,)* R> {
            pub const NULL: Self = $name(None);
            pub fn new(f: impl Fn($($ty,)* &mut Combat) -> R + Send + Sync + 'static) -> Self {
                $name(Some(Arc::new(f)))
            }
            pub fn is_null(&self) -> bool {
                self.0.is_none()
            }
            /// Calls it. Panics on null, where C# throws NullReferenceException.
            pub fn call(&self, $($arg: $ty,)* c: &mut Combat) -> R {
                (self.0.as_ref().expect("null delegate"))($($arg,)* c)
            }
        }
        impl<$($ty,)* R> Clone for $name<$($ty,)* R> {
            fn clone(&self) -> Self {
                $name(self.0.clone())
            }
        }
        impl<$($ty,)* R> Default for $name<$($ty,)* R> {
            fn default() -> Self {
                $name(None)
            }
        }
        impl<$($ty,)* R> PartialEq for $name<$($ty,)* R> {
            fn eq(&self, other: &Self) -> bool {
                match (&self.0, &other.0) {
                    (None, None) => true,
                    (Some(a), Some(b)) => Arc::ptr_eq(a, b),
                    _ => false,
                }
            }
        }
        impl<$($ty,)* R> std::fmt::Debug for $name<$($ty,)* R> {
            fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str(if self.0.is_some() { concat!(stringify!($name), "(..)") } else { "null" })
            }
        }
        impl<$($ty,)* R> super::IsNull for $name<$($ty,)* R> {
            fn is_null(&self) -> bool {
                self.0.is_none()
            }
        }
    };
}

delegate!(Fn0);
delegate!(Fn1, a: A);
delegate!(Fn2, a: A, b: B);
delegate!(Fn3, a: A, b: B, d: D);
delegate!(Fn4, a: A, b: B, d: D, e: E);
delegate!(Fn5, a: A, b: B, d: D, e: E, f: F);
