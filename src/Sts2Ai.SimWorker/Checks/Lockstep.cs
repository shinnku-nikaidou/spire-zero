using System.Text.Json;
using Sts2Ai.Brain;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Checks;

/// <summary>
/// What a combat shows where the player decides, or at its end: the three things the lockstep check compares. The
/// observation is the JSON the brain would get, byte for byte; the plays are in the game's order.
/// </summary>
internal sealed record Shown(string Observation, IReadOnlyList<string> Legal, CombatStatus Status)
{
    public static Shown Of(CombatSession real) => new(
        ProtocolJson.Serialize(real.Observation()),
        CombatSession.IsOver ? [] : [.. real.LegalActions().Select(a => a.ToString())],
        real.Outcome switch { CombatOutcome.InProgress => CombatStatus.InProgress, CombatOutcome.Loss => CombatStatus.Lost, _ => CombatStatus.Won });

    /// <summary>
    /// The first thing <paramref name="world"/> shows otherwise: in its observation (the first JSON path, in the
    /// document's order), else in its plays ("legal[i]"), else in its status ("status"). A lost combat is compared by
    /// its status alone: a loss is worth nothing whatever it holds, and nothing reads the rest.
    /// </summary>
    public (string Path, string? Real, string? World)? FirstDifference(SecondWorld world)
    {
        var status = world.World.Status;
        if (Status == CombatStatus.Lost) return status == Status ? null : ("status", Status.ToString(), status.ToString());
        if (JsonDiff.First(Observation, world.Observation()) is { } difference) return difference;
        var legal = world.World.Legal();
        for (int i = 0; i < Math.Max(Legal.Count, legal.Count); i++)
        {
            var (real, other) = (i < Legal.Count ? Legal[i] : null, i < legal.Count ? legal[i].ToString() : null);
            if (real != other) return ($"legal[{i}]", real, other);
        }
        return status == Status ? null : ("status", Status.ToString(), status.ToString());
    }
}

/// <summary>The first place two JSON texts differ.</summary>
internal static class JsonDiff
{
    /// <summary>
    /// Null when the texts are the same bytes. Else the first path, in <paramref name="real"/>'s order, where they
    /// differ, and each side's JSON there: null where a side has nothing (a member left out, a shorter list). Texts that
    /// hold the same values in other bytes (member order, a number's or a string's spelling) differ too.
    /// </summary>
    public static (string Path, string? Real, string? World)? First(string real, string world)
    {
        if (real == world) return null;
        using var a = JsonDocument.Parse(real);
        using var b = JsonDocument.Parse(world);
        return First(a.RootElement, b.RootElement, "") ?? ("", "(the same values in other bytes)", world);
    }

    private static (string, string?, string?)? First(JsonElement a, JsonElement b, string path)
    {
        if (a.ValueKind != b.ValueKind) return (path, a.GetRawText(), b.GetRawText());
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in a.EnumerateObject())
                {
                    string at = path.Length == 0 ? member.Name : $"{path}.{member.Name}";
                    if (!b.TryGetProperty(member.Name, out var other)) return (at, member.Value.GetRawText(), null);
                    if (First(member.Value, other, at) is { } inside) return inside;
                }
                foreach (var member in b.EnumerateObject())
                    if (!a.TryGetProperty(member.Name, out _)) return (path.Length == 0 ? member.Name : $"{path}.{member.Name}", null, member.Value.GetRawText());
                var (names, others) = (a.EnumerateObject().Select(m => m.Name).ToList(), b.EnumerateObject().Select(m => m.Name).ToList());
                return names.SequenceEqual(others) ? null : (path, $"(members in the order {string.Join(",", names)})", $"(members in the order {string.Join(",", others)})");
            case JsonValueKind.Array:
                int length = Math.Min(a.GetArrayLength(), b.GetArrayLength());
                for (int i = 0; i < length; i++)
                    if (First(a[i], b[i], $"{path}[{i}]") is { } inside) return inside;
                if (a.GetArrayLength() > length) return ($"{path}[{length}]", a[length].GetRawText(), null);
                if (b.GetArrayLength() > length) return ($"{path}[{length}]", null, b[length].GetRawText());
                return null;
            default:
                return a.GetRawText() == b.GetRawText() ? null : (path, a.GetRawText(), b.GetRawText());
        }
    }
}

/// <summary>One decision of a real combat as it was played: what the game showed, what it was given, and what makes a
/// second world of it. The last one of a combat is its end, with no action.</summary>
/// <param name="Subject">What the action acts with: "play CARD", "potion POTION", "pick SOURCE" (the prompt's asker), "end_turn".</param>
/// <param name="Make">Makes a second world standing at this decision; null where none can be made (<see cref="ISecondWorlds.At"/>).</param>
/// <param name="Present">The ids in the combat at the decision, by kind (<see cref="GameIds.Present"/>).</param>
/// <param name="Kept">What a dump keeps of the decision besides; null when nothing is dumped.</param>
internal sealed record RealStep(Shown Shown, int Round, CombatAction? Action, string? Subject, Func<SecondWorld>? Make, IReadOnlyDictionary<string, HashSet<string>> Present,
    Kept? Kept = null);

/// <summary>What a dump keeps of a decision of the real combat besides what the check compares (<see cref="CombatDump"/>).</summary>
/// <param name="Prompt">The decision is a card prompt, in the middle of an action.</param>
/// <param name="Export">The live combat as <c>EngineExport</c> writes it; null where the engine takes none in: at a card
/// prompt, and once the combat is over.</param>
/// <param name="Run">What the combat has changed of the run (<c>RunChange</c>), as the JSON <c>sz_world_run</c> writes.</param>
/// <param name="Keys">How search tells the state apart; null once the combat is over, where it does not ask.</param>
internal sealed record Kept(bool Prompt, byte[]? Export, string Run, Keys? Keys);

/// <summary>A state's keys in search: the world's hidden key (<c>HiddenState.Full</c>) with the two texts it is the hash
/// of, and the observation's key (<c>PimcBrain.StateKey</c>).</summary>
internal sealed record Keys(ulong Hidden, ulong State, string HiddenKeyText, string HiddenText);

/// <summary>A place where the second world did not show what the real game showed.</summary>
/// <param name="Check">"import", "step" or "continue".</param>
/// <param name="Decision">The decision's number in its combat, from 0: for an import, where the world was made; for a
/// step or a continuation, the decision whose action the world took before it differed.</param>
/// <param name="From">For a continuation: the decision the world was made at.</param>
/// <param name="Action">The action taken, for a step or a continuation.</param>
/// <param name="Subject">What the action acts with (<see cref="RealStep.Subject"/>).</param>
/// <param name="Path">The first JSON path that differs; "legal[i]", "status"; "error" where the world failed.</param>
/// <param name="Real">The real game's value there; null: none.</param>
/// <param name="World">The world's; for "error", what it failed with.</param>
internal sealed record Difference(string Check, int Decision, int? From, int Round, string? Action, string? Subject, string Path, string? Real, string? World,
    IReadOnlyList<string> Monsters, IReadOnlyList<string> Powers, IReadOnlyList<string> Relics);

/// <summary>What the lockstep check made of one combat.</summary>
/// <param name="Steps">The real combat's decisions and its end.</param>
/// <param name="Differed">For each decision, whether any check found a difference there.</param>
/// <param name="ActionDiffered">For each decision, whether a world that agreed before its action differed after it.</param>
/// <param name="GameErrors">The game's own Log.Error lines while the real combat was played.</param>
internal sealed record LockstepResult(IReadOnlyList<RealStep> Steps, string Outcome, IReadOnlyList<Difference> Differences, bool[] Differed, bool[] ActionDiffered,
    int Imports, int StepChecks, int ContinueChecks, IReadOnlyList<string> GameErrors);

/// <summary>
/// The lockstep check of one combat (engine/DESIGN.md): the real game plays it with a seeded random policy, and a second
/// world is held against it at every decision, three ways.
///  - import: the world made from the real combat at the decision shows what the game shows there;
///  - step: a world carried along from the combat's start, given the action the game was given, still agrees after it.
///    Once it differs it is of no more use, and the world made at the next decision where one agrees is carried from there;
///  - continue: the world made at the decision, played along the rest of the real combat's actions, agrees after each,
///    up to the first where it does not: what the world took in at the decision was all that matters from there.
/// The game allows one live combat per process and a second world may be one (the replay world), so the real combat is
/// played to its end first (<see cref="Record"/>), keeping at each decision what the world's maker needs, and the worlds
/// come after it, one alive at a time (<see cref="Compare"/>).
/// </summary>
internal static class Lockstep
{
    /// <param name="seed">Seeds the policy, the answers to card prompts, and which decisions are continued from.</param>
    /// <param name="continues">How many decisions the continue check starts from, drawn evenly; null: every one.</param>
    /// <param name="keep">Called at every decision of the real combat and at its end, while it is live: what a dump
    /// keeps of it; null: nothing is dumped.</param>
    public static LockstepResult Check(Pump pump, CombatSetup setup, int seed, ISecondWorlds worlds, int? continues, Func<CombatSession, Kept>? keep = null)
    {
        HeadlessGame.TakeErrors();
        var (steps, outcome) = Record(pump, setup, seed, worlds, keep);
        var gameErrors = HeadlessGame.TakeErrors();
        return Compare(steps, continues, seed) with { Outcome = outcome.ToString(), GameErrors = gameErrors };
    }

    /// <summary>Plays the real combat to its end: every decision as the game showed it, with its action.</summary>
    private static (List<RealStep> Steps, CombatOutcome Outcome) Record(Pump pump, CombatSetup setup, int seed, ISecondWorlds worlds, Func<CombatSession, Kept>? keep)
    {
        var rng = new Random(seed);
        var steps = new List<RealStep>();
        using var real = CombatSession.Start(pump, setup, new RandomChoices(rng));
        var policy = new SweepPolicy(rng);
        while (true)
        {
            var shown = Shown.Of(real);
            var present = GameIds.Present(CombatSession.State, real.Player, real.Prompt);
            int round = CombatSession.State.RoundNumber;
            var kept = keep?.Invoke(real);
            if (real.IsCapped)
            {
                steps.Add(new(shown, round, null, null, null, present, kept));
                return (steps, real.Outcome);
            }
            var make = worlds.At(real);
            var action = real.Next(policy);
            steps.Add(new(shown, round, action, Subject(real, action), make, present, kept));
            real.Execute(action);
        }
    }

    /// <summary>Holds the second worlds against a combat as it was played.</summary>
    /// <param name="seed">Draws the decisions continued from, when not every one is.</param>
    public static LockstepResult Compare(IReadOnlyList<RealStep> steps, int? continues, int seed)
    {
        int decisions = steps.Count - 1;
        var differences = new List<Difference>();
        var (differed, actionDiffered) = (new bool[decisions], new bool[decisions]);
        int imports = 0, stepChecks = 0, continueChecks = 0;

        void Report(string check, int decision, int? from, bool afterAction, (string Path, string? Real, string? World) found)
        {
            var step = steps[decision];
            differed[decision] = true;
            if (afterAction) actionDiffered[decision] = true;
            differences.Add(new(check, decision, from, step.Round, afterAction ? step.Action?.ToString() : null, afterAction ? step.Subject : null,
                found.Path, found.Real, found.World, Ids(step, "monsters"), Ids(step, "powers"), Ids(step, "relics")));
        }

        // What a made world shows otherwise at its decision, its failure to be made included.
        (string, string?, string?)? Made(int decision, out SecondWorld? world)
        {
            world = null;
            try
            {
                world = steps[decision].Make!();
                return steps[decision].Shown.FirstDifference(world);
            }
            catch (Exception e)
            {
                return ("error", null, Failure(e));
            }
        }

        // What the world shows otherwise after the decision's action, its own failure included.
        (string, string?, string?)? After(SecondWorld world, int decision)
        {
            try
            {
                world.World.Execute(steps[decision].Action!.Value);
                return steps[decision + 1].Shown.FirstDifference(world);
            }
            catch (Exception e)
            {
                return ("error", null, Failure(e));
            }
        }

        // Step: the carried world.
        SecondWorld? carried = null;
        try
        {
            for (int i = 0; i < decisions; i++)
            {
                if (carried == null)
                {
                    // A world that cannot be made, or is made wrong, is the import check's to report.
                    if (steps[i].Make == null) continue;
                    if (Made(i, out carried) != null)
                    {
                        Drop(ref carried);
                        continue;
                    }
                }
                stepChecks++;
                if (After(carried!, i) is not { } found) continue;
                Report("step", i, null, afterAction: true, found);
                Drop(ref carried);
            }
        }
        finally
        {
            carried?.Dispose();
        }

        // Import, and continue from there. The carried world is the continuation from the first decision.
        var continued = ContinuedFrom(decisions, continues, new Random(seed));
        for (int i = 0; i < decisions; i++)
        {
            if (steps[i].Make == null) continue;
            imports++;
            var wrong = Made(i, out var world);
            try
            {
                if (wrong is { } found)
                {
                    Report("import", i, null, afterAction: false, found);
                    continue;
                }
                if (!continued.Contains(i)) continue;
                for (int j = i; j < decisions; j++)
                {
                    continueChecks++;
                    if (After(world!, j) is not { } later) continue;
                    Report("continue", j, i, afterAction: true, later);
                    break;
                }
            }
            finally
            {
                world?.Dispose();
            }
        }
        return new(steps, "", differences, differed, actionDiffered, imports, stepChecks, continueChecks, []);

        static void Drop(ref SecondWorld? world)
        {
            world?.Dispose();
            world = null;
        }

        static IReadOnlyList<string> Ids(RealStep step, string kind) => [.. step.Present[kind].Order(StringComparer.Ordinal)];
    }

    /// <summary>The decisions the continue check starts from: all but the first, which the carried world starts from, or
    /// <paramref name="count"/> of them drawn evenly.</summary>
    private static HashSet<int> ContinuedFrom(int decisions, int? count, Random rng)
    {
        var all = Enumerable.Range(1, Math.Max(0, decisions - 1)).ToList();
        if (count is not { } n || n >= all.Count) return [.. all];
        for (int i = 0; i < n; i++)
        {
            int j = rng.Next(i, all.Count);
            (all[i], all[j]) = (all[j], all[i]);
        }
        return [.. all.Take(n)];
    }

    private static string Subject(CombatSession real, CombatAction action) => action.Kind switch
    {
        CombatActionKind.PlayCard => $"play {CombatActions.HandCard(real.Player.PlayerCombatState!, action.Card!.Value).Id.Entry}",
        CombatActionKind.UsePotion => $"potion {real.Player.PotionSlots[action.PotionSlot!.Value]!.Id.Entry}",
        CombatActionKind.Pick => $"pick {real.Prompt!.Context.Source ?? real.Prompt.Context.Purpose}",
        _ => "end_turn",
    };

    private static string Failure(Exception e) => $"{e.GetType().Name}: {Logs.FirstLine(e.Message)}";
}

/// <summary>
/// The sweep's player: every kind of action at every point of a turn. Mostly a random card; a potion now and then while
/// one can be used, whatever the hand holds; and the turn ended early now and then, with cards still to play.
/// </summary>
internal sealed class SweepPolicy(Random rng) : ICombatPolicy
{
    public CombatAction Choose(CombatDecision decision)
    {
        var plays = decision.Legal.Where(a => a.Kind == CombatActionKind.PlayCard).ToList();
        var potions = decision.Legal.Where(a => a.Kind == CombatActionKind.UsePotion).ToList();
        if (potions.Count > 0 && rng.Next(12) == 0) return potions[rng.Next(potions.Count)];
        if (plays.Count == 0 || rng.Next(16) == 0) return CombatAction.EndTurn;
        return plays[rng.Next(plays.Count)];
    }
}
