using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>
/// A combat's public record of enemy moves (docs: harness §4): what each living enemy showed at the first decision of
/// each round, by uid. It starts over when the round goes back; whoever keeps it clears it when the combat ends.
/// </summary>
public sealed class MoveRecord
{
    private readonly Dictionary<uint, (string? Id, List<EnemyTurn> Turns)> _turns = [];
    private int _round = -1;

    public MoveRecord()
    {
    }

    /// <summary>A record that goes on from <paramref name="history"/>, a record's <see cref="History"/>.</summary>
    public MoveRecord(IReadOnlyList<EnemyTurns> history)
    {
        foreach (var e in history)
        {
            _turns[e.Uid] = (e.Id, e.Turns.ToList());
            foreach (var turn in e.Turns) _round = Math.Max(_round, turn.Round);
        }
    }

    /// <summary>Each enemy's rounds so far, in the order the enemies first showed up.</summary>
    public IReadOnlyList<EnemyTurns> History => _turns.Select(e => new EnemyTurns(e.Key, e.Value.Id, e.Value.Turns.ToList())).ToList();

    /// <summary>A decision's observation: on the round's first, each living enemy's intents join the record.</summary>
    public void Witness(CombatObservation obs)
    {
        if (obs.Round == _round) return;
        if (obs.Round < _round) _turns.Clear();
        _round = obs.Round;
        foreach (var enemy in obs.Enemies)
        {
            if (enemy.Uid is not { } uid || enemy.Intents is not { } intents || enemy.Hp <= 0) continue;
            if (!_turns.TryGetValue(uid, out var turns)) _turns[uid] = turns = (enemy.Id, []);
            turns.Turns.Add(new(obs.Round, intents));
        }
    }

    public void Clear()
    {
        _round = -1;
        _turns.Clear();
    }
}
