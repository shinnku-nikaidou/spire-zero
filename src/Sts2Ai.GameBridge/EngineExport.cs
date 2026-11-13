using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Ai.GameBridge;

/// <summary>
/// The schema of the engine's state (<c>gen/schema.json</c>, engine/DESIGN.md "State"), bound to this game: every
/// class with state by its CLR type, every field by its CLR field. It is what the port made the engine's structs from,
/// so the export that walks along it (<see cref="EngineExport"/>) and the engine name the same things.
/// </summary>
public sealed class EngineSchema
{
    internal enum Kind { Int, Bool, Float, Decimal, String, Char, Enum, Struct, Nullable, Sequence, Dict, Tuple, Pair, Type, Ref }

    /// <summary>How a value is written: its kind and the shapes inside it.</summary>
    internal sealed record Shape(Kind Kind, Shape? Of = null, Shape? Key = null, Shape? Value = null, Shape[]? Items = null, Slot[]? Fields = null);

    /// <summary>One field of a class or struct: its name in the engine, and where the game keeps it.</summary>
    internal sealed record Slot(JsonEncodedText Name, FieldInfo Field, Shape Shape);

    /// <summary>One class's own part of an object: its fields, and the type arguments the engine keeps as fields.</summary>
    internal sealed record Level(JsonEncodedText Name, Slot[] Fields, (JsonEncodedText Name, int Argument)[] TypeParameters);

    /// <summary>A class with state: the arena its objects live in, and the levels of its chain that hold something,
    /// from the root down.</summary>
    internal sealed record Class(string Name, int Arena, Level[] Chain);

    private const int Format = 1;

    private readonly Dictionary<Type, Class> _classes = [];

    /// <summary>The arenas, in the schema's order: an arena's place is what the engine numbers it by.</summary>
    internal IReadOnlyList<string> Arenas { get; }

    internal Class? Of(Type type) => _classes.GetValueOrDefault(type.IsGenericType ? type.GetGenericTypeDefinition() : type);

    /// <summary>The schema name of a class of the game; its CLR name for a class the schema does not hold.</summary>
    internal string NameOf(Type type) => Of(type)?.Name ?? type.FullName ?? type.Name;

    /// <exception cref="InvalidDataException">The schema is of another format, or names a class or a field this game does not have.</exception>
    public static EngineSchema Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return new(doc.RootElement, path);
    }

    private EngineSchema(JsonElement root, string path)
    {
        if (root.GetProperty("format").GetInt32() != Format) throw new InvalidDataException($"{path}: schema format {root.GetProperty("format")}, not {Format}");
        Arenas = [.. root.GetProperty("arenas").EnumerateArray().Select(a => a.GetString()!)];
        var arenaIndex = Enumerable.Range(0, Arenas.Count).ToDictionary(i => Arenas[i]);
        var game = typeof(CombatManager).Assembly;
        var problems = new List<string>();
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        Type? Clr(JsonElement entry)
        {
            var type = game.GetType(entry.GetProperty("cs").GetString()!);
            if (type == null) problems.Add($"no type {entry.GetProperty("cs")}");
            return type;
        }

        // Structs first: a struct's fields may hold structs, so their slots are filled once every struct is known.
        var structs = new Dictionary<string, (Type Type, JsonElement Entry, Slot[] Slots)>();
        foreach (var entry in root.GetProperty("structs").EnumerateArray())
            if (Clr(entry) is { } type)
                structs[entry.GetProperty("name").GetString()!] = (type, entry, new Slot[entry.GetProperty("fields").GetArrayLength()]);

        Shape ShapeOf(JsonElement t) => t.GetProperty("kind").GetString() switch
        {
            "int" => new(Kind.Int),
            "bool" => new(Kind.Bool),
            "float" => new(Kind.Float),
            "decimal" => new(Kind.Decimal),
            "string" => new(Kind.String),
            "char" => new(Kind.Char),
            "enum" => new(Kind.Enum),
            "kind" => new(Kind.Type),
            "ref" => new(Kind.Ref),
            "nullable" => new(Kind.Nullable, Of: ShapeOf(t.GetProperty("of"))),
            "list" or "set" or "queue" or "stack" => new(Kind.Sequence, Of: ShapeOf(t.GetProperty("of"))),
            "dict" => new(Kind.Dict, Key: ShapeOf(t.GetProperty("key")), Value: ShapeOf(t.GetProperty("value"))),
            "pair" => new(Kind.Pair, Key: ShapeOf(t.GetProperty("key")), Value: ShapeOf(t.GetProperty("value"))),
            "tuple" => new(Kind.Tuple, Items: [.. t.GetProperty("items").EnumerateArray().Select(ShapeOf)]),
            "struct" => structs.TryGetValue(t.GetProperty("name").GetString()!, out var s) ? new(Kind.Struct, Fields: s.Slots)
                : Problem<Shape>($"no struct {t.GetProperty("name")}") ?? new(Kind.Struct, Fields: []),
            var kind => throw new InvalidDataException($"{path}: unknown kind {kind}"),
        };

        T? Problem<T>(string text) where T : class
        {
            problems.Add(text);
            return null;
        }

        void Fill(Slot[] slots, Type type, JsonElement entry)
        {
            int i = 0;
            foreach (var field in entry.GetProperty("fields").EnumerateArray())
            {
                string cs = field.GetProperty("cs").GetString()!;
                var info = type.GetField(cs, Declared);
                if (info == null) problems.Add($"no field {type.FullName}.{cs}");
                else slots[i] = new(JsonEncodedText.Encode(field.GetProperty("name").GetString()!), info, ShapeOf(field.GetProperty("type")));
                i++;
            }
        }

        foreach (var (type, entry, slots) in structs.Values) Fill(slots, type, entry);

        var entries = root.GetProperty("classes").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        var levels = new Dictionary<string, Level?>();
        Level? LevelOf(string name)
        {
            if (levels.TryGetValue(name, out var known)) return known;
            var entry = entries[name];
            Level? level = null;
            if (Clr(entry) is { } type)
            {
                var slots = new Slot[entry.GetProperty("fields").GetArrayLength()];
                Fill(slots, type, entry);
                var arguments = type.IsGenericTypeDefinition ? type.GetGenericArguments() : [];
                var parameters = entry.GetProperty("type_parameters").EnumerateArray().Select(p => p.GetString()!).Select(p =>
                {
                    int at = Array.FindIndex(arguments, a => "targ_" + Snake(a.Name) == p);
                    if (at < 0) problems.Add($"no type parameter {p} on {type.FullName}");
                    return (JsonEncodedText.Encode(p), at);
                }).ToArray();
                level = new(JsonEncodedText.Encode(name), slots, parameters);
            }
            return levels[name] = level;
        }

        foreach (var (name, entry) in entries)
        {
            var chain = new List<Level>();
            for (string? at = name; at != null; at = entries[at].GetProperty("base").GetString())
                if (LevelOf(at) is { } level && (level.Fields.Length > 0 || level.TypeParameters.Length > 0)) chain.Add(level);
            chain.Reverse();
            int arena = entry.GetProperty("arena").GetString() is { } a ? arenaIndex[a] : -1;
            if (game.GetType(entry.GetProperty("cs").GetString()!) is { } type) _classes[type] = new(name, arena, [.. chain]);
        }
        if (problems.Count > 0)
            throw new InvalidDataException($"{path} does not fit this game, {problems.Distinct().Count()} problems: {string.Join("; ", problems.Distinct().Take(12))}");
    }

    /// <summary>A C# name as the port writes it for the engine: <c>TPower</c> is <c>t_power</c>.</summary>
    private static string Snake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

/// <summary>
/// A live combat written out for the engine to take in (engine/DESIGN.md "State"): every object of it once, by walking
/// from the game's singletons along the schema's fields, with its class, its fields by the engine's names, and each
/// reference as the arena and index of the object it points to. The schema bounds the walk and names every field, so
/// nothing is mapped by hand: what the schema does not list is not written.
///
/// <code>
/// {"format":1,"game":"v0.113.0",
///  "roots":{"CombatManager":["combat_manager",0],"Player":["player",0],...},
///  "arenas":{"creature":[{"class":"Creature","Creature":{"_block":0,...}}],
///            "card_model":[{"class":"Deflect","AbstractModel":{...},"CardModel":{...}}]},
///  "outside":{"MegaCrit.Sts2.Core.Random.Rng":3}}
/// </code>
///
/// An object is its class's schema name and one member per class of its chain that holds something, from the root down:
/// that class's fields by name, and its type arguments the engine keeps (<c>targ_t</c>) as class names. Objects are
/// numbered in the order the walk meets them, so the same combat gives the same bytes. Values by the schema's kinds:
/// a number for an int, an enum (its underlying value) and a char (its code unit); a string for a decimal, as .NET
/// writes it, which keeps its scale; an array for a list, set, queue or stack in its own order, and for a tuple; an
/// array of [key, value] for a dictionary; an object for a struct; null for an absent value of any of these.
/// A reference is null or a pair: ["arena", index] for an object of the export; ["canon", "Class"] for a canonical
/// model, which the engine has already and which is not written; ["outside", "CLR.Name"] for an object of a class the
/// schema does not hold, whose state the engine therefore lacks. <c>outside</c> counts those references by class.
/// </summary>
public sealed class EngineExport(EngineSchema schema)
{
    private const int Format = 1;

    /// <summary>An export: its bytes, how many objects it holds, and the references it could not follow, by class.</summary>
    public sealed record Written(byte[] Bytes, int Objects, IReadOnlyDictionary<string, int> Outside);

    /// <summary>The live combat. Its roots are where the engine's hand-written core finds the game's singletons: the
    /// combat manager, the card ids, what the run manager holds of the schema's classes, and the player.</summary>
    /// <param name="player">The player the combat is exported for.</param>
    /// <param name="game">The game's version.</param>
    public Written Write(Player player, string game)
    {
        var run = RunManager.Instance;
        var held = typeof(RunManager).GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Where(p => p.GetIndexParameters().Length == 0 && schema.Of(p.PropertyType) != null)
            .Select(p => (Name: $"RunManager.{p.Name}", Root: p.GetValue(run))).Where(r => r.Root != null).Select(r => (r.Name, r.Root!));
        return Write([("CombatManager", CombatManager.Instance), ("NetCombatCardDb", NetCombatCardDb.Instance), .. held, ("Player", player)], game);
    }

    /// <summary>Everything reached from <paramref name="roots"/>, each under its name.</summary>
    public Written Write(IEnumerable<(string Name, object Root)> roots, string game) => new Walk(schema).Write(roots, game);

    /// <summary>One export: the objects met so far, and those still to be written.</summary>
    private sealed class Walk(EngineSchema schema)
    {
        private static readonly JsonWriterOptions Plain = new() { SkipValidation = true };

        private readonly Dictionary<object, (EngineSchema.Class Class, int Index)> _seen = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<(object Object, EngineSchema.Class Class)> _toWrite = [];
        private readonly (ArrayBufferWriter<byte> Bytes, Utf8JsonWriter Json, int Count)?[] _arenas = new (ArrayBufferWriter<byte>, Utf8JsonWriter, int)?[schema.Arenas.Count];
        private readonly SortedDictionary<string, int> _outside = new(StringComparer.Ordinal);

        public Written Write(IEnumerable<(string Name, object Root)> roots, string game)
        {
            var bytes = new ArrayBufferWriter<byte>(1 << 16);
            using var json = new Utf8JsonWriter(bytes, Plain);
            json.WriteStartObject();
            json.WriteNumber("format", Format);
            json.WriteString("game", game);

            json.WriteStartObject("roots");
            foreach (var (name, root) in roots)
            {
                json.WritePropertyName(name);
                Reference(json, root);
            }
            json.WriteEndObject();

            while (_toWrite.TryDequeue(out var next)) Object(Arena(next.Class.Arena).Json, next.Object, next.Class);

            json.WriteStartObject("arenas");
            for (int i = 0; i < _arenas.Length; i++)
            {
                if (_arenas[i] is not (var arena, var objects, _)) continue;
                objects.WriteEndArray();
                objects.Flush();
                json.WritePropertyName(schema.Arenas[i]);
                json.WriteRawValue(arena.WrittenSpan, skipInputValidation: true);
                objects.Dispose();
            }
            json.WriteEndObject();

            json.WriteStartObject("outside");
            foreach (var (type, count) in _outside) json.WriteNumber(type, count);
            json.WriteEndObject();
            json.WriteEndObject();
            json.Flush();
            return new(bytes.WrittenSpan.ToArray(), _seen.Count, _outside);
        }

        private (ArrayBufferWriter<byte> Bytes, Utf8JsonWriter Json, int Count) Arena(int index)
        {
            if (_arenas[index] is { } arena) return arena;
            var bytes = new ArrayBufferWriter<byte>(1 << 12);
            var json = new Utf8JsonWriter(bytes, Plain);
            json.WriteStartArray();
            return (_arenas[index] = (bytes, json, 0)).Value;
        }

        private void Object(Utf8JsonWriter json, object o, EngineSchema.Class cls)
        {
            json.WriteStartObject();
            json.WriteString("class", cls.Name);
            foreach (var level in cls.Chain)
            {
                json.WriteStartObject(level.Name);
                foreach (var (name, argument) in level.TypeParameters) json.WriteString(name, schema.NameOf(o.GetType().GetGenericArguments()[argument]));
                Fields(json, level.Fields, o);
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }

        private void Fields(Utf8JsonWriter json, EngineSchema.Slot[] slots, object owner)
        {
            foreach (var slot in slots)
            {
                json.WritePropertyName(slot.Name);
                Value(json, slot.Shape, slot.Field.GetValue(owner));
            }
        }

        private void Value(Utf8JsonWriter json, EngineSchema.Shape shape, object? value)
        {
            if (value == null)
            {
                json.WriteNullValue();
                return;
            }
            switch (shape.Kind)
            {
                case EngineSchema.Kind.Int or EngineSchema.Kind.Enum or EngineSchema.Kind.Char:
                    if (Type.GetTypeCode(value.GetType()) == TypeCode.UInt64) json.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                    else json.WriteNumberValue(value is char c ? c : Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case EngineSchema.Kind.Bool:
                    json.WriteBooleanValue((bool)value);
                    break;
                case EngineSchema.Kind.Float:
                    double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(number)) throw new InvalidOperationException($"a {number} in the combat's state");
                    if (value is float single) json.WriteNumberValue(single);
                    else json.WriteNumberValue(number);
                    break;
                case EngineSchema.Kind.Decimal:
                    json.WriteStringValue(((decimal)value).ToString(CultureInfo.InvariantCulture));
                    break;
                case EngineSchema.Kind.String:
                    json.WriteStringValue((string)value);
                    break;
                case EngineSchema.Kind.Type:
                    json.WriteStringValue(schema.NameOf((Type)value));
                    break;
                case EngineSchema.Kind.Nullable:
                    Value(json, shape.Of!, value);
                    break;
                case EngineSchema.Kind.Struct:
                    json.WriteStartObject();
                    Fields(json, shape.Fields!, value);
                    json.WriteEndObject();
                    break;
                case EngineSchema.Kind.Sequence:
                    json.WriteStartArray();
                    foreach (var item in (IEnumerable)value) Value(json, shape.Of!, item);
                    json.WriteEndArray();
                    break;
                case EngineSchema.Kind.Dict:
                    json.WriteStartArray();
                    foreach (DictionaryEntry entry in (IDictionary)value) Pair(json, shape, entry.Key, entry.Value);
                    json.WriteEndArray();
                    break;
                case EngineSchema.Kind.Pair:
                    // A KeyValuePair has no interface that gives its key and value.
                    var pair = value.GetType();
                    Pair(json, shape, pair.GetProperty("Key")!.GetValue(value), pair.GetProperty("Value")!.GetValue(value));
                    break;
                case EngineSchema.Kind.Tuple:
                    var tuple = (ITuple)value;
                    json.WriteStartArray();
                    for (int i = 0; i < tuple.Length; i++) Value(json, shape.Items![i], tuple[i]);
                    json.WriteEndArray();
                    break;
                case EngineSchema.Kind.Ref:
                    Reference(json, value);
                    break;
            }
        }

        private void Pair(Utf8JsonWriter json, EngineSchema.Shape shape, object? key, object? value)
        {
            json.WriteStartArray();
            Value(json, shape.Key!, key);
            Value(json, shape.Value!, value);
            json.WriteEndArray();
        }

        private void Reference(Utf8JsonWriter json, object target)
        {
            var type = target.GetType();
            var cls = schema.Of(type);
            json.WriteStartArray();
            if (cls == null)
            {
                string name = type.FullName ?? type.Name;
                _outside[name] = _outside.GetValueOrDefault(name) + 1;
                json.WriteStringValue("outside");
                json.WriteStringValue(name);
            }
            else if (target is AbstractModel { IsMutable: false })
            {
                json.WriteStringValue("canon");
                json.WriteStringValue(cls.Name);
            }
            else
            {
                if (cls.Arena < 0) throw new InvalidOperationException($"an object of {cls.Name}, which has no arena in the schema");
                if (!_seen.TryGetValue(target, out var known))
                {
                    var arena = Arena(cls.Arena);
                    _seen[target] = known = (cls, arena.Count);
                    _arenas[cls.Arena] = arena with { Count = arena.Count + 1 };
                    _toWrite.Enqueue((target, cls));
                }
                json.WriteStringValue(schema.Arenas[known.Class.Arena]);
                json.WriteNumberValue(known.Index);
            }
            json.WriteEndArray();
        }
    }
}
