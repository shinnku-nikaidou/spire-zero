namespace Sts2Ai.Protocol;

/// <summary>
/// Which screen is asking (docs: protocol/observation §5): the key that routes a decision to a brain, and the "surface" field that
/// says which <see cref="ScreenView"/> an observation carries.
/// </summary>
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
