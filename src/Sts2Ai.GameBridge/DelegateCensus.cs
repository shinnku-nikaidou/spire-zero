using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace Sts2Ai.GameBridge;

/// <summary>
/// A census of the delegates a combat's state holds (engine/DESIGN.md has delegates as state): for every delegate in a
/// field of an object that <see cref="EngineExport"/>'s walk visits, the field, the method, what kind of method it is,
/// what it is bound to, and for a closure what it captured. And one comparison (<see cref="Compare"/>): a monster's
/// machine and a card's vars made again by the game's own code, held against the live ones.
/// Rows are tab-separated texts with a count, so that the censuses of a sweep's parts add up line by line.
/// </summary>
public sealed class DelegateCensus(EngineSchema schema)
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private readonly Dictionary<Type, FieldInfo[]> _holders = [];
    private readonly Dictionary<object, List<string>> _closures = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every delegate met, by its row: "found", the field (an event's with "event" before it), the method's
    /// class, the method, its kind, its target, a closure's captures, and whether the field's delegate is one or many.</summary>
    public SortedDictionary<string, long> Rows { get; } = new(StringComparer.Ordinal);

    private void Count(string row) => Rows[row] = Rows.GetValueOrDefault(row) + 1;

    /// <summary>The delegates in the fields that <paramref name="level"/> declares, of an object or a struct of the walk.</summary>
    /// <param name="name">The class's schema name, which the rows name the field by.</param>
    public void Note(string name, Type level, object owner)
    {
        if (!_holders.TryGetValue(level, out var fields))
            _holders[level] = fields = [.. level.GetFields(Declared).Where(f => Holds(f.FieldType))];
        foreach (var field in fields)
            switch (field.GetValue(owner))
            {
                case null:
                    break;
                case Delegate held:
                    Found((level.GetEvent(field.Name, Declared) != null ? "event " : "") + $"{name}.{field.Name}", held);
                    break;
                case IDictionary dictionary:
                    foreach (var value in dictionary.Values.OfType<Delegate>()) Found($"{name}.{field.Name}{{}}", value);
                    break;
                case IEnumerable items:
                    foreach (var item in items.OfType<Delegate>()) Found($"{name}.{field.Name}[]", item);
                    break;
                case var closure when IsClosure(closure.GetType()):
                    Count($"held\t{name}.{field.Name}\t{closure.GetType().FullName}");
                    break;
            }
    }

    /// <summary>A field that can hold a delegate, delegates, or a closure object itself.</summary>
    private static bool Holds(Type type) => typeof(Delegate).IsAssignableFrom(type) || IsClosure(type)
        || (type.IsGenericType && type.GetGenericArguments().Any(a => typeof(Delegate).IsAssignableFrom(a)));

    private void Found(string field, Delegate held)
    {
        var parts = held.GetInvocationList();
        foreach (var part in parts)
        {
            var (method, target) = (part.Method, part.Target);
            string kind = method.Name.Contains(">b__") ? "lambda" : method.Name.Contains(">g__") ? "local function" : "method";
            string bound = target switch
            {
                null => "static",
                _ when target.GetType().Name == "<>c" => "<>c",
                _ when IsClosure(target.GetType()) => $"closure {target.GetType().FullName}",
                _ when schema.Of(target.GetType()) is { } cls => $"object {cls.Name}",
                _ => $"other {target.GetType().FullName}",
            };
            string captures = target != null && IsClosure(target.GetType()) ? string.Join("; ", Captures(target)) : "";
            Count($"found\t{field}\t{method.DeclaringType?.FullName}\t{method.Name}\t{kind}\t{bound}\t{captures}\t{(parts.Length == 1 ? "one" : "many")}");
            if (target == null || !IsClosure(target.GetType())) continue;
            if (!_closures.TryGetValue(target, out var users)) _closures[target] = users = [];
            users.Add($"{field} {method.Name}");
        }
    }

    /// <summary>A closure's fields: the name, the declared type, and what the value is.</summary>
    private IEnumerable<string> Captures(object closure) => closure.GetType().GetFields(Declared).OrderBy(f => f.Name, StringComparer.Ordinal).Select(f =>
        $"{f.Name}: {Short(f.FieldType)} = " + f.GetValue(closure) switch
        {
            null => "null",
            Delegate => "delegate",
            var v when IsClosure(v.GetType()) => "outer closure",
            var v when schema.Of(v.GetType()) is { } cls => $"object {cls.Name}",
            string or decimal => "value",
            var v when v.GetType().IsPrimitive || v.GetType().IsEnum => "value",
            IEnumerable => "collection",
            var v when v.GetType().IsValueType => $"struct {Short(v.GetType())}",
            var v => $"other {v.GetType().FullName}",
        });

    private static string Short(Type type) => type.IsGenericType ? $"{type.Name.Split('`')[0]}<{string.Join(",", type.GetGenericArguments().Select(Short))}>" : type.Name;

    /// <summary>A class the compiler made for captured locals.</summary>
    private static bool IsClosure(Type type) => type.Name.Contains("DisplayClass") && type.IsDefined(typeof(CompilerGeneratedAttribute), false);

    /// <summary>Ends one export's walk: a closure that more than one of its delegates is bound to is counted, by who shares it.</summary>
    public void EndExport()
    {
        foreach (var (closure, users) in _closures)
            if (users.Count > 1) Count($"shared\t{closure.GetType().FullName}\t{users.Count} delegates\t{string.Join(" | ", users.Order(StringComparer.Ordinal))}");
        _closures.Clear();
    }

    /// <summary>
    /// What an import does today, done in the game: every monster's machine made again by its own
    /// <c>GenerateMoveStateMachine</c>, every card's vars by its <c>CanonicalVars</c>, and each delegate of the live
    /// ones held against the one made again in its place: the same method, and the same captured values. It runs the
    /// game's code on the live objects, so it is for a combat that is thrown away after.
    /// </summary>
    public void Compare(CombatState state, Player player)
    {
        foreach (var monster in state.Creatures.Select(c => c.Monster).OfType<MonsterModel>().Where(m => m.IsMutable && m.MoveStateMachine != null))
        {
            var generate = typeof(MonsterModel).GetMethod("GenerateMoveStateMachine", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var live = Machine(monster.MoveStateMachine!);
            // The next move can be one made on the spot (a stun), which no machine holds.
            if (monster.NextMove is { } next && !monster.MoveStateMachine!.States.Values.Contains(next))
                foreach (var (path, held) in In($"next move {next.Id}", next, 0)) live[path] = held;
            Dictionary<string, Delegate> fresh;
            try
            {
                fresh = Machine((MonsterMoveStateMachine)generate.Invoke(monster, null)!);
            }
            catch (TargetInvocationException e)
            {
                Count($"compared\t{monster.GetType().Name}\tthe machine cannot be made again: {e.InnerException?.GetType().Name}");
                continue;
            }
            Against(monster.GetType().Name, live, fresh);
        }
        var vars = typeof(CardModel).GetProperty("CanonicalVars", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var card in player.PlayerCombatState!.AllCards)
            Against(card.GetType().Name, Vars(card.DynamicVars), Vars(new DynamicVarSet((IEnumerable<DynamicVar>)vars.GetValue(card)!)));
    }

    private void Against(string owner, Dictionary<string, Delegate> live, Dictionary<string, Delegate> fresh)
    {
        foreach (var (path, held) in live)
        {
            if (!fresh.TryGetValue(path, out var again))
            {
                Count($"compared\t{owner}\t{Generic(path)}\tnot made again: {held.Method.DeclaringType?.Name}.{held.Method.Name}");
                continue;
            }
            var differences = Differences(held, again, "").Distinct().ToList();
            if (differences.Count == 0) Count($"compared\t{owner}\t{Generic(path)}\tsame");
            foreach (string difference in differences) Count($"compared\t{owner}\t{Generic(path)}\t{difference}");
        }
        foreach (string path in fresh.Keys.Where(p => !live.ContainsKey(p))) Count($"compared\t{owner}\t{Generic(path)}\tmade again and not in the live one");
    }

    /// <summary>A path with its list indexes left out, so that rows of the same place add up.</summary>
    private static string Generic(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[]");

    private static Dictionary<string, Delegate> Machine(MonsterMoveStateMachine machine) =>
        machine.States.OrderBy(s => s.Key, StringComparer.Ordinal).SelectMany(s => In(s.Key, s.Value, 0)).ToDictionary(p => p.Path, p => p.Held);

    private static Dictionary<string, Delegate> Vars(DynamicVarSet vars) =>
        vars.OrderBy(v => v.Key, StringComparer.Ordinal).SelectMany(v => In(v.Key, v.Value, 0)).ToDictionary(p => p.Path, p => p.Held);

    /// <summary>The delegates in and under an object of a monster's machine or of a card's vars, each with its path.</summary>
    private static IEnumerable<(string Path, Delegate Held)> In(string path, object owner, int depth)
    {
        if (depth > 4) yield break;
        for (var type = owner.GetType(); type != null && type != typeof(object); type = type.BaseType)
            foreach (var field in type.GetFields(Declared))
            {
                object? value = field.GetValue(owner);
                if (value is Delegate held) yield return ($"{path}.{field.Name}", held);
                else if (value is IEnumerable items and not string)
                {
                    int i = 0;
                    foreach (object? item in items)
                    {
                        if (item is Delegate one) yield return ($"{path}.{field.Name}[{i}]", one);
                        else if (item != null && Part(item.GetType()))
                            foreach (var inner in In($"{path}.{field.Name}[{i}]", item, depth + 1)) yield return inner;
                        i++;
                    }
                }
            }
    }

    /// <summary>A part of a machine or of a var that the walk goes into: a branch, a weight, an intent.</summary>
    private static bool Part(Type type) => type.Namespace is { } space && (space.Contains("MonsterMoves") || space.Contains("DynamicVars")) && !typeof(MonsterState).IsAssignableFrom(type);

    private static IEnumerable<string> Differences(Delegate live, Delegate fresh, string at)
    {
        if (live.Method != fresh.Method)
        {
            yield return $"{at}another method: live {live.Method.DeclaringType?.Name}.{live.Method.Name}, made again {fresh.Method.DeclaringType?.Name}.{fresh.Method.Name}";
            yield break;
        }
        foreach (string difference in Values(live.Target, fresh.Target, at + "target", 0)) yield return difference;
    }

    private static IEnumerable<string> Values(object? live, object? fresh, string at, int depth)
    {
        if (ReferenceEquals(live, fresh) || depth > 4) yield break;
        if (live == null || fresh == null || live.GetType() != fresh.GetType())
        {
            yield return $"{at}: live {live?.GetType().Name ?? "null"}, made again {fresh?.GetType().Name ?? "null"}";
            yield break;
        }
        switch (live)
        {
            case Delegate held:
                foreach (string difference in Differences(held, (Delegate)fresh, at + " -> ")) yield return difference;
                break;
            case MonsterState state:
                // The machine made again has its own states: the same place in it is the same state.
                if (state.Id != ((MonsterState)fresh).Id) yield return $"{at}: live state {state.Id}, made again {((MonsterState)fresh).Id}";
                break;
            case var closure when IsClosure(closure.GetType()):
                foreach (var field in closure.GetType().GetFields(Declared))
                    foreach (string difference in Values(field.GetValue(live), field.GetValue(fresh), $"{at}.{field.Name}", depth + 1)) yield return difference;
                break;
            case string or decimal:
            case var v when v.GetType().IsPrimitive || v.GetType().IsEnum:
                if (!live.Equals(fresh)) yield return $"{at}: live {live}, made again {fresh}";
                break;
            case IEnumerable items:
                var (a, b) = (items.Cast<object?>().ToList(), ((IEnumerable)fresh).Cast<object?>().ToList());
                if (a.Count != b.Count) yield return $"{at}: live {a.Count} items, made again {b.Count}";
                else
                    for (int i = 0; i < a.Count; i++)
                        foreach (string difference in Values(a[i], b[i], $"{at}[]", depth + 1)) yield return difference;
                break;
            default:
                yield return $"{at}: another {live.GetType().Name}";
                break;
        }
    }

    public void Write(string path) => File.WriteAllLines(path, Rows.Select(r => $"{r.Value}\t{r.Key}"));

    /// <summary>The censuses of a sweep's parts, added up.</summary>
    public void Add(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            int tab = line.IndexOf('\t');
            Rows[line[(tab + 1)..]] = Rows.GetValueOrDefault(line[(tab + 1)..]) + long.Parse(line[..tab]);
        }
    }
}
