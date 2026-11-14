using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Sts2Ai.Port;

// ---------------------------------------------------------------- slots of the schema that could hold a value

public sealed partial class World
{
    /// <summary>The fields of the schema's classes and structs whose declared type can hold a value that an export
    /// would write as a value, not a reference: `object`, or an interface that a string or a value type implements
    /// (of a collection, its items, keys or values). The engine holds references there (Obj), so each is settled:
    /// dropped (port.toml, or presentation), holding objects of classes only (every write in the game's code gives
    /// one), or not settled, where an export that holds a value there is refused.</summary>
    public List<string> ValueSlots()
    {
        var valueInterfaces = new HashSet<INamedTypeSymbol>(Eq);
        void Implemented(ITypeSymbol? t)
        {
            if (t == null) return;
            foreach (var i in t.AllInterfaces) valueInterfaces.Add(i.OriginalDefinition);
        }
        foreach (var st in new[] { SpecialType.System_String, SpecialType.System_Int32, SpecialType.System_Int64, SpecialType.System_Boolean,
                     SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_Decimal, SpecialType.System_Char, SpecialType.System_Byte,
                     SpecialType.System_UInt32, SpecialType.System_UInt64, SpecialType.System_Int16, SpecialType.System_Enum })
            Implemented(Comp.GetSpecialType(st));
        foreach (var t in AllTypes.Where(t => IsSource(t) && t.TypeKind is TypeKind.Struct or TypeKind.Enum)) Implemented(t);

        string? Holds(ITypeSymbol t) => t switch
        {
            { SpecialType: SpecialType.System_Object } => "object",
            IArrayTypeSymbol a => Holds(a.ElementType) is { } e ? $"its items, {e}" : null,
            INamedTypeSymbol n when CollOf(n) != Coll.None && n.TypeArguments.Length > 0 => n.TypeArguments
                .Select((x, i) => Holds(x) is { } e ? $"its {(n.TypeArguments.Length == 2 ? (i == 0 ? "keys" : "values") : "items")}, {e}" : null)
                .FirstOrDefault(x => x != null),
            INamedTypeSymbol { TypeKind: TypeKind.Interface } n when valueInterfaces.Contains(n.OriginalDefinition) => n.ToDisplayString(),
            _ => null,
        };

        // The schema's classes, and the values (structs, value classes) their kept fields hold, at any depth.
        var inSchema = new HashSet<INamedTypeSymbol>(StatefulClasses(), Eq);
        var queue = new Queue<INamedTypeSymbol>(inSchema);
        void Reach(ITypeSymbol? t)
        {
            switch (t)
            {
                case IArrayTypeSymbol a: Reach(a.ElementType); break;
                case INamedTypeSymbol n:
                    foreach (var x in n.TypeArguments) Reach(x);
                    if (IsSource(n) && IsValue(n) && inSchema.Add(n.OriginalDefinition)) queue.Enqueue(n.OriginalDefinition);
                    break;
            }
        }
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            foreach (var kept in Fields(t)) if (kept.Field is IFieldSymbol kf) Reach(kf.Type);
        }
        var lines = new List<string>();
        foreach (var t in inSchema.Where(t => IsSource(t) && !IsDroppedNamed(t)).OrderBy(FullName, StringComparer.Ordinal))
            foreach (var f in t.GetMembers().OfType<IFieldSymbol>())
            {
                if (f.IsStatic || f.IsConst || f.AssociatedSymbol is IEventSymbol || Holds(f.Type) is not { } what) continue;
                string name = $"{t.Name}.{f.AssociatedSymbol?.Name ?? f.Name}";
                string settled = StateField(f) == null ? "dropped (port.toml or presentation): not in the schema"
                    : ObjectsOnly(f) is { } how ? $"holds objects only: {how}"
                    : "NOT SETTLED: an export that holds a value there is refused (outside)";
                lines.Add($"{name} ({f.Type.ToDisplayString()}: {what}): {settled}");
            }
        return lines;
    }

    /// <summary>How every write of the field in the game's code gives an object of a class (or null): its
    /// initializer and each assignment, and through a call, every body of the method called; null where one may not
    /// (a collection, whose items are written elsewhere, is not looked into).</summary>
    private string? ObjectsOnly(IFieldSymbol f)
    {
        if (CollOf(f.Type) != Coll.None || f.Type is IArrayTypeSymbol) return null;
        ISymbol named = f.AssociatedSymbol ?? f;
        bool IsTarget(IOperation target) => target switch
        {
            IFieldReferenceOperation fr => Eq.Equals(fr.Field.OriginalDefinition, f.OriginalDefinition),
            IPropertyReferenceOperation pr => Eq.Equals(pr.Property.OriginalDefinition, named.OriginalDefinition),
            _ => false,
        };
        static IOperation Bare(IOperation v)
        {
            while (v is IConversionOperation cv) v = cv.Operand;
            return v;
        }
        static bool ClassObject(IOperation v) => Bare(v) is var b && (b.ConstantValue is { HasValue: true, Value: null }
            || b.Type is INamedTypeSymbol { TypeKind: TypeKind.Class, SpecialType: SpecialType.None });
        var said = new SortedSet<string>(StringComparer.Ordinal);
        bool Gives(IOperation value)
        {
            if (ClassObject(value)) { said.Add($"{Bare(value).Syntax}"); return true; }
            if (Bare(value) is not IInvocationOperation { TargetMethod: var called } || !IsSource(called.ContainingType)) return false;
            // A call: every body of the method (it and its overrides) returns an object of a class.
            var slot = called.OriginalDefinition;
            var bodies = AllTypes.Where(IsSource).SelectMany(x => x.GetMembers(slot.Name).OfType<IMethodSymbol>())
                .Where(m => Eq.Equals(m.OriginalDefinition, slot) || Overrides(m, slot)).Where(m => !m.IsAbstract).ToList();
            foreach (var m in bodies)
            {
                if (BodyOf(m) is not { } body) return false;
                var returns = body.DescendantsAndSelf().OfType<IReturnOperation>().Where(r => r.ReturnedValue != null).ToList();
                if (returns.Count == 0 || !returns.All(r => ClassObject(r.ReturnedValue!))) return false;
            }
            said.Add($"{called.ContainingType.Name}.{called.Name}(), whose {bodies.Count} bodies each return an object of a class");
            return true;
        }
        int writes = 0;
        foreach (var r in f.DeclaringSyntaxReferences.Concat(named.DeclaringSyntaxReferences))
        {
            var syntax = r.GetSyntax();
            var init = syntax switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax v => (SyntaxNode?)v.Initializer?.Value,
                Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax p => p.Initializer?.Value,
                _ => null,
            };
            if (init == null) continue;
            if (Comp.GetSemanticModel(init.SyntaxTree).GetOperation(init) is not { } value || !Gives(value)) return null;
            writes++;
        }
        foreach (var t in AllTypes.Where(IsSource))
            foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
            {
                if (!m.DeclaringSyntaxReferences.Any(s => s.GetSyntax().ToString().Contains(named.Name, StringComparison.Ordinal))) continue;
                if (BodyOf(m) is not { } body) continue;
                foreach (var a in body.Descendants().OfType<IAssignmentOperation>().Where(a => IsTarget(a.Target)))
                {
                    if (a is not ISimpleAssignmentOperation simple || !Gives(simple.Value)) return null;
                    writes++;
                }
            }
        return writes == 0 ? "never written" : $"{writes} writes: {string.Join("; ", said)}";
    }

    private static bool Overrides(IMethodSymbol m, IMethodSymbol slot)
    {
        for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
            if (Eq.Equals(o.OriginalDefinition, slot)) return true;
        return false;
    }
}
