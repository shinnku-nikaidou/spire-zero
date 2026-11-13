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
| `engine/crates/sts2ai-engine/src/rt/` | the primitives the generated code stands on: `Dec`, the random streams, arenas and handles, `Combat`, the choice mechanism, the LINQ helpers | hand |
| `engine/crates/sts2ai-engine/src/manual/` | the parts of the game's core that are not translated but written by hand, one module per C# class | hand |
| `engine/crates/sts2ai-engine/src/gen/` | the translated classes, the dispatch tables, the state structs, the stubs; `gen/schema.json` | `port` (ignored by git) |
| `engine/crates/sts2ai-engine/src/{world,import,observe,ffi}.rs` | a combat as the brain uses it; a live combat taken in; the observation; the C ABI | hand |
| `engine/gen.sh` | decompiled game → `src/gen/` | hand |
| `src/Sts2Ai.GameBridge/EngineExport.cs` | the live combat written out along the schema (sim and mod) | hand |
| `src/Sts2Ai.Engine/` | P/Invoke, `EngineWorld : ICombatWorld`, the in-process planner; no game references | hand |
| `src/Sts2Ai.SimWorker` | `sim parity`, the lockstep check | hand |

Toolchain: Rust 1.100.0 (`engine/rust-toolchain.toml`), edition 2024. One crate, because generated and hand-written code
call each other in both directions.

## The translation

`port` compiles the whole decompiled tree with Roslyn only to ask it questions (types, symbols, conversions,
nullability); the tree has thousands of errors in generated serializers, and none that matter in the classes we
translate (11 unresolved names in 9,058 method bodies of the content classes).

**Scope.** Every class the content needs, found from the content classes outward: `MegaCrit.Sts2.Core.Models.{Cards,
Powers,Relics,Monsters,Potions,Orbs,Enchantments,Afflictions,Encounters}` first, then what they reference (1,091
members of the game's core). A referenced class is one of:

- *translated*: its methods are translated like any other;
- *manual*: listed in `engine/port.toml` (a class, or `Class.Member`); `port` emits nothing for it and the generated
  code calls `crate::manual::class_name::member`, written by hand with the signature `port` prints;
- *stub*: neither yet. `port` emits `gen/stubs.rs` with the right signature and `todo!("Class.Member")`, so the crate
  always compiles, and the stub list is the list of what is left;
- *dropped*: presentation. Anything declared in `Godot`, `MegaCrit.Sts2.Core.{Nodes,Audio,HoverTips,Animation,Assets,
  Bindings,Localization (but not Localization.DynamicVars),Achievements,Platform,Debug,TestSupport}` and the members
  named in `port.toml`'s `drop` (`Cmd.Wait`, `CreatureCmd.TriggerAnim`, `SfxCmd.*`, `VfxCmd.*`, `TalkCmd.*`, ...). A
  statement that only calls dropped members is left out; a local of a dropped type and the statements that use it are
  left out; `port` lists every compound statement it dropped that held a call that is not presentation, for review.

**How C# becomes Rust.** The rules are syntactic and uniform, so that reading the generated function beside the C#
method is checking it.

| C# | Rust |
| --- | --- |
| a class with state (`Creature`, `CardModel`, `PowerModel`, `CombatState`, ...) | a `Copy` handle (`Creature`, `Card`, `Power`, ...) into an arena of `Combat`, and a struct of its fields |
| a subclass of a model (`Deflect : CardModel`) | a marker type `Deflect` with `impl CardClass for Deflect { const KIND: CardKind = CardKind::Deflect; }`; its instances are `Card` handles; its own fields are a variant of `CardData` |
| `this` in a method of class `T` | the first parameter `me` |
| the context | `c: &mut Combat`, always the **last** argument (arguments are evaluated before it is borrowed, so calls nest) |
| `x.Prop`, `x.Prop = v` | `x.prop(c)`, `x.set_prop(v, c)` |
| `x.Method(a)`, `T.Static(a)` | `x.method(a, c)`, `T::static_(a, c)` |
| `async Task<T> M()`, `await M()` | `fn m(..) -> R<T>`, `m(..)?` |
| `M<T>(a)` on a model class | `m::<T>(a, c)` |
| `x is T`, `x is T y`, `(T)x` | `x.is::<T>(c)`, `if let Some(y) = x.cast::<T>(c)`, `x.cast::<T>(c).unwrap()` |
| a virtual member | `gen/dispatch.rs`: one function per virtual member, a `match` on the kind to the most derived override; `base.M()` calls the parent's function |
| `null`, `T?` | `None`, `Option<T>`, with Roslyn's flow analysis deciding where a value is known to be there |
| `decimal`, `int`, `bool`, `string`, an enum | `Dec`, `i32`, `bool`, `Str`, a generated enum; every implicit conversion Roslyn reports is written out |
| `List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, arrays | `Vec<T>` by value; LINQ through `rt::linq` (`where_`, `select`, `first_or_default`, `any`, `count`, ...) |
| a lambda, a method group | a closure, a `fn` pointer |
| `throw` | `panic!` (the caller turns a panic into an error) |

Names: `PascalCase` members become `snake_case`; a class is the module `gen/<area>/<class_snake>.rs`.

## State

`port` writes `gen/schema.json`: for every class with state, its instance fields (auto-property backing fields under
the property's name) with their kinds: number, flag, decimal, text, enum, a reference to another object, a list, a
dictionary, a nested value. Fields of dropped types, delegates and events are not state.

- The Rust structs are generated from it, with `serde`.
- `EngineExport` (C#) walks the live combat by reflection along it: every object once, with its class, its fields by
  name, and references as (class, index). The same schema bounds the walk and names every field, so nothing is mapped by
  hand and nothing can be forgotten: a field the schema does not know is an error on the C# side, one the engine does
  not know is an error on the Rust side.
- What the game keeps outside the objects (the id counters of `NetCombatCardDb`, the combat history, the action queues'
  ids) is exported as the classes that hold it, the same way.

`Combat` is that state plus nothing else that outlives an action: plain data, `Clone`, no pointers, no interior
mutability, no globals. Two worlds share nothing, so worlds are searched in threads of one process.

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
detail (its hand port of .NET's `System.Random`, its notes on hook order), come from Thomas Friedel's
[spire-mentor](https://github.com/tfriedel/spire-mentor) (MIT). Its engine is written by hand against its own state
model; ours is a translation of the game's code onto the game's model, and shares no code with it.
