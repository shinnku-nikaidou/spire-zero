namespace Sts2Ai.Protocol;

/// <summary>Which screen is asking (docs: protocol §3). Also the key that routes a decision to a brain.</summary>
public static class Surfaces
{
    public const string Combat = "combat";
    public const string CardChoice = "card_choice";
    public const string Map = "map";
    public const string Rewards = "rewards";
    public const string CardReward = "card_reward";
    public const string Bundle = "bundle";
    public const string Event = "event";
    public const string Shop = "shop";
    public const string Rest = "rest";
    public const string Treasure = "treasure";
    public const string CrystalSphere = "crystal_sphere";
}

/// <summary>
/// An out-of-combat decision: the surface and its legal actions in text form ("map:3,5", "reward:0", "leave", ...;
/// grammar in docs: protocol §3). The answer is one of <see cref="Legal"/>.
/// </summary>
public sealed record MacroDecision(string Surface, IReadOnlyList<string> Legal);
