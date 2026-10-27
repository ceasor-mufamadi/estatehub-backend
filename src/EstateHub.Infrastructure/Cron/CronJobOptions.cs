namespace EstateHub.Infrastructure.Cron;

public class CronJobOptions
{
    public Dictionary<string, CronJobConfig> Jobs { get; set; } = new();
}

public class CronJobConfig
{
    public bool Enabled { get; set; }

    public string Cron { get; set; } = default!;
    public int RetryCount { get; set; }
    public double RetryDelaySeconds { get; set; }
}