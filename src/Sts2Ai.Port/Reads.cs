using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Sts2Ai.Port;

// ---------------------------------------------------------------- what the kept walks read

public sealed partial class World
{
    private readonly object _memoLock = new();
    private HashSet<INamedTypeSymbol>? _memoClasses;

    /// <summary>The classes whose own fields a kept walk of port.toml [memo] may read where it builds its list or asks
    /// its test, or a kept getter anywhere in its body: through every call at any depth, every implementation of a
    /// virtual member, every getter. A field is
    /// written only through the `of_mut` of the class that declares it (gen/state.rs), and a new object changes none
    /// that is there, so a write by any other `of_mut`, and every `alloc`, leaves each kept list and answer as it is:
    /// only these classes' `of_mut` move the write count (rt/memo.rs). A walk that reads a static that is state, or runs
    /// code this cannot see into (a delegate it is handed), stops the translation: what it reads would not be known.</summary>
    public HashSet<INamedTypeSymbol> MemoClasses()
    {
        lock (_memoLock) return _memoClasses ??= MemoReads();
    }

    /// <summary>Whether a write to a field this class declares moves the write count.</summary>
    public bool CountsWrites(INamedTypeSymbol t) => MemoClasses().Contains(t.OriginalDefinition);

    private HashSet<INamedTypeSymbol> MemoReads()
    {
        var classes = new HashSet<INamedTypeSymbol>(Eq);
        var why = new Dictionary<INamedTypeSymbol, string>(Eq);
        var problems = new List<string>();
        var manual = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<IMethodSymbol>(Eq);
        var queue = new Queue<(IMethodSymbol Method, string From)>();

        // Every method of the game under the slot it fills, for the implementations of a virtual member.
        var bySlot = new Dictionary<ISymbol, List<IMethodSymbol>>(Eq);
        var sourceTypes = AllTypes.Where(IsSource).ToList();
        foreach (var t in sourceTypes)
            foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
            {
                var slot = Slot(m);
                if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = [];
                list.Add(m.OriginalDefinition);
            }

        IEnumerable<IMethodSymbol> Implementations(IMethodSymbol m)
        {
            yield return m;
            if (m.IsVirtual || m.IsAbstract || m.IsOverride)
                foreach (var o in bySlot.GetValueOrDefault(Slot(m)) ?? []) yield return o;
            if (m.ContainingType.TypeKind != TypeKind.Interface) yield break;
            var owner = m.ContainingType.OriginalDefinition;
            foreach (var t in sourceTypes)
                foreach (var i in t.AllInterfaces.Where(i => Eq.Equals(i.OriginalDefinition, owner)))
                    foreach (var im in i.GetMembers(m.Name).OfType<IMethodSymbol>().Where(x => Eq.Equals(x.OriginalDefinition, m)))
                        if (t.FindImplementationForInterfaceMember(im) is IMethodSymbol impl)
                        {
                            yield return impl.OriginalDefinition;
                            foreach (var o in bySlot.GetValueOrDefault(Slot(impl)) ?? []) yield return o;
                        }
        }

        void Read(INamedTypeSymbol t, string by)
        {
            t = t.OriginalDefinition;
            if (t.TypeKind == TypeKind.Class && !IsValue(t) && IsStateful(t) && classes.Add(t)) why[t] = by;
        }

        void Static(ISymbol member, string by)
        {
            if (StaticState(member) != null) problems.Add($"{by} reads the static {member.ContainingType.Name}.{member.Name}, which is state");
        }

        void Call(IMethodSymbol m, bool dispatched, string by)
        {
            m = m.OriginalDefinition.ReducedFrom?.OriginalDefinition ?? m.OriginalDefinition;
            if (!IsSource(m.ContainingType) || m.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) return;
            foreach (var x in dispatched ? Implementations(m) : [m])
                if (seen.Add(x)) queue.Enqueue((x, by));
        }

        string Name(ISymbol s) => $"{s.ContainingType?.Name}.{(s is IMethodSymbol { AssociatedSymbol: { } a } ? a.Name : s.Name)}";

        void Visit(IOperation root, string by)
        {
            foreach (var o in root.DescendantsAndSelf())
            {
                switch (o)
                {
                    case IFieldReferenceOperation { Field: var f }:
                        if (f.IsConst) break;
                        if (f.IsStatic) Static(f, by);
                        else Read(f.ContainingType, $"{by} reads {Name(f)}");
                        break;
                    case IEventReferenceOperation { Event: var e }:
                        if (e.IsStatic) Static(e, by);
                        else Read(e.ContainingType, $"{by} reads {Name(e)}");
                        break;
                    case IPropertyReferenceOperation { Property: var p }:
                    {
                        bool dispatched = p.IsVirtual || p.IsAbstract || p.IsOverride || p.ContainingType.TypeKind == TypeKind.Interface;
                        // The getter where the property is read, the setter where it is written; both for `+=` and `++`.
                        bool set = o.Parent is IAssignmentOperation { Target: var target } && target == o || o.Parent is IIncrementOrDecrementOperation;
                        bool get = o.Parent is not ISimpleAssignmentOperation { Target: var written } || written != o;
                        if (get && p.GetMethod != null) Call(p.GetMethod, dispatched, by);
                        if (set && p.SetMethod != null) Call(p.SetMethod, dispatched, by);
                        break;
                    }
                    case IInvocationOperation { TargetMethod: var m } inv:
                        if (m.MethodKind == MethodKind.DelegateInvoke) problems.Add($"{by} calls a delegate ({o.Syntax})");
                        else Call(m, inv.IsVirtual || m.ContainingType.TypeKind == TypeKind.Interface, by);
                        break;
                    case IMethodReferenceOperation { Method: var m } mr:
                        Call(m, mr.IsVirtual || m.ContainingType.TypeKind == TypeKind.Interface, by);
                        break;
                    // What is thrown ends the walk: an exception's own fields are no state.
                    case IObjectCreationOperation { Constructor: { } ctor } when IsSource(ctor.ContainingType) && !Thrown(o):
                        problems.Add($"{by} makes an object of the game ({o.Syntax})");
                        break;
                    case IArgumentOperation { Parameter: { Type.TypeKind: TypeKind.Delegate } param, Value: var v }
                        when !IsSource(param.ContainingType) && Bare(v) is not (IDelegateCreationOperation or IAnonymousFunctionOperation or ILiteralOperation):
                        problems.Add($"{by} hands a delegate it did not make to {Name(param.ContainingSymbol)} ({o.Syntax})");
                        break;
                    case IConversionOperation { OperatorMethod: { } om }: Call(om, false, by); break;
                    case IBinaryOperation { OperatorMethod: { } om }: Call(om, false, by); break;
                    case IUnaryOperation { OperatorMethod: { } om }: Call(om, false, by); break;
                    case IIncrementOrDecrementOperation { OperatorMethod: { } om }: Call(om, false, by); break;
                    case ICompoundAssignmentOperation { OperatorMethod: { } om }: Call(om, false, by); break;
                    case IForEachLoopOperation { Collection: var coll } when coll.Type is INamedTypeSymbol ct && IsSource(ct):
                        problems.Add($"{by} walks a collection of the game's own ({coll.Syntax})");
                        break;
                }
            }
        }

        static bool Thrown(IOperation op)
        {
            while (op.Parent is IConversionOperation) op = op.Parent;
            return op.Parent is IThrowOperation;
        }

        static IOperation Bare(IOperation op)
        {
            while (op is IConversionOperation cv) op = cv.Operand;
            return op;
        }

        // The walks: the statements that build the list, and the test.
        foreach (var t in sourceTypes)
            foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
            {
                string name = $"{t.Name}.{m.Name}";
                if (!Cfg.Memo.Contains(name)) continue;
                if (BodyOf(m) is not IMethodBodyOperation { BlockBody: { } block }) { problems.Add($"{name} has no body"); continue; }
                var ops = block.Operations;
                int at = ops.ToList().FindIndex(o => o is IForEachLoopOperation { Collection: var coll } && Bare(coll) is ILocalReferenceOperation);
                var body = at < 0 ? null : ((IForEachLoopOperation)ops[at]).Body;
                if (body == null || (body is IBlockOperation { Operations.Length: 1 } one ? one.Operations[0] : body) is not IConditionalOperation test)
                {
                    problems.Add($"{name} is not a kept walk");
                    continue;
                }
                // Only the kept part here; called from elsewhere, the walk is read whole.
                foreach (var o in ops.Take(at)) Visit(o, name);
                Visit(((IForEachLoopOperation)ops[at]).Collection, name);
                Visit(test.Condition, name);
            }

        // The kept getters: all of each.
        foreach (var t in sourceTypes)
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
            {
                string name = $"{t.Name}.{p.Name}";
                if (!Cfg.MemoGetters.Contains(name)) continue;
                if (p.IsStatic || p.IsVirtual || p.IsAbstract || p.IsOverride || p.Parameters.Length > 0 || p.GetMethod == null || !IsObj(p.Type) || !IsStateful(t))
                    problems.Add($"{name} is not a getter of an object, not virtual, that gives an object");
                else Call(p.GetMethod, false, name);
            }

        while (queue.Count > 0)
        {
            var (m, from) = queue.Dequeue();
            string by = $"{Name(m)} (from {from})";
            if (CatOfMember(m) == Cat.Manual) manual.Add(Name(m));
            // An auto-property's accessor reads or writes its field.
            if (m.AssociatedSymbol is IPropertySymbol p && p.ContainingType.GetMembers().OfType<IFieldSymbol>().Any(f => Eq.Equals(f.AssociatedSymbol, p)))
            {
                if (p.IsStatic) Static(p, by);
                else Read(p.ContainingType, $"{by} reads {Name(p)}");
                continue;
            }
            if (BodyOf(m) is { } op) Visit(op, Name(m));
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("port.toml [memo]: what a kept walk reads is not known:\n  " + string.Join("\n  ", problems.Distinct().Take(20)));
        foreach (var t in classes.OrderBy(Unique, StringComparer.Ordinal)) Rep.MemoClasses.Add($"{Unique(t)}: {why[t]}");
        Rep.MemoManual.AddRange(manual);
        Rep.MemoMethods = seen.Count;
        return classes;
    }

    /// <summary>The virtual members a loop over a walk calls on each value where the walk may pass over the kinds
    /// that do not override them (Body.OnlyFor): each gets a test of its kinds beside its dispatcher.</summary>
    public readonly ConcurrentDictionary<ISymbol, byte> OnlyFilters = new(Eq);

    public IOperation? MemberBody(IMethodSymbol m) => BodyOf(m);

    /// <summary>A method's body as one operation, or null when it has none here (abstract, extern, an auto-accessor).</summary>
    private IOperation? BodyOf(IMethodSymbol m)
    {
        var syntax = m.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (syntax == null) return null;
        var model = Comp.GetSemanticModel(syntax.SyntaxTree);
        return syntax switch
        {
            ArrowExpressionClauseSyntax arrow => model.GetOperation(arrow) ?? model.GetOperation(arrow.Expression),
            AccessorDeclarationSyntax { Body: null, ExpressionBody: null } => null,
            AccessorDeclarationSyntax acc => model.GetOperation(acc),
            BaseMethodDeclarationSyntax { Body: null, ExpressionBody: null } => null,
            BaseMethodDeclarationSyntax md => model.GetOperation(md),
            _ => null,
        };
    }
}
