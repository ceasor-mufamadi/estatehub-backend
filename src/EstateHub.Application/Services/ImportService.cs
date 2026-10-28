using System.Text.Json;
using EstateHub.Application.Auth;
using EstateHub.Application.Background;
using EstateHub.Application.DTOs;
using EstateHub.Application.Imports;
using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// Spreadsheet import (slice 3.3, PHASE1.md §8 "Slice 3.3 design"). The preview saves only the batch;
/// the commit runs queued, in one transaction, re-checked against the estate as it is then. Invitations
/// follow in batches within the estate's hourly cap (MembersService), sent on the committer's behalf.
/// </summary>
public class ImportService : IImportService
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int PreviewUnits = 50;
    private const int SaveEvery = 250;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    private readonly IImportRepository _imports;
    private readonly IImportFileReader _reader;
    private readonly IWorkQueue _queue;
    private readonly IWorkProgress _progress;
    private readonly IPeopleService _people;
    private readonly IMembersService _members;
    private readonly ICurrentUser _user;
    private readonly IAuditLogService _audit;
    private readonly TimeProvider _clock;
    private readonly IAccountService _accounts;

    public ImportService(
        IImportRepository imports,
        IImportFileReader reader,
        IWorkQueue queue,
        IWorkProgress progress,
        IPeopleService people,
        IMembersService members,
        ICurrentUser user,
        IAuditLogService audit,
        TimeProvider clock,
        IAccountService accounts)
    {
        _accounts = accounts;
        _imports = imports;
        _reader = reader;
        _queue = queue;
        _progress = progress;
        _people = people;
        _members = members;
        _user = user;
        _audit = audit;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, SouthAfrica));

    // ---------- preview ----------

    public async Task<ImportBatchDto> PreviewAsync(string fileName, byte[] content, CancellationToken ct = default)
    {
        fileName = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (fileName.Length is 0 or > 200)
            throw new ArgumentException("The file needs a name (up to 200 characters).");
        if (content.Length == 0)
            throw new ArgumentException("The file is empty.");
        if (content.Length > MaxFileBytes)
            throw new ArgumentException($"The file is {content.Length / 1024.0 / 1024.0:0.0} MB; the limit is 5 MB. Split it into smaller files.");

        var sheet = _reader.Read(fileName, content);
        var rows = ToRawRows(sheet, out var headerProblems);
        var plan = headerProblems.Count > 0 ? null : ImportPlanner.Plan(rows, await _imports.LoadStateAsync(Today, ct), Today);
        var problems = plan?.Problems ?? headerProblems;

        var batch = new ImportBatch
        {
            FileName = fileName,
            Status = problems.Count == 0 ? ImportStatuses.Ready : ImportStatuses.Invalid,
            RowCount = rows.Count,
            RowsJson = JsonSerializer.Serialize(rows, Json),
            CreatedByUserId = Guid.Parse(_user.UserId!),
        };
        Store(batch, plan, problems);
        _imports.Add(batch);
        await _imports.SaveChangesAsync(ct);

        await AuditAsync(batch, "Previewed", $"file={fileName} rows={rows.Count} problems={problems.Count}", ct);
        return await ToDtoAsync(batch, ct);
    }

    /// <summary>Header row → column keys; data rows → cells by key (blank rows skipped).</summary>
    private static List<ImportRawRow> ToRawRows(ImportSheet sheet, out List<ImportProblemDto> problems)
    {
        problems = [];
        var keys = new string?[sheet.Headers.Count];
        for (var i = 0; i < sheet.Headers.Count; i++)
        {
            var key = ImportColumns.Match(sheet.Headers[i]);
            if (key is not null && keys.Contains(key))
                problems.Add(new ImportProblemDto(1, sheet.Headers[i], $"The column '{ImportColumns.Label(key)}' appears twice."));
            keys[i] = key;
        }
        if (!keys.Contains(ImportColumns.Section))
            problems.Add(new ImportProblemDto(1, "", "No 'Section' column in the first row. Use the template's headers in row 1."));

        var rows = new List<ImportRawRow>();
        foreach (var row in sheet.Rows)
        {
            var values = new Dictionary<string, string>();
            for (var i = 0; i < Math.Min(keys.Length, row.Cells.Count); i++)
                if (keys[i] is { } key && !string.IsNullOrWhiteSpace(row.Cells[i]))
                    values[key] = row.Cells[i].Trim();
            if (values.Count > 0)
                rows.Add(new ImportRawRow(row.RowNumber, values));
        }
        return rows;
    }

    // ---------- commit ----------

    public async Task<ImportBatchDto> CommitAsync(Guid batchId, bool sendInvitations, CancellationToken ct = default)
    {
        var batch = await _imports.GetAsync(batchId, ct) ?? throw new KeyNotFoundException("Import not found.");
        if (ImportStatuses.IsActive(batch.Status))
            return await ToDtoAsync(batch, ct); // a repeat click: the same job

        if (batch.Status != ImportStatuses.Ready)
            throw new ArgumentException(batch.Status switch
            {
                ImportStatuses.Invalid => "This file has problems. Fix them and upload it again.",
                ImportStatuses.Done => "This import has already been done.",
                _ => "This import can't be committed any more. Upload the file again.",
            });
        if (await _imports.FindActiveAsync(ct) is { } running)
            throw new ArgumentException($"Another import ({running.FileName}) is running. Wait for it to finish.");

        batch.Status = ImportStatuses.Queued;
        batch.SendInvitations = sendInvitations;
        batch.CommittedByUserId = Guid.Parse(_user.UserId!);
        batch.CommittedByRole = _user.Role;
        batch.CommittedAt = Now;
        batch.Message = "Waiting to start…";
        await _imports.SaveChangesAsync(ct);
        await AuditAsync(batch, "Committed", $"invitations={(sendInvitations ? "yes" : "no")}", ct);

        _queue.Enqueue(new EstateWork(WorkKinds.Import, batch.EstateId, batch.Id, _user.UserId!, _user.Role!));
        return await ToDtoAsync(batch, ct);
    }

    public async Task RunAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _imports.GetAsync(batchId, ct);
        if (batch is not { Status: ImportStatuses.Queued })
            return;

        batch.Status = ImportStatuses.Running;
        batch.Message = "Checking the rows again…";
        await _imports.SaveChangesAsync(ct);
        var rows = JsonSerializer.Deserialize<List<ImportRawRow>>(batch.RowsJson, Json) ?? [];
        _progress.Report(batch.Id, 0, rows.Count, batch.Message);

        // The estate may have changed since the preview: plan again against what is there now.
        var plan = ImportPlanner.Plan(rows, await _imports.LoadStateAsync(Today, ct), Today);
        if (!plan.IsValid)
        {
            batch.Status = ImportStatuses.Failed;
            batch.Message = "Nothing was imported: the estate changed since the preview. Fix the problems below and upload the file again.";
            batch.FinishedAt = Now;
            Store(batch, plan, plan.Problems);
            await _imports.SaveChangesAsync(ct);
            await AuditAsync(batch, "Failed", $"problems={plan.Problems.Count}", ct);
            _progress.Clear(batch.Id);
            return;
        }

        await using (var transaction = await _imports.BeginTransactionAsync(ct))
        {
            var state = await _imports.LoadStateAsync(Today, ct);
            var blocks = new Dictionary<string, Guid>(state.BlocksByName, StringComparer.OrdinalIgnoreCase);
            foreach (var name in plan.NewBlocks)
            {
                var block = new Block { Name = name };
                _imports.Add(block);
                blocks[name] = block.Id;
            }

            var people = new Dictionary<string, Guid>();
            foreach (var p in plan.People.Values)
            {
                if (p.ExistingId is { } id)
                {
                    people[p.Key] = id;
                    continue;
                }
                var person = new Person { Name = p.Name, Email = p.Email, Phone = p.Phone };
                _imports.Add(person);
                people[p.Key] = person.Id;
            }

            var done = 0;
            foreach (var u in plan.Units)
            {
                var unit = new Unit
                {
                    SectionNumber = u.Section,
                    DoorNumber = u.Door,
                    BlockId = u.Block is null ? null : blocks[u.Block],
                    ParticipationQuota = u.Quota,
                    SizeSquareMetres = u.Size,
                };
                _imports.Add(unit);
                foreach (var o in u.Owners)
                {
                    _imports.Add(new Ownership { UnitId = unit.Id, PersonId = people[o.PersonKey], SharePercent = o.Share, FromDate = o.Since });
                    if (o.LivesHere)
                        _imports.Add(new Occupancy { UnitId = unit.Id, PersonId = people[o.PersonKey], Kind = OccupancyKinds.OwnerOccupier, FromDate = o.Since });
                }
                foreach (var t in u.Tenants)
                    _imports.Add(new Occupancy { UnitId = unit.Id, PersonId = people[t.PersonKey], Kind = OccupancyKinds.Tenant, FromDate = t.Since });

                if (++done % SaveEvery == 0)
                {
                    await _imports.SaveChangesAsync(ct);
                    _progress.Report(batch.Id, done, plan.Units.Count, $"Saving units: {done} of {plan.Units.Count}…");
                }
            }

            if (batch.SendInvitations)
                foreach (var invitee in plan.Invitees)
                    _imports.Add(new ImportInvite { ImportBatchId = batch.Id, PersonId = people[invitee.Key] });

            batch.Status = ImportStatuses.Done;
            batch.FinishedAt = Now;
            batch.Message = $"Imported {plan.Units.Count} units, {plan.Summary.NewPeople} new people, {plan.Summary.Ownerships} ownerships and {plan.Summary.Occupancies} occupancies.";
            Store(batch, plan, []);
            // Imported: the uploaded rows and preview (names, contacts) aren't needed any more; the counts stay.
            batch.RowsJson = "[]";
            batch.SummaryJson = JsonSerializer.Serialize(new StoredSummary(plan.Summary, [], 0), Json);
            await _imports.SaveChangesAsync(ct);
            await AuditAsync(batch, "Completed",
                $"units={plan.Units.Count} blocks={plan.NewBlocks.Count} people={plan.Summary.NewPeople} ownerships={plan.Summary.Ownerships} occupancies={plan.Summary.Occupancies}", ct);
            await transaction.CommitAsync(ct);
        }

        _progress.Report(batch.Id, plan.Units.Count, plan.Units.Count, batch.Message);
        await _accounts.SyncAllAsync(ct); // the imported owners get their accounts (slice 4.1)
        await SendPendingInvitationsAsync(batch.Id, ct);
        _progress.Clear(batch.Id);
    }

    // ---------- invitations, within the hourly cap ----------

    public async Task<int> SendPendingInvitationsAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _imports.GetAsync(batchId, ct);
        if (batch is not { Status: ImportStatuses.Done })
            return 0;

        var pending = (await _imports.InvitesAsync(batchId, ct)).Where(i => i.Status == ImportInviteStatuses.Pending).ToList();
        var allowance = await _members.InvitationAllowanceAsync(ct);
        var handled = 0;
        foreach (var invite in pending.Take(allowance))
        {
            _progress.Report(batch.Id, handled, Math.Min(allowance, pending.Count), $"Sending invitations: {handled} of {Math.Min(allowance, pending.Count)}…");
            try
            {
                var result = await _people.InviteAsync(invite.PersonId, ct);
                invite.Status = result.Linked ? ImportInviteStatuses.Linked : ImportInviteStatuses.Sent;
                invite.SentAt = Now;
            }
            catch (RateLimitedException)
            {
                break; // the hour filled up (invitations sent by hand count too): the job carries on later
            }
            catch (Exception e) when (e is ArgumentException or ActionNotAllowedException or KeyNotFoundException)
            {
                invite.Status = ImportInviteStatuses.Failed;
                invite.Message = e.Message;
            }
            handled++;
            await _imports.SaveChangesAsync(ct);
        }

        if (handled > 0)
        {
            batch.LastInviteBatchAt = Now;
            await _imports.SaveChangesAsync(ct);
        }
        return handled;
    }

    // ---------- reading, discarding, recovery ----------

    public async Task<ImportBatchDto> GetAsync(Guid batchId, CancellationToken ct = default) =>
        await ToDtoAsync(await _imports.PeekAsync(batchId, ct) ?? throw new KeyNotFoundException("Import not found."), ct);

    public async Task<IReadOnlyList<ImportBatchDto>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<ImportBatchDto>();
        foreach (var batch in await _imports.ListRecentAsync(20, ct))
            result.Add((await ToDtoAsync(batch, ct)) with { Problems = [], Preview = [] });
        return result;
    }

    public async Task DiscardAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _imports.GetAsync(batchId, ct) ?? throw new KeyNotFoundException("Import not found.");
        if (batch.Status is not (ImportStatuses.Ready or ImportStatuses.Invalid or ImportStatuses.Failed))
            throw new ArgumentException("Only an import that hasn't been done can be discarded.");

        // The rows hold personal information that was never imported: don't keep it (POPIA).
        batch.Status = ImportStatuses.Discarded;
        batch.RowsJson = "[]";
        batch.ProblemsJson = "[]";
        batch.SummaryJson = "{}";
        await _imports.SaveChangesAsync(ct);
        await AuditAsync(batch, "Discarded", $"file={batch.FileName}", ct);
    }

    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        var interrupted = await _imports.ListInterruptedAsync(ct);
        foreach (var batch in interrupted)
        {
            // Running = the transaction never committed (Done is set inside it), so nothing was saved.
            batch.Status = ImportStatuses.Ready;
            batch.Message = "Interrupted by a restart before anything was saved. Commit again.";
        }
        if (interrupted.Count > 0)
            await _imports.SaveChangesAsync(ct);
        return interrupted.Count;
    }

    public async Task MarkFailedAsync(Guid batchId, CancellationToken ct = default)
    {
        if (await _imports.GetAsync(batchId, ct) is { } batch && ImportStatuses.IsActive(batch.Status))
        {
            batch.Status = ImportStatuses.Failed;
            batch.Message = "Something went wrong and nothing was imported. Try again; if it keeps failing, contact support.";
            batch.FinishedAt = Now;
            await _imports.SaveChangesAsync(ct);
        }
    }

    // ---------- helpers ----------

    private record StoredSummary(ImportSummaryDto Summary, List<ImportPreviewUnitDto> Preview, int MoreProblems);

    private static void Store(ImportBatch batch, ImportPlan? plan, IReadOnlyList<ImportProblemDto> problems)
    {
        batch.ProblemsJson = JsonSerializer.Serialize(problems.Take(ImportPlanner.MaxStoredProblems), Json);
        var preview = plan is null
            ? []
            : plan.Units.Take(PreviewUnits).Select(u => new ImportPreviewUnitDto(
                u.Section, u.Door, u.Block, u.Quota,
                string.Join(", ", u.Owners.Select(o => $"{plan.People[o.PersonKey].Name} ({o.Share:0.##}%)")),
                string.Join(", ", u.Owners.Where(o => o.LivesHere).Select(o => $"{plan.People[o.PersonKey].Name} (owner)")
                    .Concat(u.Tenants.Select(t => $"{plan.People[t.PersonKey].Name} (tenant)"))))).ToList();
        var summary = plan?.Summary ?? new ImportSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, false);
        batch.SummaryJson = JsonSerializer.Serialize(new StoredSummary(summary, preview, Math.Max(0, problems.Count - ImportPlanner.MaxStoredProblems)), Json);
    }

    private async Task<ImportBatchDto> ToDtoAsync(ImportBatch batch, CancellationToken ct)
    {
        var stored = batch.SummaryJson is "{}" ? null : JsonSerializer.Deserialize<StoredSummary>(batch.SummaryJson, Json);
        var problems = JsonSerializer.Deserialize<List<ImportProblemDto>>(batch.ProblemsJson, Json) ?? [];
        var invites = await _imports.InvitesAsync(batch.Id, ct);
        var pending = invites.Count(i => i.Status == ImportInviteStatuses.Pending);

        return new ImportBatchDto(
            batch.Id, batch.FileName, batch.Status, batch.RowCount, batch.CreatedAt,
            stored?.Summary ?? new ImportSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, false), problems, stored?.MoreProblems ?? 0,
            stored?.Preview ?? [], _progress.Get(batch.Id), batch.Message, batch.SendInvitations,
            new ImportInvitesDto(
                pending,
                invites.Count(i => i.Status == ImportInviteStatuses.Sent),
                invites.Count(i => i.Status == ImportInviteStatuses.Linked),
                invites.Count(i => i.Status == ImportInviteStatuses.Failed),
                pending > 0 ? (batch.LastInviteBatchAt ?? batch.FinishedAt ?? Now).AddHours(1) : null,
                invites.Where(i => i.Status == ImportInviteStatuses.Failed).Select(i => i.Message ?? "Failed").Take(50).ToList()),
            batch.FinishedAt);
    }

    private Task AuditAsync(ImportBatch batch, string action, string details, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto("Import", batch.Id.ToString(), action, _user.UserId!, details), ct);
}
