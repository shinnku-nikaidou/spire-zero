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

    /// <summary>The card's identity as the player sees it: its serialization without the uid.</summary>
    public static string CardKey(CardView card) => Serialize(card.Uid == null ? card : card with { Uid = null });
}
