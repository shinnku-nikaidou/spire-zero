using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>A brain's answer as the player reads it, with names from the observation it answered.</summary>
internal static class Describe
{
    public static string[] Recommendation(Recommendation answer, Observation obs)
    {
        if (obs.Screen is not CombatScreen { Combat: var combat }) return [answer.Action];
        var lines = new List<string> { $"→ {Combat(answer.Action, combat)}" };
        if (answer.Plan is { Count: > 0 } plan) lines.Add("then " + string.Join(", ", plan.Select(a => Combat(a, combat))));
        if (answer.Candidates is { Count: > 1 } candidates)
            lines.Add(string.Join("   ", candidates.Take(3).Select(c => $"{Combat(c.Action, combat)} {c.Value:0.0}±{c.Ci:0.0}")));
        return [.. lines];
    }

    private static string Combat(string text, CombatObservation combat)
    {
        if (!CombatAction.TryParse(text, out var action)) return text;
        string target = action.Target is uint t ? " → " + Creature(t, combat) : "";
        return action.Kind switch
        {
            CombatActionKind.PlayCard => Card(action.Card!.Value, combat) + target,
            CombatActionKind.UsePotion => $"potion {combat.Potions.ElementAtOrDefault(action.PotionSlot!.Value) ?? "?"}{target}",
            _ => "end turn",
        };
    }

    private static string Card(uint uid, CombatObservation combat) =>
        combat.Hand.FirstOrDefault(c => c.Uid == uid) is { } card ? card.Id + (card.Up is > 0 ? "+" : "") : $"card {uid}";

    private static string Creature(uint uid, CombatObservation combat)
    {
        var enemies = combat.Enemies.Where(e => e.Hp > 0).ToList();
        if (enemies.FindIndex(e => e.Uid == uid) is var i and >= 0) return enemies.Count > 1 ? $"{enemies[i].Id} #{i + 1}" : enemies[i].Id ?? "enemy";
        return combat.Player.Uid == uid ? "self" : combat.Allies.FirstOrDefault(a => a.Uid == uid)?.Id ?? $"creature {uid}";
    }
}
