using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace EstateHub.Api.Streaming;

/// <summary>
/// Caps open server streams per user (SECURITY.md layer 6: 5 by default) so one account can't tie
/// up the server with thousands of subscriptions. Dispose the lease when the stream ends.
/// </summary>
public sealed class StreamConnectionLimiter
{
    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<StreamingOptions> _options;

    public StreamConnectionLimiter(IOptionsMonitor<StreamingOptions> options)
    {
        _options = options;
    }

    public IDisposable Acquire(string userId)
    {
        var max = _options.CurrentValue.MaxStreamsPerUser;
        var count = _open.AddOrUpdate(userId, 1, (_, n) => n + 1);
        if (count > max)
        {
            Release(userId);
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                $"Too many live connections open (max {max}). Close another tab and try again."));
        }

        return new Lease(this, userId);
    }

    public int OpenFor(string userId) => _open.TryGetValue(userId, out var n) ? n : 0;

    private void Release(string userId)
    {
        while (true)
        {
            if (!_open.TryGetValue(userId, out var n))
                return;
            if (n <= 1 ? _open.TryRemove(new KeyValuePair<string, int>(userId, n)) : _open.TryUpdate(userId, n - 1, n))
                return;
        }
    }

    private sealed class Lease(StreamConnectionLimiter limiter, string userId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                limiter.Release(userId);
        }
    }
}
