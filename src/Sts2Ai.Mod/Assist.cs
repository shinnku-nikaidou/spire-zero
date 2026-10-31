using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// Assist mode (docs: mod/loop, mod/sl-record §7): once the game has been idle on a decision for a few frames, asks the brain about
/// that state, and shows the answer while the state lasts. The player acts; the mod never does.
/// One decision is in flight at a time: when the state moves on first, the mod cancels it and waits for its answer
/// before asking about the new state, so a slow brain never falls behind.
/// The environment is the player's choice (<see cref="Settings"/>), kept across games. Switching it, or turning assist
/// off, ends the run as the brain sees it; the next decision starts a new one from that point. In SL the brain plans
/// combats on a clone of the node, so before each decision the mod sends the node's checkpoint and the steps taken in
/// it (<see cref="NodeRecord"/>); NoSL sends neither.
/// </summary>
internal sealed class Assist(BrainLink link, string gameVersion)
{
    /// <summary>How many frames in a row a state must hold before it counts as idle.</summary>
    private const int IdleFrames = 3;

    /// <param name="Key">The observation's hash and the legal actions: what the answer is for.</param>
    /// <param name="Sent">When it was sent, in <see cref="Environment.TickCount64"/> milliseconds.</param>
    private sealed record Asked(long Id, string Key, Observation Obs, long Sent, bool Cancelled = false);

    /// <summary>An answer as it is shown, and what it is for.</summary>
    private sealed record Answered(string Key, Shown Shown, string Foot, MapRoute? Route, CombatPlan? Plan);

    private readonly Screens _screens = new();
    private bool _connected;
    private string? _down;

    // The run the brain was told about on this connection (hello sent), and its state.
    private string? _run;
    private string? _champion;
    private string? _failure;
    private (bool Over, bool Won, int Floor) _last;

    // SL: the node and its steps the brain has been sent in this run.
    private int _sentNode;
    private int _sentSteps;

    private long _next;
    private Asked? _asked;
    private string? _askedKey;
    private Answered? _answer;
    private string? _key;
    private int _stable;

    private PlayEnvironment _env = Settings.Current.Env;

    /// <summary>The player switched the environment: the brain gets a new run from here.</summary>
    private void Switch(PlayEnvironment env)
    {
        _env = env;
        Log.Info($"{Host.Tag} environment {env}");
        if (_run != null) End("aborted");
    }

    public void Tick(Panel panel)
    {
        while (link.Inbox.TryDequeue(out var e)) Handle(e);
        var settings = Settings.Current;
        if (settings.Env != _env) Switch(settings.Env);

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
            if (_down == null) panel.Show(champion, "starting", _env, "Connecting to the brain", BrainSocket.DefaultPath);
            else panel.Show(champion, "offline", _env, "No brain running", $"{_down} · trying again every 2 s", Tone.Warn);
            return;
        }
        if (run == null || me == null || id == null)
        {
            panel.Show(champion, "ready", _env, "Start or continue a run", "single-player only");
            return;
        }
        if (!single)
        {
            panel.Show(champion, "off", null, "Off in multiplayer");
            return;
        }
        if (_run == null)
        {
            (_run, _champion, _failure, _next, _sentNode, _sentSteps) = (id, null, null, 0, -1, 0);
            link.Send(new Hello(gameVersion, _env.ToString(), "mod", new RunHeader(id, me.Character.Id.Entry, run.AscensionLevel)));
        }
        _last = (run.IsGameOver, run.IsGameOver && run.CurrentRoom?.IsVictoryRoom == true, run.TotalFloor);
        if (_failure != null)
        {
            panel.Show(champion, "off for this run", _env, "Brain error", $"{_failure} · back on at the next run", Tone.Error);
            return;
        }
        if (_champion == null)
        {
            panel.Show(champion, "run sent", _env, "Waiting for the brain", "it loads the champion for this run");
            return;
        }

        if (asking == null)
        {
            (_key, _stable) = (null, 0);
            panel.Show(champion, "ready", _env, "Waiting for a decision", note != "" ? note : null);
            return;
        }
        // SL plans a combat on the node's clone; without one there is nothing to plan on (prompts go to p0, which needs none).
        if (_env == PlayEnvironment.SL && asking.Surface == Surfaces.Combat && (NodeRecord.Checkpoint == null || NodeRecord.Broken != null))
        {
            (_key, _stable) = (null, 0);
            panel.Show(champion, "ready", _env, "No SL clone of this room", $"{NodeRecord.Broken ?? "no checkpoint"} · the next room has one", Tone.Warn);
            return;
        }
        string hash = DecisionRequest.HashOf(asking.Obs);
        // The same observation can allow different actions (whether a rest site may be left yet).
        string key = hash + " " + string.Join(" ", asking.Legal);
        if (key != _key) (_key, _stable) = (key, 0);
        if (++_stable < IdleFrames) return;

        if (_answer is { } answer && answer.Key == key)
        {
            panel.Show(champion, "ready", _env, answer.Shown, answer.Foot, answer.Route, answer.Plan);
            return;
        }
        if (key == _askedKey && _asked != null)
        {
            panel.Show(champion, "ready", _env, "Thinking…", $"{(Environment.TickCount64 - _asked.Sent) / 1000.0:0.0} s");
            return;
        }
        panel.Show(champion, "ready", _env, "Thinking…");
        if (key == _askedKey) return;
        if (_asked != null)
        {
            if (!_asked.Cancelled)
            {
                link.Send(new Cancel(_asked.Id));
                _asked = _asked with { Cancelled = true };
            }
            return;
        }
        if (_env == PlayEnvironment.SL) SendNode();
        (_asked, _askedKey) = (new Asked(_next++, key, asking.Obs, Environment.TickCount64), key);
        link.Send(new DecisionRequest(_asked.Id, hash, asking.Surface, asking.Obs, asking.Legal));
    }

    /// <summary>SL: the current node's checkpoint once, and its steps the brain has not had yet, before the next decision.</summary>
    private void SendNode()
    {
        if (NodeRecord.Checkpoint is not { } checkpoint) return;
        if (NodeRecord.Node != _sentNode)
        {
            link.Send(new NodeCheckpoint(NodeRecord.Node, checkpoint));
            (_sentNode, _sentSteps) = (NodeRecord.Node, 0);
        }
        for (var steps = NodeRecord.Steps; _sentSteps < steps.Count; _sentSteps++) link.Send(new NodeAction(_sentNode, _sentSteps, steps[_sentSteps]));
    }

    private void Handle(LinkEvent e)
    {
        switch (e)
        {
            case LinkUp:
                _connected = true;
                Log.Info($"{Host.Tag} connected to the brain");
                break;
            case LinkDown down:
                // A new connection starts again from hello.
                if (_connected || _down != down.Reason) Log.Info($"{Host.Tag} no brain: {down.Reason}");
                (_connected, _down, _run) = (false, down.Reason, null);
                Forget();
                break;
            case Received { Message: Ready ready }:
                _champion = ready.Champion;
                break;
            case Received { Message: Recommendation answer }:
                if (_asked?.Id != answer.DecisionId) break;
                // Cleared first, so an answer that cannot be described does not leave the mod waiting for it.
                var asked = _asked;
                _asked = null;
                if (!asked.Cancelled)
                    _answer = new(asked.Key, Describe.Recommendation(answer, asked.Obs), $"{answer.Brain} · {(Environment.TickCount64 - asked.Sent) / 1000.0:0.0} s",
                        MapRoute.Of(answer, asked.Obs), CombatPlan.Of(answer, asked.Obs));
                break;
            case Received { Message: WireError error }:
                // The brain refused the run or could not answer: the run goes on without it.
                Log.Warn($"{Host.Tag} brain: {error.Message}");
                if (_run != null)
                {
                    _failure = error.Message;
                    link.Send(new RunEnd(false, _last.Floor, "failed"));
                }
                Forget();
                break;
            case Received { Message: var other }:
                Log.Warn($"{Host.Tag} unexpected {other.GetType().Name} from the brain");
                break;
        }
    }

    private void End(string reason)
    {
        if (_failure == null) link.Send(new RunEnd(_last.Won, _last.Floor, reason));
        _run = null;
        Forget();
    }

    private void Forget() => (_asked, _askedKey, _answer, _key, _stable) = (null, null, null, null, 0);
}
