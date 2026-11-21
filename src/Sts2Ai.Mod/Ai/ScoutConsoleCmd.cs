using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Sts2Ai.Mod;

/// <summary>
/// The spoiler, a command of its own: what the rooms ahead hold (the map, what each ? room becomes, shops, chests,
/// card rewards along a route), from the game's save of this room, by the game's own code in the shipped simulator.
/// The AI never reads it: it goes past what either environment may know. So it is a debug command, and the game marks
/// the run as it does for its own. A route is the act's steps after this room ("left*", "right*", "c,r", "left"), "/"
/// for the next act; "ai" follows the AI's planned route on the map.
/// </summary>
public sealed class ScoutConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "scout";

    public override string Args => "[ai | steps…  (left*, right*, c,r, / for the next act)]";

    public override string Description => "Spire Zero: what the rooms ahead hold, from this room's save (a spoiler; the run is marked).";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        try
        {
            IReadOnlyList<string> route = args is ["ai"] ? Assist.Current?.Route() ?? throw new AiError("the mod is off") : args;
            return new CmdResult(true, Out.Esc(Jobs.Scout(route)));
        }
        catch (AiError e)
        {
            return new CmdResult(false, Out.Esc(e.Message));
        }
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args) =>
        CompleteArgument(["ai", "left*", "right*", "left", "right", "/"], args[..^1], args.ElementAtOrDefault(args.Length - 1) ?? "");
}
