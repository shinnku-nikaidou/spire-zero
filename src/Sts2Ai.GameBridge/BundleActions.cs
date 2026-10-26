using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge;

/// <summary>The choose-a-bundle screen: "bundle:i" takes bundle i, in the screen's order. It cannot be skipped.</summary>
public static class BundleActions
{
    public static List<string> Legal(int bundles) => Enumerable.Range(0, bundles).Select(i => $"bundle:{i}").ToList();

    /// <summary>The bundle <paramref name="action"/> takes.</summary>
    public static int Index(int bundles, string action)
    {
        if (!Legal(bundles).Contains(action)) throw ActionText.Illegal(action, Surfaces.Bundle);
        return ActionText.Index(action["bundle:".Length..]);
    }
}
