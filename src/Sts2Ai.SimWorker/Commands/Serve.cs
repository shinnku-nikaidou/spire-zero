using System.CommandLine;
using System.Text;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Commands;

/// <summary>
/// The worker side of the pool (docs: simulator/pool §4): after the hello, one request per stdin line and one reply per
/// stdout line, until stdin closes. Each request names its world in full, so nothing carries over between requests.
/// Everything else the process prints goes to stderr (moved there before the game boots), so the game's own output
/// cannot get into the stream. After a request that threw, the game's state is unknown (a
/// combat may still be set up), so the worker replies with the error and exits; the pool starts it again.
/// </summary>
internal static class Serve
{
    private static readonly LeafArgs Leaf = new();

    public static Command Command()
    {
        var command = Opt.Command("serve", "a worker: JSON lines on stdin and stdout (docs: simulator/pool §4)", Leaf.Options);
        command.SetAction(r =>
        {
            // stdout is the wire: keep it, and send everything else that is printed to stderr from the start.
            var wire = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(Console.Error);
            return Cli.Game((_, pump) => Run(pump, wire, Leaf.Read(r).Net))(r);
        });
        return command;
    }

    /// <param name="leaf">The leaf to search with; null for a worker that only probes l0's options on the clone.</param>
    private static int Run(Pump pump, TextWriter wire, ICombatLeaf? leaf)
    {
        var pimc = leaf == null ? null : new PimcBrain(leaf);
        var planner = leaf == null ? null : new SlPlanner(leaf);
        static Exception NoLeaf() => new InvalidOperationException("a search request to a worker with no --combat-net");
        HeadlessGame.TakeErrors();
        wire.WriteLine(ProtocolJson.Serialize(new WorkerHello(HeadlessGame.GameVersion)));
        while (Console.In.ReadLine() is { } line)
        {
            long id = -1;
            try
            {
                var request = ProtocolJson.Deserialize<WorkerRequest>(line);
                id = request.Id;
                var reply = request switch
                {
                    SlTurnRequest turn => new WorkerReply(id, null, null, [], SearchTurn(pump, planner ?? throw NoLeaf(), turn)),
                    SlMacroRequest macro => new WorkerReply(id, null, null, [], Afters: NodeProbe.After(pump, macro.Node, macro.Paths)),
                    PimcWorldRequest world => new WorkerReply(id, Search(pump, pimc ?? throw NoLeaf(), world), null, []),
                    _ => throw new InvalidDataException($"no search for {request.GetType().Name}"),
                };
                wire.WriteLine(ProtocolJson.Serialize(reply with { GameErrors = HeadlessGame.TakeErrors() }));
            }
            catch (Exception e)
            {
                Logs.Sim.LogError(e, "request {Id} failed", id);
                wire.WriteLine(ProtocolJson.Serialize(new WorkerReply(id, null, Describe(e), HeadlessGame.TakeErrors())));
                return 1;
            }
        }
        return 0;
    }

    /// <summary>
    /// The exception, with the innermost one's top frames first, since the run's failure keeps one short line; the
    /// whole exception follows on the next lines, for the run's log (a worker's own stderr is not kept).
    /// </summary>
    private static string Describe(Exception e)
    {
        var frames = (e.GetBaseException().StackTrace ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Take(3).Select(f => string.Join('.', f.Split('(')[0].Split(' ').Last().Split('.').TakeLast(2)));
        return $"{e.GetType().Name} at {string.Join(" from ", frames)}: {Logs.FirstLine(e.Message)}\n{e}";
    }

    private static IReadOnlyList<PlannedLine> Search(Pump pump, PimcBrain brain, PimcWorldRequest r)
    {
        using var worlds = SimWorlds.NoSL(pump, r.Observation, r.BuildSeed, r.Run, r.History);
        return brain.SearchWorld(worlds.Worlds(r.Worlds, r.SearchSeed)[r.World], r.Observation.Round, r.First, r.NodesPerFirst, r.History);
    }

    /// <summary>The SL planner's turn from a state of the node's clone: the clone at the decision, then the path.</summary>
    private static TurnSearch SearchTurn(Pump pump, SlPlanner planner, SlTurnRequest r)
    {
        using var world = new NodeWorld(pump, r.Node);
        foreach (var action in r.Path) world.Execute(action);
        return planner.SearchTurn(world, r.History, r.Nodes, r.Keep, r.Floor);
    }
}
