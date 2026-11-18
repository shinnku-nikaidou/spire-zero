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
        if (op != null) nullLocals = World.NullLocals(op, w);
        bodyOp = op;
        nullReturn = w.ReturnsNullColl(m);
        severalStatements = op is IMethodBodyOperation { BlockBody.Operations.Length: > 1 };
        // A parameter of a presentation type is not there in Rust: what uses it is presentation.
        foreach (var p in m.Parameters)
            if (w.IsDropped(p)) droppedLocals.Add(p);
        var f = new Frame { Async = m.IsAsync, ReturnsTask = World.IsTask(m.ReturnType, out _), Ret = m.ReturnsVoid ? null : m.ReturnType };
        if (op != null) f.Iterator = HasYield(op);
        // A member whose body is a LINQ chain C# has not evaluated: its `__each` form walks the chain (ReturnWalk).
        if (op != null && !f.Iterator && w.Yields(m)) f.Iterator = lazyMember = true;
        memberRoot = op;
        if (op != null && w.Lambdas != null) BindLambdas(op, w.Lambdas.Of(m));
        if (f.Iterator && !w.Yields(m)) throw new Unsupported("yield in what is not a sequence of values");
        foreach (var p in m.Parameters)
            if ((p.RefKind is RefKind.Out or RefKind.Ref || w.IsChangedParam(p)) && !w.IsDropped(p.Type)) f.Outs.Add(NameOf(p));
        frames.Push(f);
        var sb = new StringBuilder();
        switch (op)
        {
            case null: break;
            case IMethodBodyOperation { BlockBody: { } block } when f.Iterator && w.Cfg.Memo.IndexOf($"{cls.Name}.{m.Name}") is var slot and >= 0:
                Kept(slot, m, block, sb);
                break;
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
        // A walk takes the kinds its caller's code does something for first thing (rt::wanted), so that no walk
        // below sees them.
        if (f.Iterator) head.Append("let __only = c.only.take();\n");
        foreach (var p in m.Parameters)
            if (p.RefKind == RefKind.Out && !w.IsDropped(p.Type)) head.Append($"let mut {NameOf(p)}: {T(p.Type)} = Default::default();\n");
        foreach (var h in f.Hoisted) head.Append(h).Append('\n');
        string whole = head.ToString() + sb + (EndsInReturn(op) && !f.Iterator ? "" : Tail(f, m.ReturnsVoid));
        // A kept getter (port.toml [memo]): its value for this object while nothing it reads is written.
        if (m.MethodKind == MethodKind.PropertyGet && m.AssociatedSymbol is IPropertySymbol kp && w.Cfg.MemoGetters.IndexOf($"{cls.Name}.{kp.Name}") is var g and >= 0)
        {
            string type = T(m.ReturnType), arena = World.Ident(w.Unique(w.Root(cls)));
            whole = $"if let Some(__v) = rt::memo::value(c, {g}, me) {{\nreturn __v;\n}}\nlet __stamp = rt::memo::writes(c);\n"
                + $"let __v: {type} = (|c: &mut Combat| -> {type} {{\n{whole}}})(c);\nrt::memo::keep_value(c, {g}, me, __stamp, __v, c.a.{arena}.len());\n__v\n";
            w.Rep.Count("kept getters");
        }
        return whole;
    }

    /// <summary>A kept walk (port.toml [memo]): the statements that build a list, then `foreach (var x in list) if (test)
    /// yield return x;`, then the rest. The list is built only when the combat has been written to since it was last
    /// built (rt/memo.rs), and each test's answer is kept while nothing is written; the rest is as written.</summary>
    private void Kept(int slot, IMethodSymbol m, IBlockOperation block, StringBuilder sb)
    {
        var ops = block.Operations;
        static IOperation Bare(IOperation o) => o is IConversionOperation { IsImplicit: true } cv ? Bare(cv.Operand) : o;
        int at = ops.ToList().FindIndex(o => o is IForEachLoopOperation { Collection: var coll } && Bare(coll) is ILocalReferenceOperation);
        if (at < 0) throw new Unsupported("a kept walk without a loop over a local list");
        var fe = (IForEachLoopOperation)ops[at];
        var list = ((ILocalReferenceOperation)Bare(fe.Collection)).Local;
        var item = (fe.LoopControlVariable as IVariableDeclaratorOperation)?.Symbol ?? throw new Unsupported("a kept walk's loop has no plain variable");
        var inner = fe.Body is IBlockOperation { Operations.Length: 1 } one ? one.Operations[0] : fe.Body;
        if (inner is not IConditionalOperation { WhenFalse: null } test
            || !(test.WhenTrue is IReturnOperation { Kind: OperationKind.YieldReturn } y0 ? Bare(y0.ReturnedValue!) is ILocalReferenceOperation { Local: var y } && Eq.Equals(y, item)
                : test.WhenTrue is IBlockOperation { Operations: [IReturnOperation { Kind: OperationKind.YieldReturn } y1] } && Bare(y1.ReturnedValue!) is ILocalReferenceOperation { Local: var z } && Eq.Equals(z, item)))
            throw new Unsupported("a kept walk's loop is not `if (test) yield return item;`");
        if (ops.Take(at).Any(o => o.DescendantsAndSelf().OfType<IReturnOperation>().Any(r => !InsideLambda(r, o))))
            throw new Unsupported("a kept walk's list is built by statements that return or yield");
        // The key: the object the walk is of and its arguments, all objects.
        var keys = new List<string> { "me" };
        foreach (var p in m.Parameters)
        {
            if (p.Type is not INamedTypeSymbol pt || !w.Kinds.ContainsKey(pt.OriginalDefinition)) throw new Unsupported($"a kept walk's parameter {p.Name} is not an object");
            keys.Add(NameOf(p));
        }
        if (keys.Count > 2) throw new Unsupported("a kept walk with more than one argument");
        while (keys.Count < 2) keys.Add("Obj::NULL");
        string key = $"[{string.Join(", ", keys)}]";
        var build = new StringBuilder();
        foreach (var o in ops.Take(at)) Stmt(o, build);
        int hoisted = frames.Peek().Hoisted.Count;
        string passes = Expr(test.Condition);
        if (frames.Peek().Hoisted.Count != hoisted) throw new Unsupported("a kept walk's test needs statements of its own");
        string label = "'l" + tmp++;
        loops.Push(($"break {label};", $"continue {label};"));
        string hand = Sub(test.WhenTrue);
        loops.Pop();
        string name = NameOf(item);
        sb.Append($"let __kept = match rt::memo::kept(c, {slot}, {key}) {{\nSome(__kept) => __kept,\nNone => {{\nlet __stamp = rt::memo::writes(c);\n{build}rt::memo::keep(c, {slot}, {key}, __stamp, {NameOf(list)})\n}}\n}};\n");
        // An item of a kind the caller's code does nothing for is passed over before its test is asked: the walk
        // goes through the positions of the kinds it was asked for, found once for the list.
        sb.Append($"let __picked = rt::memo::picked(&__kept, __only, c);\n");
        sb.Append($"{label}: for __n in 0..rt::memo::count(&__kept, __picked) {{\nlet __at = rt::memo::nth(__picked, __n);\nlet mut {name}: {T(item.Type)} = __kept.item(__at);\nif rt::memo::passes(c, &__kept, __at, |c: &mut Combat| {passes}) {{\n{hand}}}\n}}\n");
        Written++;
        foreach (var o in ops.Skip(at + 1)) Stmt(o, sb);
        w.Rep.Count("kept walks");
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
        if (returnsVoid || f.Ret == null) return f.Outs.Count == 0 ? "" : OutTuple(f, null, true) + "\n";
        return $"#[allow(unreachable_code)]\n{{ unreachable!(\"{Where}: no return\") }}\n";
    }

    /// <summary>What a member with `ref`/`out` parameters gives back: its value and theirs. `leaving`: the member
    /// returns right here, so the parameters are given away, not copied.</summary>
    private static string OutTuple(Frame f, string? value, bool leaving = false)
    {
        var parts = new List<string>();
        if (value != null) parts.Add(value);
        parts.AddRange(f.Outs.Select(o => leaving ? o : o + ".clone()"));
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
            for (int i = 0; i < b.Operations.Length; i++)
            {
                dead = false;
                // A using declaration's scope is the rest of its block.
                if (b.Operations[i] is IUsingDeclarationOperation ud) { UsingDeclaration(ud, b.Operations.Skip(i + 1).ToList(), sb); break; }
                Stmt(b.Operations[i], sb);
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
        bool game = HoldsGameCall(op);
        if (game) w.Rep.DroppedWithCall(Where, op);
        Removed(op, op, "a statement left out", game);
    }

    /// <summary>The [drop] entries (World.DropEntry) whose types the code uses, each with the members of those types it
    /// calls or reads.</summary>
    private Dictionary<string, SortedSet<string>> DropsIn(IOperation code)
    {
        var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        SortedSet<string> Of(string e) => found.TryGetValue(e, out var s) ? s : found[e] = new SortedSet<string>(StringComparer.Ordinal);
        void Type(ITypeSymbol? t, string? member = null)
        {
            switch (t)
            {
                case INamedTypeSymbol n:
                    if (w.DropEntry(n) is { } e)
                    {
                        var s = Of(e);
                        if (member != null) s.Add($"{n.Name}.{member}");
                    }
                    foreach (var a in n.TypeArguments) Type(a);
                    break;
                case IArrayTypeSymbol a: Type(a.ElementType); break;
            }
        }
        foreach (var d in code.DescendantsAndSelf())
        {
            Type(d.Type);
            switch (d)
            {
                case IInvocationOperation inv: Type(inv.TargetMethod.ContainingType, (inv.TargetMethod.ReducedFrom ?? inv.TargetMethod).Name); break;
                case IMemberReferenceOperation mr: Type(mr.Member.ContainingType, mr.Member.Name); break;
                case IObjectCreationOperation oc: Type(oc.Type, ".ctor"); break;
            }
        }
        return found;
    }

    /// <summary>Code that a [drop] entry takes out of a translated member (a statement left out, a branch never taken
    /// under a test on presentation, the whole member), recorded against each entry whose types `cause` uses.</summary>
    private void Removed(IOperation cause, IOperation code, string what, bool game)
    {
        foreach (var (entry, used) in DropsIn(cause))
            w.Rep.Removed(entry, Where, code, what, game || WritesOuterLocal(code), used);
    }

    /// <summary>A member left out whole (its signature, or nothing but presentation in it): what the drop entries its
    /// signature and body use take out with it.</summary>
    public void RemovedWhole(IOperation? body, IMethodSymbol m, string what)
    {
        if (body == null) return;
        bool game = HoldsGameCall(body);
        var entries = DropsIn(body);
        void Sig(ITypeSymbol t)
        {
            if (t is INamedTypeSymbol n && w.DropEntry(n) is { } e && !entries.ContainsKey(e)) entries[e] = new SortedSet<string>(StringComparer.Ordinal);
        }
        Sig(m.ReturnType);
        foreach (var p in m.Parameters) Sig(p.Type);
        foreach (var (entry, used) in entries)
            w.Rep.Removed(entry, Where, body, what, game, used);
    }

    /// <summary>Whether a statement that is left out held something that is not presentation: a call into game code, or a write to state.</summary>
    /// <summary>Whether a call of a delegate is the raising of an event that is presentation (`Changed?.Invoke(x)`,
    /// `Changed(x)`): one that is not state.</summary>
    private bool RaisesEvent(IInvocationOperation inv)
    {
        bool Idle(IOperation? raised) => raised is IEventReferenceOperation er && !w.StateEvents.Contains(er.Event.OriginalDefinition);
        if (inv.Instance is IEventReferenceOperation) return Idle(inv.Instance);
        if (inv.Instance is not IConditionalAccessInstanceOperation) return false;
        for (var p = inv.Parent; p != null; p = p.Parent)
            if (p is IConditionalAccessOperation ca) return Idle(ca.Operation);
        return false;
    }

    /// <summary>Whether evaluating an expression does nothing but give its value: no call, nothing made, nothing written.</summary>
    private static bool OnlyReads(IOperation op) =>
        !op.DescendantsAndSelf().Any(d => d is IInvocationOperation or IObjectCreationOperation or IAssignmentOperation or IIncrementOrDecrementOperation
            or IDeclarationExpressionOperation or IAwaitOperation or IEventAssignmentOperation);

    private bool HoldsGameCall(IOperation op)
    {
        foreach (var d in op.DescendantsAndSelf())
        {
            switch (d)
            {
                case IInvocationOperation inv:
                    var m = inv.TargetMethod.ReducedFrom ?? inv.TargetMethod;
                    // A function that was handed over is called: what it does is not known here, so it is game code.
                    // (An event that is raised is presentation when what listens to it is the screen: one that is not state.)
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
                case IEventAssignmentOperation { EventReference: IEventReferenceOperation subscribed } when w.StateEvents.Contains(subscribed.Event.OriginalDefinition):
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
                        string yielded = NoDrop(r.ReturnedValue!, "yields presentation");
                        // An object is handed on only when the caller's code does something for its kind.
                        if (r.ReturnedValue!.Type != null && w.IsObj(r.ReturnedValue.Type))
                            sb.Append($"{{\nlet __y = {yielded};\nif rt::wanted(__only, __y, c) && __k(__y, c)? {{\n{LeaveWith("Ok(true)")}\n}}\n}}\n");
                        else
                            sb.Append($"if __k({yielded}, c)? {{\n{LeaveWith("Ok(true)")}\n}}\n");
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
                // A using block: what it holds is made, the block runs, and a resource whose disposing is game code is
                // disposed at its end (Disposals).
                var held = new List<(string? Code, ITypeSymbol? Type, string Name)>();
                if (us.Resources is IVariableDeclarationGroupOperation ug)
                {
                    Stmt(ug, sb);
                    held.AddRange(Declared(ug));
                }
                // A resource that is not a declared local is made only when its disposing is game code; otherwise it
                // is presentation (a lock's scope) or what it stands for is kept by hand (the selector stack).
                else if (us.Resources.Type is INamedTypeSymbol rt && World.IsSource(rt) && DisposeOf(rt, us.IsAsynchronous ? "DisposeAsync" : "Dispose") is { } d && !w.IsDropMember(d))
                {
                    string u = Tmp("u");
                    sb.Append($"let {u} = {Expr(us.Resources)};\n");
                    held.Add((u, us.Resources.Type, us.Resources.Syntax.ToString()));
                }
                else held.Add((null, us.Resources.Type, us.Resources.Syntax.ToString()));
                string fin = Disposals(held, us.IsAsynchronous, us);
                Scoped(inner => Block(us.Body, inner), [us.Body], fin, sb);
                return;
            }
            case IUsingDeclarationOperation ud:
                // Outside a plain block (Block takes the ones inside one): nothing to dispose at the end of it.
                Stmt(ud.DeclarationGroup, sb);
                if (Disposals(Declared(ud.DeclarationGroup), ud.IsAsynchronous, ud).Length > 0) throw new Unsupported("a using declaration that disposes, outside a plain block");
                return;
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

    /// <summary>The whole body of the member being translated.</summary>
    private IOperation? memberRoot;

    /// <summary>Whether the member being translated gives a LINQ chain that C# has not evaluated yet (World.LazyMember).</summary>
    private bool lazyMember;

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
        if (f.Iterator && lazyMember && frames.Count == 1 && value != null) return ReturnWalk(value);
        if (f.Iterator) throw new Unsupported("return in an iterator");
        if (value == null)
        {
            if (Around != null) return LeaveWith(f.ReturnsTask && f.Async ? "Ok(())" : f.Outs.Count > 0 ? OutTuple(f, null) : "()");
            return f.ReturnsTask && f.Async ? "return Ok(());" : f.Outs.Count > 0 ? $"return {OutTuple(f, null, true)};" : "return;";
        }
        string v;
        try { v = nullReturn && frames.Count == 1 ? MaybeNull(value) : Expr(value); }
        catch (Dropped d)
        {
            // A member whose one statement returns presentation is presentation as a whole. One return among several
            // is a branch nothing in a combat takes (the generator for what does not matter, of a canonical model).
            if (!severalStatements) throw new Unsupported($"returns presentation ({d.Message})");
            w.Rep.Count("a return of presentation left as a panic");
            return $"panic!(\"{Where}: {d.Message.Replace("\"", "'")}: presentation only\");";
        }
        if (f.ReturnsTask && f.Async) return LeaveWith($"Ok({v})");
        return f.Outs.Count > 0 ? $"{{ let __r = {v}; {LeaveWith(OutTuple(f, "__r", Around == null))} }}" : LeaveWith(v);
    }

    private void Declare(IVariableDeclaratorOperation d, IOperation? init, StringBuilder sb, IOperation whole)
    {
        var local = d.Symbol;
        // A collection that can be null is an Option (World.NullLocals).
        if (nullLocals.Contains(local) && !w.IsDropped(local.Type))
        {
            string opt = declaredAhead.Contains(local) ? NameOf(local) : $"let mut {NameOf(local)}: Option<{T(local.Type)}>";
            if (init == null) { if (!declaredAhead.Contains(local)) sb.Append($"{opt} = None;\n"); return; }
            sb.Append($"{opt} = {MaybeNull(init)};\n");
            Written++;
            return;
        }
        if (w.IsDropped(local.Type))
        {
            droppedLocals.Add(local);
            Leave(whole);
            return;
        }
        // A LINQ chain held in a local is not evaluated yet in C#: each use walks it then. Here the local holds the
        // chain's source, and each use runs the chain over it (a loop over it: LazySteps).
        if (init != null && local.Type is INamedTypeSymbol { Name: "IEnumerable" } && IsDeferred(init))
        {
            var chain = LazyChain(init);
            var root = chain?.Source ?? init;
            if (!reassigned.Contains(local) && !declaredAhead.Contains(local) && (chain != null || WalkOf(root) is { CallOn: not null }))
            {
                sb.Append(LazyLocal(local, root, chain?.Steps ?? [], whole));
                Written++;
                return;
            }
            deferredLocals.Add(local);
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
        // A pair's value, in a loop over a dictionary by key: the dictionary's own value.
        if (World.CollOf(local.Type) != World.Coll.None && !reassigned.Contains(local)
            && Unwrapped(init) is IPropertyReferenceOperation { Property.Name: "Value", Instance: ILocalReferenceOperation { Local: var pl } } && pairs.TryGetValue(pl, out var pv))
        {
            aliases[local] = pv.Value;
            return;
        }
        // A copy of a collection of collections (`results.ToList()`) whose items are changed in place, and that is
        // not changed itself: C# copies the outer collection only, so its items are the source's own, and the local
        // is the source.
        if (World.CollOf(local.Type) == World.Coll.Vec && !reassigned.Contains(local) && bodyOp != null && local is ILocalSymbol outer
            && init is IInvocationOperation { TargetMethod.Name: "ToList" or "ToArray", Arguments.Length: <= 1 } copy
            && (copy.Instance ?? (copy.Arguments.Length == 1 ? copy.Arguments[0].Value : null)) is { } copied && FieldPlaceOf(copied) is { } source
            && w.ChangesOf(outer, bodyOp).Count == 0
            && bodyOp.Descendants().OfType<IForEachLoopOperation>().Any(fe => fe.Collection is var fc && Unwrapped(fc) is ILocalReferenceOperation { Local: var over } && Eq.Equals(over, outer)
                && fe.LoopControlVariable is IVariableDeclaratorOperation { Symbol: var item } && w.ChangesOf(item, fe.Body).Count > 0))
        {
            aliases[local] = source;
            w.Rep.Count("copies of a collection of collections whose items are changed in place: the source itself");
            return;
        }
        // A list read out of a field of an object that takes code to reach (`GetPile(owner).Cards`,
        // `GetInternalData<Data>().list`): the object is reached once, here, as C# does, and the local is that field.
        if (World.CollOf(local.Type) != World.Coll.None && !reassigned.Contains(local) && !declaredAhead.Contains(local) && UnstableReceiver(init) is { } holder)
        {
            string h = Tmp("holder");
            pinned[holder] = h;
            string? held = FieldPlaceOf(init);
            pinned.Remove(holder);
            if (held != null)
            {
                sb.Append($"let {h} = {Expr(holder)};\n");
                aliases[local] = held;
                Written++;
                w.Rep.Count("lists read out of a field of an object reached by code: the object reached once, the local that field");
                return;
            }
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
        if (World.CollOf(local.Type) != World.Coll.None && bodyOp != null && local is ILocalSymbol cl)
        {
            collSources ??= World.CollectionSources(bodyOp);
            if (w.HoldsState(init, collSources, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)))
                w.Rep.Alias(Where, init, $"{local.Name}: read as a copy of a collection the state holds; C# would see later changes made to it under another name");
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

    /// <summary>How the code can leave the statement it is and go on elsewhere: "return" (also `yield`),
    /// "continue" (to a loop around it), "break" or "goto"; null when it cannot. Of each, the one that skips most. A
    /// throw does not count: it ends the game with an error, and the engine holds to games that do not fail. Lambdas
    /// and local functions inside are code of their own.</summary>
    private static string? Leaving(IOperation code)
    {
        var inner = new HashSet<ILabelSymbol>(SymbolEqualityComparer.Default);
        var ops = new List<IOperation>();
        void Walk(IOperation o)
        {
            ops.Add(o);
            foreach (var ch in o.ChildOperations)
                if (ch is not (IAnonymousFunctionOperation or ILocalFunctionOperation)) Walk(ch);
        }
        Walk(code);
        foreach (var o in ops)
            switch (o)
            {
                case ILoopOperation l: inner.Add(l.ExitLabel); inner.Add(l.ContinueLabel); break;
                case ISwitchOperation sw: inner.Add(sw.ExitLabel); break;
                case ILabeledOperation lb: inner.Add(lb.Label); break;
            }
        string? how = null;
        int Rank(string h) => h switch { "continue" => 0, "return" => 1, _ => 2 };
        void Take(string h) { if (how == null || Rank(h) > Rank(how)) how = h; }
        foreach (var o in ops)
            switch (o)
            {
                case IReturnOperation: Take("return"); break;
                case IBranchOperation b when !inner.Contains(b.Target):
                    Take(b.BranchKind switch { BranchKind.Continue => "continue", BranchKind.Break => "break", _ => "goto" });
                    break;
            }
        return how;
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
        if (GetOrInsert(cond, sb)) return;
        string test;
        try { test = Expr(cond.Condition); }
        catch (Dropped d)
        {
            // A test on presentation: the whole statement goes when nothing in it is left. One that can leave (a
            // return, continue or break: `if (node.IsVisible) return;`) decides whether what follows runs: it goes
            // only when it can only return or continue and nothing but presentation follows it.
            if (!HoldsGameCall(cond) && !WritesOuterLocal(cond))
            {
                if (Leaving(cond) is not { } how) { Leave(cond); return; }
                if (how is "return" or "continue" && NothingAfter(cond))
                {
                    w.Rep.Guard(Where, cond, $"leaves by {how}, nothing after it: left out");
                    Leave(cond);
                    return;
                }
                w.Rep.Guard(Where, cond, $"leaves by {how}, and what follows runs only when it does not: not translated");
                throw new Unsupported($"a test on presentation that leaves by {how} guards what follows ({d.Message})");
            }
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
            if ((test == "true" ? cond.WhenFalse : cond.WhenTrue) is { } skipped)
                Removed(cond.Condition, skipped, $"the branch a test on presentation never takes (it is {test} here)", HoldsGameCall(skipped));
            dead = test == "true" && EndsInReturn(cond.WhenTrue);
            return;
        }
        string then = Sub(cond.WhenTrue), other = Sub(cond.WhenFalse);
        // Nothing is left under the test and the test only reads: the statement is left out (a member with nothing else is presentation).
        if (then.Length == 0 && other.Length == 0 && OnlyReads(cond.Condition)) { LeftOut++; return; }
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
        if (!w.IsDropped(local.Type) && WalkCall(fe.Collection) is { } walk)
        {
            ForEachWalk(fe, local, new WalkSource(each => Invoke(walk, each), T(World.WalkItem(walk.TargetMethod.ReducedFrom ?? walk.TargetMethod)!), null, null), new LazyCode("", "", LazyFirst, "return Ok(false);", 0), sb, OnlyFor(fe, local, walk));
            return;
        }
        string coll;
        string label = "'l" + tmp++;
        LazyChainOf? lazy;
        var run = new LazyCode("", "", "", $"continue {label};", 0);
        try
        {
            if (w.IsDropped(local.Type)) throw new Dropped(local.Type.Name);
            lazy = Lazy(fe.Collection);
            coll = lazy == null ? IterSource(fe.Collection) : lazy.Code ?? "";
            if (lazy != null) run = lazy.Walk != null ? LazySteps(lazy.Steps, "return Ok(true);", "return Ok(false);") : LazySteps(lazy.Steps, $"break {label};", $"continue {label};");
            if (lazy is not { Whole: true } && IsDeferred(fe.Collection) && HoldsGameCall(fe.Body)) w.Rep.Deferred(Where, fe);
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
        if (lazy?.Walk != null) { ForEachWalk(fe, local, lazy.Walk, run, sb); return; }
        // The values of a dictionary of collections, or its pairs, where the loop changes a value in place: C# changes
        // the dictionary's own value, so the loop goes by key and the value is the dictionary's own there.
        if (lazy == null && Unwrapped(fe.Collection) is var walked && (walked is IPropertyReferenceOperation { Property.Name: "Values", Instance: { } ofValues } ? ofValues : walked) is var dict
            && World.CollOf(dict.Type) == World.Coll.Dict && ItemsPlace(dict) is { } dictPlace
            && (walked != dict ? World.CollOf(local.Type) != World.Coll.None && w.ChangesOf(local, fe.Body).Count > 0 : PairValueChanged(local, fe.Body)))
        {
            string k = Tmp("k");
            string value = $"(*{dictPlace}.at(&{k}))";
            if (walked != dict) aliases[local] = value;
            else pairs[local] = (k, value);
            loops.Push(($"break {label};", $"continue {label};"));
            string byKey = Sub(fe.Body);
            loops.Pop();
            sb.Append($"{label}: for {k} in {dictPlace}.keys() {{\n{byKey}}}\n");
            w.Rep.Count("loops over a dictionary of collections that change a value in place: by key, the value in its place");
            Written++;
            return;
        }
        // An item of a collection of collections that the loop changes in place: C# changes the collection's own
        // item, so the loop goes by position and the item is the collection's place there.
        if (lazy == null && World.CollOf(local.Type) != World.Coll.None && w.ChangesOf(local, fe.Body).Count > 0 && ItemsPlace(fe.Collection) is { } items)
        {
            string at = Tmp("at"), n = Tmp("n");
            aliases[local] = $"{items}[{at}]";
            loops.Push(($"break {label};", $"continue {label};"));
            string changing = Sub(fe.Body);
            loops.Pop();
            sb.Append($"let {n} = {items}.len();\n{label}: for {at} in 0..{n} {{\n{changing}}}\n");
            w.Rep.Count("loops over a collection of collections that change an item in place: by position, the item in its place");
            Written++;
            return;
        }
        loops.Push(($"break {label};", run.Next));
        string body = Sub(fe.Body);
        loops.Pop();
        // Nothing is left in the loop and what it walks is only read: the statement is left out.
        if (body.Length == 0 && lazy == null && OnlyReads(fe.Collection)) { LeftOut++; return; }
        if (lazy == null && fe.Body.Descendants().Any(o => o is IInvocationOperation or IAssignmentOperation or IIncrementOrDecrementOperation or IObjectCreationOperation)
            && ChangeableList(fe.Collection) is var (_, pin, listCode))
        {
            // A list the loop's code could change: the walk goes over the list as it began. C#'s enumerator throws at
            // its next step when the list changed, a bug of the game that leaves its combat stuck (the turn loop
            // dies); the engine plays on, as a copy of the list (ToList) would. The object that holds the list is
            // reached once, as C# reaches the collection once.
            string seen = Tmp("w"), at = Tmp("at");
            sb.Append($"{{\n{pin}let {seen} = {listCode};\nlet mut {at} = 0usize;\n{label}: loop {{\nif {at} == {seen}.len() {{ break {label}; }}\n"
                + $"let mut {NameOf(local)}: {T(local.Type)} = {seen}[{at}].clone();\n{at} += 1;\n{body}}}\n}}\n");
            w.Rep.Count("loops over a list their code could change: the list as it began");
        }
        else if (lazy == null) sb.Append($"{label}: for {NameOf(local)} in {coll} {{\nlet mut {NameOf(local)}: {T(local.Type)} = {NameOf(local)};\n{body}}}\n");
        else sb.Append($"{run.Before}{label}: for {LazyFirst} in {coll} {{\n{run.Code}let mut {NameOf(local)}: {T(local.Type)} = {run.Item};\n{body}}}{new string('}', run.Nested)}\n");
        Written++;
    }

    /// <summary>`if (!d.TryGetValue(k, out v)) { v = new T(); d[k] = v; }` with v changed after: C# then has one
    /// collection under two names, the dictionary's value and v. The new collection is put in when the key is not
    /// there, and v is the dictionary's own value at the key, the key reached once.</summary>
    private bool GetOrInsert(IConditionalOperation cond, StringBuilder sb)
    {
        if (cond.WhenFalse != null || bodyOp == null) return false;
        if (Unwrapped(cond.Condition) is not IUnaryOperation { OperatorKind: UnaryOperatorKind.Not, Operand: var asked }
            || Unwrapped(asked) is not IInvocationOperation { TargetMethod.Name: "TryGetValue", Instance: { } dict, Arguments: [var keyArg, var outArg] }
            || World.CollOf(dict.Type) != World.Coll.Dict)
            return false;
        if ((outArg.Value is IDeclarationExpressionOperation de ? de.Expression : outArg.Value) is not ILocalReferenceOperation { Local: var v } || World.CollOf(v.Type) == World.Coll.None)
            return false;
        var stmts = cond.WhenTrue is IBlockOperation b ? b.Operations.ToList() : [cond.WhenTrue];
        if (stmts is not [IExpressionStatementOperation { Operation: ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var made }, Value: var fresh } },
                IExpressionStatementOperation { Operation: ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true, Instance: { } dict2, Arguments: [var key2] }, Value: var stored } }]
            || !Eq.Equals(made, v) || Unwrapped(stored) is not ILocalReferenceOperation { Local: var put } || !Eq.Equals(put, v)
            || dict2.Syntax.ToString() != dict.Syntax.ToString() || key2.Value.Syntax.ToString() != keyArg.Value.Syntax.ToString())
            return false;
        collSources ??= World.CollectionSources(bodyOp);
        if (w.HoldsState(fresh, collSources, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)) || w.ChangesOf(v, bodyOp).Count == 0 || ItemsPlace(dict) is not { } d)
            return false;
        // v is set nowhere but here.
        if (bodyOp.Descendants().Count(o => o is ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var t } } && Eq.Equals(t, v)) != 1) return false;
        string k = Tmp("key");
        sb.Append($"let {k} = {Expr(keyArg.Value)};\nif !{d}.contains_key(&{k}) {{\n{Mutable(d)}.set({k}.clone(), {Expr(fresh)});\n}}\n");
        aliases[v] = $"(*{d}.at(&{k}))";
        w.Rep.Count("get-or-insert of a collection in a dictionary, changed after: the local is the dictionary's own value");
        Written++;
        return true;
    }

    /// <summary>What a loop over a walk hands the walk as the kinds its body does something for (Combat::only),
    /// set right before the call, whose target takes it first thing: what the walk was handed, when the body only
    /// hands each value on (`foreach (var x in inner) yield return x;`); or the kinds that override the one virtual
    /// member the body calls, when that member's own body leaves everything as it is for these arguments
    /// (`item.TryModifyStarCost(card, cost, out cost)` against `{ modifiedCost = originalCost; return false; }`): for
    /// any other kind the call does nothing. Null when the walk's call does more than name its arguments.</summary>
    private string? OnlyFor(IForEachLoopOperation fe, ILocalSymbol local, IInvocationOperation walk)
    {
        if (walk.Instance is { } on && !IsSimple(Unwrapped(on)) && Unwrapped(on) is not IInstanceReferenceOperation) return null;
        if (walk.Arguments.Any(a => !IsSimple(Unwrapped(a.Value)))) return null;
        // What is left of the body once the statements left out as presentation are gone.
        var kept = fe.Body is IBlockOperation block ? block.Operations.Where(s => !LeftOutWhole(s)).ToList() : [fe.Body];
        // Around the call, a pair of members of an object the loop does not make, which leaves it as it was
        // (port.toml [only] pairs): `choiceContext.PushModel(model); await model.M(choiceContext, ..); choiceContext.PopModel(model);`.
        bool paired = kept.Count == 3 && Paired(kept[0], kept[2], local);
        if (paired) kept = [kept[1]];
        // An object made for the call alone (port.toml [only] scratch): `var context = new K(model, ..); Task task =
        // model.M(context, ..); await context.A(task); tasks.Add(context.B());`.
        IInvocationOperation? forScratch = kept.Count >= 2 ? ScratchCall(kept, local) : null;
        // The call's value into a local, then statements that the value the member's own body gives leaves as they
        // were: `decimal num2 = item.ModifyDamageAdditive(..); num += num2; if (num2 != 0m) list.Add(item);` against
        // `{ return 0m; }`.
        ILocalSymbol? given = null;
        List<IOperation> withGiven = [];
        if (forScratch == null && kept.Count >= 2 && kept[0] is IVariableDeclarationGroupOperation { Declarations: [{ Declarators: [var gd] } gdecl] }
            && (gd.Initializer?.Value ?? gdecl.Initializer?.Value) is { } gv && Unwrapped(gv) is IInvocationOperation gcall)
        {
            given = gd.Symbol;
            withGiven = kept.Skip(1).ToList();
            forScratch = gcall;
        }
        if (forScratch == null && kept.Count != 1) return null;
        var body = forScratch ?? kept[0];
        if (frames.Peek().Iterator && body is IReturnOperation { Kind: OperationKind.YieldReturn, ReturnedValue: { } y } && Unwrapped(y) is ILocalReferenceOperation { Local: var yl } && Eq.Equals(yl, local))
            return "__only";
        // The call alone (awaited or not), or `if (call) ..` where the member's own body says false.
        var (asked, mustSayFalse) = body switch
        {
            IInvocationOperation made => (made, false),
            IExpressionStatementOperation { Operation: IInvocationOperation alone } => (alone, false),
            IExpressionStatementOperation { Operation: IAwaitOperation { Operation: var awaited } } when Unwrapped(awaited) is IInvocationOperation awaitedCall => (awaitedCall, false),
            IConditionalOperation { WhenFalse: null, Condition: var cond } when Unwrapped(cond) is IInvocationOperation tested => (tested, true),
            _ => ((IInvocationOperation?)null, false),
        };
        if (asked is not { Instance: { } inst, TargetMethod: var m } call
            || Unwrapped(inst) is not ILocalReferenceOperation { Local: var called } || !Eq.Equals(called, local) || !call.IsVirtual || !Dispatched(m))
            return null;
        var slot = (IMethodSymbol)World.Slot(m.OriginalDefinition);
        if (slot.IsAbstract || w.MemberBody(slot) is not IMethodBodyOperation { } ownBody || (ownBody.BlockBody ?? ownBody.ExpressionBody) is not { } own) return null;
        if (call.Arguments.Any(a => a.Value is not IDeclarationExpressionOperation && !IsSimple(Unwrapped(a.Value)))) return null;
        // The member's own body: each out parameter set to a parameter, then a constant returned (false, when the
        // call is a test), or a task that has run to its end (`Task.CompletedTask`: awaited, it goes on at once).
        static bool Done(IOperation said) => said is IPropertyReferenceOperation { Property: { IsStatic: true, Name: "CompletedTask" } done }
            && done.ContainingType.ToDisplayString() == "System.Threading.Tasks.Task";
        var copies = new Dictionary<int, int>();
        var ops = own.Operations;
        for (int i = 0; i < ops.Length; i++)
        {
            if (i == ops.Length - 1 && ops[i] is IReturnOperation { ReturnedValue: var said }
                && (said == null ? !mustSayFalse : said.ConstantValue is { HasValue: true, Value: var v } ? !mustSayFalse || v is false : !mustSayFalse && Done(Unwrapped(said))))
            {
                if (given != null && (said?.ConstantValue is not { HasValue: true, Value: { } k } || !withGiven.All(st => Idle(st, given, k)))) return null;
                break;
            }
            if (ops[i] is IExpressionStatementOperation { Operation: ISimpleAssignmentOperation { Target: IParameterReferenceOperation { Parameter: { RefKind: RefKind.Out } to }, Value: var from } }
                && Unwrapped(from) is IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } p })
                copies[to.Ordinal] = p.Ordinal;
            else return null;
        }
        // At the call, each out argument is the local handed in for the parameter it is set to.
        ISymbol? Named(IOperation v) => (v is IDeclarationExpressionOperation de ? de.Expression : Unwrapped(v)) switch
        {
            ILocalReferenceOperation l => l.Local,
            IParameterReferenceOperation pr => pr.Parameter,
            _ => null,
        };
        foreach (var p in slot.Parameters)
        {
            if (p.RefKind == RefKind.Ref) return null;
            if (p.RefKind != RefKind.Out) continue;
            if (!copies.TryGetValue(p.Ordinal, out int fromOrdinal)) return null;
            var outArg = call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == p.Ordinal);
            var inArg = call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == fromOrdinal);
            if (outArg == null || inArg == null || Named(outArg.Value) is not { } o || !Eq.Equals(o, Named(inArg.Value))) return null;
        }
        w.OnlyFilters.TryAdd(slot, 0);
        w.Rep.Count("loops over a walk whose body only calls a member that does nothing where it is not overridden: the walk passes over the other kinds");
        if (paired) w.Rep.Count("  of them with a pair of port.toml [only] pairs around the call");
        if (forScratch != null) w.Rep.Count("  of them with an object of port.toml [only] scratch made for the call");
        return $"Some(&g::dispatch::{w.Module(slot.ContainingType)}::{w.FnName(slot).ToUpperInvariant()}__ONLY)";
    }

    /// <summary>Whether a statement changes nothing when the local holds the constant: `x += v` with v a zero of no
    /// scale, `x *= v` with v a one of no scale (of decimal, the sum and the product are then x, its digits, scale and
    /// sign; of an integer too), `if (test) ..` whose test is false with v (`v != k` for k equal to v, `v < y` for v the
    /// largest value), on a local or parameter x and a y that is only read.</summary>
    private static bool Idle(IOperation st, ILocalSymbol v, object k)
    {
        bool IsV(IOperation o) => Named(o) is ILocalSymbol l && Eq.Equals(l, v);
        static bool Integral(object x) => x is int or long or short or sbyte or uint or ulong or ushort or byte;
        static bool Plain(decimal d) => (decimal.GetBits(d)[3]) == 0;
        bool Zero() => k is decimal d ? d == 0m && Plain(d) : Integral(k) && System.Convert.ToDecimal(k) == 0m;
        bool One() => k is decimal d ? d == 1m && Plain(d) : Integral(k) && System.Convert.ToDecimal(k) == 1m;
        bool Largest() => k switch { decimal d => d == decimal.MaxValue, int i => i == int.MaxValue, long l => l == long.MaxValue, _ => false };
        switch (st)
        {
            case IExpressionStatementOperation { Operation: ICompoundAssignmentOperation { OperatorKind: var op, Target: var x, Value: var by } }
                when IsV(by) && Named(x) is ILocalSymbol or IParameterSymbol:
                return op is BinaryOperatorKind.Add && Zero() || op is BinaryOperatorKind.Multiply && One();
            case IConditionalOperation { WhenFalse: null, Condition: var cond } when Unwrapped(cond) is IBinaryOperation { OperatorKind: var op, LeftOperand: var a, RightOperand: var b }:
                if (op is BinaryOperatorKind.NotEquals)
                {
                    var other = IsV(a) ? b : IsV(b) ? a : null;
                    return other?.ConstantValue is { HasValue: true, Value: { } kc } && (k is decimal || Integral(k)) && System.Convert.ToDecimal(k) == System.Convert.ToDecimal(kc);
                }
                if (op is BinaryOperatorKind.LessThan && IsV(a) && OnlyReads(b) || op is BinaryOperatorKind.GreaterThan && IsV(b) && OnlyReads(a))
                    return Largest();
                return false;
            default:
                return false;
        }
    }

    private static ISymbol? Named(IOperation o) => Unwrapped(o) switch
    {
        ILocalReferenceOperation l => l.Local,
        IParameterReferenceOperation p => p.Parameter,
        _ => null,
    };

    private static string MemberName(IMethodSymbol m) => $"{m.OriginalDefinition.ContainingType.Name}.{m.Name}";

    /// <summary>Whether two statements are calls of a pair of port.toml [only] pairs, the first before the call and the
    /// second after it, on the same object (a local or parameter the loop does not make, which the call cannot change
    /// to another), each handed the loop's value alone.</summary>
    private bool Paired(IOperation first, IOperation last, ILocalSymbol local)
    {
        if (first is not IExpressionStatementOperation { Operation: IInvocationOperation { Instance: { } oa } open }
            || last is not IExpressionStatementOperation { Operation: IInvocationOperation { Instance: { } ob } close }) return false;
        var (o, c) = (open.TargetMethod.OriginalDefinition, close.TargetMethod.OriginalDefinition);
        bool Below(INamedTypeSymbol? t, string name) { for (; t != null; t = t.BaseType) if (t.Name == name) return true; return false; }
        if (!w.Cfg.OnlyPairs.Any(p => p.Open == o.Name && p.Close == c.Name && Below(o.ContainingType, p.Class) && Eq.Equals(o.ContainingType, c.ContainingType))) return false;
        if (Named(oa) is not { } on || !Eq.Equals(on, Named(ob)) || Eq.Equals(on, local)) return false;
        bool Item(IInvocationOperation i) => i.Arguments is [var x] && Named(x.Value) is ILocalSymbol l && Eq.Equals(l, local);
        return Item(open) && Item(close);
    }

    /// <summary>The call of a loop body that makes an object of a port.toml [only] scratch class for it alone: the
    /// object made into a local from values that are only read, then the call (its task into a local, or awaited, or
    /// left), then only members of the object's class called on it (awaited, or what they give added to a list of
    /// tasks that is only added to, awaited all at once and cleared), handed the task or simple values; the object and
    /// the task used nowhere else. Null when the body is not that.</summary>
    private IInvocationOperation? ScratchCall(List<IOperation> stmts, ILocalSymbol local)
    {
        static (ILocalSymbol, IOperation)? Declared(IOperation st) =>
            st is IVariableDeclarationGroupOperation { Declarations: [{ Declarators: [var d] } decl] } && (d.Initializer?.Value ?? decl.Initializer?.Value) is { } v
                ? (d.Symbol, v) : null;
        static bool OnlyRead(IOperation op) => OnlyReads(op) && !op.DescendantsAndSelf().Any(d => d is IPropertyReferenceOperation pr && World.IsSource(pr.Property.ContainingType));
        if (Declared(stmts[0]) is not { } first) return null;
        var (made, init) = first;
        if (Unwrapped(init) is not IObjectCreationOperation { Type: INamedTypeSymbol k } creation
            || !w.Cfg.OnlyScratch.Contains(k.Name) || creation.Initializer != null || creation.Arguments.Any(a => !OnlyRead(a.Value))) return null;
        IInvocationOperation? call = null;
        ILocalSymbol? task = null;
        if (Declared(stmts[1]) is { } second && Unwrapped(second.Item2) is IInvocationOperation ti) (call, task) = (ti, second.Item1);
        else if (stmts[1] is IExpressionStatementOperation { Operation: var e1 } && Unwrapped(e1 is IAwaitOperation aw1 ? aw1.Operation : e1) is IInvocationOperation te) call = te;
        if (call == null) return null;
        var asked = new HashSet<IOperation>();
        foreach (var st in stmts.Skip(2))
        {
            if (st is not IExpressionStatementOperation { Operation: var e }) return null;
            if (e is IAwaitOperation aw) e = aw.Operation;
            if (Unwrapped(e) is not IInvocationOperation own) return null;
            if (own is { TargetMethod.Name: "Add", Instance: { } li, Arguments: [var added] } && !World.IsSource(own.TargetMethod.ContainingType))
            {
                if (Named(li) is not ILocalSymbol list || !TasksOnly(list) || Unwrapped(added.Value) is not IInvocationOperation inner) return null;
                own = inner;
            }
            bool OfK(INamedTypeSymbol? c) { for (var x = k; x != null; x = x.BaseType) if (Eq.Equals(x.OriginalDefinition, c?.OriginalDefinition)) return true; return false; }
            if (own.Instance is not { } oi || Named(oi) is not ILocalSymbol ol || !Eq.Equals(ol, made) || !OfK(own.TargetMethod.ContainingType)) return null;
            if (own.Arguments.Any(a => !(task != null && Named(a.Value) is ILocalSymbol al && Eq.Equals(al, task)) && !IsSimple(Unwrapped(a.Value)))) return null;
            asked.Add(own.Instance);
            foreach (var a in own.Arguments) asked.Add(a.Value);
        }
        // The object and the task go nowhere else: the object only into the call and as the receiver above, the task only
        // to the members above.
        var into = call.Arguments.Select(a => a.Value).ToHashSet();
        foreach (var r in stmts.SelectMany(st => st.Descendants()).OfType<ILocalReferenceOperation>())
        {
            if (Eq.Equals(r.Local, made) && !r.IsDeclaration && !into.Contains(OuterConversion(r)) && !asked.Contains(OuterConversion(r))) return null;
            if (task != null && Eq.Equals(r.Local, task) && !r.IsDeclaration && !asked.Contains(OuterConversion(r))) return null;
        }
        return call;
    }

    private static IOperation OuterConversion(IOperation o)
    {
        while (o.Parent is IConversionOperation { IsTryCast: false } cv) o = cv;
        return o;
    }

    /// <summary>Whether a local list of tasks is only added to, awaited all at once (`Task.WhenAll(list)`) and cleared,
    /// in the method: a task run to its end, more or fewer, changes nothing then.</summary>
    private bool TasksOnly(ILocalSymbol list)
    {
        if (bodyOp == null || list.Type is not INamedTypeSymbol { TypeArguments: [var item] } || item.ToDisplayString() != "System.Threading.Tasks.Task") return false;
        foreach (var r in bodyOp.Descendants().OfType<ILocalReferenceOperation>().Where(r => Eq.Equals(r.Local, list)))
        {
            if (r.IsDeclaration) continue;
            var at = OuterConversion(r);
            bool ok = at.Parent switch
            {
                IInvocationOperation { TargetMethod.Name: "Add" or "Clear" } inv when inv.Instance == at && !World.IsSource(inv.TargetMethod.ContainingType) => true,
                IArgumentOperation { Parent: IInvocationOperation { TargetMethod: { Name: "WhenAll", ContainingType: var tt } } } when tt.ToDisplayString() == "System.Threading.Tasks.Task" => true,
                ISimpleAssignmentOperation { Target: var tg } when tg == at => false,
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Whether a statement is left out whole as presentation: a call (awaited or not) of a member that is
    /// presentation, on a value and with arguments that only read (`model.InvokeExecutionFinished();`).</summary>
    private bool LeftOutWhole(IOperation stmt)
    {
        if (stmt is not IExpressionStatementOperation { Operation: var e }) return false;
        if (e is IAwaitOperation aw) e = aw.Operation;
        if (e is not IInvocationOperation inv) return false;
        var m = (inv.TargetMethod.ReducedFrom ?? inv.TargetMethod).OriginalDefinition;
        bool presentation = w.IsDropped(m.ContainingType) || World.IsSource(m.ContainingType) && w.IsDropMember(m);
        return presentation && (inv.Instance == null || OnlyReads(inv.Instance)) && inv.Arguments.All(a => OnlyReads(a.Value));
    }

    /// <summary>Whether a loop over a dictionary's pairs changes a pair's value in place, itself or through a local
    /// set to it.</summary>
    private bool PairValueChanged(ILocalSymbol pair, IOperation body)
    {
        bool IsValue(IOperation? o) => o != null && Unwrapped(o) is IPropertyReferenceOperation { Property.Name: "Value", Instance: ILocalReferenceOperation { Local: var p } } && Eq.Equals(p, pair);
        if (World.CollOf((pair.Type as INamedTypeSymbol)?.TypeArguments.ElementAtOrDefault(1)) == World.Coll.None) return false;
        foreach (var d in body.DescendantsAndSelf())
        {
            if (d is IVariableDeclaratorOperation { Symbol: var l } vd && IsValue(vd.Initializer?.Value ?? (vd.Parent as IVariableDeclarationOperation)?.Initializer?.Value)
                && !reassigned.Contains(l) && w.ChangesOf(l, body).Count > 0) return true;
            if (d is IInvocationOperation { Instance: var on } inv && IsValue(on) && World.IsSource(inv.TargetMethod.ContainingType) == false) return true;
        }
        return false;
    }

    private static IOperation Unwrapped(IOperation o)
    {
        while (o is IConversionOperation { IsTryCast: false } cv) o = cv.Operand;
        return o;
    }

    /// <summary>The place a loop's collection is, to reach its items where they are: a local, a parameter, a
    /// field; null for a value made for the loop.</summary>
    private string? ItemsPlace(IOperation coll)
    {
        while (coll is IConversionOperation { IsTryCast: false } cv && World.CollOf(cv.Operand.Type) != World.Coll.None) coll = cv.Operand;
        if (World.CollOf(coll.Type) is not (World.Coll.Vec or World.Coll.Dict)) return null;
        return coll switch
        {
            ILocalReferenceOperation l when !droppedLocals.Contains(l.Local) && !nullLocals.Contains(l.Local) && !lazyLocals.ContainsKey(l.Local) => Ref(coll),
            IParameterReferenceOperation => Ref(coll),
            _ => FieldPlaceOf(coll),
        };
    }

    /// <summary>Locals that hold a LINQ chain not yet evaluated in C# (`IEnumerable<T> xs = list.Where(f);`) and are
    /// evaluated where they are declared all the same: the ones the rule below does not take.</summary>
    private readonly HashSet<ISymbol> deferredLocals = new(Eq);

    /// <summary>Locals that hold a LINQ chain as C# holds it: the Rust local has the chain's source, and these are
    /// the operators a walk runs over it. `Whole`: nothing below the source is left for the walk to evaluate.</summary>
    private readonly Dictionary<ISymbol, LazyChainOf> lazyLocals = new(Eq);

    /// <summary>A sequence of the game that is walked by handing it the code to run for each value (a member written
    /// with yield, or one that gives a chain): the call of its `__each` form with that code, the type of a value, and,
    /// for a property of an object, the same call and the call that gives all values, on a given object.</summary>
    private sealed record WalkSource(Func<string, string> Call, string Item, Func<string, string, string>? CallOn, Func<string, string>? ValueOn);

    /// <summary>What a loop walks, when it runs the walk itself: the source (its code, or the sequence to hand the
    /// loop's code to), the operators to run for each value, and whether nothing below the source is left that C#
    /// would evaluate as the walk goes.</summary>
    private sealed record LazyChainOf(string? Code, WalkSource? Walk, List<Chains.Step> Steps, bool Whole);

    /// <summary>The operators of a chain as code (LazySteps): the code, what is declared before the walk, the name of
    /// the value the body gets, the statement that goes on with the next value, how many loops were opened inside.</summary>
    private sealed record LazyCode(string Code, string Before, string Item, string Next, int Nested);

    /// <summary>Whether C# evaluates the sequence only as it is walked, and runs code of the game while it does
    /// (World.DeferredChain); or a local that holds such a sequence.</summary>
    private bool IsDeferred(IOperation? op)
    {
        if (op != null && Chains.Bare(op) is ILocalReferenceOperation l) return deferredLocals.Contains(l.Local) || lazyLocals.ContainsKey(l.Local);
        // A chain over such a local.
        for (var at = op; at != null;)
        {
            at = Chains.Bare(at);
            if (at is ILocalReferenceOperation under) return deferredLocals.Contains(under.Local) || lazyLocals.ContainsKey(under.Local);
            if (at is not IInvocationOperation inv || !Chains.IsLinq(inv.TargetMethod) || !Chains.Streaming.Contains(inv.TargetMethod.Name)) break;
            var a = Chains.Ordered(inv);
            at = a.Count > 0 ? a[0] : null;
        }
        return w.DeferredChain(op);
    }

    /// <summary>A LINQ chain split where a walk can take it over (Chains.Split); null when no operator at its end is
    /// one a walk runs.</summary>
    private (IOperation Source, List<Chains.Step> Steps)? LazyChain(IOperation op)
    {
        var (source, steps) = Chains.Split(op, w.IsObj);
        return steps.Count == 0 ? null : (source, steps);
    }

    /// <summary>A read of a sequence of the game that can be walked value by value: a call of a method written with
    /// yield, or a property that is one or that gives a chain.</summary>
    private WalkSource? WalkOf(IOperation source)
    {
        if (WalkCall(source) is { } call) return new WalkSource(each => Invoke(call, each), T(World.WalkItem(call.TargetMethod.ReducedFrom ?? call.TargetMethod)!), null, null);
        var bare = source;
        while (bare is IConversionOperation { IsTryCast: false, Conversion: { IsIdentity: true } or { IsReference: true, IsImplicit: true } } cv) bare = cv.Operand;
        if (bare is not IPropertyReferenceOperation { Arguments.Length: 0 } op) return null;
        var def = op.Property.OriginalDefinition;
        var type = def.ContainingType;
        if (def.GetMethod is not { } getter || World.WalkItem(getter) is not { } item) return null;
        if (!World.IsSource(type) || World.IsGeneratedList(type) || w.IsValue(type) || w.IsDropped(type) || w.IsDropMember(def) || w.IsDropped(def.Type) || Value(def) != null || w.StaticState(def) != null) return null;
        bool virt = !def.IsStatic && VirtualMember(def) && !IsBaseAccess(op.Instance);
        string path = CallPath(getter, virt);
        if (!(path.StartsWith("g::dispatch::") ? w.IsWalk((IMethodSymbol)World.Slot(getter)) : w.Yields(getter))) return null;
        if (def.IsStatic)
        {
            string kinds = string.Concat(KindArgs(getter).Select(k => k + ", "));
            return new WalkSource(each => $"{path}__each({kinds}c, {each})", T(item), null, null);
        }
        return new WalkSource(each => $"{path}__each({(op.Instance == null ? "me" : Expr(op.Instance))}, c, {each})", T(item),
            (on, each) => $"{path}__each({on}, c, {each})", on => $"{path}({on}, c)");
    }

    /// <summary>What a loop walks, when the loop runs the walk itself: a LINQ chain C# evaluates as it is walked, a
    /// local that holds one, or a sequence of the game. Null for a plain collection.</summary>
    private LazyChainOf? Lazy(IOperation collection)
    {
        if (Chains.Bare(collection) is ILocalReferenceOperation l && lazyLocals.TryGetValue(l.Local, out var held)) return held;
        var chain = LazyChain(collection);
        var root = chain?.Source ?? collection;
        var steps = chain?.Steps ?? [];
        if (WalkOf(root) is { } walk) return new LazyChainOf(null, walk, steps, true);
        return steps.Count == 0 ? null : new LazyChainOf(Expr(root), null, steps, !IsDeferred(root));
    }

    private const string LazyFirst = "__x0";

    /// <summary>The operators of a chain as the code a walk of the chain's source runs for each value, before the
    /// loop's own body: C# runs an operator's function for a value when the loop reaches that value, after the loop's
    /// body ran for the values before it. `Where` goes on to the next value, `Select` makes the value, `SelectMany`
    /// is a loop inside, `TakeWhile` ends the walk. `end` and `next` are the statements that end the walk and go on
    /// with the source's next value.</summary>
    private LazyCode LazySteps(List<Chains.Step> steps, string end, string next)
    {
        var code = new StringBuilder();
        var before = new StringBuilder();
        string item = LazyFirst;
        int nested = 0;
        foreach (var step in steps)
        {
            string f = step.Function == null ? "" : $"({Closure(step.Function, 1)})";
            switch (step.Name)
            {
                case "OfType":
                    code.Append($"if !rt::is({item}, {KindExpr(step.Type!)}, c) {{ {next} }}\n");
                    break;
                case "Where":
                    code.Append($"if !{f}({item}.clone(), c) {{ {next} }}\n");
                    break;
                case "TakeWhile":
                    code.Append($"if !{f}({item}.clone(), c) {{ {end} }}\n");
                    break;
                case "SkipWhile":
                {
                    string skipping = Tmp("skipping");
                    before.Append($"let mut {skipping} = true;\n");
                    code.Append($"if {skipping} && {f}({item}.clone(), c) {{ {next} }}\n{skipping} = false;\n");
                    break;
                }
                case "Select":
                {
                    string made = Tmp("x");
                    code.Append($"let {made} = {f}({item}, c);\n");
                    item = made;
                    break;
                }
                case "SelectMany":
                {
                    string inner = Tmp("x"), label = "'l" + tmp++;
                    code.Append($"{label}: for {inner} in {f}({item}, c) {{\n");
                    next = $"continue {label};";
                    item = inner;
                    nested++;
                    break;
                }
            }
        }
        return new LazyCode(code.ToString(), before.ToString(), item, next, nested);
    }

    /// <summary>`return chain;` of a member that gives a chain, in its `__each` form: the chain is walked here, and
    /// each value is handed to the code that walks the member.</summary>
    private string ReturnWalk(IOperation value)
    {
        var lazy = Lazy(value) ?? throw new Unsupported("a member that gives a chain returns something else");
        if (lazy.Walk is { } walk)
        {
            var inside = LazySteps(lazy.Steps, "return Ok(true);", "return Ok(false);");
            string last = inside.Nested == 0 ? $"__k({inside.Item}, c)" : $"if __k({inside.Item}, c)? {{ return Ok(true); }}\n{new string('}', inside.Nested)}\nOk(false)";
            return $"{inside.Before}return {walk.Call($"&mut |{LazyFirst}: {walk.Item}, c: &mut Combat| -> R<bool> {{\n{inside.Code}{last}\n}}")};";
        }
        string label = "'l" + tmp++;
        var run = LazySteps(lazy.Steps, $"break {label};", $"continue {label};");
        return $"{run.Before}{label}: for {LazyFirst} in {lazy.Code} {{\n{run.Code}if __k({run.Item}, c)? {{ return Ok(true); }}\n}}{new string('}', run.Nested)}";
    }

    /// <summary>A local that holds a LINQ chain or a sequence of the game: the Rust local is the chain's source, read
    /// here (the object, for a sequence that is a property of one), and every use of the local is the chain over it,
    /// written here once (an alias, like a list read out of a field). C# reads the source's items at each use too;
    /// the report lists a local with game code between where it is declared and where it is last used, which is
    /// where that could show.</summary>
    private string LazyLocal(ILocalSymbol local, IOperation source, List<Chains.Step> steps, IOperation whole)
    {
        string name = NameOf(local), code, chain;
        LazyChainOf held;
        if (WalkOf(source) is { CallOn: { } callOn, ValueOn: { } valueOn } walk && Chains.Bare(source) is IPropertyReferenceOperation { Instance: var on })
        {
            code = on == null ? "me" : Expr(on);
            chain = valueOn(name);
            held = new LazyChainOf(null, new WalkSource(each => callOn(name, each), walk.Item, null, null), steps, true);
        }
        else
        {
            code = Expr(source);
            chain = $"{name}.clone()";
            held = new LazyChainOf(chain, null, steps, !IsDeferred(source));
        }
        foreach (var step in steps)
        {
            string f = step.Function == null ? "" : Closure(step.Function, 1);
            chain = step.Name switch
            {
                "OfType" => $"linq::of_type({chain}, {KindExpr(step.Type!)}, c)",
                "Cast" => chain,
                "Where" => $"linq::where_({chain}, {f}, c)",
                "Select" => $"linq::select({chain}, {f}, c)",
                "SelectMany" => $"linq::select_many({chain}, {f}, c)",
                "TakeWhile" => $"linq::take_while({chain}, {f}, c)",
                _ => $"linq::skip_while({chain}, {f}, c)",
            };
        }
        aliases[local] = chain;
        lazyLocals[local] = held;
        if (whole.Parent is IBlockOperation block)
        {
            var after = block.Operations.SkipWhile(o => o != whole).Skip(1).ToList();
            int last = after.FindLastIndex(o => o.DescendantsAndSelf().OfType<ILocalReferenceOperation>().Any(r => Eq.Equals(r.Local, local)));
            if (after.Take(Math.Max(last, 0)).Any(HoldsGameCall)) w.Rep.HeldChain(Where, whole);
        }
        return $"let mut {name} = {code};\n";
    }

    /// <summary>foreach over a call of a sequence written with yield. In C# the sequence's code and the loop's body
    /// take turns; here the loop's body is handed to the sequence, which runs it for each value as it reaches it. The
    /// body answers whether the walk ends (a break, or a return whose value waits in a variable outside).</summary>
    private void ForEachWalk(IForEachLoopOperation fe, ILocalSymbol local, WalkSource walk, LazyCode run, StringBuilder sb, string? only = null)
    {
        var f = frames.Peek();
        var scope = new Scope { Label = Tmp("w"), Frame = frames.Count, Walk = true };
        scopes.Push(scope);
        loops.Push(("return Ok(true);", run.Next));
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
        // The chain's operators run for each value before the body; a loop one of them opened closes after it.
        string each = $"&mut |{LazyFirst}: {walk.Item}, c: &mut Combat| -> R<bool> {{\n{run.Code}let mut {name}: {T(local.Type)} = {run.Item};\n{body}{new string('}', run.Nested)}\nOk(false)\n}}";
        string walked = walk.Call(each);
        sb.Append(run.Before);
        if (scope.Used) sb.Append($"let mut {scope.Label} = None;\n");
        if (only != null) sb.Append($"c.only = {only};\n");
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
    /// <summary>The place of the list a foreach walks, when C# walks a `List<T>` that code may change meanwhile: a field
    /// of an object reached the same way every time (or a property that returns one), a local or parameter set once.
    /// An array has no such check; a list made for the loop (ToList, a getter that copies) no one else holds.</summary>
    private string? EnumeratedList(IOperation collection)
    {
        var inner = collection;
        while (inner is IConversionOperation { IsTryCast: false } cv && World.CollOf(cv.Operand.Type) != World.Coll.None) inner = cv.Operand;
        static bool IsList(ITypeSymbol? t) => t is INamedTypeSymbol { OriginalDefinition: var d } && World.FullName(d) == "System.Collections.Generic.List`1";
        switch (inner)
        {
            case IFieldReferenceOperation f when IsList(f.Field.Type):
                return FieldPlaceOf(inner);
            case IPropertyReferenceOperation { Property: var p } when Forwarded(p.OriginalDefinition) is { } field && IsList(field.Type):
                return FieldPlaceOf(inner);
            case ILocalReferenceOperation l when IsList(l.Local.Type) && !reassigned.Contains(l.Local) && !droppedLocals.Contains(l.Local) && !nullLocals.Contains(l.Local) && !lazyLocals.ContainsKey(l.Local):
                return Ref(inner);
            case IParameterReferenceOperation pr when IsList(pr.Parameter.Type) && !reassigned.Contains(pr.Parameter):
                return Ref(inner);
            default:
                return null;
        }
    }

    /// <summary>EnumeratedList, with the object that holds the list reached once first where code reaches it
    /// (`Owner.PlayerCombatState.OrbQueue.Orbs`): the list's place, the statement that reaches the object, and the
    /// list as the walk takes it.</summary>
    private (string Place, string Pin, string Code)? ChangeableList(IOperation collection)
    {
        if (EnumeratedList(collection) is { } place) return (place, "", IterSource(collection));
        if (UnstableReceiver(collection) is not { } recv) return null;
        string r = Tmp("r"), code = Expr(recv);
        pinned[recv] = r;
        try
        {
            if (EnumeratedList(collection) is not { } pinnedPlace) return null;
            return (pinnedPlace, $"let {r} = {code};\n", IterSource(collection));
        }
        finally { pinned.Remove(recv); }
    }

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
            if (!HoldsGameCall(sw) && !WritesOuterLocal(sw) && NothingAfter(sw))
            {
                if (Leaving(sw) is { } how) w.Rep.Guard(Where, sw, $"a switch that leaves by {how}, nothing after it: left out");
                Leave(sw);
                return;
            }
            if (Leaving(sw) is { } how2) w.Rep.Guard(Where, sw, $"a switch that leaves by {how2}: not translated");
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

    /// <summary>`using var x = ..;`: the declaration, then the rest of the block, then the disposing of what it
    /// declared (Disposals).</summary>
    private void UsingDeclaration(IUsingDeclarationOperation ud, List<IOperation> rest, StringBuilder sb)
    {
        Stmt(ud.DeclarationGroup, sb);
        string fin = Disposals(Declared(ud.DeclarationGroup), ud.IsAsynchronous, ud);
        Scoped(inner =>
        {
            foreach (var o in rest)
            {
                dead = false;
                Stmt(o, inner);
                if (dead) break;
            }
        }, rest, fin, sb);
    }

    /// <summary>The locals a using declares, as resources: each with its code (none when it is presentation).</summary>
    private List<(string? Code, ITypeSymbol? Type, string Name)> Declared(IVariableDeclarationGroupOperation g) =>
        [.. g.Declarations.SelectMany(d => d.Declarators).Select(d => (droppedLocals.Contains(d.Symbol) || w.IsDropped(d.Symbol.Type) ? null : NameOf(d.Symbol), (ITypeSymbol?)d.Symbol.Type, d.Symbol.Name))];

    /// <summary>The disposing of a using's resources at the end of its scope, the last one first: a resource of a class
    /// of the game whose Dispose (DisposeAsync, for `await using`) is game code, when it is not null, as C# does. The
    /// rest (a lock's scope, a timer, a node guard, an animation's track) is left out. Each one is in the report.</summary>
    private string Disposals(IEnumerable<(string? Code, ITypeSymbol? Type, string Name)> resources, bool isAsync, IOperation where)
    {
        var sb = new StringBuilder();
        foreach (var (code, type, name) in resources.Reverse())
        {
            string what;
            var dispose = type is INamedTypeSymbol t && World.IsSource(t) ? DisposeOf(t, isAsync ? "DisposeAsync" : "Dispose") : null;
            if (code == null) what = "left out: presentation, or kept by hand";
            else if (dispose == null) what = $"{type?.Name} has no Dispose of the game's (left out)";
            else
            {
                try
                {
                    string call = $"{CallPath(dispose, dispose.IsVirtual || dispose.IsAbstract || dispose.IsOverride)}({code}, c)";
                    sb.Append($"if !rt::is_null(&{code}) {{\n{call}{(World.IsTask(dispose.ReturnType, out _) ? "?" : "")};\n}}\n");
                    what = $"{type!.Name}.{dispose.Name} at the end of its scope";
                }
                catch (Dropped) { what = $"{type!.Name}.{dispose.Name} is presentation (left out)"; }
            }
            w.Rep.Using(Where, where, $"{name}: {what}");
        }
        return sb.ToString();
    }

    private static IMethodSymbol? DisposeOf(INamedTypeSymbol t, string name)
    {
        for (INamedTypeSymbol? x = t; x != null; x = x.BaseType)
            if (x.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(m => !m.IsStatic && m.Parameters.Length == 0) is { } m) return m;
        return null;
    }

    /// <summary>Code with something to run at its end, as a finally block runs: a return inside leaves the code with
    /// its value, `fin` runs, then the member returns it. A stop for a card prompt leaves at once, as the game's task
    /// waits there without disposing; a break or continue out of it is not supported.</summary>
    private void Scoped(Action<StringBuilder> body, IReadOnlyList<IOperation> ops, string fin, StringBuilder sb)
    {
        if (fin.Length == 0) { body(sb); return; }
        foreach (var o in ops)
            foreach (var d in o.DescendantsAndSelf())
            {
                if (d is IReturnOperation { Kind: OperationKind.YieldReturn or OperationKind.YieldBreak } && !InsideLambda(d, o)) throw new Unsupported("yield inside a using that disposes");
                if (d is IBranchOperation { BranchKind: BranchKind.Break or BranchKind.Continue } br && !InsideLambda(d, o) && Leaves(br, o)) throw new Unsupported("a break out of a using that disposes");
            }
        bool returnsInside = ops.Any(o => o.DescendantsAndSelf().Any(d => d is IReturnOperation { Kind: OperationKind.Return } && !InsideLambda(d, o)));
        if (!returnsInside) { body(sb); sb.Append(fin); return; }
        string label = "'t" + tmp++, left = Tmp("left");
        scopes.Push(new Scope { Label = label, Frame = frames.Count });
        var inner = new StringBuilder();
        try { body(inner); }
        finally { scopes.Pop(); }
        sb.Append($"let {left} = {label}: {{\n{inner}None\n}};\n{fin}if let Some(__v) = {left} {{\n{LeaveWith("__v")}\n}}\n");
        Written++;
    }

    /// <summary>Whether a break or continue goes out of `root`: no loop or switch of its own is around it inside.</summary>
    private static bool Leaves(IBranchOperation br, IOperation root)
    {
        for (var p = br.Parent; p != null; p = p.Parent)
        {
            if (p is ILoopOperation or ISwitchOperation) return false;
            if (p == root) return true;
        }
        return true;
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
