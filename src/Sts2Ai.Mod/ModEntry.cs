using MegaCrit.Sts2.Core.Modding;

namespace Sts2Ai.Mod;

/// <summary>
/// The entry point the game's mod loader calls. It runs before the model database is ready, so it only
/// sets things up; the work starts on the first frame.
/// </summary>
[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public static void Init() => Host.Start();
}
