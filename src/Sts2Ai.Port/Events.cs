using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Sts2Ai.Port;

/// <summary>Events. An event of the game is a list of delegates, called in the order they subscribed. Most are how the
/// game tells its screen that something changed, and are presentation: nothing of them is kept. An event with a
/// subscriber that is game code is state (DESIGN.md, "Events"): who is subscribed decides what the game does, so the
/// list is a field of the class, `+=` and `-=` change it, and raising the event calls it.</summary>
public sealed partial class World
{
    /// <summary>A place in the game's source that subscribes to an event of a class the engine has: the member the
    /// place is in, and what is subscribed (a method by its name, or code written at the place).</summary>
    public sealed record Subscription(IEventSymbol Event, ISymbol? Where, IMethodSymbol? Method, string Text);

    /// <summary>Every such place of the game's source, found once.</summary>
    public List<Subscription> Subscriptions = [];

    /// <summary>The events that are state, as things stand in this pass (a subscriber found to be presentation as a
    /// whole stops counting in the next).</summary>
    public HashSet<ISymbol> StateEvents = new(Eq);

    public void FindSubscriptions()
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<Subscription>();
        Parallel.ForEach(Comp.SyntaxTrees, tree =>
        {
            var adds = tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)).ToList();
            if (adds.Count == 0) return;
            var model = Comp.GetSemanticModel(tree);
            foreach (var add in adds)
            {
                if (model.GetOperation(add) is not IEventAssignmentOperation { Adds: true, EventReference: IEventReferenceOperation { Event: var ev } } subscription) continue;
                var owner = ev.ContainingType.OriginalDefinition;
                if (!IsSource(owner) || CatOf(owner) is not (Cat.Translated or Cat.Manual)) continue;
                var where = model.GetEnclosingSymbol(add.SpanStart);
                while (where is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction }) where = where.ContainingSymbol;
                var handler = subscription.HandlerValue;
                while (handler is IConversionOperation cv) handler = cv.Operand;
                if (handler is IDelegateCreationOperation dc) handler = dc.Target;
                // What runs: a method by its name, or code written at the place (it is then the subscribing member's).
                var method = (handler as IMethodReferenceOperation)?.Method.OriginalDefinition;
                string text = method != null ? $"{method.ContainingType.Name}.{method.Name}" : handler is IAnonymousFunctionOperation ? "a lambda" : handler.Syntax.ToString();
                found.Add(new((IEventSymbol)ev.OriginalDefinition, where, method, text));
            }
        });
        Subscriptions = [.. found.OrderBy(s => $"{s.Event.ContainingType.Name}.{s.Event.Name} {s.Where?.ContainingType?.Name}.{s.Where?.Name} {s.Text}", StringComparer.Ordinal)];
    }

    /// <summary>What a subscriber is: game code (a member of a translated or hand-written class that is not
    /// presentation), a presentation member of such a class, presentation, or a class outside the translated scope.</summary>
    public string SubscriberKind(Subscription s)
    {
        var runs = s.Method?.ContainingType?.OriginalDefinition ?? s.Where?.ContainingType?.OriginalDefinition;
        if (runs == null) return "unknown";
        if (IsDroppedNamed(runs)) return "presentation";
        if (CatOf(runs) is not (Cat.Translated or Cat.Manual)) return "outside the translated scope";
        if ((s.Method ?? s.Where) is { } member && IsDropMember(member)) return "a presentation member";
        return "game code";
    }

    /// <summary>Whether port.toml says an event is not state ([drop] fields, as Class.Event).</summary>
    public bool IsDroppedEvent(IEventSymbol ev) => Cfg.DropFields.Contains($"{ev.ContainingType.Name}.{ev.Name}");

    /// <summary>Sets the events that are state: an instance event with a field behind it (one declared without
    /// accessors of its own) that port.toml does not drop, with a subscriber that is game code.</summary>
    public void FindStateEvents()
    {
        StateEvents = new(Eq);
        foreach (var s in Subscriptions)
            if (s.Event is { IsStatic: false, AddMethod.IsImplicitlyDeclared: true } && !IsDroppedEvent(s.Event) && SubscriberKind(s) == "game code")
                StateEvents.Add(s.Event);
        _fieldNames.Clear();
    }
}
