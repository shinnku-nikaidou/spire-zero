using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>V (docs: macro-brain/value §1): what a run is worth, on one scale for every option of every surface.</summary>
public interface IRunValue
{
    /// <summary>Which V this is, for the champion's name.</summary>
    string Name { get; }

    double Value(RunView run);
}
