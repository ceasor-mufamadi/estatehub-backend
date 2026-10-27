using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EstateHub.Application.Streaming;

namespace EstateHub.Infrastructure.Streaming;

/// <summary>
/// In-process <see cref="IEventBroadcaster{T}"/>: each subscriber gets its own bounded channel.
/// If a subscriber falls more than <see cref="SubscriberBufferSize"/> messages behind, the oldest
/// queued messages are dropped for that subscriber only, so one slow client never stalls publishers.
/// </summary>
public sealed class InMemoryEventBroadcaster<T> : IEventBroadcaster<T>
{
    public const int SubscriberBufferSize = 256;

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public ValueTask PublishAsync(T message, CancellationToken ct = default)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            if (subscriber.Filter is null || subscriber.Filter(message))
                subscriber.Channel.Writer.TryWrite(message);
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<T> SubscribeAsync(Func<T, bool>? filter, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(SubscriberBufferSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(channel, filter);

        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(ct))
                yield return message;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private sealed record Subscriber(Channel<T> Channel, Func<T, bool>? Filter);
}
