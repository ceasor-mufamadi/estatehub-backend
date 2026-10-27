namespace EstateHub.Application.Cron;

// Same contract as chieta-disbursement-service. Jobs live in EstateHub.Infrastructure/Cron/Jobs,
// carry [CronJob("Name")] and are configured under CronJobs:<Name>. Run with RUN_MODE=jobs.
public interface ICronJob
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}
