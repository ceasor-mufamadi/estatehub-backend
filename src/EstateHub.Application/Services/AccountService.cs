using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// Accounts and the ledger (slice 4.1). One account per ownership period: when a unit's owners change,
/// the open account closes (keeping its balance: arrears stay with the former owner) and a new one opens
/// at R 0. Entries are never edited; corrections are reversals.
/// </summary>
public class AccountService : IAccountService
{
    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    private readonly IBillingRepository _billing;
    private readonly IPeopleRepository _people;
    private readonly IUnitRepository _units;
    private readonly ICurrentUser _user;
    private readonly IAuditLogService _audit;
    private readonly TimeProvider _clock;

    public AccountService(IBillingRepository billing, IPeopleRepository people, IUnitRepository units, ICurrentUser user, IAuditLogService audit, TimeProvider clock)
    {
        _billing = billing;
        _people = people;
        _units = units;
        _user = user;
        _audit = audit;
        _clock = clock;
    }

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, SouthAfrica));

    private Guid ActorId => Guid.TryParse(_user.UserId, out var id) ? id : Guid.Empty;

    // ---------- following ownership ----------

    public async Task SyncUnitAsync(Guid unitId, CancellationToken ct = default)
    {
        var unit = await _units.GetAsync(unitId, ct);
        if (unit is null)
            return;

        var today = Today;
        var ownerships = await _people.OwnershipsOfUnitAsync(unitId, ct);
        var owners = ownerships.Where(o => o.FromDate <= today && (o.ToDate is null || o.ToDate >= today)).Select(o => o.PersonId).ToHashSet();
        var accounts = (await _billing.ListAccountsAsync(ct)).Where(a => a.UnitId == unitId).ToList();
        var open = accounts.FirstOrDefault(a => a.ClosedOn is null);
        var holders = open is null ? [] : (await _billing.HoldersAsync([open.Id], ct)).Select(h => h.PersonId).ToHashSet();

        if (open is not null && holders.SetEquals(owners))
            return;

        // When the set of owners changed: the latest start of a new owner or day after an old owner's end.
        var changes = ownerships.Where(o => owners.Contains(o.PersonId) && !holders.Contains(o.PersonId)).Select(o => o.FromDate)
            .Concat(ownerships.Where(o => holders.Contains(o.PersonId) && !owners.Contains(o.PersonId) && o.ToDate is not null).Select(o => o.ToDate!.Value.AddDays(1)))
            .Where(d => d <= today)
            .ToList();
        var changedOn = changes.Count > 0 ? changes.Max() : today;

        if (open is not null)
        {
            if (!await _billing.AnyEntriesAsync(open.Id, ct))
            {
                // Nothing posted yet (e.g. ownership still being set up): reuse it rather than leave empty accounts.
                foreach (var h in await _billing.HoldersAsync([open.Id], ct))
                    _billing.Remove(h);
                if (owners.Count == 0)
                    _billing.Remove(open); // never used: no trace needed
                else
                {
                    open.OpenedOn = changedOn;
                    foreach (var personId in owners)
                        _billing.Add(new AccountHolder { AccountId = open.Id, PersonId = personId });
                }
                await _billing.SaveChangesAsync(ct);
                return;
            }

            open.ClosedOn = changedOn.AddDays(-1) < open.OpenedOn ? open.OpenedOn : changedOn.AddDays(-1);
            await _billing.SaveChangesAsync(ct);
            await AuditAsync(open.Id, "Closed", $"on={open.ClosedOn:yyyy-MM-dd}", ct);
        }

        if (owners.Count == 0)
            return;

        var account = new Account { UnitId = unitId, Number = NumberOf(unit), OpenedOn = changedOn };
        _billing.Add(account);
        foreach (var personId in owners)
            _billing.Add(new AccountHolder { AccountId = account.Id, PersonId = personId });
        await _billing.SaveChangesAsync(ct);
        await AuditAsync(account.Id, "Opened", $"unit={unitId} on={account.OpenedOn:yyyy-MM-dd} holders={owners.Count}", ct);
    }

    public async Task<int> SyncAllAsync(CancellationToken ct = default)
    {
        var units = await _units.ListAsync(ct);
        foreach (var unit in units)
            await SyncUnitAsync(unit.Id, ct);
        return units.Count;
    }

    /// <summary>The number owners pay with: the door number, else the section number.</summary>
    private static string NumberOf(Unit unit) => unit.DoorNumber ?? unit.SectionNumber.ToString();

    // ---------- reading ----------

    public async Task<IReadOnlyList<AccountSummaryDto>> ListAsync(AccountFilter filter, string? search, CancellationToken ct = default)
    {
        var all = await SummariesAsync(await _billing.ListAccountsAsync(ct), ct);
        IEnumerable<AccountSummaryDto> list = filter switch
        {
            AccountFilter.Open => all.Where(a => a.ClosedOn is null),
            AccountFilter.Owing => all.Where(a => a.Balance > 0),
            AccountFilter.InCredit => all.Where(a => a.Balance < 0),
            AccountFilter.FormerOwnersOwing => all.Where(a => a.ClosedOn is not null && a.Balance > 0),
            _ => all,
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            list = list.Where(a => a.Number.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || a.SectionNumber.ToString() == term
                                   || a.Holders.Any(h => h.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
        return list.ToList();
    }

    public async Task<IReadOnlyList<AccountSummaryDto>> OfUnitAsync(Guid unitId, CancellationToken ct = default) =>
        await SummariesAsync((await _billing.ListAccountsAsync(ct)).Where(a => a.UnitId == unitId).ToList(), ct);

    public async Task<IReadOnlyList<AccountSummaryDto>> MineAsync(CancellationToken ct = default)
    {
        if (!Guid.TryParse(_user.UserId, out var userId))
            return [];
        var me = (await _people.PeopleOfUserAsync(userId, ct)).Select(p => p.Id).ToList();
        if (me.Count == 0)
            return [];
        var ids = (await _billing.AccountsHeldByAsync(me, ct)).ToHashSet();
        return await SummariesAsync((await _billing.ListAccountsAsync(ct)).Where(a => ids.Contains(a.Id)).ToList(), ct);
    }

    public async Task<AccountDetailDto> GetAsync(Guid accountId, CancellationToken ct = default)
    {
        var account = await _billing.GetAccountAsync(accountId, ct) ?? throw new KeyNotFoundException("Account not found.");
        var summary = (await SummariesAsync([account], ct))[0];
        var entries = (await _billing.EntriesAsync(accountId, ct)).OrderBy(e => e.Date).ThenBy(e => e.CreatedAt).ToList();
        var reversed = entries.Where(e => e.ReversesEntryId is not null).Select(e => e.ReversesEntryId!.Value).ToHashSet();

        var running = 0m;
        var lines = entries.Select(e =>
        {
            running += e.Debit - e.Credit;
            return new LedgerLineDto(e.Id, e.Date, e.Type, e.Description, e.Reference, e.Debit, e.Credit, running, e.ReversesEntryId, reversed.Contains(e.Id));
        }).ToList();
        return new AccountDetailDto(summary, lines);
    }

    private async Task<List<AccountSummaryDto>> SummariesAsync(IReadOnlyList<Account> accounts, CancellationToken ct)
    {
        var units = (await _units.ListAsync(ct)).ToDictionary(u => u.Id);
        var holders = (await _billing.HoldersAsync(accounts.Select(a => a.Id).ToList(), ct)).ToLookup(h => h.AccountId, h => h.PersonId);
        var names = new Dictionary<Guid, string>();
        foreach (var personId in holders.SelectMany(g => g).Distinct())
            if (await _people.GetPersonAsync(personId, ct) is { } p)
                names[personId] = p.Name;
        var balances = await _billing.BalancesAsync(ct);

        return accounts
            .OrderBy(a => units.TryGetValue(a.UnitId, out var u) ? u.SectionNumber : int.MaxValue)
            .ThenBy(a => a.ClosedOn is null ? 0 : 1).ThenByDescending(a => a.OpenedOn)
            .Select(a => new AccountSummaryDto(
                a.Id, a.UnitId, units.TryGetValue(a.UnitId, out var u) ? u.SectionNumber : 0, a.Number,
                holders[a.Id].Select(id => names.GetValueOrDefault(id, "?")).Order().ToList(),
                a.OpenedOn, a.ClosedOn, balances.GetValueOrDefault(a.Id)))
            .ToList();
    }

    // ---------- writing ----------

    public async Task<AccountDetailDto> RecordOpeningBalanceAsync(Guid accountId, decimal amount, DateOnly date, CancellationToken ct = default)
    {
        var account = await _billing.GetAccountAsync(accountId, ct) ?? throw new KeyNotFoundException("Account not found.");
        if (amount == 0 || Math.Abs(amount) > 10_000_000 || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Give the balance in rands and cents: positive if they owe, negative if they're in credit.");

        var entries = await _billing.EntriesAsync(accountId, ct);
        var reversed = entries.Where(e => e.ReversesEntryId is not null).Select(e => e.ReversesEntryId!.Value).ToHashSet();
        if (entries.Any(e => e.Type == LedgerTypes.OpeningBalance && !reversed.Contains(e.Id)))
            throw new ArgumentException("This account already has an opening balance. Reverse it first if it was wrong.");

        _billing.Add(new LedgerEntry
        {
            AccountId = account.Id,
            Date = date,
            Type = LedgerTypes.OpeningBalance,
            Description = amount > 0 ? "Opening balance" : "Opening balance (credit)",
            Debit = amount > 0 ? amount : 0,
            Credit = amount < 0 ? -amount : 0,
            Source = LedgerSources.Opening,
            CreatedByUserId = ActorId,
        });
        await _billing.SaveChangesAsync(ct);
        await AuditAsync(account.Id, "OpeningBalance", $"amount={amount:0.00} date={date:yyyy-MM-dd}", ct);
        return await GetAsync(account.Id, ct);
    }

    public async Task<AccountDetailDto> ReverseAsync(Guid entryId, string? reason, CancellationToken ct = default)
    {
        var entry = await _billing.GetEntryAsync(entryId, ct) ?? throw new KeyNotFoundException("Entry not found.");
        if (entry.Type == LedgerTypes.Reversal)
            throw new ArgumentException("A reversal can't itself be reversed; add a new entry instead.");
        if ((await _billing.EntriesAsync(entry.AccountId, ct)).Any(e => e.ReversesEntryId == entry.Id))
            throw new ArgumentException("This entry has already been reversed.");
        var note = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (note is { Length: > 100 })
            throw new ArgumentException("Keep the reason under 100 characters.");

        _billing.Add(new LedgerEntry
        {
            AccountId = entry.AccountId,
            Date = Today,
            Type = LedgerTypes.Reversal,
            ChargeTypeId = entry.ChargeTypeId,
            Description = Truncate($"Incorrect {entry.Description}{(note is null ? "" : $" ({note})")}", 200),
            Reference = entry.Reference,
            Debit = entry.Credit,
            Credit = entry.Debit,
            Period = entry.Period,
            Source = LedgerSources.Staff,
            ReversesEntryId = entry.Id,
            CreatedByUserId = ActorId,
        });
        await _billing.SaveChangesAsync(ct);
        await AuditAsync(entry.AccountId, "EntryReversed", $"entry={entry.Id} amount={entry.Debit - entry.Credit:0.00}", ct);
        return await GetAsync(entry.AccountId, ct);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private Task AuditAsync(Guid accountId, string action, string details, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto("Account", accountId.ToString(), action, _user.UserId ?? "system", details), ct);
}
