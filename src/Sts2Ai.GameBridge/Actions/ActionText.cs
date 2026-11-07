using System.Globalization;

namespace Sts2Ai.GameBridge.Actions;

/// <summary>Parses the "verb:argument" text form of out-of-combat actions.</summary>
internal static class ActionText
{
    /// <summary>The argument of <paramref name="action"/> if its verb is <paramref name="verb"/>.</summary>
    public static bool Is(string action, string verb, out string arg)
    {
        arg = "";
        if (!action.StartsWith(verb + ":", StringComparison.Ordinal)) return false;
        arg = action[(verb.Length + 1)..];
        return true;
    }

    public static int Index(string arg) =>
        int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : throw new FormatException($"not an index: {arg}");

    public static InvalidOperationException Illegal(string action, string surface) => new($"illegal {surface} action {action}");
}
