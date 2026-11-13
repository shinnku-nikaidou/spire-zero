using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using Sts2Ai.Engine;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

/// <summary>
/// parity's --dump: the real combats of a sweep as files, so that the engine can be held against the game with no game
/// there (its own tests). A combat is a folder; each of its decisions has the live combat as the engine imports it and
/// everything the engine must then say of it, and the action the game was given: a world imported at decision k and
/// given action k must say what the files of decision k + 1 say. <see cref="Readme"/> is the layout, written into the dump.
/// </summary>
internal sealed class CombatDump(EngineExport export, string root)
{
    /// <summary>What the dump keeps of the live combat where it stands (a <see cref="Checks.Keep"/>).</summary>
    public Kept Keep(Player player, CombatStatus status, bool prompt, CombatObservation observation)
    {
        bool going = status == CombatStatus.InProgress;
        var state = CombatManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("no combat state");
        var run = new RunChange(player.Creature.CurrentHp, player.Creature.MaxHp, player.Gold,
            [.. player.Deck.Cards.Select(c => Observe.Card(c)).OrderBy(ProtocolJson.CardKey, StringComparer.Ordinal)],
            [.. player.Relics.Select(Observe.Relic)], [.. player.PotionSlots.Select(p => p?.Id.Entry)]);
        return new(going && prompt,
            going && !prompt ? export.Write(HeadlessGame.GameVersion) : null,
            ProtocolJson.Serialize(run),
            going ? new Keys(HiddenState.Full(state, player), PimcBrain.StateKey(observation), HiddenState.KeyText(state, player), HiddenState.FullText(state, player)) : null);
    }

    /// <summary>The combat's folder: what was kept at each decision, and combat.json, which lists them.</summary>
    public void Write(SweptCombat combat, LockstepResult result)
    {
        string dir = Path.Combine(root, $"{combat.Index:D5}-{combat.Character}-A{combat.Ascension}-{(combat.Floor is { } floor ? $"F{floor:D2}-" : "")}{combat.Encounter}");
        Directory.CreateDirectory(dir);
        var decisions = new List<Decision>();
        for (int k = 0; k < result.Steps.Count; k++)
        {
            var step = result.Steps[k];
            var kept = step.Kept ?? throw new InvalidOperationException("the combat was played with nothing kept");
            File.WriteAllText(Path.Combine(dir, $"{k}.observation.json"), step.Shown.Observation);
            File.WriteAllText(Path.Combine(dir, $"{k}.run.json"), kept.Run);
            if (kept.Export != null) File.WriteAllBytes(Path.Combine(dir, $"{k}.export.json"), kept.Export.Bytes);
            if (kept.Keys != null) File.WriteAllText(Path.Combine(dir, $"{k}.hidden.txt"), $"{kept.Keys.HiddenKeyText}\n{kept.Keys.HiddenText}\n");
            decisions.Add(new(step.Round, step.Shown.Status switch { CombatStatus.InProgress => "in_progress", CombatStatus.Won => "won", _ => "lost" },
                kept.Prompt ? true : null, step.Shown.Legal, kept.Keys?.Hidden, kept.Keys?.State, step.Action?.ToString(),
                kept.Export is { Freed: { Count: > 0 } freed } ? freed : null));
        }
        File.WriteAllText(Path.Combine(dir, "combat.json"), ProtocolJson.Serialize(new Combat(1, HeadlessGame.GameVersion, combat.Index, combat.Character,
            combat.Ascension, combat.Encounter, combat.Act, combat.Floor, combat.Seed, combat.Deck, combat.Loadout, combat.PolicySeed, result.Outcome, decisions)));
    }

    private sealed record Combat(int Format, string Game, int I, string Character, int Ascension, string Encounter, int Act, int? Floor, string Seed, string Deck,
        string? Loadout, int PolicySeed, string Outcome, IReadOnlyList<Decision> Decisions);

    private sealed record Decision(int Round, string Status, bool? Prompt, IReadOnlyList<string> Legal, ulong? HiddenKey, ulong? StateKey, string? Action,
        IReadOnlyDictionary<string, int>? Freed);

    /// <param name="command">The command line that makes the dump.</param>
    public static string Readme(string command) => $$"""
        # Combat fixtures (`sim parity --dump`)

        Real combats of Slay the Spire 2 {{HeadlessGame.GameVersion}}, played by the simulator with a seeded random player, written out
        so that the Rust engine can be held against the game with no game there. Nothing here goes into git: it is
        derived from the game. Made by:

            sim {{command}}

        ## Layout

            README.md
            <index>-<CHARACTER>-A<ascension>-<ENCOUNTER>/     one combat; the index is its place in the sweep
                                                              (of a whole run: <run's index>-..-A<ascension>-F<floor>-<ENCOUNTER>)
              combat.json                                     what the combat is, and its decisions in order
              <k>.export.json                                 decision k: the live combat, as the engine imports it
              <k>.observation.json                            what the game shows there
              <k>.run.json                                    what the combat has changed of the run by then
              <k>.hidden.txt                                  the two texts the hidden key is the hash of

        `k` counts from 0. A record is a place where the player is asked (a play, or the answer to a card prompt), and
        the last record is the combat's end, with no action. (A combat the sweep cut at 1000 actions ends on a record
        that is still in progress.)

        ## `combat.json`

            {"format":1,"game":"v0.113.0","i":1080,"character":"SILENT","ascension":10,"encounter":"NIBBITS_WEAK","act":0,
             "seed":"...","deck":"starting","loadout":"...","policy_seed":123,"outcome":"Win",
             "decisions":[{"round":1,"status":"in_progress","legal":["play:0","play:1:0","end_turn"],
                           "hidden_key":123,"state_key":456,"action":"play:1:0"},
                          {"round":1,"status":"in_progress","prompt":true,"legal":[],"hidden_key":1,"state_key":2,"action":"pick:0,2"},
                          {"round":3,"status":"won","legal":[]}]}

        - `deck`: `starting` (the character's own loadout), `random` (drawn: `loadout` says what) or `given`; `run` for a
          combat of a whole run (`--runs`), which also has its `floor`, and whose `seed` is the run's.
        - `status`: `in_progress`, `won`, `lost`: `sz_world_status`'s 0, 1, 2.
        - `prompt`: there where the record is a card prompt, in the middle of an action. The engine imports no combat
          there, so the record has no export; a world reaches it by the action before, and `action` answers it
          (`pick:...`). The prompt itself is in the observation. `legal` is empty at a prompt.
        - `legal`: the plays in the game's order, as `sz_world_legal` writes them, one a line.
        - `action`: what the game was given at the record, in the same text (`sz_world_apply`). A `pick:` names the
          prompt's options by their index, in rising order, and the game is given them in that order: an action has
          one form, and the order matters to the game (the cards a prompt discards land in it).
        - `hidden_key`, `state_key`: 64-bit numbers, in full. Not there once the combat is over: search asks for
          neither then.
        - `freed`: there where the record's export holds a hash set or a dictionary with freed slots (items were
          removed from it), counted by the field that holds it (`Class.field`, with ` (empty)` for one that holds
          nothing now). Those are the collections the export writes in the object form below.

        ## The files of a record

        - `<k>.export.json`: `EngineExport`'s bytes (export format 1: engine/DESIGN.md). There for every record that
          is in progress and no prompt. A `set` or a `dict` of the schema is a .NET `HashSet<T>` or `Dictionary<K, V>`,
          written as an array of its items in its order of enumeration (a dict's as `[key, value]`). One that has
          freed slots is written as an object instead, `{"items": [...], "at": [...], "free": [...]}`: `items` is
          that array; `at` the slot of each item (rising); `free` the freed slots in the order adds will take them,
          the next one first. Every slot below `at`'s length plus `free`'s is in exactly one of the two. .NET
          enumerates such a collection by slot, and an add takes `free`'s first slot, or the next new slot when
          none is freed, so the order after later adds follows from this and from nothing else.
        - `<k>.observation.json`: the bytes `sz_world_observe` must write: the observation as `ProtocolJson` writes it
          (snake_case names, a null member left out, members in the order of `CombatObservation`'s declaration).
        - `<k>.run.json`: the bytes `sz_world_run` must write: `{"hp":..,"max_hp":..,"gold":..,"deck":[..],"relics":[..],
          "potions":[..]}`. `deck` is the player's deck (not the combat's piles) as cards with no uid and no upgrade
          preview, in the ordinal order of their keys (a card's key is its JSON); `potions` has one entry a slot, null
          for an empty one. A combat started on its own has no run around it; of a whole run's combat these six are
          what the combat can change of the run.
        - `<k>.hidden.txt`: two lines. The first is the text `HiddenState.Key` hashes: the draw pile's card keys in
          order, each followed by `|`; then the counter of every run stream and every player stream, each followed by
          `,`; then for each live monster `counter:next move id,`. The second is the text `HiddenState.Full` hashes,
          whose hash is `hidden_key`: the first text's hash as a number, `#`, each monster's moves so far joined by `,`
          and followed by `|`, then this turn's history entries, sorted, joined by `;`.

        ## The hashes

        `ProtocolJson.Hash` is FNV-1a 64 (offset 14695981039346656037, prime 1099511628211) over the UTF-8 bytes of
        the value's JSON as System.Text.Json writes it by default. For a text that is the text as a JSON string: in
        quotes, with `"` written `"`, `\` written `\\`, and `&`, `'`, `+`, `<`, `>`, the backtick and everything
        outside ASCII as `\uXXXX` (upper case hex digits).

        `state_key` is that hash of the observation with: `hand`, `discard` and `exhaust` each with no uids and sorted
        by card key (ordinal); `potions` sorted, the empty slots last; a prompt's `options` with no uids
        (`PimcBrain.StateKey`).

        ## Checking a world against a combat

        For every record k with an export: import it; the world's observation, plays, status, run and keys are record
        k's. Then apply `action` of k, k + 1, ...: after each the world says what the next record says, prompts
        included. A combat that ends lost is compared by its status alone at the end: nothing reads the rest of a lost
        combat. A won one is compared in full, after the game's end-of-combat work.
        """;
}
