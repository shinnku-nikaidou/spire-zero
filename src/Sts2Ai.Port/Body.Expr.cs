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
            case ISimpleAssignmentOperation a: return Assign(a.Target, () => Guard(a.Value, "a write of presentation to state"), false);
            case ICompoundAssignmentOperation ca: return Compound(ca, false);
            case IIncrementOrDecrementOperation inc: return Increment(inc, false);
            case ICoalesceAssignmentOperation co:
            {
                string value = Expr(co.Value);
                return $"if rt::is_null(&{Expr(co.Target)}) {{ {Assign(co.Target, () => Wrap(value, co.Value.Type, co.Target.Type), false)}; }}";
            }
            case IConditionalAccessOperation ca: return ConditionalAccess(ca, true);
            case IAwaitOperation aw: return Expr(aw);
            case IInvocationOperation inv:
            {
                string code = Expr(inv);
                if (code.Length == 0) return "";
                if (inv.Type == null || inv.Type.SpecialType == SpecialType.System_Void) return code;
                if (World.IsTask(inv.Type, out _)) w.Rep.Note("a task that is not awaited", Where);
                return $"let _ = {code}";
            }
            case IDeconstructionAssignmentOperation: throw new Unsupported("deconstruction");
            default: return $"let _ = {Expr(op)}";
        }
    }

    private string Guard(IOperation op, string why)
    {
        try { return Expr(op); }
        catch (Dropped d) { throw new Unsupported($"{why} ({d.Message})"); }
    }

    // ---------------------------------------------------------------- expressions

    /// <summary>Rust code that gives an owned value of the operation's type.</summary>
    private string Expr(IOperation op)
    {
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
            case IEventReferenceOperation ev: throw new Dropped("event " + ev.Event.Name);
            case IEventAssignmentOperation: throw new Dropped("event subscription");
            case IInvocationOperation inv: return Invoke(inv);
            case IObjectCreationOperation oc: return New(oc);
            case IConversionOperation cv: return Conv(cv);
            case IBinaryOperation b: return Binary(b);
            case IUnaryOperation u: return Unary(u);
            case ISimpleAssignmentOperation a: return Assign(a.Target, () => Guard(a.Value, "a write of presentation to state"), true);
            case ICompoundAssignmentOperation ca: return Compound(ca, true);
            case IIncrementOrDecrementOperation inc: return Increment(inc, true);
            case ICoalesceOperation co: return Coalesce(co);
            case ICoalesceAssignmentOperation coa:
            {
                string value = Expr(coa.Value);
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

    private string Read(ISymbol local, ITypeSymbol? type)
    {
        if (droppedLocals.Contains(local)) throw new Dropped(local.Name);
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
        _ => false,
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
        if (virtualCall && VirtualMember(owner) && World.IsSource(World.Slot(m).ContainingType) && !w.IsValue(type))
        {
            var slot = World.Slot(m);
            w.Slots.TryAdd(slot, 0);
            return $"g::dispatch::{w.Module(slot.ContainingType)}::{w.FnName((IMethodSymbol)slot)}";
        }
        if (m.IsAbstract) throw new Unsupported($"a direct call of the abstract {type.Name}.{m.Name}");
        var cat = w.CatOfMember(m);
        if (cat == Cat.Stub) w.StubRefs.TryAdd(m, 0);
        if (cat == Cat.Manual) w.ManualRefs.TryAdd(m, 0);
        return $"{w.Path(type, m)}::{w.FnName(m)}";
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

    private string Invoke(IInvocationOperation op)
    {
        var m = op.TargetMethod;
        if (Value(m.ReducedFrom ?? m) is { } fixedValue) return fixedValue;
        if (m.MethodKind == MethodKind.LocalFunction) throw new Unsupported("local function");
        if (m.MethodKind == MethodKind.DelegateInvoke)
        {
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
        string path = CallPath(m, op.IsVirtual && !IsBaseAccess(op.Instance)) + Turbofish(m);
        // base(..) or this(..) at the head of a constructor: the other constructor's body, on this object.
        // For a value, the other constructor makes the value.
        bool valueCtor = m.MethodKind == MethodKind.Constructor && w.IsValue(type);
        if (m.MethodKind == MethodKind.Constructor && !valueCtor) path = CtorPath(path);
        var args = KindArgs(m);
        if (op.Instance != null && !valueCtor) args.Add(Guard(op.Instance, $"a call of {type.Name}.{m.Name} on presentation"));
        var after = new List<string>();
        var ordered = op.Arguments.OrderBy(a => a.Parameter!.Ordinal).ToList();
        bool inOrder = ordered.SequenceEqual(op.Arguments);
        var pre = new List<string>();
        var outs = new List<string>();
        foreach (var a in ordered)
        {
            var kind = a.Parameter!.RefKind;
            if (w.IsDropped(a.Parameter.Type))
            {
                if (HoldsGameCall(a.Value)) w.Rep.DroppedWithCall(Where, a.Value);
                continue;
            }
            if (kind is RefKind.Out or RefKind.Ref)
            {
                string o = Tmp("o");
                outs.Add(o);
                if (a.Value is not IDiscardOperation) after.Add(Assign(a.Value is IDeclarationExpressionOperation de ? de.Expression : a.Value, () => o, false) + ";");
                if (kind == RefKind.Out) continue;
            }
            string code = Guard(a.Value, $"a call of {type.Name}.{m.Name} with presentation");
            if (!inOrder) { string t = Tmp("a"); pre.Add($"let {t} = {code};"); code = t; }
            args.Add(code);
        }
        if (!inOrder)
        {
            // Named arguments: evaluated in the order written.
            var passed = ordered.Where(x => x.Parameter!.RefKind != RefKind.Out && !w.IsDropped(x.Parameter.Type)).ToList();
            pre = op.Arguments.Where(passed.Contains).Select(a => pre[passed.IndexOf(a)]).ToList();
        }
        args.Add("c");
        string call = $"{path}({string.Join(", ", args)})";
        if (valueCtor) call = $"me = {call}";
        if (outs.Count == 0) return pre.Count == 0 ? call : $"{{ {string.Join(" ", pre)} {call} }}";
        bool hasValue = !m.ReturnsVoid;
        var names = new List<string>();
        if (hasValue) names.Add("__r");
        names.AddRange(outs);
        string pattern = names.Count == 1 ? names[0] : "(" + string.Join(", ", names) + ")";
        return $"{{ {string.Join(" ", pre)} let {pattern} = {call}; {string.Join(" ", after)} {(hasValue ? "__r" : "")} }}";
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
                    if (w.IsDropped(a.Parameter!.Type)) continue;
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
            case ILocalReferenceOperation l:
                if (droppedLocals.Contains(l.Local)) throw new Dropped(l.Local.Name);
                return aliases.TryGetValue(l.Local, out var a) ? a.Replace("::of(", "::of_mut(") : NameOf(l.Local);
            case IParameterReferenceOperation p:
                if (droppedLocals.Contains(p.Parameter)) throw new Dropped(p.Parameter.Name);
                return NameOf(p.Parameter);
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }: return implicitReceivers.Peek();
            case IInstanceReferenceOperation when isStructMember: return "me";
            case IFieldReferenceOperation { Field.IsStatic: false } f when !f.Field.ContainingType.IsTupleType: return FieldPlace(f, true);
            case IFieldReferenceOperation { Field.ContainingType.IsTupleType: true } tf: return $"{Place(tf.Instance!)}.{TupleIndex(tf.Field)}";
            case IPropertyReferenceOperation { Property: { IsStatic: false } p } pr when w.IsValue(p.ContainingType)
                && p.ContainingType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => Eq.Equals(f.AssociatedSymbol, p.OriginalDefinition)) is { } auto:
                return $"{Place(pr.Instance!)}.{World.FieldName(auto)}";
            case IPropertyReferenceOperation pr when FieldPlaceOf(pr) is { } fp: return fp.Replace("::of(", "::of_mut(");
            case IConversionOperation { IsTryCast: false } cv when N(cv.Type) == N(cv.Operand.Type): return Place(cv.Operand);
            case IConditionalAccessInstanceOperation: return condInstances.Peek();
            case IArrayElementReferenceOperation ae when ae.Indices.Length == 1 && IsSimple(ae.Indices[0]): return $"{Place(ae.ArrayReference)}[({Expr(ae.Indices[0])}) as usize]";
            case IPropertyReferenceOperation { Property.IsIndexer: true, Arguments.Length: 1 } ix when World.CollOf(ix.Property.ContainingType) == World.Coll.Vec && IsSimple(ix.Arguments[0].Value):
                return $"{Place(ix.Instance!)}[({Expr(ix.Arguments[0].Value)}) as usize]";
            default: throw new Unsupported($"a change in place of {op.Kind} ({op.Syntax.ToString().Split('\n')[0].Trim()})");
        }
    }

    /// <summary>A Rust expression to read through without copying: the place itself when there is one.</summary>
    private string Ref(IOperation op)
    {
        switch (op)
        {
            case ILocalReferenceOperation l when !droppedLocals.Contains(l.Local): return aliases.TryGetValue(l.Local, out var a) ? a : NameOf(l.Local);
            case IParameterReferenceOperation p when !droppedLocals.Contains(p.Parameter): return NameOf(p.Parameter);
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver }: return implicitReceivers.Peek();
            case IInstanceReferenceOperation when isStructMember: return "me";
            case IFieldReferenceOperation { Field.IsStatic: false } f when !f.Field.ContainingType.IsTupleType && World.IsSource(f.Field.ContainingType) && Value(f.Field) == null: return FieldPlace(f, false);
            case IPropertyReferenceOperation pr when FieldPlaceOf(pr) is { } fp: return fp;
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

    private string Assign(IOperation target, Func<string> value, bool asValue)
    {
        string Done(string store, string v) => asValue ? $"{{ let __v = {v}; {store.Replace("$v", "__v" + CloneOf(target.Type))}; __v }}" : UsesCombat(store.Replace("$v", "")) && UsesCombat(v) ? $"{{ let __v = {v}; {store.Replace("$v", "__v")}; }}" : store.Replace("$v", v);
        switch (target)
        {
            case ILocalReferenceOperation { IsDeclaration: true } decl when !droppedLocals.Contains(decl.Local) && !w.IsDropped(decl.Local.Type):
                Hoist(decl.Local);
                return Done($"{NameOf(decl.Local)} = $v", value());
            case ILocalReferenceOperation or IParameterReferenceOperation or IArrayElementReferenceOperation:
            case IFieldReferenceOperation { Field.ContainingType.IsTupleType: true }:
                return Done($"{Place(target)} = $v", value());
            case IDeclarationExpressionOperation de:
                if (de.Expression is ILocalReferenceOperation dl && !w.IsDropped(dl.Local.Type)) Hoist(dl.Local);
                return Assign(de.Expression, value, asValue);
            case IInstanceReferenceOperation when isStructMember: return Done("me = $v", value());
            case IDiscardOperation: return Done("let _ = $v", value());
            case IEventReferenceOperation ev: throw new Dropped("event " + ev.Event.Name);
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
        string old = Tmp("old");
        string value = Expr(ca.Value);
        string result = BinaryCode(ca.OperatorKind, old, value, ca.Target.Type, ca.Value.Type, ca.Target.Type, ca.IsLifted, ca.Value);
        string store = Assign(ca.Target, () => result, asValue);
        return $"{{ let {old} = {Expr(ca.Target)}; {store}{(asValue ? "" : ";")} }}";
    }

    private string Increment(IIncrementOrDecrementOperation inc, bool asValue)
    {
        string op = inc.Kind == OperationKind.Increment ? "+" : "-";
        string one = inc.Target.Type?.SpecialType == SpecialType.System_Decimal ? "Dec::ONE" : inc.Target.Type?.SpecialType is SpecialType.System_Single or SpecialType.System_Double ? "1.0" : "1";
        string old = Tmp("old");
        if (World.IsNullable(inc.Target.Type!, out _)) throw new Unsupported("increment of a nullable");
        if (!asValue && inc.Target is ILocalReferenceOperation or IParameterReferenceOperation) return $"{Place(inc.Target)} {op}= {one}";
        string store = Assign(inc.Target, () => $"({old} {op} {one})", false);
        if (!asValue) return $"{{ let {old} = {Expr(inc.Target)}; {store}; }}";
        return inc.IsPostfix ? $"{{ let {old} = {Expr(inc.Target)}; {store}; {old} }}" : $"{{ let {old} = {Expr(inc.Target)}; {store}; ({old} {op} {one}) }}";
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
            if (World.CollOf(other.Type) != World.Coll.None) w.Rep.Count("null test on a collection (never null here)");
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
        if (b.OperatorMethod is { } om && World.IsSource(om.ContainingType) && !(om.IsImplicitlyDeclared && w.IsValue(om.ContainingType)))
        {
            if (w.IsDropped(om.ContainingType)) throw new Dropped(om.ContainingType.Name);
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
        if (co.Value.Type != null && World.IsNullable(co.Value.Type, out var inner) && N(inner) == N(co.Type))
            return $"(match {value} {{ Some({v}) => {v}, None => {other} }})";
        string converted = Convert(v, co.Value.Type, co.Type!, null);
        return $"{{ let {v} = {value}; if !rt::is_null(&{v}) {{ {converted} }} else {{ {other} }} }}";
    }

    private string ConditionalAccess(IConditionalAccessOperation op, bool statement)
    {
        string r = Tmp("r");
        string recv = Expr(op.Operation);
        bool nullable = op.Operation.Type != null && World.IsNullable(op.Operation.Type, out _);
        condInstances.Push(nullable ? $"{r}.unwrap()" : r);
        try
        {
            if (statement || op.Type == null || op.Type.SpecialType == SpecialType.System_Void)
            {
                string body = ExprStmt(op.WhenNotNull);
                return $"{{ let {r} = {recv}; if !rt::is_null(&{r}) {{ {body}; }} }}";
            }
            string inner = Expr(op.WhenNotNull);
            string wrapped = Wrap(inner, op.WhenNotNull.Type, op.Type);
            return $"{{ let {r} = {recv}; if rt::is_null(&{r}) {{ {Null(op.Type)} }} else {{ {wrapped} }} }}";
        }
        finally { condInstances.Pop(); }
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
        if (World.IsTask(from, out var fr) && World.IsTask(to, out var tr) && tr == null) return $"{code}.map(|_| ())";
        if (to.SpecialType == SpecialType.System_Object || (w.IsObj(to) && !w.IsObj(from))) throw new Unsupported($"{from.Name} boxed as {to.Name}");
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
        string decl = $"let mut {NameOf(local)}: {T(local.Type)} = Default::default();";
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
                string clones = string.Concat(captured.Select(x => $"let mut {x} = {x}.clone(); "));
                return $"{{ {clones}{fn}::new(move {code}) }}";
            }
            case IMethodReferenceOperation mr:
            {
                if (mr.Method.MethodKind == MethodKind.LocalFunction) throw new Unsupported("local function");
                var ps = invoke.Parameters.Select((p, i) => $"a{i}: {T(p.Type)}").Append("c: &mut Combat");
                string call = MethodGroupCall(mr, invoke.Parameters.Length, out string? inst);
                return $"{{ {(inst == null ? "" : $"let __i = {inst}; ")}{fn}::new(move |{string.Join(", ", ps)}| {call}) }}";
            }
            default: return Expr(target);
        }
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
    private (string Code, List<string> Captured) Lambda(IAnonymousFunctionOperation f, bool stored)
    {
        var sym = f.Symbol;
        var ps = new List<string>();
        foreach (var p in sym.Parameters)
        {
            if (p.RefKind != RefKind.None) throw new Unsupported("lambda with ref parameters");
            if (w.IsDropped(p.Type)) throw new Dropped(p.Type.Name);
            ps.Add($"mut {NameOf(p)}: {T(p.Type)}");
        }
        ps.Add("c: &mut Combat");
        var frame = new Frame { Async = sym.IsAsync, ReturnsTask = World.IsTask(sym.ReturnType, out _), Ret = sym.ReturnsVoid ? null : sym.ReturnType, Lambda = sym };
        frames.Push(frame);
        var savedLoops = loops.ToArray();
        loops.Clear();
        string body;
        try { body = Sub(f.Body); }
        finally
        {
            frames.Pop();
            foreach (var l in savedLoops.Reverse()) loops.Push(l);
        }
        string head = string.Concat(frame.Hoisted.Select(h => h + "\n"));
        string ret = sym.ReturnsVoid ? "()" : T(sym.ReturnType);
        var captured = new List<string>();
        var inner = new HashSet<ISymbol>(Eq) { sym };
        foreach (var d in f.Body.Descendants())
            if (d is IAnonymousFunctionOperation nested) inner.Add(nested.Symbol);
        foreach (var d in f.Body.Descendants())
        {
            ISymbol? s = d switch { ILocalReferenceOperation l => l.Local, IParameterReferenceOperation p => p.Parameter, _ => null };
            if (s == null || inner.Contains(s.ContainingSymbol) || droppedLocals.Contains(s) || aliases.ContainsKey(s)) continue;
            ITypeSymbol type = s is ILocalSymbol ls ? ls.Type : ((IParameterSymbol)s).Type;
            if (stored && IsWritten(f.Body, s)) throw new Unsupported("a stored lambda writes a captured local");
            if (!w.IsCopy(type) && !captured.Contains(NameOf(s))) captured.Add(NameOf(s));
        }
        return ($"|{string.Join(", ", ps)}| -> {ret} {{\n{head}{body}{Tail(frame, sym.ReturnsVoid)}}}", captured);
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
