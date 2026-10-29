using Sts2Ai.Brain;

namespace Sts2Ai.Tests;

public class SoftmaxTests
{
    [Fact]
    public void BestIsTheFirstBest() => Assert.Equal(1, Softmax.Best([0.1, 0.5, 0.5, -1]));

    [Fact]
    public void DrawsInProportionToTheExponentOfTheValue()
    {
        // At τ 2, weights 1 : 1/2 : e^-20.
        var softmax = new Softmax(2, new Random(1));
        var counts = new int[3];
        for (int i = 0; i < 30_000; i++) counts[softmax.Draw([3, 3 - 2 * Math.Log(2), -37])]++;
        Assert.InRange(counts[0] / 30_000.0, 0.65, 0.68);
        Assert.InRange(counts[1] / 30_000.0, 0.32, 0.35);
        Assert.Equal(0, counts[2]);
    }

    [Fact]
    public void AColdOneTakesTheBest()
    {
        var softmax = new Softmax(1e-6, new Random(1));
        Assert.All(Enumerable.Range(0, 1000), _ => Assert.Equal(2, softmax.Draw([0, 0.5, 0.501])));
    }

    [Fact]
    public void ADrawWithinAHundredthOfTheTemperatureIsATie()
    {
        var softmax = new Softmax(0.002, new Random(1));
        Assert.False(softmax.Explored(0.79009, 0.7901));
        Assert.True(softmax.Explored(0.7898, 0.7901));
    }

    [Fact]
    public void RefusesATemperatureOfZero() => Assert.Throws<ArgumentOutOfRangeException>(() => new Softmax(0, new Random(1)));
}
