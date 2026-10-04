using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CommentsApi.Infrastructure.Queue;

/// <summary>Payload pushed onto the background queue after a comment is created.</summary>
public sealed record CommentCreatedNotification(Dtos.CommentDto Comment);

/// <summary>
/// Bounded-by-memory producer/consumer queue on <see cref="System.Threading.Channels"/>.
/// Publishing is fire-and-forget: the HTTP response never waits for downstream processing.
/// </summary>
public sealed class CommentCreatedQueue
{
    private readonly Channel<CommentCreatedNotification> _channel =
        Channel.CreateUnbounded<CommentCreatedNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private int _pending;
    private int _processed;

    public int Pending => Volatile.Read(ref _pending);

    public int Processed => Volatile.Read(ref _processed);

    public bool Enqueue(CommentCreatedNotification notification)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(notification))
        {
            return true;
        }

        Interlocked.Decrement(ref _pending);
        return false;
    }

    public IAsyncEnumerable<CommentCreatedNotification> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Called by the consumer once a notification has been fully handled.</summary>
    public void MarkProcessed()
    {
        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _processed);
    }
}
