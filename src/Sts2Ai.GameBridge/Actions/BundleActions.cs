using MegaCrit.Sts2.Core.Models;
using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>The choose-a-bundle screen: "bundle:i" takes bundle i, in the screen's order. It cannot be skipped.</summary>
public static class BundleActions
{
    public static BundleScreen Screen(IReadOnlyList<IReadOnlyList<CardModel>> bundles) => new()
    {
        Bundles = bundles.Select(b => (IReadOnlyList<CardView>)b.Select(c => Observe.Card(c)).ToList()).ToList(),
    };

    public static List<string> Legal(int bundles) => Enumerable.Range(0, bundles).Select(i => $"bundle:{i}").ToList();

    /// <summary>The bundle <paramref name="action"/> takes.</summary>
    public static int Index(int bundles, string action)
    {
        if (!Legal(bundles).Contains(action)) throw ActionText.Illegal(action, Surfaces.Bundle);
        return ActionText.Index(action["bundle:".Length..]);
    }
}
