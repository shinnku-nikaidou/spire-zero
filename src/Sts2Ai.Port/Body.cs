using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Sts2Ai.Port;

/// <summary>Translates one member's body. Statements here; expressions in Body.Expr.cs; library calls in Body.Bcl.cs.</summary>
public sealed partial class Body
{
    private static readonly SymbolEqualityComparer Eq = SymbolEqualityComparer.Default;
    private readonly World w;
    private readonly INamedTypeSymbol cls;
    private readonly ISymbol member;
    private readonly Dictionary<ISymbol, string> names = new(Eq);
    private readonly HashSet<string> usedNames = ["c", "me"];
    private readonly HashSet<ISymbol> droppedLocals = new(Eq);
    /// <summary>Locals that stand for a list held in a field: reads and writes go to the field itself.</summary>
    private readonly Dictionary<ISymbol, string> aliases = new(Eq);
    private readonly Stack<Frame> frames = new();
    private readonly Stack<string> condInstances = new();
    private readonly Stack<string> implicitReceivers = new();
    /// <summary>What break and continue are here: every loop and switch is labelled, because Rust allows no bare break inside a labelled block.</summary>
    private readonly Stack<(string Break, string? Continue)> loops = new();
    private readonly HashSet<ISymbol> reassigned = new(Eq);
    private int tmp;

    /// <summary>Statements written, and statements left out as presentation.</summary>
    public int Written, LeftOut;
    private readonly bool isStructMember;

    private sealed class Frame
    {
        public bool Async, ReturnsTask, Iterator;
        public ITypeSymbol? Ret;
        public List<string> Outs = [];
        public List<string> Hoisted = [];
        public IMethodSymbol? Lambda;
    }

    public Body(World world, INamedTypeSymbol cls, ISymbol member)
    {
        w = world;
        this.cls = cls;
        this.member = member;
        isStructMember = w.IsValue(cls);
    }

    private string Where => $"{cls.Name}.{member.Name}";
    private string T(ITypeSymbol? t) => w.RType(t);
    private string Tmp(string stem = "t") => $"__{stem}{tmp++}";

    // ---------------------------------------------------------------- names

    private string NameOf(ISymbol s)
    {
        if (names.TryGetValue(s, out var n)) return n;
        string stem = s.Name is "_" or "" ? "_d" : World.Ident(s.Name);
        n = stem;
        for (int i = 2; !usedNames.Add(n); i++) n = stem + i;
        names[s] = n;
        return n;
    }

    public string ParamName(IParameterSymbol p) => NameOf(p);

    // ---------------------------------------------------------------- entry points

    /// <summary>The body of a method, accessor or constructor as Rust statements (without the braces).</summary>
    public string Method(IMethodSymbol m, IOperation? op, string prologue = "")
    {
        if (op != null) ScanAssignments(op);
        severalStatements = op is IMethodBodyOperation { BlockBody.Operations.Length: > 1 };
        // A parameter of a presentation type is not there in Rust: what uses it is presentation.
        foreach (var p in m.Parameters)
            if (w.IsDropped(p.Type)) droppedLocals.Add(p);
        var f = new Frame { Async = m.IsAsync, ReturnsTask = World.IsTask(m.ReturnType, out _), Ret = m.ReturnsVoid ? null : m.ReturnType };
        if (op != null) f.Iterator = HasYield(op);
        foreach (var p in m.Parameters)
            if ((p.RefKind is RefKind.Out or RefKind.Ref || w.IsChangedParam(p)) && !w.IsDropped(p.Type)) f.Outs.Add(NameOf(p));
        frames.Push(f);
        var sb = new StringBuilder();
        switch (op)
        {
            case null: break;
            case IMethodBodyOperation mb: Block(mb.BlockBody ?? mb.ExpressionBody, sb); break;
            case IConstructorBodyOperation cb:
                if (cb.Initializer != null) Stmt(cb.Initializer, sb);
                Block(cb.BlockBody ?? cb.ExpressionBody, sb);
                break;
            case IBlockOperation b: Block(b, sb); break;
            default:
                // An expression-bodied member.
                if (m.ReturnsVoid) Stmt(op, sb);
                else sb.Append(Return(op)).Append('\n');
                break;
        }
        frames.Pop();
        var head = new StringBuilder(prologue);
        foreach (var p in m.Parameters)
            if (p.RefKind == RefKind.Out && !w.IsDropped(p.Type)) head.Append($"let mut {NameOf(p)}: {T(p.Type)} = Default::default();\n");
        if (f.Iterator) head.Append($"let mut __out: {T(m.ReturnType)} = Vec::new();\n");
        foreach (var h in f.Hoisted) head.Append(h).Append('\n');
        return head.ToString() + sb + (EndsInReturn(op) && !f.Iterator ? "" : Tail(f, m.ReturnsVoid));
    }

    /// <summary>The value of a field initializer or of a static, as one expression.</summary>
    public string Initial(IOperation init)
    {
        var f = new Frame();
        frames.Push(f);
        try
        {
            string code = Expr(init);
            return f.Hoisted.Count == 0 ? code : $"{{ {string.Join(" ", f.Hoisted)} {code} }}";
        }
        finally { frames.Pop(); }
    }

    /// <summary>The call a constructor makes when it names no base constructor: the base's parameterless one.</summary>
    public string BaseCtor(INamedTypeSymbol t, INamedTypeSymbol b)
    {
        var constructed = t.BaseType!;
        var ctor = constructed.InstanceConstructors.FirstOrDefault(x => x.Parameters.Length == 0)
            ?? constructed.InstanceConstructors.FirstOrDefault(x => x.Parameters.All(p => p.HasExplicitDefaultValue))
            ?? throw new Unsupported($"{b.Name} has no parameterless constructor");
        var args = KindArgs(ctor);
        args.Add("me");
        foreach (var p in ctor.Parameters) args.Add(Literal(p.Type, p.ExplicitDefaultValue));
        args.Add("c");
        return $"{CtorPath(CallPath(ctor, false))}({string.Join(", ", args)});";
    }

    /// <summary>The path of a constructor's body function, from the path of its `new`.</summary>
    private static string CtorPath(string newPath)
    {
        int i = newPath.LastIndexOf("::", StringComparison.Ordinal) + 2;
        return newPath[..i] + "ctor" + newPath[(i + 3)..];
    }

    /// <summary>Whether a body's last statement leaves it (so nothing needs to follow).</summary>
    private static bool EndsInReturn(IOperation? op)
    {
        while (true)
        {
            switch (op)
            {
                case IMethodBodyOperation mb: op = mb.BlockBody ?? mb.ExpressionBody; continue;
                case IBlockOperation b: if (b.Operations.Length == 0) return false; op = b.Operations[^1]; continue;
                case IReturnOperation { Kind: OperationKind.Return } or IThrowOperation: return true;
                case IExpressionStatementOperation { Operation: IThrowOperation }: return true;
                case null or IConstructorBodyOperation or IExpressionStatementOperation or IVariableDeclarationGroupOperation or ILoopOperation or ISwitchOperation or ITryOperation: return false;
                case IConditionalOperation { Type: null }: return false;
                // An expression-bodied member: Method writes it as a return.
                default: return op.Parent == null || op.Parent is not IBlockOperation;
            }
        }
    }

    private string Tail(Frame f, bool returnsVoid)
    {
        if (f.Iterator) return "__out\n";
        if (f.ReturnsTask && f.Async && (f.Ret == null || !World.IsTask(f.Ret, out var r) || r == null)) return "Ok(())\n";
        if (returnsVoid || f.Ret == null) return f.Outs.Count == 0 ? "" : OutTuple(f, null) + "\n";
        return $"#[allow(unreachable_code)]\n{{ unreachable!(\"{Where}: no return\") }}\n";
    }

    private static string OutTuple(Frame f, string? value)
    {
        var parts = new List<string>();
        if (value != null) parts.Add(value);
        parts.AddRange(f.Outs.Select(o => o + ".clone()"));
        return parts.Count == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")";
    }

    private static bool HasYield(IOperation op)
    {
        foreach (var d in op.DescendantsAndSelf())
            if (d is IReturnOperation { Kind: OperationKind.YieldReturn or OperationKind.YieldBreak } && !InsideLambda(d, op)) return true;
        return false;
    }

    private static bool InsideLambda(IOperation d, IOperation root)
    {
        for (var p = d.Parent; p != null && p != root; p = p.Parent)
            if (p is IAnonymousFunctionOperation or ILocalFunctionOperation) return true;
        return false;
    }

    /// <summary>Which locals are assigned after their declaration (such a local cannot stand for a field's list).</summary>
    private void ScanAssignments(IOperation op)
    {
        foreach (var d in op.DescendantsAndSelf())
        {
            IOperation? target = d switch
            {
                ISimpleAssignmentOperation a => a.Target,
                ICompoundAssignmentOperation a => a.Target,
                ICoalesceAssignmentOperation a => a.Target,
                IIncrementOrDecrementOperation a => a.Target,
                IArgumentOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref } a => a.Value,
                _ => null,
            };
            if (target is ILocalReferenceOperation l) reassigned.Add(l.Local);
            if (target is IParameterReferenceOperation p) reassigned.Add(p.Parameter);
        }
    }

    // ---------------------------------------------------------------- statements

    private void Block(IOperation? op, StringBuilder sb)
    {
        if (op == null) return;
        if (op is IBlockOperation b) foreach (var s in b.Operations) Stmt(s, sb);
        else Stmt(op, sb);
    }

    private string Sub(IOperation? op)
    {
        var sb = new StringBuilder();
        Block(op, sb);
        return sb.ToString();
    }

    private void Leave(IOperation op)
    {
        LeftOut++;
        if (HoldsGameCall(op)) w.Rep.DroppedWithCall(Where, op);
    }

    /// <summary>Whether a statement that is left out held something that is not presentation: a call into game code, or a write to state.</summary>
    private bool HoldsGameCall(IOperation op)
    {
        foreach (var d in op.DescendantsAndSelf())
        {
            switch (d)
            {
                case IInvocationOperation inv:
                    var m = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
                    if (!World.IsSource(m.ContainingType) || w.IsDropped(m.ContainingType) || w.IsDropMember(m)) continue;
                    if (w.IsDropped(m.ReturnType) || Value(m) != null) continue;
                    // A call on presentation, or one that only takes presentation and gives nothing back, is presentation.
                    if (inv.Instance != null && (w.IsDropped(inv.Instance.Type) || inv.Instance is IMemberReferenceOperation { Member: var im } && World.IsSource(im.ContainingType) && w.IsDropMember(im))) continue;
                    if (m.ReturnsVoid && m.Parameters.Any(p => w.IsDropped(p.Type))) continue;
                    if (IsPureName(m.Name) || m.ContainingType.Name is "ModelDb") continue;
                    return true;
                case IObjectCreationOperation { Type: INamedTypeSymbol t } when World.IsSource(t) && !w.IsDropped(t) && t.TypeKind == TypeKind.Class:
                    if (t.Name.Contains("ChoiceContext") || t.Name.EndsWith("Exception")) continue;
                    return true;
                case IAssignmentOperation a when a.Target is IFieldReferenceOperation { Field: var f } && !w.IsDropped(f.Type) && !w.IsDropped(f.ContainingType):
                    return true;
                case IAssignmentOperation a when a.Target is IPropertyReferenceOperation { Property: var p } && World.IsSource(p.ContainingType) && !w.IsDropped(p.Type) && !w.IsDropped(p.ContainingType) && !w.IsDropMember(p):
                    return true;
            }
        }
        return false;
    }

    /// <summary>A presentation call whose arguments do game work (CardCmd.PreviewCardPileAdd(await CardPileCmd.Add(..))): the arguments are still run.</summary>
    private bool Salvage(IOperation op, StringBuilder sb)
    {
        if (op is IAwaitOperation aw) op = aw.Operation;
        if (op is not IInvocationOperation inv) return false;
        var def = (inv.TargetMethod.ReducedFrom ?? inv.TargetMethod).OriginalDefinition;
        if (!(w.IsDropped(def.ContainingType) || (World.IsSource(def.ContainingType) && (w.IsDropMember(def) || w.IsDropped(def.ReturnType))))) return false;
        var kept = new List<string>();
        foreach (var a in inv.Arguments)
        {
            if (!HoldsGameCall(a.Value)) continue;
            try { kept.Add(Expr(a.Value)); }
            catch (Dropped) { return false; }
        }
        if (kept.Count == 0) return false;
        foreach (var code in kept) sb.Append($"let _ = {code};\n");
        Written++;
        LeftOut++;
        return true;
    }

    private static bool IsPureName(string n) => n.StartsWith("Get") || n.StartsWith("Is") || n.StartsWith("Has") || n.StartsWith("Can") || n is "ToString" or "Contains" or "Equals";

    private void Stmt(IOperation op, StringBuilder sb)
    {
        switch (op)
        {
            case IBlockOperation b:
            {
                string inner = Sub(b);
                if (inner.Length > 0) sb.Append("{\n").Append(inner).Append("}\n");
                return;
            }
            case IEmptyOperation: return;
            case IExpressionStatementOperation es:
                try
                {
                    string code = ExprStmt(es.Operation);
                    if (code.Length > 0) { sb.Append(code).Append(";\n"); Written++; }
                }
                catch (Dropped) { if (!Salvage(es.Operation, sb)) Leave(op); }
                return;
            case IVariableDeclarationGroupOperation g:
                foreach (var decl in g.Declarations)
                    foreach (var d in decl.Declarators)
                        Declare(d, d.Initializer?.Value ?? decl.Initializer?.Value, sb, op);
                return;
            case IConditionalOperation cond: If(cond, sb); return;
            case IForEachLoopOperation fe: ForEach(fe, sb); return;
            case IForLoopOperation fl: For(fl, sb); return;
            case IWhileLoopOperation wl: While(wl, sb); return;
            case IReturnOperation r:
                switch (r.Kind)
                {
                    case OperationKind.YieldBreak: sb.Append("return __out;\n"); break;
                    case OperationKind.YieldReturn:
                        sb.Append($"__out.push({NoDrop(r.ReturnedValue!, "yields presentation")});\n");
                        break;
                    default: sb.Append(Return(r.ReturnedValue)).Append('\n'); break;
                }
                Written++;
                return;
            case IBranchOperation br:
            {
                if (loops.Count == 0) throw new Unsupported("a break outside a loop");
                var (brk, cont) = loops.Peek();
                if (br.BranchKind == BranchKind.Break) sb.Append(brk).Append('\n');
                else if (br.BranchKind == BranchKind.Continue) sb.Append(cont ?? throw new Unsupported("a continue outside a loop")).Append('\n');
                else throw new Unsupported("goto");
                Written++;
                return;
            }
            case ISwitchOperation sw: Switch(sw, sb); return;
            case IThrowOperation th: sb.Append(Throw(th)).Append(";\n"); Written++; return;
            case ITryOperation tr: Try(tr, sb); return;
            case IUsingOperation us:
            {
                // A using block: what it holds is run; disposing is presentation (timers, node guards).
                if (us.Resources is IVariableDeclarationGroupOperation ug) Stmt(ug, sb);
                else { try { Expr(us.Resources); } catch (Dropped) { } }
                Stmt(us.Body, sb);
                return;
            }
            case IUsingDeclarationOperation ud: Stmt(ud.DeclarationGroup, sb); return;
            case ILockOperation lk: Stmt(lk.Body, sb); return;
            case ILocalFunctionOperation: throw new Unsupported("local function");
            case ILabeledOperation: throw new Unsupported("label");
            default:
                // An expression in statement position (a constructor initializer arrives this way).
                try
                {
                    string code = ExprStmt(op);
                    if (code.Length > 0) { sb.Append(code).Append(";\n"); Written++; }
                }
                catch (Dropped) { Leave(op); }
                return;
        }
    }

    /// <summary>Whether the member's body is more than one statement.</summary>
    private bool severalStatements;

    private string NoDrop(IOperation op, string why)
    {
        try { return Expr(op); }
        catch (Dropped d) { throw new Unsupported($"{why} ({d.Message})"); }
    }

    private string Return(IOperation? value)
    {
        var f = frames.Peek();
        if (f.Iterator) throw new Unsupported("return in an iterator");
        if (value == null)
            return f.ReturnsTask && f.Async ? "return Ok(());" : f.Outs.Count > 0 ? $"return {OutTuple(f, null)};" : "return;";
        string v;
        try { v = Expr(value); }
        catch (Dropped d)
        {
            // A member whose one statement returns presentation is presentation as a whole. One return among several
            // is a branch nothing in a combat takes (the generator for what does not matter, of a canonical model).
            if (!severalStatements) throw new Unsupported($"returns presentation ({d.Message})");
            w.Rep.Count("a return of presentation left as a panic");
            return $"panic!(\"{Where}: {d.Message.Replace("\"", "'")}: presentation only\");";
        }
        if (f.ReturnsTask && f.Async) return $"return Ok({v});";
        return f.Outs.Count > 0 ? $"{{ let __r = {v}; return {OutTuple(f, "__r")}; }}" : $"return {v};";
    }

    private void Declare(IVariableDeclaratorOperation d, IOperation? init, StringBuilder sb, IOperation whole)
    {
        var local = d.Symbol;
        if (w.IsDropped(local.Type))
        {
            droppedLocals.Add(local);
            Leave(whole);
            return;
        }
        string type = T(local.Type);
        if (init == null)
        {
            sb.Append($"let mut {NameOf(local)}: {type} = Default::default();\n");
            return;
        }
        // A list read out of a field stays that field: C# holds a reference, and sees later changes.
        if (World.CollOf(local.Type) != World.Coll.None && !reassigned.Contains(local) && FieldPlaceOf(init) is { } place)
        {
            aliases[local] = place;
            return;
        }
        // A span over a list (the compiler's way to fill a new list) is that list.
        if (init is IInvocationOperation { TargetMethod: { Name: "AsSpan", ContainingType.Name: "CollectionsMarshal" } } span)
        {
            aliases[local] = Place(span.Arguments[0].Value);
            return;
        }
        try
        {
            string code = Expr(init);
            sb.Append($"let mut {NameOf(local)}: {type} = {code};\n");
            Written++;
        }
        catch (Dropped)
        {
            droppedLocals.Add(local);
            Leave(whole);
        }
    }

    private void If(IConditionalOperation cond, StringBuilder sb)
    {
        string test;
        try { test = Expr(cond.Condition); }
        catch (Dropped d)
        {
            // A test on presentation: the whole statement goes when nothing in it is left.
            int before = Written;
            string a = Sub(cond.WhenTrue), b = Sub(cond.WhenFalse);
            if (a.Length + b.Length > 0 || Written != before) throw new Unsupported($"a test on presentation guards game code ({d.Message})");
            Leave(cond);
            return;
        }
        // A test with a fixed value (TestMode, a null test on presentation): only the branch taken is translated.
        if (test is "true" or "false")
        {
            string taken = Sub(test == "true" ? cond.WhenTrue : cond.WhenFalse);
            if (taken.Length > 0) sb.Append("{\n").Append(taken).Append("}\n");
            return;
        }
        string then = Sub(cond.WhenTrue), other = Sub(cond.WhenFalse);
        sb.Append($"if {Unparen(test)} {{\n{then}}}");
        if (other.Length > 0) sb.Append($" else {{\n{other}}}");
        sb.Append('\n');
        Written++;
    }

    private static string Unparen(string s)
    {
        if (s.Length < 2 || s[0] != '(' || s[^1] != ')') return s;
        int depth = 0;
        for (int i = 0; i < s.Length - 1; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            if (depth == 0) return s;
        }
        return s[1..^1];
    }

    private void ForEach(IForEachLoopOperation fe, StringBuilder sb)
    {
        if (fe.IsAsynchronous) throw new Unsupported("await foreach");
        ILocalSymbol? local = fe.LoopControlVariable is IVariableDeclaratorOperation vd ? vd.Symbol : null;
        if (local == null) throw new Unsupported("foreach without a plain loop variable");
        string coll;
        try
        {
            if (w.IsDropped(local.Type)) throw new Dropped(local.Type.Name);
            coll = IterSource(fe.Collection);
        }
        catch (Dropped d)
        {
            droppedLocals.Add(local);
            int before = Written;
            string inner = Sub(fe.Body);
            if (inner.Length > 0 || Written != before) throw new Unsupported($"a loop over presentation holds game code ({d.Message})");
            Leave(fe);
            return;
        }
        string label = "'l" + tmp++;
        loops.Push(($"break {label};", $"continue {label};"));
        string body = Sub(fe.Body);
        loops.Pop();
        sb.Append($"{label}: for {NameOf(local)} in {coll} {{\nlet mut {NameOf(local)}: {T(local.Type)} = {NameOf(local)};\n{body}}}\n");
        Written++;
    }

    /// <summary>What a foreach walks: a snapshot of the collection, as a Vec.</summary>
    private string IterSource(IOperation collection)
    {
        var inner = collection;
        while (inner is IConversionOperation { IsTryCast: false } cv && (World.CollOf(cv.Operand.Type) != World.Coll.None || cv.Operand.Type?.SpecialType == SpecialType.System_String)) inner = cv.Operand;
        string code = Expr(inner);
        return World.CollOf(inner.Type) switch
        {
            World.Coll.Vec => code,
            World.Coll.None => throw new Unsupported($"foreach over {inner.Type?.Name}"),
            _ => $"{code}.into_vec()",
        };
    }

    private void For(IForLoopOperation fl, StringBuilder sb)
    {
        var head = new StringBuilder();
        foreach (var b in fl.Before) Stmt(b, head);
        string test = fl.Condition == null ? "true" : NoDrop(fl.Condition, "a loop test on presentation");
        string label = "'l" + tmp++, inner = "'k" + tmp++;
        loops.Push(($"break {label};", $"break {inner};"));
        string body = Sub(fl.Body);
        loops.Pop();
        var bottom = new StringBuilder();
        foreach (var b in fl.AtLoopBottom) Stmt(b, bottom);
        sb.Append($"{{\n{head}{label}: while {test} {{\n{inner}: {{\n{body}}}\n{bottom}}}\n}}\n");
        Written++;
    }

    private void While(IWhileLoopOperation wl, StringBuilder sb)
    {
        string test;
        try { test = wl.Condition == null ? "true" : Expr(wl.Condition); }
        catch (Dropped d)
        {
            int before = Written;
            string inner = Sub(wl.Body);
            if (inner.Length > 0 || Written != before) throw new Unsupported($"a loop test on presentation guards game code ({d.Message})");
            Leave(wl);
            return;
        }
        if (test == "false" && wl.ConditionIsTop) return;
        string label = "'l" + tmp++;
        if (wl.ConditionIsTop)
        {
            loops.Push(($"break {label};", $"continue {label};"));
            string body = Sub(wl.Body);
            loops.Pop();
            sb.Append($"{label}: while {test} {{\n{body}}}\n");
        }
        else
        {
            string inner = "'k" + tmp++;
            loops.Push(($"break {label};", $"break {inner};"));
            string body = Sub(wl.Body);
            loops.Pop();
            sb.Append($"{label}: loop {{\n{inner}: {{\n{body}}}\nif !({test}) {{ break {label}; }}\n}}\n");
        }
        Written++;
    }

    private void Switch(ISwitchOperation sw, StringBuilder sb)
    {
        string subject = Tmp("s");
        string label = "'s" + tmp++;
        string value = NoDrop(sw.Value, "a switch on presentation");
        var arms = new StringBuilder();
        string? fallback = null;
        loops.Push(($"break {label};", loops.Count > 0 ? loops.Peek().Continue : null));
        bool first = true;
        foreach (var section in sw.Cases)
        {
            var tests = new List<string>();
            bool isDefault = false;
            foreach (var clause in section.Clauses)
            {
                switch (clause)
                {
                    case IDefaultCaseClauseOperation: isDefault = true; break;
                    case ISingleValueCaseClauseOperation sv: tests.Add(ConstantTest(subject, sw.Value.Type!, sv.Value)); break;
                    case IPatternCaseClauseOperation pc:
                        string t = Pattern(pc.Pattern, subject, sw.Value.Type!);
                        if (pc.Guard != null) t = $"({t} && {Expr(pc.Guard)})";
                        tests.Add(t);
                        break;
                    case IRelationalCaseClauseOperation rc: tests.Add($"({subject} {RelOp(rc.Relation)} {Expr(rc.Value)})"); break;
                    default: throw new Unsupported($"case clause {clause.CaseKind}");
                }
            }
            var body = new StringBuilder();
            foreach (var s in section.Body) Stmt(s, body);
            if (isDefault) { fallback = body.ToString(); if (tests.Count == 0) continue; }
            arms.Append(first ? "" : " else ").Append($"if {string.Join(" || ", tests)} {{\n{body}}}");
            first = false;
        }
        loops.Pop();
        if (fallback != null) arms.Append(first ? $"{{\n{fallback}}}" : $" else {{\n{fallback}}}");
        sb.Append($"{label}: {{\nlet {subject} = {value};\n{arms}\n}}\n");
        Written++;
    }

    private void Try(ITryOperation tr, StringBuilder sb)
    {
        // The engine has no exceptions to catch: a panic ends the action. A finally block runs after the body.
        if (tr.Catches.Length > 0 && tr.Catches.Any(c => Sub(c.Handler).Length > 0 && !IsRethrowOnly(c)))
            w.Rep.Note("catch block left out", Where);
        bool returnsInside = tr.Body.Descendants().Any(d => d is IReturnOperation && !InsideLambda(d, tr.Body));
        string fin = tr.Finally == null ? "" : Sub(tr.Finally);
        if (fin.Length > 0 && returnsInside) throw new Unsupported("return inside try with a finally that does something");
        Block(tr.Body, sb);
        sb.Append(fin);
    }

    private static bool IsRethrowOnly(ICatchClauseOperation c) => c.Handler.Operations.All(o => o is IThrowOperation or IExpressionStatementOperation);

    private string Throw(IThrowOperation th)
    {
        string what = th.Exception switch
        {
            null => "rethrow",
            IConversionOperation { Operand: IObjectCreationOperation oc } => ExceptionText(oc),
            IObjectCreationOperation oc => ExceptionText(oc),
            _ => th.Exception.Type?.Name ?? "exception",
        };
        return $"panic!(\"{Where}: {{}}\", {Lit(what)})";
    }

    private static string ExceptionText(IObjectCreationOperation oc)
    {
        string name = oc.Type?.Name ?? "Exception";
        var msg = oc.Arguments.Select(a => a.Value.ConstantValue).FirstOrDefault(v => v is { HasValue: true, Value: string });
        return msg.HasValue ? $"{name}: {msg.Value}" : name;
    }

    private static string Lit(string s) => "\"" + Escape(s) + "\"";

    public static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (ch < 0x20 || char.IsSurrogate(ch)) sb.Append($"\\u{{{(int)ch:x}}}");
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string RelOp(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=",
        BinaryOperatorKind.GreaterThan => ">",
        BinaryOperatorKind.GreaterThanOrEqual => ">=",
        BinaryOperatorKind.Equals => "==",
        BinaryOperatorKind.NotEquals => "!=",
        _ => throw new Unsupported($"relation {k}"),
    };
}
