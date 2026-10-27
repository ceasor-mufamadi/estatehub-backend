namespace EstateHub.Application.Streaming;

/// <summary>
/// Publish/subscribe for live updates that feed server-streaming RPCs. A module publishes an
/// event after it changes data; every open stream whose filter matches receives it.
/// Registered as an open generic singleton, so inject <c>IEventBroadcaster&lt;YourDto&gt;</c>.
/// </summary>
/// <remarks>
/// Filters MUST check the estate first: one broadcaster carries every estate's events.
/// The default implementation is in-memory (single API instance). A backplane (Postgres
/// LISTEN/NOTIFY, Redis) can replace it later without changing publishers or streams.
/// </remarks>
public interface IEventBroadcaster<T>
{
    /// <summary>Delivers <paramref name="message"/> to every current subscriber. Never blocks on slow subscribers.</summary>
    ValueTask PublishAsync(T message, CancellationToken ct = default);

    /// <summary>
    /// Yields messages published after the call (optionally filtered) until <paramref name="ct"/>
    /// is cancelled — typically the gRPC call's cancellation token, which fires when the client disconnects.
    /// </summary>
    IAsyncEnumerable<T> SubscribeAsync(Func<T, bool>? filter, CancellationToken ct);
}
