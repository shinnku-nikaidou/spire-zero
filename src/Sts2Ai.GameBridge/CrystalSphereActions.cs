using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using Sts2Ai.Protocol;
using Tool = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame.CrystalSphereToolType;

namespace Sts2Ai.GameBridge;

/// <summary>
/// The crystal sphere minigame (CRYSTAL_SPHERE event): "big:X,Y" divines the 3x3 block around cell X,Y, "small:X,Y"
/// the cell alone. Either costs one divination; an item is won once all its cells are uncovered, so the small tool
/// can stop short of the curse. Only divinations that uncover a hidden cell are offered.
/// </summary>
public static class CrystalSphereActions
{
    public static CrystalSphereScreen Screen(CrystalSphereMinigame game)
    {
        var size = game.GridSize;
        return new CrystalSphereScreen
        {
            Rows = Enumerable.Range(0, size.Y).Select(y => new string(Enumerable.Range(0, size.X).Select(x => Cell(game.cells[x, y])).ToArray())).ToList(),
            DivinationsLeft = game.DivinationCount,
        };
    }

    /// <summary>What the screen shows in the cell: the fog, or the part of the item under it.</summary>
    private static char Cell(CrystalSphereCell cell) => cell.IsHidden ? '#' : cell.Item?.ToSerializable().type switch
    {
        null => '.',
        CrystalSphereItemType.Relic => 'R',
        CrystalSphereItemType.Potion => 'P',
        CrystalSphereItemType.CardReward => 'C',
        CrystalSphereItemType.Gold => 'G',
        CrystalSphereItemType.Curse => 'X',
        var type => throw new NotSupportedException($"crystal sphere item {type} has no view"),
    };

    public static List<string> Legal(CrystalSphereMinigame game)
    {
        var actions = new List<string>();
        var size = game.GridSize;
        for (int x = 0; x < size.X; x++)
            for (int y = 0; y < size.Y; y++)
                if (AnyHidden(game, x - 1, x + 1, y - 1, y + 1)) actions.Add($"big:{x},{y}");
        for (int x = 0; x < size.X; x++)
            for (int y = 0; y < size.Y; y++)
                if (game.cells[x, y].IsHidden) actions.Add($"small:{x},{y}");
        return actions;
    }

    /// <summary>Picks the tool and clicks the cell, as the screen does. The minigame ends with the last divination.</summary>
    public static Task Start(CrystalSphereMinigame game, string action)
    {
        if (game.IsFinished || !Legal(game).Contains(action)) throw ActionText.Illegal(action, Surfaces.CrystalSphere);
        var tool = action.StartsWith("big:", StringComparison.Ordinal) ? Tool.Big : Tool.Small;
        var cell = action[(action.IndexOf(':') + 1)..].Split(',');
        game.SetTool(tool);
        return game.CellClicked(game.cells[ActionText.Index(cell[0]), ActionText.Index(cell[1])]);
    }

    private static bool AnyHidden(CrystalSphereMinigame game, int x0, int x1, int y0, int y1)
    {
        var size = game.GridSize;
        for (int x = Math.Max(x0, 0); x <= Math.Min(x1, size.X - 1); x++)
            for (int y = Math.Max(y0, 0); y <= Math.Min(y1, size.Y - 1); y++)
                if (game.cells[x, y].IsHidden) return true;
        return false;
    }
}
