using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// Checks the properties that search and training rely on, over an encounter pool:
///  - determinism: the same setup and decisions give the same observations, step for step, and the same trace;
///  - restore: rebuilding a snapshot by replay gives exactly the observation recorded at that point;
///  - determinize: resampling hidden information leaves the observation unchanged, replays exactly, and the
///    combat plays on from there;
///  - build: a combat built from a recorded observation shows exactly that observation, uids included, restores
///    by replay, and plays on;
///  - rule fidelity: Rolling Boulder hits several enemies one at a time;
///  - speed patches: the pooled hook listener iterators yield what the game's own iterators yield;
///  - protocol: observations and actions survive a round trip through their wire form, and the brain's assemblies
///    reach no game assembly.
/// </summary>
internal static class SelfTest
{
    private sealed record Recording(List<CombatSnapshot> Snapshots, List<string> Observations, List<string> Hidden, string Trace, List<string> ProtocolErrors);

    public static int Run(Pump pump, Dictionary<string, string> opts)
    {
        int n = Cli.Int(opts, "n", 20);
        int seed = Cli.Int(opts, "seed", 1);
        var (act, rooms, pool) = Cli.Pool(opts);
        var results = new SortedDictionary<string, (int Pass, int Fail)>();
        int drawCompared = 0, drawResampled = 0, drawMovedFromTrue = 0;

        void Check(string name, bool ok, string detail)
        {
            var (pass, fail) = results.GetValueOrDefault(name);
            results[name] = ok ? (pass + 1, fail) : (pass, fail + 1);
            if (!ok) Console.WriteLine($"  FAIL {name}: {detail}");
        }

        Console.WriteLine($"selftest: {n} combats, act {act} ({rooms}: {pool.Count} encounters), game {HeadlessGame.GameVersion}");
        foreach (var root in new[] { typeof(ICombatPolicy).Assembly, typeof(CombatObservation).Assembly })
        {
            var reached = GameAssembliesReachedFrom(root);
            Check("brain reaches no game assembly", reached.Count == 0, $"{root.GetName().Name} reaches {string.Join(", ", reached)}");
        }
        Check("reference check sees the bridge's game references", GameAssembliesReachedFrom(typeof(Observe).Assembly).Contains("sts2"), "sts2 not found");
        for (int i = 0; i < n; i++)
        {
            var encounter = pool[i % pool.Count];
            var setup = Cli.Setup(opts, encounter, act, Cli.MakeSeed(seed, i));
            string label = $"#{i} {encounter.Id.Entry} {setup.Seed}";
            try
            {
                var first = Record(pump, setup, seed * 1_000_003 + i);
                var second = Record(pump, setup, seed * 1_000_003 + i);
                Check("determinism", first.Observations.SequenceEqual(second.Observations) && first.Hidden.SequenceEqual(second.Hidden) && first.Trace == second.Trace, label);
                Check("protocol round trip", first.ProtocolErrors.Count == 0, $"{label}: {first.ProtocolErrors.FirstOrDefault()}");

                var cutRng = new Random(seed ^ (i * 7_919));
                foreach (int k in Enumerable.Range(0, 3).Select(_ => cutRng.Next(first.Snapshots.Count)).Distinct())
                {
                    var snapshot = first.Snapshots[k];
                    string at = $"{label} at step {k}";
                    using (var restored = CombatSession.Restore(pump, snapshot, new RandomChoices(new Random(0))))
                    {
                        Check("restore", Json(restored) == first.Observations[k], at);
                        Check("restore hidden state", Hidden(restored) == first.Hidden[k], at);
                        Check("fast hook listeners match the game's", SameListeners(restored), at);
                    }

                    string trueOrder;
                    using (var restored = CombatSession.Restore(pump, snapshot, new RandomChoices(new Random(0))))
                        trueOrder = DrawOrder(restored);
                    var (obsA, orderA) = DeterminizeAt(pump, snapshot, 11);
                    var (obsB, orderB) = DeterminizeAt(pump, snapshot, 22);
                    Check("determinize keeps observation", obsA == first.Observations[k] && obsB == first.Observations[k], at);
                    if (DistinctCards(trueOrder) >= 2)
                    {
                        drawCompared++;
                        if (orderA != orderB) drawResampled++;
                        if (orderA != trueOrder) drawMovedFromTrue++;
                    }

                    var determinized = snapshot with { Trace = [.. snapshot.Trace, new TraceEvent(TraceKind.Determinize, Seed: 11)] };
                    using (var replayed = CombatSession.Restore(pump, determinized, new RandomChoices(new Random(k))))
                    {
                        Check("determinize replays", DrawOrder(replayed) == orderA, at);
                        Cli.PlayOut(replayed, new PlayPolicy(new Random(k)));
                        Check("plays on after determinize", replayed.ActionsExecuted >= snapshot.Trace.Count(e => e.Kind == TraceKind.Action), at);
                    }

                    BuildFrom(pump, ProtocolJson.Deserialize<CombatObservation>(first.Observations[k]), Cli.MakeSeed(seed + 1, i * 3 + k), k, at, Check);
                }
            }
            catch (Exception e)
            {
                Check("no exceptions", false, $"{label}: {e.GetType().Name}: {Cli.FirstLine(e.Message)}");
                if (HeadlessGame.Verbose) Console.Error.WriteLine(e);
            }
            var errors = HeadlessGame.TakeErrors();
            Check("no game errors", errors.Count == 0, $"{label}: {errors.FirstOrDefault()}");
        }

        RollingBoulder(pump, pool, act, Check);

        Console.WriteLine($"draw order resampled by a different determinize seed: {drawResampled}/{drawCompared}; differs from the true order: {drawMovedFromTrue}/{drawCompared}");
        foreach (var (name, (pass, fail)) in results)
            Console.WriteLine($"  {(fail == 0 ? "ok  " : "FAIL")} {name}: {pass} passed, {fail} failed");
        return results.Values.Any(r => r.Fail > 0) ? 1 : 0;
    }

    /// <summary>
    /// Builds <paramref name="obs"/>, plays a few actions, and rebuilds that point by replay. The built combat must
    /// show the observation exactly; the replay must give the same visible and hidden state.
    /// </summary>
    private static void BuildFrom(Pump pump, CombatObservation obs, string seed, int k, string at, Action<string, bool, string> check)
    {
        CombatSnapshot snapshot;
        string json, hidden;
        using (var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, seed), new RandomChoices(new Random(k))))
        {
            check("build shows the observation", built.BuildDifferences.Count == 0, $"{at}: {string.Join("; ", built.BuildDifferences.Take(4))}");
            var policy = new PlayPolicy(new Random(k));
            for (int a = 0; a < 3 && !built.IsOver; a++) built.Execute(policy.Choose(Cli.Decision(built)));
            snapshot = built.Snapshot();
            (json, hidden) = (Json(built), Hidden(built));
        }
        using var restored = CombatSession.Restore(pump, snapshot, new RandomChoices(new Random(0)));
        check("built combat restores", Json(restored) == json && Hidden(restored) == hidden, at);
        Cli.PlayOut(restored, new PlayPolicy(new Random(k)));
        check("plays on after build", restored.IsOver || restored.State.RoundNumber > obs.Round, at);
    }

    private static Recording Record(Pump pump, CombatSetup setup, int rngSeed)
    {
        var snapshots = new List<CombatSnapshot>();
        var observations = new List<string>();
        var hidden = new List<string>();
        var protocolErrors = new List<string>();
        var rng = new Random(rngSeed);
        using var session = CombatSession.Start(pump, setup, new RandomChoices(rng));
        Cli.PlayOut(session, new PlayPolicy(rng), s =>
        {
            snapshots.Add(s.Snapshot());
            var json = Json(s);
            observations.Add(json);
            hidden.Add(Hidden(s));
            if (ProtocolJson.Serialize(ProtocolJson.Deserialize<CombatObservation>(json)) != json)
                protocolErrors.Add($"observation at step {snapshots.Count - 1} changes in a round trip");
            foreach (var action in s.LegalActions())
                if (CombatAction.Parse(action.ToString()) != action || ProtocolJson.Deserialize<CombatAction>(ProtocolJson.Serialize(action)) != action)
                    protocolErrors.Add($"action {action} changes in a round trip");
        });
        return new(snapshots, observations, hidden, string.Join(" ", session.Trace), protocolErrors);
    }

    private static string Json(CombatSession session) => ProtocolJson.Serialize(session.Observation());

    /// <summary>
    /// Game assemblies (and game-side projects) that <paramref name="root"/> references, directly or through our
    /// own assemblies. Compiled references only list what the code actually uses.
    /// </summary>
    private static List<string> GameAssembliesReachedFrom(Assembly root)
    {
        string[] forbidden = ["sts2", "GodotSharp", "0Harmony", "SmartFormat", "Sts2Ai.GameBridge", "Sts2Ai.SimWorker"];
        var reached = new List<string>();
        var seen = new HashSet<string> { root.GetName().Name! };
        var pending = new Stack<AssemblyName>(root.GetReferencedAssemblies());
        while (pending.TryPop(out var name))
        {
            if (!seen.Add(name.Name!)) continue;
            if (forbidden.Contains(name.Name)) reached.Add(name.Name!);
            else if (name.Name!.StartsWith("Sts2Ai.", StringComparison.Ordinal))
                foreach (var r in Assembly.Load(name).GetReferencedAssemblies()) pending.Push(r);
        }
        return reached;
    }

    private static (string Observation, string DrawOrder) DeterminizeAt(Pump pump, CombatSnapshot snapshot, ulong seed)
    {
        using var session = CombatSession.Restore(pump, snapshot, new RandomChoices(new Random(0)));
        session.Determinize(seed);
        return (Json(session), DrawOrder(session));
    }

    private static string DrawOrder(CombatSession session) =>
        string.Join("\n", session.Player.PlayerCombatState!.DrawPile.Cards.Select(Observe.CardKey));

    /// <summary>State the observation leaves out: draw order, every RNG stream's position, each monster's queued move.</summary>
    private static string Hidden(CombatSession session)
    {
        var lines = new List<string> { DrawOrder(session) };
        lines.AddRange(Enum.GetValues<RunRngType>().Select(t => $"{t} {Show(session.Run.Rng.GetRng(t))}"));
        lines.AddRange(Enum.GetValues<PlayerRngType>().Select(t => $"{t} {Show(session.Player.PlayerRng.GetRng(t))}"));
        lines.AddRange(session.State.Enemies.Select(e => $"{e.Monster?.Id.Entry} {e.Monster?.NextMove.Id} {(e.Monster == null ? "" : Show(e.Monster.Rng))}"));
        return string.Join("\n", lines);

        static string Show(Rng rng)
        {
            var r = rng.ToSerializable();
            return $"{r.counter}:{r.state0:x}:{r.state1:x}:{r.state2:x}:{r.state3:x}";
        }
    }

    /// <summary>FastHookListeners yields the same models, in the same order, as the game's own iterators.</summary>
    private static bool SameListeners(CombatSession session)
    {
        var (combat, run) = (session.State, session.Run);
        // The reverse patches must reach the game's compiler-generated iterators, or this compares ours with ours.
        if (FastHookListeners.Original.Combat(combat).GetType().DeclaringType != typeof(CombatState)) return false;
        if (FastHookListeners.Original.Run(run, null).GetType().DeclaringType != typeof(RunState)) return false;
        return FastHookListeners.Original.Combat(combat).SequenceEqual(combat.IterateHookListeners(), ReferenceEqualityComparer.Instance)
            && FastHookListeners.Original.Run(run, combat).SequenceEqual(run.IterateHookListeners(combat), ReferenceEqualityComparer.Instance)
            && FastHookListeners.Original.Run(run, null).SequenceEqual(run.IterateHookListeners(null), ReferenceEqualityComparer.Instance);
    }

    private static int DistinctCards(string drawOrder) => drawOrder.Split('\n').Distinct().Count();

    /// <summary>
    /// Plays Rolling Boulder against the first multi-enemy encounter in the pool. The power fires at the start of
    /// the next turn and must hit each enemy separately.
    /// </summary>
    private static void RollingBoulder(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var loadout = new Loadout(Deck: Enumerable.Repeat(new CardSpec("ROLLING_BOULDER"), 10).ToList(), Hp: 999, MaxHp: 999);
        foreach (var encounter in pool)
        {
            var setup = new CombatSetup("IRONCLAD", encounter, act, Cli.MakeSeed(0, 0), Loadout: loadout);
            using var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0)));
            if (session.State.HittableEnemies.Count < 2) continue;

            int before = HeadlessPatches.RollingBoulderSplits;
            var policy = new PlayPolicy(new Random(0));
            while (!session.IsOver && session.State.RoundNumber <= 3)
                session.Execute(policy.Choose(Cli.Decision(session)));
            var errors = HeadlessGame.TakeErrors();
            check("rolling boulder hits one by one", HeadlessPatches.RollingBoulderSplits > before && errors.Count == 0,
                $"{encounter.Id.Entry}: splits {HeadlessPatches.RollingBoulderSplits - before}, errors {errors.FirstOrDefault()}");
            return;
        }
        check("rolling boulder hits one by one", false, "no encounter in the pool has two or more enemies");
    }
}
