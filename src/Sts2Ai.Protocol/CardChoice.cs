namespace Sts2Ai.Protocol;

/// <summary>
/// A card-choice prompt raised while a card or power resolves (discard 1, exhaust up to 2, choose 1 of 3, ...).
/// The answer is between <see cref="Min"/> and <see cref="Max"/> distinct indexes into <see cref="Options"/>.
/// Options come in canonical order, sorted by card key, never in the game's order, which can reflect hidden state
/// such as the draw pile's order.
/// </summary>
public sealed record CardChoice
{
    public required IReadOnlyList<CardView> Options { get; init; }
    public required int Min { get; init; }
    public required int Max { get; init; }

    /// <summary>Whether <paramref name="picks"/> is a valid answer.</summary>
    public bool Accepts(IReadOnlyList<int> picks) =>
        picks.Count >= Min && picks.Count <= Max && picks.All(i => i >= 0 && i < Options.Count) && picks.Distinct().Count() == picks.Count;
}
