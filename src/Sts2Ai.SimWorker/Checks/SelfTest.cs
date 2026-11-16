using System.CommandLine;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

/// <summary>
/// Checks the properties that search and training rely on, over an encounter pool:
///  - determinism: the same setup and decisions give the same observations, step for step, and the same trace;
///  - restore: rebuilding a snapshot by replay gives exactly the observation recorded at that point;
///  - determinize: resampling hidden information leaves the observation unchanged, replays exactly, and the
///    combat plays on from there;
///  - build: a combat built from a recorded observation shows exactly that observation, uids included, restores
///    by replay, and plays on; also numbers kept inside a model (a worn Glass orb, a spent Silver Crucible, Sloth's
///    count of cards played) and the card Nightmare keeps;
///  - rule fidelity: Rolling Boulder hits several enemies one at a time;
///  - worlds: a search world restores exactly after a prompt, also when a relic asks at the start of each turn; the
///    SL world of a run's combat shows the run's observation, plays on as the run did, and restores;
///  - SL seal: a node's clone stops at the node's exit and refuses to step past it, and the SL world refuses to step
///    past the end of its combat;
///  - SL plan: the planner's plan of a run's combat holds in the real run, step for step, to the end it planned (and,
///    with --workers N, the pooled planner plans the same);
///  - speed patches: the pooled hook listener iterators yield what the game's own iterators yield;
///  - protocol: observations and actions survive a round trip through their wire form (the brain's and harness's
///    assemblies reaching no game assembly is a unit test, BoundaryTests);
///  - worker pool (with --workers N): PIMC over worlds searched in serve processes picks what it picks here, with the
///    same values.
/// The searches value their leaves with --combat-net's net. The checks are in the files next to this one: of combats,
/// of rules, and of runs and their SL worlds.
/// </summary>
internal static partial class SelfTest
{
    private static readonly Option<int> N = Opt.Of("--n", 20, "combats");

    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base");

    private static readonly Option<int> Workers = Opt.Of("--workers", 0, "also checks a pool of this many worker processes");

    private static readonly PoolArgs Pool = new();

    private static readonly LeafArgs Leaf = new();

    public static Command Command()
    {
        var command = Opt.Command("selftest", "checks determinism, restore and determinize over an encounter pool", [N, Seed, Workers],
            Pool.Options, Leaf.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        int n = r.GetValue(N);
        int seed = r.GetValue(Seed);
        var combats = Pool.Read(r);
        var net = Leaf.Read(r);
        if (net.Net == null) throw new CliException("selftest searches with the combat net: --combat-net FILE");
        var (act, rooms, pool) = (combats.Act, combats.Rooms, combats.Encounters);
        var results = new SortedDictionary<string, (int Pass, int Fail)>();
        int drawCompared = 0, drawResampled = 0, drawMovedFromTrue = 0;

        void Check(string name, bool ok, string detail)
        {
            var (pass, fail) = results.GetValueOrDefault(name);
            results[name] = ok ? (pass + 1, fail) : (pass, fail + 1);
            if (!ok) Console.WriteLine($"  FAIL {name}: {detail}");
        }

        Console.WriteLine($"selftest: {n} combats, act {act} ({rooms}: {pool.Count} encounters), game {HeadlessGame.GameVersion}");
        for (int i = 0; i < n; i++)
        {
            var setup = combats.Setup(i, GameSeeds.MakeSeed(seed, i));
            var encounter = setup.Encounter;
            string label = $"#{i} {encounter.Id.Entry} {setup.Seed}";
            try
            {
                var first = Record(pump, setup, seed * 1_000_003 + i);
                var second = Record(pump, setup, seed * 1_000_003 + i);
                Check("determinism", first.Observations.SequenceEqual(second.Observations) && first.Hidden.SequenceEqual(second.Hidden) && first.Trace == second.Trace, label);
                Check("protocol round trip", first.ProtocolErrors.Count == 0, $"{label}: {first.ProtocolErrors.FirstOrDefault()}");

                var cutRng = new Random(seed ^ (i * 7_919));
                foreach (int k in Enumerable.Range(0, 3).Select(_ => cutRng.Next(first.Snapshots.Count)).Distinct())
                {
                    var snapshot = first.Snapshots[k];
                    string at = $"{label} at step {k}";
                    bool prompted;
                    using (var restored = CombatSession.Restore(pump, snapshot, new RandomChoices(new Random(0))))
                    {
                        Check("restore", Json(restored) == first.Observations[k], at);
                        Check("restore hidden state", Hidden(restored) == first.Hidden[k], at);
                        Check("fast hook listeners match the game's", SameListeners(restored), at);
                        prompted = restored.Prompt != null;
                    }
                    // A card prompt stands in the middle of an action: there the hidden state is not sampled again, and
                    // no combat is built to show it.
                    if (prompted) continue;

                    string trueOrder;
                    using (var restored = CombatSession.Restore(pump, snapshot, new RandomChoices(new Random(0))))
                        trueOrder = DrawOrder(restored);
                    var (obsA, orderA) = DeterminizeAt(pump, snapshot, 11);
                    var (obsB, orderB) = DeterminizeAt(pump, snapshot, 22);
                    Check("determinize keeps observation", obsA == first.Observations[k] && obsB == first.Observations[k], at);
                    if (DistinctCards(trueOrder) >= 2)
                    {
                        drawCompared++;
                        if (orderA != orderB) drawResampled++;
                        if (orderA != trueOrder) drawMovedFromTrue++;
                    }

                    var determinized = snapshot with { Trace = [.. snapshot.Trace, new TraceEvent(TraceKind.Determinize, Seed: 11)] };
                    using (var replayed = CombatSession.Restore(pump, determinized, new RandomChoices(new Random(k))))
                    {
                        Check("determinize replays", DrawOrder(replayed) == orderA, at);
                        replayed.PlayOut(new PlayPolicy(new Random(k)));
                        Check("plays on after determinize", replayed.ActionsExecuted >= snapshot.Trace.Count(e => e.Kind == TraceKind.Action), at);
                    }

                    BuildFrom(pump, ProtocolJson.Deserialize<CombatObservation>(first.Observations[k]), GameSeeds.MakeSeed(seed + 1, i * 3 + k), k, at, Check);
                }
            }
            catch (Exception e)
            {
                Check("no exceptions", false, $"{label}: {e.GetType().Name}: {Logs.FirstLine(e.Message)}");
                Logs.Sim.LogDebug(e, "{Label} failed", label);
            }
            var errors = HeadlessGame.TakeErrors();
            Check("no game errors", errors.Count == 0, $"{label}: {errors.FirstOrDefault()}");
        }

        BuildsInternalNumbers(pump, pool, act, Check);
        BuildsAtLowHp(pump, pool, act, Check);
        BuildsAGrownEruption(pump, Check);
        RollingBoulder(pump, pool, act, Check);
        ThornsKillDuringSlyPlay(pump, pool, act, Check);
        HookChoiceAtATurnsStart(pump, pool, act, Check);
        FightCapLoses(pump, pool, act, Check);
        DropsAtAnEnemysPrompt(pump, Check);
        DropsAtATurnEndsPrompt(pump, Check);
        TransformKillsBeforeTheNextTransform(pump, Check);
        GatheredCardState(pump, pool, act, Check);
        FirstArrowComesWithAShootCard(pump, pool, act, Check);
        BuildsAFoolsTurn(pump, pool, act, Check);
        BuildsARegentsTurn(pump, Check);
        DrawnMovesGoOn(pump, Check);
        LoadoutPotionSlots(pump, pool, act, Check);
        FoulPotionAtMerchants(pump, Check);
        WorldRestoresAfterPrompts(pump, pool, act, Check);
        CyclesKillInOneTurn(pump, pool, act, net.Leaf, Check);
        NodeWorldPlaysAsTheRun(pump, seed, Check);
        NodeWorldStandsAtTheEntrysPrompt(pump, seed, Check);
        CloneStopsAtTheExit(pump, seed, Check);
        CloneReplaysADiscardInCombat(pump, seed, Check);
        CloneKeepsRelicState(pump, seed, Check);
        ChestRelicIsThePlayers(pump, seed, Check);
        HistoryPlaysAreTheTraces(pump, seed, Check);
        AutoWinFightsElitesAndBosses(pump, seed, Check);
        ProbeAnswersTheRuledFightsPrompt(pump, seed, Check);
        DeathsOutsideTheEnemysTurnEndTheirCombat(pump, seed, Check);
        int workers = r.GetValue(Workers);
        PlanHoldsInTheRun(pump, seed, workers, net, Check);
        if (workers > 0) PooledMatches(pump, combats, seed, workers, net, Check);

        Console.WriteLine($"draw order resampled by a different determinize seed: {drawResampled}/{drawCompared}; differs from the true order: {drawMovedFromTrue}/{drawCompared}");
        foreach (var (name, (pass, fail)) in results)
            Console.WriteLine($"  {(fail == 0 ? "ok  " : "FAIL")} {name}: {pass} passed, {fail} failed");
        return results.Values.Any(r => r.Fail > 0) ? 1 : 0;
    }
}
