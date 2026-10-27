using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker;

/// <summary>
/// The worker side of the pool (docs: simulator §8): after the hello, one request per stdin line and one reply per
/// stdout line, until stdin closes. Each request names its world in full, so nothing carries over between requests.
/// Everything else the process prints goes to stderr (<see cref="Cli.Run"/> moves it there before the game boots), so
/// the game's own output cannot get into the stream.
/// </summary>
internal static class Serve
{
    /// <param name="leaf">The leaf weights to search with; null for the default ones.</param>
    public static int Run(Pump pump, TextWriter wire, LeafWeights? leaf)
    {
        var brain = new PimcBrain(leaf);
        HeadlessGame.TakeErrors();
        wire.WriteLine(ProtocolJson.Serialize(new WorkerHello(HeadlessGame.GameVersion)));
        while (Console.In.ReadLine() is { } line)
        {
            long id = -1;
            WorkerReply reply;
            try
            {
                var request = ProtocolJson.Deserialize<WorkerRequest>(line);
                id = request.Id;
                reply = new(id, Search(pump, brain, request), null, HeadlessGame.TakeErrors());
            }
            catch (Exception e)
            {
                if (HeadlessGame.Verbose) Console.Error.WriteLine(e);
                reply = new(id, null, $"{e.GetType().Name}: {Cli.FirstLine(e.Message)}", HeadlessGame.TakeErrors());
            }
            wire.WriteLine(ProtocolJson.Serialize(reply));
        }
        return 0;
    }

    private static IReadOnlyList<PlannedLine> Search(Pump pump, PimcBrain brain, WorkerRequest request)
    {
        switch (request)
        {
            case PimcWorldRequest r:
            {
                using var worlds = SimWorlds.NoSL(pump, r.Observation, r.BuildSeed);
                return brain.SearchWorld(worlds.Worlds(r.Worlds, r.SearchSeed)[r.World], r.Observation.Round, r.First, r.NodesPerWorld);
            }
            case SlWorldRequest r:
            {
                using var world = new NodeWorld(pump, r.Node);
                return brain.SearchWorld(world, world.Observe().Round, r.First, r.NodesPerWorld);
            }
            default:
                throw new InvalidDataException($"no search for {request.GetType().Name}");
        }
    }
}
