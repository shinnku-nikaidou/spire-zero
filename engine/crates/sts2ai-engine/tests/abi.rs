//! The C ABI's own promises (engine/DESIGN.md, and what src/Sts2Ai.Engine assumes of it), apart from what a world
//! shows: a failed call is an error and no crash, the error is the calling thread's, worlds share nothing and may be
//! freed by any thread. The worlds are imported from the dump `STS2AI_ENGINE_FIXTURES` names.

mod support;

use sts2ai_engine::ffi::{self, SzBuf};
use support::dump::{Fixture, Tally, sweep};

fn last_error() -> String {
    let mut buf = SzBuf::EMPTY;
    assert_eq!(unsafe { ffi::sz_last_error(&mut buf) }, 0);
    let text = String::from_utf8_lossy(unsafe { std::slice::from_raw_parts(buf.ptr, buf.len) }).into_owned();
    unsafe { ffi::sz_buf_free(buf) };
    text
}

/// The first export of the dump: a combat at its first decision.
fn an_export() -> Vec<u8> {
    let found = std::sync::Mutex::new(None::<Vec<u8>>);
    sweep(|fixture: &Fixture| {
        if fixture.has_export(0) {
            found.lock().unwrap().get_or_insert_with(|| fixture.bytes(0, "export.json"));
        }
        Tally::default()
    });
    found.into_inner().unwrap().expect("the dump has no export")
}

fn import(export: &[u8]) -> u64 {
    let world = unsafe { ffi::sz_world_import(export.as_ptr(), export.len()) };
    assert_ne!(world, 0, "sz_world_import: {}", last_error());
    world
}

#[test]
fn a_refused_import_says_why_to_its_own_thread() {
    let not_an_export = b"refuse";
    assert_eq!(unsafe { ffi::sz_world_import(not_an_export.as_ptr(), not_an_export.len()) }, 0);
    let said = last_error();
    assert!(!said.is_empty(), "a refused import says nothing");
    assert_eq!(
        std::thread::spawn(last_error).join().unwrap(),
        "",
        "another thread hears this thread's error"
    );

    let not_text = [0xffu8, 0xfe];
    assert_eq!(unsafe { ffi::sz_world_import(not_text.as_ptr(), not_text.len()) }, 0);
    assert!(last_error().contains("UTF-8"), "{}", last_error());
}

#[test]
fn a_failed_call_is_an_error() {
    let mut buf = SzBuf {
        ptr: std::ptr::dangling_mut(),
        len: 7,
    };
    assert_eq!(unsafe { ffi::sz_world_observe(0, &mut buf) }, 1);
    assert!(buf.ptr.is_null() && buf.len == 0, "a failed call leaves a buffer to free");
    assert!(last_error().contains("handle is 0"), "{}", last_error());
    unsafe { ffi::sz_buf_free(buf) };

    assert_eq!(unsafe { ffi::sz_world_status(0) }, -1);
    assert_eq!(unsafe { ffi::sz_world_apply(0, b"end_turn".as_ptr(), 8) }, 1);
    assert_eq!(unsafe { ffi::sz_world_clone(0) }, 0);
    assert_eq!(unsafe { ffi::sz_world_legal(0, std::ptr::null_mut()) }, 1);
    assert!(last_error().contains("null pointer"), "{}", last_error());
    unsafe { ffi::sz_world_free(0) };
}

#[test]
fn a_call_that_works_clears_the_error() {
    let world = import(&an_export());
    assert_eq!(unsafe { ffi::sz_world_status(0) }, -1);
    assert!(!last_error().is_empty());
    assert_eq!(unsafe { ffi::sz_world_status(world) }, 0, "{}", last_error());
    assert_eq!(last_error(), "");
    unsafe { ffi::sz_world_free(world) };
}

#[test]
fn worlds_share_nothing_and_any_thread_frees_one() {
    let export = an_export();
    let world = import(&export);
    let copies: Vec<u64> = (0..8).map(|_| unsafe { ffi::sz_world_clone(world) }).collect();
    assert!(copies.iter().all(|copy| *copy != 0), "sz_world_clone: {}", last_error());
    // Each copy is used in a thread of its own while the others are, and freed there; the first world is freed by a
    // thread that never used it.
    let threads: Vec<_> = copies
        .into_iter()
        .map(|copy| {
            std::thread::spawn(move || {
                let mut statuses = Vec::new();
                for _ in 0..200 {
                    statuses.push(unsafe { ffi::sz_world_status(copy) });
                    let again = unsafe { ffi::sz_world_clone(copy) };
                    statuses.push(unsafe { ffi::sz_world_status(again) });
                    unsafe { ffi::sz_world_free(again) };
                }
                unsafe { ffi::sz_world_free(copy) };
                statuses
            })
        })
        .collect();
    std::thread::spawn(move || unsafe { ffi::sz_world_free(world) }).join().unwrap();
    for thread in threads {
        let statuses = thread.join().unwrap();
        assert!(
            statuses.iter().all(|status| *status == 0),
            "a world in its own thread did not stay in progress: {statuses:?}"
        );
    }
}
