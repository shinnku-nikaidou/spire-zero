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

    /// <summary>Plays the plan from the start, or after <paramref name="played"/>: each step at the observation it
    /// expects, to the end it claims.</summary>
    private static void AssertReplays(SlPlan plan, int enemyHp = 24, IReadOnlyList<CombatAction>? played = null, bool retain = false)
    {
        var world = new Toy(enemyHp, retain: retain);
        foreach (var action in played ?? []) world.Execute(action);
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

    /// <summary>Every state the toy's current turn can end at, by trying every line from the world's state: the next
    /// turn's states by key, and the ends of the combat by status.</summary>
    private static void TurnEnds(Toy world, int round, HashSet<(ulong, ulong)> seen, HashSet<(ulong, ulong)> next, HashSet<CombatStatus> over)
    {
        var here = world.Snapshot();
        foreach (var action in world.Legal())
        {
            world.Restore(here);
            world.Execute(action);
            var obs = world.Observe();
            var key = (ProtocolJson.StateKey(obs), world.HiddenKey());
            if (world.Status != CombatStatus.InProgress) over.Add(world.Status);
            else if (obs.Round != round) next.Add(key);
            else if (seen.Add(key)) TurnEnds(world, round, seen, next, over);
        }
        world.Restore(here);
        world.Forget(here);
    }

    [Fact]
    public void AnUnlimitedTurnSearchEndsAtEveryStateTheTurnCanReach()
    {
        var expected = new HashSet<(ulong, ulong)>();
        var over = new HashSet<CombatStatus>();
        TurnEnds(new Toy(), 1, [], expected, over);
        var narrow = new SlPlanner(Leaf).SearchTurn(new Toy(), [], 4, int.MaxValue);
        var found = new SlPlanner(Leaf).SearchTurn(new Toy(), [], SlPlanner.Unlimited, int.MaxValue);

        // Every end, each once, and nothing cut: a budget of 4 nodes reaches fewer.
        Assert.False(found.Cut);
        Assert.True(narrow.Cut);
        Assert.Empty(over);
        var reached = found.Ends.Select(e =>
        {
            var world = new Toy();
            foreach (var action in e.Actions) world.Execute(action);
            return (ProtocolJson.StateKey(world.Observe()), world.HiddenKey());
        }).ToList();
        Assert.Equal(reached.Count, reached.Distinct().Count());
        Assert.Equal(expected, reached.ToHashSet());
        Assert.True(narrow.Ends.Count < found.Ends.Count);
        Assert.Equal("beam 16xall", (Budget.Effort("max") with { NodesPerWorld = SlPlanner.Unlimited }).Size);
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
        var budget = Budget.Effort("low") with { Elite = Budget.Effort("high"), TurnDecay = SlPlanner.PlayerTurnDecay };
        Assert.Equal(Budget.Effort("high") with { TurnDecay = SlPlanner.PlayerTurnDecay }, budget.For("Elite"));
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
        Assert.Equal(new Budget(NodesPerWorld: 300, Beam: 8, Replan: 1), Budget.Effort("medium"));
        Assert.Equal(new Budget(NodesPerWorld: 300, Beam: 16, Replan: 1), Budget.Effort("high"));
        Assert.All(Budget.Efforts, e => Assert.True(Budget.Effort(e).Beam > 0));
        Assert.Equal("beam 8x300 easy 4 loss plans 3", Budget.Effort("low").ToString());
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

    /// <summary>A solver's turn searches, each in a toy of its own where the combat stands after <paramref name="played"/>.</summary>
    private static TurnSearcher ToySearcher(IReadOnlyList<CombatAction> played, int enemyHp = 24, bool retain = false) => (jobs, nodes, keep) => [.. jobs.Select(job =>
    {
        var world = new Toy(enemyHp, retain: retain);
        foreach (var action in played.Concat(job.Path)) world.Execute(action);
        return new SlPlanner(Leaf).SearchTurn(world, job.History, nodes, keep, job.LifeBounds);
    })];

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

    /// <summary>A plan's value from its search's toy after <paramref name="played"/>, by trying every line.</summary>
    private static double Best(IReadOnlyList<CombatAction> played, int enemyHp = 24, bool retain = false)
    {
        var world = new Toy(enemyHp, retain: retain);
        foreach (var action in played) world.Execute(action);
        return BruteForce(world, []);
    }

    [Fact]
    public void TheSolverSearchedToTheEndPicksTheBestLineAndProvesIt()
    {
        foreach (int enemyHp in new[] { 24, 500 })
        {
            var solver = new SlSolver([]);
            solver.Run(ToySearcher([], enemyHp), long.MaxValue);
            var plan = solver.Result(1);

            Assert.True(plan.Proved);
            Assert.Equal(0, solver.Left);
            Assert.Equal(enemyHp == 24 ? CombatStatus.Won : CombatStatus.Lost, plan.End);
            Assert.Equal(Best([], enemyHp), plan.Value, 9);
            AssertReplays(plan, enemyHp);
        }
    }

    [Fact]
    public void TheSolverStoppedAnywhereIsNoWorseThanTheBeamItStartsFrom()
    {
        var beam = new SlPlanner(Leaf).Plan(new Toy(), new Budget(NodesPerWorld: 10, Beam: 1));
        double best = Best([]);
        // The beam wins, not with the best line: 0.52 of 0.88.
        Assert.Equal(CombatStatus.Won, beam.End);
        Assert.True(beam.Value < best - 1e-9);

        var solver = new SlSolver([], batch: 1);
        solver.Seed(beam);
        double last = beam.Value;
        int stops = 0;
        for (; !solver.Proved; stops++)
        {
            solver.Run(ToySearcher([]), 10);
            var plan = solver.Result(1);
            Assert.Equal(CombatStatus.Won, plan.End);
            Assert.True(plan.Value >= last - 1e-12);
            last = plan.Value;
            AssertReplays(plan);
        }
        Assert.True(stops > 2);
        Assert.Equal(best, last, 9);
    }

    [Fact]
    public void TheSameNodesGiveTheSamePlan()
    {
        var plans = Enumerable.Range(0, 2).Select(_ =>
        {
            var solver = new SlSolver([], batch: 4);
            solver.Run(ToySearcher([]), 100);
            return (solver.Result(1), solver.Nodes, solver.States);
        }).ToList();

        Assert.Equal((plans[0].Item1.Result.First, plans[0].Item1.Value, plans[0].Nodes, plans[0].States),
            (plans[1].Item1.Result.First, plans[1].Item1.Value, plans[1].Nodes, plans[1].States));
        Assert.Equal(plans[0].Item1.Line!.Actions, plans[1].Item1.Line!.Actions);
        Assert.False(plans[0].Item1.Proved);
    }

    [Fact]
    public void TheSolversRootMovesWithTheCombat()
    {
        var solver = new SlSolver([]);
        solver.Run(ToySearcher([]), long.MaxValue);
        var plan = solver.Result(1);
        var line = plan.Line!.Actions;
        int turn = line.ToList().FindIndex(a => a.Kind == CombatActionKind.EndTurn) + 1;

        // Along the plan, into the turn and then past its end: all below was searched, so only the root's turn is again.
        foreach (int played in new[] { 1, turn })
        {
            var gone = line.Take(played).ToList();
            var moved = new SlSolver([]);
            moved.Run(ToySearcher([]), long.MaxValue);
            int searched = moved.Searched;
            moved.Advance(gone, Moves(gone), ToySearcher(gone));

            Assert.Equal(searched + 1, moved.Searched);
            Assert.True(moved.Proved);
            var after = moved.Result(1);
            Assert.Equal(plan.Value, after.Value, 9);
            Assert.Equal(Best(gone), after.Value, 9);
            AssertReplays(after, played: gone);
        }

        // Off the plan: the solver searches on from there, to the best line there.
        var other = new SlSolver([]);
        other.Run(ToySearcher([]), long.MaxValue);
        CombatAction[] off = [new Toy().Legal().First(a => a != line[0])];
        other.Advance(off, Moves(off), ToySearcher(off));
        other.Run(ToySearcher(off), long.MaxValue);
        var there = other.Result(1);
        Assert.True(there.Proved);
        Assert.Equal(Best(off), there.Value, 9);
        AssertReplays(there, played: off);
    }

    [Fact]
    public void TheSolversStatesStayOnLinesThatHoldTheSameCopiesOfACard()
    {
        // Cards left in hand stay: a turn that plays the first Strike and one that plays the second end in the same state
        // by its key, holding the other copy, under its own uid. After the second, a state its turn was searched at by a
        // line that played the first is searched again, so the plan plays the cards held.
        const int Hp = 18;
        var solver = new SlSolver([]);
        solver.Run(ToySearcher([], Hp, retain: true), long.MaxValue);
        var strikes = new Toy(Hp, retain: true).Observe().Hand.Where(c => c.Id == "S").ToList();
        Assert.Equal(2, strikes.Count);
        CombatAction[] gone = [CombatAction.Play(strikes[1].Uid!.Value, 1)];
        solver.Advance(gone, Moves(gone), ToySearcher(gone, Hp, retain: true));
        solver.Run(ToySearcher(gone, Hp, retain: true), long.MaxValue);
        var plan = solver.Result(1);

        Assert.True(plan.Proved);
        Assert.Equal(Best(gone, Hp, retain: true), plan.Value, 9);
        AssertReplays(plan, Hp, gone, retain: true);
    }

    [Fact]
    public void TheSolversSeedStaysOnlyWhereTheCombatWentItsWay()
    {
        var beam = new SlPlanner(Leaf).Plan(new Toy(), new Budget(NodesPerWorld: 10, Beam: 1));
        var line = beam.Line!.Actions;
        CombatAction[] along = [line[0]], off = [new Toy().Legal().First(a => a != line[0])];

        // Searched no further than the root's turn, the pick is the seed's where it went, and the solver's own elsewhere.
        var solver = new SlSolver([], batch: 1);
        solver.Seed(beam);
        solver.Advance(along, Moves(along), ToySearcher(along));
        Assert.Equal(line.Skip(1), solver.Result(1).Line!.Actions);

        var elsewhere = new SlSolver([], batch: 1);
        elsewhere.Seed(beam);
        elsewhere.Advance(off, Moves(off), ToySearcher(off));
        var plan = elsewhere.Result(1);
        Assert.NotEqual(line.Skip(1), plan.Line!.Actions);
        AssertReplays(plan, played: off);
    }

    /// <summary>Plays the toy's combat to its end by the solver's route (<see cref="SolverRoute"/>); at each decision,
    /// <paramref name="hand"/> may take another action than the route's. With <paramref name="background"/>, the solver
    /// searches between decisions too, and <paramref name="between"/> runs after each.</summary>
    private static (List<CombatAction> Played, SolverRoute Route) PlayBySolver(Budget budget, Func<int, CombatAction, CombatAction>? hand = null,
        bool background = false, Action<int>? between = null)
    {
        var route = new SolverRoute(budget, background);
        var world = new Toy();
        var played = new List<CombatAction>();
        for (int d = 0; world.Status == CombatStatus.InProgress; d++)
        {
            var at = played.ToList();
            var answer = route.Decide(world.Observe(), world.Legal(), Moves(played), () => ToyTaken(at),
                () => throw new InvalidOperationException("the toy is always taken in")).First;
            var taken = hand?.Invoke(d, answer) ?? answer;
            world.Execute(taken);
            played.Add(taken);
            between?.Invoke(d);
        }
        route.Rest();
        return (played, route);
    }

    /// <summary>The toy's combat taken in after <paramref name="played"/>, as a solver begins on it.</summary>
    private static TakenCombat ToyTaken(IReadOnlyList<CombatAction> played) => new((more, _) => ToySearcher([.. played, .. more]), more =>
    {
        var world = new Toy();
        foreach (var action in played.Concat(more)) world.Execute(action);
        return world.Observe();
    });

    [Fact]
    public void TheSolversRouteFollowsItsPicksWithOneSolverACombat()
    {
        var beam = new SlPlanner(Leaf).Plan(new Toy(), new Budget(NodesPerWorld: 10, Beam: 1));

        // Searched to the end at the first decision: the best line there is, played to its end.
        var full = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: long.MaxValue));
        Assert.Equal(1, full.Route.Begun);
        Assert.Equal(Best([]), Best(full.Played), 9);

        // A few nodes a decision: the same each time, one solver the combat long, never worse than the beam it began from.
        var few = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 20));
        Assert.Equal(few.Played, PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 20)).Played);
        Assert.Equal(1, few.Route.Begun);
        Assert.True(Best(few.Played) >= beam.Value - 1e-9);
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
        var beam = new SlPlanner(Leaf).Plan(new Toy(), new Budget(NodesPerWorld: 10, Beam: 1));
        // Each decision searched in full: what the route plays without the background.
        var full = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: long.MaxValue), background: true);
        Assert.Equal(1, full.Route.Begun);
        Assert.Equal(Best([]), Best(full.Played), 9);

        // A few nodes a decision, at least: one solver the combat long, never worse than the beam it began from.
        var few = PlayBySolver(new Budget(NodesPerWorld: 10, Beam: 1, Solve: 20), background: true);
        Assert.Equal(1, few.Route.Begun);
        Assert.True(Best(few.Played) >= beam.Value - 1e-9);

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
    public void ReachAddsEveryHealAndMaxHpTheStateStillHolds()
    {
        // 30 of 80 HP, nothing that heals: the HP and max HP now.
        var bare = Fixtures.Slugs() with
        {
            Player = Fixtures.Slugs().Player with { Hp = 30, MaxHp = 80, Powers = [] },
            Hand = [], Draw = [], Discard = [], Exhaust = [], Potions = [null, null, null], Relics = [],
        };
        static RelicView Relic(string id, string? status = null) => new() { Id = id, Status = status };
        static CardView Card(string id, bool exhausts = true, int? up = null) => new() { Id = id, Up = up, Keywords = exhausts ? ["Exhaust"] : null };
        Assert.Equal(110, LifeBound.Reach(bare));
        Assert.Equal(116, LifeBound.Reach(bare with { Relics = [Relic("BURNING_BLOOD")] }));
        // A heal fills the HP up to the max HP and no further.
        Assert.Equal(160, LifeBound.Reach(bare with { Player = bare.Player with { Hp = 75 }, Relics = [Relic("BLACK_BLOOD")] }));
        Assert.Equal(122, LifeBound.Reach(bare with { Relics = [Relic("MEAT_ON_THE_BONE")] }));
        Assert.Equal(111, LifeBound.Reach(bare with { Relics = [Relic("CHOSEN_CHEESE")] }));
        Assert.Equal(150, LifeBound.Reach(bare with { Relics = [Relic("LIZARD_TAIL")] }));
        Assert.Equal(110, LifeBound.Reach(bare with { Relics = [Relic("LIZARD_TAIL", "Disabled")] }));
        // A potion heals when drunk, and Regen goes on healing after.
        Assert.Equal(125, LifeBound.Reach(bare with { Potions = ["REGEN_POTION", null, null] }));
        Assert.Equal(126, LifeBound.Reach(bare with { Potions = ["BLOOD_POTION", null, null] }));
        Assert.Equal(150, LifeBound.Reach(bare with { Potions = ["AMBERGRIS", null, null] }));
        Assert.Equal(134, LifeBound.Reach(bare with { Potions = ["FAIRY_IN_A_BOTTLE", null, null] }));
        Assert.Equal(115, LifeBound.Reach(bare with { Potions = ["FRUIT_JUICE", null, null] }));
        Assert.Equal(116, LifeBound.Reach(bare with { Player = bare.Player with { Powers = [new("REGEN_POWER", 3)] } }));
        // A card that heals or gives max HP, in any pile, by its amount while it exhausts; no bound once it does not.
        Assert.Equal(120, LifeBound.Reach(bare with { Draw = [Card("NOT_YET")] }));
        Assert.Equal(123, LifeBound.Reach(bare with { Exhaust = [Card("NOT_YET", up: 1)] }));
        Assert.Equal(113, LifeBound.Reach(bare with { Discard = [Card("FEED")] }));
        Assert.Equal(int.MaxValue, LifeBound.Reach(bare with { Hand = [Card("NOT_YET", exhausts: false)] }));
        Assert.Equal(int.MaxValue, LifeBound.Reach(bare with { Hand = [Card("FEED", exhausts: false)] }));
        // A gain with no amount to count: no bound.
        Assert.Equal(int.MaxValue, LifeBound.Reach(bare with { Relics = [Relic("DRAGON_FRUIT")] }));
    }

    [Fact]
    public void ATurnsSearchStopsTheLinesBelowTheBoundOfTheirPotions()
    {
        // The toy starts at 20 of 20 and heals nothing: with a bound of 40 for no potions, only the ends that took no
        // damage stay.
        var bounds = new Dictionary<string, int> { [""] = 40 };
        var all = new SlPlanner(Leaf).SearchTurn(new Toy(), [], SlPlanner.Unlimited, int.MaxValue);
        var bound = new SlPlanner(Leaf).SearchTurn(new Toy(), [], SlPlanner.Unlimited, int.MaxValue, bounds);

        Assert.Equal(0, all.Pruned);
        Assert.True(bound.Pruned > 0);
        Assert.NotEmpty(bound.Ends);
        Assert.All(bound.Ends, e => Assert.True(e.Reach >= 40));
        Assert.Contains(all.Ends, e => e.Reach < 40);
        Assert.All(all.Ends, e => Assert.Equal(e.Life, e.Reach));

        // A state that still holds a potion is held to its own potions' bound, none here.
        var held = new SlPlanner(Leaf).SearchTurn(new Toy(potion: "SWIFT_POTION"), [], SlPlanner.Unlimited, int.MaxValue, bounds);
        Assert.Contains(held.Ends, e => e.Potions == "SWIFT_POTION" && e.Reach < 40);
        Assert.All(held.Ends, e => Assert.True(e.Potions != "" || e.Reach >= 40));

        // The solver's bound is its wins' HP plus max HP by their potions, and its pick still the best there is.
        var solver = new SlSolver([]);
        solver.Run(ToySearcher([]), long.MaxValue);
        var plan = solver.Result(1);
        Assert.True(plan.Proved);
        Assert.Equal(plan.Line!.Life, solver.Bounds[plan.Line.Potions]);
        Assert.Equal(Best([]), plan.Value, 9);
    }
}
