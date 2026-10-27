using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using Sts2Ai.GameBridge;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>
/// Assist mode (docs: mod §3, §5): once the game has been idle on a decision for a few frames, asks the brain about
/// that state, and shows the answer while the state lasts. The player acts; the mod never does.
/// One decision is in flight at a time: when the state moves on first, the mod cancels it and waits for its answer
/// before asking about the new state, so a slow brain never falls behind.
/// Only combat is covered yet; any other screen is left to the player.
/// </summary>
internal sealed class Assist(BrainLink link, string gameVersion)
{
    /// <summary>How many frames in a row a state must hold before it counts as idle.</summary>
    private const int IdleFrames = 3;

    private sealed record Asked(long Id, string Hash, Observation Obs, bool Cancelled = false);

    private bool _connected;
    private string _linkNote = "connecting to the brain";

    // The run the brain was told about on this connection (hello sent), and its state.
    private string? _run;
    private string? _champion;
    private string? _failure;
    private (bool Over, bool Won, int Floor) _last;

    private long _next;
    private Asked? _asked;
    private string? _askedHash;
    private (Recommendation Answer, Observation Obs)? _answer;
    private string? _hash;
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
        if (Idle(me) is not { } st)
        {
            (_hash, _stable) = (null, 0);
            panel.Show(status, CombatManager.Instance.IsInProgress ? "" : "manual: only combat is covered yet");
            return;
        }
        var obs = Observe.Of(run, me, new CombatScreen { Combat = Observe.Combat(st, me) });
        string hash = DecisionRequest.HashOf(obs);
        if (hash != _hash) (_hash, _stable) = (hash, 0);
        if (++_stable < IdleFrames) return;

        if (_answer is { } answer && answer.Answer.ObsHash == hash)
        {
            panel.Show(status, Describe.Recommendation(answer.Answer, answer.Obs));
            return;
        }
        panel.Show(status, "thinking…");
        if (hash == _askedHash) return;
        if (_asked != null)
        {
            if (!_asked.Cancelled)
            {
                link.Send(new Cancel(_asked.Id));
                _asked = _asked with { Cancelled = true };
            }
            return;
        }
        var legal = CombatActions.Legal(st, me).Select(a => a.ToString()).ToList();
        (_asked, _askedHash) = (new Asked(_next++, hash, obs), hash);
        link.Send(new DecisionRequest(_asked.Id, hash, Surfaces.Combat, obs, legal));
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
                if (!_asked.Cancelled) _answer = (answer, _asked.Obs);
                _asked = null;
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

    private void Forget() => (_asked, _askedHash, _answer, _hash, _stable) = (null, null, null, null, 0);

    /// <summary>The combat, when the player can act and nothing on screen is still moving or covering it (docs: mod §3).</summary>
    private static CombatState? Idle(Player me)
    {
        var st = CombatManager.Instance.DebugOnlyGetState();
        return st != null && CombatActions.WaitingForInput(me) && RunManager.Instance.ActionQueueSet.IsEmpty
            && NPlayerHand.Instance is { CurrentMode: NPlayerHand.Mode.Play, InCardPlay: false }
            && NOverlayStack.Instance is { ScreenCount: 0 } && NMapScreen.Instance is not { IsOpen: true }
            ? st
            : null;
    }
}
