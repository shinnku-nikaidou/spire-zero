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
    public static int Run(Pump pump, TextWriter wire)
    {
        var brain = new PimcBrain();
        HeadlessGame.TakeErrors();
        wire.WriteLine(ProtocolJson.Serialize(new WorkerHello(HeadlessGame.GameVersion)));
        while (Console.In.ReadLine() is { } line)
        {
            long id = -1;
            WorkerReply reply;
            try
            {
                var request = ProtocolJson.Deserialize<PimcWorldRequest>(line);
                id = request.Id;
                using var worlds = SimWorlds.NoSL(pump, request.Observation, request.BuildSeed);
                var world = worlds.Worlds(request.Worlds, request.SearchSeed)[request.World];
                var lines = brain.SearchWorld(world, request.Observation.Round, request.First, request.NodesPerWorld);
                reply = new(id, lines, null, HeadlessGame.TakeErrors());
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
}
