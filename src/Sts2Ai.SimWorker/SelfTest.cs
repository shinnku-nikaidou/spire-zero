using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// Checks the properties that search and training rely on, over an encounter pool:
///  - determinism: the same setup and decisions give the same observations, step for step, and the same trace;
///  - restore: rebuilding a snapshot by replay gives exactly the observation recorded at that point;
///  - determinize: resampling hidden information leaves the observation unchanged, replays exactly, and the
///    combat plays on from there;
///  - build: a combat built from a recorded observation shows exactly that observation, uids included, restores
///    by replay, and plays on; also numbers kept inside a model (a worn Glass orb, a spent Silver Crucible, Sloth's
///    count of cards played) and the card Nightmare keeps;
///  - rule fidelity: Rolling Boulder hits several enemies one at a time;
///  - worlds: a search world restores exactly after a prompt, also when a relic asks at the start of each turn; the
///    SL world of a run's combat shows the run's observation, plays on as the run did, and restores;
///  - speed patches: the pooled hook listener iterators yield what the game's own iterators yield;
///  - protocol: observations and actions survive a round trip through their wire form, and the brain's and harness's
///    assemblies reach no game assembly;
///  - worker pool (with --workers N): PIMC over worlds searched in serve processes picks what it picks here, with the
///    same values.
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
        foreach (var root in new[] { typeof(ICombatPolicy).Assembly, typeof(CombatObservation).Assembly, typeof(WorkerPool).Assembly })
        {
            var reached = GameAssembliesReachedFrom(root);
            Check("brain and harness reach no game assembly", reached.Count == 0, $"{root.GetName().Name} reaches {string.Join(", ", reached)}");
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

        BuildsInternalNumbers(pump, pool, act, Check);
        RollingBoulder(pump, pool, act, Check);
        WorldRestoresAfterPrompts(pump, pool, act, Check);
        NodeWorldPlaysAsTheRun(pump, seed, Check);
        int workers = Cli.Int(opts, "workers", 0);
        if (workers > 0) PooledMatches(pump, opts, pool, act, seed, workers, Check);

        Console.WriteLine($"draw order resampled by a different determinize seed: {drawResampled}/{drawCompared}; differs from the true order: {drawMovedFromTrue}/{drawCompared}");
        foreach (var (name, (pass, fail)) in results)
            Console.WriteLine($"  {(fail == 0 ? "ok  " : "FAIL")} {name}: {pass} passed, {fail} failed");
        return results.Values.Any(r => r.Fail > 0) ? 1 : 0;
    }

    /// <summary>
    /// A few decisions, each searched here and in a pool of <paramref name="workers"/> serve processes with the same
    /// seeds, with the nodes shared evenly and by halving: the candidates, their values and the plan must be the same.
    /// </summary>
    private static void PooledMatches(Pump pump, Dictionary<string, string> opts, List<EncounterModel> pool, int act, int seed, int workers, Action<string, bool, string> check)
    {
        var brain = new PimcBrain();
        using var workerPool = new WorkerPool(Cli.SelfCommand(), workers);
        var pooled = new PooledPimc(workerPool);
        for (int i = 0; i < 4; i++)
        {
            var encounter = pool[i % pool.Count];
            var setup = Cli.Setup(opts, encounter, act, Cli.MakeSeed(seed + 2, i));
            CombatObservation obs;
            IReadOnlyList<CombatAction> legal;
            var rng = new Random(i);
            using (var session = CombatSession.Start(pump, setup, new RandomChoices(rng)))
            {
                var policy = new PlayPolicy(rng);
                for (int a = 0; a < i && !session.IsOver; a++) session.Execute(policy.Choose(Cli.Decision(session)));
                (obs, legal) = (session.Observation(), session.LegalActions());
            }
            ulong decisionSeed = CombatSession.StableHash($"{setup.Seed}:{i}");
            string buildSeed = Cli.MakeSeed((int)decisionSeed, 0);
            foreach (var budget in new Budget[] { new(4, 120), new(4, 120, Halving: true) })
            {
                CombatDecisionResult here;
                using (var worlds = SimWorlds.NoSL(pump, obs, buildSeed))
                    here = brain.Decide(obs, legal, worlds, budget, decisionSeed);
                var there = pooled.Decide(obs, legal, buildSeed, budget, decisionSeed);
                check("pooled search matches in-process search",
                    Show(here) == Show(there), $"#{i} {encounter.Id.Entry} {budget}: here {Show(here)}; pooled {Show(there)}");
            }
            var errors = pooled.TakeGameErrors();
            check("no game errors in workers", errors.Count == 0, $"#{i}: {errors.FirstOrDefault()}");
        }
        check("no worker restarts", workerPool.Restarts == 0, $"{workerPool.Restarts} restarts");

        static string Show(CombatDecisionResult r) =>
            $"{r.First} [{string.Join(" ", r.Plan)}] " + string.Join(" ", r.Candidates.Select(c => $"{c.Action}={c.Value:R}")) +
            (r.PlanHashes == null ? "" : $" agreed {string.Join(",", r.PlanHashes)}");
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
    /// A Defect combat's observation edited to show numbers that only a model's own state holds: a Glass orb that has
    /// fired once (3 passive, 6 evoke, against a fresh one's 4 and 8), a Silver Crucible used up (no counter after its
    /// third card reward, and a treasure room since), Sloth after one card of its 3 (the icon shows the 1), Nightmare on
    /// the first card in hand, and a Hardened Shell of 20 on the first enemy after 6 damage this turn (it shows 14). The
    /// built combat must show them, still let cards be played, and add Nightmare's copies next turn.
    /// </summary>
    private static void BuildsInternalNumbers(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var setup = new CombatSetup("DEFECT", pool[0], act, Cli.MakeSeed(0, 0));
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        var held = obs.Hand[0] with { Uid = null };
        obs = obs with
        {
            Player = obs.Player with
            {
                Orbs = [new OrbView("GLASS_ORB", 3, 6), .. obs.Player.Orbs.Take(obs.Player.OrbSlots - 1)],
                Powers = [.. obs.Player.Powers, new PowerView("SLOTH_POWER", 1, 3), new PowerView("NIGHTMARE_POWER", 3, Card: held)],
            },
            Relics = [.. obs.Relics, new RelicView { Id = "SILVER_CRUCIBLE", Status = "Disabled", UsedUp = true }],
            Enemies = [obs.Enemies[0] with { Powers = [.. obs.Enemies[0].Powers, new PowerView("HARDENED_SHELL_POWER", 14, 20)] }, .. obs.Enemies.Skip(1)],
        };
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        check("build solves internal numbers", built.BuildDifferences.Count == 0, $"{pool[0].Id.Entry}: {string.Join("; ", built.BuildDifferences.Take(4))}");
        check("build keeps a power's stacks", built.LegalActions().Any(a => a.Kind == CombatActionKind.PlayCard), "Sloth after 1 card of 3 allows no card");
        built.Execute(built.LegalActions().First(a => a.Kind == CombatActionKind.EndTurn));
        var hand = built.Observation().Hand;
        check("build keeps a power's card", hand.Count(c => c.Id == held.Id) >= 3, $"Nightmare of {held.Id} left a hand of {string.Join(",", hand.Select(c => c.Id))}");
    }

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

    /// <summary>
    /// A run with random policies, stopped at its third combat decision: the SL world loaded from the node's clone must
    /// show that decision's observation, give the run's next observations for the run's actions (up to a card prompt,
    /// which the world answers its own way), and show the first observation again after a restore.
    /// </summary>
    private static void NodeWorldPlaysAsTheRun(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed);
        RunSession? session = null;
        var steps = new List<(CombatObservation Obs, CombatAction Action, int Trace)>();
        NodeClone? clone = null;
        int decisions = 0, node = -1;
        var play = new PlayPolicy(rng);
        var capture = new CapturePolicy(decision =>
        {
            var action = play.Choose(decision);
            if (++decisions >= 3 && steps.Count < 8)
            {
                clone ??= session!.Clone();
                node = session!.NodeStart;
                steps.Add((decision.Observation, action, session.Trace.Count));
            }
            return action;
        });
        string label = $"run {Cli.MakeSeed(seed, 0)}";
        using (session = RunSession.Start(pump, new RunSetup("SILENT", Cli.MakeSeed(seed, 0)), PromptBrain.Instance, capture, new RandomMacro(rng)))
            while (!session.IsOver && steps.Count < 8 && (clone == null || session.NodeStart == node)) session.Step();
        if (clone == null || steps.Count < 2)
        {
            check("SL world shows the run's combat", false, $"{label}: no combat with 3 decisions");
            return;
        }
        string Json(CombatObservation obs) => ProtocolJson.Serialize(obs);
        using var world = new NodeWorld(pump, clone);
        var root = world.Snapshot();
        check("SL world shows the run's combat", Json(world.Observe()) == Json(steps[0].Obs), label);
        int played = 0;
        for (int k = 0; k + 1 < steps.Count && steps[k + 1].Trace == steps[k].Trace + 1; k++, played++)
        {
            world.Execute(steps[k].Action);
            check("SL world plays as the run", Json(world.Observe()) == Json(steps[k + 1].Obs), $"{label} after {steps[k].Action}");
        }
        world.Restore(root);
        check("SL world restores", Json(world.Observe()) == Json(steps[0].Obs), $"{label} after {played} actions");
    }

    private sealed class CapturePolicy(Func<CombatDecision, CombatAction> choose) : ICombatPolicy
    {
        public CombatAction Choose(CombatDecision decision) => choose(decision);
    }

    /// <summary>
    /// With Toasty Mittens, which asks for a card to exhaust at the start of each turn, a world plays Survivor (which
    /// asks for a card to discard), then is rebuilt from its snapshot: the start's own prompt must not take Survivor's
    /// recorded pick. Over several search seeds, since a wrong pick can match by chance.
    /// </summary>
    private static void WorldRestoresAfterPrompts(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var deck = new[] { "SURVIVOR", "SURVIVOR", "SURVIVOR", "SURVIVOR", "SURVIVOR", "STRIKE_SILENT", "DEFEND_SILENT", "NEUTRALIZE" };
        var loadout = new Loadout(Deck: deck.Select(id => new CardSpec(id)).ToList(), Relics: ["TOASTY_MITTENS"], Hp: 999, MaxHp: 999);
        var setup = new CombatSetup("SILENT", pool[0], act, Cli.MakeSeed(0, 0), Loadout: loadout);
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        using var worlds = SimWorlds.NoSL(pump, obs, setup.Seed);
        foreach (var world in worlds.Worlds(6, 1))
        {
            var root = world.Snapshot();
            uint survivor = world.Observe().Hand.First(c => c.Id == "SURVIVOR").Uid!.Value;
            world.Execute(world.Legal().First(a => a.Card == survivor));
            string after = ProtocolJson.Serialize(world.Observe());
            var played = world.Snapshot();
            world.Restore(root);
            world.Observe();
            world.Restore(played);
            check("world restores after prompts", ProtocolJson.Serialize(world.Observe()) == after, $"{pool[0].Id.Entry}: {string.Join(" ", played.Steps)}");
        }
    }
}
