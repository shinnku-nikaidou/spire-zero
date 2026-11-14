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
  `CreatureCmd.TriggerAnim`, ...; `Class.*` takes a whole class but its `keep_members`), and every library type
  outside `System`. A type `[scope] translate` names itself is translated whatever its namespace: `LocString` is kept
  as the two strings that say where a text is in the tables (a card prompt is named by them), and everything that
  makes its words is presentation.

What is state is `port.toml`'s too, since the game's objects hold more than a combat: `[roots]` (the statics that are
state), `[fields]` (classes of which only the named fields are state: `RunManager`, `RunState`), `[drop] fields`
(fields that are not: the network, who answers prompts), `[caches]` (statics that only remember a computation: read as
unset, so the value is computed at each use), `[value]` (members with a fixed value in the headless game, as a Rust
expression: `TestMode.IsOn`, the empty list of mods).

The game the engine is held against runs with test mode on, and the simulator keeps the shipped game's rule in the
few places where test mode changes one (its `RuleFidelity`). The engine follows it there: a `[value]` for one member
where the shipped branch is code (`TestMode.IsOn@Cauldron.GenerateRewards`), and one member written by hand where the
shipped rule is drawn on the screen (Rolling Boulder damages one enemy after the other, `manual/rolling_boulder_power.rs`).

**What happens to presentation.** The engine is the headless game: there is no node, no sound, no text.

- A statement that only calls presentation is left out. A local of a presentation type, and the statements that use it,
  are left out.
- A presentation call in a chain (`x.WithHitFx(..)`, a `[drop] members` entry that returns its receiver) leaves the
  chain's value as it was.
- A parameter of a presentation type is not there in Rust: the function has one parameter fewer, a call passes one
  argument fewer, and what uses the parameter inside is presentation. `[drop] parameters` names a parameter of
  another type that only carries presentation (the text a `StringVar` shows).
- What is game code in something left out: a call into the game (not one that gives presentation back, nor one that
  takes presentation alone and gives nothing or a bare task), a call of a function that was handed over, a write to
  state. An event that is raised is presentation.
- A member that returns a presentation type is left out whole. So is a method in which nothing but presentation is
  left, and a member that only hands out presentation (an asset path): `port` finds those by translating, and runs again
  until it finds no more; a call to one is presentation too. A virtual one only when every override is like that.
- A null test on a presentation object says null (`node == null` is true), as in the headless game, and a test with a
  fixed value (`[value]` of `port.toml`: `TestMode.IsOn`) folds: only the branch taken is translated.
- A presentation call whose arguments do game work (`CardCmd.PreviewCardPileAdd(await CardPileCmd.Add(..))`) keeps its
  arguments: they are run, the call is not.
- A loop over presentation, a test or a `switch` on presentation, a lambda: left out when nothing in it is game code
  (no call into the game, no write to state or to a local from outside), and for a `switch`, when nothing after it
  could be skipped by a way out of it. A lambda like that is an empty function. A test or a loop over game values
  with nothing left under it is left out too when the test, or what the loop walks, only reads (no call, nothing
  made or written): `if (newHp < oldHp) { a sound }`.
- A local that presentation was written to (`delay += Rng.Chaotic.Next..`) is presentation from there on, and so is a
  text that only presentation reads (the words of a log line).
- A branch that takes game objects from a screen (`result = await hand.SelectCards(..)`), or returns presentation in a
  member that does more, is a `panic!` naming the member: the headless game never takes it.
- Where game code needs a presentation value otherwise (a test on one guards game code, one is written to state), the
  member is not translated and the report says so. The report also lists every statement left out that held a call
  that is not presentation, for review.
- Events (`event`, `+=` on one, raising one) are presentation, all but the few that are state (Events, below).

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
| `x is T`, `(T)x`, `x as T` | `rt::is(x, Kind::T, c)`, `rt::cast(x, Kind::T, c)` (panics on another class), `rt::as_(x, Kind::T, c)`. The handle names the arena, and for each class `kinds.rs` has the arenas that hold only Ts and those that hold some: only in the second is the object's own class read, and then compared by number (the classes are numbered so that a class and all below it are one run) |
| `ref x`, or a collection the callee changes, where `x` is a local the call alone reads | the value is handed over (`std::mem::take`) and the call's result written back right after; a member gives its `ref` and `out` parameters away where it returns |
| a virtual member | `gen/dispatch.rs`: `g::dispatch::<class that declares it>::m(me, .., c)`, a `match` on the object's kind to the most derived override; `base.M()` calls the parent's function directly |
| `new T(a)` | `t::new(a, c)`: a slot with the class's kind, then `t::ctor(me, a, c)` (field initializers, the base constructor, the body) |
| a generic struct (`SavedProperty<T>`) | a generic Rust struct. In the schema each way the state closes it is a struct of its own (`SavedPropertiesSavedPropertyOfI32`), under the name the game's assembly finds it by: the arguments with their assemblies (`..SavedProperty`1[[System.Int32, System.Private.CoreLib]]`) |
| `decimal`, `int`, `bool`, `string` | `Dec` (.NET's decimal, bit for bit), `i32`, `bool`, `Str` (`Copy`; text made at run time is interned) |
| an enum | a number with named values (`CardType::Attack`, `struct CardType(pub i32)`), so that casts, flags and arithmetic are C#'s |
| a struct; a class named in `[values]` (`ModelId`) | a Rust struct held by value and compared by content |
| `List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, arrays | `Vec<T>` by value; LINQ through `rt::linq` (`where_`, `select`, ...), evaluated at once. A local that only reads a list out of a field (`var hand = pile.Cards`) stands for that field, so it sees later changes as the C# reference does. A null collection is an empty one |
| a local of collection type that is ever set to what can be null (`null`, such a local, a call of a method whose result can be null), and the result of a method (not virtual, not async) that can return null | `Option<Vec<T>>`: `== null` is `is_none()`; read as the collection it is `rt::nn(&x)`, which panics where C# throws; handed to a parameter, null is the empty collection, as a parameter's collection is never null here. Every other collection is never null (`== null` is false), and the report lists each null test on a collection with which of the two it is |
| a collection field the game declares nullable (`HashSet<CardKeyword>? _keywords`, made when first asked for) | the collection and a flag beside it, `_keywords__set`: a collection is a value and has no null of its own. `== null`, `is null`, `??`, `??=` and `?.` on the field read the flag, a write sets it, and the import sets it from the export's `null`. Any other collection that is null is an empty one |
| a property that hands out a collection of another object (`=> GetInternalData<Data>().list`), changed in place | the other object is computed, then its field is changed |
| what follows a test that is always true and always leaves (`if (TestMode.IsOn) return;`) | not translated: it never runs |
| a member that is presentation as a whole, handed over as a function | a function that does nothing |
| a list local taken from another local (`var list2 = results; list2.Add(x)`), or from one of two fields (`var list = side == Player ? _allies : _enemies; list.Add(x)`) | the same list under two names, as in C# |
| a collection the state holds, under a second name: a local set to a field of an object reached by code (`var list = GetInternalData<Data>().covered`); the items of a list of collections, or the values of a dictionary of collections, that a loop changes in place; a get-or-insert (`if (!d.TryGetValue(k, out v)) { v = new(); d[k] = v; } v.Add(x)`); a copy of a list of collections whose items are changed (`command.Results.ToList()`) | the collection itself, as in C#: the object is reached once, and the local is its field; the loop goes by position or by key, and the item is the collection's own there; the key is reached once, the new collection is put in when the key is not there, and the local is the dictionary's value; the copy is its source, since C# copies only the outer list. A collection changed in place that is still a copy here while C# has it under another name (a list a method hands out of the state, changed by the caller: the relic grab bag's two pulls) is not translated. The report lists each collection read or changed under a second name and what became of it |
| a method written with `yield` (`IterateHookListeners`); `foreach` over a call of one | a second function `m__each(.., c, k)` whose body hands each value to `k` as it reaches it; `foreach` gives its body as `k` (a `break` ends the walk, a `return` keeps its value outside and ends it). In C# the sequence and the loop's body take turns, so what the body changes is seen by the rest of the sequence (a model an earlier listener removed is not called): here too. A virtual slot with one such body has the `__each` dispatcher. Anything else that takes the sequence (`AddRange`, LINQ) takes all of it at once: `m(..)` collects |
| `foreach` over a LINQ chain C# evaluates as it is walked (`foreach (var x in list.Where(f).Select(g))`) | a loop over the chain's source that runs the chain's operators for each value before the body, as C# does: `Where` and `OfType` go on to the next value, `Select` makes the value, `SelectMany` is a loop inside, `TakeWhile` ends the walk. So `f` reads what the body did for the values before |
| a member whose whole body is such a chain (`IEnumerable<CardModel> AllCards => AllPiles.SelectMany(p => p.Cards)`) | a sequence like one written with `yield`: it has the `m__each(.., c, k)` form, which walks the chain and hands each value to `k`, and a `foreach` over it (alone or under more operators) runs inside it. So a pile's cards are read when the walk reaches that pile |
| a local that holds such a chain or such a member's value (`IEnumerable<T> xs = list.Where(f);`) | the local is the chain's source, read where it is declared (the object, when the source is a sequence that is a property of one); every use of the local is the chain over it, evaluated there, and a `foreach` over it is one of the rows above. C# reads the source's items at each use as well, so the report lists a local with game code between where it is declared and where it is last used. There are two (Beacon of Hope, Philosopher's Stone); the code between writes a flag and flashes the relic, and the source is the list of a side's creatures |
| a change through a dictionary's key (`dict[key].Add(x)`, `dict[key].Field = v`) | the entry itself is changed |
| an enumerator taken by hand (`var e = list.GetEnumerator(); e.MoveNext(); e.Current`) | the list as it is then, and a position in it |
| a collection's `Contains` handed over as a function (`Where(seen.Contains)`) | a function over the collection as it is then |
| a change of a collection its property makes on demand (`LocalKeywords.Add(k)`) | the getter runs, then the field it returns is changed |
| `HashSet<T>`, `Dictionary<K,V>`, `Queue<T>`, `Stack<T>` | `rt::Set`, `Dict`, `Queue`, `Stack`: .NET's enumeration order (a freed slot is reused first); `List.Sort` is .NET's introsort |
| a lambda given to LINQ | a closure `\|x, c\| ..` |
| a lambda anywhere else, a method group | a delegate `Fn1<A, R>`: a function of `gen/fns.rs` and the object it is bound to (Delegates, below) |
| `x.Died += f`, `x.Died -= f`, `Died?.Invoke(a)` on an event that is state | `push` on the list of subscribers; `rt::unsubscribe`; a call of each subscriber of a copy of the list (Events, below) |
| `out` and `ref` parameters | returned: `fn m(..) -> (T, Out1)` |
| a collection parameter the callee changes (`state.RegisterStates(States)`) | returned like a `ref` parameter and written back by the caller: a collection is a value here, and C# hands over a reference. Decided per virtual slot or interface member, so every override has one signature |
| a change through a computed object (`x.GetEntry(id).Cards.Add(card)`) | the object is computed once into a local, before the arguments, as in C# |
| labels and `goto` (the decompiler's) | the block is cut into parts at its labels; a loop runs the parts in order, or the one a `goto` names; the block's locals are declared before the loop |
| `try` / `finally` | the block, then the `finally`, then the block's `return` if it had one; a `catch` that does nothing is no handler |
| `using (r) { .. }`, `using var r = ..;`, `await using` | at the end of the block (for a declaration: of the rest of its block) the resource's `Dispose` or `DisposeAsync` runs, when it is not null, as a `finally` would, if its class is the game's and that member is game code (an attack's context, whose disposing runs `AfterAttack`); a stop for a card prompt leaves without it, as the game's task waits there. A lock's scope, a timer, a node guard are left out, and so is a resource whose making is kept by hand (the selector stack). The report lists every `using` and what became of each resource |
| `value with { A = x }` | a copy with the fields set |
| a local function handed over as a delegate | a lambda at that place, under the compiler's name of the local function |
| a record's parameters; a class that keeps its primary constructor | the properties of the same names are set first in the constructor; `port` rewrites `class C(T p)` into fields and a constructor before it compiles the source (the schema names such a field as the compiled game does, `<p>P`) |
| `(T)obj` for a type parameter that is no game class by its constraints | `rt::unbox::<T>(obj)`: the object itself |
| `(object)a == b` on a class held by value | the values compared |
| `throw` | `panic!` (the caller turns a panic into an error) |
| a `static readonly` field | a function that gives its initial value; a static that is written is hand-written |

**Delegates.** A delegate of the game is a method and the object it is bound to (`Delegate.Method`,
`Delegate.Target`), and that is what one is here: `Fn0` .. `Fn5` hold the number of a function of `gen/fns.rs` and a
handle. Two of the same method on the same object are equal, as in C#, and an export can carry one.

- `gen/fns.rs` has one function for every method translated code makes a delegate of, and the table `BY_NAME` of
  them by the game's own name of the method: `<class>::<method>`, the class by its CLR full name. That name is how
  an export says what a delegate is.
- A method group (`ButtMove`, `monster.WakeUpMove`) is the method bound to its object, to nothing when it is static.
  A virtual one is in the table under every class that has a body for it: reflection names the body the object runs.
  A member that is presentation as a whole is, handed over, a function that does nothing, under the member's name.
- A lambda or a local function has no name in the source. The compiler gave it one (`<get_CanonicalVars>b__6_0`,
  `<Stun>g__Wrapper|0`), and `port` reads it from the game's assembly (`Lambdas.cs`): every place a member makes a
  delegate is an `ldftn` in its IL, in the order of the code, so the k-th compiler-made method among them is the k-th
  lambda of the source. (The code of an async member or an iterator is in its state machine's `MoveNext`.) The two
  lists must agree in length and, place by place, in the number of parameters or the local function's name; a member
  where they do not keeps its lambdas as code, and the report lists it.
- What a lambda captures decides what it is bound to, as the compiler decided it. Nothing: the method is on `<>c`,
  bound to nothing. Only `this`: the method is on the class, bound to the object. Locals: the method is on a class
  the compiler made for the scope (`<>c__DisplayClass30_0`), whose fields are the captured variables, and it is bound
  to an object of that class. Such a class is a class of the engine like any other, with an arena of its own
  (`CreatureCmdClosure30_0`): the place that makes the delegate makes the object with the captured values, and the
  function takes them from the object it is bound to.
- Known difference: C# makes one such object for a scope, the scope's lambdas share it, and a lambda sees a later
  write to a captured variable. Here every delegate gets its own object, with the values the variables have where
  the delegate is made (a variable not declared yet has its default). `port` refuses a kept lambda that writes a
  captured variable, and the report lists the ones that read a variable written after they were made. There is one,
  the card table's subscription: it reads the pile of a value whose other field, the delegate itself, is set after.
- Not data, just code (`FnN::new`): a lambda in a member with type parameters, one with a `ref` parameter or more than
  five, one whose class holds another scope's class, and those of a member the two lists above do not agree on.

Canonical models (the game's `ModelDb`: one object per model class) live in arenas built once per process
(`manual/canon.rs`, from `gen/model_table.rs`: every model class with its id, as the game's `ModelDb` finds them by
reflection). A handle to one carries a flag, every combat starts with the same ones and shares them, and a combat that
writes to one (the game fills a canonical model's caches when they are first read) gets its own copy of that arena at
that moment. `ToMutable` copies a model into the combat.

The other thing the game finds by reflection is what a model saves: the properties marked `[SavedProperty]`, which
`SavedProperties` reads when a card or a relic is written out (a card added to the deck when a combat ends goes
into the run's history this way) and writes when one is made from what was written. `port` writes them as a table
(`gen/saved.rs`: for each class its properties in the game's order, by the attribute's order and then by name, each
with the code that reads it and the code that writes it), and the two members that walk the properties are
hand-written over that table (`manual/saved_properties.rs`).

A handle is 32 bits: 21 of index, 10 of arena tag, the canonical flag.

Two members of the game announce themselves to hand-written code (`[enter]`: `m::class::enter(name, arguments.., c)`
before the member's body, `leave(c)` after it, on every way out): the `CardSelectCmd.From*` entries, because only the
outermost entry's arguments say why a prompt asks, and Whispering Earring's turn start, which is the one time the game
puts a selector of its own on the selector stack.

## State

`port` writes `gen/schema.json`:

```
{ "format": 1,
  "game": "v0.113.0",                                         // port.toml [game]
  "roots":   [{ "name": "combat_manager__instance",           // the statics that are state (port.toml [roots])
                "class": "MegaCrit.Sts2.Core.Combat.CombatManager", "member": "Instance",
                "cs": "<Instance>k__BackingField",            // the static field, as reflection names it
                "type": TYPE }],
  "arenas":  ["card_model", "creature", ...],                 // the classes that own an arena, by tag (1-based)
  "classes": [{ "name": "Creature",                           // the Rust name (the C# name, made unique)
                "cs": "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
                "base": null | "AbstractModel",
                "arena": "creature" | null,                   // null for an open class (below)
                "open": false, "abstract": false,
                "type_parameters": ["targ_t"],                // of a generic class: Kind fields
                "fields": [{ "name": "_block",                // the Rust field
                             "cs": "_block",                  // the C# field, as reflection names it
                             "type": TYPE }],                 //   (an auto-property's: "<Block>k__BackingField")
                "dropped": ["CombatBegan", "_turnLoopTask"] }],   // its other instance fields, by C# name
  "structs": [{ "name", "cs", "fields", "dropped" }],
  "enums":   [{ "name", "cs", "rust": "i32", "members": { "Attack": 1, ... } }],
  "hash": "9f2c.." }                                          // SHA-256 of this file as written without this member
```

`TYPE` is `{"kind": "int" | "float", "rust": "i32"}`, `{"kind": "bool" | "decimal" | "string" | "char" | "kind"}`,
`{"kind": "enum", "name", "rust"}`, `{"kind": "ref", "class"}`, `{"kind": "struct", "name"}`,
`{"kind": "nullable" | "list" | "set" | "queue" | "stack", "of": TYPE}`, `{"kind": "dict" | "pair", "key", "value"}`,
`{"kind": "tuple", "items": [TYPE]}`, `{"kind": "delegate"}`.

- Every class that is translated or hand-written, and every class above one, has its instance fields listed. A
  class's fields are those of its own entry and of each `base` above it.
- `dropped` names the instance fields that are left out on purpose: presentation, what cannot be state (a task, an
  event, a lock), and what `port.toml` says is not state (`[drop] fields`, or everything a `[fields]`
  entry does not name: the run outside the combat, the network). A field of the live game that is in neither list is
  one the decompiled source does not have: the C# side fails on it.
- One arena per topmost class. A class listed in `[open]` (`AbstractModel`) owns no arena: its fields are part of every
  class directly below it.
- The roots are the statics of the game that are state (`CombatManager.Instance`, `RunManager.Instance`,
  `LocalContext.NetId`, counters). In the engine they are fields of `Roots`, part of every combat (`Combat.s`);
  translated code reads and writes them there.
- The Rust structs are generated from the same walk: `Arenas` (one `Arena<TData>` per arena), `TData` per class, `TExt`
  for the fields of the classes below `T`, `Roots`.
- A copy of a combat shares its objects with the combat it was copied from: an arena is a list of handles
  (`Vec<Arc<TData>>`), a copy copies the handles, and an object is copied when one of the two first writes to it.
  What a combat never writes after it was made (the run's history, the combat's own history entries, most cards)
  is held once by every world a search makes of it. A set or dictionary does the same with its slots: the game hands
  its collections around by reference, the translated code by value, and a value that is only read costs no copy.
- A field that holds a delegate is a field of the schema, of kind `delegate`, and the classes of what lambdas capture
  (Delegates, above) are classes of the schema, each with its arena after the game's: `name` as in the engine
  (`CreatureCmdClosure30_0`), `cs` the compiler's class (`...CreatureCmd+<>c__DisplayClass30_0`), no base, the fields
  by the compiler's names (`stunMove`; `<>4__this` is `this`), a captured variable that is presentation on `dropped`.
  A field named in `[delegates] transient` of `port.toml` is on `dropped`: its delegate is never run after a decision.
- The schema has `"presentation": {"namespaces": [..], "keep": [..], "types": [..]}`:
  what `port.toml` lists under `[drop]`, as it is there. A type is presentation when its full name is on `types` (a
  `*` at the end takes every name that starts so), or the type it is nested in is, or it is no type of the game's
  assembly, or its namespace is at or below one of `namespaces` and not at or below one of `keep`. The C# side
  needs it for events: a subscriber whose method's class is presentation is not written.

## The export

`EngineExport` (C#, `src/Sts2Ai.GameBridge`) writes a live combat by reflection along the schema, and the engine reads
it (`import.rs`, with the reader `port` generates beside the structs, `gen/import.rs`). Format 1:

```json
{ "format": 1, "game": "v0.113.0", "schema": "9f2c..", "sim": { "prompts": 0 },
  "roots":  { "combat_manager__instance": ["combat_manager", 0], "local_context__net_id": 1 },
  "arenas": { "creature":   [ { "class": "Creature", "Creature": { "_block": 0, "_current_hp": 70 } } ],
              "card_model": [ { "class": "Deflect",
                                "AbstractModel": { "id": { "category": "CARD", "entry": "DEFLECT" }, "is_mutable": true },
                                "CardModel": { "_owner": ["player", 0], "_canonical_instance": ["canon", "Deflect"] } } ] },
  "outside": {} }
```

- **`schema`** is the schema's `hash`. The engine reads no export of another game version or another schema.
- **`sim`**: what the program around the game holds that later observations depend on and that is no object of the
  game: `prompts`, how many card prompts its selector was asked so far. A prompt's number seeds the order of its
  options that look the same, so the engine counts on from it.
- **`roots`**: one member per root of the schema, by its `name`, holding its value.
- **`arenas`**: one member per arena that holds an object; its value is the arena's objects, and an object's place in
  the list is its index.
- **An object**: `class` is the schema name of its own class. Then one member per class of its chain that has fields
  in the schema (or type parameters), named by that class's schema name; in it one member per type parameter (the
  schema name of the type argument) and per field, by the field's `name`. Every field of the schema is written, none
  besides.
- **Which objects**: every object reached from the roots through the schema's fields, once, numbered in the order the
  walk meets it. The same combat gives the same bytes.
- **`outside`**: the classes outside the schema that a reference pointed to, with how many references. It must be
  empty: the engine refuses an export that reaches a class it does not hold. A class a combat reaches is either in the
  schema with its real fields, or the field that reaches it is named not-state in `port.toml`. (A combat an event
  starts has the event's room below its own on the run's stack of rooms: `EventRoom` is in the schema with its one
  flag, and the event it holds is not a combat's.)

| the schema's `kind` | written as |
| --- | --- |
| `int`, `enum`, `char` | a number (an enum's underlying value, a char's UTF-16 code unit) |
| `bool` | `true` / `false` |
| `float` | a number that reads back to the same bits; a NaN or an infinity is an error |
| `decimal` | a string, as .NET writes it invariant: `"1.50"` keeps its scale |
| `string` | a string, or `null` |
| `struct` | an object: one member per field of the schema's struct, by `name`; `null` for a null class held by value |
| `nullable` | `null`, or the value |
| `list`, `queue`, `stack` | an array in the collection's own enumeration order (a stack from its top); `null` for a null collection, which the engine reads as an empty one |
| `set`, `dict` | an array in enumeration order (a dictionary's items as `[key, value]`), or `null`; with freed slots, the object below |
| `tuple`, `pair` | an array of the items; `[key, value]` |
| `kind` (a `System.Type`) | the schema name of the class, or `null` |
| `ref` | `null`; `["<arena>", index]`; `["canon", "<Class>"]` for the canonical model of a class, which is not exported as an object; `["outside", "<CLR name>"]`, refused |
| `delegate` | `null`; `{"method": "<class>::<method>", "target": REF}`: the method's declaring class by its CLR full name and the method's name, both as reflection gives them (`...Monsters.BygoneEffigy::SlashMove`, `...Cards.Unleash+<>c::<get_CanonicalVars>b__6_0`, `...CreatureCmd+<>c__DisplayClass30_0::<Stun>g__Wrapper\|0`); `target` is `null` for a static method and for a lambda on `<>c`, else a reference like any other, to a game object or to an object of what the lambda captured. A delegate made of more than one is an error. The engine refuses a method it has no function for |

An export is taken at a decision of the player with nothing running: the action queues are empty, no prompt is open.

A `HashSet` or `Dictionary` from which items were removed keeps freed slots, and the next items added fill them
before the end: its later enumeration order depends on them (an empty one with two freed slots already reverses its
next two adds). Such a collection is written as `{"items": [..], "at": [..], "free": [..]}`: the items in enumeration
order, the slot index of each, and the freed slots in the order they are filled again. Every slot below the count is
in exactly one of `at` and `free`; the engine builds its `Set` / `Dict` with exactly those slots and refuses anything
else.

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

**The action queue and the turn loop.** In the game these are tasks that wait on each other: the executor runs
queued actions, the turn loop waits for "every player is ready to end the turn", runs the turn's end, waits for
"ready for the enemies' turn", runs the enemies, starts the next turn. Here the members that wait are written by hand
(`manual/`, listed in `port.toml [manual]`, about twenty; the rest of `CombatManager`, the queues and the actions is
the game's code) and the two run in turn: `world.rs` starts an action through the entry the game's screen uses
(`CardModel.TryManualPlay`, `PotionModel.EnqueueManualUse`, `PlayerCmd.EndTurn`), then runs the executor until its
queues are empty, then one step of the turn loop, and again until neither moves. The turn loop is a state machine
with two waits (`manual/combat_manager.rs`); the game's two signals are fields of what the engine keeps beside the
game's objects (`Combat.x`).

- A task the game starts and does not await (`TaskHelper.RunSafely(..)`) runs where it is started.
- The fight round cap is the simulator's rule, not the game's, and `world.rs` has it as the simulator does: when the
  player can act again in round 81, the player is killed by the game's own kill command, forced, and the win
  condition is checked.
- A hook that asks the player in the game is left waiting while the turn goes on and finishes later as an action of
  its own. Here its prompt stops the step like any other, and when the step is run again with the answer, the hook's
  rest runs where the hook stands, before what follows it. No combat of the check has shown a difference from this;
  it is a known place to look when one does.
- Who answers a card prompt: the game asks the selector on top of the player's stack (`ICardSelector`). The engine has
  one selector, written by hand (`manual/i_card_selector.rs`): it takes the next answer or stops the step, and gives
  the prompt its number. The stack is not kept as objects.

**Events.** An event of the game is a list of delegates, called in the order they subscribed. Nearly all are how
the game tells its screen that something changed. An event is state when a subscriber is game code: a member of an
engine class that is not presentation as a whole. Then who is subscribed decides what the game does, and the event
is a field of its class, the list of its subscribers (`Vec<FnN>`, under the event's name): `+=` adds to the end,
`-=` takes out the last subscriber equal to the handler (the same method on the same object), and raising it
(`Died?.Invoke(x)`, `Died(x)`) calls the subscribers there are at that moment, in order; with none, the arguments
are not evaluated. A subscriber that is a presentation member of an engine class is in the list all the same, as a
function that does nothing, because the game's list has it. Four events are state: a pile's `ContentsChanged` (the
combat's card table gives a card its id when it shows up in a pile), the combat manager's `CombatEnded` (the card
table lets go of the piles), a creature's `Died` (a pet leaves its owner's list; two monsters unsubscribe
themselves), a player's `RelicObtained` (a card reward that is open).

In the schema such an event is a field of type list of `delegate` under the event's own name, and an export has the
subscribers in order without those whose class is presentation (`presentation` of the schema): the screen's nodes,
`CombatStateTracker`, `RewardSynchronizer`. The events of the action queue, the executor and the turn loop
(`ActionQueueSet.ActionQueueChanged`, `GameAction.AfterFinished`, `CombatState.CreaturesChanged`) are on
`[drop] fields`: the hand-written core does what they announce. The report lists every event of the engine's classes
that someone subscribes to, with each subscriber and what it is.

**Delegates in the state.** A monster's move states and a model's vars hold delegates (what a move does, when a
branch is taken, what a calculated var adds). An export carries them, and import reads each as the function of the
table under its method's name, bound to the object the export names: a game object, or the object of what a lambda
captured. So a move a monster made on the spot (a stun, with the move to go back to captured) is imported like any
other.

**Kept walks.** Every hook of the game walks the combat's listeners: `CombatState.IterateHookListeners` (and the
run's, around it) builds a list of every power, monster, relic, potion, orb, card of every pile with its enchantment
and affliction, and hands on each that passes `Contains`. A card's view alone walks it five times. The two walks are
named in `port.toml [memo]`, and for them port writes the list's building apart from the loop (`rt/memo.rs`): a
combat counts the writes that could change what the walks read (`Arenas.writes`), and the list built at a count is
kept with the combat, with each item's `Contains` answer once it is asked. The next walk at the same count, of the
same object and argument, takes the list and the answers as they are. When the code an item was handed to writes, the
count moves and the rest of that walk asks `Contains` as the game does, item by item. Which writes count, port works
out from the game's code: from the statements that build the list and from the test it follows every call at any
depth, every implementation of a virtual member and every getter, and gathers the classes whose own fields are read
(sixteen: the combat, its creatures and players, piles, orb queues, the models' fields the walk looks at, and
`AbstractModel`'s, which every model's getters check). A field is written only through the `of_mut` of the class that
declares it, and a new object changes none that is there, so the `of_mut` of these classes count and nothing else
does: a choice context made for each listener of a hook, or a relic's counter, leaves the lists kept. A walk that
reads a root, or calls code the walk cannot see into (a delegate it is handed), stops the translation; the report
lists the classes with the first read found for each. A copy of a combat has the count and the lists it was copied
with, and each one's count only goes up from there, so a list kept at a count is the list of the one combat that
stood there. A list built by code that writes what the walks read (a getter that fills a cache) is not kept. The
getters named there are kept the same way, for each object: a card's pile, which every getter of a card that looks at
its combat asks, is found once for each card at a write count (the classes it reads are counted too), in an array by
the card's index shared like the answers.

**What a hook does nothing for.** Most listeners do not override the member a hook calls, and for them the member's
own body does nothing. A loop over a walk whose body only calls one virtual member of each value, or only tests it
(`if (item.ShouldStopCombatFromEnding()) ..`), where that member's own body leaves everything as it is for these
arguments (it sets each out parameter to the value handed in for it and returns a constant, false for a test:
`item.TryModifyStarCost(card, cost, out cost)` against `{ modifiedCost = originalCost; return false; }`), sets
`Combat::only` to the kinds that override the member right before it calls the walk. Every walk takes
it first thing, so no walk below sees it; a walk hands on only values of those kinds (and null, which C# would call and
throw on), passes it on to a walk it only forwards (`foreach (var x in inner) yield return x;`), and a kept walk goes
through the positions of those kinds alone, found once for each kept list, before any test is asked. Seventeen
hooks: among them a card's energy cost (early and late), star cost and keywords, whether the combat may end, whether a
turn is taken again.

**Random streams.** The game's `Rng` (a call counter over `MegaRandom`, xoshiro256\*\*) is part of the state like any
other class. `rt::random` is the same generator written by hand and checked bit for bit against the C#.
`Rng.Chaotic`, the generator for things that do not matter, is presentation.

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
uint64_t sz_world_step(uint64_t world, const uint8_t *action, size_t len, sz_node *node);   // the world after the action; 0 on error
int32_t  sz_world_status(uint64_t world);                          // 0 in progress, 1 won, 2 lost
int32_t  sz_world_legal(uint64_t world, sz_buf *out);              // the plays, one per line, in the game's order; empty at a prompt
int32_t  sz_world_observe(uint64_t world, sz_buf *out);            // the CombatObservation's JSON, byte for byte ProtocolJson's
int32_t  sz_world_run(uint64_t world, sz_buf *out);                // what the combat has changed of the run, for RunView
uint64_t sz_world_hidden_key(uint64_t world);                      // as ICombatWorld.HiddenKey
uint64_t sz_world_state_key(uint64_t world);                       // as PimcBrain.StateKey of its observation
void     sz_buf_free(sz_buf buf);
int32_t  sz_last_error(sz_buf *out);                               // this thread's last error
```

`sz_buf` is `{ uint8_t *ptr; size_t len; }`. Actions are `CombatAction`'s text form.

`sz_world_step` is how search makes a node: the world after the action is made of `world`, which stays as it is (what a
snapshot is to search) and is freed on its own, in either order. It is one copy of the combat where a clone and an
apply are two, and `sz_node` says what search reads of every state it reaches, so a state met before costs no further
call:

```c
typedef struct {
    int32_t  status;      // as sz_world_status
    int32_t  round;       // the observation's round
    int32_t  prompt;      // 1 where a card prompt waits
    uint64_t state_key;   // as sz_world_state_key; 0 once the combat is over
    uint64_t hidden_key;  // as sz_world_hidden_key; 0 once the combat is over
} sz_node;
```

`sz_world_apply` is the same step with the world before let go, for a line played without coming back. At a card
prompt the world keeps the combat as it was before the prompted action, and every world along that action's prompts
holds the same one.
 The observation is produced by
`observe.rs`, which mirrors `src/Sts2Ai.GameBridge/Observe.cs` member by member. A world reads its observation once
where it stands: the state key is its hash, and the hidden key takes the draw pile's card keys from it (the same
cards, in the pile's own order) and reads only the random streams, the monsters' moves and the turn's history beside.

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

The crate has the same checks without the game, over a dump of combats the game played (`sim parity --world none
--dump DIR`: for every decision the export, the observation, the legal actions and the action taken), named by
`STS2AI_ENGINE_FIXTURES`: `cargo test --release --test fixtures` (`import`, `step`, `continued` as above, through the
C ABI: a world is held against a record by its observation, its legal actions, its status, what it changed of the
run and its two keys) and `--test world` (every combat is played through by the world alone, no action refused). A
dump is of one schema: it is stale when the schema's hash changes. `--test speed -- --ignored --nocapture` measures the
engine's own operations on the middle decision of the dump's combats (import, clone, apply by kind of action, the
observation and what its parts cost, the keys) and what a copy costs arena by arena; its `spin` does one kind of call
again and again and samples its own stack a thousand times a second (a timer signal and the C library's `backtrace`,
no other tool), and `engine/sample.py` names the addresses and counts which functions were on the stack.
`engine/reach.py` lists the members without code that a combat could reach, each with the chain of callers.

## Credit

The idea of a Rust port checked in lockstep against the real game, and many answers to how the game's rules work in
detail (its notes on hook order), come from Thomas Friedel's
[spire-mentor](https://github.com/tfriedel/spire-mentor) (MIT). Its engine is written by hand against its own state
model; ours is a translation of the game's code onto the game's model, and shares no code with it. `rt::dec` and
`rt::list`'s sort follow the algorithms of the .NET runtime (MIT).
