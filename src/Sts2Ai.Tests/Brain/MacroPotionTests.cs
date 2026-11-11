using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

/// <summary>The potion belt at the macro brain's decisions: what a discard is offered for, and what a potion's option on the map is worth.</summary>
public class MacroPotionTests
{
    /// <summary>A V that rates each potion held at <paramref name="potion"/>, and a run on the map that stays where it is at <paramref name="staying"/>.</summary>
    private sealed class Belt(double potion, double staying = 0) : IRunValue
    {
        public string Name => "test";
        public double Value(RunView run) => potion * run.Potions.Count(p => p != null) + (run.Map.Entering == true ? 0 : staying);
    }

    private static RunView Holding(params string?[] potions) => Fixtures.Floor1() with { Potions = potions };

    private static MacroDecision Rewards(RunView run, string reward) => new(Surfaces.Rewards, Fixtures.Acts("reward:0", "rewards_done", "discard_potion:0"),
        () => new Observation
        {
            Run = run,
            Screen = new RewardsScreen { Rewards = [new RewardView { Index = 0, Kind = reward, Id = reward == "potion" ? "FIRE_POTION" : null, Amount = 10 }] },
        });

    [Fact]
    public void ADiscardIsOfferedOnlyToMakeRoomForAPotionOnTheScreen()
    {
        // V rates a potion below an empty slot, so a discard offered would be taken.
        var l0 = new MacroBrain(new Belt(-1));
        Assert.DoesNotContain(l0.Options(Rewards(Holding("A", null, null), "potion")), o => o.Action.ToString() == "discard_potion:0");
        Assert.DoesNotContain(l0.Options(Rewards(Holding("A", "B", "C"), "gold")), o => o.Action.ToString() == "discard_potion:0");
        Assert.NotEqual("discard_potion:0", l0.Choose(Rewards(Holding("A", null, null), "potion")).Action.ToString());
        Assert.Contains(l0.Options(Rewards(Holding("A", "B", "C"), "potion")), o => o.Action.ToString() == "discard_potion:0");

        var shop = new MacroDecision(Surfaces.Shop, Fixtures.Acts("buy:0", "leave", "discard_potion:0"), () => new Observation
        {
            Run = Holding("A", "B", "C"),
            Screen = new ShopScreen { Entries = [new() { Slot = 0, Kind = "potion", Id = "FIRE_POTION", Cost = 50 }] },
        });
        Assert.Contains(l0.Options(shop), o => o.Action.ToString() == "discard_potion:0");
    }

    private static readonly IReadOnlyList<MacroAction> MapLegal = Fixtures.Acts("map:3,1", "map:4,1", "potion:0");

    private static MacroDecision Map(RunView run, IMacroClone? clone = null) => new(Surfaces.Map, MapLegal,
        () => new Observation { Run = run, Screen = new MapScreen { Travelable = [new(3, 1), new(4, 1)] } }, clone);

    /// <summary>The map's clone: drinking the potion leaves the run on the map without it.</summary>
    private sealed class Drink : IMacroClone
    {
        public IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<MacroAction>> paths) => [.. paths.Select(path =>
        {
            var run = Holding("A", null, null);
            if (path.SequenceEqual(Fixtures.Acts("potion:0"))) run = run with { Potions = [null, null, null] };
            return new Afterstate(run, Surfaces.Map, new MapScreen { Travelable = [new(3, 1), new(4, 1)] }, path.Count == 0 ? MapLegal : Fixtures.Acts("map:3,1", "map:4,1"));
        })];
    }

    [Fact]
    public void OnTheMapAPotionsOptionIsWorthTheBestMoveAfterItNotTheStateItStaysIn()
    {
        // V rates staying on the map 5 above entering any point, and a potion at 0.1: drinking for nothing would win if
        // the drink were valued as the state it stays in.
        var l0 = new MacroBrain(new Belt(0.1, staying: 5));
        foreach (var decision in new[] { Map(Holding("A", null, null)), Map(Holding("A", null, null), new Drink()) })
        {
            var options = l0.Options(decision);
            Assert.Equal(0, options.Single(o => o.Action.ToString() == "potion:0").Value, 9);
            Assert.Equal(0.1, options.Single(o => o.Action.ToString() == "map:3,1").Value, 9);
            Assert.Equal(MacroAction.Map, l0.Choose(decision).Action.Verb);
        }
    }
}
