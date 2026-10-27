using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>
/// Why the game is asking for cards (<see cref="ChoiceContext"/>). Every card prompt goes through one of the
/// <c>CardSelectCmd.From*</c> entries, and only their arguments say why, so a patch on each records the context for
/// the duration of the call. A selector runs inside that call; hosts install the patch with <see cref="Patch"/>.
/// </summary>
public static class ChoiceContexts
{
    /// <summary>Asks the selector for 0..1 cards and enforces the pick itself, unless it can be skipped.</summary>
    private const string ChooseACard = "choose_a_card_screen";

    [ThreadStatic] private static ChoiceContext? _current;
    [ThreadStatic] private static int _depth;
    private static volatile ChoiceContext? _last;

    /// <summary>The context of the prompt being asked; null outside the entries.</summary>
    public static ChoiceContext? Current => _current;

    /// <summary>
    /// The context of the latest prompt, kept after its entry returns. In the real game the prompt's screen opens
    /// after the entry's first await, when <see cref="Current"/> is already gone; a new context object means a new prompt.
    /// </summary>
    public static ChoiceContext? Last => _last;

    public static void Patch(Harmony harmony) => harmony.CreateClassProcessor(typeof(Entries)).Patch();

    /// <summary>How many cards the answer may have, as the prompt's screen allows, given what the game asks the selector for.</summary>
    public static (int Min, int Max) Bounds(ChoiceContext context, int minSelect, int maxSelect, int options)
    {
        int max = Math.Min(maxSelect, options);
        int min = context.Cancelable ? 0 : context.Purpose == ChooseACard ? 1 : minSelect;
        return (Math.Min(min, max), max);
    }

    private static ChoiceContext Read(MethodBase entry, object?[] args)
    {
        var parameters = entry.GetParameters();
        object? Arg(Func<ParameterInfo, bool> match)
        {
            int i = Array.FindIndex(parameters, p => match(p));
            return i < 0 ? null : args[i];
        }

        var prefs = Arg(p => p.ParameterType == typeof(CardSelectorPrefs)) as CardSelectorPrefs?;
        var source = Arg(p => p.Name == "source") as AbstractModel ?? (Arg(p => p.ParameterType == typeof(PlayerChoiceContext)) as PlayerChoiceContext)?.LastInvolvedModel;
        var pile = Arg(p => p.ParameterType == typeof(CardPile)) as CardPile;
        var enchantment = Arg(p => p.ParameterType == typeof(EnchantmentModel)) as EnchantmentModel;
        var prompt = prefs?.Prompt;
        return new ChoiceContext
        {
            Purpose = pile != null ? $"{JsonNamingPolicy.SnakeCaseLower.ConvertName(pile.Type.ToString())}_pile" : JsonNamingPolicy.SnakeCaseLower.ConvertName(entry.Name["From".Length..]),
            Source = source?.Id.Entry,
            Cancelable = prefs?.Cancelable ?? Arg(p => p.Name == "canSkip") is true,
            InCombat = CombatManager.Instance.IsInProgress,
            PromptKey = prompt != null ? $"{prompt.LocTable}.{prompt.LocEntryKey}" : null,
            Enchantment = enchantment != null ? new ModifierView(enchantment.Id.Entry, (int)Arg(p => p.Name == "amount")!) : null,
        };
    }

    [HarmonyPatch]
    private static class Entries
    {
        // The bundle screen is not a card prompt: it has its own surface.
        private static IEnumerable<MethodBase> TargetMethods() => typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("From", StringComparison.Ordinal) && m.Name != nameof(CardSelectCmd.FromChooseABundleScreen));

        // An entry can call another (removal goes through the generic deck prompt): the outermost one says why.
        private static void Prefix(MethodBase __originalMethod, object?[] __args)
        {
            if (_depth++ == 0) _last = _current = Read(__originalMethod, __args);
        }

        private static Exception? Finalizer(Exception? __exception)
        {
            if (--_depth == 0) _current = null;
            return __exception;
        }
    }
}
