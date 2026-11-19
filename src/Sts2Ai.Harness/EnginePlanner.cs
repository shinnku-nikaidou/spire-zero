using System.Diagnostics;
using Sts2Ai.Brain;
using Sts2Ai.Brain.Combat;
using Sts2Ai.Engine;
using Sts2Ai.Protocol;

namespace Sts2Ai.Harness;

/// <summary>
/// The SL planner, in the engine (plans/2026-11-18-2132-sl-search-rust.md): the beam over turns, the solver and every
/// turn's search with the combat net run there, in the engine's threads; V is asked of this side. The live combat is
/// taken in once a plan (<see cref="CombatDecision.Export"/>); each state the beam keeps is a copy of that world with the
/// state's path applied, and the turns come back in the order they were asked for, so a plan is the same on any number
/// of threads.
///
/// The engine takes a combat in only where the game stands between actions. A plan rooted at a card prompt (one that
/// opens its combat, one a plan ran out at, one where a step did not hold) starts on the node's clone instead, which
/// is asked what follows each answer (<see cref="PromptRoot"/>, <see cref="EngineCrossing"/>): the search is the same
/// one, over a world that is the clone's up to where the player can play again and the engine's from there.
/// </summary>
/// <param name="net">The combat net, loaded in the engine where a world is first searched (the engine's library is
/// loaded only once a world is made), once for each net.</param>
/// <param name="threads">How many turns are searched at a time.</param>
public sealed class EnginePlanner(CombatNet net, int threads)
{
    private readonly Lazy<EngineNet> _net = new(() => Loaded.GetValue(net, n => EngineNet.Load(Convert.FromBase64String(n.File.Onnx),
        ProtocolJson.Serialize(n.File with { Onnx = "", Tests = [] }))));

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

    /// <summary>The engine's beam from where <paramref name="root"/> stands (<see cref="EngineWorld.Plan"/>).</summary>
    private SlPlan Plan(EngineWorld root, Budget budget, IReadOnlyList<EnemyTurns> history)
    {
        var found = root.Plan(_net.Value, net.RunValue, history, budget.NodesPerWorld, budget.Beam, budget.EasyBeam, budget.TurnDecay, threads);
        return PlanOf.Found(found.Pick, found.From, found.ByFirst, found.Proved, budget.Replan, found.Nodes, found.Turns);
    }

    /// <summary>Plans the rest of the combat from the card prompt it waits on, which <paramref name="crossing"/>'s
    /// clone stands at.</summary>
    public SlPlan Plan(PromptRoot.Crossing crossing, Budget budget, IReadOnlyList<EnemyTurns> history)
    {
        var found = EngineCrossing.Plan(_net.Value, net.RunValue, crossing.At([]).Step.Run, path => crossing.At(path).Crossed, history,
            budget.NodesPerWorld, budget.Beam, budget.EasyBeam, budget.TurnDecay, threads);
        return PlanOf.Found(found.Pick, found.From, found.ByFirst, found.Proved, budget.Replan, found.Nodes, found.Turns);
    }

    /// <summary>Harness's combat route in SL, one per run; with a solver (<see cref="Budget.Solve"/>), <see cref="Solver"/>.</summary>
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
            var live = decision.Export ?? throw new InvalidOperationException("the host gives no live combat for the engine: plan needs a host that holds the game");
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
                () => beam.Decide(decision, seed, node, history), decision.Early, decision.Hurry, decision.After);
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
        return new(played =>
        {
            using var at = root.Clone();
            foreach (var action in played) at.Execute(action);
            return at.Observe();
        }, (budget, history) => Plan(root, budget, history),
            (history, decay) => new Solving(new EngineSolver(_net.Value, net.RunValue, root, history, decay, threads)));
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
}
