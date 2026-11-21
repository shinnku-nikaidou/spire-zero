using System.CommandLine;
using Microsoft.Extensions.Logging;
using MegaCrit.Sts2.Core.Random;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker.Brains;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// rollout: the rest of a run, N times, from a room's clone (its checkpoint and the steps since, as the mod's record has
/// them), by the champion: the clone is loaded open, the room played on as it was rolled, and from the next map point
/// on every seeded stream is drawn anew at each point entered (<see cref="RunSession.Redraw"/>), so each rollout is
/// another of the run's possible futures. The mod's console runs it (ai rollout) and reads its lines: progress FLOOR HP
/// MAXHP as each rollout reaches a new floor, then rollout I won|lost|truncated|failed FLOOR HP.
/// </summary>
internal static class RolloutCommand
{
    private static readonly Option<string> Node = Opt.Required<string>("--node", "the room's clone as JSON: {save, trace}");
    private static readonly Option<int> N = Opt.Of("--n", 8, "rollouts");
    private static readonly Option<int> Seed = Opt.Of("--seed", 1, "what the redrawn streams are drawn from");
    private static readonly Option<string> Character = Opt.Of("--character", "SILENT", "the run's character");
    private static readonly Option<int> Asc = Opt.Of("--asc", 0, "the run's ascension");
    private static readonly Option<PlayEnvironment> Env = Opt.Of("--env", PlayEnvironment.SL, "the environment");
    private static readonly Option<int> MaxDecisions = Opt.Of("--max-decisions", 20_000, "a rollout stops here, truncated");
    private static readonly ChampionArgs Champion = new(file: true);

    public static Command Command()
    {
        var command = Opt.Command("rollout", "the rest of a run N times from a room's clone, chance drawn anew from the next map point on",
            [Node, N, Seed, Character, Asc, Env, MaxDecisions], Champion.Options);
        command.SetAction(Cli.Game(Run));
        return command;
    }

    private static int Run(ParseResult r, Pump pump)
    {
        var clone = ProtocolJson.Deserialize<NodeClone>(File.ReadAllText(r.GetValue(Node)!));
        int n = r.GetValue(N), seed = r.GetValue(Seed), asc = r.GetValue(Asc), maxDecisions = r.GetValue(MaxDecisions);
        var env = r.GetValue(Env);
        string character = r.GetValue(Character)!;
        var champion = Champion.Read(r);
        if (champion.Policy == "plan" && env != PlayEnvironment.SL) throw new CliException("plan plays in SL only (--env SL)");
        using var pool = champion.Pool();
        var harness = champion.Harness(pool, log: false);
        Console.WriteLine($"rollout: {n} from a clone of {clone.Trace.Count} steps in {env}, {champion}");
        for (int i = 0; i < n; i++)
        {
            string id = $"rollout:{seed}:{i}";
            var hello = new Hello(HeadlessGame.GameVersion, env.ToString(), "worker", new RunHeader(RunHeader.IdOf(id), character, asc));
            var brain = new LocalBrain(harness(hello));
            var export = brain.TakesNodes ? new NodeExport(brain) : null;
            IRunBrain player = export ?? (IRunBrain)brain;
            RunSession? session = null;
            string outcome;
            try
            {
                session = RunSession.Load(pump, clone, player, player, player, open: true);
                if (export != null) export.Session = session;
                var sample = new Rng(CombatSession.StableHash(id));
                var s = session;
                session.Entering = () => s.Redraw(sample);
                int floor = -1;
                while (!session.IsOver && session.Trace.Count < maxDecisions)
                {
                    if (session.Run.TotalFloor != floor)
                    {
                        floor = session.Run.TotalFloor;
                        Console.WriteLine($"progress {floor} {session.Player.Creature.CurrentHp} {session.Player.Creature.MaxHp}");
                    }
                    session.Step();
                }
                outcome = session.Won ? "won" : session.IsOver ? "lost" : "truncated";
            }
            catch (Exception e)
            {
                Logs.Sim.LogError(e, "rollout {I} failed", i);
                outcome = "failed";
            }
            int at = session?.Run.TotalFloor ?? 0, hp = session?.Player.Creature.CurrentHp ?? 0;
            session?.Dispose();
            brain.End(new RunEnd(outcome == "won", at, outcome), null);
            Console.WriteLine($"rollout {i} {outcome} {at} {hp}");
        }
        return 0;
    }
}
