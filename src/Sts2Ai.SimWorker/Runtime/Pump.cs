using System.Collections.Concurrent;

namespace Sts2Ai.SimWorker.Runtime;

/// <summary>
/// Single-threaded SynchronizationContext. The game's async code (actions, hooks, commands) posts its
/// continuations here, and the driver runs them on its own thread when it pumps. This keeps a combat
/// deterministic and lets the driver step it until the game is waiting for player input.
/// </summary>
internal sealed class Pump : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly SemaphoreSlim _posted = new(0);
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    public static Pump InstallOnCurrentThread()
    {
        var pump = new Pump();
        SetSynchronizationContext(pump);
        return pump;
    }

    public int Pending => _queue.Count;

    /// <summary>
    /// Set while the game waits for an answer only the driver can give (a card prompt): nothing queued runs until it is
    /// cleared, so the game stands exactly where it asked.
    /// </summary>
    public bool Held { get; set; }

    /// <summary>How often RunUntil had to block waiting for work posted from another thread, and for how long.</summary>
    public long IdleWaits { get; private set; }
    public long IdleWaitTicks { get; private set; }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        _posted.Release();
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _ownerThread)
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(s =>
        {
            try { d(s); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        }, state);
        done.Wait();
        if (error != null) throw error;
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs queued callbacks, including ones queued while draining, up to one that leaves the pump
    /// <see cref="Held"/>. Returns how many ran.</summary>
    public int Drain()
    {
        int ran = 0;
        while (!Held && _queue.TryDequeue(out var item))
        {
            _posted.Wait(0);
            item.Callback(item.State);
            ran++;
        }
        return ran;
    }

    /// <summary>
    /// Pumps until <paramref name="done"/> holds. Throws <see cref="StuckException"/> when nothing has been
    /// posted for <paramref name="idleTimeoutMs"/> while still not done, or when the pump is <see cref="Held"/> and
    /// <paramref name="done"/> does not hold: the caller does not expect what the game waits for.
    /// </summary>
    public void RunUntil(Func<bool> done, Func<string> describe, int idleTimeoutMs = 10_000)
    {
        long idleSince = Environment.TickCount64;
        while (true)
        {
            if (Drain() > 0) idleSince = Environment.TickCount64;
            if (done()) return;
            if (Held) throw new StuckException($"the game waits for an answer nothing here gives: {describe()}");
            long waitStart = Environment.TickCount64;
            IdleWaits++;
            bool posted = _posted.Wait(2);
            IdleWaitTicks += Environment.TickCount64 - waitStart;
            if (posted) continue;
            if (Environment.TickCount64 - idleSince > idleTimeoutMs)
                throw new StuckException($"no progress for {idleTimeoutMs} ms: {describe()}");
        }
    }
}

internal sealed class StuckException(string message) : Exception(message);
