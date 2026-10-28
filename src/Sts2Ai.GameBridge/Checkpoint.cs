using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Ai.GameBridge;

/// <summary>
/// A node's SL checkpoint (docs: simulator §7): the game's save on arrival at the map point, and each relic's plain
/// fields (numbers, flags and enums, its status among them). The save keeps only the fields a relic marks as saved, so
/// without them a relic that changes its state between combats (Lava Rock disables itself) loads differently from the
/// run it came from. <see cref="RestoreRelics"/> puts them back once the save is loaded.
/// </summary>
public static class Checkpoint
{
    private sealed record Data(string Save, IReadOnlyList<RelicFields> Relics);

    private sealed record RelicFields(string Id, Dictionary<string, string> Fields);

    /// <summary>The checkpoint of the run as it is now.</summary>
    public static string Take(Player player) => JsonSerializer.Serialize(new Data(SaveManager.ToJson(RunManager.Instance.ToSave(null)),
        player.Relics.Select(r => new RelicFields(r.Id.Entry, Plain(r).ToDictionary(Key, f => Format(f.GetValue(r)!)))).ToList()));

    /// <summary>The game's save in <paramref name="checkpoint"/>, to load as the game's Continue loads one.</summary>
    public static string Save(string checkpoint) => Read(checkpoint).Save;

    /// <summary>Gives each relic of the run loaded from <paramref name="checkpoint"/> the plain fields it had in the run.</summary>
    public static void RestoreRelics(string checkpoint, Player player)
    {
        var relics = Read(checkpoint).Relics;
        if (!relics.Select(r => r.Id).SequenceEqual(player.Relics.Select(r => r.Id.Entry)))
            throw new InvalidOperationException("the loaded run's relics are not the checkpoint's");
        foreach (var (saved, relic) in relics.Zip(player.Relics))
            foreach (var field in Plain(relic))
                field.SetValue(relic, Parse(saved.Fields[Key(field)], field.FieldType));
    }

    private static Data Read(string checkpoint) =>
        JsonSerializer.Deserialize<Data>(checkpoint) ?? throw new InvalidDataException("not a node checkpoint");

    /// <summary>The relic's writable fields of a number, flag or enum type, from its own class up to RelicModel.</summary>
    private static IEnumerable<FieldInfo> Plain(RelicModel relic)
    {
        for (var type = relic.GetType(); type != typeof(AbstractModel); type = type.BaseType!)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!field.IsInitOnly && (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(decimal)))
                    yield return field;
    }

    private static string Key(FieldInfo field) => $"{field.DeclaringType!.Name}.{field.Name}";

    private static string Format(object value) => value switch
    {
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable and not Enum => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    private static object Parse(string text, Type type) =>
        type.IsEnum ? Enum.Parse(type, text) : Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
}
