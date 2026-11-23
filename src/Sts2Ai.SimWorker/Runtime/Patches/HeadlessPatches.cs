using HarmonyLib;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Rooms;
using Sts2Ai.GameBridge;

namespace Sts2Ai.SimWorker.Runtime.Patches;

/// <summary>
/// Harmony patches that let the game's model layer run without the engine. Each patch replaces a call that
/// would otherwise reach Godot (rendering, assets, OS, files), persist data, or waste time. None of them changes
/// a game rule, except the "rule fidelity" group, which puts back the shipped rules where TestMode changed them.
/// Each group is a file next to this one; <see cref="Apply"/> installs them all.
/// </summary>
internal static partial class HeadlessPatches
{
    public static string GameVersion = "v0.0.0";

    /// <summary>How many times a Rolling Boulder hit was split into one hit per enemy (checked by selftest).</summary>
    public static int RollingBoulderSplits;

    /// <summary>Plays a crystal sphere minigame the game would put on screen. Without it, the screen's code runs (and fails).</summary>
    public static Action<CrystalSphereMinigame>? CrystalSphereShown;

    /// <summary>The game saving on arrival at a map point: its state is a node checkpoint.</summary>
    public static Action? NodeEntered;

    public static void Apply()
    {
        var harmony = new Harmony("sts2ai.simworker");
        // First: patching compiles each replaced method, which can inline a CardSelectCmd entry not yet patched
        // (SapphireSeed's planting, rewritten by EventPresentation, did).
        ChoiceContexts.Patch(harmony);
        DrawPileLog.Patch(harmony);
        CombatOpening.Patch(harmony);
        harmony.PatchAll(typeof(HeadlessPatches).Assembly);
    }
}
