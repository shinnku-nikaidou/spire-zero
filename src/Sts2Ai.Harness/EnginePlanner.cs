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
///
/// In a co-op combat the world holds the local player alone, the teammates dead (<see cref="Alone"/>).
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

    /// <summary>
    /// SL in a co-op combat (another player among the allies; the user, 2026-11-22: 隊友當然是當全部死光的sl): the world
    /// holds the local player alone, every teammate killed as the game kills, before anything is played
    /// (<see cref="EngineWorld.Alone"/>), so the player's plays alone decide what follows and the world is one exact combat,
    /// as SL's always is. It is taken in again at each decision, so it follows what the teammates did, and it is searched as
    /// it shows itself: the decision becomes its observation and legal actions (a dead teammate only takes targets away, so
    /// what it gives is legal in the game too). A co-op run has no node clone (no record holds the teammates' choices): at a
    /// card prompt the world stands there from the combat the host last wrote out and the steps since
    /// (<see cref="CombatDecision.Prompted"/>). Null in a combat of one player.
    /// </summary>
    private static Coop? Alone(CombatDecision decision)
    {
        if (!Together(decision.Observation)) return null;
        var world = decision.Observation.Prompt != null
            ? EnginePimc.Stand(decision.Prompted ?? throw new InvalidOperationException("a card prompt in a co-op combat with no combat to stand at"), alone: true)
            : decision.Export is { } live ? EngineWorld.Alone(live.Bytes, live.Run, opening: false)
            : throw new InvalidOperationException("the host gives no live combat for the engine: plan needs a host that holds the game");
        var shown = world.Observe();
        return new(world, new CombatDecision(decision.Only ? decision.Legal : world.Legal(), () => shown, _ => decision.RunObservation!,
            () => decision.Export!, decision.Early, decision.Hurry, decision.After, () => decision.Prompted!, decision.Only));
    }

    /// <summary>A co-op combat: another player among the allies (a creature with no monster id).</summary>
    private static bool Together(CombatObservation observation) => observation.Allies.Any(a => a.Id == null);

    /// <summary>SL's world of a co-op combat at a decision, and the decision as it shows it (<see cref="Alone"/>).</summary>
    private sealed record Coop(EngineWorld World, CombatDecision Decision);

    /// <summary>The engine's beam from where <paramref name="root"/> stands (<see cref="EngineWorld.Plan"/>).</summary>
    private SlPlan Plan(EngineWorld root, Budget budget, IReadOnlyList<EnemyTurns> history)
    {
        var found = root.Plan(_net.Value, net.FightValue, history, budget.NodesPerWorld, budget.Beam, budget.EasyBeam, budget.TurnDecay, threads);
        return PlanOf.Found(found.Pick, found.From, found.ByFirst, found.Proved, budget.Replan, found.Nodes, found.Turns);
    }

    /// <summary>
    /// A player's question (<see cref="CombatDecision.Only"/>): each of <paramref name="first"/> played first in the
    /// engine, and the beam's plan from there (or, where it ends the combat, the combat's end as it is); the candidates
    /// are those first actions, best first, each with its line's value, chance to win and HP left, and the plan after it.
    /// </summary>
    private SlPlan Only(EngineWorld root, IReadOnlyList<CombatAction> first, Budget budget, IReadOnlyList<EnemyTurns> history)
    {
        var lines = new List<(CombatAction First, SlPlan? Plan, double Value, double Win, double Hp)>();
        foreach (var action in first)
        {
            using var world = root.Clone();
            world.Execute(action);
            if (world.Status != CombatStatus.InProgress)
            {
                var (value, win, hp) = world.Outcome(_net.Value, net.FightValue, history);
                lines.Add((action, null, value, win, hp));
                continue;
            }
            var plan = Plan(world, budget, history);
            var top = plan.Result.Candidates.FirstOrDefault();
            lines.Add((action, plan, plan.Value, top?.Win ?? (plan.End == CombatStatus.Won ? 1 : 0), top?.Hp ?? 0));
        }
        var best = lines.MaxBy(l => l.Value);
        var candidates = lines.OrderByDescending(l => l.Value).Select(l => new Candidate(l.First.ToString(), l.Value, 0, 1, [Math.Round(l.Value, 4)],
            Math.Round(l.Win, 4), Math.Round(l.Hp, 1), l.Plan is { } p ? [p.Result.First.ToString(), .. p.Result.Plan.Select(a => a.ToString())] : null)).ToList();
        int nodes = lines.Sum(l => l.Plan?.Nodes ?? 0);
        return new SlPlan(new CombatDecisionResult(best.First, [], candidates, Nodes: nodes), best.Value, best.Plan?.End ?? CombatStatus.InProgress, nodes, 0, false);
    }

    /// <summary>The card prompt the node's clone stands at, each path of answers from it played by the pool's workers.</summary>
    private static PromptRoot.Crossing Crossing(NodeClone? node, WorkerPool pool)
    {
        var clone = node ?? throw new InvalidOperationException("the SL planner has no node clone: it plays in SL only");
        return new(paths => [.. pool.Map([.. paths.Select((path, i) => (WorkerRequest)new SlPromptRequest(i, clone, path))])
            .Select(r => r.Crossed ?? throw new InvalidDataException($"request {r.Id}: no crossing in the reply"))]);
    }

    /// <summary>The beam's plan from <paramref name="prompt"/>, the prompt itself and the first <paramref name="answers"/>
    /// of its answers crossed first, side by side: the answers the first turn's search will try.</summary>
    private SlPlan Plan(CardChoice prompt, PromptRoot.Crossing crossing, Budget budget, IReadOnlyList<EnemyTurns> history, int answers)
    {
        crossing.Ahead([[], .. PromptAnswers.Of(prompt, answers, out bool _).Select(answer => (IReadOnlyList<CombatAction>)[answer])]);
        return Plan(crossing, budget, history);
    }

    /// <summary>Plans the rest of the combat from the card prompt it waits on, which <paramref name="crossing"/>'s
    /// clone stands at.</summary>
    public SlPlan Plan(PromptRoot.Crossing crossing, Budget budget, IReadOnlyList<EnemyTurns> history)
    {
        var found = EngineCrossing.Plan(_net.Value, net.FightValue, crossing.At([]).Step.Run, path => crossing.At(path).Crossed, history,
            budget.NodesPerWorld, budget.Beam, budget.EasyBeam, budget.TurnDecay, threads);
        return PlanOf.Found(found.Pick, found.From, found.ByFirst, found.Proved, budget.Replan, found.Nodes, found.Turns);
    }

    /// <summary>Harness's combat route in SL, one per run; with a solver (<see cref="Budget.Solve"/>), <see cref="Solver"/>.</summary>
    /// <param name="name">The brain's name in reports: plan and its leaf's name, as on the node's clone.</param>
    /// <param name="pool">The workers that play the node's clone across a card prompt.</param>
    /// <param name="background">Whether the solver searches between decisions too (<see cref="SolverRoute"/>): the
    /// mod's brain, whose game takes its time; self-play, the tests and run search each decision's budget alone.</param>
    /// <param name="tables">Whether each answer between actions carries its line's table (<see cref="Tabled"/>): the
    /// mod's brain.</param>
    public CombatRoute Route(Budget budget, string name, WorkerPool pool, bool background = false, bool tables = false)
    {
        if (tables) return Tabled(Route(budget, name, pool, background));
        if (budget.Solve > 0) return Solver(budget, name, pool, background);
        var route = new SlRoute(budget);
        // The search's processor time: this process's threads, and the workers' where a prompt is crossed.
        var meter = new PlanMeter(() =>
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime + pool.CpuTime();
        });
        return new(name, (decision, _, node, history) =>
        {
            var coop = Alone(decision);
            using var world = coop?.World;
            var asked = coop?.Decision ?? decision;
            return route.Decide(asked.Observation, asked.Legal, b => world != null
                ? meter.Count(() => asked.Only ? Only(world, asked.Legal, b, history) : Plan(world, b, history))
                : Searched(decision, node, b, history));
        }, budget, () => meter.Tally);

        SlPlan Searched(CombatDecision decision, NodeClone? node, Budget b, IReadOnlyList<EnemyTurns> history)
        {
            if (decision.Observation.Prompt is { } prompt)
            {
                var crossing = Crossing(node, pool);
                var plan = meter.Count(() => Plan(prompt, crossing, b, history, b.NodesPerWorld));
                meter.Crossed(crossing.Crossed);
                return plan;
            }
            long started = Stopwatch.GetTimestamp();
            var live = decision.Export ?? throw new InvalidOperationException("the host gives no live combat for the engine: plan needs a host that holds the game");
            if (decision.Only)
                return meter.Count(() =>
                {
                    using var root = EngineWorld.Import(live.Bytes, live.Run);
                    return Only(root, decision.Legal, b, history);
                });
            double exportMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return meter.Count(() => Plan(live, b, history, importMs => meter.Imported(live.Bytes.Length, exportMs, importMs)));
        }
    }

    /// <summary>The route's answers between actions with their line's table: the line played out on the real combat,
    /// as far as it goes; where it stops with the combat in progress, the pick's chance to win and HP as its search
    /// valued them.</summary>
    private static CombatRoute Tabled(CombatRoute route) => route with
    {
        Decide = (decision, seed, node, history) =>
        {
            var result = route.Decide(decision, seed, node, history);
            if (decision.Observation.Prompt != null || decision.Export is not { } live) return result;
            using var real = Together(decision.Observation) ? EngineWorld.Alone(live.Bytes, live.Run, opening: false) : EngineWorld.Import(live.Bytes, live.Run);
            var table = FightTables.Of(real, [result.First, .. result.Plan]);
            if (table.Status == CombatStatus.InProgress && result.Candidates is [{ Win: { } win, Hp: { } hp }, ..])
                table = table with { Win = win, HpLeft = hp };
            return result with { Table = table };
        },
    };

    /// <summary>
    /// max's route (<see cref="SolverRoute"/>): the combat taken in once where a solver begins, each turn's search a copy
    /// of that world with the actions played since and the state's path applied. At a card prompt the engine cannot take
    /// in (one that opens its combat, as Gambling Chip's discard, or one off the solver's line) the solver begins on the
    /// node's clone, every answer crossed there, and is the engine's from where the player can play again.
    /// </summary>
    private CombatRoute Solver(Budget budget, string name, WorkerPool pool, bool background)
    {
        // The beam's route keeps its plan only while the beam decides one decision after another. Once the solver
        // answers, a step of that plan left over would meet a decision the beam did not plan (it showed as a replan at
        // the next combat's first prompt, 16 in a SILENT A10 max run): the beam begins again with no plan.
        CombatRoute? beam = null;
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
            // A player's question about given first actions is the beam's (Only), whatever the effort.
            if (decision.Only) return Route(budget with { Solve = 0 }, name, pool).Decide(decision, seed, node, history);
            var coop = Alone(decision);
            using var world = coop?.World;
            var asked = coop?.Decision ?? decision;
            CombatDecisionResult Decide()
            {
                bool byBeam = false;
                // A solver keeps the world it begins on, after this decision too: a copy of the co-op world.
                var made = route.Decide(asked.Observation, asked.Legal, history, () => world != null ? Taken(world.Clone()) : Taken(decision, node, pool), () =>
                {
                    byBeam = true;
                    return (beam ??= Route(budget with { Solve = 0 }, name, pool)).Decide(decision, seed, node, history);
                }, decision.Early, decision.Hurry, decision.After);
                if (!byBeam) beam = null;
                return made;
            }
            if (PimcBrain.Forced(asked.Observation, asked.Legal) != null) return Decide();
            CombatDecisionResult? made = null;
            meter.Count(() =>
            {
                made = Decide();
                return new(made, 0, CombatStatus.InProgress, made.Nodes ?? 0, made.Turns ?? 0, false);
            });
            return made!;
        }, budget, () => meter.Tally, route.Rest);
    }

    /// <summary>The combat where <paramref name="decision"/> stands, for a solver: the live combat, or at a card prompt
    /// with none (the engine takes a combat in between actions only) the node's clone there; null with neither.</summary>
    private TakenCombat? Taken(CombatDecision decision, NodeClone? node, WorkerPool pool)
    {
        if (decision.Export is { } live) return Taken(live);
        if (decision.Observation.Prompt is not { } prompt || node == null) return null;
        var crossing = Crossing(node, pool);
        return new(played => Observe(crossing, played),
            // Every answer crossed side by side first: the solver crosses the prompt before it searches on.
            (budget, history) => Plan(prompt, crossing, budget, history, int.MaxValue),
            (history, decay, stop) => new Solving(new EngineSolver(_net.Value, net.FightValue, crossing.At([]).Step.Run, path => crossing.At(path).Crossed,
                history, decay, threads, stop: stop)));
    }

    /// <summary>The combat's observation after <paramref name="played"/> from the prompt the clone stands at: along its
    /// answers on the clone, and in the engine from where the player can play again.</summary>
    private static CombatObservation Observe(PromptRoot.Crossing crossing, IReadOnlyList<CombatAction> played)
    {
        for (int answered = 0; ; answered++)
        {
            var place = crossing.At([.. played.Take(answered)]).Crossed;
            if (place.Export != null)
            {
                using var at = EngineWorld.Import(place.Export, place.Run);
                foreach (var action in played.Skip(answered)) at.Execute(action);
                return at.Observe();
            }
            if (answered == played.Count) return place.Observation;
        }
    }

    /// <summary>The combat as <paramref name="live"/> is, for a solver.</summary>
    private TakenCombat Taken(CombatExport live) => Taken(EngineWorld.Import(live.Bytes, live.Run));

    /// <summary>The combat where <paramref name="root"/> stands, for a solver, which keeps the world.</summary>
    private TakenCombat Taken(EngineWorld root)
    {
        return new(played =>
        {
            using var at = root.Clone();
            foreach (var action in played) at.Execute(action);
            return at.Observe();
        }, (budget, history) => Plan(root, budget, history),
            (history, decay, stop) => new Solving(new EngineSolver(_net.Value, net.FightValue, root, history, decay, threads, stop: stop)));
    }

    /// <summary>The engine's solver as the route plays one.</summary>
    private sealed class Solving(EngineSolver solver) : ICombatSolver
    {
        public long Nodes => solver.Counts.Nodes;
        public int Left => solver.Counts.Left;
        public long Settled => solver.Counts.Settled;

        public bool Content(long begun) => solver.Content(begun);

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
