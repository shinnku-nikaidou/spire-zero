using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests;

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

        public Toy(int enemyHp = 24)
        {
            _enemyHp = enemyHp;
            Reset();
        }

        private void Reset()
        {
            (_hp, _enemy, _block, _energy, _round, _drawn) = (20, _enemyHp, 0, 2, 1, 0);
            _hand = [];
            Draw();
        }

        private void Draw()
        {
            for (int i = 0; i < 3; i++, _drawn++) _hand.Add(((uint)_drawn + 10, Deck[_drawn % Deck.Length]));
        }

        private int Attack => Attacks[(_round - 1) % Attacks.Length];

        public CombatObservation Observe() => Start with
        {
            Round = _round,
            Player = Start.Player with { Hp = Math.Max(0, _hp), MaxHp = 20, Block = _block, Energy = _energy },
            Hand = _hand.Select(c => new CardView { Id = c.Id, Uid = c.Uid }).ToList(),
            Draw = [],
            Discard = [],
            Potions = [null, null, null],
            Enemies = [Start.Enemies[0] with { Uid = 1, Hp = Math.Max(0, _enemy), MaxHp = _enemyHp, Block = 0, Powers = [],
                Intents = [new IntentView { Type = "Attack", Damage = Attack, Hits = 1 }] }],
        };

        public IReadOnlyList<CombatAction> Legal() => Status != CombatStatus.InProgress ? [] :
            [CombatAction.EndTurn, .. _energy > 0 ? _hand.Select(c => CombatAction.Play(c.Uid, c.Id == "S" ? 1u : null)) : []];

        public CombatStatus Status => _hp <= 0 ? CombatStatus.Lost : _enemy <= 0 ? CombatStatus.Won : CombatStatus.InProgress;

        public ulong HiddenKey() => (ulong)_drawn;

        public void Execute(CombatAction action)
        {
            if (!Legal().Contains(action)) throw new InvalidOperationException($"{action} is not legal");
            _steps.Add(action.ToString());
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

        public WorldSnapshot Snapshot() => new(_steps.ToList());

        public void Restore(WorldSnapshot snapshot)
        {
            _steps.Clear();
            Reset();
            foreach (var step in snapshot.Steps) Execute(CombatAction.Parse(step));
        }
    }

    private static readonly HandLeaf Leaf = new();

    /// <summary>The best end of the combat by trying every line; each state once, as the toy shows all of it but the draw.</summary>
    private static double BruteForce(Toy world, Dictionary<(ulong, ulong), double> known)
    {
        if (world.Status != CombatStatus.InProgress) return Leaf.Value(world.Status, world.Observe(), []);
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
        Assert.Equal(plan.Value, Leaf.Value(world.Status, world.Observe(), []), 9);
    }

    [Fact]
    public void AFullSearchFindsTheBestLine()
    {
        var world = new Toy();
        double best = BruteForce(world, []);
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 1000));

        Assert.True(plan.Proved);
        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(best, plan.Value, 9);
        AssertReplays(plan);
    }

    [Fact]
    public void ANarrowBeamProvesNothingAndStillPlaysItsLine()
    {
        var world = new Toy();
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 4, Beam: 1));

        Assert.False(plan.Proved);
        AssertReplays(plan);
    }

    [Fact]
    public void ALosingPlanIsPlayedForOneTurn()
    {
        var world = new Toy(enemyHp: 500);
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 2));

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
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 2, Replan: replan));

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
                return new SlPlanner(Leaf).Plan(real.Observe(), world, b);
            });
            Assert.False(result.Replanned);
            real.Execute(result.First);
        }
        return (plans, real.Status);
    }

    [Fact]
    public void AfterItsLossPlansALosingCombatIsPlayedToItsEnd()
    {
        var unlimited = PlayRoute(new Budget(NodesPerWorld: 100_000, Beam: 2), enemyHp: 500);
        var limited = PlayRoute(new Budget(NodesPerWorld: 100_000, Beam: 2, LossPlans: 2), enemyHp: 500);

        Assert.Equal(CombatStatus.Lost, unlimited.End);
        Assert.True(unlimited.Plans > 2);
        Assert.Equal((2, CombatStatus.Lost), limited);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(1, 0.5)]
    [InlineData(3, 0.7)]
    [InlineData(1000, 0.75)]
    public void ATurnSearchedInFullNarrowsToTheSearchWithAKeepAndAFloor(int keep, double? floor)
    {
        var planner = new SlPlanner(Leaf);
        foreach (var path in new[] { Array.Empty<CombatAction>(), [CombatAction.EndTurn] })
        {
            TurnSearch Searched(int k, double? f)
            {
                var world = new Toy();
                foreach (var a in path) world.Execute(a);
                return planner.SearchTurn(world, [], 200, k, f);
            }
            var direct = Searched(keep, floor);
            var narrowed = SlPlanner.Narrow(Searched(int.MaxValue, null), keep, floor);
            Assert.Equal((direct.Nodes, direct.Cut, direct.Bound), (narrowed.Nodes, narrowed.Cut, narrowed.Bound));
            Assert.Equal(direct.Ends.Select(e => (string.Join(" ", e.Actions), e.Status, e.Value, e.Key)),
                narrowed.Ends.Select(e => (string.Join(" ", e.Actions), e.Status, e.Value, e.Key)));
        }
    }

    [Fact]
    public void TheFullBeamTakesTheDivesTurnsInsteadOfSearchingThemAgain()
    {
        var planner = new SlPlanner(Leaf);
        var searched = new List<string>();
        var plan = SlPlanner.Search(new Budget(NodesPerWorld: 100_000, Beam: 4), [], (jobs, nodes, keep, floor) => jobs.Select(job =>
        {
            searched.Add(string.Join(" ", job.Path));
            var world = new Toy();
            foreach (var a in job.Path) world.Execute(a);
            return planner.SearchTurn(world, job.History, nodes, keep, floor);
        }).ToList());

        Assert.Single(searched, "");
        Assert.Equal(searched.Count, searched.Distinct().Count());
        Assert.Equal(new SlPlanner(Leaf).Plan(new Toy().Observe(), new Toy(), new Budget(NodesPerWorld: 100_000, Beam: 4)).Value, plan.Value, 9);
        AssertReplays(plan);
    }

    [Fact]
    public void WhenTheDiveWinsTheFullBeamKeepsTheEasyBeam()
    {
        var world = new Toy();
        var easy = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 1000, EasyBeam: 2));
        var narrow = new SlPlanner(Leaf).Plan(world.Observe(), new Toy(), new Budget(NodesPerWorld: 100_000, Beam: 2));

        Assert.Equal(CombatStatus.Won, easy.End);
        Assert.Equal((narrow.Value, narrow.Nodes, narrow.Turns), (easy.Value, easy.Nodes, easy.Turns));
        Assert.Equal(narrow.Result.Plan, easy.Result.Plan);
    }

    [Fact]
    public void WhenTheDiveLosesTheFullBeamKeepsTheBeam()
    {
        var losing = new SlPlanner(Leaf).Plan(new Toy(enemyHp: 500).Observe(), new Toy(enemyHp: 500), new Budget(NodesPerWorld: 100_000, Beam: 1000, EasyBeam: 2));
        var full = new SlPlanner(Leaf).Plan(new Toy(enemyHp: 500).Observe(), new Toy(enemyHp: 500), new Budget(NodesPerWorld: 100_000, Beam: 1000));

        Assert.Equal((full.Value, full.Nodes, full.Turns, full.Proved), (losing.Value, losing.Nodes, losing.Turns, losing.Proved));
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
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 1000));

        Assert.True(plan.Proved);
        Assert.Equal(CombatStatus.Lost, plan.End);
        Assert.True(plan.Result.Plan.Count(a => a.Kind == CombatActionKind.EndTurn) >= 1);
        AssertReplays(plan, enemyHp: 500);
    }

    [Fact]
    public void ItsCandidatesStartWithItsPick()
    {
        var world = new Toy();
        var plan = new SlPlanner(Leaf).Plan(world.Observe(), world, new Budget(NodesPerWorld: 100_000, Beam: 1000));
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
