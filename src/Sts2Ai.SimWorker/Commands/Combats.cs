using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Rooms;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>tables: every id of this game build that the nets' tables hold.</summary>
internal static class TablesCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Option<string> Out = Opt.Of("--out", "learn/game_ids.json", "where the file goes");

    public static Command Command()
    {
        var command = Opt.Command("tables", "every id of this game build the nets' tables hold, as JSON (learn/game_ids.py)");
        command.Options.Add(Out);
        command.SetAction(Cli.Game((r, _) => Run(r.GetValue(Out)!)));
        return command;
    }

    private static int Run(string path)
    {
        var relics = Real<RelicModel>().ToList();
        var ids = new Dictionary<string, IEnumerable<string>>
        {
            ["cards"] = Entries<CardModel>(),
            ["enchantments"] = Entries<EnchantmentModel>(),
            ["afflictions"] = Entries<AfflictionModel>(),
            ["powers"] = Entries<PowerModel>(),
            ["monsters"] = Entries<MonsterModel>(),
            ["relics"] = relics.Select(r => r.Id.Entry),
            ["used_up_relics"] = relics.Where(r => r.GetType().GetProperty(nameof(RelicModel.IsUsedUp))!.DeclaringType != typeof(RelicModel)).Select(r => r.Id.Entry),
            ["potions"] = Entries<PotionModel>(),
            ["encounters"] = Entries<EncounterModel>(),
            ["bosses"] = Real<EncounterModel>().Where(e => e.RoomType == RoomType.Boss).Select(e => e.Id.Entry),
            ["orbs"] = Entries<OrbModel>(),
            ["acts"] = Entries<ActModel>(),
            ["ancients"] = Entries<AncientEventModel>(),
            ["characters"] = ModelDb.AllCharacters.Select(c => c.Id.Entry),
            ["intents"] = Enum.GetNames<IntentType>(),
            ["relic_status"] = Enum.GetNames<RelicStatus>(),
            ["keywords"] = Enum.GetNames<CardKeyword>(),
            // what Tinker Time makes of a Mad Science: its card type and rider
            ["card_types"] = Enum.GetNames<CardType>(),
            ["riders"] = Enum.GetNames<TinkerTime.RiderEffect>(),
            ["points"] = Enum.GetNames<MapPointType>(),
        };
        var sorted = ids.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().Order(StringComparer.Ordinal).ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(sorted, Indented) + "\n");
        Console.WriteLine($"wrote {path}: game {HeadlessGame.GameVersion}, {sorted["cards"].Count} cards");
        return 0;

        static IEnumerable<string> Entries<T>() where T : AbstractModel => Real<T>().Select(m => m.Id.Entry);
        // The game's own test models sit in namespaces of their own (Models.Cards.Mocks).
        static IEnumerable<T> Real<T>() where T : AbstractModel => ModelDb.All.OfType<T>().Where(m => m.GetType().Namespace?.Contains(".Mock") != true);
    }
}

internal static class ListCommand
{
    public static Command Command()
    {
        var command = Opt.Command("list", "characters, acts and encounters known to this game build");
        command.SetAction(Cli.Game((_, _) => Run()));
        return command;
    }

    private static int Run()
    {
        Console.WriteLine($"game {HeadlessGame.GameVersion}");
        Console.WriteLine("characters: " + string.Join(" ", ModelDb.AllCharacters.Select(c => c.Id.Entry)));
        for (int i = 0; i < ModelDb.ActsByIndex.Count; i++)
        {
            foreach (var act in ModelDb.ActsByIndex[i])
            {
                Console.WriteLine($"act {i}: {act.Id.Entry}{(act.IsDefault ? " (default)" : "")}");
                Print("weak", act.AllWeakEncounters);
                Print("regular", act.AllRegularEncounters);
                Print("elite", act.AllEliteEncounters);
                Print("boss", act.AllBossEncounters);
            }
        }
        return 0;

        static void Print(string label, IEnumerable<EncounterModel> encounters) =>
            Console.WriteLine($"  {label,-8} " + string.Join(" ", encounters.Select(e => e.Id.Entry)));
    }
}

/// <summary>combat: one combat, printing every step.</summary>
internal static class CombatCommand
{
    private static readonly Option<string?> Encounter = Opt.Maybe<string?>("--encounter", "the encounter (see list)");
    private static readonly Option<string?> From = Opt.Maybe<string?>("--from", "an observation's JSON file: the combat built to show it, instead");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed");
    private static readonly Option<string> Policy = Opt.Of("--policy", "play", "the placeholder policy that plays").AcceptOnlyFromAmong(Policies.CombatNames);
    private static readonly Option<bool> Obs = Opt.Of("--obs", false, "prints each step's observation JSON too");
    private static readonly LoadoutArgs Loadout = new();

    public static Command Command()
    {
        var command = Opt.Command("combat", "one combat, printing every step", [Encounter, From, Seed, Policy, Obs], Loadout.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        int seed = r.GetValue(Seed);
        CombatSetup setup;
        if (r.GetValue(From) is { } path)
        {
            setup = CombatBuilder.Setup(ProtocolJson.Deserialize<CombatObservation>(File.ReadAllText(path)), GameSeeds.MakeSeed(seed, 0));
        }
        else
        {
            var id = r.GetValue(Encounter) ?? throw new CliException("combat needs --encounter ID (see list) or --from OBS.json");
            var (encounter, act) = Opt.Encounter(id);
            setup = new CombatSetup(Loadout.Character(r), encounter, act, GameSeeds.MakeSeed(seed, 0), Loadout: Loadout.Loadout(r));
        }
        var rng = new Random(seed);
        var policy = Policies.Combat(r.GetValue(Policy)!, rng);

        Console.WriteLine($"{setup.Encounter.Id.Entry} act {setup.ActIndex} seed {setup.Seed}{(setup.From != null ? " (built from observation)" : "")}");
        using var session = CombatSession.Start(pump, setup, new RandomChoices(rng));
        foreach (var difference in session.BuildDifferences) Console.WriteLine("  not reproduced: " + difference);
        while (!session.IsCapped)
        {
            Console.WriteLine("  " + session.Summary());
            if (r.GetValue(Obs)) Console.WriteLine("  " + ProtocolJson.Serialize(session.Observation()));
            var action = session.Next(policy);
            Console.WriteLine($"    -> {action}");
            session.Execute(action);
        }
        Console.WriteLine($"result {session.Outcome} after {session.ActionsExecuted} actions, round {CombatSession.State.RoundNumber}, hp {session.Player.Creature.CurrentHp}");
        Console.WriteLine("trace: " + string.Join(" ", session.Trace));
        foreach (var e in HeadlessGame.TakeErrors()) Console.WriteLine("  game error: " + e);
        return 0;
    }
}

/// <summary>bench: many combats over an encounter pool; reports throughput and failures.</summary>
internal static class BenchCommand
{
    private static readonly Option<int> N = Opt.Of("--n", 200, "combats");
    private static readonly Option<int> Start = Opt.Of("--start", 0, "the first combat's index");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base");
    private static readonly Option<string> Policy = Opt.Of("--policy", "play", "the placeholder policy that plays").AcceptOnlyFromAmong(Policies.CombatNames);
    private static readonly Option<string?> Out = Opt.Maybe<string?>("--out", "one JSON line per combat");
    private static readonly PoolArgs Pool = new();

    public static Command Command()
    {
        var command = Opt.Command("bench", "many combats over an encounter pool; reports throughput and failures", [N, Start, Seed, Policy, Out], Pool.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        int n = r.GetValue(N);
        int start = r.GetValue(Start);
        int seed = r.GetValue(Seed);
        var policy = r.GetValue(Policy)!;
        var pool = Pool.Read(r);

        using var output = r.GetValue(Out) is { } outPath ? new StreamWriter(outPath) : null;
        var outcomes = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        var failedEncounters = new Dictionary<string, int>();
        var errorTexts = new Dictionary<string, int>();
        int gameErrors = 0, combatsWithErrors = 0, firstWithErrors = -1;
        long actions = 0;

        Console.WriteLine($"bench: {n} combats, act {pool.Act} ({pool.Rooms}: {pool.Encounters.Count} encounters), policy {policy}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var setup = pool.Setup(i, GameSeeds.MakeSeed(seed, i));
            var encounter = setup.Encounter;
            var rng = new Random(seed * 1_000_003 + i);
            var started = clock.Elapsed;
            string outcome;
            string? failure = null;
            int steps = 0, rounds = 0, hp = 0;
            double startMs = 0;
            try
            {
                using var session = CombatSession.Start(pump, setup, new RandomChoices(rng));
                startMs = (clock.Elapsed - started).TotalMilliseconds;
                session.PlayOut(Policies.Combat(policy, rng));
                outcome = CombatSession.IsOver ? session.Outcome.ToString() : CombatOutcome.Truncated.ToString();
                steps = session.ActionsExecuted;
                rounds = CombatSession.State.RoundNumber;
                hp = session.Player.Creature.CurrentHp;
            }
            catch (Exception e)
            {
                outcome = "Failed";
                failure = $"{e.GetType().Name}: {Logs.FirstLine(e.Message)}";
                failures[failure] = failures.GetValueOrDefault(failure) + 1;
                failedEncounters[encounter.Id.Entry] = failedEncounters.GetValueOrDefault(encounter.Id.Entry) + 1;
                Logs.Sim.LogError(e, "combat #{Index} failed", i);
            }
            var errors = HeadlessGame.TakeErrors();
            gameErrors += errors.Count;
            if (errors.Count > 0 && combatsWithErrors++ == 0) firstWithErrors = i;
            foreach (var e in errors) errorTexts[Logs.FirstLine(e)] = errorTexts.GetValueOrDefault(Logs.FirstLine(e)) + 1;
            actions += steps;
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            output?.WriteLine(JsonSerializer.Serialize(new
            {
                i, encounter = encounter.Id.Entry, seed = setup.Seed, outcome, steps, rounds, hp,
                ms = (clock.Elapsed - started).TotalMilliseconds, start_ms = startMs, failure, game_errors = errors.Take(3),
            }));
        }
        clock.Stop();

        double secs = clock.Elapsed.TotalSeconds;
        Console.WriteLine($"done in {secs:F2}s: {n / secs:F1} combats/s, {actions / secs:F0} actions/s, {1000 * secs / n:F1} ms/combat");
        Console.WriteLine("outcomes: " + string.Join(" ", outcomes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine($"pump idle waits: {pump.IdleWaits} ({pump.IdleWaitTicks} ms)");
        Console.WriteLine($"game Log.Error lines: {gameErrors}; peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20)} MB");
        if (combatsWithErrors > 0)
            Console.WriteLine($"  {combatsWithErrors} combats logged errors, first #{firstWithErrors}");
        foreach (var (msg, count) in errorTexts.OrderByDescending(kv => kv.Value).Take(5))
            Console.WriteLine($"  game error x{count}: {(msg.Length > 300 ? msg[..300] + "..." : msg)}");
        foreach (var (msg, count) in failures.OrderByDescending(kv => kv.Value).Take(10))
            Console.WriteLine($"  failure x{count}: {msg}");
        if (failedEncounters.Count > 0)
            Console.WriteLine("  failing encounters: " + string.Join(" ", failedEncounters.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value}")));
        return failures.Count == 0 ? 0 : 1;
    }
}
