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
    /// is exhausted instead and draws the next of that many R in the draw pile, as a Soul does. With
    /// <paramref name="returns"/>, R comes back only that many times, as Feral returns free attacks, and the player's power
    /// shows how many are left. The hand starts with <paramref name="copies"/> R.
    /// </summary>
    private sealed class Loop(int enemyHp, int junkEvery = 0, int handCap = 0, int runsOut = int.MaxValue, Loop.Prompted prompted = Loop.Prompted.No, int souls = 0, int copies = 1, int? returns = null) : ICombatWorld
    {
        /// <summary>A potion that offers three cards, one to take: the last gives R, or a strong one-off card; or it asks
        /// twice, and only the last card of the second prompt gives the strong card.</summary>
        public enum Prompted { No, GivesR, GivesStrike, AsksTwice }

        private static readonly CardChoice Ask = new()
        {
            Options = [new CardView { Id = "A" }, new CardView { Id = "B" }, new CardView { Id = "C" }],
            Min = 1,
            Max = 1,
            Context = new() { Purpose = "choose_a_card_screen", Cancelable = false, InCombat = true },
        };

        private static readonly CombatObservation Start = Fixtures.Slugs();

        private readonly List<string> _steps = [];
        private List<(uint Uid, string Id)> _hand = Hand(prompted, copies);
        private List<string> _discard = [];
        private readonly int _maxHp = enemyHp;
        private int _enemy = enemyHp, _hp = 30, _energy = 1, _strength, _round = 1, _plays, _drawn, _returns = returns ?? 0;
        private bool _potion = prompted != Prompted.No;
        /// <summary>The prompts the potion still asks: the world waits on the first of them.</summary>
        private int _asking;

        /// <summary>The actions played, not counting the ones a restore plays again.</summary>
        public int Played { get; private set; }

        public RunView Run() => Fixtures.Around(Observe());

        public CombatObservation Observe() => Start with
        {
            Round = _round,
            Player = Start.Player with { Hp = Math.Max(0, _hp), MaxHp = 30, Block = 0, Energy = _energy, Powers = [.. _strength > 0 ? [new PowerView("STRENGTH_POWER", _strength)] : Array.Empty<PowerView>(),
                .. returns != null ? [new PowerView("FERAL_POWER", _returns)] : Array.Empty<PowerView>()] },
            Hand = [.. _hand.Select(c => new CardView { Id = c.Id, Uid = c.Uid, Cost = c.Id == "H" ? 1 : 0 })],
            Draw = [.. Enumerable.Repeat(new CardView { Id = "R" }, souls - _drawn)],
            Discard = [.. _discard.Select(id => new CardView { Id = id })],
            Potions = [_potion ? "P" : null, null, null],
            Enemies = [Start.Enemies[0] with { Uid = 1, Hp = Math.Max(0, _enemy), MaxHp = _maxHp, Block = 0, Powers = [],
                Intents = [new IntentView { Type = "Attack", Damage = 10, Hits = 1 }] }],
            // The second prompt of two is another prompt: its source tells them apart.
            Prompt = _asking > 0 ? Ask with { Context = Ask.Context with { Source = $"P{_asking}" } } : null,
        };

        public IReadOnlyList<CombatAction> Legal() => Status != CombatStatus.InProgress || _asking > 0 ? [] :
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
            if (!Observe().Takes(action, Legal())) throw new InvalidOperationException($"{action} is not legal");
            _steps.Add(action.ToString());
            if (action.Kind == CombatActionKind.Pick)
            {
                // Only the last prompt's last card gives anything.
                if (--_asking == 0 && action.Picks is [2]) _hand.Add(prompted == Prompted.GivesR ? (1u, "R") : (3u, "S"));
                return;
            }
            if (action.Kind == CombatActionKind.EndTurn)
            {
                _hp -= 10;
                (_energy, _strength, _round) = (1, 0, _round + 1);
                return;
            }
            if (action.Kind == CombatActionKind.UsePotion)
            {
                _potion = false;
                _asking = prompted == Prompted.AsksTwice ? 2 : 1;
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
                    else if ((handCap == 0 || _hand.Count < handCap) && _plays < runsOut && (returns == null || _returns > 0))
                    {
                        _hand.Add(card);
                        if (returns != null) _returns--;
                    }
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
            _hand = Hand(prompted, copies);
            _discard = [];
            (_enemy, _hp, _energy, _strength, _round, _plays, _drawn, _potion, _asking, _returns) = (_maxHp, 30, 1, 0, 1, 0, 0, prompted != Prompted.No, 0, returns ?? 0);
            foreach (var step in snapshot.Steps) Apply(CombatAction.Parse(step));
        }
    }

    private static readonly ICombatLeaf Leaf = new ToyLeaf();
    private static readonly CombatAction R = CombatAction.Play(1), H = CombatAction.Play(2, 1);

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
        var world = make();
        var first = PimcBrain.Ordered(world.Legal(), world.Observe());
        var budget = new Budget(Worlds: worlds, NodesPerWorld: 100);
        return PimcBrain.Root(first, budget, (f, nodes) => [.. Enumerable.Range(0, worlds).Select(_ => new PimcBrain(Leaf).SearchWorld(make(), 1, f, nodes))]);
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
    public void EveryAnswerToAPromptIsSearchedByPimc()
    {
        // The third answer wins at once; the first loses the fight.
        static Loop make() => new(50, prompted: Loop.Prompted.GivesStrike);
        var pimc = Pimc(2, make);
        Assert.Contains(CombatAction.Pick([2]), pimc.Plan.Prepend(pimc.First));
    }

    [Fact]
    public void EveryPromptOfAnActionIsSearchedByPimc()
    {
        // The potion asks twice, and only the third answer to its second prompt wins.
        static Loop make() => new(50, prompted: Loop.Prompted.AsksTwice);
        var pimc = Pimc(2, make);
        Assert.Equal(CombatStatus.Won, Replayed(pimc, make).Status);
    }

    [Fact]
    public void AnUnlimitedBudgetTakesEveryAnswerOfAPrompt()
    {
        // A prompt of three answers: with no limit on the nodes, all three are tried, nothing cut.
        var world = new Loop(50, prompted: Loop.Prompted.AsksTwice);
        world.Execute(CombatAction.Potion(0));
        Assert.Equal(3, PromptAnswers.Of(world.Observe().Prompt!, SlPlanner.Unlimited, out bool cut).Count);
        Assert.False(cut);
    }

    [Fact]
    public void AStateAtAPromptHasNoPlaysAndTakesOnlyItsAnswers()
    {
        var world = new Loop(50, prompted: Loop.Prompted.GivesR);
        world.Execute(CombatAction.Potion(0));
        var obs = world.Observe();

        Assert.NotNull(obs.Prompt);
        Assert.Empty(world.Legal());
        Assert.True(obs.Takes(CombatAction.Pick([1]), world.Legal()));
        Assert.False(obs.Takes(CombatAction.Pick([0, 1]), world.Legal()));
        Assert.False(obs.Takes(CombatAction.EndTurn, [CombatAction.EndTurn]));
        // And where no prompt waits, no answer is taken.
        Assert.False(Fixtures.Slugs().Takes(CombatAction.Pick([]), [CombatAction.EndTurn]));
        Assert.Equal([CombatAction.Pick([0]), CombatAction.Pick([1]), CombatAction.Pick([2])], PimcBrain.Actions(world, obs, 10, out bool cut));
        Assert.False(cut);
        // A prompt has to be answered: with no node left, its first answer is still tried.
        Assert.Equal([CombatAction.Pick([0])], PimcBrain.Actions(world, obs, 0, out cut));
        Assert.True(cut);
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
        Assert.Equal([CombatAction.Potion(0), CombatAction.Pick([2])], result.Plan.Prepend(result.First).Take(2));
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

    private static ChoiceDecision Prompt(int min = 0, int max = 1, params string[] ids) => new(new CardChoice
    {
        Options = [.. (ids.Length > 0 ? ids : ["A", "B", "C"]).Select(id => new CardView { Id = id })],
        Min = min,
        Max = max,
        Context = new() { Purpose = "hand", Cancelable = false, InCombat = true },
    });

    [Fact]
    public void TheAnswersAPlanStartsWithAreGivenWhenTheirPromptsComeEvenIfThePlanIsNotKept()
    {
        var obs = Fixtures.Slugs();
        var asked = obs with { Prompt = Prompt().Choice };
        int searches = 0;
        CombatDecisionResult Search()
        {
            searches++;
            // PIMC's worlds did not agree: no hashes, so the plan is not kept.
            return new(R, [CombatAction.Pick([2]), H, CombatAction.EndTurn], []);
        }
        var plan = new TurnPlan(all: false);

        Assert.Equal(R, plan.Decide(obs, [R, H], Search).First);
        Assert.Equal(CombatAction.Pick([2]), plan.Decide(asked, [], Search).First);
        Assert.Equal(1, searches);
        // The plays after the answer were not kept.
        Assert.Equal(R, plan.Decide(obs, [R, H], Search).First);
        Assert.Equal(2, searches);

        // An answer the prompt does not take is not given, and that is no plan gone wrong.
        var narrow = obs with { Prompt = Prompt(1, 1, "A", "B").Choice };
        var searched = plan.Decide(narrow, [], () => new(CombatAction.Pick([0]), [], []));
        Assert.Equal(CombatAction.Pick([0]), searched.First);
        Assert.False(searched.Replanned);
    }

    [Fact]
    public void APromptNoSearchWeighsTakesTheFirstOptionsItNeeds()
    {
        Assert.Empty(FirstPicks.Instance.Choose(Prompt()).Picks);
        Assert.Equal([0, 1], FirstPicks.Instance.Choose(Prompt(min: 2, max: 3)).Picks);
        Assert.Equal([0], FirstPicks.Instance.Choose(Prompt(min: 2, max: 2, "A")).Picks);
        // PIMC has no world at a prompt: one its plan did not answer gets the first options.
        Assert.Equal(CombatAction.Pick([0, 1]), PimcBrain.Unplanned(Fixtures.Slugs() with { Prompt = Prompt(min: 2, max: 3).Choice })!.First);
        Assert.Null(PimcBrain.Unplanned(Fixtures.Slugs()));
    }

    [Fact]
    public void APromptsAnswersPickCardsThePlayerCanTellApart()
    {
        static List<string> Answers(ChoiceDecision prompt) => [.. prompt.Choice.Answers().Select(a => string.Join(",", a))];

        Assert.Equal(["", "0", "1", "2"], Answers(Prompt()));
        // Two copies of A are one card twice: picking one is picking the first, the fewest picks first.
        Assert.Equal(["0", "2", "0,1", "0,2"], Answers(Prompt(1, 2, "A", "A", "B")));
        // The first answer is the one nothing has to weigh, and a prompt with one answer has nothing to decide.
        Assert.Equal(FirstPicks.Of(Prompt(1, 2, "A", "A", "B").Choice), CombatAction.Pick(Prompt(1, 2, "A", "A", "B").Choice.Answers().First()));
        Assert.Equal(CombatAction.Pick([0, 1]), PimcBrain.Forced(Fixtures.Slugs() with { Prompt = Prompt(2, 2, "A", "A").Choice }, []));
        Assert.Null(PimcBrain.Forced(Fixtures.Slugs() with { Prompt = Prompt().Choice }, []));
    }

    [Fact]
    public void AnAnswerToAPromptIsACombatAction()
    {
        foreach (var action in new[] { CombatAction.Pick([2]), CombatAction.Pick([3, 0]), CombatAction.Pick([]), CombatAction.Play(5, 1), CombatAction.Potion(1), CombatAction.EndTurn })
        {
            Assert.Equal(action, CombatAction.Parse(action.ToString()));
            Assert.Equal(action, ProtocolJson.Deserialize<CombatAction>(ProtocolJson.Serialize(action)));
        }
        // One answer has one form: its picks in order.
        Assert.Equal("pick:0,3", CombatAction.Pick([3, 0]).ToString());
        Assert.Equal("pick:", CombatAction.Pick([]).ToString());
        Assert.Equal([0, 3], CombatAction.Parse("pick:0,3").Picks);
        Assert.Empty(CombatAction.Parse("pick:").Picks);
        Assert.False(CombatAction.TryParse("pick:x", out _));
        Assert.False(CombatAction.TryParse("pick:1,", out _));
        Assert.False(CombatAction.TryParse("play:1@2", out _));
    }
}
