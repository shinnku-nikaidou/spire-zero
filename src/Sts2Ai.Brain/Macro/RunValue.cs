using Sts2Ai.Protocol;

namespace Sts2Ai.Brain.Macro;

/// <summary>V: what a run is worth, on one scale for every option of every surface.</summary>
public interface IRunValue
{
    /// <summary>Which V this is, for the champion's name.</summary>
    string Name { get; }

    double Value(RunView run);

    /// <summary>This V in <paramref name="env"/>: one that reads the environment (<see cref="RunNet"/>) marks its runs
    /// so; another is the same in every environment.</summary>
    IRunValue In(PlayEnvironment env) => this;

    /// <summary>Each potion the run holds, in the belt's order, with the HP this V says it will save in the fight where
    /// it is drunk (f, <see cref="RunNet.Saves(RunView, PlayEnvironment)"/>); none from a V that does not say.</summary>
    IReadOnlyList<double>? Saves(RunView run) => null;
}
