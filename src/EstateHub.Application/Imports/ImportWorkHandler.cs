using EstateHub.Application.Background;

namespace EstateHub.Application.Imports;

/// <summary>Queued import commits (slice 3.3).</summary>
public sealed class ImportWorkHandler(IImportService imports) : IEstateWorkHandler
{
    public string Kind => WorkKinds.Import;

    public Task RunAsync(Guid id, CancellationToken ct = default) => imports.RunAsync(id, ct);

    public Task MarkFailedAsync(Guid id, CancellationToken ct = default) => imports.MarkFailedAsync(id, ct);

    public Task<int> RecoverInterruptedAsync(CancellationToken ct = default) => imports.RecoverInterruptedAsync(ct);
}
