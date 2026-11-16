using System.Globalization;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>What the player asked the AI to play.</summary>
internal enum Goal
{
    /// <summary>Nothing: the AI only shows its answer.</summary>
    Off,

    /// <summary>The next action, then nothing.</summary>
    One,

    /// <summary>To the end of this combat turn.</summary>
    Turn,

    /// <summary>To the next map decision, after leaving the map if it starts there.</summary>
    Room,

    /// <summary>Until the player pauses it, or a stop the player set comes.</summary>
    Auto,
}

/// <summary>
/// Assist mode and the AI's play: once the game has been idle on a decision
/// for a few frames, asks the brain about that state, and shows the answer while the state lasts. The player acts, or
/// asks the AI to (<see cref="Press"/>): one action, to the end of the turn or the room, or on its own until paused.
/// The AI plays only the brain's answer to the decision on screen, through <see cref="Executor"/>, once per state, and
/// at auto-play's speed; it stops when the player acts (a step the AI did not take, or going back), when an action does
/// nothing, before an elite or a boss and when V's chance to win falls, if the player set those, and at the run's end.
/// One decision is in flight at a time: when the state moves on first, the mod cancels it and waits for its answer
/// before asking about the new state, so a slow brain never falls behind.
/// The environment and the SL planner's effort are the player's choice (<see cref="Settings"/>), kept across games.
/// Switching either, or turning assist off, ends the run as the brain sees it; the next decision starts a new one from
/// that point. In SL the brain plans a
/// combat in its engine, on the live combat the mod writes out with each combat decision between actions
/// (<see cref="DecisionRequest.Export"/>, along the engine's schema beside the brain), and crosses a card prompt and
/// values every other option on a clone of the node, so the mod sends each checkpoint once and every SL decision with
/// the steps taken since it (<see cref="NodeRecord"/>), and asks nothing where it cannot clone; NoSL sends none of these,
/// the live combat holding the hidden state. Screens, decisions and answers go to <see cref="ModLog"/>.
/// </summary>
/// <param name="schemaPath">The engine's schema (<see cref="BundledBrain.SchemaPath"/>); null with no brain shipped.</param>
internal sealed class Assist(BrainLink link, string gameVersion, string? schemaPath)
{
    /// <summary>How many frames in a row a state must hold before it counts as idle.</summary>
    private const int IdleFrames = 3;

    /// <summary>How long an action may leave the state as it was, or its button stay unready, before the AI stops.</summary>
    private const long Patience = 10_000;

    /// <summary>How long auto-play waits on a screen with no decision before it presses on (a chest, a proceed button).</summary>
    private const long NudgeAfter = 1_200;

    /// <summary>How far V's chance to win may fall below its best since auto-play started before it stops.</summary>
    private const double Drop = 0.03;

    /// <summary>How many of the AI's last actions the panel lists.</summary>
    private const int Remembered = 4;

    /// <param name="Key">The observation's hash and the legal actions: what the answer is for.</param>
    /// <param name="Sent">When it was sent, in <see cref="Environment.TickCount64"/> milliseconds.</param>
    /// <param name="Cancelled">When the state moved on and the mod cancelled it, or null.</param>
    private sealed record Asked(long Id, string Key, Observation Obs, long Sent, long? Cancelled = null);

    /// <summary>An answer as it is shown, and what it is for.</summary>
    private sealed record Answered(long Id, string Key, Recommendation Answer, Shown Shown, string Foot, MapRoute? Route, CombatPlan? Plan);

    private readonly Screens _screens = new();

    /// <summary>Writes the live combat out for the engine, read from the schema at the first SL combat decision.</summary>
    private readonly Lazy<EngineExport> _export = new(() => new EngineExport(EngineSchema.Load(
        schemaPath ?? throw new InvalidOperationException("no engine schema: no brain ships with this copy of the mod"))));
    private bool _connected;
    private string? _down;

    // The run the brain was told about on this connection (hello sent), and its state.
    private string? _run;
    private string? _champion;
    private string? _failure;
    private (bool Over, bool Won, int Floor) _last;
    private string? _line;

    // SL: the checkpoint the brain has been sent in this run.
    private int _sentNode;

    private long _next;
    private Asked? _asked;
    private string? _askedKey;
    private Answered? _answer;
    private string? _key;
    private int _stable;
    private long _shown;

    // The AI's play: what it was asked to do, what it did, and why it stopped.
    private Goal _goal;
    private int _acts;
    private string? _lastSurface;
    private (string Key, long At)? _acted;
    private (string Key, long Since)? _waiting;
    private string? _stopped;
    private string? _waived;
    private long _idle;
    private string _surface = Surfaces.Combat;
    private readonly List<string> _recent = [];

    /// <summary>The player's acts (<see cref="NodeRecord.PlayerActs"/>) already seen: one more stops the AI.</summary>
    private int _playerActs;

    /// <summary>The screen as the log last wrote it.</summary>
    private string? _logged;

    // V's chance to win at the latest macro decisions, and the SL planner's expected end of the current fight.
    private double? _win;
    private double? _before;
    private double? _best;
    private (double Win, double Hp)? _fight;

    private PlayEnvironment _env = Settings.Current.Env;
    private SearchEffort _effort = Settings.Current.Effort;
    private bool _turnDecay = Settings.Current.TurnDecay;

    /// <summary>The player switched the environment: the brain gets a new run from here.</summary>
    private void Switch(PlayEnvironment env)
    {
        _env = env;
        ModLog.Info("settings", $"environment {env}");
        if (_run != null) End("aborted");
    }

    /// <summary>The player changed the search effort: the brain gets a new run from here, with that effort.</summary>
    private void Switch(SearchEffort effort)
    {
        _effort = effort;
        ModLog.Info("settings", $"effort {effort}");
        if (_run != null) End("aborted");
    }

    /// <summary>The player switched the turn decay: the brain gets a new run from here, with or without it.</summary>
    private void Switch(bool turnDecay)
    {
        _turnDecay = turnDecay;
        ModLog.Info("settings", $"turn decay {(turnDecay ? "on" : "off")}");
        if (_run != null) End("aborted");
    }

    /// <summary>A toolbar button or its key.</summary>
    public void Press(Command command)
    {
        if (_run == null) return;
        _stopped = null;
        switch (command)
        {
            case Command.Auto when _goal == Goal.Auto:
                _goal = Goal.Off;
                break;
            case Command.Auto:
                Begin(Goal.Auto);
                _best = _win;
                // The stop that paused it does not stop it again at the same decision.
                _waived = _key;
                break;
            case Command.Step when _goal != Goal.Auto:
                Begin(Goal.One);
                break;
            case Command.Finish when _goal != Goal.Auto:
                Begin(Fights(_surface) ? Goal.Turn : Goal.Room);
                break;
        }
    }

    /// <summary>A new goal: a state the AI played before without effect may be played again.</summary>
    private void Begin(Goal goal)
    {
        (_goal, _acts, _lastSurface, _waiting, _acted) = (goal, 0, null, null, null);
        ModLog.Write("auto", $"AI goal {goal}");
    }

    private void Stop(string reason)
    {
        if (_goal != Goal.Off) ModLog.Info("auto", $"AI stopped: {reason}");
        (_goal, _stopped) = (Goal.Off, reason);
    }

    public void Tick(Panel panel)
    {
        while (link.Inbox.TryDequeue(out var e)) Handle(e);
        var settings = Settings.Current;
        if (settings.Env != _env) Switch(settings.Env);
        if (settings.Effort != _effort) Switch(settings.Effort);
        if (settings.TurnDecay != _turnDecay) Switch(settings.TurnDecay);

        var run = RunManager.Instance.DebugOnlyGetState();
        var me = run != null ? LocalContext.GetMe(run) : null;
        string? id = run != null ? RunHeader.IdOf(run.Rng.StringSeed) : null;
        if (_run != null && _run != id) End(_last.Over ? _last.Won ? "won" : "lost" : "aborted");
        else if (_run != null && !settings.Assist) End("aborted");
        bool single = run is { Players.Count: 1 } && me != null && id != null;
        // Read with assist off and without a brain too: the SL record needs the screen the player answers.
        string note = "";
        var asking = single ? _screens.Read(run!, me!, out note) : null;
        if (asking != null) NodeRecord.Seen(asking);
        Screen(asking, note);
        // The player acted (a step the AI did not take, or going back): the AI hands over.
        if (NodeRecord.PlayerActs != _playerActs)
        {
            _playerActs = NodeRecord.PlayerActs;
            if (_goal != Goal.Off) Stop(L10n.T("you acted"));
        }
        // The screen does not show a step's result before the step ends.
        if (asking != null && NodeRecord.Running(asking.Surface)) asking = null;

        if (!settings.Assist)
        {
            panel.Hide();
            return;
        }
        string champion = _champion ?? "—";
        if (!_connected)
        {
            if (_down == null) panel.Show(champion, L10n.T("starting"), _env, L10n.T("Connecting to the brain"), link.Where);
            else panel.Show(champion, L10n.T("offline"), _env, L10n.T("No brain running"), L10n.T("{0} · trying again every 2 s", _down), Tone.Warn);
            return;
        }
        if (run == null || me == null || id == null)
        {
            panel.Show(champion, L10n.T("ready"), _env, L10n.T("Start or continue a run"), L10n.T("single-player only"));
            return;
        }
        if (!single)
        {
            panel.Show(champion, L10n.T("off"), null, L10n.T("Off in multiplayer"));
            return;
        }
        if (_run == null)
        {
            (_run, _champion, _failure, _next, _sentNode) = (id, null, null, 0, -1);
            link.Send(new Hello(gameVersion, _env.ToString(), "mod", new RunHeader(id, me.Character.Id.Entry, run.AscensionLevel), settings.EffortName, settings.TurnDecay));
            ModLog.Write("brain", $"hello: run {id}, {me.Character.Id.Entry} A{run.AscensionLevel}, {_env}, effort {settings.EffortName ?? "the brain's"}, turn decay {(settings.TurnDecay ? "on" : "off")}");
        }
        _last = (run.IsGameOver, run.IsGameOver && run.CurrentRoom?.IsVictoryRoom == true, run.TotalFloor);
        if (run.IsGameOver && _goal != Goal.Off) Stop(L10n.T("the run is over"));
        if (_failure != null)
        {
            panel.Show(champion, L10n.T("off for this run"), _env, L10n.T("Brain error"), L10n.T("{0} · back on at the next run", _failure), Tone.Error);
            return;
        }
        if (_champion == null)
        {
            panel.Show(champion, L10n.T("run sent"), _env, L10n.T("Waiting for the brain"), L10n.T("it loads the champion for this run"));
            return;
        }
        if (asking != null)
        {
            var view = asking.Obs.Run;
            _line = $"{Describe.Character(view.Character)} · A{view.Ascension} · {L10n.T("floor {0}", view.Floor)} · {L10n.T("HP {0}/{1}", view.Hp, view.MaxHp)}";
            _surface = asking.Obs.Screen is CardChoiceScreen { Combat: not null } ? Surfaces.Combat : asking.Surface;
        }
        string status = _line ?? L10n.T("ready");

        if (asking == null)
        {
            (_key, _stable) = (null, 0);
            Nudge();
            panel.Show(champion, status, _env, L10n.T("Waiting for a decision"), note != "" ? L10n.T(note) : null, drive: Drive());
            return;
        }
        _idle = Environment.TickCount64;
        // SL plans a combat and values every option outside it on the node's clone; without one there is nothing to play
        // on: the brain would refuse the run, or value the options on the record's last state, not the screen's.
        if (_env == PlayEnvironment.SL && (NodeRecord.Checkpoint == null || NodeRecord.Broken != null))
        {
            (_key, _stable) = (null, 0);
            // Auto-play hands the room over, rather than wait on it: nothing plays it until the game's next save.
            if (_goal != Goal.Off) Stop(L10n.T("no SL clone of this room"));
            panel.Show(champion, status, _env, L10n.T("No SL clone of this room"),
                L10n.T("{0} · the next room has one", NodeRecord.Broken ?? L10n.T("no checkpoint")), Tone.Warn, Drive());
            return;
        }
        string hash = DecisionRequest.HashOf(asking.Obs);
        // The same observation can allow different actions (whether a rest site may be left yet).
        string key = hash + " " + string.Join(" ", asking.Legal);
        if (key != _key) (_key, _stable) = (key, 0);
        if (++_stable < IdleFrames) return;

        if (_answer is { } answer && answer.Key == key)
        {
            if (_shown == 0) _shown = Environment.TickCount64;
            Play(asking, answer, run, me);
            panel.Show(champion, status, _env, answer.Shown, Sub(answer), answer.Foot, answer.Route, answer.Plan, Drive());
            return;
        }
        _shown = 0;
        if (key == _askedKey && _asked != null)
        {
            panel.Show(champion, status, _env, L10n.T("Thinking…"), $"{(Environment.TickCount64 - _asked.Sent) / 1000.0:0.0} s", drive: Drive());
            return;
        }
        panel.Show(champion, status, _env, L10n.T("Thinking…"), drive: Drive());
        if (key == _askedKey) return;
        if (_asked != null)
        {
            if (_asked.Cancelled == null)
            {
                link.Send(new Cancel(_asked.Id));
                _asked = _asked with { Cancelled = Environment.TickCount64 };
                ModLog.Write("decision", $"{_asked.Id} cancelled: the state moved on");
            }
            return;
        }
        bool sl = _env == PlayEnvironment.SL;
        if (sl) SendNode();
        // In the same frame as the observation, with the action queue empty: the combat as it is shown.
        string? export = sl && asking.Surface == Surfaces.Combat
            ? Convert.ToBase64String(_export.Value.Write(gameVersion, new EngineExport.Around(NodeRecord.Prompts)).Bytes)
            : null;
        var trace = sl ? NodeRecord.Steps : null;
        (_asked, _askedKey) = (new Asked(_next++, key, asking.Obs, Environment.TickCount64), key);
        link.Send(new DecisionRequest(_asked.Id, hash, asking.Surface, asking.Obs, asking.Legal, export, trace));
        ModLog.Write("decision", $"{_asked.Id} asked: {asking.Surface}, floor {asking.Obs.Run.Floor}, {asking.Legal.Count} legal" +
                                 (trace != null ? $", checkpoint {NodeRecord.Node} + {trace.Count} steps" : "") + (export != null ? $", live combat {export.Length} chars" : ""));
    }

    /// <summary>The screen's decision in the log, when it changes: its surface and legal actions, or what the game waits on.</summary>
    private void Screen(Asking? asking, string note)
    {
        string now = asking != null ? $"{asking.Surface}: {(asking.Legal.Count > 0 ? string.Join(" ", asking.Legal) : "a card prompt")}"
            : note != "" ? $"no decision: {note}" : "no decision";
        if (now == _logged) return;
        _logged = now;
        ModLog.Write("screen", now);
    }

    /// <summary>
    /// The AI plays the answer shown, if the player asked it to: once per state, after auto-play's wait, unless the
    /// goal is reached or a stop comes first.
    /// </summary>
    private void Play(Asking asking, Answered answer, RunState run, MegaCrit.Sts2.Core.Entities.Players.Player me)
    {
        if (_goal == Goal.Off) return;
        long now = Environment.TickCount64;
        if (_acted is { } acted && acted.Key == answer.Key)
        {
            if (now - acted.At > Patience) Stop(L10n.T("the action did nothing"));
            return;
        }
        // Auto-play's wait comes before each action, and before each try again (an ancient's dialogue, line by line).
        if ((_goal != Goal.One || _waiting != null) && now - _shown < Settings.Current.Delay) return;
        if (_goal == Goal.Room && asking.Surface == Surfaces.Map && _acts > 0 && _lastSurface != Surfaces.Map
            || _goal == Goal.Turn && !Fights(_surface))
        {
            _goal = Goal.Off;
            return;
        }
        if (_goal == Goal.Auto && answer.Key != _waived && Stops(asking, answer) is { } reason)
        {
            Stop(reason);
            return;
        }
        Acted done;
        // Before the press: a button's handler may take the step at once, inside it.
        NodeRecord.Expect(Step(asking, answer.Answer.Action));
        try
        {
            done = Executor.Run(asking, answer.Answer.Action, run, me);
        }
        catch (Exception e)
        {
            NodeRecord.Expect(null);
            ModLog.Warn("auto", $"could not play {answer.Answer.Action}: {e}");
            Stop(L10n.T("the AI cannot press this: {0}", e.Message));
            return;
        }
        if (done == Acted.Again)
        {
            NodeRecord.Expect(null);
            _shown = now;
            if (_waiting is not { } waiting || waiting.Key != answer.Key) _waiting = (answer.Key, now);
            else if (now - waiting.Since > Patience) Stop(L10n.T("the AI cannot press this: {0}", answer.Shown.Next));
            return;
        }
        (_acted, _waiting) = ((answer.Key, now), null);
        ModLog.Write("auto", $"AI pressed {answer.Answer.Action} for decision {answer.Id} ({asking.Surface})");
        link.Send(new Executed(answer.Id, answer.Answer.Action, true));
        _recent.Insert(0, $"{L10n.T("floor {0}", asking.Obs.Run.Floor)} · {answer.Shown.Next}{Value(answer)}");
        if (_recent.Count > Remembered) _recent.RemoveAt(_recent.Count - 1);
        (_acts, _lastSurface) = (_acts + 1, asking.Surface);
        bool ended = CombatAction.TryParse(answer.Answer.Action, out var action) && action.Kind == CombatActionKind.EndTurn;
        if (_goal == Goal.One || _goal == Goal.Turn && ended) _goal = Goal.Off;
    }

    /// <summary>The action of the step the record takes for the AI's answer: a card prompt's picks as the game's own
    /// indexes, as the record names them; every other answer as it is.</summary>
    private static string Step(Asking asking, string action) =>
        asking.Surface == Surfaces.CardChoice && asking.GameIndex is { } index && MacroAction.Parse(action) is { Verb: MacroAction.PickVerb } pick
            ? string.Join(",", pick.Picks.Select(i => index[i]))
            : action;

    /// <summary>Why auto-play stops before this answer, by the player's settings; null to play it.</summary>
    private string? Stops(Asking asking, Answered answer)
    {
        var settings = Settings.Current;
        if (settings.StopAtElites && asking.Surface == Surfaces.Map)
        {
            string? type = Describe.Point(answer.Answer.Action, asking.Obs.Run.Map);
            if (type == "Elite") return L10n.T("an elite is next");
            if (type == "Boss") return L10n.T("the boss is next");
        }
        if (settings.StopOnDrop && _win is { } win && _best is { } best && win < best - Drop)
        {
            _best = win;
            return L10n.T("the win chance fell {0} points", Math.Round((best - win) * 100, 1).ToString(CultureInfo.InvariantCulture));
        }
        return null;
    }

    /// <summary>Auto-play presses on where the game waits without a decision (a chest, a proceed button), after a moment.</summary>
    private void Nudge()
    {
        if (_goal is not (Goal.Auto or Goal.Room) || Environment.TickCount64 - _idle < NudgeAfter) return;
        _idle = Environment.TickCount64;
        if (Executor.Nudge() is { } pressed) ModLog.Info("auto", $"auto-play pressed the {pressed}");
    }

    /// <summary>Whether the decisions on this surface are a combat's: its own and its card prompts.</summary>
    private static bool Fights(string surface) => surface == Surfaces.Combat;

    /// <summary>The toolbar and the strip: the goal, the labels the screen gives the step buttons, V, the AI's last actions.</summary>
    private Drive Drive()
    {
        bool fight = Fights(_surface);
        string step = fight ? L10n.T("Play one") : _surface == Surfaces.Map ? L10n.T("Step") : L10n.T("Pick for me");
        string finish = fight ? L10n.T("Finish turn") : _surface == Surfaces.Map ? L10n.T("Next room") : L10n.T("Finish room");
        var (pill, tone) = _stopped != null ? (L10n.T("AI stopped: {0}", _stopped), Tone.Warn)
            : _goal == Goal.Auto ? (L10n.T("Auto on"), Tone.Plain)
            : _goal != Goal.Off ? (L10n.T("Acting"), Tone.Plain)
            : (L10n.T("Your move"), Tone.Plain);
        return new Drive(_goal == Goal.Auto, step, finish, pill, tone, _win, _win - _before, [.. _recent]);
    }

    /// <summary>The line under the next action: V's chance to win after it, or how the fight is expected to end.</summary>
    private string? Sub(Answered answer) => answer.Shown.Kind switch
    {
        ShownKind.Run when answer.Answer.Candidates is { Count: > 0 } c => L10n.T("win chance {0}", Percent(c[0].Value)),
        ShownKind.Fight or ShownKind.Score when _fight is { } fight => L10n.T("this fight {0} · {1} HP left", Percent(fight.Win, whole: true), Math.Round(fight.Hp)),
        _ => null,
    };

    /// <summary>The value of the chosen action, for the list of the AI's last actions.</summary>
    private static string Value(Answered answer) => answer.Answer.Candidates is { Count: > 0 } c
        ? answer.Shown.Kind switch
        {
            ShownKind.Run => " · " + Percent(c[0].Value),
            ShownKind.Fight when c[0].Win is { } win => " · " + Percent(win, whole: true),
            _ => "",
        }
        : "";

    private static string Percent(double p, bool whole = false) => (p * 100).ToString(whole ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>SL: the current checkpoint, once, before the first decision after it.</summary>
    private void SendNode()
    {
        if (NodeRecord.Checkpoint is not { } checkpoint || NodeRecord.Node == _sentNode) return;
        link.Send(new NodeCheckpoint(NodeRecord.Node, checkpoint));
        _sentNode = NodeRecord.Node;
    }

    private void Handle(LinkEvent e)
    {
        switch (e)
        {
            case LinkUp:
                _connected = true;
                ModLog.Info("brain", "connected to the brain");
                break;
            case LinkDown down:
                // A new connection starts again from hello.
                if (_connected || _down != down.Reason) ModLog.Info("brain", $"no brain: {down.Reason}");
                (_connected, _down, _run) = (false, down.Reason, null);
                // The toolbar is gone without a brain: the AI stops, and starts again only when the player asks.
                Reset();
                Forget();
                break;
            case Received { Message: Ready ready }:
                _champion = ready.Shown;
                ModLog.Write("brain", $"ready: {ready.Champion}");
                break;
            case Received { Message: Recommendation answer }:
                if (_asked?.Id != answer.DecisionId) break;
                // Cleared first, so an answer that cannot be described does not leave the mod waiting for it.
                var asked = _asked;
                _asked = null;
                long now = Environment.TickCount64;
                if (asked.Cancelled is { } cancelled)
                {
                    ModLog.Write("decision", $"{asked.Id} answered after it was cancelled: the mod waited {now - cancelled} ms for it");
                    break;
                }
                ModLog.Write("decision", $"{asked.Id} answered in {now - asked.Sent} ms by {answer.Brain}: {answer.Action}" +
                                         (answer.Candidates is { Count: > 0 } c ? $"; best {string.Join(", ", c.Take(3).Select(Candidate))}" : ""));
                var shown = Describe.Recommendation(answer, asked.Obs);
                Note(shown, answer);
                _answer = new(asked.Id, asked.Key, answer, shown, $"{answer.Brain} · {(Environment.TickCount64 - asked.Sent) / 1000.0:0.0} s",
                    MapRoute.Of(answer, asked.Obs), CombatPlan.Of(answer, asked.Obs));
                break;
            case Received { Message: WireError error }:
                ModLog.Warn("brain", $"brain: {error.Message}");
                // An SL decision is asked on the node's clone, which the record builds: when it fails there, the record is
                // off for the rest of the room, as one the player went back into, and the brain stays on for the run.
                if (_asked != null && _env == PlayEnvironment.SL && NodeRecord.Checkpoint != null && NodeRecord.Broken == null)
                {
                    string line = error.Message.Split('\n')[0];
                    int cause = line.LastIndexOf(": ", StringComparison.Ordinal);
                    NodeRecord.CloneFailed($"the brain could not follow it ({(cause < 0 ? line : line[(cause + 2)..])})");
                    Forget();
                    break;
                }
                // The brain refused the run or could not answer: the run goes on without it.
                if (_run != null)
                {
                    _failure = error.Message;
                    link.Send(new RunEnd(false, _last.Floor, "failed"));
                }
                Forget();
                break;
            case Received { Message: var other }:
                ModLog.Warn("brain", $"unexpected {other.GetType().Name} from the brain");
                break;
        }
    }

    /// <summary>A scored candidate in the log: its action, value and, from a fight's plan, its chance to win and HP left.</summary>
    private static string Candidate(Candidate c) =>
        string.Create(CultureInfo.InvariantCulture, $"{c.Action} {c.Value:0.####}{(c.Win is { } w ? $" win {w:0.###}" : "")}{(c.Hp is { } hp ? $" hp {hp:0.#}" : "")}");

    /// <summary>Keeps V's chance to win from a macro answer, and the fight's expected end from a planned combat answer.</summary>
    private void Note(Shown shown, Recommendation answer)
    {
        if (answer.Candidates is not { Count: > 0 } candidates) return;
        switch (shown.Kind)
        {
            case ShownKind.Run:
                (_before, _win, _fight) = (_win, candidates[0].Value, null);
                if (_goal == Goal.Auto) _best = Math.Max(_best ?? _win.Value, _win.Value);
                break;
            case ShownKind.Fight when candidates[0] is { Win: { } win, Hp: { } hp }:
                _fight = (win, hp);
                break;
        }
    }

    private void End(string reason)
    {
        if (_failure == null) link.Send(new RunEnd(_last.Won, _last.Floor, reason));
        ModLog.Write("brain", $"run end: {reason}, floor {_last.Floor}");
        _run = null;
        Reset();
        Forget();
    }

    /// <summary>The AI's play and what the panel keeps of the run, for a run that ended or a brain that went.</summary>
    private void Reset()
    {
        (_goal, _stopped) = (Goal.Off, null);
        (_win, _before, _best, _fight, _line) = (null, null, null, null, null);
        _recent.Clear();
    }

    private void Forget() => (_asked, _askedKey, _answer, _key, _stable, _acted, _waiting) = (null, null, null, null, 0, null, null);
}
