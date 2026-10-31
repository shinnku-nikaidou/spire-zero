using System.CommandLine;
using System.Text.RegularExpressions;
using Sts2Ai.Brain;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// fidelity (docs: simulator/combat §6): how far a NoSL world built from an observation plays like the real combat, apart
/// from what Determinize samples. At every k-th decision of the panel's combats, the real combat plays on with the
/// placeholder policy to the end of the turn, the enemies' turn included; then a combat built from the decision's
/// observation and the record of the enemies' intents (<see cref="CombatBuilder"/>), given the real one's sampled state
/// (<see cref="CombatSession.Mirror"/>), plays the same actions. The first observation that differs, uids left out,
/// shows what the build approximates: this turn's history, counters inside powers and monsters, how long a cost change
/// lasts. --no-history builds without the record, as before the monsters' move logs were built from it.
/// </summary>
internal static partial class Fidelity
{
    private static readonly Option<int> N = Opt.Of("--n", 20, "combats");
    private static readonly Option<int> Start = Opt.Of("--start", 0, "the first combat's index");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "the seed base");
    private static readonly Option<int> Every = Opt.Of("--every", 3, "measure every k-th decision of a combat");
    private static readonly Option<string> Player = Opt.Of("--player", "play", "the placeholder policy that plays the combats")
        .AcceptOnlyFromAmong(Policies.CombatNames);
    private static readonly Option<int> Top = Opt.Of("--top", 15, "first differences listed");
    private static readonly Option<bool> NoHistory = Opt.Of("--no-history", false, "build without the record of the enemies' intents");
    private static readonly PoolArgs Pool = new();

    public static Command Command()
    {
        var command = Opt.Command("fidelity", "how far a world built from an observation plays like the real combat (NoSL build fidelity)",
            [N, Start, Seed, Every, Player, Top, NoHistory], Pool.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private sealed class Tally
    {
        public int Measured, BuiltWrong, Exact, InTurn, AtTurnEnd, Illegal;
        public readonly Dictionary<string, int> Paths = [];
    }

    private static int Run(ParseResult r, Pump pump)
    {
        var pool = Pool.Read(r);
        int n = r.GetValue(N), start = r.GetValue(Start), seed = r.GetValue(Seed), every = Math.Max(1, r.GetValue(Every));
        var tally = new Tally();
        Console.WriteLine($"fidelity: {n} combats from #{start}, act {pool.Act} ({pool.Rooms}), player {r.GetValue(Player)}, every {every} decisions, " +
                          $"game {HeadlessGame.GameVersion}");
        for (int i = start; i < start + n; i++)
        {
            var setup = pool.Setup(i, GameSeeds.MakeSeed(seed, i));
            var policy = Policies.Combat(r.GetValue(Player)!, new Random(seed * 1_000_003 + i));
            var moves = new MoveRecord();
            CombatSession? real = CombatSession.Start(pump, setup, PromptBrain.Instance);
            try
            {
                for (int d = 0; !real.IsCapped; d++)
                {
                    moves.Witness(real.Observation());
                    if (d % every == 0)
                        real = Measure(pump, real, r.GetValue(NoHistory) ? null : moves.History, tally, seed * 1_000_003 + i * 1000 + d);
                    var live = real;
                    real.Execute(policy.Choose(new CombatDecision(live.LegalActions(), live.Observation)));
                }
            }
            finally
            {
                real?.Dispose();
            }
        }
        double share(int x) => tally.Measured == 0 ? 0 : x / (double)tally.Measured;
        Console.WriteLine($"{tally.Measured} decisions: built wrong {tally.BuiltWrong}; the same through the enemies' turn {tally.Exact} ({share(tally.Exact):P1}); " +
                          $"first difference within the turn {tally.InTurn} ({share(tally.InTurn):P1}), after ending it {tally.AtTurnEnd} ({share(tally.AtTurnEnd):P1}); " +
                          $"an action illegal in the built combat {tally.Illegal}");
        Console.WriteLine("first differences, by path:");
        foreach (var (path, count) in tally.Paths.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(r.GetValue(Top)))
            Console.WriteLine($"  {count,5}  {path}");
        return 0;
    }

    /// <summary>Measures one decision; the real combat is played ahead and put back, so it returns the restored one.</summary>
    private static CombatSession Measure(Pump pump, CombatSession real, IReadOnlyList<EnemyTurns>? history, Tally tally, int seed)
    {
        var obs = real.Observation();
        var hidden = real.Hidden();
        var snapshot = real.Snapshot();
        var look = new PlayPolicy(new Random(seed));
        var steps = new List<(CombatAction Action, int Hand, CombatObservation After)>();
        while (!real.IsOver && steps.Count < 12)
        {
            var now = real.Observation();
            var action = look.Choose(new CombatDecision(real.LegalActions(), () => now));
            int hand = action.Kind == CombatActionKind.PlayCard ? now.Hand.ToList().FindIndex(c => c.Uid == action.Card) : -1;
            real.Execute(action);
            steps.Add((action, hand, real.Observation()));
            if (action.Kind == CombatActionKind.EndTurn) break;
        }
        real.Dispose();

        tally.Measured++;
        using (var built = CombatSession.Start(pump, CombatBuilder.Setup(obs, GameSeeds.MakeSeed(seed, 0), history), PromptBrain.Instance))
        {
            if (built.BuildDifferences.Count > 0)
                tally.BuiltWrong++;
            else
                Compare(built, hidden, steps, tally);
        }
        return CombatSession.Restore(pump, snapshot, PromptBrain.Instance);
    }

    private static void Compare(CombatSession built, HiddenDraw hidden, List<(CombatAction Action, int Hand, CombatObservation After)> steps, Tally tally)
    {
        built.Mirror(hidden);
        foreach (var (action, hand, after) in steps)
        {
            // The same card by its place in the hand: cards drawn later have other uids in a built combat.
            var played = hand < 0 ? action : action with { Card = built.Observation().Hand[hand].Uid };
            if (!built.LegalActions().Contains(played))
            {
                tally.Illegal++;
                return;
            }
            built.Execute(played);
            var differences = CombatBuilder.Differences(Stripped(after), Stripped(built.Observation()));
            if (differences.Count == 0) continue;
            if (action.Kind == CombatActionKind.EndTurn) tally.AtTurnEnd++;
            else tally.InTurn++;
            foreach (var path in differences.Select(Path).Distinct()) tally.Paths[path] = tally.Paths.GetValueOrDefault(path) + 1;
            return;
        }
        tally.Exact++;
    }

    private static CombatObservation Stripped(CombatObservation obs) => obs with
    {
        Player = obs.Player with { Uid = null },
        Hand = obs.Hand.Select(c => c with { Uid = null }).ToList(),
        Allies = obs.Allies.Select(c => c with { Uid = null }).ToList(),
        Enemies = obs.Enemies.Select(c => c with { Uid = null }).ToList(),
    };

    /// <summary>A difference's path without its values and positions: ".enemies[ AEONGLASS].powers[ STRENGTH_POWER].amount".</summary>
    private static string Path(string difference) => Index().Replace(difference[..difference.IndexOf(": want", StringComparison.Ordinal)], "[");

    [GeneratedRegex(@"\[\d+ ?")]
    private static partial Regex Index();
}
