using Sts2Ai.Brain;

namespace Sts2Ai.Tests.Brain;

public class MoveRecordTests
{
    [Fact]
    public void RecordsEachLivingEnemyOncePerRound()
    {
        var record = new MoveRecord();
        var obs = Fixtures.Slugs();
        record.Witness(obs);
        record.Witness(obs);
        record.Witness(obs with { Round = 2 });

        Assert.Equal([1u, 2u], record.History.Select(e => e.Uid));
        Assert.All(record.History, e => Assert.Equal([1, 2], e.Turns.Select(t => t.Round)));
        Assert.Equal("Attack", record.History[0].Turns[0].Intents[0].Type);
        Assert.Equal("Debuff", record.History[1].Turns[0].Intents[0].Type);
    }

    [Fact]
    public void SkipsTheDead()
    {
        var record = new MoveRecord();
        record.Witness(Fixtures.Slugs());
        record.Witness(Fixtures.Slugs().WithEnemyHp(1, 0) with { Round = 2 });

        Assert.Equal([1], record.History.Single(e => e.Uid == 1).Turns.Select(t => t.Round));
        Assert.Equal([1, 2], record.History.Single(e => e.Uid == 2).Turns.Select(t => t.Round));
    }

    [Fact]
    public void StartsOverWhenTheRoundGoesBack()
    {
        var record = new MoveRecord();
        record.Witness(Fixtures.Slugs() with { Round = 3 });
        record.Witness(Fixtures.Slugs());

        Assert.All(record.History, e => Assert.Equal([1], e.Turns.Select(t => t.Round)));
    }

    [Fact]
    public void ClearForgetsTheRound()
    {
        var record = new MoveRecord();
        record.Witness(Fixtures.Slugs());
        record.Clear();
        Assert.Empty(record.History);

        record.Witness(Fixtures.Slugs());
        Assert.Equal(2, record.History.Count);
    }
}
