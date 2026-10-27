using EstateHub.Application.Cron;
using EstateHub.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EstateHub.Infrastructure.Cron.Jobs;

/// <summary>
/// Diagnostic job proving RUN_MODE=jobs works end to end, and the reference for real jobs:
/// thin, delegates the per-estate loop to EstateJobRunner, resolves services from the estate's scope.
/// </summary>
[CronJob("DummyJob")]
public class DummyJob : ICronJob
{
    private readonly EstateJobRunner _runner;
    private readonly ILogger<DummyJob> _logger;

    public DummyJob(EstateJobRunner runner, ILogger<DummyJob> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DummyJob executed at {time}", DateTime.Now);

        await _runner.ForEachEstateAsync(requiredFeature: null, async (services, estateId, ct) =>
        {
            var blocks = await services.GetRequiredService<IBlockService>().ListAsync(ct);
            _logger.LogInformation("DummyJob: estate {EstateId} has {Count} blocks", estateId, blocks.Count);
        }, cancellationToken);
    }
}
