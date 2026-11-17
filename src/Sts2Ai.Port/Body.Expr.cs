using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Sts2Ai.Port;

public sealed partial class Body
{
    private string N(ITypeSymbol? t) => w.RType(t, norm: true);

    // ---------------------------------------------------------------- statements that are expressions

    private string ExprStmt(IOperation op)
    {
        switch (op)
        {
            // Game objects that come from a screen (the cards picked on it): a branch there is none of headless.
            case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var chosen } } pick when !droppedLocals.Contains(chosen) && HoldsObjects(chosen.Type) && IsPresentation(pick.Value):
                w.Rep.Count("a local set from a screen left as a panic");
                return $"panic!(\"{Where}: {NameOf(chosen)} comes from presentation\")";
            case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var held } } na when nullLocals.Contains(held):
                return $"{NameOf(held)} = {MaybeNull(na.Value)}";
            case ISimpleAssignmentOperation a:
            {
                string there = PresenceWrite(a.Target, a.Value) is { } set ? $"let __there = {set.Split(" = ")[1]}; " : "";
                string code = Assign(a.Target, () => Guard(a.Value, "a write of presentation to state"), false);
                return there.Length == 0 ? code : $"{{ {there}{code}; {PresenceWrite(a.Target, a.Value)!.Split(" = ")[0]} = __there; }}";
            }
            case ICompoundAssignmentOperation ca: return Compound(ca, false);
            case IIncrementOrDecrementOperation inc: return Increment(inc, false);
            case ICoalesceAssignmentOperation co:
            {
                string value = Expr(co.Value);
                if (Presence(co.Target) is { } has)
                    return $"if !{has} {{ {Assign(co.Target, () => Wrap(value, co.Value.Type, co.Target.Type), false)}; {has.Replace("::of(", "::of_mut(")} = true; }}";
                return $"if rt::is_null(&{Expr(co.Target)}) {{ {Assign(co.Target, () => Wrap(value, co.Value.Type, co.Target.Type), false)}; }}";
            }
            case IConditionalAccessOperation ca: return ConditionalAccess(ca, true);
            case IAwaitOperation aw: return Expr(aw);
            case IInvocationOperation inv:
            {
                string code = Expr(inv);
                if (code.Length == 0) return "";
                if (inv.Type == null || inv.Type.SpecialType == SpecialType.System_Void) return code;
                if (World.IsTask(inv.Type, out _))
                {
                    // A task nobody awaits has run by now, or has stopped at a card prompt with no answer yet. A stop
                    // is the caller's too: it goes up where the member can pass it on, and is noted on the combat
                    // where it cannot (the world looks there).
                    w.Rep.Note("a task that is not awaited", Where);
                    return frames.Peek().ReturnsTask ? $"let _ = {code}?" : $"if {code}.is_err() {{ c.choices.stopped = true; }}";
                }
                return $"let _ = {code}";
            }
            case IDeconstructionAssignmentOperation de: return Deconstruct(de);
            default: return $"let _ = {Expr(op)}";
        }
    }

    /// <summary>Whether the type is a game object or a collection of them.</summary>
    private bool HoldsObjects(ITypeSymbol t) => w.IsObj(t) || (t is INamedTypeSymbol n && World.CollOf(n) != World.Coll.None && n.TypeArguments.Any(HoldsObjects));

    /// <summary>Whether the expression is presentation as a whole.</summary>
    private bool IsPresentation(IOperation op)
    {
        try { Expr(op); return false; }
        catch (Dropped) { return true; }
        catch (Unsupported) { return false; }
    }

    /// <summary>The locals that stand for a nullable collection field, with the flag that says the collection is there.</summary>
    private readonly Dictionary<ISymbol, string> aliasPresence = new(Eq);

    /// <summary>For an expression that is a collection field the game declares nullable: the flag that says it is
    /// there. A collection is a value here and has no null of its own.</summary>
    private string? Presence(IOperation op)
    {
        while (op is IConversionOperation { IsTryCast: false } cv && World.CollOf(cv.Operand.Type) != World.Coll.None) op = cv.Operand;
        if (op is ILocalReferenceOperation l) return aliasPresence.GetValueOrDefault(l.Local);
        IFieldSymbol? field = op switch
        {
            IFieldReferenceOperation { Field.IsStatic: false } f => f.Field.OriginalDefinition,
            IPropertyReferenceOperation { Property: { IsStatic: false } p } => Forwarded(p.OriginalDefinition),
            _ => null,
        };
        // A value's own field, or the field behind its property.
        if (op is IPropertyReferenceOperation { Property: { IsStatic: false } vp } vpr && w.IsValue(vp.ContainingType)
            && vp.ContainingType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => Eq.Equals(f.AssociatedSymbol, vp.OriginalDefinition)) is { } auto && w.NullableColl(auto))
            return $"{(vpr.Instance == null ? "me" : Place(vpr.Instance))}.{w.StateField(auto)}__set";
        if (field == null || !w.NullableColl(field)) return null;
        if (op is IFieldReferenceOperation { Instance: { } inst } vf && w.IsValue(vf.Field.ContainingType)) return $"{Place(inst)}.{w.StateField(field)}__set";
        return FieldPlaceOf(op) is { } place ? place + "__set" : null;
    }

    /// <summary>What a write to a nullable collection field does to its flag: `flag = whether the value is there`.</summary>
    private string? PresenceWrite(IOperation target, IOperation value)
    {
        if (Presence(target) is not { } flag) return null;
        string there = IsNullConst(value) ? "false" : Presence(value) ?? "true";
        if (there != "true" && there != "false") w.Rep.Count("a nullable collection field set from another");
        return $"{flag.Replace("::of(", "::of_mut(")} = {there}";
    }

    private string Guard(IOperation op, string why)
    {
        try { return Expr(op); }
        catch (Dropped d) { throw new Unsupported($"{why} ({d.Message})"); }
    }

    // ---------------------------------------------------------------- expressions

    /// <summary>Rust code that gives an owned value of the operation's type.</summary>
    /// <summary>Receivers already computed into a local: the object a change in place goes through.</summary>
    private readonly Dictionary<IOperation, string> pinned = new();

    /// <summary>The object a place is a field of, when it is more than a name: it is computed once, before the
    /// arguments of the change, as in C#.</summary>
    private IOperation? UnstableReceiver(IOperation place)
    {
        while (place is IConversionOperation { IsTryCast: false } cv && World.CollOf(cv.Operand.Type) != World.Coll.None) place = cv.Operand;
        IOperation? recv = place switch { IFieldReferenceOperation f => f.Instance, IPropertyReferenceOperation pr => pr.Instance, _ => null };
        return recv != null && recv.Type != null && w.IsObj(recv.Type) && !StableReceiver(recv) ? recv : null;
    }

    private string Expr(IOperation op)
    {
        if (pinned.TryGetValue(op, out var pin)) return pin;
        // An enumerator taken by hand: MoveNext steps the iterator, Current is the item it is at.
        if (op is IInvocationOperation { TargetMethod.Name: "MoveNext", Instance: ILocalReferenceOperation { Local: var en } } && enumerators.Contains(en))
            return $"{{ {NameOf(en)}_at = {NameOf(en)}_it.next(); {NameOf(en)}_at.is_some() }}";
        if (op is IPropertyReferenceOperation { Property.Name: "Current", Instance: ILocalReferenceOperation { Local: var en2 } } && enumerators.Contains(en2))
            return $"{NameOf(en2)}_at.clone().expect(\"an enumerator before its first item or after its last\")";
        if (op.ConstantValue.HasValue && op is not IInterpolatedStringOperation)
        {
            if (op.ConstantValue.Value == null) return Null(op.Type);
            if (op is IFieldReferenceOperation { Field: { ContainingType.TypeKind: TypeKind.Enum } ef }) return $"{T(ef.ContainingType)}::{World.EnumMember(ef.Name)}";
            if (op.Type != null) return Literal(op.Type, op.ConstantValue.Value);
        }
        switch (op)
        {
            case ILiteralOperation lit: return Literal(lit.Type!, lit.ConstantValue.Value);
            case ILocalReferenceOperation l: return Read(l.Local, l.Type);
            case IParameterReferenceOperation p: return Read(p.Parameter, p.Type);
            case IInstanceReferenceOperation ir:
                if (ir.ReferenceKind == InstanceReferenceKind.ImplicitReceiver) return implicitReceivers.Peek() + (w.IsCopy(ir.Type) ? "" : ".clone()");
                if (ir.ReferenceKind == InstanceReferenceKind.PatternInput) return condInstances.Peek();
                if (ir.ReferenceKind != InstanceReferenceKind.ContainingTypeInstance) throw new Unsupported($"instance reference {ir.ReferenceKind}");
                return isStructMember ? "me.clone()" : "me";
            case IFieldReferenceOperation f: return FieldGet(f);
            case IPropertyReferenceOperation p: return PropGet(p);
            case IEventReferenceOperation ev:
                if (w.StateEvents.Contains(ev.Event.OriginalDefinition)) throw new Unsupported($"the event {ev.Event.Name} read as a value");
                throw new Dropped("event " + ev.Event.Name);
            case IEventAssignmentOperation ea: return Subscribe(ea);
            case IInvocationOperation inv: return Invoke(inv);
            case IObjectCreationOperation oc: return New(oc);
            case IWithOperation { Operand.Type: var wt } wo when w.IsValue(wt):
            {
                // A copy of a value with some of its fields set.
                string o = Tmp("o");
                implicitReceivers.Push(o);
                var sb = new StringBuilder($"{{ let mut {o} = {Expr(wo.Operand)}.clone(); ");
                foreach (var init in wo.Initializer.Initializers)
                {
                    try { sb.Append(ExprStmt(init)).Append("; "); }
                    catch (Dropped) { LeftOut++; }
                }
                implicitReceivers.Pop();
                return sb.Append($"{o} }}").ToString();
            }
            case IConversionOperation cv: return Conv(cv);
            case IBinaryOperation b: return Binary(b);
            case IUnaryOperation u: return Unary(u);
            case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var held } } na when nullLocals.Contains(held):
                throw new Unsupported("a collection that can be null, set inside an expression");
            case ISimpleAssignmentOperation a:
            {
                string code = Assign(a.Target, () => Guard(a.Value, "a write of presentation to state"), true);
                return PresenceWrite(a.Target, a.Value) is { } set ? $"{{ let __a = {code}; {set}; __a }}" : code;
            }
            case ICompoundAssignmentOperation ca: return Compound(ca, true);
            case IIncrementOrDecrementOperation inc: return Increment(inc, true);
            case ICoalesceOperation co: return Coalesce(co);
            case ICoalesceAssignmentOperation coa:
            {
                string value = Expr(coa.Value);
                if (Presence(coa.Target) is { } has)
                    return $"{{ if !{has} {{ {Assign(coa.Target, () => Wrap(value, coa.Value.Type, coa.Target.Type), false)}; {has.Replace("::of(", "::of_mut(")} = true; }} {Expr(coa.Target)} }}";
                return $"{{ if rt::is_null(&{Expr(coa.Target)}) {{ {Assign(coa.Target, () => Wrap(value, coa.Value.Type, coa.Target.Type), false)}; }} {Expr(coa.Target)} }}";
            }
            case IConditionalOperation c:
            {
                string test = Expr(c.Condition);
                if (test == "true") return Expr(c.WhenTrue);
                if (test == "false") return Expr(c.WhenFalse!);
                return $"(if {test} {{ {Expr(c.WhenTrue)} }} else {{ {Expr(c.WhenFalse!)} }})";
            }
            case IConditionalAccessOperation ca: return ConditionalAccess(ca, false);
            case IConditionalAccessInstanceOperation: return condInstances.Peek();
            case IIsTypeOperation it: return IsType(Expr(it.ValueOperand), it.ValueOperand.Type, it.TypeOperand);
            case IIsPatternOperation ip when NullPattern(ip.Pattern) is { } nullTest && Presence(ip.Value) is { } there:
                return nullTest ? $"!{there}" : there;
            case IIsPatternOperation ip:
            {
                string s = Tmp("s");
                string subject;
                try { subject = Expr(ip.Value); }
                catch (Dropped) when (ip.Value.Type is { IsReferenceType: true } && NullPattern(ip.Pattern) is { } isNull)
                {
                    // There is no presentation object in the engine, as in the headless game: it is null.
                    w.Rep.Count("null test on presentation (taken as null)");
                    return isNull ? "true" : "false";
                }
                return $"{{ let {s} = {subject}; {Pattern(ip.Pattern, s, ip.Value.Type!)} }}";
            }
            case ISwitchExpressionOperation se: return SwitchExpr(se);
            case IAwaitOperation aw:
            {
                string inner = Expr(aw.Operation);
                if (!World.IsTask(aw.Operation.Type, out _)) throw new Unsupported($"await of {aw.Operation.Type?.Name}");
                // A hook's wait gives a hook that still waits for the player (Stop::Pending, manual/hook_player_choice_context.rs):
                // awaited on its own, it is awaited as Task.WhenAll awaits it, which runs the hook's action.
                if (Unwrapped(aw.Operation) is IInvocationOperation { TargetMethod: { Name: "WaitForCompletion", ContainingType.Name: "HookPlayerChoiceContext" } })
                    return $"rt::when_all(vec![{inner}], c)?";
                return $"{inner}?";
            }
            case IDelegateCreationOperation dc: return Delegate(dc.Target, dc.Type!);
            case IAnonymousFunctionOperation: throw new Unsupported("a lambda without a delegate type");
            case IArrayCreationOperation ac:
                if (ac.DimensionSizes.Length != 1) throw new Unsupported("multi-dimensional array");
                if (ac.Initializer != null) return $"vec![{string.Join(", ", Elements(ac.Initializer.ElementValues))}]";
                return $"vec![<{T(((IArrayTypeSymbol)ac.Type!).ElementType)} as Default>::default(); ({Expr(ac.DimensionSizes[0])}) as usize]";
            case IArrayElementReferenceOperation ae:
                if (ae.Indices.Length != 1) throw new Unsupported("multi-dimensional array");
                return Hoisted([ae.Indices[0]], Ref(ae.ArrayReference), (r, a) => Owned($"{r}[({a[0]}) as usize]", ae.Type));
            case ITupleOperation tu: return "(" + string.Join(", ", tu.Elements.Select(Expr)) + (tu.Elements.Length == 1 ? ",)" : ")");
            case IDefaultValueOperation dv: return op.Type == null ? "Default::default()" : $"<{T(op.Type)} as Default>::default()";
            case ITypeOfOperation to: return KindExpr(to.TypeOperand);
            case IInterpolatedStringOperation istr: return Interpolated(istr);
            case IThrowOperation th: return Throw(th);
            case ITranslatedQueryOperation tq: return Expr(tq.Operation);
            case IDeclarationExpressionOperation de: return Expr(de.Expression);
            case IDiscardOperation: throw new Unsupported("discard as a value");
            case ICollectionExpressionOperation ce: return CollectionExpr(ce);
            case IInvalidOperation: throw new Unsupported("code the compiler could not bind");
            case IParenthesizedOperation po: return Expr(po.Operand);
            default: throw new Unsupported($"operation {op.Kind}");
        }
    }

    /// <summary>For `x is null` and `x is not null` (and `x is { }`): what the pattern says of a null; null for any other pattern.</summary>
    private static bool? NullPattern(IPatternOperation p) => p switch
    {
        IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } } => true,
        INegatedPatternOperation { Pattern: var inner } => NullPattern(inner) is { } b ? !b : null,
        IRecursivePatternOperation or ITypePatternOperation or IDeclarationPatternOperation { MatchesNull: false } => false,
        _ => null,
    };

    /// <summary>The items of a list written out. One that is presentation (a text variable among the numbers) is left out.</summary>
    private List<string> Elements(IEnumerable<IOperation> items)
    {
        var list = new List<string>();
        foreach (var item in items)
        {
            try { list.Add(Expr(item)); }
            catch (Dropped) { Leave(item); }
        }
        return list;
    }

    private string CloneOf(ITypeSymbol? t) => w.IsCopy(t) ? "" : ".clone()";

    /// <summary>The locals of collection type that can be null, as an Option (World.NullLocals), and whether the
    /// member's own result can be (World.ReturnsNullColl).</summary>
    private HashSet<ILocalSymbol> nullLocals = new(Eq);

    /// <summary>The body being written, where each collection local gets its value, the locals already looked at,
    /// and the target of the assignment being written (a local set, not changed).</summary>
    private IOperation? bodyOp, assignTarget;
    private Dictionary<ILocalSymbol, List<IOperation>>? collSources;
    private readonly HashSet<ISymbol> aliasChecked = new(Eq);

    /// <summary>A collection local changed in place that the rules for a second name (Body.Declare, ForEach,
    /// GetOrInsert) left a copy: when C# has the same collection under another name there (a collection the state
    /// holds, or one stored into it), the change would not reach it, and the member is not translated.</summary>
    private void AliasCheck(ILocalSymbol l, IOperation site)
    {
        if (bodyOp == null || World.CollOf(l.Type) == World.Coll.None || aliases.ContainsKey(l) || !aliasChecked.Add(l)) return;
        collSources ??= World.CollectionSources(bodyOp);
        string Line(IOperation o) => o.Syntax.ToString().Replace("\r", "").Split('\n')[0].Trim();
        bool State(IOperation v) => w.HoldsState(v, collSources, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default));
        var from = collSources.GetValueOrDefault(l)?.FirstOrDefault(State);
        // Every change made to a collection of the local's own making (`x = field; if (x == null) { x = new(); x.Add(..); }`).
        if (from != null && w.ChangesOf(l, bodyOp).All(ch => OnOwn(l, ch, State))) from = null;
        string? why = from != null ? $"may be a collection the state holds ({Line(from)})"
            : w.StoredThenChanged(l, bodyOp) is { } st ? $"stored into the state ({Line(st)}), then changed" : null;
        if (why == null) return;
        w.Rep.Alias(Where, site, $"{l.Name}: {why}; not translated");
        throw new Unsupported($"a collection changed in place under a second name ({l.Name}: {why})");
    }

    /// <summary>Whether the collection a change is made to is one the local was last set to a new collection of its
    /// own, in a block around the change, with no loop around the change setting it from the state.</summary>
    private static bool OnOwn(ILocalSymbol l, IOperation change, Func<IOperation, bool> state)
    {
        bool Sets(IOperation o, out IOperation? value)
        {
            value = o switch
            {
                IExpressionStatementOperation { Operation: ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var t }, Value: var v } } when Eq.Equals(t, l) => v,
                IVariableDeclarationGroupOperation g => g.Declarations.SelectMany(d => d.Declarators).FirstOrDefault(d => Eq.Equals(d.Symbol, l)) is { } vd ? vd.Initializer?.Value ?? (vd.Parent as IVariableDeclarationOperation)?.Initializer?.Value : null,
                _ => null,
            };
            return value != null;
        }
        for (IOperation? x = change; x?.Parent != null; x = x.Parent)
        {
            if (x.Parent is ILoopOperation loop && loop.DescendantsAndSelf().Any(o => o is ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var t }, Value: var v } && Eq.Equals(t, l) && state(v)))
                return false;
            if (x.Parent is not IBlockOperation block) continue;
            int at = block.Operations.IndexOf(x);
            for (int i = at - 1; i >= 0; i--)
                if (Sets(block.Operations[i], out var value)) return !state(value!);
        }
        return false;
    }
    private bool nullReturn;
    /// <summary>Set for the one call whose Option itself is wanted, not the collection in it.</summary>
    private bool takeOption;

    /// <summary>A value of collection type as an Option: None for null, the Option of a local or a call that can be
    /// null, Some of any other.</summary>
    private string MaybeNull(IOperation op)
    {
        var bare = op;
        while (bare is IConversionOperation { IsTryCast: false } cv) bare = cv.Operand;
        switch (bare)
        {
            case ILiteralOperation { ConstantValue: { HasValue: true, Value: null } }:
            case IDefaultValueOperation:
                return "None";
            case ILocalReferenceOperation l when nullLocals.Contains(l.Local):
                return $"{NameOf(l.Local)}.clone()";
            case IInvocationOperation inv when w.ReturnsNullColl(inv.TargetMethod):
                takeOption = true;
                try { return Expr(bare); }
                finally { takeOption = false; }
            case IConditionalOperation { WhenFalse: { } otherwise } c:
                return $"if {Expr(c.Condition)} {{ {MaybeNull(c.WhenTrue)} }} else {{ {MaybeNull(otherwise)} }}";
            default:
                return $"Some({Expr(op)})";
        }
    }

    private string Read(ISymbol local, ITypeSymbol? type)
    {
        if (droppedLocals.Contains(local)) throw new Dropped(local.Name);
        // A collection that can be null, read as one: null is C#'s NullReferenceException.
        if (local is ILocalSymbol nl && nullLocals.Contains(nl)) return $"rt::nn(&{NameOf(local)})";
        if (aliases.TryGetValue(local, out var place)) return place + ".clone()";
        return NameOf(local) + CloneOf(type);
    }

    private string Null(ITypeSymbol? t)
    {
        if (t == null) return "Default::default()";
        if (w.IsDropped(t)) throw new Dropped(t.Name);
        if (w.IsObj(t)) return "Obj::NULL";
        if (t.SpecialType == SpecialType.System_String) return "Str::NULL";
        if (World.IsNullable(t, out _)) return "None";
        if (World.CollOf(t) != World.Coll.None) w.Rep.Count("null given as a collection (taken as empty)");
        return $"<{T(t)} as Default>::default()";
    }

    private string Literal(ITypeSymbol type, object? value)
    {
        if (value == null) return Null(type);
        if (World.IsNullable(type, out var inner)) return $"Some({Literal(inner, value)})";
        if (type.TypeKind == TypeKind.Enum)
        {
            var e = (INamedTypeSymbol)type;
            if (w.IsDropped(e)) throw new Dropped(e.Name);
            if (!World.IsSource(e)) return T(e) + "::" + (e.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value))?.Name ?? throw new Unsupported("library enum value"));
            if (w.IsDropped(e)) throw new Dropped(e.Name);
            var named = e.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
            return named != null ? $"{T(e)}::{World.EnumMember(named.Name)}" : $"{T(e)}({Literal(e.EnumUnderlyingType!, value)})";
        }
        switch (value)
        {
            case bool b: return b ? "true" : "false";
            case string s: return $"Str::lit({Lit(s)})";
            case char ch: return ch == '\'' ? "'\\''" : $"'{Escape(ch.ToString())}'";
            case decimal d:
            {
                int[] bits = decimal.GetBits(d);
                var mantissa = (new BigInteger((uint)bits[2]) << 64) | (new BigInteger((uint)bits[1]) << 32) | (uint)bits[0];
                bool neg = (bits[3] & int.MinValue) != 0;
                int scale = (bits[3] >> 16) & 0xff;
                // A negative zero (-0.0m) keeps its sign in .NET; a literal's mantissa cannot carry it.
                if (mantissa.IsZero && neg) return $"Dec::from_parts(0, 0, 0, true, {scale})";
                return $"Dec::lit({(neg ? "-" : "")}{mantissa}, {scale})";
            }
            case float f:
                if (float.IsNaN(f)) return "f32::NAN";
                if (float.IsInfinity(f)) return f > 0 ? "f32::INFINITY" : "f32::NEG_INFINITY";
                return Paren(f.ToString("R", CultureInfo.InvariantCulture) + "f32");
            case double d:
                if (double.IsNaN(d)) return "f64::NAN";
                if (double.IsInfinity(d)) return d > 0 ? "f64::INFINITY" : "f64::NEG_INFINITY";
                return Paren(d.ToString("R", CultureInfo.InvariantCulture) + "f64");
        }
        string suffix = type.SpecialType switch
        {
            SpecialType.System_Int32 => "i32", SpecialType.System_UInt32 => "u32", SpecialType.System_Int64 => "i64", SpecialType.System_UInt64 => "u64",
            SpecialType.System_Int16 => "i16", SpecialType.System_UInt16 => "u16", SpecialType.System_Byte => "u8", SpecialType.System_SByte => "i8",
            _ => throw new Unsupported($"literal of {type.Name}"),
        };
        return Paren(System.Convert.ToString(value, CultureInfo.InvariantCulture) + suffix);
    }

    private static string Paren(string s) => s.StartsWith('-') ? $"({s})" : s;

    // ---------------------------------------------------------------- members

    /// <summary>A fixed value from port.toml for a member, here or everywhere.</summary>
    private string? Value(ISymbol m)
    {
        if (m.ContainingType == null) return null;
        string key = m.ContainingType.Name + "." + m.Name;
        return w.Cfg.Values.TryGetValue($"{key}@{Where}", out var v) || w.Cfg.Values.TryGetValue(key, out v) ? v : null;
    }

    private static int TupleIndex(IFieldSymbol f)
    {
        var t = f.ContainingType;
        var real = f.CorrespondingTupleField ?? f;
        for (int i = 0; i < t.TupleElements.Length; i++)
            if (Eq.Equals(t.TupleElements[i].CorrespondingTupleField ?? t.TupleElements[i], real) || t.TupleElements[i].Name == f.Name) return i;
        throw new Unsupported("tuple element");
    }

    private string FieldGet(IFieldReferenceOperation op)
    {
        var f = op.Field;
        if (Value(f) is { } fixedValue) return fixedValue;
        if (f.ContainingType.IsTupleType) return $"{Expr(op.Instance!)}.{TupleIndex(f)}";
        if (w.IsDropped(f.ContainingType) || w.IsDropped(f.Type)) throw new Dropped(f.Name);
        if (!World.IsSource(f.ContainingType)) return BclField(op) ?? throw new Unsupported($"library field {f.ContainingType.Name}.{f.Name}");
        if (w.StaticState(f) is { } inCombat) return Owned($"c.s.{inCombat}", f.Type);
        if (w.IsCache(f)) return Null(f.Type);
        if (f.IsStatic)
        {
            // A static that is read only: its initializer is a function of the class's module.
            var cat = w.CatOfMember(f);
            if (cat == Cat.Stub) w.StubRefs.TryAdd(f.OriginalDefinition, 0);
            if (cat == Cat.Manual) w.ManualRefs.TryAdd(f.OriginalDefinition, 0);
            return $"{w.Path(f.ContainingType, f)}::{World.Ident(f.Name.TrimStart('_'))}_static(c)";
        }
        return Owned(FieldPlace(op, false), f.Type);
    }

    private static readonly HashSet<string> Primitives = ["bool", "i8", "i16", "i32", "i64", "u8", "u16", "u32", "u64", "f32", "f64", "char"];

    /// <summary>The value read out of a place. A block around it ends the borrow of the combat there: an operator on
    /// anything but a primitive, or a method call, would otherwise hold it while the other operand is evaluated.</summary>
    private string Owned(string place, ITypeSymbol? type)
    {
        if (!w.IsCopy(type)) return place + ".clone()";
        return UsesCombat(place) && !Primitives.Contains(N(type)) ? $"{{ {place} }}" : place;
    }

    /// <summary>The Rust place of an instance field.</summary>
    private string FieldPlace(IFieldReferenceOperation op, bool mutable)
    {
        var f = op.Field.OriginalDefinition;
        if (w.IsDropped(f.Type) || w.IsDropped(f.ContainingType)) throw new Dropped(f.Name);
        var owner = f.ContainingType;
        if (w.IsValue(owner))
        {
            string name = World.FieldName(f);
            return $"{(mutable ? Place(op.Instance!) : Ref(op.Instance!))}.{name}";
        }
        if (!w.IsStateful(owner)) throw new Unsupported($"a field of {owner.Name}, which has no state here");
        string? field = w.StateField(f) ?? throw new Unsupported($"field {owner.Name}.{f.Name} is not kept");
        string recv = op.Instance == null ? "me" : Expr(op.Instance);
        return $"{w.DataName(owner)}::{(mutable ? "of_mut" : "of")}({recv}, c).{field}";
    }

    /// <summary>The field behind an expression that reads a list out of an object, when there is exactly one: a field itself, or a property that only returns one.</summary>
    private string? FieldPlaceOf(IOperation op)
    {
        while (op is IConversionOperation { IsTryCast: false } cv && World.CollOf(cv.Operand.Type) != World.Coll.None) op = cv.Operand;
        if (op is IFieldReferenceOperation { Field.IsStatic: false } f && f.Field.ContainingType.TypeKind == TypeKind.Class && !w.IsValue(f.Field.ContainingType) && StableReceiver(f.Instance))
            return World.IsSource(f.Field.ContainingType) && !w.IsDropped(f.Field.Type) && w.IsStateful(f.Field.ContainingType) && w.StateField(f.Field.OriginalDefinition) != null ? FieldPlace(f, false) : null;
        if (op is IPropertyReferenceOperation { Property: { IsStatic: false, IsVirtual: false, IsOverride: false, IsAbstract: false } p } pr && StableReceiver(pr.Instance)
            && !w.IsValue(p.ContainingType) && w.IsStateful(p.ContainingType) && Forwarded(p.OriginalDefinition) is { } field && w.StateField(field) is { } name && Value(p) == null)
        {
            string recv = pr.Instance == null ? "me" : Expr(pr.Instance);
            return $"{w.DataName(field.ContainingType)}::of({recv}, c).{name}";
        }
        return null;
    }

    private bool StableReceiver(IOperation? instance) => instance switch
    {
        null => true,
        IInstanceReferenceOperation => true,
        ILocalReferenceOperation l => !reassigned.Contains(l.Local) && !droppedLocals.Contains(l.Local) && !aliases.ContainsKey(l.Local),
        IParameterReferenceOperation p => !reassigned.Contains(p.Parameter),
        IConditionalAccessInstanceOperation => true,
        _ => pinned.ContainsKey(instance),
    };

    private static readonly Dictionary<IPropertySymbol, IFieldSymbol?> forwarded = new(Eq);

    /// <summary>The field a property returns, when its getter does nothing else.</summary>
    private static IFieldSymbol? Forwarded(IPropertySymbol p)
    {
        lock (forwarded)
        {
            if (forwarded.TryGetValue(p, out var known)) return known;
            IFieldSymbol? found = null;
            if (p.ContainingType.TypeKind == TypeKind.Class && p.ContainingType.TypeKind != TypeKind.Interface && World.IsSource(p))
            {
                var auto = p.ContainingType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => Eq.Equals(f.AssociatedSymbol, p));
                if (auto != null) found = auto;
                else if (p.GetMethod?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is { } syntax)
                {
                    ExpressionSyntax? e = syntax switch
                    {
                        ArrowExpressionClauseSyntax a => a.Expression,
                        AccessorDeclarationSyntax { ExpressionBody: { } eb } => eb.Expression,
                        AccessorDeclarationSyntax { Body.Statements: [ReturnStatementSyntax { Expression: { } re }] } => re,
                        _ => null,
                    };
                    string? name = e switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax id } => id.Identifier.Text,
                        _ => null,
                    };
                    if (name != null) found = p.ContainingType.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic);
                }
            }
            forwarded[p] = found;
            return found;
        }
    }

    /// <summary>The field of another object that a property hands out, and the code that gives that object, when its
    /// getter is nothing but `=> holder.field` (the holder computed from this object).</summary>
    private (IFieldSymbol Field, IOperation Holder)? Held(IPropertySymbol p)
    {
        if (p.IsStatic || !World.IsSource(p) || !(Eq.Equals(p.ContainingType, cls.OriginalDefinition) || InheritsFrom(cls, p.ContainingType))) return null;
        var syntax = p.GetMethod?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        ExpressionSyntax? e = syntax switch
        {
            ArrowExpressionClauseSyntax a => a.Expression,
            AccessorDeclarationSyntax { ExpressionBody: { } eb } => eb.Expression,
            AccessorDeclarationSyntax { Body.Statements: [ReturnStatementSyntax { Expression: { } re }] } => re,
            _ => null,
        };
        if (e is not MemberAccessExpressionSyntax { Expression: not ThisExpressionSyntax and not BaseExpressionSyntax } access) return null;
        if (w.Comp.GetSemanticModel(access.SyntaxTree).GetOperation(access) is not IFieldReferenceOperation { Field: { IsStatic: false } field, Instance: { } holder }) return null;
        if (!World.IsSource(field.ContainingType) || w.IsValue(field.ContainingType) || !w.IsStateful(field.ContainingType)) return null;
        return (field.OriginalDefinition, holder);
    }

    private static bool InheritsFrom(INamedTypeSymbol t, INamedTypeSymbol of)
    {
        for (var b = t.BaseType; b != null; b = b.BaseType) if (Eq.Equals(b.OriginalDefinition, of.OriginalDefinition)) return true;
        return false;
    }

    /// <summary>The field a property's getter makes when it is not there yet and then returns (`if (_f != null) return
    /// _f; _f = ..; return _f;`): every return of the getter gives that one field.</summary>
    private static IFieldSymbol? MadeOnDemand(IPropertySymbol p)
    {
        if (p.IsStatic || p.ContainingType.TypeKind != TypeKind.Class || !World.IsSource(p)) return null;
        if (p.GetMethod?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not AccessorDeclarationSyntax { Body: { } block }) return null;
        var names = block.DescendantNodes().OfType<ReturnStatementSyntax>().Select(r => (r.Expression as IdentifierNameSyntax)?.Identifier.Text).Distinct().ToList();
        if (names.Count != 1 || names[0] == null) return null;
        return p.ContainingType.GetMembers(names[0]!).OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic);
    }

    private static bool IsBaseAccess(IOperation? instance) => instance?.Syntax is BaseExpressionSyntax;

    private static bool VirtualMember(ISymbol m) =>
        (m.IsVirtual || m.IsAbstract || m.IsOverride || m.ContainingType.TypeKind == TypeKind.Interface) && !m.IsStatic
        && !(m.IsSealed || (m.ContainingType.IsSealed && m.ContainingType.TypeKind == TypeKind.Class));

    /// <summary>The Rust path of the function to call for a method or accessor.</summary>
    private string CallPath(IMethodSymbol m0, bool virtualCall)
    {
        var m = (m0.ReducedFrom ?? m0).OriginalDefinition;
        var type = m.ContainingType;
        if (w.IsDropped(type) || w.IsDropMember(m)) throw new Dropped($"{type.Name}.{m.Name}");
        if (w.IsDropped(m.ReturnType)) throw new Dropped($"{type.Name}.{m.Name}");
        ISymbol owner = m.AssociatedSymbol ?? m;
        if (m.IsImplicitlyDeclared && m.MethodKind != MethodKind.Constructor && m.AssociatedSymbol == null) throw new Unsupported($"the compiler-made {type.Name}.{m.Name}");
        foreach (var p in m.Parameters)
            if (!w.IsDropped(p.Type)) T(p.Type);
        if (!m.ReturnsVoid) T(m.ReturnType);
        // A virtual member of the library (ToString, Equals) has no dispatcher: the override the compiler found is called.
        // A class written by hand answers for the classes below it too: no dispatcher goes around it.
        if (virtualCall && Dispatched(m))
        {
            var slot = World.Slot(m);
            w.Slots.TryAdd(slot, 0);
            return $"g::dispatch::{w.Module(slot.ContainingType)}::{w.FnName((IMethodSymbol)slot)}";
        }
        if (m.IsAbstract && w.CatOf(type) != Cat.Manual) throw new Unsupported($"a direct call of the abstract {type.Name}.{m.Name}");
        var cat = w.CatOfMember(m);
        if (cat == Cat.Stub) w.StubRefs.TryAdd(m, 0);
        if (cat == Cat.Manual) w.ManualRefs.TryAdd(m, 0);
        return $"{w.Path(type, m)}::{w.FnName(m)}";
    }

    /// <summary>Whether a virtual call of the member goes through its slot's dispatcher.</summary>
    private bool Dispatched(IMethodSymbol m) =>
        VirtualMember(m.AssociatedSymbol ?? m) && World.IsSource(World.Slot(m).ContainingType) && !w.IsValue(m.ContainingType) && w.CatOf(World.Slot(m).ContainingType) != Cat.Manual;

    /// <summary>The call a foreach walks, when it is a call of a sequence written with yield (or of a slot one of whose
    /// bodies is): the loop's body then runs inside the call, for each value as the sequence reaches it.</summary>
    private IInvocationOperation? WalkCall(IOperation collection)
    {
        var inner = collection;
        while (inner is IConversionOperation { IsTryCast: false, Conversion: { IsIdentity: true } or { IsReference: true, IsImplicit: true } } cv) inner = cv.Operand;
        if (inner is not IInvocationOperation op) return null;
        var def = (op.TargetMethod.ReducedFrom ?? op.TargetMethod).OriginalDefinition;
        var type = def.ContainingType;
        if (!World.IsSource(type) || World.IsGeneratedList(type) || w.IsDropped(type) || w.IsDropMember(def) || Value(def) != null) return null;
        if (def.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation) || World.WalkItem(def) == null) return null;
        bool viaSlot = op.IsVirtual && !IsBaseAccess(op.Instance) && Dispatched(def);
        return (viaSlot ? w.IsWalk((IMethodSymbol)World.Slot(def)) : w.Yields(def)) ? op : null;
    }

    private bool InStaticOrCtor => member is IMethodSymbol { IsStatic: true } or IMethodSymbol { MethodKind: MethodKind.Constructor } or IPropertySymbol { IsStatic: true } or IFieldSymbol;

    private string KindExpr(ITypeSymbol t)
    {
        switch (t)
        {
            case ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } tp:
                if (!w.Erased(tp)) throw new Unsupported($"the type {tp.Name} as a value");
                return "k_" + World.Snake(tp.Name);
            case ITypeParameterSymbol tp:
                if (!w.Erased(tp)) throw new Unsupported($"the type {tp.Name} as a value");
                return InStaticOrCtor ? "k_" + World.Snake(tp.Name) : $"{w.DataName(tp.ContainingType)}::of(me, c).{World.TargField(tp)}";
            case INamedTypeSymbol n: return w.KindOf(n);
            default: throw new Unsupported($"kind of {t}");
        }
    }

    private List<string> KindArgs(IMethodSymbol m0)
    {
        var m = m0.ReducedFrom ?? m0;
        var def = m.OriginalDefinition;
        var list = new List<string>();
        if (def.IsStatic || def.MethodKind == MethodKind.Constructor)
            for (int i = 0; i < def.ContainingType.TypeParameters.Length; i++)
                if (w.Erased(def.ContainingType.TypeParameters[i])) list.Add(KindExpr(m.ContainingType.TypeArguments[i]));
        for (int i = 0; i < def.TypeParameters.Length; i++)
            if (w.Erased(def.TypeParameters[i])) list.Add(KindExpr(m.TypeArguments[i]));
        return list;
    }

    private string Turbofish(IMethodSymbol m0)
    {
        var m = m0.ReducedFrom ?? m0;
        var def = m.OriginalDefinition;
        var args = new List<string>();
        for (int i = 0; i < def.TypeParameters.Length; i++)
            if (!w.Erased(def.TypeParameters[i])) args.Add(T(m.TypeArguments[i]));
        return args.Count > 0 ? "::<" + string.Join(", ", args) + ">" : "";
    }

    private string PropGet(IPropertyReferenceOperation op)
    {
        var p = op.Property;
        var def = p.OriginalDefinition;
        var type = def.ContainingType;
        if (Value(def) is { } fixedValue) return fixedValue;
        if (type.IsTupleType || !World.IsSource(type) || World.IsGeneratedList(type))
            return BclProp(op) ?? (w.IsDropped(type) ? throw new Dropped($"{type.Name}.{p.Name}") : throw new Unsupported($"library property {type.Name}.{p.Name}"));
        if (w.IsDropped(type) || w.IsDropMember(def) || w.IsDropped(def.Type)) throw new Dropped($"{type.Name}.{p.Name}");
        if (w.StaticState(def) is { } inCombat) return Owned($"c.s.{inCombat}", def.Type);
        var getter = def.GetMethod ?? throw new Unsupported($"property {p.Name} has no getter");
        if (w.IsValue(type) && !def.IsStatic)
        {
            var auto = type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => Eq.Equals(f.AssociatedSymbol, def));
            if (auto != null) return Owned($"{Ref(op.Instance!)}.{World.FieldName(auto)}", p.Type);
        }
        var args = new List<string>();
        if (def.IsStatic) args.AddRange(KindArgs(p.GetMethod!));
        else args.Add(op.Instance == null ? "me" : Expr(op.Instance));
        foreach (var a in op.Arguments)
            if (!w.IsDropped(a.Parameter!.Type)) args.Add(Guard(a.Value, "an index of presentation"));
        args.Add("c");
        bool virt = !def.IsStatic && VirtualMember(def) && !IsBaseAccess(op.Instance);
        return $"{CallPath(getter, virt)}({string.Join(", ", args)})";
    }

    /// <summary>A call. With `walk` (the code that takes each value), the call of a sequence's `__each` form.</summary>
    private string Invoke(IInvocationOperation op, string? walk = null)
    {
        // Whether this call's Option is wanted (MaybeNull): for this call alone, not for the calls in its arguments.
        bool optionWanted = takeOption;
        takeOption = false;
        var m = op.TargetMethod;
        if (Value(m.ReducedFrom ?? m) is { } fixedValue) return fixedValue;
        if (m.MethodKind == MethodKind.LocalFunction) throw new Unsupported("local function");
        if (m.MethodKind == MethodKind.DelegateInvoke)
        {
            if (op.Instance is IEventReferenceOperation raised) return Raise(raised, op);
            var parts = op.Arguments.OrderBy(a => a.Parameter!.Ordinal).Select(a => Expr(a.Value)).Append("c");
            return $"{Expr(op.Instance!)}.call({string.Join(", ", parts)})";
        }
        var def = (m.ReducedFrom ?? m).OriginalDefinition;
        var type = def.ContainingType;
        if (m.MethodKind == MethodKind.Constructor && type.SpecialType == SpecialType.System_Object) return "";
        if (!World.IsSource(type) || World.IsGeneratedList(type))
            return BclCall(op) ?? (w.IsDropped(type) ? throw new Dropped($"{type.Name}.{m.Name}") : throw new Unsupported($"library call {type.Name}.{m.Name}/{def.Parameters.Length}"));
        if (w.IsDropped(type)) throw new Dropped($"{type.Name}.{m.Name}");
        // A record's own Equals: by content, which is what == is on a value.
        if (m.IsImplicitlyDeclared && m.Name == "Equals" && w.IsValue(type) && op.Instance != null && op.Arguments.Length == 1 && Eq.Equals(op.Arguments[0].Value.Type?.OriginalDefinition, type))
            return $"({Expr(op.Instance)} == {Expr(op.Arguments[0].Value)})";
        if (w.IsDropMember(def))
        {
            // A presentation call in a chain (x.WithHitFx(..)) leaves the chain's value as it was.
            if (op.Instance != null && !m.ReturnsVoid && Eq.Equals(m.ReturnType.OriginalDefinition, type) && !World.IsTask(m.ReturnType, out _)) return Expr(op.Instance);
            throw new Dropped($"{type.Name}.{m.Name}");
        }
        string path = CallPath(m, op.IsVirtual && !IsBaseAccess(op.Instance)) + (walk == null ? "" : "__each") + Turbofish(m);
        // base(..) or this(..) at the head of a constructor: the other constructor's body, on this object.
        // For a value, the other constructor makes the value.
        bool valueCtor = m.MethodKind == MethodKind.Constructor && w.IsValue(type);
        if (m.MethodKind == MethodKind.Constructor && !valueCtor) path = CtorPath(path);
        var args = KindArgs(m);
        // A call on what port.toml says is presentation (the generator for what does not matter) is presentation.
        if (op.Instance is IMemberReferenceOperation { Member: var im } && World.IsSource(im.ContainingType) && w.Cfg.DropMembers.Contains($"{im.ContainingType.Name}.{im.Name}"))
            throw new Dropped($"{im.ContainingType.Name}.{im.Name}");
        if (op.Instance != null && !valueCtor) args.Add(Guard(op.Instance, $"a call of {type.Name}.{m.Name} on presentation"));
        var after = new List<string>();
        var ordered = op.Arguments.OrderBy(a => a.Parameter!.Ordinal).ToList();
        bool inOrder = ordered.SequenceEqual(op.Arguments);
        var pre = new List<string>();
        var outs = new List<string>();
        foreach (var a in ordered)
        {
            var kind = a.Parameter!.RefKind;
            if (w.IsDropped(a.Parameter))
            {
                bool game = HoldsGameCall(a.Value);
                if (game) w.Rep.DroppedWithCall(Where, a.Value);
                Removed(a.Value, a.Value, "an argument of a presentation parameter", game);
                continue;
            }
            if (kind is RefKind.Out or RefKind.Ref)
            {
                string o = Tmp("o");
                outs.Add(o);
                if (a.Value is not IDiscardOperation) after.Add(Assign(a.Value is IDeclarationExpressionOperation de ? de.Expression : a.Value, () => o, false) + ";");
                if (kind == RefKind.Out) continue;
            }
            else if (w.IsChangedParam(a.Parameter))
            {
                // The callee changes the collection: what it made of it comes back, to where the caller has it. A
                // collection made for the call alone has nowhere to come back to.
                string o = Tmp("o");
                outs.Add(o);
                if (a.Value is ILocalReferenceOperation changed) AliasCheck(changed.Local, a);
                var was = assignTarget;
                assignTarget = a.Value;
                try { after.Add($"{Place(a.Value)} = {o};"); }
                catch (Unsupported) { }
                finally { assignTarget = was; }
            }
            // A task run on the side is what the task is: one that is presentation makes the whole call presentation.
            string code = def is { Name: "RunSafely", ContainingType.Name: "TaskHelper" } ? Expr(a.Value) : Guard(a.Value, $"a call of {type.Name}.{m.Name} with presentation");
            // `ref x`, or a collection the callee changes, of a local the call alone reads: what the callee makes of it
            // comes back to it right after, so the value is handed over, not copied.
            if ((kind == RefKind.Ref || w.IsChangedParam(a.Parameter)) && Moved(a, op) is { } moved) code = moved;
            // A collection that can be null, handed to a parameter: a parameter's collection is never null here, so
            // null is the empty one.
            else if (kind == RefKind.None && !w.IsChangedParam(a.Parameter) && World.CollOf(a.Value.Type) != World.Coll.None && World.MayBeNull(a.Value, nullLocals, w))
            {
                w.Rep.Count($"a collection that can be null, passed as an argument (null is the empty one): {Where}");
                code = $"({MaybeNull(a.Value)}).unwrap_or_default()";
            }
            if (!inOrder) { string t = Tmp("a"); pre.Add($"let {t} = {code};"); code = t; }
            args.Add(code);
        }
        if (!inOrder)
        {
            // Named arguments: evaluated in the order written.
            var passed = ordered.Where(x => x.Parameter!.RefKind != RefKind.Out && !w.IsDropped(x.Parameter)).ToList();
            pre = op.Arguments.Where(passed.Contains).Select(a => pre[passed.IndexOf(a)]).ToList();
        }
        args.Add("c");
        if (walk != null)
        {
            if (outs.Count > 0) throw new Unsupported("a sequence that changes what it is given");
            args.Add(walk);
        }
        string call = $"{path}({string.Join(", ", args)})";
        if (valueCtor) call = $"me = {call}";
        // A call whose collection can be null (World.ReturnsNullColl) gives an Option; read as the collection, null is
        // C#'s NullReferenceException.
        if (w.ReturnsNullColl(m) && !optionWanted) call = $"rt::nn(&{call})";
        if (outs.Count == 0) return pre.Count == 0 ? call : $"{{ {string.Join(" ", pre)} {call} }}";
        bool hasValue = !m.ReturnsVoid;
        var names = new List<string>();
        if (hasValue) names.Add("__r");
        names.AddRange(outs);
        string pattern = names.Count == 1 ? names[0] : "(" + string.Join(", ", names) + ")";
        return $"{{ {string.Join(" ", pre)} let {pattern} = {call}; {string.Join(" ", after)} {(hasValue ? "__r" : "")} }}";
    }

    /// <summary>A `ref` argument, or one the callee changes, that is a local or parameter of this body (not one that stands for a field), whose
    /// value is not a copy type and which no other part of the call mentions: the value taken out of the local, which
    /// the call's result is written back into before anything else can read it.</summary>
    private string? Moved(IArgumentOperation a, IInvocationOperation call)
    {
        ISymbol? s = a.Value switch
        {
            ILocalReferenceOperation l => l.Local,
            IParameterReferenceOperation p => p.Parameter,
            _ => null,
        };
        if (s == null || aliases.ContainsKey(s) || droppedLocals.Contains(s) || w.IsCopy(a.Value.Type)) return null;
        bool Mentions(IOperation? o) => o != null && o.DescendantsAndSelf().Any(d =>
            d is ILocalReferenceOperation { Local: var x } && Eq.Equals(x, s) || d is IParameterReferenceOperation { Parameter: var y } && Eq.Equals(y, s));
        if (Mentions(call.Instance) || call.Arguments.Any(other => other != a && Mentions(other.Value))) return null;
        w.Rep.Count("ref arguments handed over, not copied");
        return $"std::mem::take(&mut {NameOf(s)})";
    }

    private string New(IObjectCreationOperation op)
    {
        var type = (INamedTypeSymbol)op.Type!;
        if (w.IsDropped(type)) throw new Dropped(type.Name);
        string made;
        if (World.IsGeneratedList(type))
        {
            if (type.Name.Contains("SingleElement")) made = $"vec![{string.Join(", ", Elements([op.Arguments[0].Value]))}]";
            else made = Expr(op.Arguments[0].Value);
        }
        else if (!World.IsSource(type)) made = BclNew(op) ?? throw new Unsupported($"new {type.Name}/{op.Arguments.Length}");
        else
        {
            var ctor = op.Constructor ?? throw new Unsupported("creation without a constructor");
            if (w.IsValue(type) && ctor.IsImplicitlyDeclared) made = $"<{T(type)} as Default>::default()";
            else
            {
                if (type.IsAbstract) throw new Unsupported("creation of an abstract class");
                var args = KindArgs(ctor);
                foreach (var a in op.Arguments.OrderBy(a => a.Parameter!.Ordinal))
                {
                    if (w.IsDropped(a.Parameter!)) continue;
                    if (a.Parameter!.RefKind != RefKind.None) throw new Unsupported("constructor with ref parameters");
                    args.Add(Guard(a.Value, $"creation of {type.Name} with presentation"));
                }
                args.Add("c");
                made = $"{CallPath(ctor, false)}({string.Join(", ", args)})";
            }
        }
        if (op.Initializer == null || op.Initializer.Initializers.Length == 0) return made;
        string o = Tmp("o");
        implicitReceivers.Push(o);
        var sb = new StringBuilder($"{{ let mut {o} = {made}; ");
        foreach (var init in op.Initializer.Initializers)
        {
            try { sb.Append(ExprStmt(init)).Append("; "); }
            catch (Dropped) { LeftOut++; }
        }
        implicitReceivers.Pop();
        return sb.Append($"{o} }}").ToString();
    }

    // ---------------------------------------------------------------- places and writes

    /// <summary>A Rust place that can be written or changed in place.</summary>
    private string Place(IOperation op)
    {
        switch (op)
        {
            case ILocalReferenceOperation l when nullLocals.Contains(l.Local):
                if (!ReferenceEquals(op, assignTarget)) AliasCheck(l.Local, op);
                return $"(*rt::nn_mut(&mut {NameOf(l.Local)}))";
            case ILocalReferenceOperation l:
                if (droppedLocals.Contains(l.Local)) throw new Dropped(l.Local.Name);
                if (!ReferenceEquals(op, assignTarget)) AliasCheck(l.Local, op);
                return aliases.TryGetValue(l.Local, out var a) ? Mutable(a) : NameOf(l.Local);
            case IParameterReferenceOperation p:
                if (droppedLocals.Contains(p.Parameter)) throw new Dropped(p.Parameter.Name);
                return NameOf(p.Parameter);
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }: return implicitReceivers.Peek();
            case IInstanceReferenceOperation when isStructMember: return "me";
            case IFieldReferenceOperation { Field.IsStatic: true } sf when w.StaticState(sf.Field) is { } root: return $"c.s.{root}";
            case IFieldReferenceOperation { Field.IsStatic: false } f when !f.Field.ContainingType.IsTupleType: return FieldPlace(f, true);
            case IFieldReferenceOperation { Field.ContainingType.IsTupleType: true } tf: return $"{Place(tf.Instance!)}.{TupleIndex(tf.Field)}";
            case IPropertyReferenceOperation { Property: { IsStatic: false } p } pr when w.IsValue(p.ContainingType)
                && p.ContainingType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => Eq.Equals(f.AssociatedSymbol, p.OriginalDefinition)) is { } auto:
                return $"{Place(pr.Instance!)}.{World.FieldName(auto)}";
            case IPropertyReferenceOperation pr when FieldPlaceOf(pr) is { } fp: return fp.Replace("::of(", "::of_mut(");
            case IPropertyReferenceOperation { Property.Name: "Value", Instance: ILocalReferenceOperation { Local: var pl } } when pairs.TryGetValue(pl, out var pv): return Mutable(pv.Value);
            // A collection its property makes on demand: the getter runs (and makes it), then the field it returned is the place.
            case IPropertyReferenceOperation { Property: var lazy } lp when Forwarded(lazy.OriginalDefinition) == null && MadeOnDemand(lazy.OriginalDefinition) is { } made
                && w.StateField(made) is { } madeName && StableReceiver(lp.Instance) && !w.IsValue(made.ContainingType):
                return $"(*{{ let _ = {Expr(lp)}; &mut {w.DataName(made.ContainingType)}::of_mut({(lp.Instance == null ? "me" : Expr(lp.Instance))}, c).{madeName} }})";
            // A property of this object that hands out a field of another object (`=> GetInternalData<Data>().list`): that field.
            case IPropertyReferenceOperation { Instance: null or IInstanceReferenceOperation, Property: var hp } when Held(hp.OriginalDefinition) is { } held && w.StateField(held.Field) is { } heldName:
                return $"(*{{ let __h = {Expr(held.Holder)}; &mut {w.DataName(held.Field.ContainingType)}::of_mut(__h, c).{heldName} }})";
            case IConversionOperation { IsTryCast: false } cv when N(cv.Type) == N(cv.Operand.Type): return Place(cv.Operand);
            case IConditionalAccessInstanceOperation: return condInstances.Peek();
            case IArrayElementReferenceOperation ae when ae.Indices.Length == 1 && IsSimple(ae.Indices[0]): return $"{Place(ae.ArrayReference)}[({Expr(ae.Indices[0])}) as usize]";
            case IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } ix when World.CollOf(ix.Property.ContainingType) == World.Coll.Vec && IsSimple(ix.Arguments[0].Value):
                return $"{Place(ix.Instance!)}[({Expr(ix.Arguments[0].Value)}) as usize]";
            case IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } dx when World.CollOf(dx.Property.ContainingType) == World.Coll.Dict && IsSimple(dx.Arguments[0].Value):
                return $"(*{Place(dx.Instance!)}.get_mut(&{Expr(dx.Arguments[0].Value)}))";
            // A key or an index that is computed: computed first, then the place is taken.
            case IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } dk when World.CollOf(dk.Property.ContainingType) == World.Coll.Dict:
                return $"(*{{ let __k = {Expr(dk.Arguments[0].Value)}; {Place(dk.Instance!)}.get_mut(&__k) }})";
            case IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } vk when World.CollOf(vk.Property.ContainingType) == World.Coll.Vec:
                return $"(*{{ let __i = ({Expr(vk.Arguments[0].Value)}) as usize; &mut {Place(vk.Instance!)}[__i] }})";
            default: throw new Unsupported($"a change in place of {op.Kind} ({op.Syntax.ToString().Split('\n')[0].Trim()})");
        }
    }

    /// <summary>A place read where it is, to be changed: the same objects reached for writing.</summary>
    private static string Mutable(string place) => place.Replace("{ &", "{ &mut ").Replace("::of(", "::of_mut(").Replace(".at(&", ".get_mut(&");

    /// <summary>Pairs of a loop over a dictionary of collections by key (Body.ForEach): the key's local, and the
    /// value's place in the dictionary.</summary>
    private readonly Dictionary<ISymbol, (string Key, string Value)> pairs = new(Eq);

    /// <summary>A Rust expression to read through without copying: the place itself when there is one.</summary>
    private string Ref(IOperation op)
    {
        switch (op)
        {
            case ILocalReferenceOperation l when nullLocals.Contains(l.Local): return $"(*rt::nn_ref(&{NameOf(l.Local)}))";
            case ILocalReferenceOperation l when !droppedLocals.Contains(l.Local): return aliases.TryGetValue(l.Local, out var a) ? a : NameOf(l.Local);
            case IParameterReferenceOperation p when !droppedLocals.Contains(p.Parameter): return NameOf(p.Parameter);
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }: return implicitReceivers.Peek();
            case IInstanceReferenceOperation when isStructMember: return "me";
            case IFieldReferenceOperation { Field.IsStatic: false } f when !f.Field.ContainingType.IsTupleType && World.IsSource(f.Field.ContainingType) && Value(f.Field) == null: return FieldPlace(f, false);
            case IPropertyReferenceOperation pr when FieldPlaceOf(pr) is { } fp: return fp;
            case IPropertyReferenceOperation { Property.Name: "Value", Instance: ILocalReferenceOperation { Local: var pl } } when pairs.TryGetValue(pl, out var pv): return pv.Value;
            case IConversionOperation { IsTryCast: false } cv when cv.Operand.Type != null && N(cv.Type) == N(cv.Operand.Type) && !cv.Conversion.IsUserDefined: return Ref(cv.Operand);
            default: return $"({Expr(op)})";
        }
    }

    private static bool IsSimple(IOperation op) => op is ILiteralOperation or ILocalReferenceOperation or IParameterReferenceOperation || op.ConstantValue.HasValue
        || op is IConversionOperation { Operand: var o } && IsSimple(o) || op is IBinaryOperation { LeftOperand: var l, RightOperand: var r } && IsSimple(l) && IsSimple(r);

    private static bool UsesCombat(string code) => System.Text.RegularExpressions.Regex.IsMatch(code, @"\bc\b");

    /// <summary>Builds receiver.op(args) so that the arguments are evaluated before the receiver is borrowed.</summary>
    private string Hoisted(IReadOnlyList<IOperation> args, string receiver, Func<string, string[], string> build)
    {
        var codes = args.Select(Expr).ToArray();
        return HoistedCodes(codes, receiver, build);
    }

    private static bool IsName(string code) => System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Za-z0-9_:.]*(\.clone\(\))?$") && !UsesCombat(code);

    /// <summary>With always, every argument that is more than a name is evaluated first: the receiver is about to be borrowed for writing.</summary>
    private static string HoistedCodes(string[] codes, string receiver, Func<string, string[], string> build, bool always = false)
    {
        bool Needs(string code) => always ? !IsName(code) : UsesCombat(receiver) && UsesCombat(code);
        if (!codes.Any(Needs)) return build(receiver, codes);
        var sb = new StringBuilder("{ ");
        var names = new string[codes.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            if (!Needs(codes[i])) { names[i] = codes[i]; continue; }
            names[i] = $"__h{i}";
            sb.Append($"let __h{i} = {codes[i]}; ");
        }
        return sb.Append(build(receiver, names)).Append(" }").ToString();
    }

    /// <summary>var (a, b) = pair; (x, y) = (y, x): the value is taken apart into its targets.</summary>
    private string Deconstruct(IDeconstructionAssignmentOperation de)
    {
        var target = de.Target is IDeclarationExpressionOperation { Expression: var inner } ? inner : de.Target;
        if (target is not ITupleOperation tuple) throw new Unsupported("deconstruction into something that is no tuple");
        var value = de.Value;
        while (value is IConversionOperation { IsTryCast: false, Operand.Type: not null } cv && (cv.Operand.Type.IsTupleType || World.MetaName((INamedTypeSymbol)cv.Operand.Type) == "System.Collections.Generic.KeyValuePair`2")) value = cv.Operand;
        string d = Tmp("d");
        var sb = new StringBuilder($"let {d} = {Expr(value)}; ");
        void Parts(ITupleOperation t, string from, ITypeSymbol? type)
        {
            bool pair = type is INamedTypeSymbol n && World.MetaName(n) == "System.Collections.Generic.KeyValuePair`2";
            if (!pair && type is not { IsTupleType: true }) throw new Unsupported($"deconstruction of {type?.Name}");
            for (int i = 0; i < t.Elements.Length; i++)
            {
                string part = pair ? $"{from}.{(i == 0 ? "key" : "value")}" : $"{from}.{i}";
                var partType = pair ? ((INamedTypeSymbol)type!).TypeArguments[i] : ((INamedTypeSymbol)type!).TupleElements[i].Type;
                var e = t.Elements[i] is IDeclarationExpressionOperation { Expression: var x } ? x : t.Elements[i];
                switch (e)
                {
                    case IDiscardOperation: break;
                    case ITupleOperation nested: Parts(nested, part, partType); break;
                    case ILocalReferenceOperation { Local: var l } when w.IsDropped(l.Type): droppedLocals.Add(l); break;
                    default: sb.Append(Assign(e, () => part, false)).Append("; "); break;
                }
            }
        }
        Parts(tuple, d, value.Type);
        return $"{{ {sb}}}";
    }

    /// <summary>What a write's target reaches before its value, as C# does it: the object that holds the member
    /// (where code reaches it, not a local), then an indexer's or an array's index, each reached once and first. Gives
    /// the statements that reach them, with them pinned for the code that follows; Unpin takes them back.</summary>
    private (string Pre, List<IOperation> Pins) PinTarget(IOperation target)
    {
        var pins = new List<IOperation>();
        var pre = new StringBuilder();
        void Pin(IOperation op, string name, bool clone)
        {
            pre.Append($"let {name} = {Expr(op)}; ");
            pinned[op] = clone ? $"{name}.clone()" : name;
            pins.Add(op);
        }
        bool Trivial(IOperation op) => op.ConstantValue.HasValue || pinned.ContainsKey(op) || op is ILocalReferenceOperation { Local: var l } && !reassigned.Contains(l) || op is IParameterReferenceOperation { Parameter: var p } && !reassigned.Contains(p) || op is IInstanceReferenceOperation;
        IOperation? holder = target switch
        {
            IFieldReferenceOperation f => f.Instance,
            IPropertyReferenceOperation { Property: var prop } pr when World.IsSource(prop.ContainingType) => pr.Instance,
            // A library indexer (a list's, a dictionary's): the object that holds the collection.
            IPropertyReferenceOperation pr when pr.Instance != null => UnstableReceiver(pr.Instance),
            IArrayElementReferenceOperation ae => UnstableReceiver(ae.ArrayReference),
            _ => null,
        };
        if (holder is { Type: { } ht } && w.IsObj(ht) && !Trivial(holder) && !StableReceiver(holder)) Pin(holder, Tmp("r"), false);
        IEnumerable<IOperation> indices = target switch
        {
            IPropertyReferenceOperation pr => pr.Arguments.Select(a => a.Value),
            IArrayElementReferenceOperation ae => ae.Indices,
            _ => [],
        };
        foreach (var i in indices)
            if (!Trivial(i)) Pin(i, Tmp("i"), true);
        return (pre.ToString(), pins);
    }

    private void Unpin(List<IOperation> pins)
    {
        foreach (var p in pins) pinned.Remove(p);
    }

    private string Assign(IOperation target, Func<string> value, bool asValue)
    {
        // The target's holder and index first, then the value, as C# evaluates them.
        if (target is IFieldReferenceOperation or IPropertyReferenceOperation or IArrayElementReferenceOperation && PinTarget(target) is { Pins.Count: > 0 } first)
        {
            try { return $"{{ {first.Pre}{Assign(target, value, asValue)} }}"; }
            finally { Unpin(first.Pins); }
        }
        // The decompiler writes an object initializer's `{ P = v }` as a statement even where P is init-only, which
        // the compiler refuses: the property is the one it names.
        if (target is IInvalidOperation invalid && invalid.ChildOperations.Count == 1 && invalid.ChildOperations.First() is IPropertyReferenceOperation { Property.SetMethod.IsInitOnly: true } initOnly)
            target = initOnly;
        string Done(string store, string v) => asValue ? $"{{ let __v = {v}; {store.Replace("$v", "__v" + CloneOf(target.Type))}; __v }}" : UsesCombat(store.Replace("$v", "")) && UsesCombat(v) ? $"{{ let __v = {v}; {store.Replace("$v", "__v")}; }}" : store.Replace("$v", v);
        switch (target)
        {
            case ILocalReferenceOperation { IsDeclaration: true } decl when !droppedLocals.Contains(decl.Local) && !w.IsDropped(decl.Local.Type):
                Hoist(decl.Local);
                return Done($"{NameOf(decl.Local)} = $v", value());
            case ILocalReferenceOperation { Local: var held } when nullLocals.Contains(held):
                return Done($"{NameOf(held)} = $v", $"Some({value()})");
            case ILocalReferenceOperation or IParameterReferenceOperation or IArrayElementReferenceOperation:
            case IFieldReferenceOperation { Field.ContainingType.IsTupleType: true }:
            {
                var was = assignTarget;
                assignTarget = target;
                string place = Place(target);
                assignTarget = was;
                return Done($"{place} = $v", value());
            }
            case IDeclarationExpressionOperation de:
                if (de.Expression is ILocalReferenceOperation dl && !w.IsDropped(dl.Local.Type)) Hoist(dl.Local);
                return Assign(de.Expression, value, asValue);
            case IInstanceReferenceOperation when isStructMember: return Done("me = $v", value());
            case IDiscardOperation: return Done("let _ = $v", value());
            case IEventReferenceOperation ev:
                if (w.StateEvents.Contains(ev.Event.OriginalDefinition)) throw new Unsupported($"a write to the event {ev.Event.Name}");
                throw new Dropped("event " + ev.Event.Name);
            case IFieldReferenceOperation f:
                if (w.StaticState(f.Field) is { } sf) return Done($"c.s.{sf} = $v", value());
                if (w.IsCache(f.Field)) return asValue ? value() : $"{{ let _ = {value()}; }}";
                if (f.Field.IsStatic) throw new Unsupported($"a write to the static {f.Field.ContainingType.Name}.{f.Field.Name}");
                if (w.IsDropped(f.Field.Type) || w.IsDropped(f.Field.ContainingType)) throw new Dropped(f.Field.Name);
                return Done($"{FieldPlace(f, true)} = $v", value());
            case IPropertyReferenceOperation pr:
            {
                var p = pr.Property.OriginalDefinition;
                var type = p.ContainingType;
                if (w.IsDropped(type) || w.IsDropped(p.Type) || (World.IsSource(type) && w.IsDropMember(p))) throw new Dropped($"{type.Name}.{p.Name}");
                if (!World.IsSource(type))
                {
                    if (p.IsIndexer && pr.Arguments.Length == 1)
                    {
                        string key = Expr(pr.Arguments[0].Value), v = value();
                        string place = Place(pr.Instance!);
                        string store = World.CollOf(type) switch
                        {
                            World.Coll.Vec => $"{place}[__k as usize] = $v",
                            World.Coll.Dict => $"{place}.set(__k, $v)",
                            _ => throw new Unsupported($"indexer of {type.Name}"),
                        };
                        return asValue ? $"{{ let __k = {key}; let __v = {v}; {store.Replace("$v", "__v" + CloneOf(target.Type))}; __v }}" : $"{{ let __k = {key}; let __v = {v}; {store.Replace("$v", "__v")}; }}";
                    }
                    throw new Unsupported($"a write to the library property {type.Name}.{p.Name}");
                }
                if (w.StaticState(p) is { } sp) return Done($"c.s.{sp} = $v", value());
                var auto = type.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(x => Eq.Equals(x.AssociatedSymbol, p));
                if (w.IsValue(type) && auto != null) return Done($"{Place(pr.Instance!)}.{World.FieldName(auto)} = $v", value());
                if (p.SetMethod == null)
                {
                    // A get-only auto-property written by its constructor: the field itself.
                    if (auto == null || p.IsStatic) throw new Unsupported($"a write to {type.Name}.{p.Name}, which has no setter");
                    string? field = w.StateField(auto) ?? throw new Unsupported($"field of {type.Name}.{p.Name} is not kept");
                    string recv0 = pr.Instance == null ? "me" : Expr(pr.Instance);
                    return Done($"{w.DataName(type)}::of_mut({recv0}, c).{field} = $v", value());
                }
                if (p.IsStatic) throw new Unsupported($"a write to the static {type.Name}.{p.Name}");
                var args = new List<string> { pr.Instance == null ? "me" : Expr(pr.Instance) };
                foreach (var a in pr.Arguments) args.Add(Expr(a.Value));
                bool virt = VirtualMember(p) && !IsBaseAccess(pr.Instance);
                string path = CallPath(p.SetMethod, virt);
                string v2 = value();
                return asValue ? $"{{ let __v = {v2}; {path}({string.Join(", ", args)}, __v{CloneOf(target.Type)}, c); __v }}" : $"{path}({string.Join(", ", args)}, {v2}, c)";
            }
            default: throw new Unsupported($"assignment to {target.Kind}");
        }
    }

    private string Compound(ICompoundAssignmentOperation ca, bool asValue)
    {
        if (ca.Target is IEventReferenceOperation ev) throw new Dropped("event " + ev.Event.Name);
        if (ca.Target.Type?.TypeKind == TypeKind.Delegate) throw new Unsupported("delegates combined");
        if (ca.OperatorMethod != null && World.IsSource(ca.OperatorMethod.ContainingType)) throw new Unsupported("compound assignment with a game operator");
        // The target's holder and index reached once, then the target read, the value, the write (C#'s order).
        var (pre, pins) = PinTarget(ca.Target);
        try
        {
            string old = Tmp("old");
            string read = Expr(ca.Target);
            string value = Expr(ca.Value);
            string result = BinaryCode(ca.OperatorKind, old, value, ca.Target.Type, ca.Value.Type, ca.Target.Type, ca.IsLifted, ca.Value);
            string store = Assign(ca.Target, () => result, asValue);
            return $"{{ {pre}let {old} = {read}; {store}{(asValue ? "" : ";")} }}";
        }
        finally { Unpin(pins); }
    }

    private string Increment(IIncrementOrDecrementOperation inc, bool asValue)
    {
        string op = inc.Kind == OperationKind.Increment ? "+" : "-";
        string one = inc.Target.Type?.SpecialType == SpecialType.System_Decimal ? "Dec::ONE" : inc.Target.Type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double ? "1.0" : "1";
        string old = Tmp("old");
        if (World.IsNullable(inc.Target.Type!, out _)) throw new Unsupported("increment of a nullable");
        if (!asValue && inc.Target is ILocalReferenceOperation or IParameterReferenceOperation) return $"{Place(inc.Target)} {op}= {one}";
        // The target's holder and index reached once (C#'s order), then read and written.
        var (pre, pins) = PinTarget(inc.Target);
        try
        {
            string read = Expr(inc.Target);
            string store = Assign(inc.Target, () => $"({old} {op} {one})", false);
            if (!asValue) return $"{{ {pre}let {old} = {read}; {store}; }}";
            return inc.IsPostfix ? $"{{ {pre}let {old} = {read}; {store}; {old} }}" : $"{{ {pre}let {old} = {read}; {store}; ({old} {op} {one}) }}";
        }
        finally { Unpin(pins); }
    }

    // ---------------------------------------------------------------- operators

    private static bool IsNullConst(IOperation op) => op.ConstantValue is { HasValue: true, Value: null };

    private static IOperation Unboxed(IOperation op)
    {
        while (op is IConversionOperation { IsTryCast: false } cv && cv.Type?.SpecialType == SpecialType.System_Object && cv.Operand.Type != null) op = cv.Operand;
        return op;
    }

    private string Binary(IBinaryOperation b)
    {
        if (b.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && (IsNullConst(b.LeftOperand) || IsNullConst(b.RightOperand)))
        {
            var other = Unboxed(IsNullConst(b.LeftOperand) ? b.RightOperand : b.LeftOperand);
            if (IsNullConst(other)) return b.OperatorKind == BinaryOperatorKind.Equals ? "true" : "false";
            if (other is IFieldReferenceOperation { Field: var cache } && w.IsCache(cache)) return b.OperatorKind == BinaryOperatorKind.Equals ? "true" : "false";
            if (Presence(other) is { } there) return b.OperatorKind == BinaryOperatorKind.Equals ? $"!{there}" : there;
            if (World.CollOf(other.Type) != World.Coll.None && World.MayBeNull(other, nullLocals, w))
            {
                w.Rep.Count($"null test on a collection that can be null (an Option): {Where}: {other.Syntax.ToString().Split('\n')[0].Trim()} [{other.Kind}]");
                return $"({MaybeNull(other)}).{(b.OperatorKind == BinaryOperatorKind.Equals ? "is_none" : "is_some")}()";
            }
            if (World.CollOf(other.Type) != World.Coll.None) w.Rep.Count($"null test on a collection (never null here): {Where}: {other.Syntax.ToString().Split('\n')[0].Trim()} [{other.Kind}]");
            string tested;
            try { tested = Ref(other); }
            catch (Dropped) when (other.Type is { IsReferenceType: true })
            {
                // There is no presentation object in the engine, as in the headless game: it is null.
                w.Rep.Count("null test on presentation (taken as null)");
                return b.OperatorKind == BinaryOperatorKind.Equals ? "true" : "false";
            }
            return $"{(b.OperatorKind == BinaryOperatorKind.Equals ? "" : "!")}rt::is_null(&{tested})";
        }
        // `(object)a == b` on a class that is a value here: the same object has the same content, and where the game
        // goes on to compare the content of two that are not the same object, it finds the same answer.
        if (b.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals && Unboxed(b.LeftOperand) is var ul && Unboxed(b.RightOperand) is var ur
            && (ul != b.LeftOperand || ur != b.RightOperand) && w.IsValue(ul.Type) && w.IsValue(ur.Type) && N(ul.Type) == N(ur.Type))
            return $"({Expr(ul)} {(b.OperatorKind == BinaryOperatorKind.Equals ? "==" : "!=")} {Expr(ur)})";
        // A game's == or != of its own: called (listed with the comparisons by equality); a record's, which C# writes
        // over its fields: a value here compares them too (not listed), an object's is not translated (CallPath).
        if (b.OperatorMethod is { } om && World.IsSource(om.ContainingType) && !(om.IsImplicitlyDeclared && w.IsValue(om.ContainingType)))
        {
            if (w.IsDropped(om.ContainingType)) throw new Dropped(om.ContainingType.Name);
            if (om.Name is "op_Equality" or "op_Inequality")
                w.Rep.Equality(Where, b, om.IsImplicitlyDeclared ? $"{om.ContainingType.Name}: a record's {om.Name}, of an object: not translated" : $"{om.ContainingType.Name}: its own {om.Name}, called");
            return $"{CallPath(om, false)}({Expr(b.LeftOperand)}, {Expr(b.RightOperand)}, c)";
        }
        if (b.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
        {
            // Tests with a fixed value fold, and what they switch off is not translated at all.
            bool and = b.OperatorKind == BinaryOperatorKind.ConditionalAnd;
            string l = Expr(b.LeftOperand);
            if (l == (and ? "false" : "true")) return l;
            string r = Expr(b.RightOperand);
            if (l == (and ? "true" : "false")) return r;
            if (r == (and ? "true" : "false")) return l;
            return $"({l} {(and ? "&&" : "||")} {r})";
        }
        if (b.Type?.SpecialType == SpecialType.System_String && b.OperatorKind == BinaryOperatorKind.Add)
            return $"Str::concat(&[{StrOf(b.LeftOperand)}, {StrOf(b.RightOperand)}])";
        return BinaryCode(b.OperatorKind, Expr(b.LeftOperand), Expr(b.RightOperand), b.LeftOperand.Type, b.RightOperand.Type, b.Type, b.IsLifted, b.RightOperand);
    }

    private string StrOf(IOperation op)
    {
        op = Unboxed(op);
        if (IsNullConst(op)) return "Str::EMPTY";
        return op.Type?.SpecialType == SpecialType.System_String ? Expr(op) : $"rt::to_s({Expr(op)}, c)";
    }

    private string BinaryCode(BinaryOperatorKind kind, string l, string r, ITypeSymbol? lt, ITypeSymbol? rt, ITypeSymbol? result, bool lifted, IOperation? right)
    {
        if (result?.SpecialType == SpecialType.System_String && kind == BinaryOperatorKind.Add)
            return $"Str::concat(&[{l}, {(rt?.SpecialType == SpecialType.System_String ? r : $"rt::to_s({r}, c)")}])";
        if (lt != null && (w.IsObj(lt) || lt.TypeKind == TypeKind.Delegate || World.CollOf(lt) != World.Coll.None) && kind is not (BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals))
            throw new Unsupported($"operator {kind} on {lt.Name}");
        if (lt != null && World.CollOf(lt) != World.Coll.None) throw new Unsupported("lists compared by reference");
        string op = kind switch
        {
            BinaryOperatorKind.Add => "+", BinaryOperatorKind.Subtract => "-", BinaryOperatorKind.Multiply => "*", BinaryOperatorKind.Divide => "/",
            BinaryOperatorKind.Remainder => "%", BinaryOperatorKind.And => "&", BinaryOperatorKind.Or => "|", BinaryOperatorKind.ExclusiveOr => "^",
            BinaryOperatorKind.Equals => "==", BinaryOperatorKind.NotEquals => "!=", BinaryOperatorKind.LessThan => "<", BinaryOperatorKind.LessThanOrEqual => "<=",
            BinaryOperatorKind.GreaterThan => ">", BinaryOperatorKind.GreaterThanOrEqual => ">=",
            BinaryOperatorKind.LeftShift => "<<", BinaryOperatorKind.RightShift => ">>", BinaryOperatorKind.UnsignedRightShift => ">>>",
            BinaryOperatorKind.ConditionalAnd => "&&", BinaryOperatorKind.ConditionalOr => "||",
            _ => throw new Unsupported($"operator {kind}"),
        };
        bool compare = kind is BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual;
        bool equality = kind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals;
        if (op is "<<" or ">>")
        {
            if (lifted) throw new Unsupported("lifted shift");
            return $"{l}.{(op == "<<" ? "wrapping_shl" : "wrapping_shr")}(({r}) as u32)";
        }
        if (op == ">>>") throw new Unsupported("unsigned shift");
        // Enum arithmetic: on the numbers underneath.
        if (kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract && (lt?.TypeKind == TypeKind.Enum || rt?.TypeKind == TypeKind.Enum) && !lifted)
        {
            string a = lt!.TypeKind == TypeKind.Enum ? $"({l}.0 as i64)" : $"(({l}) as i64)", bb = rt!.TypeKind == TypeKind.Enum ? $"({r}.0 as i64)" : $"(({r}) as i64)";
            return result!.TypeKind == TypeKind.Enum ? $"{T(result)}(({a} {op} {bb}) as {T(((INamedTypeSymbol)result).EnumUnderlyingType)})" : $"(({a} {op} {bb}) as {T(result)})";
        }
        bool lNull = lt != null && World.IsNullable(lt, out _), rNull = rt != null && World.IsNullable(rt, out _);
        if (lifted || lNull || rNull)
        {
            if (equality) return $"({l} {op} {r})";
            string a = lNull ? l : $"Some({l})", bb = rNull ? r : $"Some({r})";
            if (compare) return $"rt::lifted_test({a}, {bb}, |x, y| x {op} y)";
            return $"rt::lifted({a}, {bb}, |x, y| x {op} y)";
        }
        return $"({l} {op} {r})";
    }

    private string Unary(IUnaryOperation u)
    {
        if (u.OperatorMethod is { } om && World.IsSource(om.ContainingType)) return $"{CallPath(om, false)}({Expr(u.Operand)}, c)";
        string inner = Expr(u.Operand);
        string op = u.OperatorKind switch
        {
            UnaryOperatorKind.Minus => "-", UnaryOperatorKind.Not => "!", UnaryOperatorKind.BitwiseNegation => "!", UnaryOperatorKind.Plus => "",
            _ => throw new Unsupported($"operator {u.OperatorKind}"),
        };
        if (u.OperatorKind == UnaryOperatorKind.Not && inner is "true" or "false") return inner == "true" ? "false" : "true";
        if (u.IsLifted || (u.Operand.Type != null && World.IsNullable(u.Operand.Type, out _))) return $"{inner}.map(|x| {op}x)";
        return $"({op}{inner})";
    }

    private string Coalesce(ICoalesceOperation co)
    {
        if (co.Value is IFieldReferenceOperation { Field: var cache } && w.IsCache(cache)) return Expr(co.WhenNull);
        string v = Tmp("v");
        string value = Expr(co.Value);
        string other = Expr(co.WhenNull);
        if (Presence(co.Value) is { } there) return $"(if {there} {{ {Convert(value, co.Value.Type, co.Type!, null)} }} else {{ {other} }})";
        if (co.Value.Type != null && World.IsNullable(co.Value.Type, out var inner) && N(inner) == N(co.Type))
            return $"(match {value} {{ Some({v}) => {v}, None => {other} }})";
        string converted = Convert(v, co.Value.Type, co.Type!, null);
        return $"{{ let {v} = {value}; if !rt::is_null(&{v}) {{ {converted} }} else {{ {other} }} }}";
    }

    private string ConditionalAccess(IConditionalAccessOperation op, bool statement)
    {
        if (op.Operation is IEventReferenceOperation raised && op.WhenNotNull is IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke } call)
            return Raise(raised, call);
        string r = Tmp("r");
        string recv = Expr(op.Operation);
        bool nullable = op.Operation.Type != null && World.IsNullable(op.Operation.Type, out _);
        condInstances.Push(nullable ? $"{r}.unwrap()" : r);
        try
        {
            string isNull = Presence(op.Operation) is { } there ? $"!{there}" : $"rt::is_null(&{r})";
            if (statement || op.Type == null || op.Type.SpecialType == SpecialType.System_Void)
            {
                string body = ExprStmt(op.WhenNotNull);
                return $"{{ let {r} = {recv}; if !({isNull}) {{ {body}; }} }}";
            }
            string inner = Expr(op.WhenNotNull);
            string wrapped = Wrap(inner, op.WhenNotNull.Type, op.Type);
            return $"{{ let {r} = {recv}; if {isNull} {{ {Null(op.Type)} }} else {{ {wrapped} }} }}";
        }
        finally { condInstances.Pop(); }
    }

    // ---------------------------------------------------------------- events (Events.cs)

    /// <summary>The list of an event's subscribers in the object that has the event.</summary>
    private string Subscribers(IEventReferenceOperation er, string receiver, bool mutable)
    {
        var ev = (IEventSymbol)er.Event.OriginalDefinition;
        string name = w.StateField(ev) ?? throw new Unsupported($"the event {ev.ContainingType.Name}.{ev.Name} is not kept");
        return $"{w.DataName(ev.ContainingType)}::{(mutable ? "of_mut" : "of")}({receiver}, c).{name}";
    }

    /// <summary>`x.Changed += f`, `x.Changed -= f` on an event that is state: f joins the end of the list, or the
    /// last subscriber equal to f (the same method on the same object) leaves it. On any other event the statement
    /// is presentation.</summary>
    private string Subscribe(IEventAssignmentOperation ea)
    {
        if (ea.EventReference is not IEventReferenceOperation er || !w.StateEvents.Contains(er.Event.OriginalDefinition)) throw new Dropped("event subscription");
        if (er.Instance == null) throw new Unsupported("a static event");
        string e = Tmp("e"), h = Tmp("h");
        string receiver = Expr(er.Instance), handler = Expr(ea.HandlerValue);
        string list = Subscribers(er, e, true);
        return $"{{ let {e} = {receiver}; let {h} = {handler}; {(ea.Adds ? $"{list}.push({h})" : $"rt::unsubscribe(&mut {list}, &{h})")}; }}";
    }

    /// <summary>Raising an event that is state (`Changed?.Invoke(x)`, `Changed(x)`): the subscribers are called in
    /// the order they subscribed, those there are when it is raised (one that leaves or joins while the others run
    /// changes nothing of this round, as a C# delegate is a value). With nobody subscribed the arguments are not
    /// evaluated. On any other event the statement is presentation.</summary>
    private string Raise(IEventReferenceOperation er, IInvocationOperation call)
    {
        if (!w.StateEvents.Contains(er.Event.OriginalDefinition)) throw new Dropped("event " + er.Event.Name);
        if (er.Instance == null) throw new Unsupported("a static event");
        if (!call.TargetMethod.ReturnsVoid) throw new Unsupported($"the event {er.Event.Name}, whose subscribers give a value");
        string s = Tmp("s"), f = Tmp("f");
        var args = call.Arguments.OrderBy(a => a.Parameter!.Ordinal).Select(a => (Name: Tmp("a"), Code: Expr(a.Value))).ToList();
        string lets = string.Concat(args.Select(a => $"let {a.Name} = {a.Code}; "));
        string passed = string.Join(", ", args.Select(a => a.Name + ".clone()").Append("c"));
        return $"{{ let {s} = {Subscribers(er, Expr(er.Instance), false)}.clone(); if !{s}.is_empty() {{ {lets}for {f} in {s} {{ {f}.call({passed}); }} }} }}";
    }

    /// <summary>T as T? when the target is a nullable value.</summary>
    private string Wrap(string code, ITypeSymbol? from, ITypeSymbol? to)
    {
        if (to != null && from != null && World.IsNullable(to, out _) && !World.IsNullable(from, out _)) return $"Some({code})";
        return code;
    }

    // ---------------------------------------------------------------- conversions

    private string Conv(IConversionOperation cv)
    {
        var from = cv.Operand.Type;
        var to = cv.Type!;
        if (w.IsDropped(to)) throw new Dropped(to.Name);
        if (from == null)
        {
            if (IsNullConst(cv.Operand) || cv.Operand is IDefaultValueOperation) return Null(to);
            if (cv.Operand is IAnonymousFunctionOperation or IMethodReferenceOperation) return Delegate(cv.Operand, to);
            if (cv.Operand is ICollectionExpressionOperation ce) return CollectionExpr(ce, to);
            if (cv.Operand is ISwitchExpressionOperation or IConditionalOperation or ITupleOperation or IThrowOperation) return Expr(cv.Operand);
            throw new Unsupported($"conversion of an untyped {cv.Operand.Kind}");
        }
        if (World.MetaName(to) is "System.Collections.IEnumerable" or "System.Collections.ICollection" or "System.Collections.IList" && World.CollOf(from) != World.Coll.None) return IterSource(cv.Operand);
        if (cv.IsTryCast) return IsCastNeeded(from, to) ? $"rt::as_({Expr(cv.Operand)}, {KindExpr(to)}, c)" : Expr(cv.Operand);
        if (cv.OperatorMethod is { } om)
        {
            if (!World.IsSource(om.ContainingType)) return BclConversion(cv, om) ?? throw new Unsupported($"library conversion {om.ContainingType.Name}.{om.Name}");
            if (w.IsDropped(om.ContainingType)) throw new Dropped(om.ContainingType.Name);
            return $"{CallPath(om, false)}({Expr(cv.Operand)}, c)";
        }
        return Convert(Expr(cv.Operand), from, to, cv);
    }

    private bool IsCastNeeded(ITypeSymbol from, ITypeSymbol to)
    {
        if (!w.IsObj(to) || to.SpecialType == SpecialType.System_Object) return false;
        if (!w.IsObj(from)) throw new Unsupported($"cast of {from.Name} to {to.Name}");
        return !Comp.ClassifyConversion(from, to).IsImplicit;
    }

    private Compilation Comp => w.Comp;

    private static bool IsNumeric(ITypeSymbol t) => t.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Decimal or SpecialType.System_Char;

    private string Convert(string code, ITypeSymbol? from, ITypeSymbol to, IConversionOperation? cv)
    {
        if (from == null) return code;
        string nf = N(from), nt = N(to);
        bool fromNull = World.IsNullable(from, out var fi), toNull = World.IsNullable(to, out var ti);
        if (cv != null && !cv.Conversion.IsImplicit && w.IsObj(from) && w.IsObj(to) && IsCastNeeded(from, to)) return $"rt::cast({code}, {KindExpr(to)}, c)";
        if (nf == nt) return code;
        if (fromNull && toNull) return $"{code}.map(|x| {Convert("x", fi, ti, null)})";
        if (toNull) return $"Some({Convert(code, from, ti, null)})";
        if (fromNull) return Convert($"{code}.unwrap()", fi, to, null);
        if ((IsNumeric(from) || from.TypeKind == TypeKind.Enum) && (IsNumeric(to) || to.TypeKind == TypeKind.Enum)) return Numeric(code, from, to);
        var cf = World.CollOf(from);
        var ct = World.CollOf(to);
        if (cf != World.Coll.None && ct == World.Coll.Vec) return $"{code}.into_vec()";
        if (ct == World.Coll.Vec && World.MetaName(from) == "System.Linq.IGrouping`2") return $"{code}.into_vec()";
        if (World.IsTask(from, out var fr) && World.IsTask(to, out var tr) && tr == null) return $"{code}.map(|_| ())";
        if (to.SpecialType == SpecialType.System_Object || (w.IsObj(to) && !w.IsObj(from))) throw new Unsupported($"{from.Name} boxed as {to.Name}");
        // (T)object, for a type parameter that stands for a class at every call.
        if (w.IsObj(from) && to is ITypeParameterSymbol) return $"rt::unbox::<{nt}>({code})";
        throw new Unsupported($"conversion {nf} to {nt}");
    }

    private string Numeric(string code, ITypeSymbol from, ITypeSymbol to)
    {
        if (from.TypeKind == TypeKind.Enum)
        {
            var under = ((INamedTypeSymbol)from).EnumUnderlyingType!;
            return Numeric($"{code}.0", under, to);
        }
        if (to.TypeKind == TypeKind.Enum)
        {
            var under = ((INamedTypeSymbol)to).EnumUnderlyingType!;
            return $"{T(to)}({Numeric(code, from, under)})";
        }
        string f = N(from), t = N(to);
        if (f == t) return code;
        if (t == "Dec")
            return f switch
            {
                "i32" => $"Dec::from_i32({code})", "i64" => $"Dec::from_i64({code})", "u32" => $"Dec::from_u32({code})", "u64" => $"Dec::from_u64({code})",
                "f32" => $"Dec::from_f32({code})", "f64" => $"Dec::from_f64({code})", "char" => $"Dec::from_i32({code} as u32 as i32)",
                _ => $"Dec::from_i32(({code}) as i32)",
            };
        if (f == "Dec")
            return t switch
            {
                "i32" => $"{code}.to_i32()", "i64" => $"{code}.to_i64()", "u32" => $"{code}.to_u32()", "u64" => $"{code}.to_u64()",
                "f32" => $"{code}.to_f32()", "f64" => $"{code}.to_f64()",
                _ => $"({code}.to_i32() as {t})",
            };
        if (f == "char") return $"(({code}) as u32 as {t})";
        if (t == "char") return $"rt::char_from(({code}) as u32)";
        return $"(({code}) as {t})";
    }

    // ---------------------------------------------------------------- type tests and patterns

    private string IsType(string value, ITypeSymbol? from, ITypeSymbol target)
    {
        if (w.IsDropped(target)) throw new Dropped(target.Name);
        if (from != null && World.IsNullable(from, out var inner) && N(inner) == N(target)) return $"{value}.is_some()";
        if (w.IsObj(target) && (from == null || w.IsObj(from)))
        {
            if (target.SpecialType == SpecialType.System_Object || (from != null && target is not ITypeParameterSymbol && Comp.ClassifyConversion(from, target).IsImplicit)) return $"!rt::is_null(&{value})";
            return $"rt::is({value}, {KindExpr(target)}, c)";
        }
        if (from != null && N(from) == N(target)) return $"!rt::is_null(&{value})";
        throw new Unsupported($"type test {from?.Name} is {target.Name}");
    }

    private string ConstantTest(string subject, ITypeSymbol subjectType, IOperation value)
    {
        if (IsNullConst(value)) return $"rt::is_null(&{subject})";
        string v = Expr(value);
        bool sn = World.IsNullable(subjectType, out _), vn = value.Type != null && World.IsNullable(value.Type, out _);
        return sn && !vn ? $"({subject} == Some({v}))" : $"({subject} == {v})";
    }

    private void Hoist(ILocalSymbol local)
    {
        // A task that has not been started is one that is done.
        string decl = $"let mut {NameOf(local)}: {T(local.Type)} = {(World.IsTask(local.Type, out _) ? "Ok(Default::default())" : "Default::default()")};";
        var f = frames.Peek();
        if (!f.Hoisted.Contains(decl)) f.Hoisted.Add(decl);
    }

    /// <summary>A bool expression: whether the value in the variable `s` matches the pattern (binding the pattern's variables).</summary>
    private string Pattern(IPatternOperation p, string s, ITypeSymbol st)
    {
        bool nullable = World.IsNullable(st, out var inner);
        switch (p)
        {
            case IDiscardPatternOperation: return "true";
            case IConstantPatternOperation cp: return ConstantTest(s, st, cp.Value);
            case INegatedPatternOperation np: return $"!({Pattern(np.Pattern, s, st)})";
            case IBinaryPatternOperation bp:
                return $"({Pattern(bp.LeftPattern, s, st)} {(bp.OperatorKind == BinaryOperatorKind.And ? "&&" : "||")} {Pattern(bp.RightPattern, s, st)})";
            case IRelationalPatternOperation rp:
                return nullable ? $"{s}.map_or(false, |x| x {RelOp(rp.OperatorKind)} {Expr(rp.Value)})" : $"({s} {RelOp(rp.OperatorKind)} {Expr(rp.Value)})";
            case ITypePatternOperation tp: return IsType(s, st, tp.MatchedType);
            case IDeclarationPatternOperation dp:
            {
                string test = dp.MatchesNull ? "true" : dp.MatchedType == null ? $"!rt::is_null(&{s})" : IsType(s, st, dp.MatchedType);
                if (dp.DeclaredSymbol is not ILocalSymbol local) return test;
                if (w.IsDropped(local.Type)) throw new Dropped(local.Name);
                Hoist(local);
                string bound = nullable && !World.IsNullable(local.Type, out _) ? $"{s}.unwrap()" : $"{s}{CloneOf(st)}";
                return test == "true" ? $"{{ {NameOf(local)} = {bound}; true }}" : $"(if {test} {{ {NameOf(local)} = {bound}; true }} else {{ false }})";
            }
            case IRecursivePatternOperation rp:
            {
                if (rp.DeconstructionSubpatterns.Length > 0) throw new Unsupported("positional pattern");
                string test = rp.MatchedType != null && N(rp.MatchedType) != N(nullable ? inner : st) || (rp.MatchedType != null && w.IsObj(rp.MatchedType)) ? IsType(s, st, rp.MatchedType) : $"!rt::is_null(&{s})";
                string subject = nullable ? $"{s}.unwrap()" : s;
                string rest = "true";
                if (rp.DeclaredSymbol is ILocalSymbol local)
                {
                    Hoist(local);
                    rest = $"{{ {NameOf(local)} = {subject}{CloneOf(local.Type)}; true }}";
                }
                foreach (var sub in rp.PropertySubpatterns.Reverse())
                {
                    string ps = Tmp("p");
                    condInstances.Push(subject);
                    string read;
                    try { read = sub.Member switch { IPropertyReferenceOperation pr => PropGet(pr), IFieldReferenceOperation fr => FieldGet(fr), _ => throw new Unsupported("pattern member") }; }
                    finally { condInstances.Pop(); }
                    var mt = (sub.Member as IMemberReferenceOperation)!.Type!;
                    rest = $"{{ let {ps} = {read}; if {Pattern(sub.Pattern, ps, mt)} {{ {rest} }} else {{ false }} }}";
                }
                return $"(if {test} {{ {rest} }} else {{ false }})";
            }
            default: throw new Unsupported($"pattern {p.Kind}");
        }
    }

    private string SwitchExpr(ISwitchExpressionOperation se)
    {
        string s = Tmp("s");
        var sb = new StringBuilder($"{{ let {s} = {Expr(se.Value)}; ");
        bool first = true;
        bool closed = false;
        foreach (var arm in se.Arms)
        {
            string test = Pattern(arm.Pattern, s, se.Value.Type!);
            if (arm.Guard != null) test = $"({test} && {Expr(arm.Guard)})";
            string value = Expr(arm.Value);
            if (test == "true") { sb.Append(first ? $"{{ {value} }}" : $" else {{ {value} }}"); closed = true; break; }
            sb.Append(first ? "" : " else ").Append($"if {test} {{ {value} }}");
            first = false;
        }
        if (!closed) sb.Append($" else {{ panic!(\"{Where}: no arm of the switch matches\") }}");
        return sb.Append(" }").ToString();
    }

    // ---------------------------------------------------------------- lambdas

    /// <summary>A lambda or method group as a stored delegate (FnN).</summary>
    private string Delegate(IOperation target, ITypeSymbol delegateType)
    {
        if (w.IsDropped(delegateType)) throw new Dropped(delegateType.Name);
        var invoke = ((INamedTypeSymbol)delegateType).DelegateInvokeMethod ?? throw new Unsupported("delegate without Invoke");
        string fn = $"Fn{invoke.Parameters.Length}";
        switch (target)
        {
            case IAnonymousFunctionOperation f:
            {
                var (code, captured) = Lambda(f, true);
                if (BoundLambda(f, f.Symbol, f.Body, invoke, code) is { } bound) return bound;
                string clones = string.Concat(captured.Select(x => $"let mut {x} = {x}.clone(); "));
                return $"{{ {clones}{fn}::new(move {code}) }}";
            }
            case IMethodReferenceOperation { Method: { Name: "Contains" } cm, Instance: { } list } when !World.IsSource(cm.ContainingType) && invoke.Parameters.Length == 1 && World.CollOf(list.Type) != World.Coll.None:
                // list.Contains handed over as a function.
                return $"{{ let __m = {Expr(list)}.into_vec(); {fn}::new(move |a0: {T(invoke.Parameters[0].Type)}, _c: &mut Combat| linq::contains(&__m, &a0)) }}";
            case IMethodReferenceOperation mr:
            {
                if (mr.Method.MethodKind == MethodKind.LocalFunction)
                {
                    // A local function handed over as a delegate is a lambda with a name.
                    IOperation root = mr;
                    while (root.Parent != null) root = root.Parent;
                    var local = root.DescendantsAndSelf().OfType<ILocalFunctionOperation>().First(l => Eq.Equals(l.Symbol, mr.Method.OriginalDefinition));
                    var (code, captured) = Lambda(local.Symbol, local.Body ?? throw new Unsupported("a local function without a body"), true);
                    if (BoundLambda(mr, local.Symbol, local.Body, invoke, code) is { } boundLocal) return boundLocal;
                    string clones = string.Concat(captured.Select(x => $"let mut {x} = {x}.clone(); "));
                    return $"{{ {clones}{fn}::new(move {code}) }}";
                }
                // A member that is presentation as a whole, handed over as a function, is one that does nothing.
                if (World.IsSource(mr.Method.ContainingType) && !w.IsDropped(mr.Method.ContainingType) && w.IsDropMember(mr.Method.OriginalDefinition)
                    && (invoke.ReturnsVoid || (World.IsTask(invoke.ReturnType, out var given) && given == null)))
                {
                    LeftOut++;
                    if (BoundNothing(mr, invoke) is { } nothing) return nothing;
                    var unused = invoke.Parameters.Select((p, i) => $"_a{i}: {T(p.Type)}").Append("_c: &mut Combat");
                    return $"{fn}::new(move |{string.Join(", ", unused)}| -> {(invoke.ReturnsVoid ? "()" : "R<()>")} {{ {(invoke.ReturnsVoid ? "" : "Ok(())")} }})";
                }
                if (BoundGroup(mr, invoke) is { } bound) return bound;
                var ps = invoke.Parameters.Select((p, i) => $"a{i}: {T(p.Type)}").Append("c: &mut Combat");
                string call = MethodGroupCall(mr, invoke.Parameters.Length, out string? inst);
                return $"{{ {(inst == null ? "" : $"let __i = {inst}; ")}{fn}::new(move |{string.Join(", ", ps)}| {call}) }}";
            }
            default: return Expr(target);
        }
    }

    /// <summary>The compiler's method of each place in the member where a delegate is made of a lambda or of a local
    /// function (Lambdas): filled when the member's translation starts, for the member and the lambdas inside it.</summary>
    private readonly Dictionary<IOperation, Lambdas.Made> compiled = [];

    /// <summary>Binds the places of `root` where a delegate is made, in the order of the code, to the compiler-made
    /// methods the same code's IL makes delegates of. They are bound only when both lists agree in length and, place
    /// by place, in what they are; a member where they do not is noted and its lambdas stay code made on the spot.</summary>
    private void BindLambdas(IOperation root, List<Lambdas.Made>? made)
    {
        var sites = new List<IOperation>();
        void Walk(IOperation op)
        {
            foreach (var child in op.ChildOperations)
            {
                if (child is IAnonymousFunctionOperation) { sites.Add(child); continue; }
                if (child is ILocalFunctionOperation) continue;
                if (child is IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction }) sites.Add(child);
                Walk(child);
            }
        }
        Walk(root);
        if (sites.Count == 0) return;
        bool Agrees(IOperation site, Lambdas.Made m) => site switch
        {
            IAnonymousFunctionOperation f => m.Params < 0 || m.Params == f.Symbol.Parameters.Length,
            IMethodReferenceOperation r => m.Method.Contains($"g__{r.Method.Name}|"),
            _ => false,
        };
        if (made == null || made.Count != sites.Count || !sites.Zip(made).All(x => Agrees(x.First, x.Second)))
        {
            w.Rep.Note("members whose lambdas are not bound to the compiler's names", Where);
            return;
        }
        for (int i = 0; i < sites.Count; i++)
        {
            compiled[sites[i]] = made[i];
            if (sites[i] is IAnonymousFunctionOperation { Body: { } body }) BindLambdas(body, w.Lambdas!.Inside(made[i]));
        }
    }

    /// <summary>A lambda (or a local function handed over) as a delegate that is data, when the compiler's method of it
    /// is known: that method as a function of gen/fns.rs, bound to what the compiler binds it to. That is nothing when
    /// it captures nothing, this object when it captures only `this`, and otherwise an object of the class the
    /// compiler made for what it captures (World.ClosureClass), made here with the captured values. Null where that
    /// cannot be written: the delegate is then code made on the spot.</summary>
    private string? BoundLambda(IOperation site, IMethodSymbol sym, IBlockOperation body, IMethodSymbol invoke, string code)
    {
        if (!compiled.TryGetValue(site, out var made)) return null;
        if (invoke.Parameters.Length > 5 || invoke.Parameters.Any(p => p.RefKind != RefKind.None)) return null;
        // What the lambda's code may name besides its own parameters: `me` and the combat. Not a type parameter's kind.
        if (cls.TypeParameters.Length > 0 || member is IMethodSymbol { TypeParameters.Length: > 0 }) return null;
        string target, prelude;
        switch (made.Holder)
        {
            case "c":
                (target, prelude) = ("Obj::NULL", "");
                break;
            case "this" when made.IsStatic:
                (target, prelude) = ("Obj::NULL", "");
                break;
            case "this":
                if (isStructMember || member.IsStatic) return null;
                (target, prelude) = ("me", "let me = t;\n");
                break;
            case "closure":
                if (ClosureObject(site, made, sym, body) is not { } closure) return null;
                (target, prelude) = closure;
                break;
            default:
                return null;
        }
        var types = invoke.Parameters.Select(p => T(p.Type)).ToList();
        string ret = invoke.ReturnsVoid ? "()" : T(invoke.ReturnType);
        string clr = $"{made.Class}::{made.Method}";
        string name = $"lf_{World.Snake(cls.Name)}__{World.Snake(member.Name)}_{World.Mark(clr + "|" + string.Join(",", types) + "->" + ret)}";
        var args = Enumerable.Range(0, types.Count).Select(i => $"a{i}").Append("c");
        w.Fns.TryAdd(name, new World.FnEntry(name, clr, null, types, ret, $"{prelude}({code})({string.Join(", ", args)})"));
        w.Rep.Count(made.Holder == "closure" ? "lambdas that are data, bound to an object of what they capture" : "lambdas that are data (bound to the compiler's name)");
        return $"g::fns::{name}({target})";
    }

    /// <summary>The class the compiler made for what a lambda captures, as a class of the engine: the code that makes
    /// its object here with the captured values, and the code that takes them out again where the lambda runs. Null
    /// when a field of it is not a variable of this member (another scope's class held by this one).
    ///
    /// C# makes one object for a scope and every lambda of the scope shares it; here each place a delegate is made
    /// makes its own, with the values the variables have there. A variable of the class that is not declared yet at
    /// that place has its default, as in C#.</summary>
    private (string Target, string Prelude)? ClosureObject(IOperation site, Lambdas.Made made, IMethodSymbol sym, IBlockOperation body)
    {
        if (!made.Class.StartsWith(World.FullName(cls) + "+", StringComparison.Ordinal)) return null;
        var inner = new HashSet<ISymbol>(Eq) { sym };
        foreach (var d in body.Descendants())
        {
            if (d is IAnonymousFunctionOperation nested) inner.Add(nested.Symbol);
            if (d is ILocalFunctionOperation local) inner.Add(local.Symbol);
        }
        var used = new HashSet<ISymbol>(Eq);
        bool usesThis = false;
        foreach (var d in body.Descendants())
        {
            if (d is IInstanceReferenceOperation) usesThis = true;
            if (d.ConstantValue.HasValue) continue;
            ISymbol? s = d switch { ILocalReferenceOperation l => l.Local, IParameterReferenceOperation p => p.Parameter, _ => null };
            if (s != null && !inner.Contains(s.ContainingSymbol)) used.Add(s);
        }
        if (usesThis && !made.Fields.Contains("<>4__this")) return null;
        // Every variable of the member by its name: a field of the class is one of them.
        var known = new Dictionary<string, List<ISymbol>>();
        void Know(ISymbol s) { if (!known.TryGetValue(s.Name, out var list)) known[s.Name] = list = []; if (!list.Contains(s, Eq)) list.Add(s); }
        if (member is IMethodSymbol owner) foreach (var p in owner.Parameters) Know(p);
        if (member is IPropertySymbol { SetMethod: { } setter }) foreach (var p in setter.Parameters) Know(p);
        if (memberRoot != null)
            foreach (var d in memberRoot.DescendantsAndSelf())
                switch (d)
                {
                    case IVariableDeclaratorOperation v: Know(v.Symbol); break;
                    case ILocalReferenceOperation l: Know(l.Local); break;
                    case IParameterReferenceOperation p: Know(p.Parameter); break;
                }
        var fields = new List<World.ClosureField>();
        var dropped = new List<string>();
        var values = new List<string>();
        var prelude = new StringBuilder();
        string name = w.Unique(cls) + "Closure" + made.Class[(made.Class.LastIndexOf("DisplayClass", StringComparison.Ordinal) + "DisplayClass".Length)..];
        foreach (string cs in made.Fields)
        {
            if (cs == "<>4__this")
            {
                if (isStructMember || member.IsStatic) return null;
                fields.Add(new World.ClosureField("this", cs, cls));
                values.Add("me");
                prelude.Append($"let me = {name}Data::of(t, c).this;\n");
                continue;
            }
            ISymbol? s = used.FirstOrDefault(u => u.Name == cs) ?? (known.TryGetValue(cs, out var same) && same.Count == 1 ? same[0] : null);
            if (s == null || aliases.ContainsKey(s)) return null;
            ITypeSymbol type = s is ILocalSymbol ls ? ls.Type : ((IParameterSymbol)s).Type;
            if (s is IParameterSymbol { RefKind: not RefKind.None }) return null;
            // A captured variable that is presentation is not held, as everywhere.
            if (w.IsDropped(type) || droppedLocals.Contains(s) || (s is IParameterSymbol dp && w.IsDropped(dp))) { dropped.Add(cs); continue; }
            string rust = World.Ident(World.Snake(cs));
            if (rust == "this") rust += "_";
            string rtype;
            try { rtype = T(type); }
            catch (Exception e) when (e is Dropped or Unsupported) { return null; }
            fields.Add(new World.ClosureField(rust, cs, type));
            bool here = used.Contains(s) || names.ContainsKey(s);
            values.Add(here ? NameOf(s) + CloneOf(type) : "Default::default()");
            if (used.Contains(s)) prelude.Append($"let mut {NameOf(s)}: {rtype} = {name}Data::of(t, c).{rust}.clone();\n");
        }
        // A variable the lambda uses that the class does not hold: the lambda is not the one this class was made for.
        if (used.Any(u => !made.Fields.Contains(u.Name))) return null;
        // The known difference: the object holds the values as they are here, and C# would show the lambda a later write.
        // Later: after it in the code, or anywhere in a loop around it, for a variable declared outside that loop's body
        // (a round's write runs after the lambda of the round before; a `for`'s step runs after its body).
        var loopsAround = new List<ILoopOperation>();
        for (var up = site.Parent; up != null && up is not (IAnonymousFunctionOperation or ILocalFunctionOperation); up = up.Parent)
            if (up is ILoopOperation loop) loopsAround.Add(loop);
        bool InLoopAfter(IOperation write, ISymbol variable) => loopsAround.Any(loop =>
            loop.Syntax.Span.Contains(write.Syntax.Span)
            && !variable.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == loop.Body.Syntax.SyntaxTree && loop.Body.Syntax.Span.Contains(r.Span)));
        if (memberRoot != null)
            foreach (var d in memberRoot.Descendants())
            {
                IOperation? written = d switch
                {
                    ISimpleAssignmentOperation a => a.Target,
                    ICompoundAssignmentOperation a => a.Target,
                    IIncrementOrDecrementOperation a => a.Target,
                    _ => null,
                };
                // A write to a part of a value held in the variable is a write to the variable.
                while (written is IMemberReferenceOperation { Instance: { Type: { } held } inside } && w.IsValue(held)) written = inside;
                ISymbol? target = written switch { ILocalReferenceOperation l => l.Local, IParameterReferenceOperation p => p.Parameter, _ => null };
                if (target != null && used.Contains(target) && (d.Syntax.Span.End >= site.Syntax.Span.End || InLoopAfter(d, target)))
                    w.Rep.Note("lambdas that read a variable written after they were made (they read the value from before)", $"{Where}: {target.Name}");
            }
        w.Closures.TryAdd(made.Class, new World.ClosureClass(name, made.Class, fields, dropped));
        return ($"{name}Data::make({string.Concat(values.Select(v => v + ", "))}c)", prelude.ToString());
    }

    /// <summary>A method group as a delegate that is data: the method as a function of gen/fns.rs, bound to the object
    /// it is taken from (nothing for a static one). Null where the method cannot be named that way (a generic one, one
    /// of a value): the delegate is then code made on the spot.</summary>
    private string? BoundGroup(IMethodReferenceOperation mr, IMethodSymbol invoke)
    {
        var m = mr.Method;
        if (!World.IsSource(m.ContainingType) || m.MethodKind != MethodKind.Ordinary || invoke.Parameters.Length > 5) return null;
        if (invoke.Parameters.Any(p => p.RefKind != RefKind.None) || m.Parameters.Any(p => p.RefKind != RefKind.None || w.IsDropped(p) || w.IsChangedParam(p))) return null;
        if (m.Parameters.Length != invoke.Parameters.Length || KindArgs(m).Count > 0 || Turbofish(m).Length > 0) return null;
        if (mr.Instance != null && (w.IsValue(m.ContainingType) || mr.Instance.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface })) return null;
        bool virt = mr.IsVirtual && !IsBaseAccess(mr.Instance);
        if (IsBaseAccess(mr.Instance)) return null;
        var types = invoke.Parameters.Select(p => T(p.Type)).ToList();
        string ret = invoke.ReturnsVoid ? "()" : T(invoke.ReturnType);
        var args = new List<string>();
        if (mr.Instance != null) args.Add("t");
        for (int i = 0; i < types.Count; i++) args.Add($"a{i}");
        args.Add("c");
        string path = CallPath(m, virt);
        string stem = path.StartsWith("g::") ? path[3..] : path.StartsWith("m::") ? "m_" + path[3..] : path;
        string name = $"mg_{stem.Replace("::", "__")}_{World.Mark(string.Join(",", types) + "->" + ret)}";
        var slot = virt && path.StartsWith("g::dispatch::") ? (IMethodSymbol)World.Slot(m.OriginalDefinition) : null;
        w.Fns.TryAdd(name, new World.FnEntry(name, $"{World.FullName(m.ContainingType)}::{m.Name}", slot, types, ret, $"{path}({string.Join(", ", args)})"));
        return $"g::fns::{name}({(mr.Instance == null ? "Obj::NULL" : Expr(mr.Instance))})";
    }

    /// <summary>A member that is presentation as a whole, handed over as a function: still the game's method by its
    /// name, bound to its object, and a function of the table that does nothing.</summary>
    private string? BoundNothing(IMethodReferenceOperation mr, IMethodSymbol invoke)
    {
        var m = mr.Method;
        if (m.MethodKind != MethodKind.Ordinary || invoke.Parameters.Length > 5 || invoke.Parameters.Any(p => p.RefKind != RefKind.None)) return null;
        if (m.IsGenericMethod || m.ContainingType.IsGenericType || IsBaseAccess(mr.Instance)) return null;
        if (mr.Instance != null && (w.IsValue(m.ContainingType) || mr.Instance.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Interface })) return null;
        var types = invoke.Parameters.Select(p => T(p.Type)).ToList();
        string ret = invoke.ReturnsVoid ? "()" : T(invoke.ReturnType);
        string name = $"mg_{World.Snake(w.Unique(m.ContainingType))}__{World.Snake(m.Name)}_nothing_{World.Mark(World.FullName(m.ContainingType) + "|" + string.Join(",", types) + "->" + ret)}";
        var slot = mr.IsVirtual ? (IMethodSymbol)World.Slot(m.OriginalDefinition) : null;
        w.Fns.TryAdd(name, new World.FnEntry(name, $"{World.FullName(m.ContainingType)}::{m.Name}", slot, types, ret, invoke.ReturnsVoid ? "()" : "Ok(())"));
        return $"g::fns::{name}({(mr.Instance == null ? "Obj::NULL" : Expr(mr.Instance))})";
    }

    private string MethodGroupCall(IMethodReferenceOperation mr, int arity, out string? instance)
    {
        var m = mr.Method;
        if (!World.IsSource(m.ContainingType)) throw new Unsupported($"library method {m.ContainingType.Name}.{m.Name} as a delegate");
        var args = KindArgs(m);
        instance = null;
        if (mr.Instance != null)
        {
            instance = Expr(mr.Instance);
            args.Add("__i");
        }
        for (int i = 0; i < arity; i++) args.Add($"a{i}");
        args.Add("c");
        return $"{CallPath(m, mr.IsVirtual && !IsBaseAccess(mr.Instance))}{Turbofish(m)}({string.Join(", ", args)})";
    }

    /// <summary>A closure `|a: A, c: &mut Combat| -> R { .. }`, and the outer locals it captures that are not Copy.</summary>
    private (string Code, List<string> Captured) Lambda(IAnonymousFunctionOperation f, bool stored) => Lambda(f.Symbol, f.Body, stored);

    private (string Code, List<string> Captured) Lambda(IMethodSymbol sym, IBlockOperation fBody, bool stored)
    {
        var ps = new List<string>();
        foreach (var p in sym.Parameters)
        {
            if (p.RefKind != RefKind.None) throw new Unsupported("lambda with ref parameters");
            if (w.IsDropped(p.Type)) throw new Dropped(p.Type.Name);
            ps.Add($"mut {NameOf(p)}: {T(p.Type)}");
        }
        ps.Add("c: &mut Combat");
        // A lambda with nothing in it but presentation does nothing here.
        if ((sym.ReturnsVoid || (World.IsTask(sym.ReturnType, out var gives) && gives == null)) && !HoldsGameCall(fBody) && WritesOnlyItsOwn(sym, fBody))
        {
            LeftOut++;
            return ($"|{string.Join(", ", ps.Select(p => p.StartsWith("mut ") ? "_" + p[4..] : "_" + p))}| -> {(sym.ReturnsVoid ? "()" : T(sym.ReturnType))} {{ {(sym.ReturnsVoid ? "" : "Ok(())")} }}", []);
        }
        var frame = new Frame { Async = sym.IsAsync, ReturnsTask = World.IsTask(sym.ReturnType, out _), Ret = sym.ReturnsVoid ? null : sym.ReturnType, Lambda = sym };
        frames.Push(frame);
        var savedLoops = loops.ToArray();
        loops.Clear();
        string body;
        try { body = Sub(fBody); }
        finally
        {
            frames.Pop();
            foreach (var l in savedLoops.Reverse()) loops.Push(l);
        }
        string head = string.Concat(frame.Hoisted.Select(h => h + "\n"));
        string ret = sym.ReturnsVoid ? "()" : T(sym.ReturnType);
        var captured = new List<string>();
        var inner = new HashSet<ISymbol>(Eq) { sym };
        foreach (var d in fBody.Descendants())
            if (d is IAnonymousFunctionOperation nested) inner.Add(nested.Symbol);
        foreach (var d in fBody.Descendants())
        {
            ISymbol? s = d switch { ILocalReferenceOperation l => l.Local, IParameterReferenceOperation p => p.Parameter, _ => null };
            if (s == null || inner.Contains(s.ContainingSymbol) || droppedLocals.Contains(s) || aliases.ContainsKey(s)) continue;
            ITypeSymbol type = s is ILocalSymbol ls ? ls.Type : ((IParameterSymbol)s).Type;
            if (stored && IsWritten(fBody, s)) throw new Unsupported("a stored lambda writes a captured local");
            if (!w.IsCopy(type) && !captured.Contains(NameOf(s))) captured.Add(NameOf(s));
        }
        return ($"|{string.Join(", ", ps)}| -> {ret} {{\n{head}{body}{Tail(frame, sym.ReturnsVoid)}}}", captured);
    }

    /// <summary>Whether every local from outside that the lambda writes is one nothing else reads.</summary>
    private bool WritesOnlyItsOwn(IMethodSymbol sym, IBlockOperation fBody)
    {
        IOperation root = fBody;
        while (root.Parent != null) root = root.Parent;
        var inside = new HashSet<IOperation>(fBody.Descendants());
        foreach (var d in fBody.Descendants())
        {
            IOperation? target = d switch { IAssignmentOperation a => a.Target, IIncrementOrDecrementOperation a => a.Target, _ => null };
            if (target is IParameterReferenceOperation p && !Eq.Equals(p.Parameter.ContainingSymbol, sym)) return false;
            if (target is not ILocalReferenceOperation l || w.IsDropped(l.Local.Type)) continue;
            if (root.Descendants().OfType<ILocalReferenceOperation>().Any(r => Eq.Equals(r.Local, l.Local) && !inside.Contains(r))) return false;
        }
        return true;
    }

    private static bool IsWritten(IOperation body, ISymbol s)
    {
        foreach (var d in body.Descendants())
        {
            IOperation? target = d switch
            {
                ISimpleAssignmentOperation a => a.Target,
                ICompoundAssignmentOperation a => a.Target,
                IIncrementOrDecrementOperation a => a.Target,
                _ => null,
            };
            if (target is ILocalReferenceOperation l && Eq.Equals(l.Local, s)) return true;
            if (target is IParameterReferenceOperation p && Eq.Equals(p.Parameter, s)) return true;
        }
        return false;
    }

    /// <summary>An argument that is a function, as a plain closure for an rt helper: `|x, c| ..`.</summary>
    private string Closure(IOperation arg, int arity)
    {
        while (arg is IConversionOperation cv) arg = cv.Operand;
        if (arg is IDelegateCreationOperation dc) arg = dc.Target;
        switch (arg)
        {
            case IAnonymousFunctionOperation f: return Lambda(f, false).Code;
            case IMethodReferenceOperation { Method: { Name: "Contains" } cm, Instance: { } list } when !World.IsSource(cm.ContainingType) && arity == 1 && World.CollOf(list.Type) != World.Coll.None:
                return $"{{ let __m = {Expr(list)}.into_vec(); move |a0, _c: &mut Combat| linq::contains(&__m, &a0) }}";
            case IMethodReferenceOperation mr:
            {
                if (mr.Instance != null && mr.Instance is not IInstanceReferenceOperation) throw new Unsupported("method group on a computed receiver");
                var invoke = mr.Method;
                var ps = Enumerable.Range(0, arity).Select(i => $"a{i}").Append("c: &mut Combat");
                string call = MethodGroupCall(mr, arity, out string? inst);
                return $"|{string.Join(", ", ps)}| {call.Replace("__i", inst ?? "__i")}";
            }
            default:
            {
                var ps = Enumerable.Range(0, arity).Select(i => $"a{i}").ToList();
                return $"|{string.Join(", ", ps.Append("c: &mut Combat"))}| {Ref(arg)}.call({string.Join(", ", ps.Append("c"))})";
            }
        }
    }

    // ---------------------------------------------------------------- strings and collections

    private string Interpolated(IInterpolatedStringOperation s)
    {
        var parts = new List<string>();
        foreach (var part in s.Parts)
        {
            switch (part)
            {
                case IInterpolatedStringTextOperation t: parts.Add(Expr(t.Text)); break;
                case IInterpolationOperation i:
                    if (i.FormatString != null || i.Alignment != null) throw new Unsupported("format string");
                    parts.Add(StrOf(i.Expression));
                    break;
                default: throw new Unsupported($"interpolation part {part.Kind}");
            }
        }
        return $"Str::concat(&[{string.Join(", ", parts)}])";
    }

    private string CollectionExpr(ICollectionExpressionOperation ce, ITypeSymbol? target = null)
    {
        target ??= ce.Type;
        string v;
        if (ce.Elements.All(e => e is not ISpreadOperation)) v = $"vec![{string.Join(", ", ce.Elements.Select(Expr))}]";
        else
        {
            var sb = new StringBuilder("{ let mut __v = Vec::new(); ");
            foreach (var e in ce.Elements)
                sb.Append(e is ISpreadOperation sp ? $"__v.extend({IterSource(sp.Operand)}); " : $"__v.push({Expr(e)}); ");
            v = sb.Append("__v }").ToString();
        }
        return World.CollOf(target) switch
        {
            World.Coll.Vec => v,
            World.Coll.Set => $"Set::from_vec({v})",
            World.Coll.Queue => $"Queue::from_vec({v})",
            _ => throw new Unsupported($"collection expression of {target?.Name}"),
        };
    }
}
