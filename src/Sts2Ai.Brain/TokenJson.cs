using System.Text.Json;

namespace Sts2Ai.Brain;

/// <summary>The pieces a net's tokens are written to JSON with, for the trainer.</summary>
public static class TokenJson
{
    /// <summary>A JSON array of strings.</summary>
    public static void Strings(Utf8JsonWriter json, IEnumerable<string> values)
    {
        json.WriteStartArray();
        foreach (var v in values) json.WriteStringValue(v);
        json.WriteEndArray();
    }

    /// <summary>A JSON array of numbers.</summary>
    public static void Numbers(Utf8JsonWriter json, IEnumerable<double> values)
    {
        json.WriteStartArray();
        foreach (double v in values) json.WriteNumberValue(v);
        json.WriteEndArray();
    }

    /// <summary>A JSON array of arrays of numbers.</summary>
    public static void Rows(Utf8JsonWriter json, IEnumerable<IReadOnlyList<double>> rows)
    {
        json.WriteStartArray();
        foreach (var row in rows) Numbers(json, row);
        json.WriteEndArray();
    }
}
