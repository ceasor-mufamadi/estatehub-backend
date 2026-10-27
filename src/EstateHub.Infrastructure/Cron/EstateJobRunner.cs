using EstateHub.Application.Interfaces;
using EstateHub.Application.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EstateHub.Infrastructure.Cron;

public record EstateJobSummary(int Processed, int Skipped, int Failed);

/// <summary>
/// The multi-tenant part of a cron job (PHASE1.md §1c). Runs <c>work</c> once per estate, each in
/// its own DI scope with the tenant set, so EF filters and RLS confine it to that estate.
/// One estate failing is logged and counted; it never fails or retries the whole run, which also
/// keeps Polly retries from re-processing estates that already succeeded. Work must still be
/// idempotent (natural keys + unique indexes) in case a run is retried after a crash.
/// </summary>
public sealed class EstateJobRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EstateJobRunner> _logger;

    public EstateJobRunner(IServiceScopeFactory scopes, ILogger<EstateJobRunner> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <param name="requiredFeature">Skip estates where this feature is off (null = all estates).</param>
    public async Task<EstateJobSummary> ForEachEstateAsync(
        string? requiredFeature,
        Func<IServiceProvider, Guid, CancellationToken, Task> work,
        CancellationToken ct)
    {
        IReadOnlyList<Guid> estateIds;
        using (var listScope = _scopes.CreateScope())
        {
            var estates = await listScope.ServiceProvider.GetRequiredService<IEstateRepository>().ListAsync(ct);
            estateIds = estates.Select(e => e.Id).ToList();
        }

        int processed = 0, skipped = 0, failed = 0;
        var jobName = JobRunContext.Current?.JobName ?? "(no job)";

        foreach (var estateId in estateIds)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = _scopes.CreateScope();
            var services = scope.ServiceProvider;
            services.GetRequiredService<ITenantContext>().Use(estateId);

            try
            {
                if (requiredFeature is not null
                    && !await services.GetRequiredService<IFeatureService>().IsEnabledAsync(estateId, requiredFeature, ct))
                {
                    skipped++;
                    continue;
                }

                await work(services, estateId, ct);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogError(ex, "{Job} failed for estate {EstateId}", jobName, estateId);
            }
        }

        _logger.LogInformation(
            "{Job}: {Processed} estates processed, {Skipped} skipped, {Failed} failed", jobName, processed, skipped, failed);
        return new EstateJobSummary(processed, skipped, failed);
    }
}
