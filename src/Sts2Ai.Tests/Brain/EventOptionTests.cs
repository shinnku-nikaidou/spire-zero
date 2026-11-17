using Sts2Ai.Brain;
using Sts2Ai.Brain.Combat;
using Sts2Ai.Brain.Macro;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

/// <summary>NoSL's event options valued pending, and SL's lines of them played out as NoSL plays them
/// (plans/2026-11-17-0306-nosl-shared-nets.md, 3.2).</summary>
public class EventOptionTests
{
    /// <summary>A V that is the sum of what each relic is worth, a potion 0.1 each, and an option pending by its key.</summary>
    private sealed class Worth(Dictionary<string, double> worth) : IRunValue
    {
        public string Name => "test";

        public double Value(RunView run) => run.Relics.Sum(r => worth.GetValueOrDefault(r.Id)) + 0.1 * run.Potions.Count(p => p != null)
            + (run.Pending is { } option ? worth.GetValueOrDefault(option.Key) : 0);
    }

    private static readonly Worth V = new(new() { ["B"] = 3, ["C"] = 2.9, ["GOOD"] = 2, ["BAD"] = -1, ["K2"] = 1, ["K3"] = 2 });

    private static readonly RunView Start = Fixtures.Floor1() with { Relics = [], Potions = ["FIRE", null, null] };

    private static EventOptionView Option(int index, string key, string? relic = null, bool? kills = null) =>
        new() { Index = index, Key = key, Relic = relic, Kills = kills, Numbers = new Dictionary<string, decimal> { ["TOY.Gold"] = 50 } };

    private static Observation Page(params EventOptionView[] options) =>
        new() { Run = Start, Screen = new EventScreen { Id = "TOY", Options = options, Finished = false } };

    [Fact]
    public void AnOptionWhoseResultItsButtonDoesNotShowIsTheRunWithItPending()
    {
        var obs = Page(Option(0, "GOOD"), Option(1, "ANCIENT", relic: "B"), Option(2, "BAD", kills: true));
        var screen = new ScreenAfterstates(V);
        var pending = screen.After(obs, MacroAction.Parse("event:0"));
        Assert.Equal("GOOD", pending.Pending!.Key);
        Assert.Equal(50, pending.Pending.Numbers!["TOY.Gold"]);
        Assert.Equal(pending with { Pending = null }, Start);
        // a relic's option adds the relic, one that kills ends the run: neither pends
        Assert.Null(screen.After(obs, MacroAction.Parse("event:1")).Pending);
        Assert.Equal("B", screen.After(obs, MacroAction.Parse("event:1")).Relics.Single().Id);
        Assert.Equal(0, screen.After(obs, MacroAction.Parse("event:2")).Hp);
        var events = (EventScreen)obs.Screen;
        Assert.Equal([true, false, false], new[] { 0, 1, 2 }.Select(i => ScreenAfterstates.Pends(events, MacroAction.Of(MacroAction.Event, i))));
    }

    [Fact]
    public void APotionDrunkAtAnEventIsWorthTheBestPendingOptionAfterIt()
    {
        // Every option is worth less than the run as it is: valued as the run it leaves, drinking would win.
        var obs = Page(Option(0, "BAD"));
        var decision = new MacroDecision(Surfaces.Event, Fixtures.Acts("event:0", "potion:0"), () => obs);
        var brain = new MacroBrain(V);
        var options = brain.Options(decision);
        Assert.Equal(-1 + 0.1, options[0].Value, 9);
        Assert.Equal(-1, options[1].Value, 9);
        Assert.Equal("event:0", brain.Choose(decision).Action.ToString());
    }

    /// <summary>A node's clone of a toy event: its first page has option 0 (pending K1), which leads to a page whose
    /// event:0 (pending K2) gives relic B and event:1 (pending K3) relic C, then the map; option 1 starts a fight; option
    /// 2 ends the run.</summary>
    private sealed class Toy : IMacroClone
    {
        public IReadOnlyList<Afterstate> After(IReadOnlyList<IReadOnlyList<MacroAction>> paths) => [.. paths.Select(p => Play([.. p.Select(a => a.ToString())]))];

        private static Afterstate Play(IReadOnlyList<string> path) => path switch
        {
            [] => new(Start, Surfaces.Event, Page(Option(0, "K1"), Option(1, "FIGHT"), Option(2, "DEATH")).Screen, Fixtures.Acts("event:0", "event:1", "event:2")),
            ["event:0"] => new(Start, Surfaces.Event, Page(Option(0, "K2"), Option(1, "K3")).Screen, Fixtures.Acts("event:0", "event:1")),
            ["event:0", "event:0"] => new(Start with { Relics = [new RelicView { Id = "B" }] }, Surfaces.Map, null, []),
            ["event:0", "event:1"] => new(Start with { Relics = [new RelicView { Id = "C" }] }, Surfaces.Map, null, []),
            ["event:1"] => new(Start, Surfaces.Combat, new CombatScreen { Combat = null! }, []),
            ["event:2"] => new(Start with { Hp = 0 }, null, null, [], Won: false),
            _ => throw new InvalidOperationException(string.Join(" ", path)),
        };
    }

    [Fact]
    public void AnEventsOptionsArePlayedOutAsNoSlPlaysThemToWhereTheRoomIsLeft()
    {
        var toy = new Toy();
        var root = toy.After([[]])[0];
        var lines = new EventLines(V, toy, new HashSet<ulong>()).All(root, root.Legal);
        Assert.Equal(["K1", "FIGHT", "DEATH"], lines.Select(l => l.Option.Key));
        // NoSL takes K3 (2 pending) over K2 (1): C, though the clone's true results make K2's B the better
        Assert.Equal(("left", "C"), (lines[0].Ending, lines[0].End!.Relics.Single().Id));
        Assert.Equal(("fight", null), (lines[1].Ending, lines[1].End));
        Assert.Equal(("died", 0), (lines[2].Ending, lines[2].End!.Hp));
    }

    /// <summary>A combat leaf that values every fight at nothing.</summary>
    private sealed class Even : ICombatLeaf
    {
        public LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history) => new(0, 0, 0);
    }

    [Fact]
    public void SlLogsTheLinesOfAnEventsOptionsOnlyWhenAskedTo()
    {
        var toy = new Toy();
        var root = toy.After([[]])[0];
        MacroDecision Decision() => new(root.Surface!, root.Legal, () => new Observation { Run = root.Run, Screen = root.Screen! }, toy);
        Assert.Null(new MacroBrain(V, fights: new Even()).Choose(Decision()).Event);
        var answer = new MacroBrain(V, fights: new Even(), eventLines: V).Choose(Decision());
        Assert.Equal(3, answer.Event!.Count);
    }
}
