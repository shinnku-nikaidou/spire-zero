using System.Globalization;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>An answer the console's ai command gives as an error: what the player asked cannot be done now.</summary>
internal sealed class AiError(string message) : Exception(message);

/// <summary>
/// What the console's ai command (<see cref="AiConsoleCmd"/>) reads and asks: the decision on screen and the brain's
/// answer to it, questions on the side (<see cref="Query"/>: the decision asked again with fewer options, another
/// environment or effort; <see cref="ValueQuery"/>: V of runs the player asks about), and the run's journal of answers
/// and the steps taken after them, which the review, the graph, the quiz and the engine's parity check read.
/// Everything here runs on the game's main thread, as the console's commands and <see cref="Tick"/> do.
/// </summary>
internal sealed partial class Assist
{
    /// <summary>The mod's assist, for the console; null when the mod is off.</summary>
    public static Assist? Current { get; set; }

    // ---- the decisions asked ----

    /// <summary>A decision asked of the brain, with what a query needs to ask it again.</summary>
    /// <param name="Trace">The steps since the checkpoint, whatever the run's environment: what SL asks it on.</param>
    /// <param name="Node">The room's checkpoint, where the record is whole; null with none.</param>
    /// <param name="Round">A combat decision's round; -1 elsewhere.</param>
    private sealed record Request(DecisionRequest Decision, string Key, IReadOnlyList<string>? Trace, NodeCheckpoint? Node, RunHeader Header, int Round);

    /// <summary>How many of the latest decisions are kept for <c>ai replay</c>.</summary>
    private const int KeptRequests = 64;

    private readonly LinkedList<Request> _requests = new();

    private Request? Last => _requests.Last?.Value;

    private void Remember(DecisionRequest decision, string key, RunState run, Player me, Asking asking)
    {
        var node = run.Players.Count == 1 && NodeRecord.Checkpoint is { } checkpoint && NodeRecord.Broken == null
            ? new NodeCheckpoint(NodeRecord.Node, checkpoint)
            : null;
        int round = asking.Obs.Screen is CombatScreen combat ? combat.Combat.Round : -1;
        _requests.AddLast(new Request(decision, key, node != null ? NodeRecord.Steps : null, node,
            new RunHeader(_run!, me.Character.Id.Entry, run.AscensionLevel), round));
        if (_requests.Count > KeptRequests) _requests.RemoveFirst();
        Checked(asking, run.TotalFloor);
    }

    /// <summary>The decision on screen with the brain's answer to it, or why there is none.</summary>
    private (Request Request, Answered Answer) Now()
    {
        if (_answer is not { } answer || answer.Key != _key) throw new AiError(_asked != null ? "the brain is still thinking about this decision" : "no answer on screen");
        if (Last is not { } request || request.Decision.DecisionId != answer.Id) throw new AiError("no answer on screen");
        return (request, answer);
    }

    // ---- questions on the side ----

    private sealed record Pending(string What, Action<QueryAnswer>? Query, Action<ValueAnswer>? Value);

    private long _queryId;
    private readonly Dictionary<long, Pending> _pending = [];

    /// <summary>The questions the brain has not answered yet, oldest first.</summary>
    public IEnumerable<(long Id, string What)> Questions => _pending.Select(p => (p.Key, p.Value.What));

    /// <summary>Asks <paramref name="request"/>'s decision again on a run of its own in <paramref name="env"/>, at
    /// <paramref name="effort"/> (the player's own for null), with <paramref name="legal"/> (all of them for null);
    /// <paramref name="done"/> gets the picks so far of a brain that searches on, then the answer.</summary>
    /// <param name="only">The legal actions given are the only first actions to search, each played first (<see cref="Query.Only"/>).</param>
    private long Ask(string what, Request request, PlayEnvironment env, string? effort, IReadOnlyList<string>? legal, Action<QueryAnswer> done, bool only = false)
    {
        if (!_connected) throw new AiError("no brain is running");
        bool sl = env == PlayEnvironment.SL;
        if (sl && request.Node == null)
            throw new AiError("no SL clone of this decision: SL's questions need the room's checkpoint (one player, the record whole)");
        var decision = request.Decision with { Legal = legal ?? request.Decision.Legal, Trace = sl ? request.Trace : null };
        long id = ++_queryId;
        var settings = Settings.Current;
        _pending[id] = new(what, done, null);
        link.Send(new Query(id, new Hello(gameVersion, env.ToString(), "mod", request.Header, effort ?? settings.EffortName, settings.TurnDecay),
            sl ? request.Node : null, decision, only));
        ModLog.Write("ai", $"query {id} ({what}): decision {request.Decision.DecisionId} in {env}, effort {effort ?? settings.EffortName}" +
                           (legal != null ? $", legal {string.Join(" | ", legal)}" : ""));
        return id;
    }

    /// <summary>Asks V of each run in the run's environment.</summary>
    private void Values(string what, IReadOnlyList<RunView> runs, Action<IReadOnlyList<double>> done)
    {
        if (!_connected) throw new AiError("no brain is running");
        long id = ++_queryId;
        _pending[id] = new(what, null, answer =>
        {
            if (answer.Values is not { } values) Out.LaterError($"{what}: {answer.Error}");
            else done(values);
        });
        link.Send(new ValueQuery(id, _env.ToString(), runs));
    }

    /// <summary>A query's answer, or V's, from the brain.</summary>
    private void SideAnswer(WireMessage message)
    {
        switch (message)
        {
            case QueryAnswer q when _pending.TryGetValue(q.Id, out var pending):
                if (q.Answer?.Searching != true) _pending.Remove(q.Id);
                Safe(pending.What, () => pending.Query!(q));
                break;
            case ValueAnswer v when _pending.Remove(v.Id, out var pending):
                Safe(pending.What, () => pending.Value!(v));
                break;
        }
    }

    /// <summary>The brain went: every question still open gets its error.</summary>
    private void Unanswered(string reason)
    {
        foreach (var (id, pending) in _pending.ToList())
        {
            _pending.Remove(id);
            if (pending.Query != null) Safe(pending.What, () => pending.Query(new QueryAnswer(id, null, reason)));
            else Safe(pending.What, () => pending.Value!(new ValueAnswer(id, null, reason)));
        }
    }

    /// <summary>Tells the brain to answer every open question at once with its pick so far.</summary>
    public int Hurry()
    {
        foreach (var id in _pending.Keys) link.Send(new CancelQuery(id));
        return _pending.Count;
    }

    private static void Safe(string what, Action answer)
    {
        try
        {
            answer();
        }
        catch (Exception e)
        {
            ModLog.Warn("ai", $"{what}: {e}");
            Out.LaterError($"{what}: {e.Message}");
        }
    }

    // ---- reading the answer on screen ----

    /// <summary>The settings the mod plays with, and the brain's state, for <c>ai status</c>.</summary>
    public IEnumerable<string> Status()
    {
        var s = Settings.Current;
        yield return $"brain      {(_connected ? "connected" : $"offline: {_down ?? "starting"}")} · {link.Where}";
        yield return $"champion   {_champion ?? "—"}";
        yield return $"run        {(_run != null ? $"{_run} · {_line}" : "none sent")}{(_failure != null ? $" · off: {_failure}" : "")}";
        yield return $"env        {_env}{(s.Env != _env ? $" (settings {s.Env})" : "")} · effort {s.EffortName} · turn decay {(s.TurnDecay ? "on" : "off")}";
        yield return $"SL clone   {(NodeRecord.Checkpoint == null ? "no checkpoint" : NodeRecord.Broken ?? $"checkpoint {NodeRecord.Node} + {NodeRecord.Steps.Count} steps")}";
        yield return $"auto       {_goal}{(_until != null ? $" until {_until}" : "")}{(_stopped != null ? $" · stopped: {_stopped}" : "")} · speed {s.Speed}";
        yield return $"decision   {(_asked != null ? $"{_asked.Id} thinking {(Environment.TickCount64 - _asked.Sent) / 1000.0:0.0} s" : _answer != null ? $"{_answer.Id} answered: {_answer.Answer.Action}" : "none")}";
        yield return $"quiz       {(_quiz ? "on" : "off")} · journal {_journal.Count} decisions · questions open {_pending.Count}";
    }

    /// <summary><c>ai why</c>: every option the brain scored at the decision on screen.</summary>
    public string Why(int? pick)
    {
        var (request, answer) = Now();
        var candidates = answer.Answer.Candidates ?? [];
        if (candidates.Count == 0) return Out.Esc($"{Describe.Action(answer.Answer.Action, answer.Obs)}: the only thing to do here (nothing scored)");
        double best = candidates.Max(c => c.Value);
        if (pick is { } n)
        {
            if (n < 1 || n > candidates.Count) throw new AiError($"option {n} of {candidates.Count}");
            var c = candidates[n - 1];
            var lines = new List<string> { $"{n}. {Describe.Action(c.Action, answer.Obs, c.Plan)}", $"   value {c.Value:0.0000} ({Out.Pts(c.Value - best)} pts against the best)  ±{c.Ci:0.0000}  samples {c.Samples}" };
            if (c.Win is { } w) lines.Add($"   fight: win {Out.Pct(w)}, {c.Hp:0.#} HP left");
            if (c.Plan is { Count: > 0 } plan) lines.AddRange(plan.Select((a, i) => $"   {i + 1,3}. {Describe.Action(a, answer.Obs)}"));
            return Out.Code(lines);
        }
        var rows = new List<string> { $"{answer.Answer.Brain} · decision {answer.Id} · {request.Decision.Surface}" + (answer.Answer.Nodes is { } nodes ? $" · {nodes:N0} nodes" : "") + (answer.Answer.Proved == true ? " · proved" : "") };
        rows.Add($" #  {"option",-38} {"value",8} {"vs best",8} {"±",7} {"win",6} {"HP",5}");
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            string mark = c.Action == answer.Answer.Action ? "*" : " ";
            rows.Add($"{mark}{i + 1,2} {Out.Pad(Describe.Action(c.Action, answer.Obs, c.Plan), 38)} {c.Value,8:0.0000} {Out.Pts(c.Value - best),8} {c.Ci,7:0.0000} " +
                     $"{(c.Win is { } w ? Out.Pct(w, "0") : ""),6} {(c.Hp is { } hp ? hp.ToString("0", CultureInfo.InvariantCulture) : ""),5}");
        }
        rows.Add("value: V, the run's chance to win, where the option leads (vs best in points of it); ai why N for an option's plan");
        return Out.Code(rows);
    }

    /// <summary><c>ai legal</c>: the actions the decision on screen allows, numbered as <c>ai try</c> takes them.</summary>
    public string Legal()
    {
        var request = Last ?? throw new AiError("no decision asked yet");
        if (request.Key != _key) throw new AiError("the decision on screen has not been asked yet");
        var legal = request.Decision.Legal;
        if (legal.Count == 0) return "a card prompt: its answers are picks (try is for the other decisions)";
        return Out.Code(legal.Select((a, i) => $"{i + 1,3}. {Describe.Action(a, request.Decision.Obs)}   [{a}]"));
    }

    /// <summary><c>ai line</c>: the picked line of the fight, turn by turn, as the details card has it.</summary>
    public string Line()
    {
        var table = _table ?? throw new AiError("no fight line: one comes with a combat answer");
        var rows = new List<string> { $"start: HP {table.Hp} · " + string.Join(", ", table.Enemies.Select(e => $"{Describe.TableEnemy(e.Id)} {e.Hp}/{e.MaxHp}")) };
        rows.Add($"{"round",5} {"dealt",5} {"block",5} {"taken",5} {"HP",4}  enemies · cards");
        foreach (var t in table.Turns)
            rows.Add($"{t.Round,5} {t.Dealt,5} {t.Block,5} {t.Taken,5} {t.Hp,4}  [{string.Join(" ", t.Enemies)}] · {string.Join(", ", t.Cards.Select(Describe.TableCard))}");
        rows.Add(table.Status switch
        {
            CombatStatus.Won => "the line wins the fight",
            CombatStatus.Lost => "the line loses the fight",
            _ => $"the line stops in progress: the combat net's guess win {(table.Win is { } w ? Out.Pct(w) : "?")}, {table.HpLeft:0.#} HP at a win",
        });
        if (_env == PlayEnvironment.NoSL) rows.Add("NoSL: played on one drawn world (draws and enemy moves guessed), to this turn's enemy turn");
        return Out.Code(rows);
    }

    /// <summary><c>ai lethal</c>: whether the picked line ends the fight this turn, and what the enemies keep if not.</summary>
    public string Lethal()
    {
        var table = _table ?? throw new AiError("no fight line: one comes with a combat answer");
        if (table.Turns.Count == 0) return "no turn in the line";
        var turn = table.Turns[0];
        if (turn.Enemies.All(hp => hp <= 0)) return Out.Esc($"lethal this turn: {string.Join(", ", turn.Cards.Select(Describe.TableCard))}");
        return Out.Esc($"no lethal in the plan this turn: it deals {turn.Dealt}, the enemies keep {turn.Enemies.Sum(h => Math.Max(h, 0))} HP " +
                       $"({string.Join(", ", turn.Enemies)}); the fight ends at round {table.Turns[^1].Round}{(table.Status == CombatStatus.InProgress ? " or later" : "")}");
    }

    /// <summary><c>ai danger</c>: the damage the enemies' intents show, and what the plan takes of it.</summary>
    public string Danger()
    {
        var (request, _) = Now();
        if (request.Decision.Obs.Screen is not CombatScreen { Combat: var combat }) throw new AiError("not in a fight");
        var lines = new List<string>();
        int total = 0;
        foreach (var enemy in combat.Enemies)
        {
            int hits = (enemy.Intents ?? []).Where(i => i.Damage != null).Sum(i => i.Damage!.Value * (i.Hits ?? 1));
            total += hits;
            lines.Add($"{Out.Pad(Describe.TableEnemy(enemy.Id), 24)} {enemy.Hp,4}/{enemy.MaxHp,-4} intends {hits}");
        }
        lines.Add($"shown: {total} damage at {combat.Player.Block} block, HP {combat.Player.Hp}");
        if (_table is { Turns.Count: > 0 } table) lines.Add($"the plan: block {table.Turns[0].Block} at the turn's end, takes {table.Turns[0].Taken}, HP {table.Turns[0].Hp} after the enemies' turn");
        return Out.Code(lines);
    }

    // ---- questions about the decision on screen ----

    /// <summary>The action a player names: its number in <c>ai legal</c>, or its text.</summary>
    private static string Named(Request request, string named)
    {
        var legal = request.Decision.Legal;
        if (int.TryParse(named, out int n) && n >= 1 && n <= legal.Count) return legal[n - 1];
        return legal.FirstOrDefault(a => a.Equals(named, StringComparison.OrdinalIgnoreCase))
               ?? legal.FirstOrDefault(a => Describe.Action(a, request.Decision.Obs).Contains(named, StringComparison.OrdinalIgnoreCase))
               ?? throw new AiError($"no legal action {named}: ai legal lists them");
    }

    /// <summary><c>ai try</c>: the option the player names, against the brain's answer: from the answer's own scores where
    /// it scored it, else asked again with that option and the answer alone (a decision of one option is not searched).</summary>
    public string Try(string named)
    {
        var (request, answer) = Now();
        string action = Named(request, named);
        var obs = request.Decision.Obs;
        var scored = answer.Answer.Candidates ?? [];
        if (scored.FirstOrDefault(c => c.Action == action) is { } mine && scored.Count > 1)
            return Out.Code(Compared(obs, mine, scored.MaxBy(c => c.Value)!, from: "the answer's own scores"));
        string other = action == answer.Answer.Action ? request.Decision.Legal.First(a => a != action) : answer.Answer.Action;
        Ask($"try {Describe.Action(action, obs)}", request, _env, null, [action, other], only: true, done: q =>
        {
            if (q.Answer?.Searching == true) return;
            var c = q.Answer?.Candidates ?? [];
            if (c.FirstOrDefault(x => x.Action == action) is not { } tried || c.FirstOrDefault(x => x.Action == other) is not { } against)
                Out.LaterError($"try: {q.Error ?? "the brain scored no such option"}");
            else Out.Later(Out.Code(Compared(obs, tried, against, from: $"searched again with these two{(q.Answer!.Nodes is { } n ? $", {n:N0} nodes" : "")}")));
        });
        return Out.Esc($"asking the brain about {Describe.Action(action, obs)}…");
    }

    private static IEnumerable<string> Compared(Observation obs, Candidate tried, Candidate against, string from)
    {
        yield return $"{Describe.Action(tried.Action, obs)}: value {tried.Value:0.0000}" + (tried.Win is { } w ? $", fight win {Out.Pct(w)}, {tried.Hp:0.#} HP" : "");
        yield return $"{Describe.Action(against.Action, obs)}: value {against.Value:0.0000}" + (against.Win is { } w2 ? $", fight win {Out.Pct(w2)}, {against.Hp:0.#} HP" : "");
        double d = tried.Value - against.Value;
        yield return d >= 0 ? $"→ yours is as good or better by {Out.Pts(d)} pts" : $"→ yours costs {Out.Pts(-d)} pts of V";
        if (tried.Plan is { Count: > 0 } plan) yield return "its plan: " + string.Join(", ", plan.Take(12).Select(a => Describe.Action(a, obs)));
        yield return $"({from})";
    }

    /// <summary><c>ai deep</c>: the decision on screen asked of SL's max (the solver) on a run of its own.</summary>
    public string Deep()
    {
        var (request, answer) = Now();
        long started = Environment.TickCount64;
        string? lastPick = null;
        Ask("deep", request, PlayEnvironment.SL, "max", null, q =>
        {
            var obs = request.Decision.Obs;
            if (q.Answer is not { } a)
            {
                Out.LaterError($"deep: {q.Error}");
                return;
            }
            string pick = Describe.Action(a.Action, obs);
            double s = (Environment.TickCount64 - started) / 1000.0;
            if (a.Searching == true)
            {
                if (pick != lastPick) Out.Later(Out.Esc($"deep, {s:0.0} s{(a.Nodes is { } n ? $", {n:N0} nodes" : "")}: so far {pick}"));
                lastPick = pick;
                return;
            }
            var best = a.Candidates?.FirstOrDefault(c => c.Action == a.Action);
            Out.Later(Out.Esc($"deep answered in {s:0.0} s{(a.Nodes is { } nodes ? $", {nodes:N0} nodes" : "")}{(a.Proved == true ? ", proved" : "")}: {pick}" +
                              (best?.Win is { } w ? $" (fight win {Out.Pct(w)}, {best.Hp:0.#} HP)" : "") +
                              (a.Action == answer.Answer.Action ? " — the same as the answer on screen" : $" — the answer on screen is {Describe.Action(answer.Answer.Action, obs)}")));
            if (a.Plan is { Count: > 0 } plan) Out.Later(Out.Esc("its plan: " + string.Join(", ", plan.Take(16).Select(x => Describe.Action(x, obs)))));
        });
        return "asking SL's max (the solver) about this decision; ai stop answers it now with its pick so far";
    }

    /// <summary><c>ai compare</c>: the decision on screen asked of the other environment's brain.</summary>
    public string Compare()
    {
        var (request, answer) = Now();
        var other = _env == PlayEnvironment.SL ? PlayEnvironment.NoSL : PlayEnvironment.SL;
        var obs = request.Decision.Obs;
        Ask($"compare with {other}", request, other, null, null, q =>
        {
            if (q.Answer?.Searching == true) return;
            if (q.Answer is not { } a)
            {
                Out.LaterError($"compare: {q.Error}");
                return;
            }
            var rows = new List<string>
            {
                $"{_env} (on screen): {Describe.Action(answer.Answer.Action, obs)}",
                $"{other}: {Describe.Action(a.Action, obs)}{(a.Action == answer.Answer.Action ? "  — they agree" : "  — they differ")}",
            };
            foreach (var (name, list) in new[] { (_env.ToString(), answer.Answer.Candidates ?? []), (other.ToString(), a.Candidates ?? []) })
                rows.Add($"{name,-5} top: " + string.Join(" · ", list.Take(3).Select(c => $"{Describe.Action(c.Action, obs)} {c.Value:0.000}")));
            Out.Later(Out.Code(rows));
        });
        return $"asking {other}'s brain about this decision…";
    }

    /// <summary><c>ai replay</c>: a decision asked again as it was, at today's settings, against the answer it had.</summary>
    public string Replay(long? id)
    {
        var request = (id is { } n ? _requests.FirstOrDefault(r => r.Decision.DecisionId == n) : _requests.Last?.Value)
                      ?? throw new AiError(id != null ? $"decision {id} is not among the last {KeptRequests} kept" : "no decision asked yet");
        var old = _journal.FirstOrDefault(e => e.Id == request.Decision.DecisionId);
        var obs = request.Decision.Obs;
        Ask($"replay {request.Decision.DecisionId}", request, _env, null, null, q =>
        {
            if (q.Answer?.Searching == true) return;
            if (q.Answer is not { } a)
            {
                Out.LaterError($"replay: {q.Error}");
                return;
            }
            Out.Later(Out.Esc($"decision {request.Decision.DecisionId} again: {Describe.Action(a.Action, obs)}" +
                              (old == null ? "" : a.Action == old.Action ? " — the same as then" : $" — then: {Describe.Action(old.Action, obs)}")));
        });
        return $"asking decision {request.Decision.DecisionId} again…";
    }

    // ---- V of runs the player asks about ----

    /// <summary>The run on screen as V reads it.</summary>
    private RunView RunNow() => (Last ?? throw new AiError("no decision asked yet")).Decision.Obs.Run.WithoutPreviews();

    /// <summary><c>ai deck</c>: what each card, relic and potion is worth to the run: V with it less V without it.</summary>
    public string Deck()
    {
        var run = RunNow();
        var variants = new List<(string Name, RunView Run)> { ("", run) };
        foreach (var card in run.Deck.DistinctBy(ProtocolJson.CardKey))
        {
            int at = run.Deck.ToList().FindIndex(c => ProtocolJson.CardKey(c) == ProtocolJson.CardKey(card));
            variants.Add(($"card   {Describe.TableCard(card.Id + (card.Up > 0 ? "+" : ""))}", run with { Deck = [.. run.Deck.Where((_, i) => i != at)] }));
        }
        foreach (var relic in run.Relics)
            variants.Add(($"relic  {Describe.Relic(relic.Id)}", run with { Relics = [.. run.Relics.Where(r => !ReferenceEquals(r, relic))] }));
        for (int slot = 0; slot < run.Potions.Count; slot++)
            if (run.Potions[slot] is { } potion)
            {
                int s = slot;
                variants.Add(($"potion {Describe.Potion(potion)}", run with { Potions = [.. run.Potions.Select((p, i) => i == s ? null : p)] }));
            }
        Values("deck", [.. variants.Select(v => v.Run)], values =>
        {
            double base0 = values[0];
            var rows = variants.Skip(1).Select((v, i) => (v.Name, Worth: base0 - values[i + 1])).OrderByDescending(v => v.Worth)
                .Select(v => $"{Out.Pts(v.Worth),7}  {v.Name}").Prepend($"V now {Out.Pct(base0)}; each line: V with it less V without it, in points (copies count once)");
            Out.Later(Out.Code(rows));
        });
        return "asking V about the deck…";
    }

    /// <summary><c>ai worth</c>: what a card, relic or potion would add to the run (V with it less V now).</summary>
    public string Worth(string kind, string id, bool upgraded)
    {
        var run = RunNow();
        string key = id.ToUpperInvariant();
        RunView with;
        string name;
        switch (kind)
        {
            case "card":
                var model = ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == key) ?? throw new AiError($"no card {key}");
                var card = CardModel.FromSerializable(new SerializableCard { Id = model.Id, CurrentUpgradeLevel = upgraded ? 1 : 0, FloorAddedToDeck = run.Floor });
                var view = Observe.Card(card);
                with = run with { Deck = [.. run.Deck.Append(view).OrderBy(ProtocolJson.CardKey, StringComparer.Ordinal)] };
                name = Describe.TableCard(key + (upgraded ? "+" : ""));
                break;
            case "relic":
                var relic = ModelDb.AllRelics.FirstOrDefault(r => r.Id.Entry == key) ?? throw new AiError($"no relic {key}");
                with = run with { Relics = [.. run.Relics, Observe.Relic(relic)] };
                name = Describe.Relic(key);
                break;
            case "potion":
                if (ModelDb.AllPotions.All(p => p.Id.Entry != key)) throw new AiError($"no potion {key}");
                int empty = run.Potions.ToList().IndexOf(null);
                if (empty < 0) throw new AiError("no empty potion slot");
                with = run with { Potions = [.. run.Potions.Select((p, i) => i == empty ? key : p)] };
                name = Describe.Potion(key);
                break;
            default:
                throw new AiError("worth card|relic|potion ID");
        }
        Values($"worth {name}", [run, with], v => Out.Later(Out.Esc($"{name}: V {Out.Pct(v[0])} → {Out.Pct(v[1])}, {Out.Pts(v[1] - v[0])} pts")));
        return Out.Esc($"asking V about {name}…");
    }

    // ---- the journal ----

    /// <summary>An answered decision, and the step taken after it.</summary>
    private sealed class Entry
    {
        public required long Id;
        public required string Surface;
        public required int Floor;
        public required int Act;
        public required int Fight;
        public required int Round;
        public required ShownKind Kind;
        public required IReadOnlyList<Candidate> Candidates;
        public required string Action;
        public required FightTable? Table;
        /// <summary>Kept for the player's own answers, which the review names.</summary>
        public Observation? Obs;
        public string? Played;
        public bool Ai;
        /// <summary>V the step gave up against the answer (negative: it did better); null until known.</summary>
        public double? Loss;
        /// <summary>For a step the answer did not score: the decision, to ask it again in the review.</summary>
        public Request? Request;
        public double Best => Candidates.Count > 0 ? Candidates.Max(c => c.Value) : 0;
    }

    private readonly List<Entry> _journal = [];
    private string? _journalRun;
    private Entry? _open;
    private int _fights;

    private void Journal(long id, Observation obs, Recommendation answer, Shown shown)
    {
        if (_journalRun != _run)
        {
            _journal.Clear();
            (_journalRun, _fights, _predicted) = (_run, 0, null);
        }
        if (Last is not { } request || request.Decision.DecisionId != id) return;
        bool fight = obs.Screen is CombatScreen or CardChoiceScreen { Combat: not null };
        if (fight && (_journal.Count == 0 || _journal[^1].Fight == 0)) _fights++;
        var entry = new Entry
        {
            Id = id, Surface = request.Decision.Surface, Floor = obs.Run.Floor, Act = obs.Run.Act, Fight = fight ? _fights : 0, Round = request.Round,
            Kind = shown.Kind, Candidates = answer.Candidates ?? [], Action = answer.Action, Table = answer.Table, Obs = obs,
        };
        // A decision the state left unanswered keeps nothing to name.
        if (_open is { Played: null } left) left.Obs = null;
        _journal.Add(entry);
        _open = entry;
    }

    /// <summary>A step taken (<see cref="NodeRecord.Taken"/>): the answer to the decision last answered, if it is on its surface.</summary>
    public void Took(string surface, string action, bool ai)
    {
        if (_open is not { } e) return;
        _open = null;
        if (surface != e.Surface) return;
        (e.Played, e.Ai) = (action, ai);
        if (surface == Surfaces.CardChoice) return;
        if (action == e.Action) e.Loss = 0;
        else if (e.Candidates.FirstOrDefault(c => c.Action == action) is { } c) e.Loss = e.Best - c.Value;
        else e.Request = Last?.Decision.DecisionId == e.Id ? Last : null;
        if (ai) e.Obs = null;
        Quizzed(e);
        Predicted(e);
    }

    private IEnumerable<Entry> Scope(string scope)
    {
        if (_journal.Count == 0) return [];
        return scope switch
        {
            "fight" => _journal.Where(e => e.Fight == _journal.Where(x => x.Fight > 0).Select(x => x.Fight).DefaultIfEmpty(-1).Max()),
            "act" => _journal.Where(e => e.Act == _journal[^1].Act),
            "run" => _journal,
            _ => throw new AiError("review fight|act|run"),
        };
    }

    /// <summary><c>ai review</c>: each of the player's own answers against the brain's, worst first. A step the answer did
    /// not score is asked first, one question at a time.</summary>
    public string Review(string scope)
    {
        var mine = Scope(scope).Where(e => e.Played != null && !e.Ai && e.Surface != Surfaces.CardChoice).ToList();
        var unknown = mine.Where(e => e.Loss == null && e.Request != null).ToList();
        if (unknown.Count == 0) return Reviewed(scope, mine);
        Score(unknown, 0, () => Out.Later(Reviewed(scope, mine)));
        return $"scoring {unknown.Count} of your answers the brain had not scored, one by one; the review follows";
    }

    private void Score(List<Entry> list, int at, Action done)
    {
        if (at == list.Count)
        {
            done();
            return;
        }
        var e = list[at];
        try
        {
            Ask($"review {e.Id}", e.Request!, _env, null, [e.Played!, e.Action], only: true, done: q =>
            {
                if (q.Answer?.Searching == true) return;
                var c = q.Answer?.Candidates ?? [];
                if (c.FirstOrDefault(x => x.Action == e.Played) is { } mine && c.FirstOrDefault(x => x.Action == e.Action) is { } ai) e.Loss = ai.Value - mine.Value;
                e.Request = null;
                Score(list, at + 1, done);
            });
        }
        catch (AiError error)
        {
            Out.LaterError($"review: {error.Message}");
            done();
        }
    }

    private static string Reviewed(string scope, List<Entry> mine)
    {
        if (mine.Count == 0) return $"no answers of yours in this {scope} yet (the AI's own and card prompts are left out)";
        var scored = mine.Where(e => e.Loss != null).ToList();
        int same = scored.Count(e => e.Loss == 0 && e.Played == e.Action);
        double lost = scored.Sum(e => Math.Max(e.Loss!.Value, 0));
        var rows = new List<string>
        {
            $"{scope}: {mine.Count} answers of yours, {scored.Count} scored; the AI's own answer {same} times; V given up {Out.Pts(lost)} pts in all" +
            (scored.Count > 0 ? $", {Out.Pts(lost / scored.Count)} a decision" : ""),
            "?? 3 pts or more · ? 1 or more · ?! 0.3 or more",
        };
        foreach (var e in scored.Where(e => e.Loss > 0).OrderByDescending(e => e.Loss).Take(10))
        {
            string mark = e.Loss >= 0.03 ? "??" : e.Loss >= 0.01 ? "? " : e.Loss >= 0.003 ? "?!" : "  ";
            rows.Add($"{mark} F{e.Floor,-2} {Out.Pad(e.Surface, 8)} {Out.Pts(-e.Loss!.Value),7}  you: {Out.Pad(Name(e, e.Played!), 30)} AI: {Name(e, e.Action)}");
        }
        foreach (var e in scored.Where(e => e.Loss < 0).OrderBy(e => e.Loss).Take(3))
            rows.Add($"!  F{e.Floor,-2} {Out.Pad(e.Surface, 8)} {Out.Pts(-e.Loss!.Value),7}  you beat the AI: {Out.Pad(Name(e, e.Played!), 30)} AI: {Name(e, e.Action)}");
        if (scored.Count < mine.Count) rows.Add($"{mine.Count - scored.Count} could not be scored (no answer kept for them)");
        return Out.Code(rows);
    }

    private static string Name(Entry e, string action) => e.Obs != null ? Describe.Action(action, e.Obs) : action;

    /// <summary><c>ai graph</c>: V's chance to win the run along the floors, from the answers outside combat.</summary>
    public string Graph()
    {
        var points = _journal.Where(e => e.Kind == ShownKind.Run && e.Candidates.Count > 0).GroupBy(e => e.Floor)
            .Select(g => (Floor: g.Key, V: g.Last().Best)).OrderBy(p => p.Floor).ToList();
        if (points.Count == 0) throw new AiError("no answers outside combat in this run yet");
        double top = Math.Max(points.Max(p => p.V), 1e-6);
        var rows = new List<string>();
        for (int i = 0; i < points.Count; i++)
        {
            var (floor, v) = points[i];
            int width = (int)Math.Round(v / top * 40);
            string delta = i == 0 ? "" : Out.Pts(v - points[i - 1].V);
            rows.Add($"F{floor,-3} {new string('█', width),-40} {Out.Pct(v),6} {delta,7}");
        }
        var drops = points.Zip(points.Skip(1), (a, b) => (b.Floor, D: b.V - a.V)).Where(d => d.D < 0).OrderBy(d => d.D).Take(3).ToList();
        if (drops.Count > 0) rows.Add("biggest drops: " + string.Join(", ", drops.Select(d => $"F{d.Floor} {Out.Pts(d.D)}")));
        return Out.Code(rows);
    }

    // ---- the quiz ----

    private bool _quiz;
    private string? _reveal;
    private readonly Dictionary<string, (int N, int Same, double Lost, int Scored)> _quizStats = [];

    public string Quiz(string what)
    {
        switch (what)
        {
            case "on":
                (_quiz, _reveal) = (true, null);
                if (_goal != Goal.Off) Stop(L10n.T("quiz on"));
                return "quiz on: the panel hides the AI's answer until you have made your choice; ai quiz stats for your scores";
            case "off":
                _quiz = false;
                return "quiz off";
            case "reset":
                _quizStats.Clear();
                return "quiz scores cleared";
            case "stats":
                if (_quizStats.Count == 0) return "no quiz answers yet";
                return Out.Code(_quizStats.OrderByDescending(s => s.Value.Scored > 0 ? s.Value.Lost / s.Value.Scored : 0).Select(s =>
                    $"{Out.Pad(s.Key, 12)} {s.Value.N,4} answers, the AI's own {s.Value.Same,4} ({Out.Pct((double)s.Value.Same / s.Value.N, "0")}), " +
                    $"V given up {(s.Value.Scored > 0 ? Out.Pts(s.Value.Lost / s.Value.Scored) : "?")} pts a decision")
                    .Prepend("weakest first"));
            default:
                throw new AiError("quiz on|off|stats|reset");
        }
    }

    /// <summary>While the quiz is on, the panel hides the answer, and the AI plays nothing; true when it did.</summary>
    private bool Quizzing(Answered answer, Panel panel, string champion, string status)
    {
        if (!_quiz) return false;
        panel.Show(champion, status, L10n.T("Quiz: your move"), _reveal ?? L10n.T("the AI has answered; make your choice"), Tone.Plain, Drive());
        return true;
    }

    private void Quizzed(Entry e)
    {
        if (!_quiz || e.Ai) return;
        string category = e.Surface;
        var s = _quizStats.GetValueOrDefault(category);
        bool same = e.Played == e.Action;
        _quizStats[category] = (s.N + 1, s.Same + (same ? 1 : 0), s.Lost + Math.Max(e.Loss ?? 0, 0), s.Scored + (e.Loss != null ? 1 : 0));
        _reveal = same ? L10n.T("last: the AI's own answer") :
            $"{L10n.T("last: the AI would")} {Name(e, e.Action)}" + (e.Loss is { } loss ? $" · {Out.Pts(-loss)}" : "");
        Out.Later(Out.Esc($"quiz F{e.Floor} {e.Surface}: you {Name(e, e.Played!)} · AI {Name(e, e.Action)}" +
                          (same ? " — the same" : e.Loss is { } l ? $" · {Out.Pts(-l)} pts" : " · not scored (ai review scores it)")));
    }

    // ---- the engine's parity, on the real fight ----

    /// <summary>What the SL line said the end of a turn leaves, at its end-turn answer the player or the AI took.</summary>
    private (int Floor, int Round, int Hp, IReadOnlyList<int> Enemies)? _predicted;

    private readonly List<string> _parity = [];
    private int _parityChecked;

    private void Predicted(Entry e)
    {
        if (_env != PlayEnvironment.SL || e.Played != CombatAction.EndTurn.ToString() || e.Table is not { Turns.Count: > 0 } table) return;
        var row = table.Turns[0];
        if (row.Round == e.Round) _predicted = (e.Floor, e.Round, row.Hp, row.Enemies);
    }

    /// <summary>At the next round's first decision, the fight as the game has it against what the line said.</summary>
    private void Checked(Asking asking, int floor)
    {
        if (_predicted is not { } p) return;
        if (asking.Obs.Screen is not CombatScreen { Combat: var combat } || floor != p.Floor)
        {
            if (floor != p.Floor) _predicted = null;
            return;
        }
        if (combat.Round <= p.Round) return;
        _predicted = null;
        if (combat.Round != p.Round + 1) return;
        _parityChecked++;
        var enemies = combat.Enemies.Select(e => e.Hp).ToList();
        bool same = combat.Player.Hp == p.Hp && (enemies.Count == p.Enemies.Count(h => h > 0)
            ? enemies.SequenceEqual(p.Enemies.Where(h => h > 0))
            : enemies.Sum() == p.Enemies.Sum(h => Math.Max(h, 0)));
        if (!same)
            _parity.Add($"F{floor} round {p.Round}: the line said HP {p.Hp}, enemies [{string.Join(" ", p.Enemies)}]; the game has HP {combat.Player.Hp}, enemies [{string.Join(" ", enemies)}]");
    }

    public string Parity()
    {
        var rows = new List<string> { $"{_parityChecked} turn ends checked in SL (the engine's line against the real game after an end turn the line planned)" };
        rows.AddRange(_parity.TakeLast(12));
        rows.Add(_parity.Count == 0 ? "no difference" : $"{_parity.Count} differ (a turn's start, such as poison or a relic, can move HP before the check)");
        return Out.Code(rows);
    }

    // ---- marks ----

    /// <summary><c>ai mark</c>: the decision last asked, written whole with the player's note: a wire to give the brain
    /// again (hello, checkpoint, decision) and what the panel showed.</summary>
    public string Mark(string note)
    {
        var request = Last ?? throw new AiError("no decision asked yet");
        var dir = Path.Combine(ModLog.Folder ?? Settings.Dir, "marks");
        Directory.CreateDirectory(dir);
        int n = Directory.GetFiles(dir, "mark-*.json").Length + 1;
        string stem = Path.Combine(dir, $"mark-{n}");
        var run = RunManager.Instance.DebugOnlyGetState();
        var settings = Settings.Current;
        var hello = new Hello(gameVersion, _env.ToString(), "mod", request.Header, settings.EffortName, settings.TurnDecay);
        var wire = new List<WireMessage> { hello };
        if (_env == PlayEnvironment.SL && request.Node != null) wire.Add(request.Node);
        wire.Add(request.Decision with { Trace = _env == PlayEnvironment.SL ? request.Trace : null });
        File.WriteAllLines(stem + ".wire.jsonl", wire.Select(m => ProtocolJson.Serialize(m)));
        var answer = _answer is { } a && a.Id == request.Decision.DecisionId ? a.Answer : null;
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(new
        {
            note, at = DateTime.UtcNow.ToString("O"), seed = run?.Rng.StringSeed, floor = request.Decision.Obs.Run.Floor, surface = request.Decision.Surface,
            decision = request.Decision.DecisionId, env = _env.ToString(), effort = settings.EffortName, champion = _champion,
            answer = answer?.Action, shown = answer == null ? null : Describe.Action(answer.Action, request.Decision.Obs),
        }, new JsonSerializerOptions { WriteIndented = true }));
        ModLog.Info("ai", $"mark {n}: {note} → {stem}.json");
        return Out.Esc($"marked decision {request.Decision.DecisionId} (floor {request.Decision.Obs.Run.Floor}): {stem}.json and .wire.jsonl");
    }

    /// <summary>The AI's planned route on the map from the point the run is at, as the scout takes it ("c,r" steps): the
    /// latest map answer's point and the route after it.</summary>
    public IReadOnlyList<string> Route()
    {
        var map = _journal.LastOrDefault(e => e.Surface == Surfaces.Map) ?? throw new AiError("no map answer in this run yet");
        var plan = map.Candidates.FirstOrDefault(c => c.Action == map.Action)?.Plan ?? [];
        var visited = (Last?.Decision.Obs.Run.Map.Visited ?? []).LastOrDefault();
        return [.. plan.Prepend(map.Action).Select(a => MacroAction.Parse(a).Coord).Where(c => visited == null || c.Row > visited.Row).Select(c => $"{c.Col},{c.Row}")];
    }

    // ---- auto-play's stops ----

    /// <summary>Where <c>ai auto until</c> stops: a room type next on the map, an act, a floor, or the run's end.</summary>
    private sealed record UntilStop(string Kind, int Value)
    {
        public override string ToString() => Kind switch { "floor" => $"floor {Value}", "act" => $"act {Value + 1}", _ => Kind };
    }

    private UntilStop? _until;

    /// <summary>Auto-play on until the stop the player named, which replaces the settings' stops.</summary>
    public string Auto(string what, string? arg)
    {
        if (_run == null) throw new AiError("no run sent to the brain yet");
        if (_quiz) throw new AiError("the quiz is on: ai quiz off first");
        if (_coop) throw new AiError("a run of more than one player: the AI only advises");
        UntilStop stop = what switch
        {
            "boss" => new("boss", 0),
            "elite" => new("elite", 0),
            "act" => new("act", _requests.Last?.Value.Decision.Obs.Run.Act ?? 0),
            "floor" when int.TryParse(arg, out int f) => new("floor", f),
            "end" => new("end", 0),
            _ => throw new AiError("auto until boss|elite|act|floor N|end"),
        };
        _stopped = null;
        Begin(Goal.Auto);
        (_until, _waived) = (stop, _key);
        return $"auto-play until {stop} (the settings' stops are off for it)";
    }

    private string? Until(Asking asking, Answered answer)
    {
        var u = _until!;
        var run = asking.Obs.Run;
        switch (u.Kind)
        {
            case "boss" or "elite" when asking.Surface == Surfaces.Map:
                string? type = Describe.Point(answer.Answer.Action, run.Map);
                if (type == "Boss" || u.Kind == "elite" && type == "Elite") return L10n.T(type == "Boss" ? "the boss is next" : "an elite is next");
                break;
            case "act" when run.Act > u.Value:
                return $"act {run.Act + 1}";
            case "floor" when run.Floor >= u.Value:
                return $"floor {run.Floor}";
        }
        return null;
    }

    /// <summary>The toolbar's buttons, from the console.</summary>
    public string Drive(Command command, bool? on = null)
    {
        if (_run == null) throw new AiError("no run sent to the brain yet");
        if (_coop) throw new AiError("a run of more than one player: the AI only advises");
        if (command == Command.Auto && on is { } want && want == (_goal == Goal.Auto)) return want ? "auto-play is already on" : "auto-play is already off";
        if (_quiz) throw new AiError("the quiz is on: ai quiz off first");
        Press(command);
        return command switch { Command.Auto => _goal == Goal.Auto ? "auto-play on" : "auto-play paused", Command.Step => "one action", _ => "to the end of this turn or room" };
    }
}
