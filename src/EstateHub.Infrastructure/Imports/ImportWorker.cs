using System.Collections.Concurrent;
using System.Threading.Channels;
using EstateHub.Application.Auth;
using EstateHub.Application.Imports;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Cron;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EstateHub.Infrastructure.Imports;

/// <summary>Committed imports waiting for the worker (in memory; a restart puts them back to "ready").</summary>
public sealed class ImportQueue : IImportQueue
{
    private readonly Channel<ImportWork> _channel = Channel.CreateUnbounded<ImportWork>();

    public ChannelReader<ImportWork> Reader => _channel.Reader;

    public void Enqueue(ImportWork work) => _channel.Writer.TryWrite(work);
}

public sealed class ImportProgress : IImportProgress
{
    private readonly ConcurrentDictionary<Guid, ImportProgressDto> _running = new();

    public void Report(Guid batchId, int done, int total, string message) => _running[batchId] = new ImportProgressDto(done, total, message);

    public ImportProgressDto? Get(Guid batchId) => _running.GetValueOrDefault(batchId);

    public void Clear(Guid batchId) => _running.TryRemove(batchId, out _);
}

/// <summary>
/// Runs committed imports one at a time, in the API process (they are started by a person who is watching
/// the progress). Each runs in its own scope for the batch's estate, acting as the person who committed it.
/// On start, imports interrupted by a restart go back to "ready": their transaction never committed.
/// </summary>
public sealed class ImportWorker : BackgroundService
{
    private readonly ImportQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly EstateJobRunner _estates;
    private readonly IImportProgress _progress;
    private readonly ILogger<ImportWorker> _logger;

    public ImportWorker(ImportQueue queue, IServiceScopeFactory scopes, EstateJobRunner estates, IImportProgress progress, ILogger<ImportWorker> logger)
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
            await _estates.ForEachEstateAsync(null, (services, _, ct) => services.GetRequiredService<IImportService>().RecoverInterruptedAsync(ct), stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Couldn't recover interrupted imports");
        }

        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = ActAs(work);
                await scope.ServiceProvider.GetRequiredService<IImportService>().RunAsync(work.BatchId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // shutting down: recovered as "ready" on the next start
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Import {BatchId} failed", work.BatchId);
                await MarkFailedAsync(work, stoppingToken);
            }
            finally
            {
                _progress.Clear(work.BatchId);
            }
        }
    }

    /// <summary>A scope inside the batch's estate, acting as the person who committed it.</summary>
    public IServiceScope ActAs(ImportWork work)
    {
        var scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(work.EstateId);
        scope.ServiceProvider.GetRequiredService<CallerOverride>().User = new SnapshotCurrentUser(work.UserId, work.EstateId, work.Role);
        return scope;
    }

    // In a fresh scope: the failed one's transaction was rolled back and its tracked changes are stale.
    private async Task MarkFailedAsync(ImportWork work, CancellationToken ct)
    {
        try
        {
            using var scope = ActAs(work);
            var db = scope.ServiceProvider.GetRequiredService<EstateHubDbContext>();
            if (await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == work.BatchId, ct) is { } batch && ImportStatuses.IsActive(batch.Status))
            {
                batch.Status = ImportStatuses.Failed;
                batch.Message = "Something went wrong and nothing was imported. Try again; if it keeps failing, contact support.";
                batch.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't mark import {BatchId} as failed", work.BatchId);
        }
    }
}
