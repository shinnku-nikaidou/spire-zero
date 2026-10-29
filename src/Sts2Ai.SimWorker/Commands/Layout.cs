namespace Sts2Ai.SimWorker;

/// <summary>
/// Where each character's files go, from the repository root (docs: learning §6, evaluation §2): each character is
/// trained and judged on its own, every ascension by the same brain, so its weights and its evaluation have a folder
/// of their own. The seed sets are shared.
/// </summary>
internal static class Layout
{
    /// <summary>The character's folder name: SILENT's is silent.</summary>
    public static string Folder(string character) => character.ToLowerInvariant();

    /// <summary>eval/CHARACTER: champions/ENV.json, reports/ and leaderboard.md.</summary>
    public static string Eval(string character) => Path.Combine("eval", Folder(character));

    /// <summary>weights/CHARACTER: every weights file of the character, NAME.json, each name used once.</summary>
    public static string Weights(string character) => Path.Combine("weights", Folder(character));

    public static string Seeds(string seedSet) => Path.Combine("eval", "seeds", seedSet + ".txt");
}
