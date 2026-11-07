using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.Tests.Brain;

public class PimcMemoTests
{
    /// <summary>
    /// A turn of two cards, A and B, where A rolls once if played first and three times if second, and the enemies'
    /// turn then hits for 10 per roll. Played in either order, the two cards show the same state, but not the same
    /// future.
    /// </summary>
    private sealed class Rolls : ICombatWorld
    {
        private static readonly CombatObservation Start = Fixtures.Slugs() with
        {
            Round = 1,
            Hand = [new CardView { Id = "A", Uid = 1 }, new CardView { Id = "B", Uid = 2 }],
        };

        private readonly List<string> _steps = [];

        private int Count => _steps.IndexOf("A") switch { 0 => 1, 1 => 3, _ => 0 };
        private bool Ended => _steps.Contains("end");

        public RunView Run() => Fixtures.Around(Observe());

        public CombatObservation Observe() => Start with
        {
            Round = Ended ? 2 : 1,
            Player = Start.Player with { Hp = Ended ? 100 - 10 * Count : 100, MaxHp = 100 },
            Hand = [.. Start.Hand.Where(c => !_steps.Contains(c.Id))],
        };

        public IReadOnlyList<CombatAction> Legal() => Ended ? [] :
            [CombatAction.EndTurn, .. Observe().Hand.Select(c => CombatAction.Play(c.Uid!.Value))];

        public CombatStatus Status => CombatStatus.InProgress;
        public ulong HiddenKey() => (ulong)Count;

        public void Execute(CombatAction action) =>
            _steps.Add(action.Kind == CombatActionKind.EndTurn ? "end" : Start.Hand.First(c => c.Uid == action.Card).Id);

        public WorldSnapshot Snapshot() => new([.. _steps]);

        public void Restore(WorldSnapshot snapshot)
        {
            _steps.Clear();
            _steps.AddRange(snapshot.Steps);
        }
    }

    /// <summary>The HP after the enemies' turn, and 50 for each card played.</summary>
    private sealed class Leaf : ICombatLeaf
    {
        public LeafOutcome Outcome(CombatStatus status, CombatObservation obs, RunView run, IReadOnlyList<EnemyTurns> history) =>
            new(obs.Player.Hp + 50 * (2 - obs.Hand.Count), 0, 0);
    }

    private static readonly CombatAction A = CombatAction.Play(1), B = CombatAction.Play(2);

    [Fact]
    public void LinesThatLookTheSameButRolledDifferentlyAreNotMerged()
    {
        var pimc = new PimcBrain(new Leaf());
        // A then B: one roll, 190. B then A: three rolls, 170. Searched in either order.
        Assert.Equal([190.0, 170.0], pimc.SearchWorld(new Rolls(), 1, [A, B], 1000).Select(l => l.Value));
        Assert.Equal([170.0, 190.0], pimc.SearchWorld(new Rolls(), 1, [B, A], 1000).Select(l => l.Value));
    }
}
