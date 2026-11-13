using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.CSharp;

namespace Sts2Ai.Port;

public enum Cat { Translated, Manual, Stub, Dropped }

/// <summary>The expression touches presentation: the statement around it is left out.</summary>
public sealed class Dropped(string what) : Exception(what);

/// <summary>The translator cannot translate this: the member's body becomes a todo! and the reason is reported.</summary>
public sealed class Unsupported(string why) : Exception(why);

/// <summary>The game's symbols as the translator sees them: which class is what, the Rust name and type of everything.</summary>
public sealed class World
{
    public readonly CSharpCompilation Comp;
    public readonly Config Cfg;
    public readonly Report Rep = new();
    public const string Core = "MegaCrit.Sts2.Core.";

    private static readonly SymbolEqualityComparer Eq = SymbolEqualityComparer.Default;
    private readonly Dictionary<INamedTypeSymbol, string> _unique = new(Eq);
    private readonly ConcurrentDictionary<INamedTypeSymbol, Cat> _cat = new(Eq);
    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _droppedNamed = new(Eq);
    private readonly ConcurrentDictionary<ISymbol, string> _memberName = new(Eq);
    /// <summary>Classes and interfaces that have a kind, in the order they were first seen.</summary>
    public readonly ConcurrentDictionary<INamedTypeSymbol, int> Kinds = new(Eq);
    /// <summary>Members of stub classes that translated code refers to.</summary>
    public readonly ConcurrentDictionary<ISymbol, byte> StubRefs = new(Eq);
    /// <summary>Members written by hand that translated code refers to.</summary>
    public readonly ConcurrentDictionary<ISymbol, byte> ManualRefs = new(Eq);
    /// <summary>Virtual slots (the first declaration of a virtual member) that are called or overridden.</summary>
    public readonly ConcurrentDictionary<ISymbol, byte> Slots = new(Eq);
    /// <summary>Enums and structs that are referred to.</summary>
    public readonly ConcurrentDictionary<INamedTypeSymbol, byte> Enums = new(Eq), Structs = new(Eq);
    /// <summary>Methods whose whole body is presentation: a call to one is presentation too. Found by translating, used by the next pass.</summary>
    public HashSet<ISymbol> AutoDropped = new(Eq);
    public List<INamedTypeSymbol> AllTypes = [];

    public World(CSharpCompilation comp, Config cfg)
    {
        Comp = comp;
        Cfg = cfg;
        Collect(comp.Assembly.GlobalNamespace);
        AssignUniqueNames();
        foreach (var t in AllTypes)
            if (t.TypeKind is TypeKind.Class or TypeKind.Interface && !IsValue(t) && CatOf(t) is Cat.Translated or Cat.Manual) KindOf(t);
    }

    private void Collect(INamespaceOrTypeSymbol ns)
    {
        foreach (var m in ns.GetMembers())
        {
            if (m is INamespaceSymbol n) Collect(n);
            else if (m is INamedTypeSymbol t)
            {
                AllTypes.Add(t);
                Collect(t);
            }
        }
    }

    // ---------------------------------------------------------------- classification

    public static bool IsSource(ISymbol s) => s.Locations.Any(l => l.IsInSource);

    /// <summary>A C# struct, or a class port.toml says is a value: held by value in Rust, compared by content.</summary>
    public bool IsValue(ITypeSymbol? t) => t is INamedTypeSymbol n && IsSource(n) && (n.TypeKind == TypeKind.Struct || (n.TypeKind == TypeKind.Class && Cfg.ValueClasses.Contains(n.Name)));

    public static string FullName(INamedTypeSymbol t)
    {
        var parts = new List<string>();
        for (INamedTypeSymbol? x = t; x != null; x = x.ContainingType) parts.Insert(0, x.MetadataName);
        string ns = t.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        return ns + string.Join("+", parts);
    }

    public static string Namespace(INamedTypeSymbol t) => t.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : "";

    /// <summary>Library types the translator maps itself although their namespace is presentation.</summary>
    private static readonly HashSet<string> LibraryKept = ["Godot.Mathf"];

    public bool IsDroppedNamed(INamedTypeSymbol t0)
    {
        var t = t0.OriginalDefinition;
        return _droppedNamed.GetOrAdd(t, static (t, self) =>
        {
            string full = FullName(t);
            if (self.Cfg.DropTypes.Contains(full) || self.Cfg.DropTypes.Any(d => d.EndsWith('*') && full.StartsWith(d[..^1], StringComparison.Ordinal))) return true;
            if (t.ContainingType != null) return self.IsDroppedNamed(t.ContainingType);
            // A type the scope names itself is translated, whatever its namespace.
            if (self.Cfg.Translate.Contains(full)) return false;
            string ns = Namespace(t);
            if (!IsSource(t))
            {
                if (LibraryKept.Contains(full)) return false;
                return !(ns == "System" || ns.StartsWith("System."));
            }
            foreach (var keep in self.Cfg.KeepNamespaces)
                if (ns == keep || ns.StartsWith(keep + ".")) return false;
            foreach (var d in self.Cfg.DropNamespaces)
                if (ns == d || ns.StartsWith(d + ".")) return true;
            return false;
        }, this);
    }

    /// <summary>Whether the type, or a type inside it, is presentation.</summary>
    public bool IsDropped(ITypeSymbol? t) => t switch
    {
        null => false,
        IArrayTypeSymbol a => IsDropped(a.ElementType),
        IPointerTypeSymbol or IFunctionPointerTypeSymbol => true,
        INamedTypeSymbol n => IsDroppedNamed(n) || n.TypeArguments.Any(IsDropped),
        _ => false,
    };

    public Cat CatOf(INamedTypeSymbol t0)
    {
        var t = t0.OriginalDefinition;
        return _cat.GetOrAdd(t, static (t, self) =>
        {
            if (self.IsDroppedNamed(t)) return Cat.Dropped;
            if (!IsSource(t)) return Cat.Stub;
            if (self.Cfg.ManualClasses.Contains(self.Unique(t))) return Cat.Manual;
            if (t.ContainingType != null && t.TypeKind != TypeKind.Enum) return self.CatOf(t.ContainingType) is Cat.Translated or Cat.Manual ? Cat.Translated : Cat.Stub;
            string full = FullName(t);
            if (self.Cfg.Exclude.Contains(full)) return Cat.Stub;
            if (!(self.Cfg.Translate.Contains(Namespace(t)) || self.Cfg.Translate.Contains(full))) return Cat.Stub;
            // A class generic over plain values has no single state layout: it stays a stub until written by hand.
            if (t.TypeKind == TypeKind.Class && !t.IsStatic && !self.IsValue(t) && t.TypeParameters.Any(p => !self.Erased(p))) return Cat.Stub;
            return Cat.Translated;
        }, this);
    }

    public bool IsManualMember(ISymbol m)
    {
        if (m.ContainingType == null) return false;
        var owner = m is IMethodSymbol { AssociatedSymbol: { } a } ? a : m;
        return Cfg.ManualMembers.Contains(Unique(m.ContainingType) + "." + owner.Name);
    }

    public bool IsDropMember(ISymbol m)
    {
        if (m.ContainingType == null) return false;
        var owner = m is IMethodSymbol { AssociatedSymbol: { } a } ? a : m;
        var t = m.ContainingType.OriginalDefinition;
        if (Cfg.KeepMembers.Contains(t.Name + "." + owner.Name)) return false;
        return Cfg.DropMembers.Contains(t.Name + "." + owner.Name) || Cfg.DropMembers.Contains(t.Name + ".*") || AutoDropped.Contains(m.OriginalDefinition);
    }

    /// <summary>Where a member's code is: translated, written by hand, or not there yet.</summary>
    public Cat CatOfMember(ISymbol m)
    {
        var c = CatOf(m.ContainingType);
        if (c == Cat.Translated && IsManualMember(m)) return Cat.Manual;
        return c;
    }

    /// <summary>A type parameter that stands for a game class: values of it are handles, the parameter itself is passed as a Kind.</summary>
    public bool Erased(ITypeParameterSymbol p) =>
        p.ConstraintTypes.Any(c => c is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface } n && IsSource(n) && !IsDroppedNamed(n) && !IsValue(n))
        || p.HasReferenceTypeConstraint && p.ConstraintTypes.Length == 0 && false;

    // ---------------------------------------------------------------- names

    private static readonly HashSet<string> ReservedTypeNames =
    [
        "Obj", "Str", "Dec", "Kind", "Combat", "R", "Stop", "Vec", "Set", "Dict", "Queue", "Stack", "Option", "Some", "None", "Ok", "Err", "Result", "Box",
        "String", "Arenas", "Arena", "KeyValuePair", "Fn0", "Fn1", "Fn2", "Fn3", "Fn4", "Fn5", "Default", "Clone", "Copy", "Self", "Ordered", "Group", "Val",
        "MidpointRounding", "StringComparison", "Exception", "Send", "Sync", "Sized", "Drop", "Iterator", "Eq", "Ord", "Hash", "Debug",
    ];

    private void AssignUniqueNames()
    {
        static string Simple(INamedTypeSymbol t)
        {
            var sb = new StringBuilder();
            for (INamedTypeSymbol? x = t; x != null; x = x.ContainingType) sb.Insert(0, Clean(x.Name));
            return sb.ToString();
        }
        var live = AllTypes.Where(t => !IsDroppedNamed(t) && IsSource(t)).ToList();
        foreach (var g in live.GroupBy(Simple))
        {
            var list = g.OrderBy(FullName, StringComparer.Ordinal).ToList();
            if (list.Count == 1 && !ReservedTypeNames.Contains(g.Key)) { _unique[list[0]] = g.Key; continue; }
            foreach (var t in list)
            {
                string ns = Namespace(t);
                string last = ns.Length == 0 ? "Global" : Clean(ns[(ns.LastIndexOf('.') + 1)..]);
                string name = list.Count == 1 ? g.Key + "Cs" : last + g.Key + (t.Arity > 0 && list.Count(x => Namespace(x) == ns) > 1 ? t.Arity.ToString() : "");
                _unique[t] = name;
            }
        }
        foreach (var dup in _unique.GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
            foreach (var kv in dup.Skip(1)) _unique[kv.Key] = Clean(FullName(kv.Key).Replace(".", "").Replace("+", "").Replace("`", ""));
    }

    public static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s) if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>The Rust name of a game type: its own name, made unique.</summary>
    public string Unique(INamedTypeSymbol t)
    {
        t = t.OriginalDefinition;
        return _unique.TryGetValue(t, out var n) ? n : Clean(t.Name);
    }

    private static readonly HashSet<string> Keywords =
    [
        "as", "break", "const", "continue", "crate", "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod", "move",
        "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super", "trait", "true", "type", "unsafe", "use", "where", "while", "async", "await",
        "dyn", "abstract", "become", "box", "do", "final", "macro", "override", "priv", "typeof", "unsized", "virtual", "yield", "try", "gen", "c", "me", "g", "m",
    ];

    public static string Snake(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (!char.IsLetterOrDigit(ch) && ch != '_') continue;
            if (char.IsUpper(ch))
            {
                bool prevLower = i > 0 && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]));
                bool acronymEnd = i > 0 && char.IsUpper(s[i - 1]) && i + 1 < s.Length && char.IsLower(s[i + 1]);
                if ((prevLower || acronymEnd) && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
                sb.Append(char.ToLowerInvariant(ch));
            }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string Ident(string s)
    {
        string n = Snake(s);
        if (n.Length == 0 || char.IsDigit(n[0])) n = "_" + n;
        return Keywords.Contains(n) ? n + "_" : n;
    }

    public string Module(INamedTypeSymbol t) => Ident(Unique(t));

    public static string Area(INamedTypeSymbol t)
    {
        string ns = Namespace(t);
        if (ns.StartsWith(Core)) ns = ns[Core.Length..];
        else if (ns == "MegaCrit.Sts2.Core") ns = "core";
        return ns.Length == 0 ? "global" : Snake(ns.Replace(".", "_")).ToLowerInvariant();
    }

    /// <summary>The module that holds a class's functions.</summary>
    public string Path(INamedTypeSymbol t, ISymbol? member = null)
    {
        t = t.OriginalDefinition;
        var cat = member != null ? CatOfMember(member) : CatOf(t);
        return cat switch
        {
            Cat.Translated => $"g::{Area(t)}::{Module(t)}",
            Cat.Manual => $"m::{Module(t)}",
            Cat.Stub => $"g::stubs::{Module(t)}",
            _ => throw new Dropped(t.Name),
        };
    }

    /// <summary>The first declaration of a virtual member: the slot every override fills.</summary>
    public static ISymbol Slot(ISymbol m)
    {
        m = m.OriginalDefinition;
        while (true)
        {
            ISymbol? up = m switch { IMethodSymbol x => x.OverriddenMethod, IPropertySymbol p => p.OverriddenProperty, _ => null };
            if (up == null) return m;
            m = up.OriginalDefinition;
        }
    }

    private static IEnumerable<IMethodSymbol> Named(INamedTypeSymbol t, IMethodSymbol like) =>
        t.GetMembers(like.Name).OfType<IMethodSymbol>().Where(x => x.MethodKind == like.MethodKind);

    /// <summary>The Rust function name of a method, a constructor, or a property accessor.</summary>
    public string FnName(IMethodSymbol m0)
    {
        var m = m0.OriginalDefinition;
        if (m.ReducedFrom != null) m = m.ReducedFrom.OriginalDefinition;
        return _memberName.GetOrAdd(m, static (sym, self) =>
        {
            var m = (IMethodSymbol)sym;
            var t = m.ContainingType;
            switch (m.MethodKind)
            {
                case MethodKind.PropertyGet or MethodKind.PropertySet when m.AssociatedSymbol is IPropertySymbol p:
                {
                    string baseName = p.IsIndexer ? "item" : Snake(p.ExplicitInterfaceImplementations.Length > 0 ? p.Name[(p.Name.LastIndexOf('.') + 1)..] : p.Name);
                    if (p.IsIndexer) return m.MethodKind == MethodKind.PropertyGet ? "get_item" : "set_item";
                    if (m.MethodKind == MethodKind.PropertyGet) return Ident(baseName);
                    // A setter and a method SetX would share a name: the setter gives way.
                    var slot = (IPropertySymbol)Slot(p);
                    bool clash = false;
                    for (INamedTypeSymbol? x = slot.ContainingType; x != null && !clash; x = x.BaseType) clash = x.GetMembers("Set" + p.Name).Any(y => y is IMethodSymbol);
                    return "set_" + baseName + (clash ? "__prop" : "");
                }
                case MethodKind.Constructor:
                    return "new" + (t.InstanceConstructors.Count(c => !c.IsImplicitlyDeclared || t.InstanceConstructors.Length == 1) > 1 ? self.Suffix(m) : "");
                case MethodKind.StaticConstructor:
                    return "static_init";
                case MethodKind.UserDefinedOperator or MethodKind.Conversion:
                    return Snake(m.Name) + (t.GetMembers(m.Name).Length > 1 ? self.Suffix(m) : "");
                default:
                {
                    string name = m.Name;
                    if (m.ExplicitInterfaceImplementations.Length > 0) name = Clean(m.ExplicitInterfaceImplementations[0].ContainingType.Name) + "_" + m.ExplicitInterfaceImplementations[0].Name;
                    var slot = (IMethodSymbol)Slot(m);
                    bool overloaded = Named(slot.ContainingType, slot).Count() > 1
                        || (!m.IsOverride && Named(t, m).Count() > 1);
                    return Ident(name) + (overloaded ? self.Suffix(m) : "");
                }
            }
        }, this);
    }

    private string Suffix(IMethodSymbol m)
    {
        var sb = new StringBuilder("_");
        foreach (var p in m.Parameters)
        {
            string ty;
            try { ty = RType(p.Type); } catch (Exception) { ty = p.Type.Name; }
            sb.Append('_').Append(Snake(Clean(ty.Replace("<", "_").Replace(",", "_").Replace("::", "_"))));
        }
        if (m.Parameters.Length == 0) sb.Append("_0");
        return sb.ToString();
    }

    public static string FieldName(IFieldSymbol f)
    {
        if (f.AssociatedSymbol is IPropertySymbol p) return Ident(p.Name);
        string n = Snake(f.Name);
        return Keywords.Contains(n) ? n + "_" : n;
    }

    // ---------------------------------------------------------------- kinds

    public string KindOf(INamedTypeSymbol t0)
    {
        var t = t0.OriginalDefinition;
        if (IsDroppedNamed(t)) throw new Dropped(t.Name);
        if (!IsSource(t)) throw new Unsupported($"kind of the library type {t.Name}");
        if (t.TypeKind is not (TypeKind.Class or TypeKind.Interface) || IsValue(t)) throw new Unsupported($"the value type {t.Name} as a class");
        Kinds.GetOrAdd(t, _ => Kinds.Count);
        return "Kind::" + Unique(t);
    }

    // ---------------------------------------------------------------- types

    public static bool IsNullable(ITypeSymbol t, out ITypeSymbol inner)
    {
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n) { inner = n.TypeArguments[0]; return true; }
        inner = t;
        return false;
    }

    public static string MetaName(ITypeSymbol t) => t is INamedTypeSymbol n ? Namespace(n.OriginalDefinition) + "." + n.OriginalDefinition.MetadataName : t.Name;

    public static bool IsTask(ITypeSymbol? t, out ITypeSymbol? result)
    {
        result = null;
        if (t is not INamedTypeSymbol n) return false;
        string meta = MetaName(n);
        if (meta is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask") return true;
        if (meta is "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask`1") { result = n.TypeArguments[0]; return true; }
        return false;
    }

    /// <summary>Game classes and interfaces, and object: values are handles.</summary>
    public bool IsObj(ITypeSymbol? t) => t switch
    {
        null => false,
        ITypeParameterSymbol p => Erased(p),
        INamedTypeSymbol { SpecialType: SpecialType.System_Object } => true,
        INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface } n => IsSource(n) && !IsDroppedNamed(n) && !IsGeneratedList(n) && !IsValue(n),
        _ => false,
    };

    public static bool IsGeneratedList(INamedTypeSymbol n) => n.Name.Contains("z__ReadOnly");

    /// <summary>The keys or the values of a dictionary, as their own type: a list here.</summary>
    public static bool IsDictPart(INamedTypeSymbol n) => n.Name is "ValueCollection" or "KeyCollection" && n.ContainingType is { } d && MetaName(d) == "System.Collections.Generic.Dictionary`2";

    /// <summary>What a list type holds.</summary>
    public static ITypeSymbol ItemOf(INamedTypeSymbol n) => IsDictPart(n) ? n.ContainingType.TypeArguments[n.Name == "KeyCollection" ? 0 : 1] : n.TypeArguments[0];

    public enum Coll { None, Vec, Set, Dict, Queue, Stack, Ordered }

    public static Coll CollOf(ITypeSymbol? t)
    {
        if (t is IArrayTypeSymbol) return Coll.Vec;
        if (t is not INamedTypeSymbol n) return Coll.None;
        if (IsGeneratedList(n) || IsDictPart(n)) return Coll.Vec;
        return MetaName(n) switch
        {
            "System.Collections.Generic.List`1" or "System.Collections.Generic.IList`1" or "System.Collections.Generic.IReadOnlyList`1"
                or "System.Collections.Generic.IEnumerable`1" or "System.Collections.Generic.ICollection`1" or "System.Collections.Generic.IReadOnlyCollection`1"
                or "System.Collections.ObjectModel.ReadOnlyCollection`1" or "System.Collections.Immutable.ImmutableArray`1" or "System.Collections.Immutable.ImmutableList`1"
                or "System.ReadOnlySpan`1" or "System.Span`1" => Coll.Vec,
            "System.Linq.IOrderedEnumerable`1" => Coll.Ordered,
            "System.Collections.Generic.HashSet`1" or "System.Collections.Generic.ISet`1" or "System.Collections.Generic.IReadOnlySet`1"
                or "System.Collections.Immutable.ImmutableHashSet`1" => Coll.Set,
            "System.Collections.Generic.Dictionary`2" or "System.Collections.Generic.IDictionary`2" or "System.Collections.Generic.IReadOnlyDictionary`2" => Coll.Dict,
            "System.Collections.Generic.Queue`1" => Coll.Queue,
            "System.Collections.Generic.Stack`1" => Coll.Stack,
            _ => Coll.None,
        };
    }

    /// <summary>The Rust type of a C# type. With norm, every handle prints as Obj, so that two types can be compared.</summary>
    public string RType(ITypeSymbol? t, bool norm = false)
    {
        switch (t)
        {
            case null: return "()";
            case ITypeParameterSymbol p: return Erased(p) ? "Obj" : p.Name;
            case IArrayTypeSymbol a: return $"Vec<{RType(a.ElementType, norm)}>";
            case IPointerTypeSymbol or IFunctionPointerTypeSymbol: throw new Dropped("pointer");
            case IDynamicTypeSymbol: throw new Unsupported("dynamic");
            case IErrorTypeSymbol: throw new Unsupported($"unresolved type {t.Name}");
        }
        var n = (INamedTypeSymbol)t;
        switch (n.SpecialType)
        {
            case SpecialType.System_Void: return "()";
            case SpecialType.System_Boolean: return "bool";
            case SpecialType.System_Int32: return "i32";
            case SpecialType.System_UInt32: return "u32";
            case SpecialType.System_Int64: return "i64";
            case SpecialType.System_UInt64: return "u64";
            case SpecialType.System_Int16: return "i16";
            case SpecialType.System_UInt16: return "u16";
            case SpecialType.System_Byte: return "u8";
            case SpecialType.System_SByte: return "i8";
            case SpecialType.System_Single: return "f32";
            case SpecialType.System_Double: return "f64";
            case SpecialType.System_Decimal: return "Dec";
            case SpecialType.System_String: return "Str";
            case SpecialType.System_Char: return "char";
            case SpecialType.System_Object: return "Obj";
        }
        if (IsNullable(n, out var inner)) return $"Option<{RType(inner, norm)}>";
        if (IsTask(n, out var result)) return $"R<{RType(result, norm)}>";
        if (n.IsTupleType) return "(" + string.Join(", ", n.TupleElements.Select(e => RType(e.Type, norm))) + (n.TupleElements.Length == 1 ? ",)" : ")");
        if (IsDroppedNamed(n) || n.TypeArguments.Any(IsDropped)) throw new Dropped(n.Name);
        switch (CollOf(n))
        {
            case Coll.Vec: return $"Vec<{RType(ItemOf(n), norm)}>";
            case Coll.Ordered: return $"Ordered<{RType(n.TypeArguments[0], norm)}>";
            case Coll.Set: return $"Set<{RType(n.TypeArguments[0], norm)}>";
            case Coll.Dict: return $"Dict<{RType(n.TypeArguments[0], norm)}, {RType(n.TypeArguments[1], norm)}>";
            case Coll.Queue: return $"Queue<{RType(n.TypeArguments[0], norm)}>";
            case Coll.Stack: return $"Stack<{RType(n.TypeArguments[0], norm)}>";
        }
        if (n.TypeKind == TypeKind.Delegate)
        {
            var invoke = n.DelegateInvokeMethod ?? throw new Unsupported("delegate without Invoke");
            if (invoke.Parameters.Length > 5) throw new Unsupported("delegate with more than 5 parameters");
            if (invoke.Parameters.Any(p => p.RefKind != RefKind.None)) throw new Unsupported("delegate with ref parameters");
            var args = invoke.Parameters.Select(p => RType(p.Type, norm)).Append(RType(invoke.ReturnsVoid ? null : invoke.ReturnType, norm));
            return $"Fn{invoke.Parameters.Length}<{string.Join(", ", args)}>";
        }
        string meta = MetaName(n);
        switch (meta)
        {
            case "System.Collections.Generic.KeyValuePair`2": return $"KeyValuePair<{RType(n.TypeArguments[0], norm)}, {RType(n.TypeArguments[1], norm)}>";
            case "System.Linq.IGrouping`2": return $"Group<{RType(n.TypeArguments[0], norm)}, {RType(n.TypeArguments[1], norm)}>";
            case "System.Type": return "Kind";
            case "System.MidpointRounding": return "MidpointRounding";
            case "System.StringComparison": return "StringComparison";
            case "System.Exception": return "Exception";
        }
        if (!IsSource(n)) throw new Unsupported($"library type {meta}");
        if (IsValue(n))
        {
            if (n.TypeArguments.Length > 0) throw new Unsupported($"generic struct {n.Name}");
            Structs.TryAdd(n.OriginalDefinition, 0);
            return Unique(n);
        }
        switch (n.TypeKind)
        {
            case TypeKind.Enum:
                Enums.TryAdd(n.OriginalDefinition, 0);
                return Unique(n);
            case TypeKind.Struct:
                if (n.TypeArguments.Length > 0) throw new Unsupported($"generic struct {n.Name}");
                Structs.TryAdd(n.OriginalDefinition, 0);
                return Unique(n);
            case TypeKind.Class or TypeKind.Interface:
                KindOf(n);
                return norm ? "Obj" : Unique(n);
        }
        throw new Unsupported($"type {meta}");
    }

    /// <summary>Whether a value of the type is copied by a plain read (no .clone()).</summary>
    public bool IsCopy(ITypeSymbol? t)
    {
        switch (t)
        {
            case null: return true;
            case ITypeParameterSymbol p: return Erased(p);
            case IArrayTypeSymbol: return false;
        }
        var n = (INamedTypeSymbol)t!;
        if (n.SpecialType != SpecialType.None) return true;
        if (IsNullable(n, out var inner)) return IsCopy(inner);
        if (IsTask(n, out var r)) return false;
        if (n.IsTupleType) return n.TupleElements.All(e => IsCopy(e.Type));
        if (CollOf(n) != Coll.None || n.TypeKind == TypeKind.Delegate) return false;
        if (MetaName(n) is "System.Type" or "System.MidpointRounding" or "System.StringComparison") return true;
        if (n.TypeKind == TypeKind.Enum) return true;
        if (IsValue(n)) return StructIsCopy(n);
        if (IsObj(n)) return true;
        return false;
    }

    public bool StructIsCopy(INamedTypeSymbol s) =>
        s.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst).All(f => !IsDropped(f.Type) && IsCopy(f.Type));

    // ---------------------------------------------------------------- class layout

    public bool IsOpen(INamedTypeSymbol t) => Cfg.Open.Contains(Unique(t));

    private readonly ConcurrentDictionary<INamedTypeSymbol, bool> _stateful = new(Eq);

    /// <summary>Classes whose instances have generated state: translated and hand-written classes, and their ancestors.</summary>
    public List<INamedTypeSymbol> StatefulClasses()
    {
        var set = new HashSet<INamedTypeSymbol>(Eq);
        foreach (var t in AllTypes)
        {
            if (t.TypeKind != TypeKind.Class || t.IsStatic || IsValue(t) || CatOf(t) is not (Cat.Translated or Cat.Manual)) continue;
            for (INamedTypeSymbol? x = t; x != null && IsSource(x) && !IsDroppedNamed(x) && x.SpecialType == SpecialType.None; x = x.BaseType?.OriginalDefinition)
                set.Add(x.OriginalDefinition);
        }
        return set.OrderBy(FullName, StringComparer.Ordinal).ToList();
    }

    private HashSet<INamedTypeSymbol>? _statefulSet;

    /// <summary>Whether the class's fields are kept (see StatefulClasses).</summary>
    public bool IsStateful(INamedTypeSymbol t)
    {
        _statefulSet ??= new HashSet<INamedTypeSymbol>(StatefulClasses(), Eq);
        return _statefulSet.Contains(t.OriginalDefinition);
    }

    public INamedTypeSymbol? BaseClass(INamedTypeSymbol t)
    {
        var b = t.BaseType?.OriginalDefinition;
        return b != null && b.SpecialType == SpecialType.None && IsSource(b) && !IsDroppedNamed(b) ? b : null;
    }

    /// <summary>The class whose arena holds the instances of this class: its topmost ancestor that is not open.</summary>
    public INamedTypeSymbol Root(INamedTypeSymbol t)
    {
        t = t.OriginalDefinition;
        while (BaseClass(t) is { } b && !IsOpen(b)) t = b;
        return t;
    }

    /// <summary>The instance fields of a class that are state, and those that are kept but not state (delegates).</summary>
    public IEnumerable<(IFieldSymbol Field, string Name, string Type, bool IsState)> Fields(INamedTypeSymbol t)
    {
        var seen = new HashSet<string>();
        foreach (var f in t.GetMembers().OfType<IFieldSymbol>())
        {
            if (f.IsStatic || f.IsConst) continue;
            if (f.AssociatedSymbol is IEventSymbol) continue;
            if (Cfg.DropFields.Contains($"{t.Name}.{f.AssociatedSymbol?.Name ?? f.Name}")) continue;
            if (Cfg.OnlyFields.TryGetValue(t.Name, out var only) && !only.Contains(f.AssociatedSymbol?.Name ?? f.Name)) continue;
            if (IsDropped(f.Type)) continue;
            // A stored task is a handle on running code, not state.
            if (IsTask(f.Type, out _)) { Rep.FieldSkipped($"{t.Name}.{f.Name}: a task"); continue; }
            string type;
            try { type = RType(f.Type); }
            catch (Dropped) { continue; }
            catch (Unsupported e) { Rep.FieldSkipped($"{t.Name}.{f.Name}: {e.Message}"); continue; }
            string name = FieldName(f);
            while (!seen.Add(name)) name += "_";
            yield return (f, name, type, f.Type.TypeKind != TypeKind.Delegate);
        }
    }

    private readonly ConcurrentDictionary<IFieldSymbol, string?> _fieldNames = new(Eq);

    /// <summary>The Rust name of a state field, or null when the field is not kept.</summary>
    public string? StateField(IFieldSymbol f)
    {
        f = f.OriginalDefinition;
        return _fieldNames.GetOrAdd(f, static (f, self) =>
        {
            foreach (var x in self.Fields(f.ContainingType.OriginalDefinition))
                if (Eq.Equals(x.Field, f)) return x.Name;
            return null;
        }, this);
    }

    // ---------------------------------------------------------------- collections a callee changes

    private static readonly HashSet<string> Mutators =
    [
        "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "Sort", "Reverse",
        "Enqueue", "Dequeue", "TryDequeue", "Push", "Pop", "TryPop", "TryAdd", "UnionWith", "ExceptWith", "IntersectWith",
        "SymmetricExceptWith", "AddFirst", "AddLast", "RemoveFirst", "RemoveLast",
    ];

    private HashSet<(ISymbol Slot, int Ordinal)>? _changed;

    /// <summary>Whether a method changes the collection it is given as this parameter (adds to it, clears it, hands it
    /// to a method that does). A C# collection is one object for the caller and the callee; here it is a value, so
    /// such a parameter comes back to the caller with the result, as a ref parameter does. Decided for a virtual
    /// member as a whole: if one implementation changes it, the parameter comes back from every one.</summary>
    public bool IsChangedParam(IParameterSymbol p)
    {
        if (p.RefKind != RefKind.None || CollOf(p.Type) == Coll.None || p.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.Ordinary } m) return false;
        _changed ??= ChangedParams();
        var def = m.OriginalDefinition.ReducedFrom?.OriginalDefinition ?? m.OriginalDefinition;
        return _changed.Contains((SigSlot(def), p.Ordinal));
    }

    private readonly ConcurrentDictionary<ISymbol, ISymbol> _sigSlots = new(Eq);

    /// <summary>The member that decides a method's signature: its slot, or the interface member the slot implements.</summary>
    private ISymbol SigSlot(IMethodSymbol m) => _sigSlots.GetOrAdd(m, static (sym, self) =>
    {
        var slot = (IMethodSymbol)Slot(sym);
        var type = ((IMethodSymbol)sym).ContainingType;
        if (type.TypeKind == TypeKind.Interface) return slot;
        foreach (var i in type.AllInterfaces)
        {
            if (!IsSource(i)) continue;
            foreach (var im in i.GetMembers(slot.Name).OfType<IMethodSymbol>())
                if (type.FindImplementationForInterfaceMember(im) is IMethodSymbol impl && Eq.Equals(Slot(impl.OriginalDefinition), slot)) return im.OriginalDefinition;
        }
        return slot;
    }, this);

    private HashSet<(ISymbol, int)> ChangedParams()
    {
        static IOperation Bare(IOperation op)
        {
            while (op is IConversionOperation { IsTryCast: false } cv) op = cv.Operand;
            return op;
        }
        var set = new HashSet<(ISymbol, int)>();
        // What each method does with its collection parameters: changes one itself, or passes one on.
        var passes = new List<(IParameterSymbol From, IParameterSymbol To)>();
        var lockObj = new object();
        var methods = AllTypes.Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface && CatOf(t) is Cat.Translated or Cat.Manual)
            .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
            .Where(m => m.MethodKind is MethodKind.Ordinary && m.Parameters.Any(p => p.RefKind == RefKind.None && CollOf(p.Type) != Coll.None)).ToList();
        Parallel.ForEach(methods, m =>
        {
            var syntax = m.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (syntax == null) return;
            var op = Comp.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax);
            if (op == null) return;
            foreach (var o in op.DescendantsAndSelf())
            {
                IParameterSymbol? Own(IOperation? x) => x != null && Bare(x) is IParameterReferenceOperation pr && Eq.Equals(pr.Parameter.ContainingSymbol, m)
                    && pr.Parameter.RefKind == RefKind.None && CollOf(pr.Parameter.Type) != Coll.None ? pr.Parameter : null;
                switch (o)
                {
                    case IInvocationOperation inv when !IsSource(inv.TargetMethod.ContainingType) && Mutators.Contains(inv.TargetMethod.Name) && Own(inv.Instance) is { } changed:
                        lock (lockObj) set.Add((SigSlot(m.OriginalDefinition), changed.Ordinal));
                        break;
                    case IAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true } ix } when Own(ix.Instance) is { } changed:
                        lock (lockObj) set.Add((SigSlot(m.OriginalDefinition), changed.Ordinal));
                        break;
                    case IAssignmentOperation { Target: IArrayElementReferenceOperation ae } when Own(ae.ArrayReference) is { } changed:
                        lock (lockObj) set.Add((SigSlot(m.OriginalDefinition), changed.Ordinal));
                        break;
                    case IArgumentOperation { Parameter: { RefKind: RefKind.None } to } arg when IsSource(to.ContainingType) && CollOf(to.Type) != Coll.None && Own(arg.Value) is { } from:
                        lock (lockObj) passes.Add((from, to));
                        break;
                }
            }
        });
        // Passing a collection on to a method that changes it changes it.
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var (from, to) in passes)
            {
                var toMethod = (IMethodSymbol)to.ContainingSymbol;
                var toDef = toMethod.OriginalDefinition.ReducedFrom?.OriginalDefinition ?? toMethod.OriginalDefinition;
                if (!set.Contains((SigSlot(toDef), to.OriginalDefinition.Ordinal))) continue;
                grew |= set.Add((SigSlot(((IMethodSymbol)from.ContainingSymbol).OriginalDefinition), from.Ordinal));
            }
        }
        return set;
    }

    /// <summary>Whether a static only remembers the result of a computation (port.toml [caches]): it reads as null and a
    /// write to it does nothing, so the computation runs at each use.</summary>
    public bool IsCache(ISymbol field) => field.IsStatic && field.ContainingType != null && Cfg.Caches.Contains(field.ContainingType.Name + "." + field.Name);

    /// <summary>The field of Combat.s that holds a static of the game, when port.toml lists it as state; else null.</summary>
    public string? StaticState(ISymbol fieldOrProperty)
    {
        var owner = fieldOrProperty is IFieldSymbol { AssociatedSymbol: { } a } ? a : fieldOrProperty;
        if (!owner.IsStatic || owner.ContainingType == null || !Cfg.Statics.Contains(owner.ContainingType.Name + "." + owner.Name)) return null;
        return Snake(Unique(owner.ContainingType)) + "__" + Snake(owner.Name.TrimStart('_'));
    }

    /// <summary>The statics of the game that are state: the field or property, the name in Combat.s, the type.</summary>
    public List<(ISymbol Member, string Name, ITypeSymbol Type)> StaticStates()
    {
        var list = new List<(ISymbol, string, ITypeSymbol)>();
        foreach (var t in AllTypes)
            foreach (var m in t.GetMembers())
            {
                if (m is IFieldSymbol { AssociatedSymbol: not null }) continue;
                ITypeSymbol? type = m switch { IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, _ => null };
                if (type != null && StaticState(m) is { } name) list.Add((m, name, type));
            }
        return list.OrderBy(x => x.Item2, StringComparer.Ordinal).ToList();
    }

    private HashSet<string>? _typeNames;

    /// <summary>The Rust name of the struct that holds a class's fields.</summary>
    public string DataName(INamedTypeSymbol t)
    {
        _typeNames ??= _unique.Values.ToHashSet();
        string name = Unique(t) + "Data";
        return _typeNames.Contains(name) ? name + "_" : name;
    }

    /// <summary>The Rust name of an enum member (Self is a keyword).</summary>
    public static string EnumMember(string name) => name == "Self" ? "Self_" : name;

    /// <summary>The type parameters of a class that are kinds stored in each instance.</summary>
    public static string TargField(ITypeParameterSymbol p) => "targ_" + Snake(p.Name);
}
