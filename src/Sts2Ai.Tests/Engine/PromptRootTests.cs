using Sts2Ai.Brain;
using Sts2Ai.Harness;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Engine;

/// <summary>The crossing of a card prompt a plan is rooted at, on the node's clone: each path of answers is asked of the
/// clone once, a batch at a time. The engine's search over it is tested in the engine.</summary>
public sealed class PromptRootTests
{
    private static readonly RunView Around = new()
    {
        Character = "SILENT", Ascension = 0, Act = 1, ActId = "HIVE", Floor = 3, Hp = 70, MaxHp = 70, Gold = 99,
        Deck = [], Relics = [], Potions = [null],
        Map = new MapView { Points = [], Visited = [], Boss = "QUEEN_BOSS" },
    };

    [Fact]
    public void APromptsAnswersAreCrossedAFewAtATime()
    {
        var batches = new List<int>();
        var crossing = new PromptRoot.Crossing(paths =>
        {
            batches.Add(paths.Count);
            return [.. paths.Select(_ => new PromptStep(CombatStatus.Lost, Fixtures.Slugs(), Around))];
        });
        var answers = Enumerable.Range(0, 2 * PromptRoot.Crossing.Batch + 5).Select(i => (IReadOnlyList<CombatAction>)[CombatAction.Pick([i])]).ToList();
        crossing.Ahead(answers);
        crossing.Ahead(answers);
        Assert.Equal([PromptRoot.Crossing.Batch, PromptRoot.Crossing.Batch, 5], batches);
        Assert.Equal(answers.Count, crossing.Crossed);
    }

    [Fact]
    public void AnswersCrossedAheadAreNotAskedAgain()
    {
        var asked = new List<string>();
        var crossing = new PromptRoot.Crossing(paths => [.. paths.Select(path =>
        {
            asked.Add(string.Join(' ', path));
            return new PromptStep(CombatStatus.Lost, Fixtures.Slugs(), Around);
        })]);
        crossing.Ahead([[], [CombatAction.Pick([0])], [CombatAction.Pick([1])]]);
        crossing.Ahead([[CombatAction.Pick([1])], [CombatAction.Pick([1]), CombatAction.Pick([0])]]);
        Assert.Equal(["", "pick:0", "pick:1", "pick:1 pick:0"], asked);
        Assert.Equal(4, crossing.Crossed);
    }
}
