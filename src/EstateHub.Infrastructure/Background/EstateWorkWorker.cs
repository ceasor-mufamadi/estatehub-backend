using System.Collections.Concurrent;
using System.Threading.Channels;
using EstateHub.Application.Auth;
using EstateHub.Application.Background;
using EstateHub.Application.Tenancy;
using EstateHub.Infrastructure.Cron;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EstateHub.Infrastructure.Background;

/// <summary>Work waiting for the worker (in memory; a restart puts it back via each handler's recovery).</summary>
public sealed class WorkQueue : IWorkQueue
{
    private readonly Channel<EstateWork> _channel = Channel.CreateUnbounded<EstateWork>();

    public ChannelReader<EstateWork> Reader => _channel.Reader;

    public void Enqueue(EstateWork work) => _channel.Writer.TryWrite(work);
}

public sealed class WorkProgress : IWorkProgress
{
    private readonly ConcurrentDictionary<Guid, WorkProgressDto> _running = new();

    public void Report(Guid id, int done, int total, string message) => _running[id] = new WorkProgressDto(done, total, message);

    public WorkProgressDto? Get(Guid id) => _running.GetValueOrDefault(id);

    public void Clear(Guid id) => _running.TryRemove(id, out _);
}

/// <summary>
/// Runs queued estate work one item at a time, in the API process (whoever started it is watching).
/// Each item runs in its own scope for its estate, acting as the person who started it, through the
/// handler for its kind. On start, every handler recovers work interrupted by a restart.
/// </summary>
public sealed class EstateWorkWorker : BackgroundService
{
    private readonly WorkQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly EstateJobRunner _estates;
    private readonly IWorkProgress _progress;
    private readonly ILogger<EstateWorkWorker> _logger;

    public EstateWorkWorker(WorkQueue queue, IServiceScopeFactory scopes, EstateJobRunner estates, IWorkProgress progress, ILogger<EstateWorkWorker> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _estates = estates;
        _progress = progress;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _estates.ForEachEstateAsync(null, async (services, _, ct) =>
            {
                foreach (var handler in services.GetServices<IEstateWorkHandler>())
                    await handler.RecoverInterruptedAsync(ct);
            }, stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Couldn't recover interrupted work");
        }

        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = ActAs(work);
                await Handler(scope, work).RunAsync(work.Id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // shutting down: recovered on the next start
            }
            catch (Exception e)
            {
                _logger.LogError(e, "{Kind} {Id} failed", work.Kind, work.Id);
                try
                {
                    using var scope = ActAs(work);
                    await Handler(scope, work).MarkFailedAsync(work.Id, stoppingToken);
                }
                catch (Exception inner)
                {
                    _logger.LogError(inner, "Couldn't mark {Kind} {Id} as failed", work.Kind, work.Id);
                }
            }
            finally
            {
                _progress.Clear(work.Id);
            }
        }
    }

    private static IEstateWorkHandler Handler(IServiceScope scope, EstateWork work) =>
        scope.ServiceProvider.GetServices<IEstateWorkHandler>().Single(h => h.Kind == work.Kind);

    /// <summary>A scope inside the work's estate, acting as the person who started it.</summary>
    private IServiceScope ActAs(EstateWork work)
    {
        var scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(work.EstateId);
        scope.ServiceProvider.GetRequiredService<CallerOverride>().User = new SnapshotCurrentUser(work.UserId, work.EstateId, work.Role);
        return scope;
    }
}
