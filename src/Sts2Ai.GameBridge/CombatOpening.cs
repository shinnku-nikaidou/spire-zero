using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace Sts2Ai.GameBridge;

/// <summary>
/// The moment a combat begins: the game's turn loop starts its combat (<c>CombatManager.StartCombatInternal</c>) and has
/// waited for the executor to finish what it ran (in a run, the map move that entered the room), the combat set up and
/// nothing of its start run yet. That is the first <c>CombatManager.AfterCreatureAdded(Creature, CombatState)</c> while
/// the combat is starting: creatures added later (<c>CreatureCmd.Add</c>) are added to a combat in progress. A host
/// writes the combat out here (plans/2026-11-21-1213-nosl-prompt-worlds.md): the engine begins it from there
/// (<c>sz_world_begin</c>), which is how it stands at a card prompt that opens the combat, before any decision the combat
/// could be written out at. Hosts install the patch with <see cref="Patch"/>, and say what to do with
/// <see cref="Starting"/>; it runs on the game's thread, and an error in it never reaches the game.
/// </summary>
public static class CombatOpening
{
    /// <summary>Told each time a combat begins, before any of its start runs.</summary>
    public static Action? Starting;

    /// <summary>What went wrong the last time <see cref="Starting"/> ran; null when it ran.</summary>
    public static string? Failed { get; private set; }

    /// <summary>The combat <see cref="Starting"/> was told of last: the start's loop adds every creature.</summary>
    private static readonly WeakReference<CombatState?> Told = new(null);

    public static void Patch(Harmony harmony) => harmony.Patch(
        AccessTools.DeclaredMethod(typeof(CombatManager), "AfterCreatureAdded", [typeof(Creature), typeof(CombatState)]),
        prefix: new HarmonyMethod(typeof(CombatOpening), nameof(Adding)));

    private static void Adding(CombatState state)
    {
        if (!CombatManager.Instance.IsStarting || (Told.TryGetTarget(out var told) && ReferenceEquals(told, state))) return;
        Told.SetTarget(state);
        try
        {
            Starting?.Invoke();
            Failed = null;
        }
        catch (Exception e)
        {
            Failed = $"{e.GetType().Name}: {e.Message}";
        }
    }
}
