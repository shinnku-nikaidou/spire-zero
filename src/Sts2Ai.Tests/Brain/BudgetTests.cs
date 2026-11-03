using Sts2Ai.Brain;

namespace Sts2Ai.Tests;

public class BudgetTests
{
    [Fact]
    public void NamesShowOnlyWhatIsNotTheDefault()
    {
        Assert.Equal("8x300", new Budget().ToString());
        Assert.Equal("4x100", new Budget(4, 100).ToString());
        Assert.Equal("beam 16x300", new Budget(Beam: 16).ToString());
        Assert.Equal("beam 4x300 loss plans 3", new Budget(Beam: 4, LossPlans: 3).ToString());
        Assert.Equal("beam 8x300 easy 4 loss plans 3", new Budget(Beam: 8, EasyBeam: 4, LossPlans: 3).ToString());
    }
}
