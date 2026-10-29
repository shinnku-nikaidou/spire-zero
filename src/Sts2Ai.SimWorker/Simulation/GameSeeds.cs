namespace Sts2Ai.SimWorker;

/// <summary>The seeds the sim's combats and runs are played from: the game's own kind, made from a base and an index.</summary>
internal static class GameSeeds
{
    /// <summary>A game-style seed string (the game's alphabet, 12 chars), derived deterministically from (seed, index).</summary>
    public static string MakeSeed(int seed, int index)
    {
        const string alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        var rng = new Random(unchecked(seed * 1_000_003 + index * 7_919) ^ 0x5eed);
        return string.Concat(Enumerable.Range(0, 12).Select(_ => alphabet[rng.Next(alphabet.Length)]));
    }
}
