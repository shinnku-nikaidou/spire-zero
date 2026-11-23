using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// The character the player picks on a screen that starts a run (the standard one, custom, Blitz, Dread), before the
/// run exists: the panel takes its colours (<see cref="Palette"/>) while the screen shows. Every such screen picks
/// through its buttons' <c>Select</c>; the random button's character has no colours of its own.
/// </summary>
internal static class CharacterPick
{
    private static NCharacterSelectButton? _button;

    /// <summary>The picked character's id while its screen shows; null otherwise.</summary>
    public static string? Shown =>
        _button is { } button && GodotObject.IsInstanceValid(button) && button.IsVisibleInTree() ? button.Character?.Id.Entry : null;

    public static void Patch(Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(NCharacterSelectButton), nameof(NCharacterSelectButton.Select)),
        postfix: new HarmonyMethod(typeof(CharacterPick), nameof(Picked)));

    private static void Picked(NCharacterSelectButton __instance) => _button = __instance;
}
