namespace Sts2Ai.SimWorker;

/// <summary>
/// Where the files go, from the repository root (docs: learning/reference §6, evaluation/champions §5): each character is
/// judged on its own, so its reports have a folder of its own; the combat net and V are each one net for every
/// character and ascension, so their weights share one folder, and so do the champion files. The seed sets are shared.
/// </summary>
internal static class Layout
{
    /// <summary>The character's folder name: SILENT's is silent.</summary>
    public static string Folder(string character) => character.ToLowerInvariant();

    /// <summary>eval/CHARACTER: reports/ and leaderboard.md.</summary>
    public static string Eval(string character) => Path.Combine("eval", Folder(character));

    /// <summary>eval/champions: ENV.json, each environment's champion, every character's.</summary>
    public static string Champions => Path.Combine("eval", "champions");

    /// <summary>weights/all: every weights file the champions may play, NAME.json, each name used once.</summary>
    public static string Weights => Path.Combine("weights", "all");

    public static string Seeds(string seedSet) => Path.Combine("eval", "seeds", seedSet + ".txt");
}
