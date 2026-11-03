using System.Text;
using Sts2Ai.Protocol;

namespace Sts2Ai.Brain;

/// <summary>A state on the search's current path, for <see cref="LoopSearch"/> to compare a later state with.</summary>
/// <param name="Obs">The state's observation.</param>
/// <param name="Depth">How many actions of the path lead to it.</param>
/// <param name="Snapshot">The world at it.</param>
public sealed record LoopPoint(CombatObservation Obs, int Depth, WorldSnapshot Snapshot);

/// <summary>
/// Cycles inside a turn (docs: sl-planner/turn-search §5), for both combat brains. A depth-first search has no end to a
/// move that can be played again and again: the first such chain spends the turn's nodes, the budget cuts every move
/// left at every state above it, and the attack that should come after the repeats is never tried.
///
/// So the search does not go down a cycle. A move that brings back the cards of a state on the path, with no less energy
/// or stars, no fewer copies of those cards left and something changed, is tried a few more times, each of which must
/// bring them back too; if it holds, it is set aside (<see cref="Intercept"/>). What the piles do to other cards does not matter: a curse a boss deals every six
/// cards changes them, and the cycle goes on. A move that runs out by itself, or uses up its own cards, is an ordinary
/// line, and the search goes on as it always did, so a deck with no cycle is played move for move as it would be without
/// this class.
///
/// After the search (<see cref="RunFound"/>), on a budget of its own, each cycle is repeated 1, 2, 4... times, and each
/// count is cashed out: a small search with the cycle's moves left out, to the leaf at the next turn's start, which is
/// the state the combat net knows. Numbers in the middle of a turn mean nothing to it. It stops when neither the value nor
/// the damage done still grows, when a move is no longer there (a full hand sends a returning card to the discard pile),
/// or at a win, which is then cut down to the fewest repeats. The count just before a repeat changed the piles is tried
/// too, since a boss's next curse may cost more than one more repeat gives.
///
/// The cards are all it knows of a cycle. Every repeat is played in the world, so what the game does to it is the game's.
/// </summary>
public sealed class LoopSearch(ICombatWorld world, int round, LoopSearch.EndOf end)
{
    /// <summary>Scores a line that ended the turn or the combat, with the world at its end.</summary>
    public delegate TurnEnd EndOf(CombatStatus status, CombatObservation obs, IReadOnlyList<CombatAction> actions, IReadOnlyList<ulong> hashes);

    /// <summary>The moves one search may play for its cycles, on top of its nodes; cutting a win down may use as many again.</summary>
    public const int Moves = 4000;

    /// <summary>A cycle that holds for this many repeats is taken for one with no end of its own.</summary>
    private const int ProbeRepeats = 4;

    private const int CashNodes = 60, MaxRepeats = 4096, FlatRounds = 2, MaxBreaks = 8;

    private readonly List<Cycle> _found = [];
    private int _used;
    private bool _won, _cutting;

    /// <summary>Whether a cycle was found: the search has then not seen everything there is.</summary>
    public bool Found { get; private set; }

    /// <summary>Out of moves, or a cycle already wins: nothing more is looked for.</summary>
    public bool Spent => _used >= Moves || _won;

    /// <summary>Counts a move a caller played on the cycles' budget.</summary>
    public void Charge() => _used++;

    private sealed record Cycle(WorldSnapshot Start, List<CombatAction> Prefix, List<ulong> PrefixHashes, List<CombatAction> Moves, List<string?> Ids, int Potential);

    /// <summary>
    /// Whether the search should not go down from <paramref name="now"/>, the state the path's last action reached: it
    /// comes back to a state on the path with nothing changed, or it is a cycle, which is kept for <see cref="RunFound"/>.
    /// If not, the world is where it was.
    /// </summary>
    public bool Intercept(IReadOnlyList<LoopPoint> path, CombatObservation now, IReadOnlyList<CombatAction> actions, IReadOnlyList<ulong> hashes)
    {
        var back = Back(path, now);
        if (back == null) return false;
        if (Shape(back.Obs) == Shape(now)) return true;
        if (Spent) return false;
        var cycle = Cycled(path, back, actions, hashes);
        var here = world.Snapshot();
        int done = 1;
        var scratch = new Line([], []);
        if (Repeat(cycle, scratch, ref done, ProbeRepeats, null, out var status, back.Obs) && status == CombatStatus.InProgress)
        {
            _found.Add(cycle);
            Found = true;
            return true;
        }
        world.Restore(here);
        return false;
    }

    /// <summary>Repeats and cashes out the cycles found since the last call, the ones with the most left to spend first.</summary>
    public List<TurnEnd> RunFound()
    {
        var ends = new List<TurnEnd>();
        foreach (var cycle in _found.OrderByDescending(c => c.Potential).ThenBy(c => c.Prefix.Count))
        {
            if (Spent) break;
            ends.AddRange(Run(cycle, 0));
        }
        _found.Clear();
        return ends;
    }

    /// <summary>
    /// Whether one play of a free card from <paramref name="now"/>, the world's state, is a cycle. The world stays where it
    /// is. Each try costs a restore, on every other answer of every prompt the search meets, and a card that costs
    /// something can be a cycle only by giving it back, so the free ones are all that is tried.
    /// </summary>
    public bool OpensCycle(CombatObservation now)
    {
        var free = now.Hand.Where(c => c.Cost == 0 && (c.Stars ?? 0) == 0).Select(c => c.Uid).ToHashSet();
        var here = world.Snapshot();
        bool moved = false, opens = false;
        foreach (var action in world.Legal().Where(a => a.Kind == CombatActionKind.PlayCard && free.Contains(a.Card)))
        {
            if (moved) world.Restore(here);
            moved = true;
            _used++;
            world.Execute(action);
            var after = world.Observe();
            if (world.Status == CombatStatus.InProgress && Recurs(now, after) && Shape(now) != Shape(after))
            {
                opens = true;
                break;
            }
        }
        if (moved) world.Restore(here);
        return opens;
    }

    /// <summary>The latest state on the path that <paramref name="now"/> brings back, if any.</summary>
    private static LoopPoint? Back(IReadOnlyList<LoopPoint> path, CombatObservation now)
    {
        for (int i = path.Count - 1; i >= 0; i--)
            if (Recurs(path[i].Obs, now)) return path[i];
        return null;
    }

    /// <summary>
    /// Whether the moves from <paramref name="from"/> to <paramref name="now"/> can be played again: the same round, potions
    /// and living enemies, every card of its hand back (a copy will do) and no dearer, no less energy or stars, and no
    /// fewer copies of its hand's cards left to draw or play. Moves that use up their own cards run out by themselves:
    /// a Soul draws the next Soul and is exhausted, so a pile of Souls is a long line, not a cycle.
    /// </summary>
    public static bool Recurs(CombatObservation from, CombatObservation now)
    {
        if (from.Round != now.Round || now.Player.Energy < from.Player.Energy || now.Player.Stars < from.Player.Stars) return false;
        if (!from.Potions.SequenceEqual(now.Potions)) return false;
        if (!from.Enemies.Where(e => e.Hp > 0).Select(e => e.Uid).SequenceEqual(now.Enemies.Where(e => e.Hp > 0).Select(e => e.Uid))) return false;
        var hand = now.Hand.ToList();
        foreach (var card in from.Hand)
        {
            int i = hand.FindIndex(c => c.Id == card.Id && c.Up == card.Up && (c.Cost ?? int.MaxValue) <= (card.Cost ?? int.MaxValue) && (c.Stars ?? 0) <= (card.Stars ?? 0));
            if (i < 0) return false;
            hand.RemoveAt(i);
        }
        static int Left(CombatObservation obs, string id) => obs.Hand.Count(c => c.Id == id) + obs.Draw.Count(c => c.Id == id) + obs.Discard.Count(c => c.Id == id);
        return from.Hand.Select(c => c.Id).Distinct().All(id => Left(now, id) >= Left(from, id));
    }

    /// <summary>How much is left to spend the repeats on: what can still be played from the cycle's start, then the energy.</summary>
    private static int Potential(CombatObservation obs) =>
        10 * obs.Hand.Count(c => c.Keywords?.Contains("Unplayable") != true) + obs.Player.Energy;

    private static Cycle Cycled(IReadOnlyList<LoopPoint> path, LoopPoint back, IReadOnlyList<CombatAction> actions, IReadOnlyList<ulong> hashes)
    {
        var moves = actions.Skip(back.Depth).ToList();
        var ids = moves.Select((move, i) =>
        {
            var before = path.FirstOrDefault(p => p.Depth == back.Depth + i);
            return before?.Obs.Hand.FirstOrDefault(c => c.Uid == move.Card)?.Id;
        }).ToList();
        return new(back.Snapshot, actions.Take(back.Depth).ToList(), hashes.Take(back.Depth).ToList(), moves, ids, Potential(back.Obs));
    }

    /// <summary>The piles and what is fixed about the enemies, then every number: whether two states differ at all.</summary>
    private static string Shape(CombatObservation obs) => $"{Piles(obs)}|{Numbers(obs)}";

    private static string Piles(CombatObservation obs)
    {
        static string Cards(IReadOnlyList<CardView> cards) =>
            string.Join(",", cards.Select(c => $"{c.Id}|{c.Up}|{c.Cost}|{c.XCost}|{c.Stars}|{c.Enchant?.Id}|{c.Affliction?.Id}").Order(StringComparer.Ordinal));
        var text = new StringBuilder();
        text.Append(Cards(obs.Hand)).Append('#').Append(Cards(obs.Draw)).Append('#').Append(Cards(obs.Discard)).Append('#').Append(Cards(obs.Exhaust)).Append('#');
        text.Append(string.Join(",", obs.Potions)).Append('#').Append(string.Join(",", obs.Player.Orbs.Select(o => o.Id))).Append('#');
        foreach (var enemy in obs.Enemies.Where(e => e.Hp > 0))
            text.Append(enemy.Uid).Append(':').Append(string.Join("+", (enemy.Intents ?? []).Select(i => i.Type))).Append(';');
        return text.ToString();
    }

    private static string Numbers(CombatObservation obs)
    {
        var text = new StringBuilder();
        void Powers(IReadOnlyList<PowerView> powers)
        {
            foreach (var power in powers.OrderBy(p => p.Id, StringComparer.Ordinal)) text.Append(power.Id).Append('=').Append(power.Amount).Append(',');
        }
        var player = obs.Player;
        text.Append($"{player.Hp},{player.Block},{player.Energy},{player.Stars};");
        Powers(player.Powers);
        foreach (var enemy in obs.Enemies)
        {
            text.Append($"|{enemy.Hp},{enemy.Block};");
            Powers(enemy.Powers);
        }
        foreach (var card in obs.Hand)
            foreach (var kv in (card.Vars ?? new Dictionary<string, decimal>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                text.Append('/').Append(kv.Key).Append('=').Append(kv.Value);
        return text.ToString();
    }

    private sealed record Line(List<CombatAction> Actions, List<ulong> Hashes)
    {
        public Line Copy() => new([.. Actions], [.. Hashes]);
    }

    private void Play(CombatAction action, Line line)
    {
        _used++;
        line.Hashes.Add(ProtocolJson.Hash(world.Observe()));
        world.Execute(action);
        line.Actions.Add(action);
    }

    /// <summary>The move, or the same card under a new uid: a copy the game made is the same step of the cycle.</summary>
    private CombatAction? Find(CombatAction move, string? id)
    {
        var legal = world.Legal();
        if (legal.Contains(move.Plain)) return move;
        if (id == null) return null;
        var hand = world.Observe().Hand;
        foreach (var action in legal)
            if (action.Kind == CombatActionKind.PlayCard && action.Target == move.Target && hand.FirstOrDefault(c => c.Uid == action.Card)?.Id == id)
                return action with { Pick = move.Pick };
        return null;
    }

    /// <summary>Plays the cycle until <paramref name="done"/> reaches <paramref name="to"/>; false if a move is not there, or the combat or the budget ended.</summary>
    /// <param name="breaks">Takes the repeats done before one that changed the piles.</param>
    /// <param name="back">For a probe, the state each repeat must bring back (<see cref="Recurs"/>): a hand that holds a few
    /// copies of the card can play them once each, as when Music Box copies the first attack of the turn and two more
    /// Neutralize are in the hand, and that is no cycle.</param>
    private bool Repeat(Cycle cycle, Line line, ref int done, int to, List<int>? breaks, out CombatStatus status, CombatObservation? back = null)
    {
        status = world.Status;
        while (done < to)
        {
            string? piles = breaks == null ? null : Piles(world.Observe());
            for (int i = 0; i < cycle.Moves.Count; i++)
            {
                if (status != CombatStatus.InProgress || _used >= 2 * Moves) return false;
                if (Find(cycle.Moves[i], cycle.Ids[i]) is not { } move) return false;
                Play(move, line);
                status = world.Status;
            }
            if (status == CombatStatus.InProgress && breaks is { Count: < MaxBreaks } && done >= 1 && Piles(world.Observe()) != piles) breaks.Add(done);
            if (back != null && status == CombatStatus.InProgress && !Recurs(back, world.Observe())) return false;
            done++;
        }
        return true;
    }

    /// <summary>
    /// The best the state can do with what the repeats built: a small search to the end of the turn, without the cycle's
    /// moves. The damage is the enemies' HP and block it leaves at best: on a fight the leaf calls hopeless the value sits at
    /// 0 until the repeats reach lethal, and the damage shows they are getting there.
    /// </summary>
    private (TurnEnd? End, int Enemies) Cash(Line line, HashSet<CombatAction> banned, int depth)
    {
        var ends = new List<TurnEnd>();
        int nodes = 0, enemies = int.MaxValue;
        var seen = new HashSet<(ulong, ulong)>();
        void Note(CombatStatus status, CombatObservation obs)
        {
            ends.Add(end(status, obs, line.Actions, line.Hashes));
            enemies = Math.Min(enemies, obs.Enemies.Sum(e => Math.Max(0, e.Hp) + e.Block));
        }
        void Go(CombatObservation obs, List<LoopPoint> stack)
        {
            if (!seen.Add((ProtocolJson.Hash(obs), world.HiddenKey()))) return;
            var here = world.Snapshot();
            List<LoopPoint> path = [.. stack, new(obs, line.Actions.Count, here)];
            bool moved = false;
            foreach (var action in PimcBrain.Ordered(world.Legal()).Where(a => !banned.Contains(a)))
            {
                if (moved && (nodes >= CashNodes || _used >= (_cutting ? 2 * Moves : Moves))) break;
                if (moved) world.Restore(here);
                moved = true;
                nodes++;
                Play(action, line);
                var status = world.Status;
                var after = world.Observe();
                if (status != CombatStatus.InProgress || action.Kind == CombatActionKind.EndTurn || after.Round != round) Note(status, after);
                else if (Back(path, after) is not { } back) Go(after, path);
                else if (Shape(back.Obs) != Shape(after) && depth < 2) ends.AddRange(Run(Cycled(path, back, line.Actions, line.Hashes), depth + 1));
                line.Actions.RemoveAt(line.Actions.Count - 1);
                line.Hashes.RemoveAt(line.Hashes.Count - 1);
            }
        }
        var start = world.Status;
        var obs = world.Observe();
        if (start != CombatStatus.InProgress) Note(start, obs);
        else Go(obs, []);
        return (ends.OrderByDescending(e => e.Value).ThenBy(e => e.Actions.Count).FirstOrDefault(), enemies);
    }

    /// <summary>
    /// A cycle's lines: each count it was cashed out at, best first and, among equals, the shortest. Only a cycle the
    /// search found goes back to the counts where the piles changed; one that a cash-out found is one of many.
    /// </summary>
    private List<TurnEnd> Run(Cycle cycle, int depth)
    {
        var banned = new HashSet<CombatAction>(cycle.Moves.Where(m => m.Kind != CombatActionKind.EndTurn).Select(m => m.Plain));
        var kept = new List<TurnEnd>();
        var breaks = new List<int>();
        double best = double.NegativeInfinity;
        int least = int.MaxValue, flat = 0, lose = 0, win = -1, done = 0;
        world.Restore(cycle.Start);
        var line = new Line([.. cycle.Prefix], [.. cycle.PrefixHashes]);
        for (int count = 1; count <= MaxRepeats && !Spent; count *= 2)
        {
            bool held = Repeat(cycle, line, ref done, count, depth == 0 ? breaks : null, out var status);
            if (status == CombatStatus.Won)
            {
                kept.Add(end(status, world.Observe(), line.Actions, line.Hashes));
                win = done;
                break;
            }
            if (status == CombatStatus.Lost) break;
            var here = world.Snapshot();
            var (cashed, enemies) = Cash(line.Copy(), banned, depth);
            if (cashed != null) kept.Add(cashed);
            if (cashed?.Status == CombatStatus.Won)
            {
                win = done;
                break;
            }
            double value = cashed?.Value ?? double.NegativeInfinity;
            if (value > best + 1e-4 || enemies < least)
            {
                (best, least, flat) = (Math.Max(best, value), Math.Min(least, enemies), 0);
            }
            else if (++flat >= FlatRounds) break;
            lose = done;
            if (!held) break;
            world.Restore(here);
        }
        if (win < 0)
        {
            foreach (int count in breaks.Distinct())
            {
                if (_used >= 2 * Moves) break;
                var at = Replayed(cycle, count, out var status);
                if (status == CombatStatus.InProgress && Cash(at, banned, depth).End is { } cashed) kept.Add(cashed);
            }
        }
        // Won lines are worth the same at any count that wins with the same HP, and a shorter one is less to play and to log.
        bool outer = _cutting;
        _cutting = true;
        while (win > 0 && win - lose > 1 && _used < 2 * Moves)
        {
            int count = (lose + win) / 2;
            var at = Replayed(cycle, count, out var status);
            if (status == CombatStatus.Won)
            {
                kept.Add(end(status, world.Observe(), at.Actions, at.Hashes));
                win = count;
                continue;
            }
            if (status == CombatStatus.Lost) break;
            var (cashed, _) = Cash(at, banned, depth);
            if (cashed != null) kept.Add(cashed);
            if (cashed?.Status == CombatStatus.Won) win = count;
            else lose = count;
        }
        _cutting = outer;
        if (kept.Any(e => e.Status == CombatStatus.Won)) _won = true;
        return kept.OrderByDescending(e => e.Value).ThenBy(e => e.Actions.Count).ToList();
    }

    private Line Replayed(Cycle cycle, int count, out CombatStatus status)
    {
        world.Restore(cycle.Start);
        var line = new Line([.. cycle.Prefix], [.. cycle.PrefixHashes]);
        int done = 0;
        Repeat(cycle, line, ref done, count, null, out status);
        return line;
    }
}
