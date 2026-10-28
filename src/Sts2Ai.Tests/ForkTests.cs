using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.Tests;

public class ForkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sts2ai-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AnAfterstateIsTheFieldsItChanges()
    {
        var run = Fixtures.Floor1();
        var after = run with { Gold = run.Gold + 10, Map = run.Map with { Visited = [.. run.Map.Visited, new Coord(1, 1)] } };
        var changes = Logged.Changes(run, after);
        Assert.Equal(["gold", "map"], changes.Select(c => c.Key).Order());
        Assert.Equal(run.Gold + 10, (int)changes["gold"]!);
        Assert.Equal(["visited"], changes["map"]!.AsObject().Select(c => c.Key));
        Assert.Empty(Logged.Changes(run, run));
    }

    [Fact]
    public void ALoggedRunNeedsItsRecordAndEveryLine()
    {
        string Line(string seed, int id, string surface = "map") =>
            $$$"""{"run":"{{{RunHeader.IdOf(seed)}}}","decision_id":{{{id}}},"surface":"{{{surface}}}","chosen":"map:0,1","obs_hash":"0","legal":2,"run_view":{"act":0}}""";
        var log = Path.Combine(_dir, "log.jsonl");
        var records = Path.Combine(_dir, "rec.jsonl");
        File.WriteAllLines(log,
        [
            Line("WHOLE", 0), Line("WHOLE", 1, "combat"),
            Line("GAP", 0), Line("GAP", 2),
            Line("NORECORD", 0),
            Line("CUT", 0), Line("CUT", 1)[..20],
        ]);
        File.WriteAllLines(records, new[] { "WHOLE", "GAP", "CUT" }.Select(s => ProtocolJson.Serialize(Fixtures.Run(s, 10))));
        var runs = LoggedRun.Read(log, records).ToList();
        Assert.Equal(["WHOLE", "CUT"], runs.Select(r => r.Setup.Seed));
        Assert.Equal([Surfaces.Map, Surfaces.Combat], runs[0].Steps.Select(s => s.Surface));
        Assert.Equal(0, runs[0].Steps[0].Act);
    }
}
