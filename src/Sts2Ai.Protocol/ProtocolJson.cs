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

    /// <summary>The obs_hash: FNV-1a 64 over the UTF-8 of the observation's JSON.</summary>
    public static ulong Hash<T>(T observation)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in JsonSerializer.SerializeToUtf8Bytes(observation, Options)) hash = (hash ^ b) * 1099511628211;
        return hash;
    }

    /// <summary>The card's identity as the player sees it: its serialization without the uid.</summary>
    public static string CardKey(CardView card) => Serialize(card.Uid == null ? card : card with { Uid = null });
}
