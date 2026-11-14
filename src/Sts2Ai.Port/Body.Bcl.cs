using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Sts2Ai.Port;

/// <summary>Calls into the .NET library (and Godot's Mathf), mapped onto the rt primitives.</summary>
public sealed partial class Body
{
    private static string TypeKey(INamedTypeSymbol type)
    {
        if (World.IsGeneratedList(type)) return "Vec";
        return World.CollOf(type) switch
        {
            World.Coll.Vec => "Vec",
            World.Coll.Set => "Set",
            World.Coll.Dict => "Dict",
            World.Coll.Queue => "Queue",
            World.Coll.Stack => "Stack",
            World.Coll.Ordered => "Ordered",
            _ => type.SpecialType == SpecialType.System_Array ? "Vec" : type.Name,
        };
    }

    /// <summary>The type of the values a call of the library compares by their equality (EqualityComparer&lt;T&gt;.Default,
    /// `object.Equals`): a list's Remove, Contains and IndexOf, every operation of a set and of a dictionary (its keys;
    /// ContainsValue its values), LINQ's Contains, Distinct, the set operations and the ones that group by a key.</summary>
    private static ITypeSymbol? Compared(IInvocationOperation op, string tk, string name)
    {
        var m = op.TargetMethod;
        var ct = m.ContainingType;
        ITypeSymbol? Arg(int i) => ct.TypeArguments.Length > i ? ct.TypeArguments[i] : null;
        ITypeSymbol? Of(int i) => (m.ReducedFrom != null ? m.ReducedFrom.TypeArguments.Length : m.TypeArguments.Length) > i ? (m.TypeArguments.Length > i ? m.TypeArguments[i] : null) : null;
        static ITypeSymbol? Bare(IOperation? o)
        {
            while (o is IConversionOperation cv) o = cv.Operand;
            return o?.Type;
        }
        return (tk, name) switch
        {
            ("Vec", "Remove" or "Contains" or "IndexOf" or "LastIndexOf") => Arg(0),
            ("Set", _) => Arg(0),
            ("Dict", "ContainsValue") => Arg(1),
            ("Dict", _) => Arg(0),
            ("Queue" or "Stack", "Contains") => Arg(0),
            ("Enumerable", "Contains" or "Distinct" or "Except" or "Intersect" or "Union" or "SequenceEqual" or "ToHashSet") => Of(0),
            ("Enumerable", "GroupBy" or "ToDictionary" or "DistinctBy" or "ToLookup" or "CountBy" or "ExceptBy" or "IntersectBy" or "UnionBy") => Of(1),
            ("Object", "Equals") => op.Instance != null ? Bare(op.Instance) : Bare(op.Arguments.FirstOrDefault()?.Value),
            ("EqualityComparer", "Equals") => Arg(0),
            _ => null,
        };
    }

    /// <summary>The function that answers `x.Equals(y)` for objects of a class of the game whose equality is its own,
    /// as EqualityComparer&lt;T&gt;.Default asks it: its IEquatable&lt;T&gt;.Equals where it has one, else its
    /// Equals(object). Null where it has none of its own, no translated one, or a class below it answers otherwise (a
    /// virtual call this does not dispatch).</summary>
    private string? OwnEquals(ITypeSymbol t)
    {
        if (t is not INamedTypeSymbol n || !World.IsSource(n) || n.TypeKind != TypeKind.Class) return null;
        IMethodSymbol? eq = null;
        if (n.AllInterfaces.FirstOrDefault(i => i.Name == "IEquatable" && i.TypeArguments is [var ta] && Eq.Equals(ta.OriginalDefinition, n.OriginalDefinition)) is { } equatable
            && n.FindImplementationForInterfaceMember(equatable.GetMembers("Equals")[0]) is IMethodSymbol im)
            eq = im;
        for (var x = n; x != null && eq == null && World.IsSource(x); x = x.BaseType)
            eq = x.GetMembers("Equals").OfType<IMethodSymbol>().FirstOrDefault(m => m.IsOverride && m.Parameters is [{ Type.SpecialType: SpecialType.System_Object }]);
        if (eq == null || eq.IsImplicitlyDeclared || w.CatOf(eq.ContainingType) != Cat.Translated || w.IsDropMember(eq)) return null;
        var root = n.OriginalDefinition;
        bool Under(INamedTypeSymbol s) { for (var x = s.BaseType; x != null; x = x.BaseType) if (Eq.Equals(x.OriginalDefinition, root)) return true; return false; }
        if (w.AllTypes.Any(s => World.IsSource(s) && Under(s) && s.GetMembers("Equals").OfType<IMethodSymbol>().Any(m => m.IsOverride && !m.IsImplicitlyDeclared))) return null;
        return CallPath(eq, false);
    }

    /// <summary>A comparison by the values' equality over objects of a class whose equality is its own, with `eq` its
    /// Equals, as .NET makes it: a list's Remove, Contains and IndexOf and LINQ's Contains ask each item in order
    /// (`items[i].Equals(x)`, a null `x` finds a null), `object.Equals(a, b)` the same object or `a.Equals(b)`, a
    /// comparer `a.Equals(b)` with both null equal. Null for every other operation (sets, keys, Distinct ..).</summary>
    private string? EqualityCall(IInvocationOperation op, string key, string eq, List<IOperation> a, IOperation? inst,
        Func<Func<string, string[], string>, int[], string> rd, Func<Func<string, string[], string>, int[], string> mut, Func<int, string> v)
    {
        int n = a.Count;
        switch (key)
        {
            case "Vec.Remove" when n == 1:
                return mut((p, x) => $"{{ let __items = {p}.clone(); match rt::position_eq(&__items, {x[0]}, {eq}, c) {{ Some(__at) => {{ {p}.remove(__at); true }} None => false }} }}", [0]);
            case "Vec.Contains" when n == 1:
                return rd((r, x) => $"rt::position_eq(&{r}.clone(), {x[0]}, {eq}, c).is_some()", [0]);
            case "Vec.IndexOf" when n == 1:
                return rd((r, x) => $"rt::position_eq(&{r}.clone(), {x[0]}, {eq}, c).map_or(-1, |at| at as i32)", [0]);
            case "Enumerable.Contains" when n == 2 && World.CollOf(a[0].Type) == World.Coll.Vec:
                return $"{{ let __items = {v(0)}; let __x = {v(1)}; rt::position_eq(&__items, __x, {eq}, c).is_some() }}";
            case "Object.Equals" when inst == null && n == 2:
                return $"{{ let __a = {Expr(Unboxed(a[0]))}; let __b = {Expr(Unboxed(a[1]))}; rt::object_equals(__a, __b, {eq}, c) }}";
            case "Object.Equals" when inst != null && n == 1:
                return $"{{ let __a = {Expr(Unboxed(inst))}; let __b = {Expr(Unboxed(a[0]))}; {eq}(__a, __b, c) }}";
            case "EqualityComparer.Equals" when n == 2:
                return $"{{ let __a = {v(0)}; let __b = {v(1)}; rt::comparer_equals(__a, __b, {eq}, c) }}";
            default:
                return null;
        }
    }

    /// <summary>How a library call would compare or case strings other than ordinal, as .NET does it (the engine's
    /// strings compare by their UTF-16 code units, like StringComparison.Ordinal): by a StringComparison or comparer
    /// it is handed, or by the current culture where it is handed none (string.Compare, CompareTo, StartsWith,
    /// EndsWith and IndexOf of a string, ToLower, ToUpper, and ordering strings with Comparer.Default). Null when
    /// ordinal.</summary>
    private static string? CultureString(IInvocationOperation op, string tk, string name)
    {
        foreach (var arg in op.Arguments)
        {
            var pt = arg.Parameter?.Type;
            if (pt == null) continue;
            if (pt.Name == "StringComparison")
                return arg.Value.ConstantValue is { HasValue: true, Value: 4 } ? null : $"compared by {arg.Value.Syntax}";
            if (pt.Name is "CultureInfo" or "IFormatProvider" && tk == "String") return $"cased by {arg.Value.Syntax}";
            if (pt is INamedTypeSymbol { Name: "IComparer" or "IEqualityComparer" or "StringComparer", TypeArguments: var ta } && (ta.Length == 0 || ta[0].SpecialType == SpecialType.System_String))
                return Unconverted(arg.Value) is IPropertyReferenceOperation { Property: { Name: "Ordinal", ContainingType.Name: "StringComparer" } } or ILiteralOperation { ConstantValue.Value: null } ? null : $"compared by {arg.Value.Syntax}";
        }
        var m = op.TargetMethod.ReducedFrom ?? op.TargetMethod;
        bool firstIsString = m.Parameters.FirstOrDefault()?.Type.SpecialType == SpecialType.System_String || op.Instance?.Type?.SpecialType == SpecialType.System_String && m.Parameters.FirstOrDefault()?.Type.SpecialType == SpecialType.System_String;
        switch (tk, name)
        {
            case ("String", "Compare" or "CompareTo"):
            case ("String", "StartsWith" or "EndsWith" or "IndexOf" or "LastIndexOf") when firstIsString:
                return "compared by the current culture";
            case ("String", "ToLower" or "ToUpper"):
                return "cased by the current culture";
            case ("Enumerable", "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Order" or "OrderDescending" or "Min" or "Max" or "MinBy" or "MaxBy"):
            {
                // The key: what the selector gives, or the items themselves.
                var key = op.Arguments.Length > 1 && op.Arguments[1].Value is IDelegateCreationOperation or IAnonymousFunctionOperation || op.Arguments.Length > 1 && op.Arguments[1].Parameter?.Type is INamedTypeSymbol { TypeKind: TypeKind.Delegate }
                    ? (op.Arguments[1].Parameter!.Type as INamedTypeSymbol)?.TypeArguments.LastOrDefault()
                    : (m.Parameters.FirstOrDefault()?.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault() ?? op.Type;
                return key?.SpecialType == SpecialType.System_String ? "ordered by the current culture (Comparer<string>.Default)" : null;
            }
            case ("Vec", "Sort") when op.Arguments.Length == 0 && (op.Instance?.Type as INamedTypeSymbol)?.TypeArguments.FirstOrDefault()?.SpecialType == SpecialType.System_String:
                return "ordered by the current culture (Comparer<string>.Default)";
        }
        return null;
    }

    private string? BclCall(IInvocationOperation op)
    {
        var m = op.TargetMethod;
        var def = (m.ReducedFrom ?? m).OriginalDefinition;
        var type = def.ContainingType;
        var a = op.Arguments.OrderBy(x => x.Parameter!.Ordinal).Select(x => x.Value).ToList();
        var inst = op.Instance;
        int n = a.Count;
        string tk = TypeKey(type);
        string V(int i) => Expr(a[i]);
        string F(int i, int arity) => Closure(a[i], arity);
        string I() => Expr(inst!);
        string Rd(Func<string, string[], string> build, params int[] idx) => HoistedCodes(idx.Select(V).ToArray(), Ref(inst!), build);
        string Mut(Func<string, string[], string> build, params int[] idx)
        {
            // A change of a collection that its property makes on demand: the getter runs (and makes it), then the
            // field it returned is changed.
            if (inst is IPropertyReferenceOperation { Property: var lazy } lp && Forwarded(lazy.OriginalDefinition) == null && MadeOnDemand(lazy.OriginalDefinition) is { } made
                && w.StateField(made) is { } madeName && StableReceiver(lp.Instance) && !w.IsValue(made.ContainingType))
            {
                string on = lp.Instance == null ? "me" : Expr(lp.Instance);
                return $"{{ let _ = {Expr(lp)}; {HoistedCodes(idx.Select(V).ToArray(), $"{w.DataName(made.ContainingType)}::of_mut({on}, c).{madeName}", build, always: true)} }}";
            }
            if (UnstableReceiver(inst!) is not { } recv) return HoistedCodes(idx.Select(V).ToArray(), Place(inst!), build, always: true);
            string r = Tmp("r"), code = Expr(recv);
            pinned[recv] = r;
            try { return $"{{ let {r} = {code}; {HoistedCodes(idx.Select(V).ToArray(), Place(inst!), build, always: true)} }}"; }
            finally { pinned.Remove(recv); }
        }
        // A change in place that runs a closure: the list is taken out of its field while the closure runs.
        string Taken(Func<string, string> build)
        {
            string p = Place(inst!);
            return UsesCombat(p) ? $"{{ let mut __l = std::mem::take(&mut {p}); let __r = {build("__l")}; {p} = __l; __r }}" : build(p);
        }
        bool IsF(int i) => a[i].Type?.TypeKind == TypeKind.Delegate || Unconverted(a[i]) is IAnonymousFunctionOperation or IMethodReferenceOperation or IDelegateCreationOperation;
        string elem = "";

        // A string compared or cased by culture or ignoring case, where the engine's strings are ordinal.
        if (CultureString(op, tk, def.Name) is { } byCulture)
        {
            // Names compared by the current culture compare as ordinal: port.toml [strings] ordinal states which.
            if (byCulture == "compared by the current culture" && w.Cfg.OrdinalStrings.Contains(Where))
                w.Rep.Culture(Where, op, $"{byCulture}, of ASCII names ([strings] ordinal): translated as ordinal");
            else
            {
                w.Rep.Culture(Where, op, $"{byCulture}: not translated");
                throw new Unsupported($"a string {byCulture} ({tk}.{def.Name})");
            }
        }
        // A comparison by the values' equality (EqualityComparer<T>.Default, object.Equals) over a type of the game
        // whose equality is its own: a value here is compared by its fields (World.ValueEquality says whether its
        // Equals compares the same), an object by its own Equals where this translates the operation, else not at all.
        if (Compared(op, tk, def.Name) is { } compared && w.OwnEquality(compared) is { } own)
        {
            if (w.IsValue(compared) || compared.IsValueType)
            {
                if (compared is INamedTypeSymbol cn && w.EqualityLeftOut(cn) is [_, ..] left)
                {
                    w.Rep.Equality(Where, op, $"{compared.Name}: a value whose own Equals leaves out {string.Join(", ", left)}: not translated");
                    throw new Unsupported($"compares {compared.Name}, whose Equals leaves out {string.Join(", ", left)} ({tk}.{def.Name})");
                }
                w.Rep.Equality(Where, op, $"{compared.Name}: a value, its fields compared");
            }
            else
            {
                string? eq = OwnEquals(compared);
                string? code = eq == null ? null : EqualityCall(op, $"{tk}.{def.Name}", eq, a, inst, Rd, Mut, V);
                if (code == null)
                {
                    w.Rep.Equality(Where, op, $"{compared.Name} (own: {own}): not translated");
                    throw new Unsupported($"compares {compared.Name} by its own equality ({tk}.{def.Name})");
                }
                w.Rep.Equality(Where, op, $"{compared.Name}: by its own Equals");
                return code;
            }
        }

        switch ($"{tk}.{def.Name}")
        {
            // ---- LINQ
            case "Enumerable.Where": return $"linq::where_({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.Select": return ArityOf(a[1]) == 2 ? $"linq::select_indexed({V(0)}, {F(1, 2)}, c)" : $"linq::select({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.SelectMany" when n == 2: return $"linq::select_many({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.Any": return n == 1 ? $"!linq::is_empty(&{Ref(a[0])})" : $"linq::any({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.All": return $"linq::all({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.Count": return n == 1 ? $"linq::count(&{Ref(a[0])})" : $"linq::count_by({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.First": return n == 1 ? $"linq::first({V(0)})" : $"linq::first_by({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.FirstOrDefault": return n == 1 ? $"linq::first_or_default({V(0)})" : IsF(1) ? $"linq::first_or_default_by({V(0)}, {F(1, 1)}, c)" : null;
            case "Enumerable.Last": return n == 1 ? $"linq::last({V(0)})" : $"linq::last_by({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.LastOrDefault": return n == 1 ? $"linq::last_or_default({V(0)})" : IsF(1) ? $"linq::last_or_default_by({V(0)}, {F(1, 1)}, c)" : null;
            case "Enumerable.Single": return n == 1 ? $"linq::single({V(0)})" : $"linq::single_by({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.SingleOrDefault": return n == 1 ? $"linq::single_or_default({V(0)})" : IsF(1) ? $"linq::single_or_default_by({V(0)}, {F(1, 1)}, c)" : null;
            case "Enumerable.ToList" or "Enumerable.ToArray" or "Enumerable.AsEnumerable" or "Enumerable.Cast": return V(0);
            // Of a set, .NET's ConstructFrom keeps its slots (rt::Set::copy_of); of anything else, its items in order.
            case "Enumerable.ToHashSet" when n == 1: return World.CollOf(Unconverted(a[0]).Type) == World.Coll.Set ? $"Set::copy_of(&{Expr(Unconverted(a[0]))})" : $"Set::from_vec({V(0)})";
            case "Enumerable.ToDictionary" when n == 1: return $"Dict::from_pairs({V(0)})";
            case "Enumerable.ToDictionary" when n == 2: return $"linq::to_dictionary({V(0)}, {F(1, 1)}, |x, _c| x, c)";
            case "Enumerable.ToDictionary" when n == 3 && IsF(2): return $"linq::to_dictionary({V(0)}, {F(1, 1)}, {F(2, 1)}, c)";
            case "Enumerable.Concat": return $"linq::concat({V(0)}, {V(1)})";
            case "Enumerable.Except" when n == 2: return $"linq::except({V(0)}, {V(1)})";
            case "Enumerable.Intersect" when n == 2: return $"linq::intersect({V(0)}, {V(1)})";
            case "Enumerable.Union" when n == 2: return $"linq::union({V(0)}, {V(1)})";
            case "Enumerable.Distinct" when n == 1: return $"linq::distinct({V(0)})";
            case "Enumerable.DistinctBy" when n == 2: return $"linq::distinct_by({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.OrderBy" when n == 2: return $"linq::order_by({V(0)}, {F(1, 1)}, false, c)";
            case "Enumerable.OrderByDescending" when n == 2: return $"linq::order_by({V(0)}, {F(1, 1)}, true, c)";
            case "Enumerable.ThenBy" when n == 2: return $"linq::then_by({V(0)}, {F(1, 1)}, false, c)";
            case "Enumerable.ThenByDescending" when n == 2: return $"linq::then_by({V(0)}, {F(1, 1)}, true, c)";
            case "Enumerable.Order" when n == 2 && Unconverted(a[1]) is IPropertyReferenceOperation { Property: { Name: "Ordinal", ContainingType.Name: "StringComparer" } }:
            case "Enumerable.Order" when n == 1: return $"linq::order_by({V(0)}, |x, _c| x, false, c)";
            case "Enumerable.Sum": return $"linq::sum_{NumberKind(m, n)}({(n == 1 ? V(0) : LinqSelect(V(0), a[1]))})";
            case "Enumerable.Max": return n == 1 ? $"linq::max({V(0)})" : $"linq::max({LinqSelect(V(0), a[1])})";
            case "Enumerable.Min": return n == 1 ? $"linq::min({V(0)})" : $"linq::min({LinqSelect(V(0), a[1])})";
            // Of no items, a class's or a nullable's is null; of a value .NET throws.
            case "Enumerable.MaxBy" when n == 2: return $"linq::max_by{(op.Type is { IsReferenceType: true } || World.IsNullable(op.Type!, out _) ? "" : "_value")}({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.MinBy" when n == 2: return $"linq::min_by{(op.Type is { IsReferenceType: true } || World.IsNullable(op.Type!, out _) ? "" : "_value")}({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.Average": return $"linq::average_{NumberKind(m, n)}({(n == 1 ? V(0) : LinqSelect(V(0), a[1]))})";
            case "Enumerable.Take" when a[1].Type?.SpecialType == SpecialType.System_Int32: return $"linq::take({V(0)}, {V(1)})";
            case "Enumerable.Skip": return $"linq::skip({V(0)}, {V(1)})";
            case "Enumerable.TakeLast": return $"linq::take_last({V(0)}, {V(1)})";
            case "Enumerable.SkipLast": return $"linq::skip_last({V(0)}, {V(1)})";
            case "Enumerable.TakeWhile" when ArityOf(a[1]) == 1: return $"linq::take_while({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.SkipWhile" when ArityOf(a[1]) == 1: return $"linq::skip_while({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.Reverse": return $"linq::reverse({V(0)})";
            case "Enumerable.Contains" when n == 2: return HoistedCodes([V(1)], Ref(a[0]), (r, x) => $"linq::contains(&{r}, &{x[0]})");
            case "Enumerable.ElementAt" when a[1].Type?.SpecialType == SpecialType.System_Int32: return $"linq::element_at({V(0)}, {V(1)})";
            case "Enumerable.ElementAtOrDefault" when a[1].Type?.SpecialType == SpecialType.System_Int32: return $"linq::element_at_or_default({V(0)}, {V(1)})";
            case "Enumerable.OfType":
            {
                var target = m.TypeArguments[0];
                if (!w.IsObj(target)) throw new Unsupported($"OfType<{target.Name}>");
                return $"linq::of_type({V(0)}, {KindExpr(target)}, c)";
            }
            case "Enumerable.Zip" when n == 2: return $"linq::zip({V(0)}, {V(1)}, |a, b, _c: &mut Combat| (a, b), c)";
            case "Interlocked.Increment" when n == 1: { string place = Place(a[0]); return $"{{ {place} += 1; {place} }}"; }
            case "Enumerable.Zip" when n == 3: return $"linq::zip({V(0)}, {V(1)}, {F(2, 2)}, c)";
            case "Enumerable.GroupBy" when n == 2: return $"linq::group_by({V(0)}, {F(1, 1)}, c)";
            case "Enumerable.Append": return $"linq::append({V(0)}, {V(1)})";
            case "Enumerable.Prepend": return $"linq::prepend({V(0)}, {V(1)})";
            case "Enumerable.Aggregate" when n == 3: return $"linq::aggregate({V(0)}, {V(1)}, {F(2, 2)}, c)";
            case "Enumerable.SequenceEqual" when n == 2: return $"({V(0)} == {V(1)})";
            case "Enumerable.Range": return $"linq::range({V(0)}, {V(1)})";
            case "Enumerable.Repeat": return $"linq::repeat({V(0)}, {V(1)})";
            case "Enumerable.Empty" or "Vec.Empty": return $"Vec::<{T(m.TypeArguments[0])}>::new()";
            case "Enumerable.DefaultIfEmpty" when n == 1: return $"linq::default_if_empty({V(0)})";
            case "Ordered.GetEnumerator" or "Vec.GetEnumerator" or "Set.GetEnumerator" or "Dict.GetEnumerator": throw new Unsupported("an enumerator used by hand");

            // ---- List<T> and arrays
            case "Vec.Add": return Mut((p, x) => $"{p}.push({x[0]})", 0);
            case "Vec.AddRange": return Mut((p, x) => $"{p}.extend({x[0]})", 0);
            case "Vec.Insert": return Mut((p, x) => $"{p}.insert(({x[0]}) as usize, {x[1]})", 0, 1);
            case "Vec.InsertRange": return Mut((p, x) => $"list::insert_range(&mut {p}, {x[0]}, {x[1]})", 0, 1);
            case "Vec.Remove": return Mut((p, x) => $"list::remove(&mut {p}, &{x[0]})", 0);
            case "Vec.RemoveAt": return Mut((p, x) => $"list::remove_at(&mut {p}, {x[0]})", 0);
            case "Vec.RemoveRange": return Mut((p, x) => $"list::remove_range(&mut {p}, {x[0]}, {x[1]})", 0, 1);
            case "Vec.RemoveAll": { string f = F(0, 1); return Taken(l => $"list::remove_all(&mut {l}, {f}, c)"); }
            case "Vec.Clear": return $"{Place(inst!)}.clear()";
            case "Vec.Contains": return Rd((r, x) => $"{r}.contains(&{x[0]})", 0);
            case "Vec.IndexOf" when n == 1: return Rd((r, x) => $"list::index_of(&{r}, &{x[0]})", 0);
            case "Vec.IndexOf" when n == 2 && inst == null: return HoistedCodes([V(1)], Ref(a[0]), (r, x) => $"list::index_of(&{r}, &{x[0]})");
            case "Vec.Find": return $"linq::first_or_default_by({I()}, {F(0, 1)}, c)";
            case "Vec.FindLast": return $"linq::last_or_default_by({I()}, {F(0, 1)}, c)";
            case "Vec.FindAll": return $"linq::where_({I()}, {F(0, 1)}, c)";
            case "Vec.FindIndex" when n == 1: return $"linq::find_index({I()}, {F(0, 1)}, c)";
            case "Vec.FindIndex" when n == 2 && inst == null: return $"linq::find_index({V(0)}, {F(1, 1)}, c)";
            case "Vec.Exists" when inst != null: return $"linq::any({I()}, {F(0, 1)}, c)";
            case "Vec.TrueForAll" when inst != null: return $"linq::all({I()}, {F(0, 1)}, c)";
            case "Vec.Sort" when n == 0: return Taken(l => $"list::sort(&mut {l}, c)");
            case "Vec.Sort" when n == 1 && inst != null && IsF(0): { string f = F(0, 2); return Taken(l => $"list::sort_by(&mut {l}, {f}, c)"); }
            case "Vec.Reverse" when n == 0: return $"{Place(inst!)}.reverse()";
            case "Vec.ToArray" or "Vec.ToList" or "Vec.AsReadOnly" or "Vec.AsSpan" when inst != null: return I();
            case "Vec.ForEach" when inst != null: return $"list::for_each({I()}, {F(0, 1)}, c)";
            case "Vec.GetRange": return Rd((r, x) => $"list::get_range(&{r}, {x[0]}, {x[1]})", 0, 1);
            case "Vec.Slice" when n == 2: return Rd((r, x) => $"list::get_range(&{r}, {x[0]}, {x[1]})", 0, 1);
            case "Vec.CopyTo": throw new Unsupported("CopyTo");

            // ---- Dictionary<K, V>
            case "Dict.Add" when n == 2: return Mut((p, x) => $"{p}.add({x[0]}, {x[1]})", 0, 1);
            case "Dict.TryAdd": return Mut((p, x) => $"{p}.try_add({x[0]}, {x[1]})", 0, 1);
            case "Dict.ContainsKey": return Rd((r, x) => $"{r}.contains_key(&{x[0]})", 0);
            case "Dict.ContainsValue": return Rd((r, x) => $"{r}.contains_value(&{x[0]})", 0);
            case "Dict.Remove" when n == 1: return Mut((p, x) => $"{p}.remove(&{x[0]})", 0);
            case "Dict.Remove" when n == 2:
            {
                string found = Tmp("f");
                string store = a[1] is IDiscardOperation ? "" : Assign(a[1] is IDeclarationExpressionOperation de0 ? de0.Expression : a[1], () => $"{found}.clone().unwrap_or_default()", false) + ";";
                return HoistedCodes([V(0)], Place(inst!), (p, x) => $"{{ let {found} = {p}.take(&{x[0]}); {store} {found}.is_some() }}");
            }
            case "Dict.Clear": return $"{Place(inst!)}.clear()";
            case "CollectionsMarshal.SetCount": return HoistedCodes([V(1)], Place(a[0]), (p, x) => $"list::set_count(&mut {p}, {x[0]})", always: true);
            case "Dict.TryGetValue":
            {
                string found = Tmp("f");
                string store = a[1] is IDiscardOperation ? "" : Assign(a[1] is IDeclarationExpressionOperation de ? de.Expression : a[1], () => $"{found}.clone().unwrap_or_default()", false) + ";";
                return HoistedCodes([V(0)], Ref(inst!), (r, x) => $"{{ let {found} = {r}.try_get(&{x[0]}); {store} {found}.is_some() }}");
            }
            case "CollectionExtensions.GetValueOrDefault" when n == 2: return HoistedCodes([V(1)], Ref(a[0]), (r, x) => $"{r}.try_get(&{x[0]}).unwrap_or_default()");
            case "CollectionExtensions.GetValueOrDefault" when n == 3: return HoistedCodes([V(1), V(2)], Ref(a[0]), (r, x) => $"{r}.try_get(&{x[0]}).unwrap_or({x[1]})");
            case "Dict.GetValueOrDefault" when n == 1: return Rd((r, x) => $"{r}.try_get(&{x[0]}).unwrap_or_default()", 0);

            // ---- HashSet<T>
            case "Set.Add": return Mut((p, x) => $"{p}.add({x[0]})", 0);
            case "Set.Remove": return Mut((p, x) => $"{p}.remove(&{x[0]})", 0);
            case "Set.Contains": return Rd((r, x) => $"{r}.contains(&{x[0]})", 0);
            case "Set.Clear": return $"{Place(inst!)}.clear()";
            case "Set.UnionWith": return Mut((p, x) => $"{p}.union_with({x[0]})", 0);
            case "Set.ExceptWith": return Mut((p, x) => $"{p}.except_with({x[0]})", 0);
            // Against an empty collection that has a count, .NET clears the set; against a LINQ sequence it removes
            // item by item.
            case "Set.IntersectWith": return Mut((p, x) => $"{p}.intersect_with({x[0]}, {(IsDeferred(a[0]) ? "false" : "true")})", 0);
            case "Set.SetEquals": return Rd((r, x) => $"{r}.set_equals(&{x[0]})", 0);
            case "Set.Overlaps": return Rd((r, x) => $"{r}.overlaps(&{x[0]})", 0);

            // ---- Queue<T>, Stack<T>
            case "Queue.Enqueue": return Mut((p, x) => $"{p}.enqueue({x[0]})", 0);
            case "Queue.Dequeue": return $"{Place(inst!)}.dequeue()";
            case "Queue.Peek" or "Stack.Peek": return $"{Ref(inst!)}.peek()";
            case "Queue.Clear" or "Stack.Clear": return $"{Place(inst!)}.clear()";
            case "Queue.Contains" or "Stack.Contains": return Rd((r, x) => $"{r}.contains(&{x[0]})", 0);
            case "Stack.Push": return Mut((p, x) => $"{p}.push({x[0]})", 0);
            case "Stack.Pop": return $"{Place(inst!)}.pop()";
            case "Queue.TryDequeue" or "Stack.TryPop" or "Queue.TryPeek" or "Stack.TryPeek":
            {
                string found = Tmp("f");
                string take = def.Name switch { "TryDequeue" => $"{Place(inst!)}.try_dequeue()", "TryPop" => $"{Place(inst!)}.try_pop()", _ => $"{Ref(inst!)}.try_peek()" };
                string store = a[0] is IDiscardOperation ? "" : Assign(a[0] is IDeclarationExpressionOperation de ? de.Expression : a[0], () => $"{found}.clone().unwrap_or_default()", false) + ";";
                return $"{{ let {found} = {take}; {store} {found}.is_some() }}";
            }

            // ---- strings
            case "String.Equals" when inst != null && n >= 1: return $"({I()} == {V(0)})";
            case "String.Equals" when inst == null && n >= 2: return $"({V(0)} == {V(1)})";
            case "String.op_Equality": return $"({V(0)} == {V(1)})";
            case "String.op_Inequality": return $"({V(0)} != {V(1)})";
            case "String.Contains" when n == 1: return $"{I()}.contains({V(0)})";
            case "String.StartsWith" when n >= 1: return $"{I()}.starts_with({V(0)})";
            case "String.EndsWith" when n >= 1: return $"{I()}.ends_with({V(0)})";
            case "String.ToLowerInvariant" or "String.ToLower": return $"{I()}.to_lower()";
            case "String.ToUpperInvariant" or "String.ToUpper": return $"{I()}.to_upper()";
            case "String.Trim" when n == 0: return $"{I()}.trim()";
            case "String.Substring": return n == 1 ? $"{I()}.substring({V(0)}, i32::MAX)" : $"{I()}.substring({V(0)}, {V(1)})";
            case "String.IndexOf" when n == 1: return $"{I()}.index_of({V(0)})";
            case "String.Replace" when n == 2 && a[0].Type?.SpecialType == SpecialType.System_String: return $"{I()}.replace({V(0)}, {V(1)})";
            case "String.Split" when n == 1 && a[0].Type?.SpecialType == SpecialType.System_Char: return $"{I()}.split({V(0)})";
            case "String.IsNullOrEmpty": return $"{V(0)}.is_null_or_empty()";
            case "String.IsNullOrWhiteSpace": return $"{V(0)}.is_null_or_white_space()";
            case "String.Join" when n == 2 && World.CollOf(a[1].Type) == World.Coll.Vec && N(a[1].Type) == "Vec<Str>" && a[0].Type?.SpecialType == SpecialType.System_String: return $"Str::join({V(0)}, &{V(1)})";
            case "String.Concat" when a.All(x => x.Type?.SpecialType == SpecialType.System_String): return $"Str::concat(&[{string.Join(", ", a.Select(Expr))}])";
            case "String.Compare" or "String.CompareOrdinal" when n >= 2: return $"{V(0)}.compare({V(1)})";
            case "String.CompareTo" when n == 1: return $"{I()}.compare({V(0)})";
            case "String.ToString" when n == 0: return I();
            case "String.GetHashCode": throw new Unsupported("string.GetHashCode (random per process in .NET)");

            // ---- object
            case "Object.ToString" or "ValueType.ToString" or "Enum.ToString" or "Int32.ToString" or "Decimal.ToString" or "Boolean.ToString" or "UInt32.ToString" or "UInt64.ToString" or "Int64.ToString" when n == 0:
                return $"rt::to_s({Expr(Unconverted(inst!))}, c)";
            case "Object.Equals" when inst != null && n == 1: return $"({Expr(Unboxed(inst))} == {Expr(Unboxed(a[0]))})";
            case "Object.Equals" when inst == null && n == 2: return $"({Expr(Unboxed(a[0]))} == {Expr(Unboxed(a[1]))})";
            case "Object.ReferenceEquals": return $"({Expr(Unboxed(a[0]))} == {Expr(Unboxed(a[1]))})";
            case "Object.GetType": return $"g::kinds::kind_of({I()}, c)";
            case "Object.MemberwiseClone": return $"g::state::memberwise_clone({I()}, c)";
            case "IEquatable.Equals" or "Int32.Equals" or "UInt32.Equals" or "UInt64.Equals" or "Boolean.Equals" or "Decimal.Equals" when n == 1: return $"({I()} == {V(0)})";
            case "IComparable.CompareTo" or "Int32.CompareTo" or "Decimal.CompareTo" or "UInt32.CompareTo" or "UInt64.CompareTo" or "Single.CompareTo" or "Double.CompareTo" or "Int64.CompareTo" when n == 1:
                return $"rt::compare({I()}, {V(0)}, c)";
            case "EqualityComparer.Equals" when n == 2: return $"({V(0)} == {V(1)})";
            case "Comparer.Compare" when n == 2: return $"rt::compare({V(0)}, {V(1)}, c)";
            case "Type.IsAssignableFrom": return $"g::kinds::is_subclass({V(0)}, {I()})";
            case "Type.IsSubclassOf": return $"({I()} != {V(0)} && g::kinds::is_subclass({I()}, {V(0)}))";
            case "Type.IsInstanceOfType": return $"rt::is({V(0)}, {I()}, c)";
            case "Type.op_Equality": return $"({V(0)} == {V(1)})";
            case "Type.op_Inequality": return $"({V(0)} != {V(1)})";
            case "Enum.Equals" when n == 1: return $"({Expr(Unconverted(inst!))} == {Expr(Unconverted(a[0]))})";
            case "Enum.CompareTo" when n == 1: return $"rt::compare({Expr(Unconverted(inst!))}, {Expr(Unconverted(a[0]))}, c)";
            case "Enum.HasFlag": return $"{Expr(Unconverted(inst!))}.has_flag({Expr(Unconverted(a[0]))})";

            // ---- nullable values
            case "Nullable.GetValueOrDefault": return n == 0 ? $"{I()}.unwrap_or_default()" : $"{I()}.unwrap_or({V(0)})";

            // ---- numbers
            case "Math.Max" or "Mathf.Max" or "MathF.Max" or "Decimal.Max" or "Int32.Max": return $"rt::max({V(0)}, {V(1)})";
            case "Math.Min" or "Mathf.Min" or "MathF.Min" or "Decimal.Min" or "Int32.Min": return $"rt::min({V(0)}, {V(1)})";
            // Of an int, .NET throws where the value has no positive (int.MinValue).
            case "Math.Abs" when a[0].Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64: return $"rt::abs_checked({V(0)})";
            case "Math.Abs" or "Mathf.Abs" or "MathF.Abs" or "Decimal.Abs": return $"{V(0)}.abs()";
            case "KeyValuePair.Deconstruct" when n == 2:
            {
                string d = Tmp("d");
                return $"{{ let {d} = {I()}; {Assign(a[0], () => $"{d}.key", false)}; {Assign(a[1], () => $"{d}.value", false)}; }}";
            }
            case "BitOperations.RotateLeft": return $"{V(0)}.rotate_left(({V(1)}) as u32)";
            case "BitOperations.RotateRight": return $"{V(0)}.rotate_right(({V(1)}) as u32)";
            case "Math.Floor" or "MathF.Floor" or "Mathf.Floor" or "Decimal.Floor": return $"{V(0)}.floor()";
            case "Math.Ceiling" or "MathF.Ceiling" or "Mathf.Ceil" or "Decimal.Ceiling": return $"{V(0)}.{(a[0].Type?.SpecialType == SpecialType.System_Decimal ? "ceiling" : "ceil")}()";
            case "Math.Truncate" or "MathF.Truncate" or "Decimal.Truncate": return $"{V(0)}.{(a[0].Type?.SpecialType == SpecialType.System_Decimal ? "truncate" : "trunc")}()";
            case "Math.Round" or "MathF.Round" or "Mathf.Round" or "Decimal.Round":
                if (a[0].Type?.SpecialType == SpecialType.System_Decimal)
                    return n == 1 ? $"{V(0)}.round()" : n == 2 && a[1].Type?.SpecialType == SpecialType.System_Int32 ? $"{V(0)}.round_to({V(1)}, MidpointRounding::ToEven)"
                        : n == 2 ? $"{V(0)}.round_to(0, {V(1)})" : $"{V(0)}.round_to({V(1)}, {V(2)})";
                return n == 1 ? $"rt::round_even({V(0)})" : n == 2 && a[1].Type?.SpecialType != SpecialType.System_Int32 ? $"rt::round_mode({V(0)}, {V(1)})" : null;
            case "Math.Sign" or "Mathf.Sign" or "MathF.Sign" or "Decimal.Sign": return $"rt::sign({V(0)})";
            case "Math.Clamp" or "Mathf.Clamp": return $"rt::clamp({V(0)}, {V(1)}, {V(2)})";
            case "Math.Pow" or "MathF.Pow" or "Mathf.Pow": return $"{V(0)}.powf({V(1)})";
            case "Math.Sqrt" or "MathF.Sqrt" or "Mathf.Sqrt": return $"{V(0)}.sqrt()";
            case "Math.Log" or "MathF.Log" or "Mathf.Log" when n == 1: return $"{V(0)}.ln()";
            case "Mathf.CeilToInt": return $"({V(0)}.ceil() as i32)";
            case "Mathf.FloorToInt": return $"({V(0)}.floor() as i32)";
            case "Mathf.RoundToInt": return $"(rt::round_even({V(0)}) as i32)";
            case "Mathf.Lerp": return $"rt::lerp({V(0)}, {V(1)}, {V(2)})";
            case "Decimal.ToInt32": return $"{V(0)}.to_i32()";
            case "Decimal.op_Addition": return $"({V(0)} + {V(1)})";
            case "Decimal.op_Subtraction": return $"({V(0)} - {V(1)})";
            case "Decimal.op_Multiply": return $"({V(0)} * {V(1)})";
            case "Decimal.op_Division": return $"({V(0)} / {V(1)})";
            case "Decimal.op_Modulus": return $"({V(0)} % {V(1)})";
            case "Decimal.op_UnaryNegation": return $"(-{V(0)})";

            // ---- tasks
            case "Task.FromResult": return $"Ok({V(0)})";
            case "Task.WhenAll" when n == 1 && World.CollOf(a[0].Type) != World.Coll.None:
            {
                var element = a[0].Type is IArrayTypeSymbol arr ? arr.ElementType : ((INamedTypeSymbol)a[0].Type!).TypeArguments[0];
                return World.IsTask(element, out var result) && result != null ? $"rt::when_all_values({V(0)})" : $"rt::when_all({V(0)})";
            }
            case "Task.ConfigureAwait": return I();
            case "Task.Yield" or "Task.Delay": throw new Dropped("Task." + def.Name);

            // ---- argument checks
            case "ArgumentNullException.ThrowIfNull": return $"rt::throw_if_null(&{Ref(Unboxed(a[0]))}, \"{Where}\")";
            case "ArgumentOutOfRangeException.ThrowIfNegative": return $"rt::throw_if({V(0)} < <{T(a[0].Type)} as Default>::default(), \"{Where}\")";
            case "ArgumentOutOfRangeException.ThrowIfNegativeOrZero": return $"rt::throw_if({V(0)} <= <{T(a[0].Type)} as Default>::default(), \"{Where}\")";
            case "ArgumentOutOfRangeException.ThrowIfZero": return $"rt::throw_if({V(0)} == <{T(a[0].Type)} as Default>::default(), \"{Where}\")";
            case "ArgumentOutOfRangeException.ThrowIfGreaterThan": return $"rt::throw_if({V(0)} > {V(1)}, \"{Where}\")";
            case "ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual": return $"rt::throw_if({V(0)} >= {V(1)}, \"{Where}\")";
            case "ArgumentOutOfRangeException.ThrowIfLessThan": return $"rt::throw_if({V(0)} < {V(1)}, \"{Where}\")";
            case "ArgumentOutOfRangeException.ThrowIfLessThanOrEqual": return $"rt::throw_if({V(0)} <= {V(1)}, \"{Where}\")";
            case "ArgumentException.ThrowIfNullOrEmpty" or "ArgumentException.ThrowIfNullOrWhiteSpace": return $"rt::throw_if({V(0)}.is_null_or_empty(), \"{Where}\")";
        }
        _ = elem;
        return null;
    }

    private static IOperation Unconverted(IOperation op)
    {
        while (op is IConversionOperation { IsTryCast: false, OperatorMethod: null } cv && cv.Operand.Type != null) op = cv.Operand;
        return op;
    }

    internal static int ArityOf(IOperation arg)
    {
        var t = arg.Type as INamedTypeSymbol;
        if (t?.DelegateInvokeMethod != null) return t.DelegateInvokeMethod.Parameters.Length;
        arg = Unconverted(arg);
        if (arg is IDelegateCreationOperation dc) arg = dc.Target;
        return arg is IAnonymousFunctionOperation f ? f.Symbol.Parameters.Length : 1;
    }

    /// <summary>Max(selector), Min(selector), Average(selector): the selected values first.</summary>
    /// <summary>What Sum or Average adds (.NET 9 adds each in its own way, rt/linq.rs): the numbers of the sequence, or
    /// what the selector gives.</summary>
    private static string NumberKind(IMethodSymbol m, int n)
    {
        // A reduced extension method has no parameter for the sequence it is called on.
        int at = (n == 1 ? 0 : 1) - (m.Parameters.Length < n ? 1 : 0);
        var p = at < 0 ? m.ReceiverType as INamedTypeSymbol : m.Parameters[at].Type as INamedTypeSymbol;
        if (at < 0 && p != null) p = p.AllInterfaces.Prepend(p).FirstOrDefault(i => i.Name == "IEnumerable" && i.TypeArguments.Length == 1) ?? p;
        var t = n == 1 ? p?.TypeArguments.FirstOrDefault() : p?.TypeArguments.LastOrDefault();
        return t?.SpecialType switch
        {
            SpecialType.System_Int32 => "i32",
            SpecialType.System_Int64 => "i64",
            SpecialType.System_Single => "f32",
            SpecialType.System_Double => "f64",
            SpecialType.System_Decimal => "dec",
            _ => throw new Unsupported($"{m.Name} over {t?.ToDisplayString() ?? "?"}"),
        };
    }

    private string LinqSelect(string source, IOperation selector) => $"linq::select({source}, {Closure(selector, 1)}, c)";

    private string? BclProp(IPropertyReferenceOperation op)
    {
        var p = op.Property.OriginalDefinition;
        var type = p.ContainingType;
        string tk = TypeKey(type);
        var inst = op.Instance;
        switch ($"{tk}.{p.Name}")
        {
            case "Vec.Count" or "Vec.Length" or "Set.Count" or "Dict.Count" or "Queue.Count" or "Stack.Count": return $"({Ref(inst!)}.len() as i32)";
            case "Vec.this[]" when op.Arguments.Length == 1 && op.Arguments[0].Value.Type?.SpecialType == SpecialType.System_Int32:
                return Hoisted([op.Arguments[0].Value], Ref(inst!), (r, a) => Owned($"{r}[({a[0]}) as usize]", op.Type));
            case "Dict.this[]": return Hoisted([op.Arguments[0].Value], Ref(inst!), (r, a) => $"{r}.get(&{a[0]})");
            case "Dict.Keys": return $"{Ref(inst!)}.keys()";
            case "Dict.Values": return $"{Ref(inst!)}.values()";
            case "String.Length": return $"{Expr(inst!)}.len16()";
            case "String.this[]": return $"{Expr(inst!)}.char_at({Expr(op.Arguments[0].Value)})";
            case "Nullable.HasValue": return $"{Ref(inst!)}.is_some()";
            case "Nullable.Value": return $"{Expr(inst!)}.unwrap()";
            case "KeyValuePair.Key" when inst is ILocalReferenceOperation { Local: var pk } && pairs.TryGetValue(pk, out var kp): return $"{kp.Key}.clone()";
            case "KeyValuePair.Value" when inst is ILocalReferenceOperation { Local: var pv } && pairs.TryGetValue(pv, out var vp): return $"{vp.Value}.clone()";
            case "KeyValuePair.Key": return Owned($"{Ref(inst!)}.key", op.Type);
            case "KeyValuePair.Value": return Owned($"{Ref(inst!)}.value", op.Type);
            case "IGrouping.Key": return Owned($"{Ref(inst!)}.key", op.Type);
            case "Task.CompletedTask" or "ValueTask.CompletedTask": return "Ok(())";
            case "Task.IsCompleted" or "Task.IsCompletedSuccessfully": return "true";
            case "Type.Name" or "MemberInfo.Name": return $"g::kinds::kind_name({Expr(inst!)})";
            case "EqualityComparer.Default" or "Comparer.Default": return "()";
        }
        return null;
    }

    private string? BclField(IFieldReferenceOperation op)
    {
        var f = op.Field;
        return $"{f.ContainingType.Name}.{f.Name}" switch
        {
            "String.Empty" => "Str::EMPTY",
            "ImmutableHashSet.Empty" => $"<{T(f.ContainingType)}>::new()",
            "Decimal.Zero" => "Dec::ZERO",
            "Decimal.One" => "Dec::ONE",
            "Decimal.MinusOne" => "Dec::MINUS_ONE",
            "Decimal.MaxValue" => "Dec::MAX_VALUE",
            "Decimal.MinValue" => "Dec::MIN_VALUE",
            _ => null,
        };
    }

    private string? BclNew(IObjectCreationOperation op)
    {
        var type = (INamedTypeSymbol)op.Type!;
        var a = op.Arguments.OrderBy(x => x.Parameter!.Ordinal).Select(x => x.Value).ToList();
        bool isInt(int i) => a[i].Type?.SpecialType == SpecialType.System_Int32;
        for (INamedTypeSymbol? b = type; b != null; b = b.BaseType)
            if (World.MetaName(b) == "System.Exception") return $"Exception::new(\"{type.Name}\")";
        if (World.IsNullable(type, out _)) return $"Some({Expr(a[0])})";
        if (type.IsTupleType) return "(" + string.Join(", ", a.Select(Expr)) + ")";
        string targs = string.Join(", ", type.TypeArguments.Select(x => T(x)));
        switch (World.CollOf(type))
        {
            case World.Coll.Vec when World.MetaName(type) == "System.Collections.Generic.List`1":
                return a.Count == 0 || isInt(0) ? $"Vec::<{targs}>::new()" : a.Count == 1 ? IterSource(a[0]) : null;
            case World.Coll.Dict:
                if (a.Count == 0 || (a.Count == 1 && isInt(0))) return $"Dict::<{targs}>::new()";
                // .NET copies a dictionary's live entries in order, no freed slot.
                return a.Count == 1 && World.CollOf(a[0].Type) == World.Coll.Dict ? $"{Expr(a[0])}.packed()" : null;
            case World.Coll.Set:
                if (a.Count == 0 || (a.Count == 1 && isInt(0))) return $"Set::<{targs}>::new()";
                if (a.Count == 1 && World.CollOf(Unconverted(a[0]).Type) == World.Coll.Set) return $"Set::copy_of(&{Expr(Unconverted(a[0]))})";
                return a.Count == 1 && World.CollOf(Unconverted(a[0]).Type) != World.Coll.None ? $"Set::from_vec({IterSource(a[0])})" : null;
            case World.Coll.Queue:
                if (a.Count == 0 || isInt(0)) return $"Queue::<{targs}>::new()";
                return a.Count == 1 ? $"Queue::from_vec({IterSource(a[0])})" : null;
            case World.Coll.Stack:
                if (a.Count == 0 || isInt(0)) return $"Stack::<{targs}>::new()";
                return a.Count == 1 ? $"Stack::from_vec({IterSource(a[0])})" : null;
        }
        switch (World.MetaName(type))
        {
            case "System.Collections.Generic.KeyValuePair`2" when a.Count == 2: return $"KeyValuePair {{ key: {Expr(a[0])}, value: {Expr(a[1])} }}";
            case "System.Decimal" when a.Count == 1: return Numeric(Expr(a[0]), a[0].Type!, type);
        }
        return null;
    }

    private string? BclConversion(IConversionOperation cv, IMethodSymbol om)
    {
        if (om.ContainingType.SpecialType == SpecialType.System_Decimal && cv.Operand.Type != null) return Convert(Expr(cv.Operand), cv.Operand.Type, cv.Type!, null);
        return null;
    }
}
