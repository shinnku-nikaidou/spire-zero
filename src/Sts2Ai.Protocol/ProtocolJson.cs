using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Ai.Protocol;

/// <summary>The wire format: snake_case names, absent fields left out.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"null {typeof(T).Name}");

    /// <summary>A value from its JSON as UTF-8.</summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"null {typeof(T).Name}");

    /// <summary>The obs_hash: FNV-1a 64 over the UTF-8 of the observation's JSON.</summary>
    public static ulong Hash<T>(T observation) => HashOfJson(JsonSerializer.SerializeToUtf8Bytes(observation, Options));

    /// <summary>The obs_hash of an observation from its JSON as UTF-8, as this class writes it.</summary>
    public static ulong HashOfJson(ReadOnlySpan<byte> json)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in json) hash = (hash ^ b) * 1099511628211;
        return hash;
    }

    /// <summary>
    /// A state as search tells states apart, with the world's hidden key beside it: the
    /// observation with the hand, the discard and the exhaust pile each as a multiset of cards, with no uids, and the
    /// potions as a multiset, so the same cards played in another order, or another copy of one, meet as one state. The
    /// draw pile is one already; its order is in the hidden key. A prompt it waits on is part of it, its options with no
    /// uids either.
    /// </summary>
    public static ulong StateKey(CombatObservation obs) => Hash(obs with
    {
        Hand = Cards(obs.Hand),
        Discard = Cards(obs.Discard),
        Exhaust = Cards(obs.Exhaust),
        Potions = [.. obs.Potions.OrderBy(p => p == null).ThenBy(p => p, StringComparer.Ordinal)],
        Prompt = obs.Prompt is { } prompt ? prompt with { Options = [.. prompt.Options.Select(c => c.Uid == null ? c : c with { Uid = null })] } : null,
    });

    private static List<CardView> Cards(IReadOnlyList<CardView> cards) =>
        [.. cards.Select(c => c.Uid == null ? c : c with { Uid = null }).OrderBy(CardKey, StringComparer.Ordinal)];

    /// <summary>The card's identity as the player sees it: its serialization without the uid.</summary>
    public static string CardKey(CardView card) => Serialize(card.Uid == null ? card : card with { Uid = null });
}
