using MegaCrit.Sts2.Core.Entities.Players;
using Sts2Ai.Brain;
using Sts2Ai.Engine;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.SimWorker.Simulation;

/// <summary>The live combat written out for the engine (<see cref="CombatDecision.Export"/>), where the game stands
/// between actions. The engine's schema is read at the first one: a run that plans on the node's clone reads none.</summary>
internal static class LiveCombat
{
    private static readonly Lazy<EngineExport> Writer = new(() => new EngineExport(EngineSchema.Load(EngineLibrary.SchemaPath)));

    /// <param name="run">The run around the combat as it stands.</param>
    public static CombatExport Export(Player player, RunView run) => new(Bytes(), run);

    /// <summary>The live combat's bytes alone.</summary>
    public static byte[] Bytes() => Writer.Value.Write(HeadlessGame.GameVersion, PromptSelector.Around()).Bytes;
}
