using System.Globalization;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Ai.GameBridge;

/// <summary>
/// An SL checkpoint: one of the game's saves (on arrival at a map point, or after a room's fight or ancient), and each relic's plain
/// fields (numbers, flags and enums, its status among them). The save keeps only the fields a relic marks as saved, so
/// without them a relic that changes its state between combats (Lava Rock disables itself) loads differently from the
/// run it came from. <see cref="RestoreRelics"/> puts them back once the save is loaded.
/// </summary>
public static class Checkpoint
{
    /// <summary>A checkpoint read (<see cref="Open"/>): the game's save, to load as the game's Continue loads one, and the
    /// relics' plain fields.</summary>
    public sealed record Opened(string Save, IReadOnlyList<RelicFields> Relics, string? Dominion);

    public sealed record RelicFields(string Id, Dictionary<string, string> Fields);

    /// <summary>
    /// The checkpoint of the run as it is now, saved as the game saves it: on arrival at a map point with no room, after a
    /// won fight or a finished ancient with that room, which a load then enters as finished (<c>SaveManager.SaveRun</c>).
    /// </summary>
    public static string Take(Player player, AbstractRoom? preFinishedRoom = null) => Of(RunManager.Instance.ToSave(preFinishedRoom), player);

    /// <summary>The checkpoint of <paramref name="save"/>, a save of the run as it is now (one the game just wrote and
    /// read back), with <paramref name="player"/>'s relics.</summary>
    public static string Of(SerializableRun save, Player player) => JsonSerializer.Serialize(new Opened(
        SaveManager.ToJson(save),
        [.. player.Relics.Select(r => new RelicFields(r.Id.Entry, Plain(r).ToDictionary(Key, f => Format(f.GetValue(r)!))))],
        player.RunState.Dominion?.Id.Entry));

    /// <summary>Reads <paramref name="checkpoint"/>, once for every load of it.</summary>
    public static Opened Open(string checkpoint) =>
        JsonSerializer.Deserialize<Opened>(checkpoint) ?? throw new InvalidDataException("not a node checkpoint");

    /// <summary>Gives each relic of the run loaded from <paramref name="checkpoint"/> the plain fields it had in the run.</summary>
    public static void RestoreRelics(Opened checkpoint, Player player)
    {
        var relics = checkpoint.Relics;
        if (!relics.Select(r => r.Id).SequenceEqual(player.Relics.Select(r => r.Id.Entry)))
            throw new InvalidOperationException("the loaded run's relics are not the checkpoint's");
        foreach (var (saved, relic) in relics.Zip(player.Relics))
            foreach (var field in Plain(relic))
                field.SetValue(relic, Parse(saved.Fields[Key(field)], field.FieldType));
    }

    /// <summary>
    /// Takes the Dread mode's dominion off the run loaded from <paramref name="checkpoint"/> where the run it came from had
    /// none: entering the act's boss room takes it off (DreadRunModel.RemoveDominionIfRoomIsBoss), but a load gives the
    /// act's back (AfterRunLoaded), so the clone of a checkpoint after the boss's fight read a dominion the run no longer
    /// had, and no state of its afterstates was ever the decision's (a card reward skipped there was opened again, 2026-11-23).
    /// </summary>
    public static void RestoreDominion(Opened checkpoint, RunState run)
    {
        string? loaded = run.Dominion?.Id.Entry;
        if (loaded == checkpoint.Dominion) return;
        if (checkpoint.Dominion != null)
            throw new InvalidOperationException($"the loaded run's dominion {loaded ?? "none"} is not the checkpoint's {checkpoint.Dominion}");
        run.SetDominion(null);
    }

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
