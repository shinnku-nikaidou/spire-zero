using System.Diagnostics;

namespace Sts2Ai.Harness;

/// <summary>
/// What a run's combat plans cost and where they were rooted, for the profile: kept in the run's record
/// (<see cref="RunRecord.Search"/>). The planner counts the first part (<see cref="PlanMeter"/>), Harness the second.
/// </summary>
/// <param name="Plans">Plans searched: a planned step and a forced answer are none.</param>
/// <param name="Nodes">Actions simulated, over all the plans.</param>
/// <param name="Turns">Turns the beams searched.</param>
/// <param name="WallMs">Time spent inside the plans.</param>
/// <param name="CpuMs">Processor time the search took meanwhile: this process's on the engine world (its threads), the
/// pool's workers' on the replay world.</param>
/// <param name="Imports">Engine world: live combats taken in, one a plan.</param>
/// <param name="ImportBytes">Their bytes as written out.</param>
/// <param name="ExportMs">The time writing them out took.</param>
/// <param name="ImportMs">The time the engine took to take them in.</param>
/// <param name="Crossings">Engine world: paths of answers played on the node's clone for the plans rooted at a card
/// prompt, a worker's request each (<see cref="PromptRoot"/>).</param>
/// <param name="Replans">Plans made because a step of the plan before did not hold (<see cref="Dispatcher.Replans"/>).</param>
/// <param name="PromptOpens">Plans rooted at a card prompt that opens its combat, before any decision of the player's.</param>
/// <param name="PromptAfterPlan">Plans rooted at a card prompt where the plan before had run out.</param>
/// <param name="PromptReplanned">Plans rooted at a card prompt because a step did not hold there.</param>
public sealed record PlanTally(int Plans = 0, long Nodes = 0, int Turns = 0, double WallMs = 0, double CpuMs = 0,
    int Imports = 0, long ImportBytes = 0, double ExportMs = 0, double ImportMs = 0, int Crossings = 0,
    int Replans = 0, int PromptOpens = 0, int PromptAfterPlan = 0, int PromptReplanned = 0);

/// <summary>Counts what one run's plans cost, as its planner makes them.</summary>
/// <param name="cpu">The processor time the search has taken so far, wherever it runs.</param>
public sealed class PlanMeter(Func<TimeSpan> cpu)
{
    public PlanTally Tally { get; private set; } = new();

    /// <summary>Makes a plan and counts it.</summary>
    public SlPlan Count(Func<SlPlan> plan)
    {
        var (clock, before) = (Stopwatch.StartNew(), cpu());
        var made = plan();
        Tally = Tally with
        {
            Plans = Tally.Plans + 1, Nodes = Tally.Nodes + made.Nodes, Turns = Tally.Turns + made.Turns,
            WallMs = Tally.WallMs + clock.Elapsed.TotalMilliseconds, CpuMs = Tally.CpuMs + (cpu() - before).TotalMilliseconds,
        };
        return made;
    }

    /// <summary>Counts paths of answers played on the node's clone.</summary>
    public void Crossed(int paths) => Tally = Tally with { Crossings = Tally.Crossings + paths };

    /// <summary>Counts a live combat taken in by the engine.</summary>
    public void Imported(int bytes, double exportMs, double importMs) => Tally = Tally with
    {
        Imports = Tally.Imports + 1, ImportBytes = Tally.ImportBytes + bytes, ExportMs = Tally.ExportMs + exportMs, ImportMs = Tally.ImportMs + importMs,
    };
}
