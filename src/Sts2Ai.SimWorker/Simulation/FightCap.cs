using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2Ai.SimWorker;

/// <summary>
/// The fight round cap (docs: simulator/runs §3): a fight still going when round <see cref="MaxRounds"/> + 1 starts is
/// lost. The game has no round limit, and a player who can no longer hurt the enemies, behind a block they cannot get
/// through, would play the fight forever. At the player's turn past the cap the driver kills the player the way the
/// game's own kill command does, forced so that nothing revives them, and then checks the win condition. The run, its
/// search worlds and the records see a death like any other.
/// </summary>
internal static class FightCap
{
    public const int MaxRounds = 80;

    /// <summary>Kills <paramref name="player"/> if their fight is past the cap. Returns whether it did.</summary>
    public static bool Enforce(Pump pump, Player player, Func<string> describe)
    {
        if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) return false;
        if (CombatManager.Instance.DebugOnlyGetState() is not { RoundNumber: > MaxRounds }) return false;
        var kill = Kill(player);
        pump.RunUntil(() => kill.IsCompleted, describe);
        if (!kill.IsCompletedSuccessfully) throw kill.Exception?.GetBaseException() ?? new OperationCanceledException();
        pump.Drain();
        return true;
    }

    private static async Task Kill(Player player)
    {
        await CreatureCmd.Kill(player.Creature, force: true);
        await CombatManager.Instance.CheckWinCondition();
    }
}
