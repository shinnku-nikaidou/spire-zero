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
            if (w.IsDropped(p)) droppedLocals.Add(p);
        var f = new Frame { Async = m.IsAsync, ReturnsTask = World.IsTask(m.ReturnType, out _), Ret = m.ReturnsVoid ? null : m.ReturnType };
        if (op != null) f.Iterator = HasYield(op);
        if (op != null && w.Lambdas != null) BindLambdas(op, w.Lambdas.Of(m));
        if (f.Iterator && !w.Yields(m)) throw new Unsupported("yield in what is not a sequence of values");
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
        if (f.Iterator) return "Ok(false)\n";
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
        if (op is IBlockOperation { Operations: var ops } lb && ops.Any(o => o is ILabeledOperation)) Labeled(lb, sb);
        else if (op is IBlockOperation b)
        {
            foreach (var s in b.Operations)
            {
                dead = false;
                Stmt(s, sb);
                // What follows a test that is always true and always leaves (`if (TestMode.IsOn) return;`) never runs.
                if (dead) break;
            }
            dead = false;
        }
        else Stmt(op, sb);
    }

    /// <summary>Set by a statement after which nothing of its block runs.</summary>
    private bool dead;

    /// <summary>Where each label of a block with labels is: the loop of its block, the variable that says which part
    /// runs next, and the number of the part the label starts.</summary>
    private readonly Dictionary<ISymbol, (string Loop, string Pc, int Part, int Walks)> labels = new(Eq);

    /// <summary>How many loops over a sequence the code being written is inside of (each is a closure of its own).</summary>
    private int walks;

    /// <summary>The locals of blocks with labels: declared before the block's loop, so every part sees them.</summary>
    private readonly HashSet<ISymbol> declaredAhead = new(Eq);

    /// <summary>A block with labels (the decompiler's gotos): its statements are cut into parts at the labels, and a
    /// loop runs the parts, one after the other or the one a goto names.</summary>
    private void Labeled(IBlockOperation b, StringBuilder sb)
    {
        string loop = "'g" + tmp++, pc = Tmp("pc");
        var parts = new List<List<IOperation>> { new() };
        foreach (var s in b.Operations)
        {
            if (s is ILabeledOperation lab)
            {
                parts.Add([]);
                labels[lab.Label] = (loop, pc, parts.Count - 1, walks);
                if (lab.Operation != null) parts[^1].Add(lab.Operation);
            }
            else parts[^1].Add(s);
        }
        foreach (var s in parts.SelectMany(p => p).OfType<IVariableDeclarationGroupOperation>())
            foreach (var d in s.Declarations.SelectMany(x => x.Declarators))
            {
                if (w.IsDropped(d.Symbol.Type)) continue;
                declaredAhead.Add(d.Symbol);
                sb.Append($"let mut {NameOf(d.Symbol)}: {T(d.Symbol.Type)} = {(World.IsTask(d.Symbol.Type, out _) ? "Ok(Default::default())" : "Default::default()")};\n");
            }
        sb.Append($"let mut {pc}: i32 = 0;\n{loop}: loop {{\nmatch {pc} {{\n");
        bool endReached = false;
        for (int i = 0; i < parts.Count; i++)
        {
            var body = new StringBuilder();
            // (A part goes on after a test that always leaves: a goto may still reach what follows.)
            foreach (var s in parts[i]) { Stmt(s, body); dead = false; }
            bool leaves = parts[i].LastOrDefault() is IReturnOperation or IThrowOperation or IBranchOperation { BranchKind: BranchKind.GoTo };
            if (!leaves && i == parts.Count - 1) endReached = true;
            sb.Append($"{i} => {{\n{body}{(leaves ? "" : $"{pc} = {i + 1};\n")}}}\n");
        }
        sb.Append(endReached ? $"_ => break {loop},\n" : "_ => unreachable!(),\n").Append("}\n}\n");
        Written++;
    }

    private string Sub(IOperation? op)
    {
        var sb = new StringBuilder();
        Block(op, sb);
        dead = false;
        return sb.ToString();
    }

    private void Leave(IOperation op)
    {
        LeftOut++;
        if (HoldsGameCall(op)) w.Rep.DroppedWithCall(Where, op);
    }

    /// <summary>Whether a statement that is left out held something that is not presentation: a call into game code, or a write to state.</summary>
    /// <summary>Whether a call of a delegate is the raising of an event (`Changed?.Invoke(x)`, `Changed(x)`).</summary>
    private static bool RaisesEvent(IInvocationOperation inv)
    {
        if (inv.Instance is IEventReferenceOperation) return true;
        if (inv.Instance is not IConditionalAccessInstanceOperation) return false;
        for (var p = inv.Parent; p != null; p = p.Parent)
            if (p is IConditionalAccessOperation ca) return ca.Operation is IEventReferenceOperation;
        return false;
    }

    private bool HoldsGameCall(IOperation op)
    {
        foreach (var d in op.DescendantsAndSelf())
        {
            switch (d)
            {
                case IInvocationOperation inv:
                    var m = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
                    // A function that was handed over is called: what it does is not known here, so it is game code.
                    // (An event that is raised is presentation: what listens to it is the screen.)
                    if (m.MethodKind == MethodKind.DelegateInvoke && inv.Instance is { Type: { } handed } && !w.IsDropped(handed) && !RaisesEvent(inv)
                        && !(inv.Instance is ILocalReferenceOperation { Local: var dl } && droppedLocals.Contains(dl))) return true;
                    if (!World.IsSource(m.ContainingType) || w.IsDropped(m.ContainingType) || w.IsDropMember(m)) continue;
                    if (w.IsDropped(m.ReturnType) || Value(m) != null) continue;
                    // A call on presentation, or one that only takes presentation and gives nothing back, is presentation.
                    if (inv.Instance != null && (w.IsDropped(inv.Instance.Type) || inv.Instance is IMemberReferenceOperation { Member: var im } && World.IsSource(im.ContainingType) && w.IsDropMember(im))) continue;
                    if ((m.ReturnsVoid || (World.IsTask(m.ReturnType, out var gives) && gives == null)) && m.Parameters.Length > 0 && m.Parameters.All(p => w.IsDropped(p.Type))) continue;
                    if (IsPureName(m.Name) || m.ContainingType.Name is "ModelDb") continue;
                    // A task run on the side is what the task is: its own calls decide.
                    if (m is { Name: "RunSafely", ContainingType.Name: "TaskHelper" }) continue;
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
                catch (Dropped)
                {
                    // A local that presentation was written to is presentation from here on: its value is not kept.
                    IOperation? target = es.Operation switch { IAssignmentOperation a => a.Target, IIncrementOrDecrementOperation i => i.Target, _ => null };
                    if (target is ILocalReferenceOperation { Local: var tainted }) droppedLocals.Add(tainted);
                    // What a statement that is left out declares is not there.
                    foreach (var declared in es.Operation.DescendantsAndSelf().OfType<ILocalReferenceOperation>().Where(l => l.IsDeclaration)) droppedLocals.Add(declared.Local);
                    if (!Salvage(es.Operation, sb)) Leave(op);
                }
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
                    // A sequence hands each value to the code that walks it, which says whether to go on (see World.Yields).
                    case OperationKind.YieldBreak:
                        if (!frames.Peek().Iterator) throw new Unsupported("yield in a local function");
                        sb.Append(LeaveWith("Ok(false)")).Append('\n');
                        break;
                    case OperationKind.YieldReturn:
                        if (!frames.Peek().Iterator) throw new Unsupported("yield in a local function");
                        sb.Append($"if __k({NoDrop(r.ReturnedValue!, "yields presentation")}, c)? {{\n{LeaveWith("Ok(true)")}\n}}\n");
                        break;
                    default: sb.Append(Return(r.ReturnedValue)).Append('\n'); break;
                }
                Written++;
                return;
            case IBranchOperation { BranchKind: BranchKind.GoTo } go:
            {
                if (!labels.TryGetValue(go.Target, out var to)) throw new Unsupported("a goto to a label inside another statement");
                if (to.Walks != walks) throw new Unsupported("a goto out of a loop over a sequence");
                sb.Append($"{to.Pc} = {to.Part};\ncontinue {to.Loop};\n");
                Written++;
                return;
            }
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
            // A local function is written where it is used: handed over as a delegate.
            case ILocalFunctionOperation: return;
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

    /// <summary>What a return has to get out of first: a try block with a finally (a labeled block), or the body of a
    /// loop over a sequence (a closure, which keeps the value in a variable outside it).</summary>
    private sealed class Scope
    {
        public string Label = "";
        public int Frame;
        public bool Walk, Used;
    }

    /// <summary>The scopes the code being written is inside of, innermost last.</summary>
    private readonly Stack<Scope> scopes = new();

    /// <summary>The scope around the code being written, in the same member or lambda.</summary>
    private Scope? Around => scopes.Count > 0 && scopes.Peek().Frame == frames.Count ? scopes.Peek() : null;

    /// <summary>The statement that leaves with what the member returns: out of the member, or out of the scope around
    /// it, after which the value goes on outwards.</summary>
    private string LeaveWith(string value)
    {
        if (Around is not { } scope) return $"return {value};";
        scope.Used = true;
        return scope.Walk ? $"{{ {scope.Label} = Some({value}); return Ok(true); }}" : $"break {scope.Label} Some({value});";
    }

    private string Return(IOperation? value)
    {
        var f = frames.Peek();
        if (f.Iterator) throw new Unsupported("return in an iterator");
        if (value == null)
        {
            if (Around != null) return LeaveWith(f.ReturnsTask && f.Async ? "Ok(())" : f.Outs.Count > 0 ? OutTuple(f, null) : "()");
            return f.ReturnsTask && f.Async ? "return Ok(());" : f.Outs.Count > 0 ? $"return {OutTuple(f, null)};" : "return;";
        }
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
        if (f.ReturnsTask && f.Async) return LeaveWith($"Ok({v})");
        return f.Outs.Count > 0 ? $"{{ let __r = {v}; {LeaveWith(OutTuple(f, "__r"))} }}" : LeaveWith(v);
    }

    private void Declare(IVariableDeclaratorOperation d, IOperation? init, StringBuilder sb, IOperation whole)
    {
        var local = d.Symbol;
        if (IsDeferred(init) && local.Type is INamedTypeSymbol { Name: "IEnumerable" }) deferredLocals.Add(local);
        if (w.IsDropped(local.Type))
        {
            droppedLocals.Add(local);
            Leave(whole);
            return;
        }
        // An enumerator taken by hand (the compiler's foreach with a way out): the list's iterator, and the item it is at.
        if (World.MetaName(local.Type) == "System.Collections.Generic.IEnumerator`1" && init is IInvocationOperation { TargetMethod.Name: "GetEnumerator", Instance: { } over } && World.CollOf(over.Type) != World.Coll.None)
        {
            string item = T(((INamedTypeSymbol)local.Type).TypeArguments[0]);
            enumerators.Add(local);
            sb.Append($"let mut {NameOf(local)}_it = {Expr(over)}.into_vec().into_iter();\nlet mut {NameOf(local)}_at: Option<{item}> = None;\n");
            Written++;
            return;
        }
        // A text only presentation reads (the words of a log line) goes with it.
        if (local.Type.SpecialType == SpecialType.System_String && OnlyPresentationReads(local, whole))
        {
            droppedLocals.Add(local);
            Leave(whole);
            return;
        }
        string type = T(local.Type);
        // In a block with labels the local is declared before the block's loop.
        string let = declaredAhead.Contains(local) ? NameOf(local) : $"let mut {NameOf(local)}: {type}";
        if (init == null)
        {
            if (!declaredAhead.Contains(local)) sb.Append($"{let} = Default::default();\n");
            return;
        }
        // A list read out of a field stays that field: C# holds a reference, and sees later changes.
        if (World.CollOf(local.Type) != World.Coll.None && !reassigned.Contains(local) && FieldPlaceOf(init) is { } place)
        {
            aliases[local] = place;
            if (Presence(init) is { } there) aliasPresence[local] = there;
            return;
        }
        // One of two lists of the state, picked by a test (`side == Player ? _allies : _enemies`): the local is the
        // one picked, and the test is made once, here.
        if (World.CollOf(local.Type) != World.Coll.None && !reassigned.Contains(local) && init is IConditionalOperation { WhenFalse: { } otherwise } pick
            && FieldPlaceOf(pick.WhenTrue) is { } first && FieldPlaceOf(otherwise) is { } second)
        {
            string which = Tmp("which");
            sb.Append($"let {which}: bool = {Expr(pick.Condition)};\n");
            aliases[local] = $"(*(if {which} {{ &{first} }} else {{ &{second} }}))";
            Written++;
            return;
        }
        // A list taken from another local is that list: C# holds one reference to it under two names.
        {
            var from = init;
            while (from is IConversionOperation { IsTryCast: false } cv && World.CollOf(cv.Operand.Type) != World.Coll.None) from = cv.Operand;
            ISymbol? other = from switch { ILocalReferenceOperation l => l.Local, IParameterReferenceOperation p => p.Parameter, _ => null };
            if (other != null && World.CollOf(local.Type) != World.Coll.None && World.CollOf(from.Type) != World.Coll.None && !reassigned.Contains(local) && !reassigned.Contains(other) && !droppedLocals.Contains(other)
                && N(local.Type) == N(from.Type))
            {
                aliases[local] = aliases.TryGetValue(other, out var known) ? known : NameOf(other);
                if (aliasPresence.TryGetValue(other, out var there2)) aliasPresence[local] = there2;
                return;
            }
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
            sb.Append($"{let} = {code};\n");
            Written++;
        }
        catch (Dropped)
        {
            droppedLocals.Add(local);
            Leave(whole);
        }
    }

    /// <summary>Whether nothing but presentation follows the statement in its round of the loop around it (or in the
    /// member, with no loop around it).</summary>
    private bool NothingAfter(IOperation statement)
    {
        for (IOperation at = statement; at.Parent != null; at = at.Parent)
        {
            switch (at.Parent)
            {
                case IBlockOperation block:
                    foreach (var later in block.Operations.SkipWhile(o => o != at).Skip(1))
                        if (HoldsGameCall(later) || WritesOuterLocal(later) || later is IReturnOperation { ReturnedValue: not null }) return false;
                    break;
                case IConditionalOperation: break;
                case ILoopOperation or IAnonymousFunctionOperation or IMethodBodyOperation: return true;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>Whether the code writes a local declared outside of it.</summary>
    private bool WritesOuterLocal(IOperation code)
    {
        var own = new HashSet<ISymbol>(code.Descendants().OfType<IVariableDeclaratorOperation>().Select(d => (ISymbol)d.Symbol), Eq);
        foreach (var d in code.Descendants())
        {
            IOperation? target = d switch { IAssignmentOperation a => a.Target, IIncrementOrDecrementOperation i => i.Target, _ => null };
            if (target is ILocalReferenceOperation l && !own.Contains(l.Local) && !w.IsDropped(l.Local.Type)) return true;
            if (target is IParameterReferenceOperation) return true;
        }
        return false;
    }

    /// <summary>The locals that are enumerators taken by hand.</summary>
    private readonly HashSet<ISymbol> enumerators = new(Eq);

    /// <summary>Whether every use of the local is inside the arguments of a presentation call.</summary>
    private bool OnlyPresentationReads(ILocalSymbol local, IOperation declaration)
    {
        IOperation root = declaration;
        while (root.Parent != null) root = root.Parent;
        foreach (var use in root.Descendants().OfType<ILocalReferenceOperation>().Where(r => Eq.Equals(r.Local, local)))
        {
            bool inPresentation = false;
            for (IOperation? at = use.Parent; at != null && !inPresentation; at = at.Parent)
            {
                if (at is IInvocationOperation inv && w.IsDropped((inv.TargetMethod.ReducedFrom ?? inv.TargetMethod).OriginalDefinition.ContainingType)) inPresentation = true;
                else if (at is IExpressionStatementOperation or IReturnOperation or IVariableDeclaratorOperation or IAnonymousFunctionOperation) break;
            }
            if (!inPresentation) return false;
        }
        return true;
    }

    private void If(IConditionalOperation cond, StringBuilder sb)
    {
        string test;
        try { test = Expr(cond.Condition); }
        catch (Dropped d)
        {
            // A test on presentation: the whole statement goes when nothing in it is left.
            if (!HoldsGameCall(cond) && !WritesOuterLocal(cond)) { Leave(cond); return; }
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
            // The branch never taken, when it is presentation, is a statement left out: a member with nothing else is presentation.
            else if ((test == "true" ? cond.WhenFalse : cond.WhenTrue) is { } never && !HoldsGameCall(never) && !WritesOuterLocal(never)) LeftOut++;
            dead = test == "true" && EndsInReturn(cond.WhenTrue);
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
        if (fe.LoopControlVariable is IDeclarationExpressionOperation { Expression: ITupleOperation parts }) { ForEachTuple(fe, parts, sb); return; }
        ILocalSymbol? local = fe.LoopControlVariable is IVariableDeclaratorOperation vd ? vd.Symbol : null;
        if (local == null) throw new Unsupported("foreach without a plain loop variable");
        if (!w.IsDropped(local.Type) && WalkCall(fe.Collection) is { } walk) { ForEachWalk(fe, local, walk, sb); return; }
        string coll;
        try
        {
            if (w.IsDropped(local.Type)) throw new Dropped(local.Type.Name);
            coll = IterSource(fe.Collection);
            if (IsDeferred(fe.Collection) && HoldsGameCall(fe.Body)) w.Rep.Deferred(Where, fe);
        }
        catch (Dropped d)
        {
            droppedLocals.Add(local);
            // Nothing in the loop but presentation and the ways out of a round of it.
            if (!HoldsGameCall(fe.Body) && !WritesOuterLocal(fe.Body)) { Leave(fe); return; }
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

    /// <summary>Locals that hold a LINQ chain not yet evaluated in C# (`IEnumerable<T> xs = list.Where(f);`).</summary>
    private readonly HashSet<ISymbol> deferredLocals = new(Eq);

    /// <summary>Whether C# evaluates the sequence only as it is walked: a LINQ chain that ends in an operator that hands
    /// its values on one by one and runs a function for each, or a local that holds one.</summary>
    private bool IsDeferred(IOperation? op)
    {
        while (op is IConversionOperation { IsTryCast: false } cv) op = cv.Operand;
        if (op is ILocalReferenceOperation l) return deferredLocals.Contains(l.Local);
        return op is IInvocationOperation { TargetMethod: { ContainingType: { Name: "Enumerable", ContainingNamespace.Name: "Linq" }, Name: "Where" or "Select" or "SelectMany" or "TakeWhile" or "SkipWhile" } };
    }

    /// <summary>foreach over a call of a sequence written with yield. In C# the sequence's code and the loop's body
    /// take turns; here the loop's body is handed to the sequence, which runs it for each value as it reaches it. The
    /// body answers whether the walk ends (a break, or a return whose value waits in a variable outside).</summary>
    private void ForEachWalk(IForEachLoopOperation fe, ILocalSymbol local, IInvocationOperation call, StringBuilder sb)
    {
        var f = frames.Peek();
        var scope = new Scope { Label = Tmp("w"), Frame = frames.Count, Walk = true };
        scopes.Push(scope);
        loops.Push(("return Ok(true);", "return Ok(false);"));
        walks++;
        string body;
        try { body = Sub(fe.Body); }
        finally
        {
            walks--;
            loops.Pop();
            scopes.Pop();
        }
        string name = NameOf(local);
        string item = T(World.WalkItem((call.TargetMethod.ReducedFrom ?? call.TargetMethod))!);
        string each = $"&mut |{name}: {item}, c: &mut Combat| -> R<bool> {{\nlet mut {name}: {T(local.Type)} = {name};\n{body}Ok(false)\n}}";
        string walked = Invoke(call, each);
        if (scope.Used) sb.Append($"let mut {scope.Label} = None;\n");
        // A stop (a card prompt) inside the body leaves through the sequence; where nothing is awaited there is none.
        sb.Append(f.Async || f.Iterator ? $"{walked}?;\n" : $"rt::walked({walked});\n");
        if (scope.Used) sb.Append($"if let Some(__v) = {scope.Label} {{\n{LeaveWith("__v")}\n}}\n");
        w.Rep.Count("foreach loops run inside the sequence they walk");
        Written++;
    }

    /// <summary>foreach (var (a, b) in pairs): the items are taken apart in the loop's pattern.</summary>
    private void ForEachTuple(IForEachLoopOperation fe, ITupleOperation parts, StringBuilder sb)
    {
        var binds = new StringBuilder();
        string Pattern(ITupleOperation t) => "(" + string.Join(", ", t.Elements.Select(e => e switch
        {
            ITupleOperation inner => Pattern(inner),
            IDiscardOperation => "_",
            ILocalReferenceOperation { Local: var l } when w.IsDropped(l.Type) => Skip(l),
            ILocalReferenceOperation { Local: var l } => Bind(l),
            _ => throw new Unsupported("foreach without a plain loop variable"),
        })) + ")";
        string Skip(ILocalSymbol l) { droppedLocals.Add(l); return "_"; }
        string Bind(ILocalSymbol l) { binds.Append($"let mut {NameOf(l)}: {T(l.Type)} = {NameOf(l)};\n"); return NameOf(l); }
        string pattern = Pattern(parts);
        // A dictionary's items are pairs.
        var walked = fe.Collection;
        while (walked is IConversionOperation { IsTryCast: false } cv) walked = cv.Operand;
        if (World.CollOf(walked.Type) == World.Coll.Dict && parts.Elements.Length == 2)
        {
            string[] halves = pattern[1..^1].Split(", ");
            if (halves.Length != 2) throw new Unsupported("foreach over a dictionary with a nested pattern");
            pattern = $"KeyValuePair {{ key: {halves[0]}, value: {halves[1]} }}";
        }
        string coll = IterSource(fe.Collection);
        string label = "'l" + tmp++;
        loops.Push(($"break {label};", $"continue {label};"));
        string body = Sub(fe.Body);
        loops.Pop();
        sb.Append($"{label}: for {pattern} in {coll} {{\n{binds}{body}}}\n");
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
        string test;
        try { test = fl.Condition == null ? "true" : Expr(fl.Condition); }
        catch (Dropped d)
        {
            if (!HoldsGameCall(fl.Body) && !WritesOuterLocal(fl.Body)) { Leave(fl); return; }
            throw new Unsupported($"a loop test on presentation ({d.Message})");
        }
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
        string value;
        try { value = Expr(sw.Value); }
        catch (Dropped d)
        {
            // A switch on presentation with nothing but presentation in it, and nothing after it that a way out of
            // it (continue, return) could skip.
            if (!HoldsGameCall(sw) && !WritesOuterLocal(sw) && NothingAfter(sw)) { Leave(sw); return; }
            throw new Unsupported($"a switch on presentation ({d.Message})");
        }
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
        if (tr.Catches.Length > 0 && tr.Catches.Any(c => !IsRethrowOnly(c) && HandlerDoesSomething(c)))
            w.Rep.Note("catch block left out", Where);
        bool returnsInside = tr.Body.Descendants().Any(d => d is IReturnOperation && !InsideLambda(d, tr.Body));
        string fin = tr.Finally == null ? "" : Sub(tr.Finally);
        if (fin.Length > 0 && returnsInside)
        {
            // A return inside leaves the body with its value, the finally block runs, then the member returns it.
            // (A stop for a card prompt leaves at once: the copy it ran on is thrown away.)
            string label = "'t" + tmp++;
            scopes.Push(new Scope { Label = label, Frame = frames.Count });
            var inner = new StringBuilder();
            Block(tr.Body, inner);
            scopes.Pop();
            sb.Append($"let __left = {label}: {{\n{inner}None\n}};\n{fin}if let Some(__v) = __left {{\n{LeaveWith("__v")}\n}}\n");
            Written++;
            return;
        }
        Block(tr.Body, sb);
        sb.Append(fin);
    }

    /// <summary>Whether a catch block holds anything but presentation. It is never written: only noted.</summary>
    private bool HandlerDoesSomething(ICatchClauseOperation c)
    {
        int written = Written, leftOut = LeftOut;
        try { return Sub(c.Handler).Length > 0; }
        catch (Exception e) when (e is Unsupported or Dropped) { return true; }
        finally { Written = written; LeftOut = leftOut; }
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
