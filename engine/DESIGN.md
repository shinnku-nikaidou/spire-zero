# The combat engine

A fast copy of Slay the Spire 2's combat in Rust, for the SL planner's search. The run itself (map, events, shops,
rewards) and every action actually taken stay on the real `sts2.dll`; the engine only answers "what happens if".

## Why it has this shape

Measured on 2026-11-14 (game v0.113.0): a run costs 599 CPU s, of which 578 s are the planner's search, and 81.5 % of
that restores a state by reloading the run's save and replaying the combat, because the game allows one live run per
process. The game's own step costs 95 µs. An engine whose state is plain data, copied in about a microsecond, removes
that cost; what remains is the nets.

Three rules follow from the goal and decide everything below.

1. **The game's code is the source.** The game is updated every few weeks (v0.107, v0.111, v0.113 within a month), has
   six characters and about 1,700 content classes (cards, powers, relics, monsters, potions, ...). So the engine's
   content is not written by hand: `port` translates the decompiled C# of the player's own copy of the game to Rust,
   mechanically. A new game version is a re-run, plus whatever new construct the translator meets.
2. **The engine mirrors the game's own model**: the same classes, fields, hooks, commands, random streams and ids, under
   the same names. Then a translated method means what it meant in C#, a live combat can be copied field by field, and
   what the player sees (our observation) can be read off the state exactly as `Observe.cs` reads it off the game.
3. **The real game judges every step.** A plan carries the observation hash expected after each of its steps, and is
   played on the real game. Where the engine was wrong the hashes differ, the step counts as a divergence, and the
   planner plans again from the real state. A wrong engine costs time and shows in a counter; it never makes wrong data.
   So there is no fallback path and no tolerance: a state the engine cannot take is an error, and "supported" means the
   lockstep check (below) finds no difference.

Nothing derived from the game is in git: not the generated Rust, not the schema, not dumped tables. They are built from
the user's own copy of the game, like `tmp/gamebin`.

## Pieces

| path | what | written by |
| --- | --- | --- |
| `src/Sts2Ai.Port/` | `port`: reads the decompiled game (Roslyn, with the game's other assemblies as references), writes the generated Rust and the schema | hand |
| `engine/port.toml` | what `port` does with each class: translated, written by hand, presentation | hand |
| `engine/crates/sts2ai-engine/src/rt/` | the primitives the generated code stands on: `Dec`, the random streams, handles and arenas, `Combat`, the choice mechanism, .NET's collections and LINQ, delegates | hand |
| `engine/crates/sts2ai-engine/src/manual/` | the parts of the game's core that are not translated but written by hand, one module per C# class | hand |
| `engine/crates/sts2ai-engine/src/gen/` | the translated classes, the dispatchers, the state, the kinds, the stubs; `schema.json`, `report.txt`, `manual.txt` | `port` (ignored by git) |
| `engine/crates/sts2ai-engine/src/{world,import,observe,ffi}.rs` | a combat as the brain uses it; a live combat taken in; the observation; the C ABI | hand |
| `engine/gen.sh`, `engine/quarantine.py` | decompiled game → `src/gen/`; with `--check`, until `cargo check` passes | hand |
| `src/Sts2Ai.GameBridge/EngineExport.cs` | the live combat written out along the schema (sim and mod) | hand |
| `src/Sts2Ai.Engine/` | P/Invoke, `EngineWorld : ICombatWorld`, the in-process planner; no game references | hand |
| `src/Sts2Ai.SimWorker` | `sim parity`, the lockstep check | hand |

Toolchain: Rust 1.100.0 (`engine/rust-toolchain.toml`), edition 2024. One crate, because generated and hand-written code
call each other in both directions. `gen` is a keyword of that edition, so the module is written `crate::r#gen`
(generated files call it `g`, and `crate::manual` `m`). Integer overflow checks are off in every profile: the game's
arithmetic wraps, as C#'s does.

## The translation

`port` compiles the whole decompiled tree with Roslyn only to ask it questions; it translates from Roslyn's operation
tree (`IOperation`), where every call is bound, every implicit conversion is written out, default arguments are filled
in and query syntax is already method calls. The tree has thousands of errors in generated serializers, and none that
matter in the classes we translate.

**Scope** (`engine/port.toml`). A class of the game is one of:

- *translated*: in a namespace or named in `[scope] translate`. Its members are translated one by one; a member the
  translator cannot translate becomes a `todo!("port: Class.Member: reason")` and the report lists the reason;
- *manual*: listed in `[manual]` (a class, or `Class.Member`); `port` emits nothing for it and the generated code calls
  `crate::manual::class_name::member`, written by hand with the signature `port` prints in `gen/manual.txt`. Its
  state is still generated;
- *stub*: neither. `port` emits `gen/stubs.rs` with the right signature and `todo!("Class.Member")` for every member of
  such a class that translated code uses, so the crate always compiles, and the stub list is the list of what is left;
- *presentation* (dropped): anything declared in the namespaces of `[drop] namespaces` (`Godot`,
  `MegaCrit.Sts2.Core.{Nodes,Audio,HoverTips,Animation,Assets,Bindings,Localization,...}` but not
  `Localization.DynamicVars`), the types of `[drop] types`, the members of `[drop] members` (`Cmd.Wait`,
  `CreatureCmd.TriggerAnim`, ...), and every library type outside `System`.

**What happens to presentation.** The engine is the headless game: there is no node, no sound, no text.

- A statement that only calls presentation is left out. A local of a presentation type, and the statements that use it,
  are left out.
- A presentation call in a chain (`x.WithHitFx(..)`, a `[drop] members` entry that returns its receiver) leaves the
  chain's value as it was.
- A parameter of a presentation type is not there in Rust: the function has one parameter fewer, a call passes one
  argument fewer, and what uses the parameter inside is presentation.
- A member that returns a presentation type is left out whole. So is a method in which nothing but presentation is
  left, and a member that only hands out presentation (an asset path): `port` finds those by translating, and runs again
  until it finds no more; a call to one is presentation too. A virtual one only when every override is like that.
- A null test on a presentation object says null (`node == null` is true), as in the headless game, and a test with a
  fixed value (`[value]` of `port.toml`: `TestMode.IsOn`) folds: only the branch taken is translated.
- A presentation call whose arguments do game work (`CardCmd.PreviewCardPileAdd(await CardPileCmd.Add(..))`) keeps its
  arguments: they are run, the call is not.
- Where game code needs a presentation value (a test on one guards game code, one is written to state), the member is
  not translated and the report says so. The report also lists every statement left out that held a call that is not
  presentation, for review.
- Events (`event`, `+=` on one, raising one) are presentation.

**How C# becomes Rust.** The rules are uniform, so that reading the generated function beside the C# method is checking
it.

| C# | Rust |
| --- | --- |
| a class with state (`Creature`, `CardModel`, `CombatState`, ...) | a slot in the arena of its topmost class, and a struct of its fields (`CreatureData`); a reference to it is an `Obj` (arena tag + index, `Copy`), written under the class's name (`Creature`, `CardModel`: aliases of `Obj`) |
| a subclass (`Deflect : CardModel`) | a `Kind` (`Kind::Deflect`), stored in the object; its instances are in `CardModel`'s arena; its own fields are a variant of `CardModelExt` |
| `null`, `T?` of a class, `x == null` | `Obj::NULL` (also `Str::NULL`); `rt::is_null(&x)`. A reference is never an `Option`: null is a value of the handle, as in C#, and using it panics where C# throws |
| `int?`, `decimal?` (a nullable value) | `Option<i32>`, `Option<Dec>`; every conversion Roslyn reports is written out |
| a member `M` of class `T` | a function of the module `gen/<area>/<t_snake>.rs`: `t::m(me, args.., c)`; `this` is the first parameter `me` |
| the context | `c: &mut Combat`, always the **last** argument |
| `x.Prop`, `x.Prop = v`, a field | `t::prop(x, c)`, `t::set_prop(x, v, c)`; `TData::of(x, c).field`, `TData::of_mut(x, c).field` |
| overloads | the name, then `__` and the parameter types (`get_power__model_id`); only where the name is overloaded |
| `async Task<T> M()`, `await M()` | `fn m(..) -> R<T>`, `m(..)?` |
| `M<T>(a)` where `T` is a game class | `m(Kind::T, a, c)`: the type argument is a `Kind` value, first in the list. A generic class keeps its type arguments as `Kind` fields |
| `M<T>(a)` otherwise | a Rust generic, `T: Val` |
| `x is T`, `(T)x`, `x as T` | `rt::is(x, Kind::T, c)`, `rt::cast(x, Kind::T, c)` (panics on another class), `rt::as_(x, Kind::T, c)` |
| a virtual member | `gen/dispatch.rs`: `g::dispatch::<class that declares it>::m(me, .., c)`, a `match` on the object's kind to the most derived override; `base.M()` calls the parent's function directly |
| `new T(a)` | `t::new(a, c)`: a slot with the class's kind, then `t::ctor(me, a, c)` (field initializers, the base constructor, the body) |
| `decimal`, `int`, `bool`, `string` | `Dec` (.NET's decimal, bit for bit), `i32`, `bool`, `Str` (`Copy`; text made at run time is interned) |
| an enum | a number with named values (`CardType::Attack`, `struct CardType(pub i32)`), so that casts, flags and arithmetic are C#'s |
| a struct; a class named in `[values]` (`ModelId`) | a Rust struct held by value and compared by content |
| `List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, arrays | `Vec<T>` by value; LINQ through `rt::linq` (`where_`, `select`, ...), evaluated at once. A local that only reads a list out of a field (`var hand = pile.Cards`) stands for that field, so it sees later changes as the C# reference does. A null collection is an empty one |
| `HashSet<T>`, `Dictionary<K,V>`, `Queue<T>`, `Stack<T>` | `rt::Set`, `Dict`, `Queue`, `Stack`: .NET's enumeration order (a freed slot is reused first); `List.Sort` is .NET's introsort |
| a lambda given to LINQ | a closure `\|x, c\| ..`; anywhere else, and a method group, a stored delegate `Fn1<A, R>` (clonable, can be null) |
| `out` and `ref` parameters | returned: `fn m(..) -> (T, Out1)` |
| `throw` | `panic!` (the caller turns a panic into an error) |
| a `static readonly` field | a function that gives its initial value; a static that is written is hand-written |

Canonical models (the game's `ModelDb`: one object per model class) live in arenas built once per process and never
written; a handle to one carries a flag, and `ToMutable` copies it into the combat.

## State

`port` writes `gen/schema.json`:

```
{ "format": 1,
  "arenas":  ["card_model", "creature", ...],                 // the classes that own an arena, by tag (1-based)
  "classes": [{ "name": "Creature",                           // the Rust name (the C# name, made unique)
                "cs": "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
                "base": null | "AbstractModel",
                "arena": "creature" | null,                   // null for an open class (below)
                "open": false, "abstract": false,
                "type_parameters": ["targ_t"],                // of a generic class: Kind fields
                "fields": [{ "name": "_block",                // the Rust field
                             "cs": "_block",                  // the C# field, as reflection names it
                             "type": TYPE }] }],              //   (an auto-property's: "<Block>k__BackingField")
  "structs": [{ "name", "cs", "fields" }],
  "enums":   [{ "name", "cs", "rust": "i32", "members": { "Attack": 1, ... } }] }
```

`TYPE` is `{"kind": "int" | "float", "rust": "i32"}`, `{"kind": "bool" | "decimal" | "string" | "char" | "kind"}`,
`{"kind": "enum", "name", "rust"}`, `{"kind": "ref", "class"}`, `{"kind": "struct", "name"}`,
`{"kind": "nullable" | "list" | "set" | "queue" | "stack", "of": TYPE}`, `{"kind": "dict" | "pair", "key", "value"}`,
`{"kind": "tuple", "items": [TYPE]}`. Fields of presentation types, delegates, events and tasks are not state.

- Every class that is translated or hand-written, and every class above one, has its instance fields listed
  (auto-property backing fields under the property's name). A class's fields are those of its own entry and of each
  `base` above it.
- One arena per topmost class. A class listed in `[open]` (`AbstractModel`) owns no arena: its fields are part of every
  class directly below it.
- The Rust structs are generated from the same walk, with `serde`: `Arenas` (one `Arena<TData>` per arena), `TData` per
  class, `TExt` for the fields of the classes below `T`.
- `EngineExport` (C#) walks the live combat by reflection along the schema: every object once, with its class, its
  fields by name, and references as (arena, index). A field the schema does not know is an error on the C# side, one
  the engine does not know is an error on the Rust side.
- What the game keeps outside the objects (the id counters of `NetCombatCardDb`, the combat history, the action queues'
  ids) is exported as the classes that hold it, the same way.

`Combat` is that state plus the answers to the running action's prompts: plain data, `Clone`, no pointers, no interior
mutability. Two worlds share nothing but the canonical models, so worlds are searched in threads of one process.

## Running

The game's combat is asynchronous C#; with nothing to wait for it runs straight through, and the engine does the same
synchronously. Two places need care.

**Choices.** A card prompt in the middle of an effect (`await CardSelectCmd.FromHand(..)`) is a state of the search
like any other: it must be copied and answered many ways. So an action is a function of the state before it and the
answers given so far. `apply(action)` runs it on a copy; a prompt with no answer left stops it (`Err(Stop::Choice)`),
and the world keeps the state before the action, the action, the answers, and the copy as it stood at the prompt (what
the observation shows). The next answer runs the action again from the start with one more answer. Effects are
deterministic (the random streams are part of the state), so the run up to the prompt repeats exactly. No continuation
is ever stored, and translated code stays a straight line with `?`.

**The action queue and the turn loop** (`CombatManager`, `ActionExecutor`, the `GameAction`s, the multiplayer
synchronizer) are written by hand: the order in which queued actions, hooks and the two sides' turns run is the
game's, the waiting for input and for the screen is not there.

**Random streams.** The game's `Rng` (a call counter over `MegaRandom`, xoshiro256\*\*) is `rt::random`, checked bit
for bit against the C#. `Rng.Chaotic`, the generator for things that do not matter, is presentation.

## Building

`engine/gen.sh` runs `port` on `tmp/decompiled/sts2-<version>` with `tmp/gamebin/<version>` and formats `src/gen/`.
`engine/gen.sh --check` then runs `cargo check`; a translated function rustc refuses goes on the quarantine list
(`tmp/engine/quarantine.txt`), `port` writes its body as a `todo!` and counts it, and the loop repeats until the crate
compiles. The list is for a new game version's surprises; on v0.113.0 it is empty.

`port` prints its report and writes it in full to `gen/report.txt`: classes and members translated, members not
translated with the reason, stubs, hand-written members used, statements left out, and the ones to review.

## The C ABI

`libsts2ai_engine.so`, loaded by `Sts2Ai.Engine`. A world is an opaque handle; a call on one world never touches
another, so different worlds may be used from different threads. Every function catches panics.

```c
uint64_t sz_world_import(const uint8_t *json, size_t len);        // a live combat as EngineExport wrote it; 0 on error
uint64_t sz_world_clone(uint64_t world);
void     sz_world_free(uint64_t world);
int32_t  sz_world_apply(uint64_t world, const uint8_t *action, size_t len);   // "play:10:1", "potion:0", "end_turn", "pick:0,2"; 0 ok
int32_t  sz_world_status(uint64_t world);                          // 0 in progress, 1 won, 2 lost
int32_t  sz_world_legal(uint64_t world, sz_buf *out);              // the plays, one per line, in the game's order; empty at a prompt
int32_t  sz_world_observe(uint64_t world, sz_buf *out);            // the CombatObservation's JSON, byte for byte ProtocolJson's
int32_t  sz_world_run(uint64_t world, sz_buf *out);                // what the combat has changed of the run, for RunView
uint64_t sz_world_hidden_key(uint64_t world);                      // as ICombatWorld.HiddenKey
uint64_t sz_world_state_key(uint64_t world);                       // as PimcBrain.StateKey of its observation
void     sz_buf_free(sz_buf buf);
int32_t  sz_last_error(sz_buf *out);                               // this thread's last error
```

`sz_buf` is `{ uint8_t *ptr; size_t len; }`. Actions are `CombatAction`'s text form. The observation is produced by
`observe.rs`, which mirrors `src/Sts2Ai.GameBridge/Observe.cs` member by member.

## The lockstep check

`sim parity` plays combats on the real game and, at every decision:

1. **import**: exports the combat, imports it, and compares the engine's observation with `Observe.Combat` byte for
   byte, and the legal actions in order;
2. **step**: applies the action the real game is given to the engine's own world (the one carried from the start, not
   re-imported) and compares again;
3. **continue**: plays the world imported at this decision along the real combat's remaining actions to its end,
   comparing each step, which shows hidden state the export missed.

A difference is reported with the first JSON path that differs and the ids involved. The engine supports the game
version when every card, power, relic, potion, monster, encounter, orb, enchantment and affliction id of that version
has been met in the check with no difference, and whole runs of all six characters at A0 and A10 pass it too.

## Credit

The idea of a Rust port checked in lockstep against the real game, and many answers to how the game's rules work in
detail (its notes on hook order), come from Thomas Friedel's
[spire-mentor](https://github.com/tfriedel/spire-mentor) (MIT). Its engine is written by hand against its own state
model; ours is a translation of the game's code onto the game's model, and shares no code with it. `rt::dec` and
`rt::list`'s sort follow the algorithms of the .NET runtime (MIT).
