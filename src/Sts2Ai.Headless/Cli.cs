using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Ai.Headless;

/// <summary>
/// Command line:
///   list                                   characters, acts and encounters known to this game build
///   combat --encounter ID [--seed N] [--policy random|play] [--obs] [--verbose]
///                                          one combat, printing every step (--obs: the observation JSON too)
///   bench [--n 200] [--start 0] [--seed 1] [--act 0] [--rooms weak,regular] [--policy play] [--out results.jsonl]
///                                          many combats over an encounter pool; reports throughput and failures
///   selftest [--n 20] [--seed 1] [--act 0] [--rooms weak,regular]
///                                          checks determinism, restore and determinize over an encounter pool
///   forkbench [--n 30] [--seed 1] [--act 0] [--rooms weak,regular] [--branches 20]
///                                          cost of restoring a combat and of one search branch
/// Loadout options for every command that runs combats: [--character IRONCLAD] [--deck A*5,B+,...]
/// [--relics A,B] [--potions A,B] [--hp N] [--max-hp N]. Omitted parts keep the character's starting values.
/// </summary>
internal static class Cli
{
    private const int MaxRounds = 50;
    private const int MaxActions = 1000;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: list | combat --encounter ID | bench | selftest | forkbench"); return 2; }
        var opts = ParseOptions(args.Skip(1).ToArray());
        HeadlessGame.Boot(opts.ContainsKey("verbose"));
        var pump = Pump.InstallOnCurrentThread();
        return args[0] switch
        {
            "list" => List(),
            "combat" => OneCombat(pump, opts),
            "bench" => Bench(pump, opts),
            "selftest" => SelfTest.Run(pump, opts),
            "forkbench" => ForkBench.Run(pump, opts),
            _ => Fail($"unknown command {args[0]}"),
        };
    }

    private static int List()
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

    private static int OneCombat(Pump pump, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("encounter", out var id)) return Fail("combat needs --encounter ID (see `list`)");
        var encounter = ModelDb.AllEncounters.FirstOrDefault(e => e.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (encounter == null) return Fail($"no encounter {id}");
        int seed = Int(opts, "seed", 1);
        int act = Enumerable.Range(0, ModelDb.ActsByIndex.Count).FirstOrDefault(i => ModelDb.ActsByIndex[i].Any(a => a.AllEncounters.Contains(encounter)));
        var setup = Setup(opts, encounter, act, MakeSeed(seed, 0));
        var rng = new Random(seed);
        var policy = opts.GetValueOrDefault("policy", "play");

        Console.WriteLine($"{encounter.Id.Entry} act {act} seed {setup.Seed}");
        using var session = CombatSession.Start(pump, setup, new RandomChoices(rng));
        while (!session.IsOver && session.State.RoundNumber <= MaxRounds && session.ActionsExecuted < MaxActions)
        {
            Console.WriteLine("  " + session.Summary());
            if (opts.ContainsKey("obs")) Console.WriteLine("  " + Observation.Of(session).ToJsonString());
            var action = Choose(policy, session.LegalActions(), rng);
            Console.WriteLine($"    -> {action}");
            session.Execute(action);
        }
        Console.WriteLine($"result {session.Outcome} after {session.ActionsExecuted} actions, round {session.State.RoundNumber}, hp {session.Player.Creature.CurrentHp}");
        Console.WriteLine("trace: " + string.Join(" ", session.Trace));
        foreach (var e in HeadlessGame.TakeErrors()) Console.WriteLine("  game error: " + e);
        return 0;
    }

    private static int Bench(Pump pump, Dictionary<string, string> opts)
    {
        int n = Int(opts, "n", 200);
        int start = Int(opts, "start", 0);
        int seed = Int(opts, "seed", 1);
        var policy = opts.GetValueOrDefault("policy", "play");
        var (act, rooms, pool) = Pool(opts);
        if (pool.Count == 0) return Fail("empty encounter pool");

        using var output = opts.TryGetValue("out", out var outPath) ? new StreamWriter(outPath) : null;
        var outcomes = new Dictionary<string, int>();
        var failures = new Dictionary<string, int>();
        var failedEncounters = new Dictionary<string, int>();
        var errorTexts = new Dictionary<string, int>();
        int gameErrors = 0, combatsWithErrors = 0, firstWithErrors = -1;
        long actions = 0;

        Console.WriteLine($"bench: {n} combats, act {act} ({rooms}: {pool.Count} encounters), policy {policy}, game {HeadlessGame.GameVersion}{HeadlessGame.JitNote}");
        var clock = Stopwatch.StartNew();
        for (int i = start; i < start + n; i++)
        {
            var encounter = pool[i % pool.Count];
            var setup = Setup(opts, encounter, act, MakeSeed(seed, i));
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
                PlayOut(session, policy, rng);
                outcome = session.IsOver ? session.Outcome.ToString() : CombatOutcome.Truncated.ToString();
                steps = session.ActionsExecuted;
                rounds = session.State.RoundNumber;
                hp = session.Player.Creature.CurrentHp;
            }
            catch (Exception e)
            {
                outcome = "Failed";
                failure = $"{e.GetType().Name}: {FirstLine(e.Message)}";
                failures[failure] = failures.GetValueOrDefault(failure) + 1;
                failedEncounters[encounter.Id.Entry] = failedEncounters.GetValueOrDefault(encounter.Id.Entry) + 1;
                if (HeadlessGame.Verbose) Console.Error.WriteLine(e);
            }
            var errors = HeadlessGame.TakeErrors();
            gameErrors += errors.Count;
            if (errors.Count > 0 && combatsWithErrors++ == 0) firstWithErrors = i;
            foreach (var e in errors) errorTexts[FirstLine(e)] = errorTexts.GetValueOrDefault(FirstLine(e)) + 1;
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

    /// <summary>Plays until the combat ends or hits the round/action cap. Calls <paramref name="beforeAction"/> before each action.</summary>
    public static void PlayOut(CombatSession session, string policy, Random rng, Action<CombatSession>? beforeAction = null)
    {
        while (!session.IsOver && session.State.RoundNumber <= MaxRounds && session.ActionsExecuted < MaxActions)
        {
            beforeAction?.Invoke(session);
            session.Execute(Choose(policy, session.LegalActions(), rng));
        }
    }

    /// <summary>
    /// Placeholder policies until the brain exists. "random" is uniform over legal actions (it ends turns
    /// early a lot); "play" keeps playing random cards and only ends the turn when nothing else is legal.
    /// </summary>
    public static CombatAction Choose(string policy, List<CombatAction> legal, Random rng)
    {
        if (policy == "random") return legal[rng.Next(legal.Count)];
        var plays = legal.Where(a => a.Kind == CombatActionKind.PlayCard).ToList();
        if (plays.Count > 0) return plays[rng.Next(plays.Count)];
        var potions = legal.Where(a => a.Kind == CombatActionKind.UsePotion).ToList();
        if (potions.Count > 0 && rng.Next(4) == 0) return potions[rng.Next(potions.Count)];
        return legal.First(a => a.Kind == CombatActionKind.EndTurn);
    }

    public static CombatSetup Setup(Dictionary<string, string> opts, EncounterModel encounter, int act, string seed)
    {
        var loadout = Loadout.Parse(opts.GetValueOrDefault("deck"), opts.GetValueOrDefault("relics"), opts.GetValueOrDefault("potions"),
            opts.GetValueOrDefault("hp"), opts.GetValueOrDefault("max-hp"));
        return new CombatSetup(opts.GetValueOrDefault("character", "IRONCLAD"), encounter, act, seed, Loadout: loadout);
    }

    /// <summary>The encounters of one act's room kinds (--act, --rooms), or just --encounter if given.</summary>
    public static (int Act, string Rooms, List<EncounterModel> Pool) Pool(Dictionary<string, string> opts)
    {
        if (opts.TryGetValue("encounter", out var id))
        {
            var encounter = ModelDb.AllEncounters.First(e => e.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase));
            int encounterAct = Enumerable.Range(0, ModelDb.ActsByIndex.Count).FirstOrDefault(i => ModelDb.ActsByIndex[i].Any(a => a.AllEncounters.Contains(encounter)));
            return (encounterAct, encounter.Id.Entry, [encounter]);
        }
        int act = Int(opts, "act", 0);
        var rooms = opts.GetValueOrDefault("rooms", "weak,regular");
        var pool = ModelDb.ActsByIndex[act]
            .SelectMany(a => rooms.Split(',').SelectMany(r => r switch
            {
                "weak" => a.AllWeakEncounters,
                "regular" => a.AllRegularEncounters,
                "elite" => a.AllEliteEncounters,
                "boss" => a.AllBossEncounters,
                _ => throw new ArgumentException("unknown room kind " + r),
            }))
            .Distinct().ToList();
        return (act, rooms, pool);
    }

    /// <summary>A game-style seed string (the game's alphabet, 12 chars), derived deterministically from (seed, index).</summary>
    public static string MakeSeed(int seed, int index)
    {
        const string alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var rng = new Random(unchecked(seed * 1_000_003 + index * 7_919) ^ 0x5eed);
        return string.Concat(Enumerable.Range(0, 12).Select(_ => alphabet[rng.Next(alphabet.Length)]));
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var opts = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException("unexpected argument " + args[i]);
            var key = args[i][2..];
            opts[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        }
        return opts;
    }

    public static int Int(Dictionary<string, string> opts, string key, int fallback) =>
        opts.TryGetValue(key, out var v) ? int.Parse(v) : fallback;

    public static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
