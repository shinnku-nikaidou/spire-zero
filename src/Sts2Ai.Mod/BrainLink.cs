using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod;

/// <summary>What the link hands to the main thread.</summary>
internal abstract record LinkEvent;

/// <summary>Connected to the brain: nothing sent before this reached it.</summary>
internal sealed record LinkUp : LinkEvent;

internal sealed record LinkDown(string Reason) : LinkEvent;

internal sealed record Received(WireMessage Message) : LinkEvent;

/// <summary>
/// The mod's end of the wire. All IO runs on background threads, which never touch the game:
/// they send what the main thread queues and queue what arrives, and <see cref="Inbox"/> is drained once per frame.
/// The brain is the one shipped beside the mod (<see cref="BundledBrain"/>), a child process of the game; the link starts
/// it again whenever it ends or the connection drops.
/// </summary>
internal sealed class BrainLink
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    private readonly BundledBrain? _bundled;
    private volatile BlockingCollection<WireMessage>? _outbox;

    private BrainLink(BundledBrain? bundled) => _bundled = bundled;

    public ConcurrentQueue<LinkEvent> Inbox { get; } = new();

    /// <summary>Where the brain comes from, as the panel says it while connecting.</summary>
    public string Where => _bundled?.Launcher ?? NoBrain;

    private static string NoBrain => "no brain ships with this copy of the mod for this platform";

    public static BrainLink Start(BundledBrain? bundled)
    {
        var link = new BrainLink(bundled);
        new Thread(link.Run) { IsBackground = true, Name = "sts2ai brain link" }.Start();
        return link;
    }

    /// <summary>Queues a message; it is dropped when there is no connection (the next connection starts again from hello).</summary>
    public void Send(WireMessage message)
    {
        try
        {
            _outbox?.Add(message);
        }
        catch (InvalidOperationException)
        {
            // The connection closed meanwhile.
        }
    }

    private void Run()
    {
        while (true)
        {
            string reason;
            try
            {
                reason = _bundled?.Run(Session) ?? NoBrain;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or NotSupportedException or Win32Exception
                                          or UnauthorizedAccessException)
            {
                reason = e.Message;
            }
            Inbox.Enqueue(new LinkDown(reason));
            Thread.Sleep(Retry);
        }
    }

    /// <summary>Talks to the brain until it closes the connection; why it ended.</summary>
    private string Session(WireStream wire)
    {
        var outbox = new BlockingCollection<WireMessage>();
        var writer = new Thread(() => Write(wire, outbox)) { IsBackground = true, Name = "sts2ai brain writer" };
        _outbox = outbox;
        Inbox.Enqueue(new LinkUp());
        writer.Start();
        try
        {
            while (wire.Receive() is { } message) Inbox.Enqueue(new Received(message));
            return "the brain closed the connection";
        }
        finally
        {
            _outbox = null;
            outbox.CompleteAdding();
        }
    }

    private static void Write(WireStream wire, BlockingCollection<WireMessage> outbox)
    {
        try
        {
            foreach (var message in outbox.GetConsumingEnumerable()) wire.Send(message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The reader sees the connection go too.
        }
    }
}
