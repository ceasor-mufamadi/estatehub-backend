namespace EstateHub.Infrastructure.Cron;

/// The cron job run the current code is executing in (flows across awaits). Set by
/// CronJobHostedService; read by anything that records what a job did, e.g. the audit log.
/// Null outside a job (API requests).
public sealed class JobRunContext
{
    private static readonly AsyncLocal<JobRunContext?> CurrentRun = new();

    public string JobName { get; }
    public Guid RunId { get; } = Guid.NewGuid();

    /// 1 for the first try; incremented by each scheduler retry of the same run.
    public int Attempt { get; internal set; } = 1;

    private JobRunContext(string jobName) => JobName = jobName;

    public static JobRunContext? Current => CurrentRun.Value;

    /// Starts a run for this async flow; dispose to end it.
    public static IDisposable Begin(string jobName)
    {
        var previous = CurrentRun.Value;
        CurrentRun.Value = new JobRunContext(jobName);
        return new Restore(previous);
    }

    private sealed class Restore(JobRunContext? previous) : IDisposable
    {
        public void Dispose() => CurrentRun.Value = previous;
    }
}
