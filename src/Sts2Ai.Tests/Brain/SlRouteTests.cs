using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

/// <summary>
/// The SL planner's C# side on a small exact combat: the route that follows its plans (<see cref="SlRoute"/>), the plan
/// made of what the engine found (<see cref="PlanOf"/>), the budgets, and max's solver route (<see cref="SolverRoute"/>)
/// over a solver that tries every line. The search itself is the engine's, tested there
/// (engine/crates/sts2ai-engine/src/search/).
/// </summary>
public class SlRouteTests
{
    /// <summary>
    /// A small exact combat: 2 energy and 3 cards a turn from a fixed draw order, Strikes (1 energy, 6 damage to the one
    /// enemy) and Defends (1 energy, 5 block this turn), against an enemy with 24 HP whose attacks are known for every
    /// round, 4 then 13 then 4 again. HP never comes back, so the planner's bound holds and a full search is exact.
    /// </summary>
    private sealed class Toy : ICombatWorld
    {
        private static readonly string[] Deck = ["S", "D", "S", "S", "D", "D", "S", "S", "D", "S", "D", "S", "S", "D", "S"];
        private static readonly int[] Attacks = [4, 13, 4];
        private static readonly CombatObservation Start = Fixtures.Slugs();

        private readonly List<string> _steps = [];
        private int _hp, _enemy, _block, _energy, _round, _drawn;
        private List<(uint Uid, string Id)> _hand = [];

        private readonly int _enemyHp;

        /// <summary>A potion in the first slot that does nothing when drunk; none if null.</summary>
        private readonly string? _potion;
        private bool _held;

        /// <summary>Whether the cards left in hand stay there at the turn's end, under their uids.</summary>
        private readonly bool _retain;

        public Toy(int enemyHp = 24, string? potion = null, bool retain = false)
        {
            _enemyHp = enemyHp;
            _potion = potion;
            _retain = retain;
            Reset();
        }

        private void Reset()
        {
            (_hp, _enemy, _block, _energy, _round, _drawn) = (20, _enemyHp, 0, 2, 1, 0);
            _held = _potion != null;
            _hand = [];
            Draw();
        }

        private void Draw()
        {
            for (int i = 0; i < 3; i++, _drawn++) _hand.Add(((uint)_drawn + 10, Deck[_drawn % Deck.Length]));
        }

        private int Attack => Attacks[(_round - 1) % Attacks.Length];

        public RunView Run() => Fixtures.Around(Observe());

        public CombatObservation Observe() => Start with
        {
            Round = _round,
            Player = Start.Player with { Hp = Math.Max(0, _hp), MaxHp = 20, Block = _block, Energy = _energy },
            Hand = [.. _hand.Select(c => new CardView { Id = c.Id, Uid = c.Uid })],
            Draw = [],
            Discard = [],
            Potions = [_held ? _potion : null, null, null],
            Enemies = [Start.Enemies[0] with { Uid = 1, Hp = Math.Max(0, _enemy), MaxHp = _enemyHp, Block = 0, Powers = [],
                Intents = [new IntentView { Type = "Attack", Damage = Attack, Hits = 1 }] }],
        };

        public IReadOnlyList<CombatAction> Legal() => Status != CombatStatus.InProgress ? [] :
            [CombatAction.EndTurn, .. _energy > 0 ? _hand.Select(c => CombatAction.Play(c.Uid, c.Id == "S" ? 1u : null)) : [],
                .. _held ? new[] { CombatAction.Potion(0) } : []];

        public CombatStatus Status => _hp <= 0 ? CombatStatus.Lost : _enemy <= 0 ? CombatStatus.Won : CombatStatus.InProgress;

        public ulong HiddenKey() => (ulong)_drawn;

        public void Execute(CombatAction action)
        {
            if (!Legal().Contains(action)) throw new InvalidOperationException($"{action} is not legal");
            _steps.Add(action.ToString());
            if (action.Kind == CombatActionKind.UsePotion)
            {
                _held = false;
                return;
            }
            if (action.Kind == CombatActionKind.EndTurn)
            {
                _hp -= Math.Max(0, Attack - _block);
                (_block, _energy, _round) = (0, 2, _round + 1);
                if (!_retain) _hand = [];
                Draw();
                return;
            }
            var card = _hand.First(c => c.Uid == action.Card);
            _hand.Remove(card);
            _energy--;
            if (card.Id == "S") _enemy -= 6;
            else _block += 5;
        }

        /// <summary>The snapshots taken here and not yet forgotten, and the forgotten ones, which search may not restore.</summary>
        private readonly HashSet<WorldSnapshot> _open = new(ReferenceEqualityComparer.Instance), _forgotten = new(ReferenceEqualityComparer.Instance);

        public int Open => _open.Count;

        public WorldSnapshot Snapshot()
        {
            var snapshot = new WorldSnapshot([.. _steps]);
            _open.Add(snapshot);
            return snapshot;
        }

        public void Forget(WorldSnapshot snapshot)
        {
            if (!_open.Remove(snapshot)) throw new InvalidOperationException("the snapshot was forgotten before, or is not this world's");
            _forgotten.Add(snapshot);
        }

        public void Restore(WorldSnapshot snapshot)
        {
            if (_forgotten.Contains(snapshot)) throw new InvalidOperationException("the snapshot was forgotten");
            _steps.Clear();
            Reset();
            foreach (var step in snapshot.Steps) Execute(CombatAction.Parse(step));
        }
    }


    private static readonly ToyLeaf Leaf = new();

    /// <summary>The toy after <paramref name="played"/>.</summary>
    private static Toy After(IReadOnlyList<CombatAction> played, int enemyHp = 24)
    {
        var world = new Toy(enemyHp);
        foreach (var action in played) world.Execute(action);
        return world;
    }

    /// <summary>The best end of the combat by trying every line, each state once, as the toy shows all of it but the
    /// draw; with the best step from each state.</summary>
    private static double Solve(Toy world, Dictionary<(ulong, ulong), (double Value, CombatAction? Step)> known)
    {
        if (world.Status != CombatStatus.InProgress) return Leaf.Outcome(world.Status, world.Observe(), world.Run(), []).Value;
        var key = (ProtocolJson.Hash(world.Observe()), world.HiddenKey());
        if (known.TryGetValue(key, out var seen)) return seen.Value;
        var here = world.Snapshot();
        (double Value, CombatAction? Step) best = (double.NegativeInfinity, null);
        foreach (var action in world.Legal())
        {
            world.Restore(here);
            world.Execute(action);
            double value = Solve(world, known);
            if (value > best.Value) best = (value, action);
        }
        world.Restore(here);
        known[key] = best;
        return best.Value;
    }

    /// <summary>The best end's value after <paramref name="played"/>.</summary>
    private static double Best(IReadOnlyList<CombatAction> played) => Solve(After(played), []);

    /// <summary>The best line after <paramref name="played"/>, to the combat's end.</summary>
    private static List<CombatAction> BestLine(IReadOnlyList<CombatAction> played)
    {
        var world = After(played);
        var known = new Dictionary<(ulong, ulong), (double Value, CombatAction? Step)>();
        Solve(world, known);
        var line = new List<CombatAction>();
        while (world.Status == CombatStatus.InProgress)
        {
            var step = known[(ProtocolJson.Hash(world.Observe()), world.HiddenKey())].Step!.Value;
            line.Add(step);
            world.Execute(step);
        }
        return line;
    }

    /// <summary>A plain line after <paramref name="played"/>, to the combat's end: each turn the Strikes first, then the
    /// Defends, while there is energy.</summary>
    private static List<CombatAction> Greedy(IReadOnlyList<CombatAction> played, int enemyHp = 24)
    {
        var world = After(played, enemyHp);
        var line = new List<CombatAction>();
        while (world.Status == CombatStatus.InProgress)
        {
            var plays = world.Legal().Where(a => a.Kind == CombatActionKind.PlayCard).ToList();
            var step = plays.Where(a => a.Target != null).Concat(plays).Append(CombatAction.EndTurn).First();
            line.Add(step);
            world.Execute(step);
        }
        return line;
    }

    /// <summary><paramref name="line"/> after <paramref name="played"/> as a search's end: the hash of each step's
    /// observation, and the leaf's outcome where it ends.</summary>
    private static TurnEnd End(IReadOnlyList<CombatAction> played, IReadOnlyList<CombatAction> line, int enemyHp = 24)
    {
        var world = After(played, enemyHp);
        var hashes = new List<ulong>();
        foreach (var step in line)
        {
            hashes.Add(ProtocolJson.Hash(world.Observe()));
            world.Execute(step);
        }
        var outcome = Leaf.Outcome(world.Status, world.Observe(), world.Run(), []);
        return new(line, hashes, world.Status, outcome.Value, 0, [], outcome.Win, outcome.Hp);
    }

    /// <summary>The plan of <paramref name="line"/> after <paramref name="played"/>, as the engine's search would give it.</summary>
    private static SlPlan Planned(IReadOnlyList<CombatAction> played, IReadOnlyList<CombatAction> line, int replan, bool proved = false,
        int nodes = 0, int enemyHp = 24)
    {
        var end = End(played, line, enemyHp);
        return PlanOf.Found(end, 0, [end], proved, replan, nodes, 1);
    }

    /// <summary>A combat played through the route, each plan the plain line from where the combat stands.</summary>
    private static (int Plans, CombatStatus End) PlayRoute(Budget budget, int enemyHp)
    {
        var played = new List<CombatAction>();
        var real = new Toy(enemyHp);
        var route = new SlRoute(budget);
        int plans = 0;
        while (real.Status == CombatStatus.InProgress)
        {
            var result = route.Decide(real.Observe(), real.Legal(), b =>
            {
                plans++;
                return Planned(played, Greedy(played, enemyHp), b.Replan, enemyHp: enemyHp);
            });
            Assert.False(result.Replanned);
            real.Execute(result.First);
            played.Add(result.First);
        }
        return (plans, real.Status);
    }

    [Fact]
    public void TheRoutePlansEachFightWithTheBudgetOfItsRoom()
    {
        var budget = new Budget(NodesPerWorld: 100_000, Beam: 2) with { Boss = new Budget(NodesPerWorld: 100_000, Beam: 3) };
        foreach (var (room, beam) in new[] { ("Monster", 2), ("Elite", 2), ("Boss", 3) })
        {
            var real = new Toy();
            var used = new List<Budget>();
            new SlRoute(budget).Decide(real.Observe() with { Room = room }, real.Legal(), b =>
            {
                used.Add(b);
                return Planned([], Greedy([]), b.Replan);
            });
            Assert.Equal(beam, Assert.Single(used).Beam);
            Assert.Null(used[0].Boss);
        }
    }

    [Fact]
    public void AfterItsLossPlansALosingCombatIsPlayedToItsEnd()
    {
        var (plans, end) = PlayRoute(new Budget(NodesPerWorld: 100_000, Beam: 2), enemyHp: 500);
        var limited = PlayRoute(new Budget(NodesPerWorld: 100_000, Beam: 2, LossPlans: 2), enemyHp: 500);

        Assert.Equal(CombatStatus.Lost, end);
        Assert.True(plans > 2);
        Assert.Equal((2, CombatStatus.Lost), limited);
    }

    [Fact]
    public void AWinningCombatIsPlannedOnce() =>
        Assert.Equal((1, CombatStatus.Won), PlayRoute(new Budget(NodesPerWorld: 100_000, Beam: 1000, LossPlans: 1), enemyHp: 24));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    public void ALossTheSearchMayBeWrongAboutIsPlayedForItsReplanTurns(int replan)
    {
        var line = Greedy([], enemyHp: 500);
        var plan = Planned([], line, replan, enemyHp: 500);

        Assert.Equal(CombatStatus.Lost, plan.End);
        var steps = plan.Result.Plan.Prepend(plan.Result.First).ToList();
        int ends = steps.Count(a => a.Kind == CombatActionKind.EndTurn);
        Assert.Equal(replan > 0 ? replan : line.Count(a => a.Kind == CombatActionKind.EndTurn), ends);
        if (replan > 0) Assert.Equal(CombatActionKind.EndTurn, steps[^1].Kind);
        Assert.Equal(steps.Count - 1, plan.Result.PlanHashes!.Count);
        // A proved loss has nothing better: it is played to its end.
        Assert.Equal(line, Planned([], line, replan, proved: true, enemyHp: 500).Result.Plan.Prepend(plan.Result.First));
    }

    [Fact]
    public void ItsCandidatesStartWithItsPickThenGoByValue()
    {
        var pick = End([], Greedy([]));
        var others = new Toy().Legal().Where(a => a != pick.Actions[0]).Select(first => End([], [first, .. Greedy([first])])).ToList();
        var plan = PlanOf.Found(pick, 0, [.. others, pick], false, 1, 0, 1);

        Assert.Equal(pick.Actions[0].ToString(), plan.Result.Candidates[0].Action);
        Assert.Equal(pick.Value, plan.Result.Candidates[0].Value, 9);
        Assert.Equal(1, plan.Result.Candidates[0].Win);
        Assert.Equal(After(pick.Actions).Observe().Player.Hp, plan.Result.Candidates[0].Hp);
        var rest = plan.Result.Candidates.Skip(1).Select(c => c.Value).ToList();
        Assert.Equal(rest.OrderByDescending(v => v), rest);
        Assert.Equal(others.Count + 1, plan.Result.Candidates.Count);
    }

    [Fact]
    public void TheTurnDecayIsTheBudgetsInEveryRoom()
    {
        var budget = Budget.Effort("low") with { Elite = Budget.Effort("high"), TurnDecay = SlPlanner.PlayerTurnDecay };
        Assert.Equal(Budget.Effort("high") with { TurnDecay = SlPlanner.PlayerTurnDecay }, budget.For("Elite"));
        Assert.Equal(SlPlanner.PlayerTurnDecay, budget.For("Monster").TurnDecay);
        Assert.Equal("beam 8x300 easy 4 loss plans 3 turn decay 0.999 elite (beam 16x300)", budget.ToString());
        // Self-play's budgets have none.
        Assert.All(Budget.Efforts, level => Assert.Equal(1, Budget.Effort(level).TurnDecay));
    }

    [Fact]
    public void EachEffortIsAPlannerBudget()
    {
        Assert.Equal(new Budget(NodesPerWorld: 300, Beam: 8, Replan: 1), Budget.Effort("medium"));
        Assert.Equal(new Budget(NodesPerWorld: 300, Beam: 16, Replan: 1), Budget.Effort("high"));
        Assert.All(Budget.Efforts, e => Assert.True(Budget.Effort(e).Beam > 0));
        Assert.Equal("beam 8x300 easy 4 loss plans 3", Budget.Effort("low").ToString());
        Assert.Equal("beam 16xall", (Budget.Effort("max") with { NodesPerWorld = SlPlanner.Unlimited }).Size);
        Assert.Throws<ArgumentException>(() => Budget.Effort("ultra"));
    }

    [Fact]
    public void ARecordGoesOnFromItsHistory()
    {
        var record = new MoveRecord();
        record.Witness(Fixtures.Slugs());
        var copy = new MoveRecord(record.History);
        copy.Witness(Fixtures.Slugs());
        copy.Witness(Fixtures.Slugs() with { Round = 2 });

        Assert.All(copy.History, e => Assert.Equal([1, 2], e.Turns.Select(t => t.Round)));
        Assert.All(record.History, e => Assert.Equal([1], e.Turns.Select(t => t.Round)));
    }

    /// <summary>
    /// A solver over the toy, for the route: its whole search is <see cref="Work"/> nodes, and until they are searched its
    /// pick is the seed's line, then the best line there is. Its root moves along the steps played; each search first
    /// waits <paramref name="slow"/> milliseconds.
    /// </summary>
    private sealed class ToySolver(IReadOnlyList<CombatAction> begun, int slow) : ICombatSolver
    {
        public const long Work = 60;
        private readonly List<CombatAction> _root = [.. begun];
        private List<CombatAction> _line = [];

        public long Nodes { get; private set; }
        public int Left => Nodes < Work ? 1 : 0;
        public long Settled { get; private set; }

        private CombatAction? First => _line.Count > 0 ? _line[0] : null;

        private void Look(CombatAction? before)
        {
            if (First != before) Settled = Nodes;
        }

        public void Seed(SlPlan plan) => _line = [.. plan.Line!.Actions];

        public void Run(long nodes, CancellationToken stop = default)
        {
            Thread.Sleep(slow);
            stop.ThrowIfCancellationRequested();
            if (Left == 0) return;
            var before = First;
            Nodes += Math.Min(nodes, Work - Nodes);
            if (Left == 0) _line = BestLine(_root);
            Look(before);
        }

        public void Advance(IReadOnlyList<CombatAction> played, IReadOnlyList<EnemyTurns> moves, CancellationToken stop = default)
        {
            stop.ThrowIfCancellationRequested();
            _root.AddRange(played);
            var before = First;
            _line = _line.Take(played.Count).SequenceEqual(played) ? [.. _line.Skip(played.Count)] : BestLine(_root);
            Look(before);
        }

        public SlPlan Result(int replan) => Planned(_root, _line, replan, Left == 0, (int)Nodes);

        public void Dispose()
        {
        }
    }

    /// <summary>The toy's combat taken in after <paramref name="played"/>, as a solver begins on it: the plain line is
    /// the beam's plan there.</summary>
    private static TakenCombat ToyTaken(IReadOnlyList<CombatAction> played, int slow = 0) =>
        new(more => After([.. played, .. more]).Observe(), (budget, _) => Planned(played, Greedy(played), budget.Replan),
            (_, _) => new ToySolver(played, slow));

    /// <summary>The record of enemy moves after <paramref name="played"/>, as a decision there has it: each round's
    /// intents from the second on, as the planner's lines record them from a root with none.</summary>
    private static IReadOnlyList<EnemyTurns> Moves(IReadOnlyList<CombatAction> played)
    {
        var world = new Toy();
        var record = new MoveRecord();
        foreach (var action in played)
        {
            world.Execute(action);
            if (world.Status == CombatStatus.InProgress && world.Observe().Round != 1) record.Witness(world.Observe());
        }
        return record.History;
    }

    /// <summary>Plays the toy's combat to its end by the solver's route (<see cref="SolverRoute"/>); at each decision,
    /// <paramref name="hand"/> may take another action than the route's. With <paramref name="background"/>, the solver
    /// searches between decisions too, and <paramref name="between"/> runs after each. Each search of the solvers waits
    /// <paramref name="slow"/> milliseconds first.</summary>
    private static (List<CombatAction> Played, SolverRoute Route) PlayBySolver(Budget budget, Func<int, CombatAction, CombatAction>? hand = null,
        bool background = false, Action<int>? between = null, Action<int, CombatDecisionResult>? early = null,
        Action<int, CombatDecisionResult>? after = null, int slow = 0)
    {
        var route = new SolverRoute(budget, background);
        var world = new Toy();
        var played = new List<CombatAction>();
        for (int d = 0; world.Status == CombatStatus.InProgress; d++)
        {
            var at = played.ToList();
            int decision = d;
            var answer = route.Decide(world.Observe(), world.Legal(), Moves(played), () => ToyTaken(at, slow),
                () => throw new InvalidOperationException("the toy is always taken in"), early == null ? null : r => early(decision, r),
                after: after == null ? null : r => after(decision, r)).First;
            var taken = hand?.Invoke(d, answer) ?? answer;
            world.Execute(taken);
            played.Add(taken);
            between?.Invoke(d);
        }
        route.Rest();
        return (played, route);
    }

    /// <summary>The value the plain line from the start ends with: the beam's plan a solver begins from.</summary>
    private static double BeamValue => End([], Greedy([])).Value;

    [Fact]
    public void TheSolversRouteFollowsItsPicksWithOneSolverACombat()
    {
        // Searched to the end at the first decision: the best line there is, played to its end.
        var full = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: long.MaxValue));
        Assert.Equal(1, full.Route.Begun);
        Assert.Equal(Best([]), Best(full.Played), 9);

        // A few nodes a decision: the same each time, one solver the combat long, never worse than the beam it began from.
        var few = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 20));
        Assert.Equal(few.Played, PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 20)).Played);
        Assert.Equal(1, few.Route.Begun);
        Assert.True(Best(few.Played) >= BeamValue - 1e-9);
    }

    [Fact]
    public void TheSolversAnswerSaysWhenItSettled()
    {
        // Searched to the end at the first decision: the answer settled where the best line replaced the beam's, if its
        // first action is another; a decision the beam's answer holds through settled at 0.
        var world = new Toy();
        var answer = new SolverRoute(new Budget(NodesPerWorld: 10, Beam: 1, Solve: long.MaxValue))
            .Decide(world.Observe(), world.Legal(), [], () => ToyTaken([]), () => throw new InvalidOperationException("the toy is always taken in"));
        Assert.Equal(answer.First != Greedy([])[0] ? (int)ToySolver.Work : 0, answer.Settled);

        // The beam's answer, where no solver begins, says nothing of it.
        var beam = new SolverRoute(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 20))
            .Decide(world.Observe(), world.Legal(), [], () => null, () => PimcBrain.Only(CombatAction.EndTurn));
        Assert.Null(beam.Settled);
    }

    [Fact]
    public void TheSolversRouteBeginsAgainWhereTheCombatLeftItsLine()
    {
        // The first decision taken by hand, otherwise: a new solver from there, which plays the best line there.
        CombatAction? other = null;
        var (played, route) = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: long.MaxValue),
            (d, answer) => d > 0 ? answer : (other = new Toy().Legal().First(a => a != answer)).Value);
        Assert.Equal(2, route.Begun);
        Assert.Equal(Best([other!.Value]), Best(played), 9);

        // Where the combat cannot be taken in, the beam decides.
        var world = new Toy();
        var beam = new SolverRoute(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 20))
            .Decide(world.Observe(), world.Legal(), [], () => null, () => PimcBrain.Only(CombatAction.EndTurn));
        Assert.Equal(CombatAction.EndTurn, beam.First);
    }

    [Fact]
    public void TheSolverInTheBackgroundSearchesBetweenDecisions()
    {
        // Each decision searched in full: what the route plays without the background.
        var full = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: long.MaxValue), background: true);
        Assert.Equal(1, full.Route.Begun);
        Assert.Equal(Best([]), Best(full.Played), 9);

        // A few nodes a decision, at least: one solver the combat long, never worse than the beam it began from.
        var few = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 20), background: true);
        Assert.Equal(1, few.Route.Begun);
        Assert.True(Best(few.Played) >= BeamValue - 1e-9);

        // One node a decision, and time after the first: the search there went on to the end, so the rest is the best line.
        var waited = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 1), background: true, between: d =>
        {
            if (d == 0) Thread.Sleep(1000);
        });
        Assert.Equal(1, waited.Route.Begun);
        Assert.Equal(Best([waited.Played[0]]), Best(waited.Played), 9);

        // The first decision taken by hand, otherwise: a new solver from there.
        CombatAction? other = null;
        var (played, route) = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: long.MaxValue),
            (d, answer) => d > 0 ? answer : (other = new Toy().Legal().First(a => a != answer)).Value, background: true);
        Assert.Equal(2, route.Begun);
        Assert.Equal(Best([other!.Value]), Best(played), 9);
    }

    [Fact]
    public void TheSolverInTheBackgroundTellsItsPickSoFarAndAnswersAtOnceWhenHurried()
    {
        // The pick so far before each decision's search: the beam's plan where the solver begins, then the rest of the plan
        // given, which a search in full only keeps; between them, each pick that shows otherwise. None without the background.
        var budget = new Budget(NodesPerWorld: 10, Beam: 1, Solve: long.MaxValue);
        var early = new List<(int Decision, CombatDecisionResult Pick)>();
        var (played, _) = PlayBySolver(budget, background: true, early: (d, r) => early.Add((d, r)));
        Assert.Equal((0, Greedy([])[0]), (early[0].Decision, early[0].Pick.First));
        var firsts = early.GroupBy(e => e.Decision).Where(g => g.Key > 0).Select(g => g.First()).ToList();
        Assert.NotEmpty(firsts);
        Assert.All(firsts, e => Assert.Equal(played[e.Decision], e.Pick.First));
        static string Shows(CombatDecisionResult r) => string.Join(" ", [r.First, .. r.Plan]) + $" {r.Candidates[0].Win} {r.Candidates[0].Hp} {r.Proved}";
        Assert.All(early.Zip(early.Skip(1)).Where(p => p.First.Decision == p.Second.Decision),
            p => Assert.NotEqual(Shows(p.First.Pick), Shows(p.Second.Pick)));
        var none = new List<int>();
        PlayBySolver(budget, early: (d, _) => none.Add(d));
        Assert.Empty(none);

        // A host that moved on: the decision is answered once the root's turn is searched, not at the end of the combat.
        var world = new Toy();
        CombatDecisionResult Decide(CancellationToken hurry)
        {
            var route = new SolverRoute(budget, background: true);
            try
            {
                return route.Decide(world.Observe(), world.Legal(), [], () => ToyTaken([], slow: 20), () => throw new InvalidOperationException(), hurry: hurry);
            }
            finally
            {
                route.Rest();
            }
        }
        using var moved = new CancellationTokenSource();
        moved.Cancel();
        Assert.True(Decide(moved.Token).Nodes < Decide(CancellationToken.None).Nodes);
    }

    [Fact]
    public void TheSolverInTheBackgroundTellsThePlanAfterItsAnswerUntilTheNextDecision()
    {
        // One node a decision, a slow search, and time after the first answer: the solver searches on from where it leads
        // to the end, and the answer is told again as the plan after its step changes, the last time proved, with the best
        // line after that step. The next decision's pick so far is that plan, and nothing more of the first answer is told
        // once it begins. None without the background.
        var budget = new Budget(NodesPerWorld: 10, Beam: 1, Solve: 1);
        var told = new List<(string Kind, int Decision, CombatDecisionResult Pick)>();
        var (played, _) = PlayBySolver(budget, background: true, between: d =>
        {
            if (d == 0) Thread.Sleep(3000);
        }, early: (d, r) =>
        {
            lock (told) told.Add(("early", d, r));
        }, after: (d, r) =>
        {
            lock (told) told.Add(("after", d, r));
        }, slow: 20);
        var first = told.Where(t => t is ("after", 0, _)).Select(t => t.Pick).ToList();
        Assert.NotEmpty(first);
        Assert.All(first, p => Assert.Equal(played[0], p.First));
        var best = BestLine([played[0]]);
        Assert.NotEqual(Greedy([played[0]]), best);
        Assert.True(first[^1].Proved);
        Assert.Equal(best, first[^1].Plan);
        var next = told.FindIndex(t => t is ("early", 1, _));
        Assert.Equal(best, [told[next].Pick.First, .. told[next].Pick.Plan]);
        Assert.DoesNotContain(told.Skip(next), t => t is ("after", 0, _));

        var none = new List<int>();
        PlayBySolver(budget, after: (d, _) => none.Add(d), slow: 20);
        Assert.Empty(none);
    }
}
