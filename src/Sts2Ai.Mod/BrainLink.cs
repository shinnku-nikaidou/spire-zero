using System.Collections.Concurrent;
using System.Net.Sockets;
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
/// The mod's end of the wire (docs: mod §3, §5). All socket IO runs on background threads, which never touch the game:
/// they send what the main thread queues and queue what arrives, and <see cref="Inbox"/> is drained once per frame.
/// The link connects to the brain, and reconnects whenever the connection drops.
/// </summary>
internal sealed class BrainLink
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    private readonly string _path;
    private volatile BlockingCollection<WireMessage>? _outbox;

    private BrainLink(string path) => _path = path;

    public ConcurrentQueue<LinkEvent> Inbox { get; } = new();

    public static BrainLink Start(string path)
    {
        var link = new BrainLink(path);
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
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(_path));
                using var stream = new NetworkStream(socket);
                var wire = new WireStream(stream);
                var outbox = new BlockingCollection<WireMessage>();
                var writer = new Thread(() => Write(wire, outbox)) { IsBackground = true, Name = "sts2ai brain writer" };
                _outbox = outbox;
                Inbox.Enqueue(new LinkUp());
                writer.Start();
                try
                {
                    while (wire.Receive() is { } message) Inbox.Enqueue(new Received(message));
                    reason = "the brain closed the connection";
                }
                finally
                {
                    _outbox = null;
                    outbox.CompleteAdding();
                }
            }
            catch (Exception e) when (e is SocketException or IOException or InvalidDataException or JsonException or NotSupportedException)
            {
                reason = e.Message;
            }
            Inbox.Enqueue(new LinkDown(reason));
            Thread.Sleep(Retry);
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
