using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>The choose-a-bundle screen: "bundle:i" takes bundle i, in the screen's order. It cannot be skipped.</summary>
public static class BundleActions
{
    public static BundleScreen Screen(IReadOnlyList<IReadOnlyList<CardModel>> bundles) => new()
    {
        Bundles = [.. bundles.Select(b => (IReadOnlyList<CardView>)[.. b.Select(c => Observe.Card(c))])],
    };

    public static List<MacroAction> Legal(int bundles) => [.. Enumerable.Range(0, bundles).Select(i => MacroAction.Of(MacroAction.Bundle, i))];

    /// <summary>The bundle <paramref name="action"/> takes.</summary>
    public static int Index(int bundles, MacroAction action)
    {
        if (!Legal(bundles).Contains(action)) throw Illegal.Action(action, Surfaces.Bundle);
        return action.Index;
    }
}
