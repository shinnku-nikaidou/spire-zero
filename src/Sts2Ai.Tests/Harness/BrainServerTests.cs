using System.IO.Pipes;
using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Harness;

public class BrainServerTests
{
    private sealed class Fixed : IMacroPolicy, IChoicePolicy
    {
        public MacroAnswer Choose(MacroDecision decision) => new(decision.Legal[0]);
        public ChoiceAnswer Choose(ChoiceDecision decision) => new([]);
    }

    [Fact]
    public void ARunWithoutPreviewsIsTheDeckOfPlainCardsInTheirKeysOrder()
    {
        var a = new CardView { Id = "A" };
        var b = new CardView { Id = "B" };
        var run = Fixtures.Floor1() with { Deck = [b, a with { Upgraded = a with { Up = 1 } }] };

        Assert.Equal([a, b], run.WithoutPreviews().Deck);
        Assert.All(Fixtures.Floor1().WithoutPreviews().Deck, c => Assert.Null(c.Upgraded));
    }

    [Fact]
    public void TheCombatBrainGetsTheLiveCombatTheHostSends()
    {
        var seen = new List<CombatExport?>();
        var combat = new CombatRoute("test", (decision, _, _, _) =>
        {
            seen.Add(decision.Export);
            return new(CombatAction.EndTurn, [], []);
        });
        var macro = new Fixed();
        var server = new BrainServer(hello => (new Dispatcher(hello.Run.RunId, PlayEnvironment.NoSL, combat, new("l0", macro), new("l0", macro)), "test"));

        using var toBrain = new AnonymousPipeServerStream(PipeDirection.Out);
        using var brainIn = new AnonymousPipeClientStream(PipeDirection.In, toBrain.ClientSafePipeHandle);
        using var toHost = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostIn = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
        var serving = new Thread(() => server.Serve(new WireStream(brainIn, toHost)));
        serving.Start();
        var host = new WireStream(hostIn, toBrain);

        var run = Fixtures.Floor1() with { Deck = [new CardView { Id = "B" }, new CardView { Id = "A", Upgraded = new CardView { Id = "A", Up = 1 } }] };
        var obs = new Observation { Run = run, Screen = new CombatScreen { Combat = Fixtures.Slugs() } };
        string hash = DecisionRequest.HashOf(obs);
        byte[] bytes = [1, 2, 3, 250];
        host.Send(new Hello("v-test", "NoSL", "worker", new RunHeader("run-1", "SILENT", 0)));
        host.Expect<Ready>();
        host.Send(new DecisionRequest(0, hash, Surfaces.Combat, obs, [CombatAction.EndTurn.ToString()], Convert.ToBase64String(bytes)));
        Assert.Equal(CombatAction.EndTurn.ToString(), host.Expect<Recommendation>().Action);
        host.Send(new DecisionRequest(1, hash, Surfaces.Combat, obs, [CombatAction.EndTurn.ToString()]));
        host.Expect<Recommendation>();
        toBrain.Dispose();
        serving.Join();

        Assert.Equal(bytes, seen[0]!.Bytes);
        // The run around it as a combat's leaf reads it, as a host in the brain's process gives it.
        Assert.Equal(ProtocolJson.Serialize(run.WithoutPreviews()), ProtocolJson.Serialize(seen[0]!.Run));
        Assert.Equal(["A", "B"], seen[0]!.Run.Deck.Select(c => c.Id));
        Assert.Null(seen[1]);
    }

    [Fact]
    public void EachAnswerHandsOverItsRunsLogAsItGrows()
    {
        var combat = new CombatRoute("test", (_, _, _, _) => new(CombatAction.EndTurn, [], []));
        var macro = new Fixed();
        var logged = new List<int>();
        var server = new BrainServer(hello => (new Dispatcher(hello.Run.RunId, PlayEnvironment.NoSL, combat, new("l0", macro), new("l0", macro), log: true), "test"),
            answered: run => logged.Add(run.Log.Count));

        using var toBrain = new AnonymousPipeServerStream(PipeDirection.Out);
        using var brainIn = new AnonymousPipeClientStream(PipeDirection.In, toBrain.ClientSafePipeHandle);
        using var toHost = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostIn = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
        var serving = new Thread(() => server.Serve(new WireStream(brainIn, toHost)));
        serving.Start();
        var host = new WireStream(hostIn, toBrain);

        var obs = new Observation { Run = Fixtures.Floor1(), Screen = new CombatScreen { Combat = Fixtures.Slugs() } };
        string hash = DecisionRequest.HashOf(obs);
        host.Send(new Hello("v-test", "NoSL", "worker", new RunHeader("run-1", "SILENT", 0)));
        host.Expect<Ready>();
        for (int id = 0; id < 3; id++)
        {
            host.Send(new DecisionRequest(id, hash, Surfaces.Combat, obs, [CombatAction.EndTurn.ToString()]));
            host.Expect<Recommendation>();
        }
        // A decision answered with an error logs nothing and hands nothing over.
        host.Send(new DecisionRequest(3, "not the hash", Surfaces.Combat, obs, [CombatAction.EndTurn.ToString()]));
        host.Expect<WireError>();
        toBrain.Dispose();
        serving.Join();

        Assert.Equal([1, 2, 3], logged);
    }

    [Fact]
    public void AQueryIsAskedOnARunOfItsOwnAndLeavesTheHostsRunAsItWas()
    {
        // Each decision answered with its last legal action, scored; the harnesses made, by hello.
        var combat = new CombatRoute("test", (decision, _, _, _) => new(decision.Legal[^1], [], []));
        var macro = new Fixed();
        var made = new List<string>();
        var server = new BrainServer(hello =>
        {
            made.Add(hello.Effort ?? "none");
            return (new Dispatcher(hello.Run.RunId, PlayEnvironment.NoSL, combat, new("l0", macro), new("l0", macro), log: true), "test");
        }, values: env => run => env == "NoSL" ? run.Hp / 100.0 : throw new NotSupportedException("no SL here"));

        using var toBrain = new AnonymousPipeServerStream(PipeDirection.Out);
        using var brainIn = new AnonymousPipeClientStream(PipeDirection.In, toBrain.ClientSafePipeHandle);
        using var toHost = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostIn = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
        var serving = new Thread(() => server.Serve(new WireStream(brainIn, toHost)));
        serving.Start();
        var host = new WireStream(hostIn, toBrain);

        var obs = new Observation { Run = Fixtures.Floor1(), Screen = new CombatScreen { Combat = Fixtures.Slugs() } };
        string hash = DecisionRequest.HashOf(obs);
        string end = CombatAction.EndTurn.ToString(), potion = CombatAction.Potion(0, null).ToString();
        var hello = new Hello("v-test", "NoSL", "mod", new RunHeader("run-1", "SILENT", 0), "medium");
        host.Send(hello);
        host.Expect<Ready>();
        var decision = new DecisionRequest(0, hash, Surfaces.Combat, obs, [end, potion]);
        host.Send(decision);
        Assert.Equal(potion, host.Expect<Recommendation>().Action);
        // The same decision with fewer options, on a run of its own at another effort.
        host.Send(new Query(1, hello with { Effort = "max" }, null, decision with { Legal = [end] }));
        var answer = host.Expect<QueryAnswer>();
        Assert.Equal(1, answer.Id);
        Assert.Equal(end, answer.Answer!.Action);
        // A query that fails is answered with its error, and the host's run goes on.
        host.Send(new Query(2, hello, null, decision with { ObsHash = "not the hash" }));
        var failed = host.Expect<QueryAnswer>();
        Assert.Null(failed.Answer);
        Assert.NotNull(failed.Error);
        host.Send(new DecisionRequest(1, hash, Surfaces.Combat, obs, [end, potion]));
        Assert.Equal(potion, host.Expect<Recommendation>().Action);
        // V of runs, one value each, in their order; an environment without V answers with its error.
        host.Send(new ValueQuery(3, "NoSL", [obs.Run, obs.Run with { Hp = 7 }]));
        Assert.Equal([obs.Run.Hp / 100.0, 0.07], host.Expect<ValueAnswer>().Values!);
        host.Send(new ValueQuery(4, "SL", [obs.Run]));
        Assert.Null(host.Expect<ValueAnswer>().Values);
        toBrain.Dispose();
        serving.Join();

        Assert.Equal(["medium", "max", "medium"], made);
    }

    /// <summary>Answers each decision with its first legal action, at a state numbered in order from 1, and keeps the
    /// states it hears were played.</summary>
    private sealed class Numbered : IMacroPolicy, IChoicePolicy
    {
        private ulong _states;

        public List<ulong> Heard { get; } = [];

        public MacroAnswer Choose(MacroDecision decision) => new(decision.Legal[0], State: ++_states);

        public ChoiceAnswer Choose(ChoiceDecision decision) => new([]);

        public void Played(ulong state) => Heard.Add(state);
    }

    [Fact]
    public void TheMacroSideHearsOfEachAnswerTheHostSaysItPlayed()
    {
        var combat = new CombatRoute("test", (_, _, _, _) => new(CombatAction.EndTurn, [], []));
        var macro = new Numbered();
        var server = new BrainServer(hello => (new Dispatcher(hello.Run.RunId, PlayEnvironment.NoSL, combat, new("l0", macro), new("l0", macro)), "test"));

        using var toBrain = new AnonymousPipeServerStream(PipeDirection.Out);
        using var brainIn = new AnonymousPipeClientStream(PipeDirection.In, toBrain.ClientSafePipeHandle);
        using var toHost = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostIn = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
        var serving = new Thread(() => server.Serve(new WireStream(brainIn, toHost)));
        serving.Start();
        var host = new WireStream(hostIn, toBrain);

        var obs = new Observation { Run = Fixtures.Floor1(), Screen = new RewardsScreen { Rewards = [] } };
        string hash = DecisionRequest.HashOf(obs);
        string Ask(long id)
        {
            host.Send(new DecisionRequest(id, hash, Surfaces.Rewards, obs, [MacroAction.RewardsDone.ToString()]));
            return host.Expect<Recommendation>().Action;
        }
        host.Send(new Hello("v-test", "NoSL", "mod", new RunHeader("run-1", "SILENT", 0)));
        host.Expect<Ready>();
        host.Send(new Executed(0, Ask(0), true));
        // An action that failed was not played, nor an answer the player acted instead of (decision 2).
        host.Send(new Executed(1, Ask(1), false));
        Ask(2);
        host.Send(new Executed(3, Ask(3), true));
        host.Send(new Executed(0, "rewards_done", true));
        // A new run forgets the last run's answers.
        host.Send(new Hello("v-test", "NoSL", "mod", new RunHeader("run-2", "SILENT", 0)));
        host.Expect<Ready>();
        host.Send(new Executed(2, "rewards_done", true));
        // Messages are read in order: once this is answered, every Executed before it was handled.
        Ask(0);
        toBrain.Dispose();
        serving.Join();

        Assert.Equal([1UL, 4UL], macro.Heard);
    }
}
