using EstateHub.Application.Background;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Billing;

/// <summary>
/// The monthly run (slice 4.2): budgets, metered uploads, a draft worked out live, then posting (queued,
/// one transaction, once per month). PHASE1.md "Sprint 4 design" items 5 and 9.
/// </summary>
public interface IBillingRunService
{
    Task<IReadOnlyList<LevyBudgetDto>> ListBudgetsAsync(CancellationToken ct = default);
    Task<LevyBudgetDto> AddBudgetAsync(string effectiveFrom, decimal adminMonthly, decimal reserveMonthly, string? note, CancellationToken ct = default);

    /// <summary>Only a budget no posted month has used yet.</summary>
    Task RemoveBudgetAsync(Guid budgetId, CancellationToken ct = default);

    /// <summary>
    /// A month's amounts for one metered charge, from a .csv/.xlsx (Unit, Amount, optional Reference). All rows
    /// are checked first: with problems nothing is saved; otherwise they replace that month's earlier upload.
    /// </summary>
    Task<MeteredUploadResultDto> UploadMeteredAsync(string period, Guid chargeTypeId, string fileName, byte[] content, CancellationToken ct = default);
    Task<IReadOnlyList<MeteredSummaryDto>> ListMeteredAsync(string period, CancellationToken ct = default);

    /// <summary>The month's run (created as a draft if there is none yet).</summary>
    Task<BillingRunDto> CreateDraftAsync(string period, CancellationToken ct = default);
    Task<IReadOnlyList<BillingRunDto>> ListRunsAsync(CancellationToken ct = default);
    Task<BillingRunDto> GetRunAsync(Guid runId, CancellationToken ct = default);
    Task DiscardDraftAsync(Guid runId, CancellationToken ct = default);

    /// <summary>What posting would charge now: per account, per charge, with warnings and blockers.</summary>
    Task<RunPreviewDto> PreviewAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Queues a draft for posting (recent MFA at the API). Repeat clicks return the same run.</summary>
    Task<BillingRunDto> PostAsync(Guid runId, CancellationToken ct = default);

    /// <summary>The queued posting (run by the work handler).</summary>
    Task RunPostingAsync(Guid runId, CancellationToken ct = default);
    Task MarkFailedAsync(Guid runId, CancellationToken ct = default);
    Task<int> RecoverInterruptedAsync(CancellationToken ct = default);
}

public record LevyBudgetDto(Guid Id, string EffectiveFrom, decimal AdminFundMonthly, decimal ReserveFundMonthly, string? Note, bool Used);

public record MeteredUploadResultDto(bool Saved, int Rows, decimal Total, IReadOnlyList<string> Problems);

public record MeteredSummaryDto(Guid ChargeTypeId, string ChargeName, int Units, decimal Total, string? FileName, DateTime? UploadedAt);

public record BillingRunDto(
    Guid Id, string Period, string Status, DateOnly BillingDate, decimal TotalPosted, int AccountsPosted, string? Message,
    DateTime? PostedAt, WorkProgressDto? Progress);

public record RunLineDto(string Charge, string Type, decimal Amount, string? Reference);

public record RunAccountDto(
    Guid AccountId, string Number, int SectionNumber, IReadOnlyList<string> Holders, IReadOnlyList<RunLineDto> Lines,
    decimal Total, decimal? PreviousTotal, IReadOnlyList<string> Flags);

public record RunChargeTotalDto(string Charge, decimal Total, int Accounts);

/// <summary>
/// <paramref name="Blockers"/> stop posting (e.g. quotas not 100%); <paramref name="Warnings"/> don't
/// (e.g. a unit without an owner isn't billed, a metered amount missing).
/// </summary>
public record RunPreviewDto(
    BillingRunDto Run, IReadOnlyList<RunAccountDto> Accounts, IReadOnlyList<RunChargeTotalDto> Totals, decimal GrandTotal,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings);

public interface IBillingRunRepository
{
    Task<IReadOnlyList<LevyBudget>> ListBudgetsAsync(CancellationToken ct = default);
    Task<LevyBudget?> GetBudgetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<MeteredAmount>> MeteredAsync(string period, CancellationToken ct = default);
    Task<IReadOnlyList<MeteredAmount>> MeteredAsync(string period, Guid chargeTypeId, CancellationToken ct = default);
    Task<IReadOnlyList<BillingRun>> ListRunsAsync(CancellationToken ct = default);
    Task<BillingRun?> GetRunAsync(Guid id, CancellationToken ct = default);
    Task<BillingRun?> PeekRunAsync(Guid id, CancellationToken ct = default);
    Task<BillingRun?> FindRunAsync(string period, CancellationToken ct = default);

    /// <summary>Per unit, what an earlier posted run charged (for "changed a lot since last month").</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> PostedTotalsByAccountAsync(Guid runId, CancellationToken ct = default);

    Task<Imports.IImportTransaction> BeginTransactionAsync(CancellationToken ct = default);
    void Add(object entity);
    void Remove(object entity);
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Queued postings (slice 4.2).</summary>
public sealed class BillingRunWorkHandler(IBillingRunService runs) : IEstateWorkHandler
{
    public string Kind => WorkKinds.BillingRun;

    public Task RunAsync(Guid id, CancellationToken ct = default) => runs.RunPostingAsync(id, ct);

    public Task MarkFailedAsync(Guid id, CancellationToken ct = default) => runs.MarkFailedAsync(id, ct);

    public Task<int> RecoverInterruptedAsync(CancellationToken ct = default) => runs.RecoverInterruptedAsync(ct);
}
