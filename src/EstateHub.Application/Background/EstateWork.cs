namespace EstateHub.Application.Background;

/// <summary>
/// Heavy work staff start and watch (SECURITY.md layer 6: "heavy operations queued, one at a time per
/// estate, progress over a stream"): an import commit, posting a billing run. Queued in the API process,
/// run one at a time in a scope for the estate, acting as the person who started it.
/// </summary>
public record EstateWork(string Kind, Guid EstateId, Guid Id, string UserId, string Role);

public static class WorkKinds
{
    public const string Import = "import";
    public const string BillingRun = "billing_run";
}

public interface IWorkQueue
{
    void Enqueue(EstateWork work);
}

/// <summary>Live progress of running work (in memory; the database has the status).</summary>
public interface IWorkProgress
{
    void Report(Guid id, int done, int total, string message);
    WorkProgressDto? Get(Guid id);
    void Clear(Guid id);
}

public record WorkProgressDto(int Done, int Total, string Message);

/// <summary>Runs one kind of queued work. Registered scoped; the worker picks the handler by <see cref="Kind"/>.</summary>
public interface IEstateWorkHandler
{
    string Kind { get; }

    Task RunAsync(Guid id, CancellationToken ct = default);

    /// <summary>After an unexpected error (in a fresh scope: the failed one's transaction was rolled back).</summary>
    Task MarkFailedAsync(Guid id, CancellationToken ct = default);

    /// <summary>On start: work interrupted by a restart goes back to where it can be started again.</summary>
    Task<int> RecoverInterruptedAsync(CancellationToken ct = default);
}
