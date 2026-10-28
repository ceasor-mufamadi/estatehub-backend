using EstateHub.Domain.Entities;

namespace EstateHub.Application.Imports;

/// <summary>
/// Spreadsheet import of units, owners and tenants (slice 3.3, PHASE1.md §8). Upload → preview (nothing
/// saved but the batch) → commit (queued; one transaction) → invitations in batches within the hourly cap.
/// </summary>
public interface IImportService
{
    /// <summary>Reads and checks the file; stores the batch as ready or invalid. Nothing else is saved.</summary>
    Task<ImportBatchDto> PreviewAsync(string fileName, byte[] content, CancellationToken ct = default);

    /// <summary>
    /// Queues a ready batch. Clicking again while it runs returns the same batch; another batch of this
    /// estate running at the time is refused.
    /// </summary>
    Task<ImportBatchDto> CommitAsync(Guid batchId, bool sendInvitations, CancellationToken ct = default);

    Task<ImportBatchDto> GetAsync(Guid batchId, CancellationToken ct = default);
    Task<IReadOnlyList<ImportBatchDto>> ListAsync(CancellationToken ct = default);
    Task DiscardAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>The queued work (run by the import worker in a scope for the batch's estate and committer).</summary>
    Task RunAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>Sends the batch's waiting invitations, as many as the estate's hourly cap allows now.</summary>
    Task<int> SendPendingInvitationsAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>Puts batches interrupted by a restart back to ready (the transaction means nothing was saved).</summary>
    Task<int> RecoverInterruptedAsync(CancellationToken ct = default);
}

/// <summary>Reads .xlsx or .csv into header + rows, with limits against oversized and zip-bomb files.</summary>
public interface IImportFileReader
{
    /// <exception cref="ArgumentException">The file can't be read (with a reason staff can act on).</exception>
    ImportSheet Read(string fileName, byte[] content);
}

/// <summary>The downloadable template (.xlsx).</summary>
public interface IImportTemplate
{
    byte[] Create();
}

public record ImportSheet(IReadOnlyList<string> Headers, IReadOnlyList<ImportSheetRow> Rows);

/// <summary>A data row; <see cref="RowNumber"/> as the spreadsheet shows it (header = row 1).</summary>
public record ImportSheetRow(int RowNumber, IReadOnlyList<string> Cells);

public interface IImportRepository
{
    Task<ImportBatch?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Fresh from the database (no tracking): for watching progress.</summary>
    Task<ImportBatch?> PeekAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ImportBatch>> ListRecentAsync(int take, CancellationToken ct = default);
    Task<ImportBatch?> FindActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ImportBatch>> ListInterruptedAsync(CancellationToken ct = default);

    /// <summary>Batches still holding uploaded rows (previews not imported or discarded): personal information to minimise.</summary>
    Task<IReadOnlyList<ImportBatch>> ListHoldingRowsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> BatchesWithPendingInvitesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ImportInvite>> InvitesAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>What the planner checks against: the estate's units, blocks, people and open occupancies.</summary>
    Task<ImportEstateState> LoadStateAsync(DateOnly today, CancellationToken ct = default);

    Task<IImportTransaction> BeginTransactionAsync(CancellationToken ct = default);
    void Add(object entity);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IImportTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}

/// <summary>Hands committed batches to the import worker (one at a time).</summary>
public interface IImportQueue
{
    void Enqueue(ImportWork work);
}

/// <summary>The batch, its estate, and who committed it (the work runs on their behalf).</summary>
public record ImportWork(Guid EstateId, Guid BatchId, string UserId, string Role);

/// <summary>Live progress of running batches (in memory; the database has the status).</summary>
public interface IImportProgress
{
    void Report(Guid batchId, int done, int total, string message);
    ImportProgressDto? Get(Guid batchId);
    void Clear(Guid batchId);
}

public record ImportProgressDto(int Done, int Total, string Message);

public record ImportProblemDto(int Row, string Column, string Message);

public record ImportSummaryDto(
    int Units, int NewBlocks, int NewPeople, int ExistingPeople, int Ownerships, int Occupancies, int Invitations,
    decimal QuotaTotalAfter, bool QuotaComplete);

public record ImportPreviewUnitDto(int Section, string? Door, string? Block, decimal Quota, string Owners, string Occupants);

public record ImportInvitesDto(int Pending, int Sent, int Linked, int Failed, DateTime? NextBatchBy, IReadOnlyList<string> Failures);

public record ImportBatchDto(
    Guid Id, string FileName, string Status, int RowCount, DateTime CreatedAt,
    ImportSummaryDto Summary, IReadOnlyList<ImportProblemDto> Problems, int MoreProblems,
    IReadOnlyList<ImportPreviewUnitDto> Preview, ImportProgressDto? Progress, string? Message,
    bool SendInvitations, ImportInvitesDto Invites, DateTime? FinishedAt);
