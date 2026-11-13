using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Sts2Ai.Port;

/// <summary>LINQ chains as C# evaluates them: an operator like `Where` does nothing when it is called. It runs its
/// function for a value when the code that walks the chain asks for that value, so after that code has dealt with
/// the values before. What the translator needs to know of a chain to keep that order is here.</summary>
internal static class Chains
{
    /// <summary>The operators that hand values on as they are asked for (the others take everything first).</summary>
    public static readonly HashSet<string> Streaming =
    [
        "Where", "Select", "SelectMany", "TakeWhile", "SkipWhile", "Take", "Skip", "Concat", "Append", "Prepend", "Cast", "OfType", "Distinct", "DefaultIfEmpty",
        "Union", "Except", "Intersect", "Zip", "AsEnumerable",
    ];

    /// <summary>One operator a walk can run for each value itself: `Where`, `Select`, `SelectMany`, `TakeWhile`,
    /// `SkipWhile` with their function, `OfType` with its class, `Cast`.</summary>
    public sealed record Step(string Name, IOperation? Function, ITypeSymbol? Type);

    public static bool IsLinq(IMethodSymbol m) => m.ContainingType is { Name: "Enumerable", ContainingNamespace.Name: "Linq" };

    public static List<IOperation> Ordered(IInvocationOperation inv) => inv.Arguments.OrderBy(x => x.Parameter!.Ordinal).Select(x => x.Value).ToList();

    /// <summary>The expression under conversions that change nothing, parentheses and the compiler's form of a query.</summary>
    public static IOperation Bare(IOperation op)
    {
        while (true)
        {
            switch (op)
            {
                case IConversionOperation { IsTryCast: false } cv: op = cv.Operand; break;
                case ITranslatedQueryOperation tq: op = tq.Operation; break;
                case IParenthesizedOperation p: op = p.Operand; break;
                default: return op;
            }
        }
    }

    /// <summary>A chain split where a walk can take it over: the operators at its end that a walk runs itself
    /// (innermost first), and the sequence they are applied to.</summary>
    public static (IOperation Source, List<Step> Steps) Split(IOperation op, Func<ITypeSymbol, bool> isObject)
    {
        var steps = new List<Step>();
        while (Bare(op) is IInvocationOperation inv && IsLinq(inv.TargetMethod))
        {
            var a = Ordered(inv);
            string name = inv.TargetMethod.Name;
            if (name is "Where" or "Select" or "SelectMany" or "TakeWhile" or "SkipWhile" && a.Count == 2 && Body.ArityOf(a[1]) == 1) steps.Add(new Step(name, a[1], null));
            else if (name == "OfType" && a.Count == 1 && isObject(inv.TargetMethod.TypeArguments[0])) steps.Add(new Step(name, null, inv.TargetMethod.TypeArguments[0]));
            else if (name == "Cast" && a.Count == 1) steps.Add(new Step(name, null, null));
            else break;
            op = a[0];
        }
        steps.Reverse();
        return (op, steps);
    }

    /// <summary>The one expression a member's body gives, when the body is nothing else.</summary>
    public static IOperation? Returned(IOperation? body)
    {
        var block = body switch
        {
            IMethodBodyOperation mb => mb.BlockBody ?? mb.ExpressionBody,
            IBlockOperation b => b,
            _ => null,
        };
        if (block == null) return body is IReturnOperation { ReturnedValue: { } direct } ? direct : null;
        return block.Operations is [IReturnOperation { ReturnedValue: { } value }] ? value : null;
    }
}
