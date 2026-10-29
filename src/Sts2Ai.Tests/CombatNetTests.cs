using Sts2Ai.Brain;
using Sts2Ai.Protocol;
using Sts2Ai.SimWorker;

namespace Sts2Ai.Tests;

public class CombatNetTests
{
    private static string Repo(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "Sts2Ai.sln"))) dir = dir.Parent!;
        return Path.Combine([dir.FullName, .. path]);
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "combat-sl-tiny.json");

    /// <summary>
    /// An untrained SL combat net with small widths, its test states three real SL round starts (one with four rounds
    /// of an enemy's history, one with an ally), made by <c>learn/combat/export.py</c>'s net_file; loading checks
    /// CombatNet predicts each as the trainer did, so a change to <c>learn/combat/encode.py</c> or to CombatNet that
    /// reads a state otherwise fails here.
    /// </summary>
    [Fact]
    public void ReadsAnSlNetsStatesAsItsTrainerDid()
    {
        var file = ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Fixture));
        using var net = CombatNet.Load(file);
        Assert.Equal("SL", net.Env);
        Assert.Equal(3, file.Tests.Count);
        Assert.Equal(3, file.Tests.Select(t => Math.Round(t.PotionsUsed, 2)).Distinct().Count());
    }

    [Fact]
    public void StillReadsC2()
    {
        using var net = CombatNet.Load(ProtocolJson.Deserialize<CombatNetFile>(File.ReadAllText(Repo("weights", "silent", "c2.json"))));
        Assert.Null(net.Env);
    }

    [Fact]
    public void ANetPlaysOnlyInItsEnvironmentAndAnOldOneInEither()
    {
        var sl = LeafChoice.Read(null, Fixture);
        sl.Check(PlayEnvironment.SL);
        Assert.Contains("SL's endings, not NoSL's", Assert.Throws<NotSupportedException>(() => sl.Check(PlayEnvironment.NoSL)).Message);
        var c2 = LeafChoice.Read(null, Repo("weights", "silent", "c2.json"));
        c2.Check(PlayEnvironment.SL);
        c2.Check(PlayEnvironment.NoSL);
    }
}
