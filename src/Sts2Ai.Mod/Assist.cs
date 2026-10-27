using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// Assist mode (docs: mod §3, §5): once the game has been idle on a decision for a few frames, asks the brain about
/// that state, and shows the answer while the state lasts. The player acts; the mod never does.
/// One decision is in flight at a time: when the state moves on first, the mod cancels it and waits for its answer
/// before asking about the new state, so a slow brain never falls behind.
/// </summary>
internal sealed class Assist(BrainLink link, string gameVersion)
{
    /// <summary>How many frames in a row a state must hold before it counts as idle.</summary>
    private const int IdleFrames = 3;

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

    private long _next;
    private Asked? _asked;
    private string? _askedKey;
    private (string Key, MapRoute? Route, string[] Lines)? _answer;
    private string? _key;
    private int _stable;

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
        if (!_connected)
        {
            panel.Show(_linkNote);
            return;
        }
        if (_run == null)
        {
            (_run, _champion, _failure, _next) = (id, null, null, 0);
            link.Send(new Hello(gameVersion, nameof(PlayEnvironment.NoSL), "mod", new RunHeader(id, me.Character.Id.Entry, run.AscensionLevel)));
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

        string status = $"{_champion}, NoSL";
        if (_screens.Read(run, me, out string note) is not { } asking)
        {
            (_key, _stable) = (null, 0);
            panel.Show(status, note);
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
        (_asked, _askedKey) = (new Asked(_next++, key, asking.Obs), key);
        link.Send(new DecisionRequest(_asked.Id, hash, asking.Surface, asking.Obs, asking.Legal));
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
