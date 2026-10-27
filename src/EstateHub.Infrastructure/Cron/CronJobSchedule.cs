using Cronos;

namespace EstateHub.Infrastructure.Cron;

public class CronJobSchedule
{
    public string Name { get; set; } = default!;

    public Type JobType { get; set; } = default!;

    public CronExpression Expression { get; set; } = default!;

    public DateTime? NextRun { get; set; }

    public bool Running { get; set; }
    public TimeZoneInfo TimeZoneInfo { get; set; } = default!;
    public int RetryCount { get; set; }
    public double RetryDelaySeconds { get; set; }
}