using System.CommandLine;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using Microsoft.Extensions.Logging;
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
///  - SL seal: a node's clone stops at the node's exit and refuses to step past it, and the SL world refuses to step
///    past the end of its combat;
///  - SL plan: the planner's plan of a run's combat holds in the real run, step for step, to the end it planned (and,
///    with --workers N, the pooled planner plans the same);
///  - speed patches: the pooled hook listener iterators yield what the game's own iterators yield;
///  - protocol: observations and actions survive a round trip through their wire form, and the brain's and harness's
///    assemblies reach no game assembly;
///  - worker pool (with --workers N): PIMC over worlds searched in serve processes picks what it picks here, with the
///    same values.
/// </summary>
internal static class SelfTest
{
    private sealed record Recording(List<CombatSnapshot> Snapshots, List<string> Observations, List<string> Hidden, string Trace, List<string> ProtocolErrors);

    private static readonly Option<int> N = Opt.Of("--n", 20, "combats");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base");
    private static readonly Option<int> Workers = Opt.Of("--workers", 0, "also checks a pool of this many worker processes");
    private static readonly PoolArgs Pool = new();

    public static Command Command()
    {
        var command = Opt.Command("selftest", "checks determinism, restore and determinize over an encounter pool", [N, Seed, Workers], Pool.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        int n = r.GetValue(N);
        int seed = r.GetValue(Seed);
        var combats = Pool.Read(r);
        var (act, rooms, pool) = (combats.Act, combats.Rooms, combats.Encounters);
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
            var setup = combats.Setup(i, Cli.MakeSeed(seed, i));
            var encounter = setup.Encounter;
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
                Logs.Sim.LogDebug(e, "{Label} failed", label);
            }
            var errors = HeadlessGame.TakeErrors();
            Check("no game errors", errors.Count == 0, $"{label}: {errors.FirstOrDefault()}");
        }

        BuildsInternalNumbers(pump, pool, act, Check);
        BuildsAtLowHp(pump, pool, act, Check);
        BuildsAGrownEruption(pump, Check);
        RollingBoulder(pump, pool, act, Check);
        ThornsKillDuringSlyPlay(pump, pool, act, Check);
        WorldRestoresAfterPrompts(pump, pool, act, Check);
        NodeWorldPlaysAsTheRun(pump, seed, Check);
        CloneStopsAtTheExit(pump, seed, Check);
        CloneKeepsRelicState(pump, seed, Check);
        int workers = r.GetValue(Workers);
        PlanHoldsInTheRun(pump, seed, workers, Check);
        if (workers > 0) PooledMatches(pump, combats, seed, workers, Check);

        Console.WriteLine($"draw order resampled by a different determinize seed: {drawResampled}/{drawCompared}; differs from the true order: {drawMovedFromTrue}/{drawCompared}");
        foreach (var (name, (pass, fail)) in results)
            Console.WriteLine($"  {(fail == 0 ? "ok  " : "FAIL")} {name}: {pass} passed, {fail} failed");
        return results.Values.Any(r => r.Fail > 0) ? 1 : 0;
    }

    /// <summary>
    /// A few decisions, each searched here and in a pool of <paramref name="workers"/> serve processes with the same
    /// seeds, with the nodes shared evenly and by halving: the candidates, their values and the plan must be the same.
    /// </summary>
    private static void PooledMatches(Pump pump, CombatPool combats, int seed, int workers, Action<string, bool, string> check)
    {
        var brain = new PimcBrain();
        using var workerPool = new WorkerPool(Cli.SelfCommand(), workers);
        var pooled = new PooledPimc(workerPool);
        for (int i = 0; i < 4; i++)
        {
            var setup = combats.Setup(i, Cli.MakeSeed(seed + 2, i));
            var encounter = setup.Encounter;
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
            var errors = workerPool.TakeGameErrors();
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
    /// A player built at low HP still plays after a start-of-combat effect that would kill them: Royal Poison deals 4
    /// at the start of the first turn, which every build starts with.
    /// </summary>
    private static void BuildsAtLowHp(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var setup = new CombatSetup("SILENT", pool[0], act, Cli.MakeSeed(0, 0));
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        obs = obs with { Player = obs.Player with { Hp = 3 }, Relics = [.. obs.Relics, new RelicView { Id = "ROYAL_POISON" }] };
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        check("build survives a deadly start", built.BuildDifferences.Count == 0 && built.Player.IsActiveForHooks,
            $"{pool[0].Id.Entry} at 3 HP with Royal Poison: hooks {(built.Player.IsActiveForHooks ? "on" : "off")}; {string.Join("; ", built.BuildDifferences.Take(4))}");
    }

    /// <summary>
    /// The Waterfall Giant about to erupt shows its eruption as a death blow, a number the monster keeps that grows all
    /// fight and can pass 100: the build must find it.
    /// </summary>
    private static void BuildsAGrownEruption(Pump pump, Action<string, bool, string> check)
    {
        var (encounter, act) = Cli.Encounter("WATERFALL_GIANT_BOSS");
        var setup = new CombatSetup("SILENT", encounter, act, Cli.MakeSeed(0, 0));
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        obs = obs with { Enemies = [obs.Enemies[0] with { Intents = [new IntentView { Type = "DeathBlow", Damage = 150, Hits = 1 }] }, .. obs.Enemies.Skip(1)] };
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        check("build finds an eruption past 100", built.BuildDifferences.Count == 0, string.Join("; ", built.BuildDifferences.Take(4)));
    }

    /// <summary>
    /// A Defect combat's observation edited to show numbers that only a model's own state holds: a Glass orb that has
    /// fired once (3 passive, 6 evoke, against a fresh one's 4 and 8), a Silver Crucible used up (no counter after its
    /// third card reward, and a treasure room since), a Pael's Tooth keeping two cards (its counter is their number),
    /// Sloth after one card of its 3 (the icon shows the 1), Nightmare on
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
            Relics =
            [
                .. obs.Relics, new RelicView { Id = "SILVER_CRUCIBLE", Status = "Disabled", UsedUp = true },
                new RelicView { Id = "PAELS_TOOTH", Counter = 2, Cards = [obs.Hand[0].Id, obs.Hand[^1].Id] },
            ],
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
    /// Promotion-1 seed 68's game error: at 2 HP behind a block the enemies' turn cannot pass, with Tools of the Trade
    /// and only a Ricochet in the deck, against Thorns on every enemy. The next turn's block is gone, Tools of the
    /// Trade discards the Ricochet, Sly plays it, and Thorns kill the player in the middle of the play: the combat
    /// must end lost with no game error, through <see cref="HeadlessPatches.PopCardAfterEarlyReturn"/>.
    /// </summary>
    private static void ThornsKillDuringSlyPlay(Pump pump, List<EncounterModel> pool, int act, Action<string, bool, string> check)
    {
        var loadout = new Loadout(Deck: [new CardSpec("RICOCHET")]);
        var setup = new CombatSetup("SILENT", pool[0], act, Cli.MakeSeed(0, 0), Loadout: loadout);
        CombatObservation obs;
        using (var session = CombatSession.Start(pump, setup, new RandomChoices(new Random(0))))
            obs = session.Observation();
        obs = obs with
        {
            Player = obs.Player with { Hp = 2, Block = 999, Powers = [.. obs.Player.Powers, new PowerView("TOOLS_OF_THE_TRADE_POWER", 1)] },
            Enemies = [.. obs.Enemies.Select(e => e with { Powers = [.. e.Powers, new PowerView("THORNS_POWER", 5)] })],
        };
        int before = HeadlessPatches.PopCardAfterEarlyReturn.Pops;
        using var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, setup.Seed), new RandomChoices(new Random(0)));
        built.Execute(built.LegalActions().First(a => a.Kind == CombatActionKind.EndTurn));
        var errors = HeadlessGame.TakeErrors();
        check("thorns kill during a sly play", built.Player.Creature.IsDead && HeadlessPatches.PopCardAfterEarlyReturn.Pops > before && errors.Count == 0,
            $"{pool[0].Id.Entry}: dead {built.Player.Creature.IsDead}, pops {HeadlessPatches.PopCardAfterEarlyReturn.Pops - before}, " +
            $"build {string.Join("; ", built.BuildDifferences.Take(2))}, errors {errors.FirstOrDefault()}");
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

    /// <summary>
    /// A run with random policies, to its third node: its first node's clone (the save on arrival and every decision of
    /// the node, the travel last) must load at its horizon, without travelling, and refuse another step (docs:
    /// environments §7, guard 8).
    /// </summary>
    private static void CloneStopsAtTheExit(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed + 1);
        var nodes = new List<(string Save, int Start)>();
        List<string> trace;
        using (var session = RunSession.Start(pump, new RunSetup("SILENT", Cli.MakeSeed(seed + 1, 0)), PromptBrain.Instance, new PlayPolicy(rng), new RandomMacro(rng)))
        {
            while (!session.IsOver && nodes.Count < 3)
            {
                if (nodes.Count == 0 || nodes[^1].Start != session.NodeStart) nodes.Add((session.Clone().Save, session.NodeStart));
                session.Step();
            }
            trace = session.Trace.ToList();
        }
        if (nodes.Count < 3)
        {
            check("SL clone stops at its node's exit", false, "the run ended before its third node");
            return;
        }
        var clone = new NodeClone(nodes[1].Save, trace.Skip(nodes[1].Start).Take(nodes[2].Start - nodes[1].Start).ToList());
        using var loaded = RunSession.Load(pump, clone, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance);
        check("SL clone stops at its node's exit", loaded.AtHorizon && clone.Trace[^1].StartsWith(Surfaces.Map + " ", StringComparison.Ordinal),
            $"{clone.Trace.Count} decisions, the last {clone.Trace[^1]}, at horizon {loaded.AtHorizon}");
        check("SL clone refuses to step past its node's exit", Throws(loaded.Step), $"stepped past {clone.Trace[^1]}");
    }

    /// <summary>
    /// A run with random policies, to its third node, whose first relic is disabled at the start, a status that the save
    /// drops, as Lava Rock's own. The second node's clone must show every observation of the node as the run did, the
    /// disabled relic among them (docs: simulator §7).
    /// </summary>
    private static void CloneKeepsRelicState(Pump pump, int seed, Action<string, bool, string> check)
    {
        var rng = new Random(seed + 3);
        var nodes = new List<(string Save, int Start)>();
        var seen = new List<string>();
        List<string> trace;
        using (var session = RunSession.Start(pump, new RunSetup("SILENT", Cli.MakeSeed(seed + 3, 0)), PromptBrain.Instance, new PlayPolicy(rng), new RandomMacro(rng),
                   obs => seen.Add(ProtocolJson.Serialize(obs))))
        {
            session.Player.Relics[0].Status = RelicStatus.Disabled;
            while (!session.IsOver && nodes.Count < 3)
            {
                if (nodes.Count == 0 || nodes[^1].Start != session.NodeStart) nodes.Add((session.Clone().Save, session.NodeStart));
                session.Step();
            }
            trace = session.Trace.ToList();
        }
        if (nodes.Count < 3)
        {
            check("SL clone keeps relic state the save drops", false, "the run ended before its third node");
            return;
        }
        var (start, end) = (nodes[1].Start, nodes[2].Start);
        var cloned = new List<string>();
        using (RunSession.Load(pump, new NodeClone(nodes[1].Save, trace.Skip(start).Take(end - start).ToList()),
                   RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, RunCommand.Unasked.Instance, obs => cloned.Add(ProtocolJson.Serialize(obs))))
        {
        }
        bool disabled = seen[start].Contains("\"status\":\"Disabled\"", StringComparison.Ordinal);
        check("SL clone keeps relic state the save drops", disabled && cloned.SequenceEqual(seen.Skip(start).Take(end - start)),
            $"run shows the relic disabled: {disabled}; {cloned.Count} of {end - start} observations, the first differing at " +
            $"{Enumerable.Range(0, Math.Min(cloned.Count, end - start)).FirstOrDefault(i => cloned[i] != seen[start + i], -1)}");
    }

    /// <summary>
    /// A run with random policies, stopped at the first decision of its second combat. The SL planner plans the rest of
    /// that combat on the node's clone (here, and with <paramref name="workers"/> in a pool too, which must plan the
    /// same). A hallway fight at full HP: the plan must win it. The clone, played to the plan's end, must end the combat as
    /// planned and refuse to step past it. Then the run is played again to the decision and follows the plan: each step
    /// must find the observation the plan expects, and the combat must end where the plan ends, as the plan says (docs:
    /// combat-brain §7, test 6).
    /// </summary>
    private static void PlanHoldsInTheRun(Pump pump, int seed, int workers, Action<string, bool, string> check)
    {
        var budget = new Budget(NodesPerWorld: 150, Beam: 4);
        string label = $"run {Cli.MakeSeed(seed + 2, 0)}";
        RunSession? session = null;
        NodeClone? clone = null;
        CombatObservation? at = null;
        var record = new MoveRecord();
        Play((decision, _) =>
        {
            clone ??= session!.Clone();
            at ??= decision.Observation;
            record.Witness(decision.Observation);
            return null;
        });
        var history = record.History;
        if (clone == null || at == null)
        {
            check("SL plan holds in the run", false, $"{label}: no second combat");
            return;
        }

        SlPlan plan;
        using (var world = new NodeWorld(pump, clone))
        {
            plan = new SlPlanner().Plan(at, world, budget.For(at.Room), history);
            world.Restore(new([]));
            foreach (var step in plan.Result.Plan.Prepend(plan.Result.First)) world.Execute(step);
            check("SL world ends its combat as planned", plan.End == CombatStatus.Won && world.Status == plan.End, $"{label}: {world.Status}, planned {plan.End}");
            check("SL world refuses to step past its combat", world.Legal().Count == 0 && Throws(() => world.Execute(CombatAction.EndTurn)), label);
        }
        if (workers > 0)
        {
            using var workerPool = new WorkerPool(Cli.SelfCommand(), workers);
            var pooled = new PooledPlanner(workerPool).Plan(clone, budget.For(at.Room), history);
            check("pooled planner plans what the planner plans here", Show(pooled) == Show(plan), $"{label}: here {Show(plan)}; pooled {Show(pooled)}");
            check("no game errors in workers", workerPool.TakeGameErrors() is [], label);
        }

        var steps = new Queue<CombatAction>(plan.Result.Plan.Prepend(plan.Result.First));
        var hashes = new Queue<ulong>(plan.Result.PlanHashes!.Prepend(ProtocolJson.Hash(at)));
        string? broke = null;
        var end = Play((decision, inCombat) =>
        {
            if (!inCombat || broke != null) return null;
            if (!steps.TryDequeue(out var step)) broke = "the combat went on past the plan";
            else if (hashes.Dequeue() != ProtocolJson.Hash(decision.Observation)) broke = $"before {step}, {steps.Count} steps from the end, the observation differs";
            return broke == null ? step : null;
        });
        check("SL plan holds in the run", broke == null && steps.Count == 0 && plan.Result.Plan.Count >= 2,
            $"{label}: {broke ?? $"{steps.Count} steps left of {plan.Result.Plan.Count + 1}"}");
        var w = LeafValue.Default;
        check("SL plan ends the run's combat as planned", end is { Died: false } e && Math.Abs(w.WinBase + w.PerHp * e.Hp / e.MaxHp + w.PerPotion * e.Potions - plan.Value) < 1e-9,
            $"{label}: {end}, planned {plan.End} at {plan.Value:F4}");

        // Plays the run to the first decision of its second combat, then asks follow at each combat decision of that
        // combat (true) and the next decision after it (false), which plays the rest of it, until follow gives null.
        CombatEnd? Play(Func<CombatDecision, bool, CombatAction?> follow)
        {
            var rng = new Random(seed + 2);
            var play = new PlayPolicy(rng);
            bool done = false, following = false;
            var policy = new CapturePolicy(decision =>
            {
                if (!following && session!.Combats.Count == 2) following = true;
                if (!following) return play.Choose(decision);
                var action = follow(decision, true);
                done |= action == null;
                return action ?? play.Choose(decision);
            });
            var macro = new FollowMacro(new RandomMacro(rng), () =>
            {
                if (following && !done) done = follow(null!, false) == null;
            });
            using (session = RunSession.Start(pump, new RunSetup("SILENT", Cli.MakeSeed(seed + 2, 0)), PromptBrain.Instance, policy, macro))
            {
                while (!session.IsOver && !done) session.Step();
                return session.Combats.ElementAtOrDefault(1)?.End;
            }
        }

        static string Show(SlPlan p) =>
            $"{p.Result.First} [{string.Join(" ", p.Result.Plan)}] {p.Value:R} {p.End} {p.Nodes} nodes {p.Turns} turns {p.Proved} " +
            string.Join(",", p.Result.PlanHashes!);
    }

    private static bool Throws(Action act)
    {
        try
        {
            act();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>A macro policy that tells <paramref name="before"/> of each decision it is asked.</summary>
    private sealed class FollowMacro(IMacroPolicy macro, Action before) : IMacroPolicy
    {
        public string Choose(MacroDecision decision)
        {
            before();
            return macro.Choose(decision);
        }
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
