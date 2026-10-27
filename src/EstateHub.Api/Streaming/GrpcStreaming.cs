using Grpc.Core;

namespace EstateHub.Api.Streaming;

/// <summary>
/// Reusable plumbing for server-streaming RPCs. A streaming method only has to say where events
/// come from and how to turn them into proto messages:
/// <code>
/// public override Task SubscribeThings(SubscribeThingsRequest request,
///     IServerStreamWriter&lt;AuditLogStreamEvent&gt; responseStream, ServerCallContext context) =>
///     GrpcStreaming.PumpAsync(
///         _things.SubscribeAsync(request.OwnerId, context.CancellationToken),
///         responseStream, context,
///         toMessage: dto => new AuditLogStreamEvent { Thing = ToEntry(dto) },
///         heartbeat: hb => new AuditLogStreamEvent { Heartbeat = hb },
///         _streamingOptions.HeartbeatInterval);
/// </code>
/// It sends a "connected" heartbeat immediately, a "keepalive" heartbeat whenever the stream has
/// been idle for <c>heartbeatInterval</c>, writes events one at a time (gRPC forbids concurrent
/// writes), and ends quietly when the client disconnects or cancels.
/// </summary>
public static class GrpcStreaming
{
    public const string ConnectedReason = "connected";
    public const string KeepaliveReason = "keepalive";

    public static async Task PumpAsync<TEvent, TMessage>(
        IAsyncEnumerable<TEvent> events,
        IServerStreamWriter<TMessage> responseStream,
        ServerCallContext context,
        Func<TEvent, TMessage> toMessage,
        Func<StreamHeartbeat, TMessage> heartbeat,
        TimeSpan heartbeatInterval)
    {
        var ct = context.CancellationToken;
        // Own token for the source, so a pending read can always be stopped before disposal
        // (disposing an async iterator while MoveNextAsync is in flight throws).
        using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var enumerator = events.GetAsyncEnumerator(sourceCts.Token);
        Task<bool>? next = null;

        try
        {
            await responseStream.WriteAsync(heartbeat(NewHeartbeat(ConnectedReason)), ct);

            next = enumerator.MoveNextAsync().AsTask();
            while (true)
            {
                var idle = Task.Delay(heartbeatInterval, ct);
                var finished = await Task.WhenAny(next, idle);

                if (finished == idle)
                {
                    ct.ThrowIfCancellationRequested();
                    await responseStream.WriteAsync(heartbeat(NewHeartbeat(KeepaliveReason)), ct);
                    continue;
                }

                if (!await next)
                    return; // source completed

                await responseStream.WriteAsync(toMessage(enumerator.Current), ct);
                next = enumerator.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected or cancelled: a normal way for a stream to end.
        }
        finally
        {
            if (next is { IsCompleted: false })
            {
                await sourceCts.CancelAsync();
                try { await next; } catch (OperationCanceledException) { }
            }
        }
    }

    private static StreamHeartbeat NewHeartbeat(string reason) => new()
    {
        Reason = reason,
        ServerTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
    };
}
