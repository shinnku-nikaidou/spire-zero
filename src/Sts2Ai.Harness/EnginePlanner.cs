using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sts2Ai.Brain;
using Sts2Ai.Brain.Combat;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// The SL planner in the engine: <see cref="SlPlanner"/>'s beam and <see cref="SlSolver"/>, moved there line for line
/// with the combat net (plans/2026-11-18-2132-sl-search-rust.md), searching in the engine's threads; V is asked of this
/// side. The live combat is taken in once a plan (<see cref="CombatDecision.Export"/>); each state the beam keeps is then
/// a copy of that world with the state's path applied, searched in a thread of its own, and the turns come back in the
/// order they were asked for, so a plan is the same on any number of threads. With the same budget and leaf it plans
/// what <see cref="PooledPlanner"/> plans on the node's clone, as long as the engine plays the combat as the game does.
///
/// The engine takes a combat in only where the game stands between actions. A plan rooted at a card prompt (one that
/// opens its combat, one a plan ran out at, one where a step did not hold) starts on the node's clone instead, which
/// is asked what follows each answer (<see cref="PromptRoot"/>, <see cref="EngineCrossing"/>): the search is the same
/// one, over a world that is the clone's up to where the player can play again and the engine's from there.
/// </summary>
/// <param name="leaf">Prices the lines; asked from every thread at once.</param>
/// <param name="threads">How many turns are searched at a time.</param>
/// <param name="searchIn">Where the plans are searched: all in the engine (the beam, the solver and every turn, with the
/// combat net loaded there too; plans/2026-11-18-2132-sl-search-rust.md), by the C# search on the engine's world, or by
/// both, held against each other.</param>
public sealed class EnginePlanner(ICombatLeaf leaf, int threads, TurnSearchIn searchIn = TurnSearchIn.Engine)
{
    private readonly SlPlanner _planner = new(leaf);

    /// <summary>The engine's net of the leaf, for a search in the engine: loaded once for each net, where a world is
    /// first searched (the engine's library is loaded only once a world is made).</summary>
    private readonly Lazy<EngineNet?> _net = new(() => searchIn == TurnSearchIn.CSharp ? null
        : leaf is CombatNet net ? Loaded.GetValue(net, n => EngineNet.Load(Convert.FromBase64String(n.File.Onnx),
            ProtocolJson.Serialize(n.File with { Onnx = "", Tests = [] })))
        : throw new ArgumentException($"the engine searches with a combat net, not a {leaf.GetType().Name}", nameof(leaf)));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CombatNet, EngineNet> Loaded = new();

    /// <summary>Plans the rest of the combat that <paramref name="live"/> is.</summary>
    /// <param name="history">The combat's record of enemy moves so far (<see cref="MoveRecord"/>).</param>
    /// <param name="imported">Told the engine's time to take the combat in, in milliseconds.</param>
    public SlPlan Plan(CombatExport live, Budget budget, IReadOnlyList<EnemyTurns> history, Action<double>? imported = null)
    {
        long started = Stopwatch.GetTimestamp();
        using var root = EngineWorld.Import(live.Bytes, live.Run);
        imported?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return Plan(root, budget, history);
    }

    /// <summary>The plan from where <paramref name="root"/> stands: the engine's beam (<see cref="EngineWorld.Plan"/>), or
    /// <see cref="SlPlanner.Search"/>'s with its turns where <see cref="TurnSearchIn"/> says; in a check, the engine's held
    /// against <see cref="SlPlanner.Search"/>'s with the C# turn search.</summary>
    private SlPlan Plan(EngineWorld root, Budget budget, IReadOnlyList<EnemyTurns> history)
    {
        if (_net.Value is not { } net) return SlPlanner.Search(budget, history, Searcher(root));
        var found = root.Plan(net, ((CombatNet)leaf).RunValue, history, budget.NodesPerWorld, budget.Beam, budget.EasyBeam, budget.TurnDecay, threads);
        var plan = PlanOf.Found(found.Pick, found.From, found.ByFirst, found.Proved, budget.Replan, found.Nodes, found.Turns);
        if (searchIn == TurnSearchIn.Check) Same(SlPlanner.Search(budget, history, Searcher(() => new Stoppable(root.Clone(), CancellationToken.None))), plan);
        return plan;
    }

    /// <summary>Two texts where they first differ: a stretch of each from a little before.</summary>
    private static string Apart(string a, string b)
    {
        int at = 0;
        while (at < a.Length && at < b.Length && a[at] == b[at]) at++;
        int from = Math.Max(0, at - 300);
        string Part(string text) => text[from..Math.Min(text.Length, at + 300)];
        return $"at character {at}: ...{Part(a)}... against ...{Part(b)}...";
    }

    /// <summary>Plans the rest of the combat from the card prompt it waits on, which <paramref name="crossing"/>'s
    /// clone stands at.</summary>
    public SlPlan Plan(PromptRoot.Crossing crossing, Budget budget, IReadOnlyList<EnemyTurns> history)
    {
        if (_net.Value is not { } net) return Search(budget, history, () => new PromptRoot(crossing));
        var found = EngineCrossing.Plan(net, ((CombatNet)leaf).RunValue, crossing.At([]).Step.Run, path => crossing.At(path).Crossed, history,
            budget.NodesPerWorld, budget.Beam, budget.EasyBeam, budget.TurnDecay, threads);
        var plan = PlanOf.Found(found.Pick, found.From, found.ByFirst, found.Proved, budget.Replan, found.Nodes, found.Turns);
        if (searchIn == TurnSearchIn.Check) Same(Search(budget, history, () => new PromptRoot(crossing)), plan);
        return plan;
    }

    /// <summary>Throws where the engine's plan differs from <see cref="SlPlanner.Search"/>'s in anything.</summary>
    private static void Same(SlPlan reference, SlPlan plan)
    {
        string a = ProtocolJson.Serialize(reference), b = ProtocolJson.Serialize(plan);
        if (a != b) throw new InvalidOperationException($"the engine's plan differs from SlPlanner's {Apart(a, b)}");
    }

    /// <param name="start">A world of its own at the plan's root, for one turn's search.</param>
    private SlPlan Search<TWorld>(Budget budget, IReadOnlyList<EnemyTurns> history, Func<TWorld> start) where TWorld : ICombatWorld, IDisposable =>
        SlPlanner.Search(budget, history, Searcher(start));

    /// <summary>Each job's turn searched from <paramref name="root"/>, where <see cref="TurnSearchIn"/> says, in threads,
    /// in job order; once <paramref name="stop"/> is cancelled each search stops where it stands.</summary>
    private TurnSearcher Searcher(EngineWorld root, CancellationToken stop = default)
    {
        if (_net.Value is not { } net) return Searcher(() => new Stoppable(root.Clone(), stop));
        var value = ((CombatNet)leaf).RunValue;
        return (jobs, nodes, keep) =>
        {
            // One world from one thread at a time: each job's world is made here, before the threads start.
            var worlds = jobs.Select(_ => root.Clone()).ToArray();
            var found = new TurnSearch[jobs.Count];
            try
            {
                Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                {
                    var job = jobs[i];
                    found[i] = worlds[i].SearchTurn(net, value, job.Path, job.History, nodes, keep, job.LifeBounds, stop);
                    if (searchIn != TurnSearchIn.Check) return;
                    using var world = new Stoppable(root.Clone(), stop);
                    foreach (var action in job.Path) world.Execute(action);
                    var reference = _planner.SearchTurn(world, job.History, nodes, keep, job.LifeBounds);
                    if (Differs(reference, found[i]) is { } differs)
                        throw new InvalidOperationException($"the engine's turn search differs from SlPlanner's at path [{string.Join(' ', job.Path)}], " +
                                                            $"{nodes} nodes, keep {keep}: {differs}");
                });
            }
            catch (AggregateException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            finally
            {
                foreach (var world in worlds) world.Dispose();
            }
            return found;
        };
    }

    /// <summary>Where the engine's turn search differs from the reference, as text; null where every member of it and of
    /// every end is the same, to the bit.</summary>
    private static string? Differs(TurnSearch reference, TurnSearch engine)
    {
        if ((reference.Nodes, reference.Cut, reference.Pruned, reference.Ends.Count) != (engine.Nodes, engine.Cut, engine.Pruned, engine.Ends.Count))
            return $"nodes {reference.Nodes} cut {reference.Cut} pruned {reference.Pruned} ends {reference.Ends.Count} against " +
                   $"{engine.Nodes} {engine.Cut} {engine.Pruned} {engine.Ends.Count}";
        for (int i = 0; i < reference.Ends.Count; i++)
        {
            string a = ProtocolJson.Serialize(reference.Ends[i]), b = ProtocolJson.Serialize(engine.Ends[i]);
            if (a != b) return $"end {i} {Apart(a, b)}";
        }
        return null;
    }

    /// <summary>Each job's turn searched in a world of its own from <paramref name="start"/>, in threads, in job order.</summary>
    private TurnSearcher Searcher<TWorld>(Func<TWorld> start) where TWorld : ICombatWorld, IDisposable =>
        (jobs, nodes, keep) =>
        {
            // One world from one thread at a time: each job's world is made here, before the threads start.
            var worlds = jobs.Select(_ => start()).ToArray();
            var found = new TurnSearch[jobs.Count];
            try
            {
                Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                {
                    foreach (var action in jobs[i].Path) worlds[i].Execute(action);
                    found[i] = _planner.SearchTurn(worlds[i], jobs[i].History, nodes, keep, jobs[i].LifeBounds);
                });
            }
            catch (AggregateException e)
            {
                // The first failure as it was thrown: a run's record keeps one line of it.
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            finally
            {
                foreach (var world in worlds) world.Dispose();
            }
            return found;
        };

    /// <summary>As Harness's combat route in SL, one per run, like <see cref="PooledPlanner.Route"/>; with a solver
    /// (<see cref="Budget.Solve"/>), <see cref="Solver"/>.</summary>
    /// <param name="name">The brain's name in reports: plan and its leaf's name, as on the node's clone.</param>
    /// <param name="pool">The workers that play the node's clone across a card prompt.</param>
    /// <param name="background">Whether the solver searches between decisions too (<see cref="SolverRoute"/>): the
    /// mod's brain, whose game takes its time; self-play, the tests and run search each decision's budget alone.</param>
    public CombatRoute Route(Budget budget, string name, WorkerPool pool, bool background = false)
    {
        if (budget.Solve > 0) return Solver(budget, name, pool, background);
        var route = new SlRoute(budget);
        // The search's processor time: this process's threads, and the workers' where a prompt is crossed.
        var meter = new PlanMeter(() =>
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime + pool.CpuTime();
        });
        return new(name, (decision, _, node, history) => route.Decide(decision.Observation, decision.Legal, b =>
        {
            if (decision.Observation.Prompt is { } prompt)
            {
                var clone = node ?? throw new InvalidOperationException("the SL planner has no node clone: it plays in SL only");
                var crossing = new PromptRoot.Crossing(paths =>
                    [.. pool.Map([.. paths.Select((path, i) => (WorkerRequest)new SlPromptRequest(i, clone, path))])
                        .Select(r => r.Crossed ?? throw new InvalidDataException($"request {r.Id}: no crossing in the reply"))]);
                var plan = meter.Count(() =>
                {
                    // The prompt itself and each answer the first turn's search will try, crossed side by side.
                    crossing.Ahead([[], .. PromptAnswers.Of(prompt, b.NodesPerWorld, out bool _).Select(answer => (IReadOnlyList<CombatAction>)[answer])]);
                    return Plan(crossing, b, history);
                });
                meter.Crossed(crossing.Crossed);
                return plan;
            }
            long started = Stopwatch.GetTimestamp();
            var live = decision.Export ?? throw new InvalidOperationException("the host gives no live combat for the engine: --combat-world engine needs a host that holds the game");
            double exportMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return meter.Count(() => Plan(live, b, history, importMs => meter.Imported(live.Bytes.Length, exportMs, importMs)));
        }), budget, () => meter.Tally);
    }

    /// <summary>
    /// max's route (<see cref="SolverRoute"/>): the combat taken in once where a solver begins, each turn's search a copy
    /// of that world with the actions played since and the state's path applied. Where the solver cannot begin (a card
    /// prompt off its line), the beam's route decides.
    /// </summary>
    private CombatRoute Solver(Budget budget, string name, WorkerPool pool, bool background)
    {
        var beam = Route(budget with { Solve = 0 }, name, pool);
        var route = new SolverRoute(budget, background);
        // Every decision searched, the beam's where it decides: this process's threads, and the workers' where a prompt
        // is crossed.
        var meter = new PlanMeter(() =>
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime + pool.CpuTime();
        });
        return new(name, (decision, seed, node, history) =>
        {
            CombatDecisionResult Decide() => route.Decide(decision.Observation, decision.Legal, history, () => Taken(decision.Export),
                () => beam.Decide(decision, seed, node, history), decision.Early, decision.Hurry);
            if (PimcBrain.Forced(decision.Observation, decision.Legal) != null) return Decide();
            CombatDecisionResult? made = null;
            meter.Count(() =>
            {
                made = Decide();
                return new(made, 0, CombatStatus.InProgress, made.Nodes ?? 0, made.Turns ?? 0, false);
            });
            return made!;
        }, budget, () => meter.Tally, route.Rest);
    }

    /// <summary>The combat as <paramref name="live"/> is, for a solver; null with no live combat.</summary>
    private TakenCombat? Taken(CombatExport? live)
    {
        if (live == null) return null;
        var root = EngineWorld.Import(live.Bytes, live.Run);
        EngineWorld At(IReadOnlyList<CombatAction> played)
        {
            var at = root.Clone();
            foreach (var action in played) at.Execute(action);
            return at;
        }
        return new((played, stop) => Searcher(At(played), stop), played =>
        {
            using var at = At(played);
            return at.Observe();
        }, (budget, history) => Plan(root, budget, history), (history, decay) =>
        {
            if (_net.Value is not { } net) return new SearchingSolver(new SlSolver(history, decay), (played, stop) => Searcher(At(played), stop));
            var engine = new Solving(new EngineSolver(net, ((CombatNet)leaf).RunValue, root, history, decay, threads));
            if (searchIn != TurnSearchIn.Check) return engine;
            return new CheckedSolver(engine, new SearchingSolver(new SlSolver(history, decay), (played, stop) =>
            {
                var at = At(played);
                return Searcher(() => new Stoppable(at.Clone(), stop));
            }));
        });
    }

    /// <summary>The engine's solver as the route plays one.</summary>
    private sealed class Solving(EngineSolver solver) : ICombatSolver
    {
        public long Nodes => solver.Counts.Nodes;
        public int Left => solver.Counts.Left;

        public void Seed(SlPlan plan)
        {
            if (plan.Line is { } line) solver.Seed(line, plan.From);
        }

        public void Run(long nodes, CancellationToken stop = default) => solver.Run(nodes, stop);

        public void Advance(IReadOnlyList<CombatAction> played, IReadOnlyList<EnemyTurns> moves, CancellationToken stop = default) =>
            solver.Advance(played, moves, stop);

        public SlPlan Result(int replan)
        {
            var found = solver.Result();
            return PlanOf.Found(found.Pick, found.From, found.ByFirst, found.Proved, replan, found.Nodes, found.Turns);
        }

        public void Dispose() => solver.Dispose();
    }

    /// <summary>The engine's solver held against <see cref="SlSolver"/> step by step (<see cref="TurnSearchIn.Check"/>):
    /// after each search both have searched the same nodes and leave the same, and their picks are the same plan.</summary>
    private sealed class CheckedSolver(ICombatSolver engine, ICombatSolver reference) : ICombatSolver
    {
        public long Nodes => engine.Nodes;
        public int Left => engine.Left;

        public void Seed(SlPlan plan)
        {
            reference.Seed(plan);
            engine.Seed(plan);
        }

        public void Run(long nodes, CancellationToken stop = default)
        {
            reference.Run(nodes, stop);
            engine.Run(nodes, stop);
            Same("Run");
        }

        public void Advance(IReadOnlyList<CombatAction> played, IReadOnlyList<EnemyTurns> moves, CancellationToken stop = default)
        {
            reference.Advance(played, moves, stop);
            engine.Advance(played, moves, stop);
            Same("Advance");
        }

        public SlPlan Result(int replan)
        {
            var (a, b) = (reference.Result(replan), engine.Result(replan));
            string x = ProtocolJson.Serialize(a), y = ProtocolJson.Serialize(b);
            if (x != y) throw new InvalidOperationException($"the engine's solver picks otherwise than SlSolver {Apart(x, y)}");
            return b;
        }

        private void Same(string after)
        {
            if ((reference.Nodes, reference.Left) != (engine.Nodes, engine.Left))
                throw new InvalidOperationException($"after {after} the engine's solver has {engine.Nodes} nodes and {engine.Left} states left, " +
                                                    $"SlSolver {reference.Nodes} and {reference.Left}");
        }

        public void Dispose()
        {
            engine.Dispose();
            reference.Dispose();
        }
    }

    /// <summary>A world whose steps stop, with an <see cref="OperationCanceledException"/>, once <paramref name="stop"/>
    /// is cancelled: a solver searching in the background ends where it stands.</summary>
    private sealed class Stoppable(EngineWorld world, CancellationToken stop) : ICombatWorld, IDisposable
    {
        public CombatObservation Observe() => world.Observe();
        public RunView Run() => world.Run();
        public IReadOnlyList<CombatAction> Legal() => world.Legal();
        public CombatStatus Status => world.Status;
        public ulong HiddenKey() => world.HiddenKey();

        public void Execute(CombatAction action)
        {
            stop.ThrowIfCancellationRequested();
            world.Execute(action);
        }

        public WorldSnapshot Snapshot() => world.Snapshot();
        public void Restore(WorldSnapshot snapshot) => world.Restore(snapshot);
        public ulong Hash(CombatObservation observation) => world.Hash(observation);
        public ulong StateKey(CombatObservation observation) => world.StateKey(observation);
        public void Forget(WorldSnapshot snapshot) => world.Forget(snapshot);
        public void Dispose() => world.Dispose();
    }
}

/// <summary>Where <see cref="EnginePlanner"/> searches (plans/2026-11-18-2132-sl-search-rust.md).</summary>
public enum TurnSearchIn
{
    /// <summary>In the engine, the combat net loaded there too: the beam (<see cref="EngineWorld.Plan"/>, from a card
    /// prompt <see cref="EngineCrossing"/>), the solver (<see cref="EngineSolver"/>) and every turn.</summary>
    Engine,

    /// <summary>By <see cref="SlPlanner"/> and <see cref="SlSolver"/> on the engine's world.</summary>
    CSharp,

    /// <summary>Both, the engine's taken: a plan, a turn or a step of the solver where they differ in anything stops the run
    /// with what differs.</summary>
    Check,
}
