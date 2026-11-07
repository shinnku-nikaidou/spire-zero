using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class LoopSearchTests
{
    /// <summary>
    /// A turn with a card that comes back: R (free) gives 1 strength for the turn and returns to the hand, H (1 energy)
    /// deals 6 plus the strength. The enemies' turn hits for 10. Every <paramref name="junkEvery"/> plays a junk card comes,
    /// to the discard pile, or to the hand if <paramref name="handCap"/> is set, and then a full hand sends R to the discard
    /// pile, which ends the cycle. <paramref name="runsOut"/> ends it after that many plays. With <paramref name="souls"/>, R
    /// is exhausted instead and draws the next of that many R in the draw pile, as a Soul does. The hand starts with
    /// <paramref name="copies"/> R.
    /// </summary>
    private sealed class Loop(int enemyHp, int junkEvery = 0, int handCap = 0, int runsOut = int.MaxValue, Loop.Prompted prompted = Loop.Prompted.No, int souls = 0, int copies = 1) : ICombatWorld
    {
        /// <summary>A potion that offers three cards, with answer 0 the policy's; the last gives R, or a strong one-off card.</summary>
        public enum Prompted { No, GivesR, GivesStrike }

        private static readonly CombatObservation Start = Fixtures.Slugs();

        private readonly List<string> _steps = [];
        private List<(uint Uid, string Id)> _hand = Hand(prompted, copies);
        private List<string> _discard = [];
        private readonly int _maxHp = enemyHp;
        private int _enemy = enemyHp, _hp = 30, _energy = 1, _strength, _round = 1, _plays, _drawn;
        private bool _potion = prompted != Prompted.No;

        /// <summary>The actions played, not counting the ones a restore plays again.</summary>
        public int Played { get; private set; }

        public PromptSeen? LastPrompt { get; private set; }

        public RunView Run() => Fixtures.Around(Observe());

        public CombatObservation Observe() => Start with
        {
            Round = _round,
            Player = Start.Player with { Hp = Math.Max(0, _hp), MaxHp = 30, Block = 0, Energy = _energy, Powers = _strength > 0 ? [new PowerView("STRENGTH_POWER", _strength)] : [] },
            Hand = [.. _hand.Select(c => new CardView { Id = c.Id, Uid = c.Uid, Cost = c.Id == "H" ? 1 : 0 })],
            Draw = [.. Enumerable.Repeat(new CardView { Id = "R" }, souls - _drawn)],
            Discard = [.. _discard.Select(id => new CardView { Id = id })],
            Potions = [_potion ? "P" : null, null, null],
            Enemies = [Start.Enemies[0] with { Uid = 1, Hp = Math.Max(0, _enemy), MaxHp = _maxHp, Block = 0, Powers = [],
                Intents = [new IntentView { Type = "Attack", Damage = 10, Hits = 1 }] }],
        };

        public IReadOnlyList<CombatAction> Legal() => Status != CombatStatus.InProgress ? [] :
        [
            CombatAction.EndTurn,
            .. _hand.Where(c => c.Id is "R" or "S" || c.Id == "H" && _energy > 0).Select(c => CombatAction.Play(c.Uid, c.Id == "R" ? null : 1u)),
            .. _potion ? [CombatAction.Potion(0)] : Array.Empty<CombatAction>(),
        ];

        public CombatStatus Status => _hp <= 0 ? CombatStatus.Lost : _enemy <= 0 ? CombatStatus.Won : CombatStatus.InProgress;

        public ulong HiddenKey() => 0;

        public void Execute(CombatAction action)
        {
            Played++;
            Apply(action);
        }

        private void Apply(CombatAction action)
        {
            if (!Legal().Contains(action.Plain)) throw new InvalidOperationException($"{action} is not legal");
            _steps.Add(action.ToString());
            LastPrompt = null;
            if (action.Kind == CombatActionKind.EndTurn)
            {
                _hp -= 10;
                (_energy, _strength, _round) = (1, 0, _round + 1);
                return;
            }
            if (action.Kind == CombatActionKind.UsePotion)
            {
                _potion = false;
                LastPrompt = new(3, 0);
                if (action.Pick == 2) _hand.Add(prompted == Prompted.GivesR ? (1u, "R") : (3u, "S"));
                return;
            }
            var card = _hand.First(c => c.Uid == action.Card);
            _hand.Remove(card);
            _plays++;
            if (junkEvery > 0 && _plays % junkEvery == 0)
            {
                if (handCap == 0) _discard.Add("J");
                else if (_hand.Count < handCap) _hand.Add((100 + (uint)_plays, "J"));
            }
            switch (card.Id)
            {
                case "R":
                    _strength++;
                    if (souls > 0)
                    {
                        if (_drawn < souls) _hand.Add((200 + (uint)_drawn++, "R"));
                    }
                    else if ((handCap == 0 || _hand.Count < handCap) && _plays < runsOut) _hand.Add(card);
                    else _discard.Add("R");
                    break;
                case "H":
                    _enemy -= 6 + _strength;
                    _energy--;
                    _discard.Add("H");
                    break;
                default:
                    _enemy -= 100;
                    break;
            }
        }

        private static List<(uint Uid, string Id)> Hand(Prompted prompted, int copies) =>
            prompted == Prompted.No ? [(1, "R"), .. Enumerable.Range(1, copies - 1).Select(i => (10u + (uint)i, "R")), (2, "H")] : [(2, "H")];

        public WorldSnapshot Snapshot() => new([.. _steps]);

        public void Restore(WorldSnapshot snapshot)
        {
            _steps.Clear();
            _hand = Hand(prompted, copies);
            _discard = [];
            (_enemy, _hp, _energy, _strength, _round, _plays, _drawn, _potion) = (_maxHp, 30, 1, 0, 1, 0, 0, prompted != Prompted.No);
            foreach (var step in snapshot.Steps) Apply(CombatAction.Parse(step));
        }
    }

    private static readonly ICombatLeaf Leaf = new ToyLeaf();
    private static readonly CombatAction R = CombatAction.Play(1), H = CombatAction.Play(2, 1);

    private static SlPlan Plan(Loop world) => new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 300, Beam: 8));

    private static List<CombatAction> Line(SlPlan plan) => [plan.Result.First, .. plan.Result.Plan];

    /// <summary>Plays the line on a new world, each step at the observation it expects.</summary>
    private static Loop Replayed(CombatDecisionResult result, Func<Loop> make)
    {
        var world = make();
        List<CombatAction> steps = [result.First, .. result.Plan];
        for (int i = 0; i < steps.Count; i++)
        {
            if (i > 0) Assert.Equal(result.PlanHashes![i - 1], ProtocolJson.Hash(world.Observe()));
            world.Execute(steps[i]);
        }
        return world;
    }

    private static CombatDecisionResult Pimc(int worlds, Func<Loop> make)
    {
        var first = PimcBrain.Ordered(make().Legal());
        var budget = new Budget(Worlds: worlds, NodesPerWorld: 100);
        return PimcBrain.Root(first, budget, (f, nodes) => [.. Enumerable.Range(0, worlds).Select(_ => new PimcBrain(Leaf).SearchWorld(make(), 1, f, nodes))]);
    }

    [Fact]
    public void TheCycleIsRepeatedUntilItKillsInOneTurn()
    {
        // 44 strength and H: 50 damage. A plain search spends its 300 nodes on R and never gets to H.
        var plan = Plan(new Loop(50));

        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(1, plan.Turns);
        Assert.Equal([.. Enumerable.Repeat(R, 44), H], Line(plan));
        Assert.Equal(CombatStatus.Won, Replayed(plan.Result, () => new Loop(50)).Status);
    }

    [Fact]
    public void WhatTheCycleDoesToThePilesDoesNotStopIt()
    {
        var plan = Plan(new Loop(50, junkEvery: 6));

        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(1, plan.Turns);
        Assert.Equal(44, Line(plan).Count(a => a == R));
    }

    [Fact]
    public void ACycleEndsWhereTheGameEndsIt()
    {
        // A full hand sends R to the discard pile: 4 junk in a hand of 6 is 12 plays. Lethal before that, and not after it.
        var plan = Plan(new Loop(15, junkEvery: 3, handCap: 6));
        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(1, plan.Turns);

        var far = new Loop(10_000, junkEvery: 3, handCap: 6);
        var line = Line(Plan(far));
        int end = line.IndexOf(CombatAction.EndTurn);
        Assert.Contains(H, line.Take(end));
        Assert.Equal(15, line.Take(end).Count(a => a == R));
    }

    [Fact]
    public void ACardThatRunsOutIsAnOrdinaryLine()
    {
        // R stops coming back after its third play, too soon to be taken for a cycle, and the search plays it out as always.
        var plan = Plan(new Loop(9, runsOut: 3));

        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(1, plan.Turns);
        Assert.Equal([R, R, R, H], Line(plan));
    }

    [Fact]
    public void CardsThatUseThemselvesUpAreAnOrdinaryLine()
    {
        // Each R is exhausted and draws the next of 8, as a Soul does: one fewer is left each time, so it is no cycle.
        var cycle = new Loop(15);
        var souls = new Loop(15, souls: 8);
        var (before, soulsBefore) = (cycle.Observe(), souls.Observe());
        cycle.Execute(R);
        souls.Execute(R);
        Assert.True(LoopSearch.Recurs(before, cycle.Observe()));
        Assert.False(LoopSearch.Recurs(soulsBefore, souls.Observe()));

        // The search plays the nine R and H out on its nodes.
        var plan = Plan(new Loop(15, souls: 8));
        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.Equal(1, plan.Turns);
    }

    [Fact]
    public void CopiesInTheHandDoNotMakeACycle()
    {
        // The first R comes back once, as Music Box copies the first attack of a turn; three more R in the hand would let
        // the probe repeat it, but the hand is not back after the second play.
        var world = new Loop(1_000, runsOut: 2, copies: 4);
        var start = world.Observe();
        List<LoopPoint> path = [new(start, 0, world.Snapshot())];
        world.Execute(R);
        var loops = new LoopSearch(world, 1, (_, _, _, _) => throw new InvalidOperationException("no line ends in a probe"));

        Assert.True(LoopSearch.Recurs(start, world.Observe()));
        Assert.False(loops.Intercept(path, world.Observe(), [R], [ProtocolJson.Hash(start)]));
        Assert.False(loops.Found);
    }

    [Fact]
    public void ACycleWithNoLethalStaysOnItsBudget()
    {
        var world = new Loop(1_000_000);
        var plan = new SlPlanner(Leaf).Plan(world, new Budget(NodesPerWorld: 300, Beam: 1));

        Assert.NotEqual(CombatStatus.Won, plan.End);
        Assert.InRange(world.Played, 1, plan.Turns * (2 * LoopSearch.Moves + 400));
    }

    [Fact]
    public void AnotherAnswerToAPromptIsTakenIfItOpensACycle()
    {
        static Loop make() => new(50, prompted: Loop.Prompted.GivesR);
        var plan = Plan(make());

        // Plan.Turns would count the next turn it looked at too, for a win that keeps the potion.
        Assert.Equal(CombatStatus.Won, plan.End);
        Assert.DoesNotContain(CombatAction.EndTurn, Line(plan));
        Assert.Equal(CombatAction.Potion(0) with { Pick = 2 }, plan.Result.First);
        Assert.Equal(CombatStatus.Won, Replayed(plan.Result, make).Status);
    }

    [Fact]
    public void AnotherAnswerToAPromptIsLeftIfItOpensNoCycle()
    {
        // The third answer wins at once, but search does not look for better answers in general: the prompt policy gives them.
        var line = Line(Plan(new Loop(50, prompted: Loop.Prompted.GivesStrike)));

        Assert.All(line, a => Assert.Null(a.Pick));
    }

    [Fact]
    public void PimcRepeatsTheCycleAsWell()
    {
        static Loop make() => new(50);
        var result = Pimc(2, make);

        Assert.True(result.Repeats);
        Assert.Equal(R, result.First);
        Assert.Equal(44, result.Plan.Count(a => a == R) + 1);
        Assert.Equal(CombatStatus.Won, Replayed(result, make).Status);
    }

    [Fact]
    public void PimcTakesAnotherAnswerThatOpensACycle()
    {
        static Loop make() => new(50, prompted: Loop.Prompted.GivesR);
        var result = Pimc(2, make);

        Assert.True(result.Repeats);
        Assert.Equal(CombatAction.Potion(0) with { Pick = 2 }, result.First);
        Assert.Equal(CombatStatus.Won, Replayed(result, make).Status);
    }

    [Fact]
    public void PimcKeepsOnlyAPlanThatRepeats()
    {
        var world = new Loop(50);
        var repeating = new TurnPlan(all: false);
        int searches = 0;
        CombatDecisionResult Search()
        {
            searches++;
            return Pimc(2, () => new Loop(50));
        }
        for (int i = 0; world.Status == CombatStatus.InProgress; i++)
            world.Execute(repeating.Decide(world.Observe(), world.Legal(), Search).First);

        Assert.Equal(CombatStatus.Won, world.Status);
        Assert.Equal(1, searches);

        var ordinary = new TurnPlan(all: false);
        var obs = Fixtures.Slugs();
        CombatDecisionResult Plain()
        {
            searches++;
            return new(CombatAction.EndTurn, [CombatAction.EndTurn], [], [ProtocolJson.Hash(obs)]);
        }
        ordinary.Decide(obs, [CombatAction.EndTurn], Plain);
        ordinary.Decide(obs, [CombatAction.EndTurn], Plain);
        Assert.Equal(3, searches);
    }

    private sealed class Answers(params int[] answer) : IChoicePolicy
    {
        public IReadOnlyList<int> Choose(ChoiceDecision decision) => answer;
    }

    private static ChoiceDecision Prompt(string? source = null, string purpose = "hand", int max = 1) => new(new CardChoice
    {
        Options = [new CardView { Id = "A" }, new CardView { Id = "B" }, new CardView { Id = "C" }],
        Min = 0,
        Max = max,
        Context = new() { Purpose = purpose, Source = source, Cancelable = false, InCombat = true },
    });

    [Fact]
    public void APromptIsAnsweredByThePolicyUnlessTheActionCarriesAnAnswer()
    {
        var prompts = new SearchPrompts(new Answers(1));

        prompts.Arm(null);
        Assert.Equal([1], prompts.Choose(Prompt("TOUCH_OF_INSANITY")));
        Assert.Equal(new PromptSeen(3, 1), prompts.Seen);

        prompts.Arm(2);
        Assert.Equal([2], prompts.Choose(Prompt("TOUCH_OF_INSANITY")));
        // Its first prompt only, and what the first offered is what stays noted.
        Assert.Equal([1], prompts.Choose(Prompt("OTHER", max: 2)));
        Assert.Equal(new PromptSeen(3, 1), prompts.Seen);

        prompts.Arm(9);
        Assert.Equal([1], prompts.Choose(Prompt("TOUCH_OF_INSANITY")));
    }

    [Fact]
    public void APromptThatTakesAwayOrAsksForMoreIsNotAChoiceForCycles()
    {
        var prompts = new SearchPrompts(new Answers(1));

        foreach (var prompt in new[] { Prompt(purpose: "hand_for_discard"), Prompt("THINKING_AHEAD"), Prompt(max: 2) })
        {
            prompts.Arm(null);
            prompts.Choose(prompt);
            Assert.Null(prompts.Seen);
        }
    }

    [Fact]
    public void APickIsPartOfAnActionsText()
    {
        foreach (var action in new[] { CombatAction.Play(5, 1) with { Pick = 2 }, CombatAction.Play(5) with { Pick = 0 }, CombatAction.Potion(1) with { Pick = 3 } })
        {
            Assert.Equal(action, CombatAction.Parse(action.ToString()));
            Assert.Equal(action, ProtocolJson.Deserialize<CombatAction>(ProtocolJson.Serialize(action)));
            Assert.Null(action.Plain.Pick);
        }
        Assert.Equal("potion:0:1@2", (CombatAction.Potion(0, 1) with { Pick = 2 }).ToString());
        Assert.False(CombatAction.TryParse("end_turn@1", out _));
        Assert.False(CombatAction.TryParse("play:1@x", out _));
    }
}
