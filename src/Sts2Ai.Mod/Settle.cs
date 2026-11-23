using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// Whether the game waits for the player, read from the game itself: a decision is asked only then
/// (<see cref="Screens"/>), and auto-play presses a button only then (<see cref="Executor.Nudge"/>). The simulator
/// settles the same way, when the game has nothing left to run (RunSession.Settle); the real game also changes rooms and
/// acts behind its fades, and shows a step's result only once the step's own task ends.
/// </summary>
internal static class Settle
{
    private static readonly AccessTools.FieldRef<EventSynchronizer, List<Task>> EventOptionTasks = AccessTools.FieldRefAccess<EventSynchronizer, List<Task>>("_pendingOptionTasks");

    /// <summary>The tasks of the steps whose tasks the game keeps no list of, with the room and the surface they were taken on.</summary>
    private static readonly List<(AbstractRoom? Room, string Surface, Task Task)> _steps = [];

    public static void Patch(Harmony harmony)
    {
        Postfix(harmony, typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption), nameof(RestTaken));
        Postfix(harmony, typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.SelectLocalReward), nameof(RewardTaken));
        Postfix(harmony, typeof(MerchantSynchronizer), nameof(MerchantSynchronizer.PurchaseLocalEntry), nameof(Bought));
        Postfix(harmony, typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked), nameof(Divined));
    }

    private static void Postfix(Harmony harmony, Type type, string method, string postfix) =>
        harmony.Patch(AccessTools.DeclaredMethod(type, method), postfix: new HarmonyMethod(typeof(Settle), postfix));

    /// <summary>
    /// The game runs nothing between the player's decisions: no action queued or running; no room being changed and no
    /// loading sequence, which an act's entry is to its end, its hooks after the fade included (a Dread act's dominion
    /// comes there: asked before it, Neow's first screen had none, 2026-11-23); no combat being set up (the last room's
    /// proceed button, pressed during the travel, opened the map over the new room, 2026-11-23).
    /// </summary>
    public static bool Idle
    {
        get
        {
            var rm = RunManager.Instance;
            return rm.ActionQueueSet.IsEmpty && !rm.IsChangingRooms && !rm.NetService.IsGameLoading && !CombatManager.Instance.IsStarting;
        }
    }

    /// <summary>
    /// Whether a step taken on <paramref name="surface"/> in the room on screen still plays out (an event option, a rest
    /// option, a reward, a purchase, a divination): until it ends, the screen still shows the decision it answered. The
    /// simulator waits for the same tasks before its next decision. An event's are the game's own list, which each event
    /// starts empty; the others' are kept here, and one of another room, or of an abandoned run, holds no screen of this one.
    /// </summary>
    public static bool Playing(RunState run, string surface)
    {
        if (surface == Surfaces.Event) return EventOptionTasks(RunManager.Instance.EventSynchronizer).Any(t => !t.IsCompleted);
        _steps.RemoveAll(s => s.Task.IsCompleted || !ReferenceEquals(s.Room, run.CurrentRoom));
        return _steps.Any(s => s.Surface == surface);
    }

    private static void Add(string surface, Task task) => _steps.Add((RunManager.Instance.DebugOnlyGetState()?.CurrentRoom, surface, task));

    private static void RestTaken(Task __result) => Add(Surfaces.Rest, __result);

    private static void RewardTaken(Task __result) => Add(Surfaces.Rewards, __result);

    private static void Bought(Task __result) => Add(Surfaces.Shop, __result);

    private static void Divined(Task __result) => Add(Surfaces.CrystalSphere, __result);
}
