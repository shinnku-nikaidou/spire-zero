using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// Assist mode (docs: mod §3, §5, §6): once the game has been idle on a decision for a few frames, asks the brain about
/// that state, and shows the answer while the state lasts. The player acts; the mod never does.
/// One decision is in flight at a time: when the state moves on first, the mod cancels it and waits for its answer
/// before asking about the new state, so a slow brain never falls behind.
/// The environment is the player's choice (the panel's SL box), kept across games. Switching it tells the brain a new
/// run starts at this point. In SL the brain plans combats on a clone of the node, so before each decision the mod sends
/// the node's checkpoint and the steps taken in it (<see cref="NodeRecord"/>); NoSL sends neither.
/// </summary>
internal sealed class Assist(BrainLink link, string gameVersion)
{
    /// <summary>How many frames in a row a state must hold before it counts as idle.</summary>
    private const int IdleFrames = 3;

    /// <summary>Where the chosen environment is kept: beside the brain's socket, outside the game's profile.</summary>
    private static readonly string EnvFile = Path.Combine(Path.GetDirectoryName(BrainSocket.DefaultPath)!, "env");

    /// <param name="Key">The observation's hash and the legal actions: what the answer is for.</param>
    private sealed record Asked(long Id, string Key, Observation Obs, bool Cancelled = false);

    private readonly Screens _screens = new();
    private bool _connected;
    private string _linkNote = "connecting to the brain";

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
    private (string Key, MapRoute? Route, string[] Lines)? _answer;
    private string? _key;
    private int _stable;

    public PlayEnvironment Env { get; private set; } = ReadEnv();

    /// <summary>The player switched the environment: the brain gets a new run from here.</summary>
    public void Switch(PlayEnvironment env)
    {
        if (env == Env) return;
        Env = env;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EnvFile)!);
            File.WriteAllText(EnvFile, env.ToString());
        }
        catch (IOException e)
        {
            Log.Warn($"{Host.Tag} could not keep the environment in {EnvFile}: {e.Message}");
        }
        Log.Info($"{Host.Tag} environment {env}");
        if (_run != null) End("aborted");
    }

    /// <summary>The kept choice; SL until the player chooses.</summary>
    private static PlayEnvironment ReadEnv()
    {
        try
        {
            return File.Exists(EnvFile) && Enum.TryParse<PlayEnvironment>(File.ReadAllText(EnvFile).Trim(), out var env) ? env : PlayEnvironment.SL;
        }
        catch (IOException)
        {
            return PlayEnvironment.SL;
        }
    }

    public void Tick(Panel panel)
    {
        while (link.Inbox.TryDequeue(out var e)) Handle(e);

        var run = RunManager.Instance.DebugOnlyGetState();
        var me = run != null ? LocalContext.GetMe(run) : null;
        string? id = run != null ? RunHeader.IdOf(run.Rng.StringSeed) : null;
        if (_run != null && _run != id) End(_last.Over ? _last.Won ? "won" : "lost" : "aborted");
        if (run == null || me == null || id == null)
        {
            panel.Show(_connected ? "ready, no run" : _linkNote);
            return;
        }
        if (run.Players.Count > 1)
        {
            panel.Show("off in multiplayer");
            return;
        }
        // Read without a brain too: the SL record needs the screen the player answers.
        var asking = _screens.Read(run, me, out string note);
        if (asking != null) NodeRecord.Seen(asking);
        // The screen does not show a step's result before the step ends.
        if (asking != null && NodeRecord.Running(asking.Surface)) asking = null;
        if (!_connected)
        {
            panel.Show(_linkNote);
            return;
        }
        if (_run == null)
        {
            (_run, _champion, _failure, _next, _sentNode, _sentSteps) = (id, null, null, 0, -1, 0);
            link.Send(new Hello(gameVersion, Env.ToString(), "mod", new RunHeader(id, me.Character.Id.Entry, run.AscensionLevel)));
        }
        _last = (run.IsGameOver, run.IsGameOver && run.CurrentRoom?.IsVictoryRoom == true, run.TotalFloor);
        if (_failure != null)
        {
            panel.Show($"brain error, off for this run: {_failure}");
            return;
        }
        if (_champion == null)
        {
            panel.Show("waiting for the brain");
            return;
        }

        string status = $"{_champion}, {Env}";
        if (asking == null)
        {
            (_key, _stable) = (null, 0);
            panel.Show(status, note);
            return;
        }
        // SL plans a combat on the node's clone; without one there is nothing to plan on (prompts go to p0, which needs none).
        if (Env == PlayEnvironment.SL && asking.Surface == Surfaces.Combat && (NodeRecord.Checkpoint == null || NodeRecord.Broken != null))
        {
            (_key, _stable) = (null, 0);
            panel.Show(status, $"no SL clone of this room ({NodeRecord.Broken}); the next room has one");
            return;
        }
        string hash = DecisionRequest.HashOf(asking.Obs);
        // The same observation can allow different actions (whether a rest site may be left yet).
        string key = hash + " " + string.Join(" ", asking.Legal);
        if (key != _key) (_key, _stable) = (key, 0);
        if (++_stable < IdleFrames) return;

        if (_answer is { } answer && answer.Key == key)
        {
            panel.Show(status, answer.Lines, answer.Route);
            return;
        }
        panel.Show(status, "thinking…");
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
        if (Env == PlayEnvironment.SL) SendNode();
        (_asked, _askedKey) = (new Asked(_next++, key, asking.Obs), key);
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
                if (_connected || _linkNote != $"no brain: {down.Reason}") Log.Info($"{Host.Tag} no brain: {down.Reason}");
                (_connected, _linkNote, _run) = (false, $"no brain: {down.Reason}", null);
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
                if (!asked.Cancelled) _answer = (asked.Key, MapRoute.Of(answer, asked.Obs), Describe.Recommendation(answer, asked.Obs));
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
