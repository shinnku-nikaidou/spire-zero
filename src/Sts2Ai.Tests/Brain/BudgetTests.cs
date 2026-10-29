using Sts2Ai.Brain;

namespace Sts2Ai.Tests;

public class BudgetTests
{
    [Theory]
    [InlineData("Monster", 300)]
    [InlineData(null, 300)]
    [InlineData("Elite", 600)]
    [InlineData("Boss", 450)]
    public void TheRoomsMultipleScalesTheNodes(string? room, int nodes)
    {
        var budget = new Budget(Elite: 2, Boss: 1.5).For(room);
        Assert.Equal(new Budget(NodesPerWorld: nodes), budget);
    }

    [Theory]
    [InlineData("Monster", 4)]
    [InlineData("Elite", 8)]
    [InlineData("Boss", 6)]
    public void ThePlannersRoomMultipleScalesItsBeam(string room, int beam)
    {
        var budget = new Budget(NodesPerWorld: 150, Elite: 2, Boss: 1.5, Beam: 4, EasyBeam: 2).For(room);
        Assert.Equal(new Budget(NodesPerWorld: 150, Beam: beam, EasyBeam: beam / 2), budget);
        Assert.Equal(budget, budget.For(room));
    }

    [Fact]
    public void NamesShowOnlyWhatIsNotTheDefault()
    {
        Assert.Equal("8x300", new Budget().ToString());
        Assert.Equal("4x100 elite x2 halving", new Budget(4, 100, Elite: 2, Halving: true).ToString());
        Assert.Equal("beam 16x300 boss x2", new Budget(Boss: 2, Beam: 16).ToString());
        Assert.Equal("beam 4x300 elite x2 loss plans 3", new Budget(Elite: 2, Beam: 4, LossPlans: 3).ToString());
        Assert.Equal("beam 8x300 easy 4 loss plans 3", new Budget(Beam: 8, EasyBeam: 4, LossPlans: 3).ToString());
    }
}
