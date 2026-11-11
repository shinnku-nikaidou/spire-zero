using Sts2Ai.Protocol;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>An action that the screen does not offer.</summary>
internal static class Illegal
{
    public static InvalidOperationException Action(MacroAction action, string surface) => new($"illegal {surface} action {action}");
}
