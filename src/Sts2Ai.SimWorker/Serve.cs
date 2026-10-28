using System.CommandLine;
using System.Text;
using Microsoft.Extensions.Logging;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// The worker side of the pool (docs: simulator §8): after the hello, one request per stdin line and one reply per
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
        var command = Opt.Command("serve", "a worker: JSON lines on stdin and stdout (docs: simulator §8)", Leaf.Options);
        command.SetAction(r =>
        {
            // stdout is the wire: keep it, and send everything else that is printed to stderr from the start.
            var wire = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(Console.Error);
            return Cli.Game((_, pump) => Run(pump, wire, Leaf.Read(r).Leaf))(r);
        });
        return command;
    }

    /// <param name="leaf">The leaf to search with.</param>
    private static int Run(Pump pump, TextWriter wire, ICombatLeaf leaf)
    {
        var brain = new PimcBrain(leaf);
        HeadlessGame.TakeErrors();
        wire.WriteLine(ProtocolJson.Serialize(new WorkerHello(HeadlessGame.GameVersion)));
        while (Console.In.ReadLine() is { } line)
        {
            long id = -1;
            try
            {
                var request = ProtocolJson.Deserialize<WorkerRequest>(line);
                id = request.Id;
                wire.WriteLine(ProtocolJson.Serialize(new WorkerReply(id, Search(pump, brain, request), null, HeadlessGame.TakeErrors())));
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
        return $"{e.GetType().Name} at {string.Join(" from ", frames)}: {Cli.FirstLine(e.Message)}\n{e}";
    }

    private static IReadOnlyList<PlannedLine> Search(Pump pump, PimcBrain brain, WorkerRequest request)
    {
        switch (request)
        {
            case PimcWorldRequest r:
            {
                using var worlds = SimWorlds.NoSL(pump, r.Observation, r.BuildSeed, r.History);
                return brain.SearchWorld(worlds.Worlds(r.Worlds, r.SearchSeed)[r.World], r.Observation.Round, r.First, r.NodesPerFirst, r.History);
            }
            case SlWorldRequest r:
            {
                using var world = new NodeWorld(pump, r.Node);
                return brain.SearchWorld(world, world.Observe().Round, r.First, r.NodesPerFirst, r.History);
            }
            default:
                throw new InvalidDataException($"no search for {request.GetType().Name}");
        }
    }
}
