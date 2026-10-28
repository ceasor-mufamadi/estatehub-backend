using System.Globalization;
using EstateHub.Application.Auth;
using EstateHub.Application.Background;
using EstateHub.Application.Billing;
using EstateHub.Application.DTOs;
using EstateHub.Application.Imports;
using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// The monthly run (slice 4.2, PHASE1.md "Sprint 4 design" item 5). A draft is worked out live from the
/// budget (split by quota to the cent), the flat and per-unit charges and that month's metered uploads,
/// for every account open on the billing date. Posting re-works it out and writes the entries in one
/// transaction; a month is posted once (status + unique period).
/// </summary>
public class BillingRunService : IBillingRunService
{
    private const int SaveEvery = 500;
    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    private readonly IBillingRunRepository _runs;
    private readonly IBillingRepository _billing;
    private readonly IBillingService _settings;
    private readonly IUnitRepository _units;
    private readonly IAccountService _accounts;
    private readonly IImportFileReader _reader;
    private readonly IWorkQueue _queue;
    private readonly IWorkProgress _progress;
    private readonly ICurrentUser _user;
    private readonly IAuditLogService _audit;
    private readonly TimeProvider _clock;

    public BillingRunService(
        IBillingRunRepository runs,
        IBillingRepository billing,
        IBillingService settings,
        IUnitRepository units,
        IAccountService accounts,
        IImportFileReader reader,
        IWorkQueue queue,
        IWorkProgress progress,
        ICurrentUser user,
        IAuditLogService audit,
        TimeProvider clock)
    {
        _runs = runs;
        _billing = billing;
        _settings = settings;
        _units = units;
        _accounts = accounts;
        _reader = reader;
        _queue = queue;
        _progress = progress;
        _user = user;
        _audit = audit;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, SouthAfrica));
    private Guid ActorId => Guid.TryParse(_user.UserId, out var id) ? id : Guid.Empty;

    /// <summary>"2026-11" → 1 Nov 2026, or an ArgumentException.</summary>
    public static DateOnly MonthOf(string period) =>
        DateOnly.TryParseExact((period ?? string.Empty).Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            ? month
            : throw new ArgumentException("The month must look like 2026-11.");

    private static string PeriodOf(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // ---------- budgets ----------

    public async Task<IReadOnlyList<LevyBudgetDto>> ListBudgetsAsync(CancellationToken ct = default)
    {
        var posted = (await _runs.ListRunsAsync(ct)).Where(r => r.Status == BillingRunStatuses.Posted).Select(r => r.Period).ToList();
        var budgets = (await _runs.ListBudgetsAsync(ct)).OrderBy(b => b.EffectiveFrom).ToList();
        return budgets.Select((b, i) =>
        {
            var until = i + 1 < budgets.Count ? budgets[i + 1].EffectiveFrom : "9999-12";
            return new LevyBudgetDto(b.Id, b.EffectiveFrom, b.AdminFundMonthly, b.ReserveFundMonthly, b.Note,
                posted.Any(p => string.CompareOrdinal(p, b.EffectiveFrom) >= 0 && string.CompareOrdinal(p, until) < 0));
        }).Reverse().ToList();
    }

    public async Task<LevyBudgetDto> AddBudgetAsync(string effectiveFrom, decimal adminMonthly, decimal reserveMonthly, string? note, CancellationToken ct = default)
    {
        var period = PeriodOf(MonthOf(effectiveFrom));
        foreach (var amount in new[] { adminMonthly, reserveMonthly })
            if (amount < 0 || amount > 100_000_000 || decimal.Round(amount, 2) != amount)
                throw new ArgumentException("Budget amounts must be in rands and cents.");
        if (adminMonthly + reserveMonthly == 0)
            throw new ArgumentException("Give the monthly admin fund and reserve fund totals.");
        if ((await _runs.ListBudgetsAsync(ct)).Any(b => b.EffectiveFrom == period))
            throw new ArgumentException($"There's already a budget from {period}. Remove it first, or start this one in another month.");
        if ((await _runs.ListRunsAsync(ct)).Any(r => r.Status == BillingRunStatuses.Posted && string.CompareOrdinal(r.Period, period) >= 0))
            throw new ArgumentException($"{period} or a later month has been posted already; a new budget can only start after the last posted month.");
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > 200 })
            throw new ArgumentException("Keep the note under 200 characters.");

        var budget = new LevyBudget { EffectiveFrom = period, AdminFundMonthly = adminMonthly, ReserveFundMonthly = reserveMonthly, Note = trimmed, CreatedByUserId = ActorId };
        _runs.Add(budget);
        await _runs.SaveChangesAsync(ct);
        await AuditAsync("LevyBudget", budget.Id, "Added", $"from={period} admin={adminMonthly:0.00} reserve={reserveMonthly:0.00}", ct);
        return (await ListBudgetsAsync(ct)).Single(b => b.Id == budget.Id);
    }

    public async Task RemoveBudgetAsync(Guid budgetId, CancellationToken ct = default)
    {
        var dto = (await ListBudgetsAsync(ct)).FirstOrDefault(b => b.Id == budgetId) ?? throw new KeyNotFoundException("Budget not found.");
        if (dto.Used)
            throw new ArgumentException("A posted month used this budget, so it stays as history. Add a new budget from a later month instead.");
        _runs.Remove((await _runs.GetBudgetAsync(budgetId, ct))!);
        await _runs.SaveChangesAsync(ct);
        await AuditAsync("LevyBudget", budgetId, "Removed", $"from={dto.EffectiveFrom}", ct);
    }

    // ---------- metered uploads ----------

    public async Task<MeteredUploadResultDto> UploadMeteredAsync(string period, Guid chargeTypeId, string fileName, byte[] content, CancellationToken ct = default)
    {
        var month = PeriodOf(MonthOf(period));
        var charge = await _billing.GetChargeTypeAsync(chargeTypeId, ct) ?? throw new KeyNotFoundException("Charge type not found.");
        if (charge.Method != ChargeMethods.Metered)
            throw new ArgumentException($"{charge.Name} isn't a metered charge.");
        if (await _runs.FindRunAsync(month, ct) is { Status: not BillingRunStatuses.Draft })
            throw new ArgumentException($"{month} has been posted (or is posting); its amounts can't change any more.");
        if (content.Length == 0 || content.Length > ImportService.MaxFileBytes)
            throw new ArgumentException("The file must be between 1 byte and 5 MB.");

        var sheet = _reader.Read(fileName ?? "upload.csv", content);
        var columns = sheet.Headers.Select(MeterColumn).ToList();
        var problems = new List<string>();
        if (!columns.Contains("unit") || !columns.Contains("amount"))
            return new MeteredUploadResultDto(false, 0, 0, ["The first row needs a 'Unit' column (door or section number) and an 'Amount' column."]);

        var units = await _units.ListAsync(ct);
        var parsed = new Dictionary<Guid, (decimal Amount, string? Reference, int Row)>();
        foreach (var row in sheet.Rows)
        {
            string? Cell(string key) => columns.IndexOf(key) is var i and >= 0 && i < row.Cells.Count && !string.IsNullOrWhiteSpace(row.Cells[i]) ? row.Cells[i].Trim() : null;
            var unitText = Cell("unit");
            var amountText = Cell("amount");
            if (unitText is null && amountText is null)
                continue;

            var unit = unitText is null ? null : FindUnit(units, unitText);
            if (unit is null)
                problems.Add($"Row {row.RowNumber}: there's no unit '{unitText}' in this estate.");
            var amount = amountText is null ? null : ImportPlanner.Number(amountText);
            if (amount is not { } a || a < 0 || a > 1_000_000 || decimal.Round(a, 2) != a)
            {
                problems.Add($"Row {row.RowNumber}: '{amountText}' isn't an amount in rands and cents (0 or more).");
                continue;
            }
            if (unit is null)
                continue;
            if (parsed.TryGetValue(unit.Id, out var first))
            {
                problems.Add($"Row {row.RowNumber}: unit {unitText} is also on row {first.Row}.");
                continue;
            }
            var reference = Cell("reference");
            parsed[unit.Id] = (a, reference is { Length: > 40 } ? reference[..40] : reference, row.RowNumber);
        }
        if (parsed.Count == 0 && problems.Count == 0)
            problems.Add("The file has no rows under the header.");
        if (problems.Count > 0)
            return new MeteredUploadResultDto(false, parsed.Count, parsed.Values.Sum(p => p.Amount), problems.Take(200).ToList());

        foreach (var old in await _runs.MeteredAsync(month, chargeTypeId, ct))
            _runs.Remove(old);
        foreach (var (unitId, p) in parsed)
            _runs.Add(new MeteredAmount
            {
                Period = month, ChargeTypeId = chargeTypeId, UnitId = unitId, Amount = p.Amount, Reference = p.Reference,
                FileName = Path.GetFileName(fileName), UploadedByUserId = ActorId,
            });
        await _runs.SaveChangesAsync(ct);
        await AuditAsync("ChargeType", chargeTypeId, "MeteredUploaded", $"period={month} units={parsed.Count} total={parsed.Values.Sum(p => p.Amount):0.00}", ct);
        return new MeteredUploadResultDto(true, parsed.Count, parsed.Values.Sum(p => p.Amount), []);
    }

    private static string? MeterColumn(string header) =>
        new string(header.Trim().ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()) switch
        {
            "unit" or "unitnumber" or "unitno" or "door" or "doornumber" or "section" or "sectionnumber" or "account" => "unit",
            "amount" or "total" or "amountdue" or "charge" => "amount",
            "reference" or "reading" or "meter" or "meterreading" or "ref" => "reference",
            _ => null,
        };

    /// <summary>A door number ("003", "A12") or a section number ("3"); leading zeros don't matter.</summary>
    private static UnitDto? FindUnit(IReadOnlyList<UnitDto> units, string text)
    {
        var t = text.Trim();
        var byDoor = units.Where(u => u.DoorNumber is { } d && string.Equals(d.Trim(), t, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byDoor.Count == 1)
            return byDoor[0];
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            var byNumber = units.Where(u => u.SectionNumber == n || (u.DoorNumber is { } d && int.TryParse(d, out var dn) && dn == n)).ToList();
            if (byNumber.Select(u => u.Id).Distinct().Count() == 1)
                return byNumber[0];
        }
        return null;
    }

    public async Task<IReadOnlyList<MeteredSummaryDto>> ListMeteredAsync(string period, CancellationToken ct = default)
    {
        var month = PeriodOf(MonthOf(period));
        var amounts = await _runs.MeteredAsync(month, ct);
        return (await _billing.ListChargeTypesAsync(ct)).Where(c => c.Method == ChargeMethods.Metered && c.IsActive)
            .Select(c =>
            {
                var mine = amounts.Where(a => a.ChargeTypeId == c.Id).ToList();
                return new MeteredSummaryDto(c.Id, c.Name, mine.Count, mine.Sum(a => a.Amount), mine.FirstOrDefault()?.FileName, mine.Count > 0 ? mine.Max(a => a.UpdatedAt) : null);
            }).ToList();
    }

    // ---------- runs ----------

    public async Task<BillingRunDto> CreateDraftAsync(string period, CancellationToken ct = default)
    {
        var month = MonthOf(period);
        var key = PeriodOf(month);
        if (await _runs.FindRunAsync(key, ct) is { } existing)
            return ToDto(existing);

        var settings = await SettingsAsync(ct);
        var run = new BillingRun { Period = key, BillingDate = month.AddDays(settings.BillingDay - 1) };
        _runs.Add(run);
        await _runs.SaveChangesAsync(ct);
        await AuditAsync("BillingRun", run.Id, "DraftCreated", $"period={key}", ct);
        return ToDto(run);
    }

    public async Task<IReadOnlyList<BillingRunDto>> ListRunsAsync(CancellationToken ct = default) =>
        (await _runs.ListRunsAsync(ct)).OrderByDescending(r => r.Period).Select(ToDto).ToList();

    public async Task<BillingRunDto> GetRunAsync(Guid runId, CancellationToken ct = default) =>
        ToDto(await _runs.PeekRunAsync(runId, ct) ?? throw new KeyNotFoundException("Billing run not found."));

    public async Task DiscardDraftAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _runs.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Billing run not found.");
        if (run.Status != BillingRunStatuses.Draft)
            throw new ArgumentException("Only a draft can be discarded.");
        _runs.Remove(run);
        await _runs.SaveChangesAsync(ct);
        await AuditAsync("BillingRun", run.Id, "DraftDiscarded", $"period={run.Period}", ct);
    }

    public async Task<RunPreviewDto> PreviewAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _runs.PeekRunAsync(runId, ct) ?? throw new KeyNotFoundException("Billing run not found.");
        var plan = await PlanAsync(run, ct);
        return new RunPreviewDto(ToDto(run), plan.Accounts, plan.Totals, plan.Accounts.Sum(a => a.Total), plan.Blockers, plan.Warnings);
    }

    public async Task<BillingRunDto> PostAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _runs.GetRunAsync(runId, ct) ?? throw new KeyNotFoundException("Billing run not found.");
        if (BillingRunStatuses.IsActive(run.Status))
            return ToDto(run); // a repeat click
        if (run.Status == BillingRunStatuses.Posted)
            throw new ArgumentException($"{run.Period} has already been posted.");

        var plan = await PlanAsync(run, ct);
        if (plan.Blockers.Count > 0)
            throw new ArgumentException(string.Join(" ", plan.Blockers));

        run.Status = BillingRunStatuses.Queued;
        run.Message = "Waiting to start…";
        await _runs.SaveChangesAsync(ct);
        await AuditAsync("BillingRun", run.Id, "PostRequested", $"period={run.Period} accounts={plan.Accounts.Count} total={plan.Accounts.Sum(a => a.Total):0.00}", ct);
        _queue.Enqueue(new EstateWork(WorkKinds.BillingRun, run.EstateId, run.Id, _user.UserId!, _user.Role!));
        return ToDto(run);
    }

    public async Task RunPostingAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await _runs.GetRunAsync(runId, ct);
        if (run is not { Status: BillingRunStatuses.Queued })
            return;

        run.Status = BillingRunStatuses.Posting;
        run.Message = "Working out the charges…";
        await _runs.SaveChangesAsync(ct);

        // Everything is worked out again now: uploads or owners may have changed since the preview.
        var plan = await PlanAsync(run, ct);
        if (plan.Blockers.Count > 0)
        {
            run.Status = BillingRunStatuses.Draft;
            run.Message = "Not posted: " + string.Join(" ", plan.Blockers);
            await _runs.SaveChangesAsync(ct);
            return;
        }

        await using (var transaction = await _runs.BeginTransactionAsync(ct))
        {
            var done = 0;
            foreach (var account in plan.Accounts)
            {
                foreach (var line in account.Lines)
                    _runs.Add(new LedgerEntry
                    {
                        AccountId = account.AccountId,
                        Date = run.BillingDate,
                        Type = line.Type,
                        ChargeTypeId = plan.ChargeIds[line.Charge],
                        Description = line.Charge,
                        Reference = line.Reference ?? run.Period,
                        Debit = line.Amount,
                        Period = run.Period,
                        Source = LedgerSources.Run,
                        SourceId = run.Id,
                        CreatedByUserId = ActorId,
                    });
                if (++done % SaveEvery == 0)
                {
                    await _runs.SaveChangesAsync(ct);
                    _progress.Report(run.Id, done, plan.Accounts.Count, $"Posting: {done} of {plan.Accounts.Count} accounts…");
                }
            }

            run.Status = BillingRunStatuses.Posted;
            run.PostedAt = Now;
            run.PostedByUserId = ActorId;
            run.AccountsPosted = plan.Accounts.Count;
            run.TotalPosted = plan.Accounts.Sum(a => a.Total);
            run.Message = $"Posted {run.Period}: {run.AccountsPosted} accounts, R {run.TotalPosted:N2}.";
            await _runs.SaveChangesAsync(ct);
            await AuditAsync("BillingRun", run.Id, "Posted", $"period={run.Period} accounts={run.AccountsPosted} total={run.TotalPosted:0.00}", ct);
            await transaction.CommitAsync(ct);
        }
        _progress.Clear(run.Id);
    }

    public async Task MarkFailedAsync(Guid runId, CancellationToken ct = default)
    {
        if (await _runs.GetRunAsync(runId, ct) is { } run && BillingRunStatuses.IsActive(run.Status))
        {
            run.Status = BillingRunStatuses.Draft;
            run.Message = "Something went wrong and nothing was posted. Try again; if it keeps failing, contact support.";
            await _runs.SaveChangesAsync(ct);
        }
    }

    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        var interrupted = (await _runs.ListRunsAsync(ct)).Where(r => BillingRunStatuses.IsActive(r.Status)).ToList();
        foreach (var run in interrupted)
        {
            // "Posted" is set inside the transaction, so a run still queued/posting saved nothing.
            var tracked = await _runs.GetRunAsync(run.Id, ct);
            tracked!.Status = BillingRunStatuses.Draft;
            tracked.Message = "Interrupted by a restart before anything was posted. Post again.";
        }
        if (interrupted.Count > 0)
            await _runs.SaveChangesAsync(ct);
        return interrupted.Count;
    }

    // ---------- working out a month ----------

    private sealed record Plan(
        List<RunAccountDto> Accounts, List<RunChargeTotalDto> Totals, List<string> Blockers, List<string> Warnings, Dictionary<string, Guid> ChargeIds);

    private async Task<Plan> PlanAsync(BillingRun run, CancellationToken ct)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (run.Status == BillingRunStatuses.Draft && run.BillingDate > Today)
            blockers.Add($"{run.Period} can be posted from its billing date, {run.BillingDate:yyyy-MM-dd}.");

        await _accounts.SyncAllAsync(ct); // owners recorded since the last change count
        var units = await _units.ListAsync(ct);
        var charges = (await _billing.ListChargeTypesAsync(ct)).Where(c => c.IsActive).ToList();
        var levy = charges.FirstOrDefault(c => c.IsLevy);
        var allAccounts = await _accounts.ListAsync(AccountFilter.All, null, ct);
        var accounts = allAccounts
            .Where(a => a.OpenedOn <= run.BillingDate && (a.ClosedOn is null || a.ClosedOn >= run.BillingDate))
            .ToDictionary(a => a.UnitId);
        // Owners who start after the billing date are billed from the next month (a buyer isn't charged twice with the seller).
        var startingLater = allAccounts.Where(a => a.OpenedOn > run.BillingDate && !accounts.ContainsKey(a.UnitId))
            .GroupBy(a => a.UnitId).ToDictionary(g => g.Key, g => g.Min(a => a.OpenedOn));
        var lines = units.ToDictionary(u => u.Id, _ => new List<RunLineDto>());

        // The levy: this month's budget split by quota (to the cent) over every unit.
        var budget = (await _runs.ListBudgetsAsync(ct)).Where(b => string.CompareOrdinal(b.EffectiveFrom, run.Period) <= 0)
            .OrderByDescending(b => b.EffectiveFrom).FirstOrDefault();
        if (levy is null || budget is null)
            warnings.Add($"No levy budget applies to {run.Period}: add one under Billing settings, or no levy is charged.");
        else if (units.Sum(u => u.ParticipationQuota) != 100m)
            blockers.Add($"Participation quotas add up to {units.Sum(u => u.ParticipationQuota):0.0000}%, not 100%. Fix the units first.");
        else
        {
            var quotas = units.Select(u => (u.Id, u.ParticipationQuota)).ToList();
            foreach (var (unitId, amount) in QuotaSplit.Split(budget.AdminFundMonthly, quotas))
                lines[unitId].Add(new RunLineDto($"{levy.Name} (admin fund)", LedgerTypes.LevyAdmin, amount, null));
            foreach (var (unitId, amount) in QuotaSplit.Split(budget.ReserveFundMonthly, quotas))
                lines[unitId].Add(new RunLineDto($"{levy.Name} (reserve fund)", LedgerTypes.LevyReserve, amount, null));
        }

        foreach (var charge in charges.Where(c => c.Method == ChargeMethods.Flat && c.Amount > 0))
            foreach (var unit in units)
                lines[unit.Id].Add(new RunLineDto(charge.Name, LedgerTypes.Charge, charge.Amount!.Value, null));

        foreach (var charge in charges.Where(c => c.Method == ChargeMethods.PerUnit))
            foreach (var row in await _billing.ListUnitChargesAsync(charge.Id, ct))
                if (lines.TryGetValue(row.UnitId, out var list) && row.Amount > 0)
                    list.Add(new RunLineDto(charge.Name, LedgerTypes.Charge, row.Amount, null));

        var metered = await _runs.MeteredAsync(run.Period, ct);
        foreach (var charge in charges.Where(c => c.Method == ChargeMethods.Metered))
        {
            var mine = metered.Where(m => m.ChargeTypeId == charge.Id).ToDictionary(m => m.UnitId);
            if (mine.Count == 0)
            {
                warnings.Add($"Nothing uploaded for {charge.Name} in {run.Period}: no {charge.Name.ToLowerInvariant()} is charged.");
                continue;
            }
            var missing = units.Where(u => accounts.ContainsKey(u.Id) && !mine.ContainsKey(u.Id)).Select(u => Label(u)).ToList();
            if (missing.Count > 0)
                warnings.Add($"{charge.Name}: no amount uploaded for {string.Join(", ", missing.Take(20))}{(missing.Count > 20 ? $" and {missing.Count - 20} more" : "")}.");
            foreach (var (unitId, m) in mine)
                if (lines.TryGetValue(unitId, out var list) && m.Amount > 0)
                    list.Add(new RunLineDto(charge.Name, LedgerTypes.Charge, m.Amount, m.Reference));
        }

        // Units without an owner on the billing date can't be billed: their share is left unbilled.
        var unbilled = units.Where(u => !accounts.ContainsKey(u.Id) && lines[u.Id].Count > 0).ToList();
        var later = unbilled.Where(u => startingLater.ContainsKey(u.Id)).ToList();
        var ownerless = unbilled.Except(later).ToList();
        if (later.Count > 0)
            warnings.Add($"Owner only from {later.Select(u => startingLater[u.Id]).Min():yyyy-MM-dd} for {string.Join(", ", later.Take(20).Select(Label))}: " +
                         $"billed from next month (R {later.Sum(u => lines[u.Id].Sum(l => l.Amount)):N2} not billed for {run.Period}). " +
                         "If they owned it before, correct their \"from\" date on the unit.");
        if (ownerless.Count > 0)
            warnings.Add($"No owner on {run.BillingDate:yyyy-MM-dd} for {string.Join(", ", ownerless.Take(20).Select(Label))}: " +
                         $"R {ownerless.Sum(u => lines[u.Id].Sum(l => l.Amount)):N2} won't be billed.");

        var previousRun = (await _runs.ListRunsAsync(ct)).Where(r => r.Status == BillingRunStatuses.Posted && string.CompareOrdinal(r.Period, run.Period) < 0)
            .OrderByDescending(r => r.Period).FirstOrDefault();
        var previous = previousRun is null ? new Dictionary<Guid, decimal>() : await _runs.PostedTotalsByAccountAsync(previousRun.Id, ct);
        var previousByUnit = previous.Count == 0 ? new Dictionary<Guid, decimal>()
            : (await _accounts.ListAsync(AccountFilter.All, null, ct)).Where(a => previous.ContainsKey(a.Id))
                .GroupBy(a => a.UnitId).ToDictionary(g => g.Key, g => g.Sum(a => previous[a.Id]));

        var result = units.Where(u => accounts.ContainsKey(u.Id) && lines[u.Id].Count > 0).OrderBy(u => u.SectionNumber).Select(u =>
        {
            var a = accounts[u.Id];
            var total = lines[u.Id].Sum(l => l.Amount);
            decimal? before = previousByUnit.TryGetValue(u.Id, out var p) ? p : null;
            var flags = new List<string>();
            if (before is > 0 && Math.Abs(total - before.Value) > before.Value / 2)
                flags.Add($"Changed by more than half since {previousRun!.Period} (R {before:N2}).");
            return new RunAccountDto(a.Id, a.Number, u.SectionNumber, a.Holders, lines[u.Id], total, before, flags);
        }).ToList();

        var totals = result.SelectMany(a => a.Lines).GroupBy(l => l.Charge)
            .Select(g => new RunChargeTotalDto(g.Key, g.Sum(l => l.Amount), g.Count())).ToList();
        var chargeIds = new Dictionary<string, Guid>();
        if (levy is not null)
        {
            chargeIds[$"{levy.Name} (admin fund)"] = levy.Id;
            chargeIds[$"{levy.Name} (reserve fund)"] = levy.Id;
        }
        foreach (var c in charges.Where(c => !c.IsLevy))
            chargeIds[c.Name] = c.Id;
        if (result.Count == 0)
            blockers.Add("There's nothing to post: no owned unit has a charge this month.");

        return new Plan(result, totals, blockers, warnings, chargeIds);
    }

    private static string Label(UnitDto u) => u.DoorNumber ?? u.SectionNumber.ToString(CultureInfo.InvariantCulture);

    private Task<BillingSettingsDto> SettingsAsync(CancellationToken ct) => _settings.GetSettingsAsync(ct);

    private BillingRunDto ToDto(BillingRun r) =>
        new(r.Id, r.Period, r.Status, r.BillingDate, r.TotalPosted, r.AccountsPosted, r.Message, r.PostedAt, _progress.Get(r.Id));

    private Task AuditAsync(string type, Guid id, string action, string details, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto(type, id.ToString(), action, _user.UserId ?? "system", details), ct);
}
