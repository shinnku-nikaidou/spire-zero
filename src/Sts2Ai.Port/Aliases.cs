using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Sts2Ai.Port;

// ---------------------------------------------------------------- collections the state holds, under a second name

public sealed partial class World
{
    /// <summary>Where a collection local of a body gets its value: every value it is set to (an initializer, an
    /// assignment, an out argument with its call, a foreach over the collection it is an item of).</summary>
    public static Dictionary<ILocalSymbol, List<IOperation>> CollectionSources(IOperation body)
    {
        var sets = new Dictionary<ILocalSymbol, List<IOperation>>(SymbolEqualityComparer.Default);
        void Add(ILocalSymbol l, IOperation v)
        {
            if (CollOf(l.Type) == Coll.None) return;
            if (!sets.TryGetValue(l, out var list)) sets[l] = list = [];
            list.Add(v);
        }
        foreach (var d in body.DescendantsAndSelf())
            switch (d)
            {
                case IVariableDeclaratorOperation { Symbol: var l } vd when (vd.Initializer?.Value ?? (vd.Parent as IVariableDeclarationOperation)?.Initializer?.Value) is { } init:
                    Add(l, init);
                    break;
                case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var t }, Value: var v }:
                    Add(t, v);
                    break;
                case IArgumentOperation { Parameter.RefKind: RefKind.Out, Value: var ov } arg when arg.Parent is IInvocationOperation call
                    && (ov is IDeclarationExpressionOperation de ? de.Expression : ov) is ILocalReferenceOperation { Local: var o }:
                    Add(o, call);
                    break;
                case IForEachLoopOperation fe:
                    foreach (var l in fe.Locals) Add(l, fe);
                    break;
            }
        return sets;
    }

    private readonly ConcurrentDictionary<ISymbol, bool> _handsOut = new(Eq);
    private readonly ThreadLocal<HashSet<ISymbol>> _handsOutAsked = new(() => new HashSet<ISymbol>(Eq));

    /// <summary>Whether a method of the game may return a collection the state holds (a field's list, an element of
    /// a stored collection, or what a method it calls hands out), rather than one of its own making.</summary>
    public bool HandsOutState(IMethodSymbol m0)
    {
        var m = m0.OriginalDefinition.ReducedFrom?.OriginalDefinition ?? m0.OriginalDefinition;
        if (!IsSource(m.ContainingType) || CollOf(m.ReturnType) == Coll.None) return false;
        if (_handsOut.TryGetValue(m, out bool known)) return known;
        if (!_handsOutAsked.Value!.Add(m)) return false;
        try
        {
            bool found = false;
            if (m.AssociatedSymbol is IPropertySymbol p && p.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => Eq.Equals(f.AssociatedSymbol, p)))
                found = true;
            else if (BodyOf(m) is { } body)
            {
                var sources = CollectionSources(body);
                found = Returns(body).Any(v => HoldsState(v, sources, new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default)));
            }
            _handsOut[m] = found;
            return found;
        }
        finally { _handsOutAsked.Value!.Remove(m); }
    }

    /// <summary>What a body returns: its return statements' values, not those of the lambdas in it; nothing for a
    /// body written with yield (its sequence is its own).</summary>
    public static IEnumerable<IOperation> Returns(IOperation body)
    {
        var all = body.DescendantsAndSelf().OfType<IReturnOperation>().ToList();
        if (all.Any(r => r.Kind == OperationKind.YieldReturn)) yield break;
        foreach (var r in all)
        {
            if (r.ReturnedValue == null) continue;
            bool inLambda = false;
            for (var x = r.Parent; x != null && x != body; x = x.Parent)
                if (x is IAnonymousFunctionOperation or ILocalFunctionOperation) { inLambda = true; break; }
            if (!inLambda) yield return r.ReturnedValue;
        }
    }

    private static readonly HashSet<string> ElementTakers =
    [
        "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault", "ElementAt", "ElementAtOrDefault",
        "GetValueOrDefault", "Peek", "Dequeue", "Pop", "Find", "FindLast", "MaxBy", "MinBy", "TryGetValue", "TryDequeue",
        "TryPop", "TryPeek", "Value",
    ];

    /// <summary>Whether a value may be a collection the state holds: C# then has one collection under two names, and
    /// a change through either is seen through the other.</summary>
    public bool HoldsState(IOperation v, Dictionary<ILocalSymbol, List<IOperation>> sources, HashSet<ILocalSymbol> asked)
    {
        while (v is IConversionOperation cv) v = cv.Operand;
        switch (v)
        {
            case IFieldReferenceOperation f:
                return CollOf(f.Type) != Coll.None && !f.Field.IsConst;
            case IPropertyReferenceOperation pr:
                if (CollOf(pr.Type) == Coll.None) return false;
                if (pr.Property.IsIndexer) return true;
                if (!IsSource(pr.Property.ContainingType)) return ElementTakers.Contains(pr.Property.Name) || pr.Property.Name is "Values" or "Keys";
                return pr.Property.GetMethod is { } get && HandsOutState(get);
            case IArrayElementReferenceOperation ae:
                return CollOf(ae.Type) != Coll.None;
            case IInvocationOperation inv:
                if (CollOf(inv.Type) == Coll.None && inv.TargetMethod.Name is not ("TryGetValue" or "TryDequeue" or "TryPop" or "TryPeek")) return false;
                if (IsSource(inv.TargetMethod.ContainingType)) return HandsOutState(inv.TargetMethod);
                return ElementTakers.Contains(inv.TargetMethod.Name);
            case IForEachLoopOperation fe:
                return true;
            case ILocalReferenceOperation l:
                if (!asked.Add(l.Local)) return false;
                return sources.TryGetValue(l.Local, out var vs) && vs.Any(x => HoldsState(x, sources, asked));
            case IConditionalOperation c:
                return HoldsState(c.WhenTrue, sources, asked) || c.WhenFalse != null && HoldsState(c.WhenFalse, sources, asked);
            case ICoalesceOperation co:
                return HoldsState(co.Value, sources, asked) || HoldsState(co.WhenNull, sources, asked);
            default:
                return false;
        }
    }

    /// <summary>Where a collection local is changed in place within the code: a change of its own (Add, Clear, an
    /// element set), or a call that changes it as a parameter or takes it by ref.</summary>
    public List<IOperation> ChangesOf(ILocalSymbol l, IOperation within)
    {
        static bool Is(IOperation? x, ILocalSymbol l)
        {
            while (x is IConversionOperation cv) x = cv.Operand;
            return x is ILocalReferenceOperation r && SymbolEqualityComparer.Default.Equals(r.Local, l);
        }
        var changes = new List<IOperation>();
        foreach (var d in within.DescendantsAndSelf())
            switch (d)
            {
                case IInvocationOperation inv when !IsSource(inv.TargetMethod.ContainingType) && Mutators.Contains(inv.TargetMethod.Name) && Is(inv.Instance, l):
                    changes.Add(d);
                    break;
                case IInvocationOperation inv when !IsSource(inv.TargetMethod.ContainingType) && inv.TargetMethod.IsExtensionMethod && Mutators.Contains(inv.TargetMethod.Name)
                    && inv.Arguments.Length > 0 && Is(inv.Arguments[0].Value, l):
                    changes.Add(d);
                    break;
                case IAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true, Instance: var ix } } when Is(ix, l):
                case IAssignmentOperation { Target: IArrayElementReferenceOperation { ArrayReference: var ar } } when Is(ar, l):
                    changes.Add(d);
                    break;
                case IArgumentOperation { Parameter: { } p, Value: var v } when Is(v, l) && (p.RefKind == RefKind.Ref || IsChangedParam(p)):
                    changes.Add(d);
                    break;
            }
        return changes;
    }

    /// <summary>Where a collection local is stored into the state (assigned to a field, an element or a parameter
    /// that goes back to the caller, added to a collection, handed to a constructor) and changed after: C# then holds
    /// one collection under two names, and the change is seen through the other. A change after is one later in the
    /// text, or one in a loop around the store.</summary>
    public IOperation? StoredThenChanged(ILocalSymbol l, IOperation body)
    {
        static bool Is(IOperation? x, ILocalSymbol l)
        {
            while (x is IConversionOperation cv) x = cv.Operand;
            return x is ILocalReferenceOperation r && SymbolEqualityComparer.Default.Equals(r.Local, l);
        }
        var stores = new List<IOperation>();
        var changes = new List<IOperation>();
        foreach (var d in body.DescendantsAndSelf())
            switch (d)
            {
                case ISimpleAssignmentOperation { Target: not ILocalReferenceOperation, Value: var v } when Is(v, l):
                    stores.Add(d);
                    break;
                case IInvocationOperation { TargetMethod.Name: "Add" or "Insert" or "Push" or "Enqueue" or "TryAdd" or "AddLast" or "AddFirst" } inv
                    when !IsSource(inv.TargetMethod.ContainingType) && inv.Arguments.Any(a => Is(a.Value, l)):
                    stores.Add(d);
                    break;
                case IObjectCreationOperation oc when oc.Arguments.Any(a => Is(a.Value, l)) && oc.Constructor != null && IsSource(oc.Constructor.ContainingType):
                    stores.Add(d);
                    break;
            }
        changes.AddRange(ChangesOf(l, body));
        static IEnumerable<IOperation> Loops(IOperation o)
        {
            for (var x = o.Parent; x != null; x = x.Parent)
                if (x is ILoopOperation) yield return x;
        }
        // A loop the local is declared in makes a new collection each round: a change in a later round is not to it.
        var declared = body.DescendantsAndSelf().OfType<IVariableDeclaratorOperation>().FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, l));
        var fresh = declared == null ? [] : Loops(declared).ToHashSet();
        foreach (var st in stores)
        {
            var around = Loops(st).Where(x => !fresh.Contains(x)).ToHashSet();
            if (changes.Any(ch => ch.Syntax.SpanStart > st.Syntax.Span.End || Loops(ch).Any(around.Contains))) return st;
        }
        return null;
    }
}
