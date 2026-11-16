using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class SlPlannerTests
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

        public Toy(int enemyHp = 24, string? potion = null)
        {
            _enemyHp = enemyHp;
            _potion = potion;
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
                _hand = [];
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

    /// <summary>The best end of the combat by trying every line; each state once, as the toy shows all of it but the draw.</summary>
    private static double BruteForce(Toy world, Dictionary<(ulong, ulong), double> known)
    {
        if (world.Status != CombatStatus.InProgress) return Leaf.Outcome(world.Status, world.Observe(), world.Run(), []).Value;
        var key = (ProtocolJson.Hash(world.Observe()), world.HiddenKey());
        if (known.TryGetValue(key, out var value)) return value;
        var here = world.Snapshot();
        double best = double.NegativeInfinity;
        foreach (var action in world.Legal())
        {
            world.Restore(here);
            world.Execute(action);
            best = Math.Max(best, BruteForce(world, known));
        }
        world.Restore(here);
        return known[key] = best;
    }

    /// <summary>Plays the plan from the start: each step at the observation it expects, to the end it claims.</summary>
    private static void AssertReplays(SlPlan plan, int enemyHp = 24)
    {
        var world = new Toy(enemyHp);
        var steps = plan.Result.Plan.Prepend(plan.Result.First).ToList();
        var hashes = plan.Result.PlanHashes!;
        Assert.Equal(steps.Count - 1, hashes.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            if (i > 0) Assert.Equal(hashes[i - 1], ProtocolJson.Hash(world.Observe()));
            world.Execute(steps[i]);
        }
        // A losing plan is played for its first turn only, unless proved; InProgress checks the steps alone.
        if (plan.End != CombatStatus.Won && !(plan.End == CombatStatus.Lost && plan.Proved)) return;
        Assert.Equal(plan.End, world.Status);
        Assert.Equal(plan.Value, Leaf.Outcome(world.Status, world.Observe(), world.Run(), []).Value, 9);
    }

    [Fact]
    public void AFullSearchFindsTheBestLine()
    {
        var world = new Toy();
        double best = BruteForce(world, []);
        var plan = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 1000));

        Assert.True(plan.Proved);
        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(best, plan.Value, 9);
        AssertReplays(plan);
        // The result says what its search took, for the log.
        Assert.Equal((plan.Nodes, plan.Turns), (plan.Result.Nodes!.Value, plan.Result.Turns!.Value));
    }

    /// <summary>A leaf that tells how it was asked: one state at a time, or several together.</summary>
    private sealed class Asked : ICombatLeaf
    {
        public int Alone { get; private set; }
        public List<int> Together { get; } = [];

        public LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history)
        {
            Alone++;
            return Leaf.Outcome(status, obs, run, history);
        }

        public IReadOnlyList<LeafOutcome> Outcomes(IReadOnlyList<LeafState> states)
        {
            Together.Add(states.Count);
            return [.. states.Select(s => Leaf.Outcome(s.Status, s.Obs, s.Run, s.History))];
        }
    }

    [Fact]
    public void TheLeafIsAskedAboutATurnsEndsTogether()
    {
        var asked = new Asked();
        var found = new SlPlanner(asked).SearchTurn(new Toy(), [], 100_000, int.MaxValue);

        // One question a turn's search, about every end it found; no end is valued by itself.
        Assert.Single(asked.Together);
        Assert.True(asked.Together[0] >= found.Ends.Count);
        Assert.Equal(0, asked.Alone);

        // The plan is the one a leaf asked one state at a time gives.
        var plan = new SlPlanner(new Asked()).Plan(new Toy(), new Budget(NodesPerWorld: 100_000, Beam: 1000));
        var plain = new SlPlanner(Leaf).Plan(new Toy(), new Budget(NodesPerWorld: 100_000, Beam: 1000));
        Assert.Equal((plain.Value, plain.End, plain.Nodes, plain.Result.First), (plan.Value, plan.End, plan.Nodes, plan.Result.First));
        Assert.Equal(plain.Result.Plan, plan.Result.Plan);
    }

    [Fact]
    public void ThePlannerLetsGoOfEveryStateItLeaves()
    {
        var world = new Toy();
        new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 1000));
        Assert.Equal(0, world.Open);
        new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 4, Beam: 1));
        Assert.Equal(0, world.Open);
    }

    [Fact]
    public void EachCandidateShowsHowItsBestLineEnds()
    {
        var world = new Toy();
        var plan = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 1000));

        var chosen = plan.Result.Candidates[0];
        Assert.Equal(plan.Result.First.ToString(), chosen.Action);
        Assert.Equal(1, chosen.Win);
        var end = new Toy();
        foreach (var step in plan.Result.Plan.Prepend(plan.Result.First)) end.Execute(step);
        Assert.Equal(end.Observe().Player.Hp, chosen.Hp);
        // Every line here reaches the combat's end: a win with its HP, or a loss.
        Assert.All(plan.Result.Candidates, c => Assert.True(c.Win == 1 ? c.Hp > 0 : c is { Win: 0, Hp: 0 }));
    }

    [Fact]
    public void ANarrowBeamProvesNothingAndStillPlaysItsLine()
    {
        var world = new Toy();
        var plan = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 4, Beam: 1));

        Assert.False(plan.Proved);
        AssertReplays(plan);
    }

    [Fact]
    public void ALosingPlanIsPlayedForOneTurn()
    {
        var world = new Toy(enemyHp: 500);
        var plan = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 2));

        Assert.Equal(CombatStatus.Lost, plan.End);
        Assert.False(plan.Proved);
        var steps = plan.Result.Plan.Prepend(plan.Result.First).ToList();
        Assert.Equal(CombatActionKind.EndTurn, steps[^1].Kind);
        Assert.Single(steps, a => a.Kind == CombatActionKind.EndTurn);
        Assert.Equal(steps.Count - 1, plan.Result.PlanHashes!.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void ALosingPlanIsPlayedForItsReplanTurns(int replan)
    {
        var world = new Toy(enemyHp: 500);
        var plan = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 2, Replan: replan));

        Assert.Equal(CombatStatus.Lost, plan.End);
        Assert.False(plan.Proved);
        var steps = plan.Result.Plan.Prepend(plan.Result.First).ToList();
        int ends = steps.Count(a => a.Kind == CombatActionKind.EndTurn);
        if (replan > 0) Assert.Equal(replan, ends);
        else Assert.True(ends > 2);
        // Played to its end, the plan dies where it claims.
        if (replan == 0)
        {
            var replay = new Toy(enemyHp: 500);
            foreach (var step in steps) replay.Execute(step);
            Assert.Equal(CombatStatus.Lost, replay.Status);
        }
    }

    /// <summary>A combat played through the route, the real toy apart from the one planned on, as in a run.</summary>
    private static (int Plans, CombatStatus End) PlayRoute(Budget budget, int enemyHp)
    {
        var real = new Toy(enemyHp);
        var route = new SlRoute(budget);
        int plans = 0;
        while (real.Status == CombatStatus.InProgress)
        {
            var result = route.Decide(real.Observe(), real.Legal(), b =>
            {
                plans++;
                var world = new Toy(enemyHp);
                world.Restore(real.Snapshot());
                return new SlPlanner(Leaf).Plan(world, b);
            });
            Assert.False(result.Replanned);
            real.Execute(result.First);
        }
        return (plans, real.Status);
    }

    [Fact]
    public void TheRoutePlansEachFightWithTheBudgetOfItsRoom()
    {
        var budget = new Budget(NodesPerWorld: 100_000, Beam: 2) with { Boss = new Budget(NodesPerWorld: 100_000, Beam: 3) };
        foreach (var (room, beam) in new[] { ("Monster", 2), ("Elite", 2), ("Boss", 3) })
        {
            var real = new Toy(enemyHp: 30);
            var used = new List<Budget>();
            new SlRoute(budget).Decide(real.Observe() with { Room = room }, real.Legal(), b =>
            {
                used.Add(b);
                var world = new Toy(30);
                world.Restore(real.Snapshot());
                return new SlPlanner(Leaf).Plan(world, b);
            });
            Assert.Equal(beam, Assert.Single(used).Beam);
            Assert.Null(used[0].Boss);
        }
    }

    [Fact]
    public void AfterItsLossPlansALosingCombatIsPlayedToItsEnd()
    {
        var (Plans, End) = PlayRoute(new Budget(NodesPerWorld: 100_000, Beam: 2), enemyHp: 500);
        var limited = PlayRoute(new Budget(NodesPerWorld: 100_000, Beam: 2, LossPlans: 2), enemyHp: 500);

        Assert.Equal(CombatStatus.Lost, End);
        Assert.True(Plans > 2);
        Assert.Equal((2, CombatStatus.Lost), limited);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(1000)]
    public void ATurnSearchedInFullNarrowsToTheSearchWithAKeep(int keep)
    {
        var planner = new SlPlanner(Leaf);
        foreach (var path in new[] { Array.Empty<CombatAction>(), [CombatAction.EndTurn] })
        {
            TurnSearch Searched(int k)
            {
                var world = new Toy();
                foreach (var a in path) world.Execute(a);
                return planner.SearchTurn(world, [], 200, k);
            }
            var direct = Searched(keep);
            var narrowed = SlPlanner.Narrow(Searched(int.MaxValue), keep);
            Assert.Equal((direct.Nodes, direct.Cut), (narrowed.Nodes, narrowed.Cut));
            Assert.Equal(direct.Ends.Select(e => (string.Join(" ", e.Actions), e.Status, e.Value, e.Key)),
                narrowed.Ends.Select(e => (string.Join(" ", e.Actions), e.Status, e.Value, e.Key)));
        }
    }

    [Fact]
    public void TheFullBeamTakesTheDivesTurnsInsteadOfSearchingThemAgain()
    {
        var planner = new SlPlanner(Leaf);
        var searched = new List<string>();
        var plan = SlPlanner.Search(new Budget(NodesPerWorld: 100_000, Beam: 4), [], (jobs, nodes, keep) => [.. jobs.Select(job =>
        {
            searched.Add(string.Join(" ", job.Path));
            var world = new Toy();
            foreach (var a in job.Path) world.Execute(a);
            return planner.SearchTurn(world, job.History, nodes, keep);
        })]);

        Assert.Single(searched, "");
        Assert.Equal(searched.Count, searched.Distinct().Count());
        Assert.Equal(new SlPlanner(Leaf).Plan(new Toy(), new Budget(NodesPerWorld: 100_000, Beam: 4)).Value, plan.Value, 9);
        AssertReplays(plan);
    }

    [Fact]
    public void WhenTheDiveWinsTheFullBeamKeepsTheEasyBeam()
    {
        var world = new Toy();
        var easy = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 1000, EasyBeam: 2));
        var narrow = new SlPlanner(Leaf).Plan(new Toy(), new Budget(NodesPerWorld: 100_000, Beam: 2));

        Assert.Equal(CombatStatus.Won, easy.End);
        Assert.Equal((narrow.Value, narrow.Nodes, narrow.Turns), (easy.Value, easy.Nodes, easy.Turns));
        Assert.Equal(narrow.Result.Plan, easy.Result.Plan);
    }

    [Fact]
    public void WhenTheDiveLosesTheFullBeamKeepsTheBeam()
    {
        var losing = new SlPlanner(Leaf).Plan(new Toy(enemyHp: 500), new Budget(NodesPerWorld: 100_000, Beam: 1000, EasyBeam: 2));
        var full = new SlPlanner(Leaf).Plan(new Toy(enemyHp: 500), new Budget(NodesPerWorld: 100_000, Beam: 1000));

        Assert.Equal((full.Value, full.Nodes, full.Turns, full.Proved), (losing.Value, losing.Nodes, losing.Turns, losing.Proved));
    }

    /// <summary>
    /// The dive follows play:1, the start the leaf values higher, to a win after two turns; play:2's win comes after one,
    /// worth <paramref name="below"/> less. Without a turn decay only a win worth exactly the same takes the place of the
    /// slower one; with one, a win worth less by no more than the turn takes does too. The plan's value is its end's.
    /// </summary>
    [Theory]
    [InlineData(1, 0.0, "play:2")]
    [InlineData(1, 1e-12, "play:1")]
    [InlineData(SlPlanner.PlayerTurnDecay, 0.0005, "play:2")]
    [InlineData(SlPlanner.PlayerTurnDecay, 0.002, "play:1")]
    public void ASoonerWinIsThePickOnAnExactTieOrWithinTheTurnDecay(double decay, double below, string pick)
    {
        TurnEnd End(CombatStatus status, double value, ulong key, params CombatAction[] actions) =>
            new([.. actions], [.. actions.Select(_ => key)], status, value, key, []);
        var turns = new Dictionary<string, TurnSearch>
        {
            [""] = new([End(CombatStatus.InProgress, 0.6, 1, CombatAction.Play(1), CombatAction.EndTurn),
                End(CombatStatus.InProgress, 0.5, 2, CombatAction.Play(2), CombatAction.EndTurn)], 2, false),
            ["play:1 end_turn"] = new([End(CombatStatus.InProgress, 0.7, 3, CombatAction.EndTurn)], 1, false),
            ["play:1 end_turn end_turn"] = new([End(CombatStatus.Won, 0.9, 0, CombatAction.Play(1))], 1, false),
            ["play:2 end_turn"] = new([End(CombatStatus.Won, 0.9 - below, 0, CombatAction.Play(2))], 1, false),
        };
        var plan = SlPlanner.Search(new Budget(Beam: 2, TurnDecay: decay), [], (jobs, nodes, keep) =>
            [.. jobs.Select(job => SlPlanner.Narrow(turns[string.Join(" ", job.Path)], keep))]);

        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(pick, plan.Result.First.ToString());
        Assert.Equal(pick == "play:2" ? 0.9 - below : 0.9, plan.Value);
        Assert.Equal(plan.Value, plan.Result.Candidates[0].Value);
    }

    [Fact]
    public void TheTurnDecayIsTheBudgetsInEveryRoom()
    {
        var budget = Budget.Effort("medium") with { Elite = Budget.Effort("max"), TurnDecay = SlPlanner.PlayerTurnDecay };
        Assert.Equal(Budget.Effort("max") with { TurnDecay = SlPlanner.PlayerTurnDecay }, budget.For("Elite"));
        Assert.Equal(SlPlanner.PlayerTurnDecay, budget.For("Monster").TurnDecay);
        Assert.Equal("beam 8x300 easy 4 loss plans 3 turn decay 0.999 elite (beam 16x300)", budget.ToString());
        // Self-play's budgets have none.
        Assert.All(Budget.Efforts, level => Assert.Equal(1, Budget.Effort(level).TurnDecay));
    }

    /// <summary>A leaf that values each potion held below an empty slot while the combat goes on, as a combat net can; a
    /// win is priced with free disposal, as the combat net's are, and is not.</summary>
    private sealed class Disliking : ICombatLeaf
    {
        public LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history)
        {
            var outcome = Leaf.Outcome(status, obs, run, history);
            return status != CombatStatus.InProgress ? outcome : outcome with { Value = outcome.Value - 0.01 * obs.Potions.Count(p => p != null) };
        }
    }

    [Fact]
    public void APotionThatChangesNothingIsNotDrunk()
    {
        // The toy's potion does nothing, and the leaf values every state holding it lower: drinking it for nothing would
        // win each turn's end. It is thrown away for nothing as the game allows, so the end that keeps it is worth as
        // much, and comes first.
        var world = new Toy(potion: "NOTHING");
        var found = new SlPlanner(new Disliking()).SearchTurn(world, [], 100_000, int.MaxValue);
        var kept = found.Ends.Where(e => e.Status == CombatStatus.InProgress && !e.Actions.Any(a => a.Kind == CombatActionKind.UsePotion)).ToList();
        var drunk = found.Ends.Where(e => e.Status == CombatStatus.InProgress && e.Actions.Any(a => a.Kind == CombatActionKind.UsePotion)).ToList();
        Assert.NotEmpty(drunk);
        Assert.All(drunk, d => Assert.Contains(kept, k => k.Value == d.Value && k.Actions.SequenceEqual(d.Actions.Where(a => a.Kind != CombatActionKind.UsePotion))));
        var plan = new SlPlanner(new Disliking()).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 1000));
        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.DoesNotContain(plan.Result.Plan.Prepend(plan.Result.First), a => a.Kind == CombatActionKind.UsePotion);
    }

    [Fact]
    public void AWinningCombatIsPlannedOnce() =>
        Assert.Equal((1, CombatStatus.Won), PlayRoute(new Budget(NodesPerWorld: 100_000, Beam: 1000, LossPlans: 1), enemyHp: 24));

    [Fact]
    public void EachEffortIsAPlannerBudget()
    {
        Assert.Equal(new Budget(NodesPerWorld: 300, Beam: 8, Replan: 1), Budget.Effort("high"));
        Assert.All(Budget.Efforts, e => Assert.True(Budget.Effort(e).Beam > 0));
        Assert.Equal("beam 8x300 easy 4 loss plans 3", Budget.Effort("medium").ToString());
        Assert.Throws<ArgumentException>(() => Budget.Effort("ultra"));
    }

    [Fact]
    public void AProvedLossIsPlayedToTheEnd()
    {
        var world = new Toy(enemyHp: 500);
        var plan = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 1000));

        Assert.True(plan.Proved);
        Assert.Equal(CombatStatus.Lost, plan.End);
        Assert.Contains(plan.Result.Plan, a => a.Kind == CombatActionKind.EndTurn);
        AssertReplays(plan, enemyHp: 500);
    }

    [Fact]
    public void ItsCandidatesStartWithItsPick()
    {
        var world = new Toy();
        var plan = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 100_000, Beam: 1000));
        Assert.Equal(plan.Result.First.ToString(), plan.Result.Candidates[0].Action);
        Assert.Equal(plan.Value, plan.Result.Candidates[0].Value, 9);
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
}
