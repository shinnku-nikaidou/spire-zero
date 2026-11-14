using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Sts2Ai.Port;

// ---------------------------------------------------------------- the game's classes that say what equal is

public sealed partial class World
{
    private readonly ConcurrentDictionary<ITypeSymbol, string?> _ownEquality = new(Eq);
    private List<(INamedTypeSymbol Type, string How)>? _equalityTypes;

    /// <summary>The game's types whose equality is their own rather than the object's identity: a class or struct that
    /// overrides `Equals(object)`, or has an `operator ==`, and every record (C# writes a record's Equals, GetHashCode
    /// and == over its fields).</summary>
    public List<(INamedTypeSymbol Type, string How)> EqualityTypes()
    {
        lock (_memoLock)
        {
            if (_equalityTypes != null) return _equalityTypes;
            var found = new List<(INamedTypeSymbol, string)>();
            foreach (var t in AllTypes.Where(IsSource))
            {
                var how = new List<string>();
                if (t.IsRecord) how.Add("a record");
                if (t.GetMembers("Equals").OfType<IMethodSymbol>().Any(m => m.IsOverride && !m.IsImplicitlyDeclared && m.Parameters is [{ Type.SpecialType: SpecialType.System_Object }]))
                    how.Add("Equals(object)");
                if (t.GetMembers("Equals").OfType<IMethodSymbol>().Any(m => !m.IsImplicitlyDeclared && !m.IsStatic && m.Parameters is [{ Type: var p }] && Eq.Equals(p.OriginalDefinition, t)))
                    how.Add($"Equals({t.Name})");
                if (t.GetMembers("GetHashCode").OfType<IMethodSymbol>().Any(m => m.IsOverride && !m.IsImplicitlyDeclared)) how.Add("GetHashCode");
                if (t.GetMembers("op_Equality").OfType<IMethodSymbol>().Any(m => !m.IsImplicitlyDeclared)) how.Add("operator ==");
                if (how.Count > 0) found.Add((t, string.Join(", ", how)));
            }
            return _equalityTypes = found;
        }
    }

    /// <summary>What equal is for values of this type when C# compares them by `Equals` (a collection's Remove,
    /// Contains, a set or a dictionary's key, `object.Equals`): the name of the type of the game, this one or one
    /// below it, whose own equality may answer; null when identity answers for every value (or the value is of the
    /// library).</summary>
    public string? OwnEquality(ITypeSymbol? t)
    {
        if (t == null) return null;
        return _ownEquality.GetOrAdd(t, t0 =>
        {
            var own = EqualityTypes().Where(e => Below(e.Type, t0)).Select(e => e.Type.Name).ToList();
            return own.Count == 0 ? null : string.Join(", ", own);
        });
    }

    /// <summary>A value here is equal to another when all its fields are. For a value type of the game whose Equals or
    /// == is written by hand, the fields those leave out (by name: a field or an auto-property that none of their
    /// bodies names), where the two answers can differ; empty where they name every field, as a record's own do.</summary>
    public List<string> EqualityLeftOut(INamedTypeSymbol t)
    {
        var written = t.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.Name is "Equals" or "op_Equality" && !m.IsImplicitlyDeclared && m.DeclaringSyntaxReferences.Length > 0).ToList();
        if (written.Count == 0) return [];
        var named = written.SelectMany(m => m.DeclaringSyntaxReferences)
            .SelectMany(r => r.GetSyntax().DescendantTokens())
            .Where(k => k.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken)).Select(k => k.Text).ToHashSet(StringComparer.Ordinal);
        return t.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst)
            .Select(f => f.AssociatedSymbol?.Name ?? f.Name).Where(n => !named.Contains(n)).Order(StringComparer.Ordinal).ToList();
    }

    private static bool Below(INamedTypeSymbol t, ITypeSymbol of)
    {
        var target = of.OriginalDefinition;
        for (INamedTypeSymbol? x = t; x != null; x = x.BaseType)
            if (Eq.Equals(x.OriginalDefinition, target)) return true;
        return t.AllInterfaces.Any(i => Eq.Equals(i.OriginalDefinition, target)) || of.SpecialType == SpecialType.System_Object && t.TypeKind == TypeKind.Class;
    }
}
