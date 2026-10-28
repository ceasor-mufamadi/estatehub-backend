using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace EstateHub.Application.Services;

/// <summary>
/// Billing setup (slice 4.1, PHASE1.md "Sprint 4 design"): the estate's rules, its bank details and its
/// charge types. Scheme-specific rules are settings here, never code.
/// </summary>
public class BillingService : IBillingService
{
    public const int MaxChargeTypes = 30;

    private readonly IBillingRepository _billing;
    private readonly IUnitRepository _units;
    private readonly IMembersRepository _members;
    private readonly IEstateRepository _estates;
    private readonly IEmailSender _email;
    private readonly ICurrentUser _user;
    private readonly IAuditLogService _audit;
    private readonly ILogger<BillingService> _logger;

    public BillingService(
        IBillingRepository billing,
        IUnitRepository units,
        IMembersRepository members,
        IEstateRepository estates,
        IEmailSender email,
        ICurrentUser user,
        IAuditLogService audit,
        ILogger<BillingService> logger)
    {
        _billing = billing;
        _units = units;
        _members = members;
        _estates = estates;
        _email = email;
        _user = user;
        _audit = audit;
        _logger = logger;
    }

    // ---------- settings ----------

    public async Task<BillingSettingsDto> GetSettingsAsync(CancellationToken ct = default) => ToDto(await SettingsAsync(ct));

    private async Task<BillingSettings> SettingsAsync(CancellationToken ct)
    {
        if (await _billing.GetSettingsAsync(ct) is { } existing)
            return existing;

        // First time: defaults, and the built-in levy (by quota) every scheme has.
        var settings = new BillingSettings();
        _billing.Add(settings);
        if (!(await _billing.ListChargeTypesAsync(ct)).Any(c => c.IsLevy))
            _billing.Add(new ChargeType { Name = "Levy", Method = ChargeMethods.Quota, IsLevy = true, SortOrder = 0 });
        await _billing.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<BillingSettingsDto> UpdateSettingsAsync(BillingSettingsInput input, CancellationToken ct = default)
    {
        if (input.FinancialYearStartMonth is < 1 or > 12)
            throw new ArgumentException("Choose the month the financial year starts.");
        if (input.BillingDay is < 1 or > 28 || input.DueDay is < 1 or > 28)
            throw new ArgumentException("Billing and due days must be between 1 and 28 (every month has them).");
        if (input.LevyDisplay is not (LevyDisplays.Combined or LevyDisplays.Split))
            throw new ArgumentException("Show the levy as one line or split into admin and reserve fund.");
        if (input.InterestRatePercent is < 0 or > 50 || decimal.Round(input.InterestRatePercent, 2) != input.InterestRatePercent)
            throw new ArgumentException("Interest rate must be a yearly percentage from 0 to 50, with up to 2 decimals.");
        if (input.InterestEnabled && input.InterestRatePercent == 0)
            throw new ArgumentException("Give the yearly interest rate, or switch interest off.");

        var settings = await SettingsAsync(ct);
        settings.FinancialYearStartMonth = input.FinancialYearStartMonth;
        settings.BillingDay = input.BillingDay;
        settings.DueDay = input.DueDay;
        settings.LevyDisplay = input.LevyDisplay;
        settings.InterestEnabled = input.InterestEnabled;
        settings.InterestRatePercent = input.InterestRatePercent;
        settings.QueriesEmail = string.IsNullOrWhiteSpace(input.QueriesEmail) ? null : ContactPoints.Email(input.QueriesEmail);
        settings.QueriesPhone = Optional(input.QueriesPhone, 30, "Queries phone");
        await _billing.SaveChangesAsync(ct);
        await AuditAsync("BillingSettings", settings.Id, "Updated",
            $"year_start={settings.FinancialYearStartMonth} billing_day={settings.BillingDay} due_day={settings.DueDay} levy={settings.LevyDisplay} interest={(settings.InterestEnabled ? settings.InterestRatePercent + "%" : "off")}", ct);
        return ToDto(settings);
    }

    public async Task<BillingSettingsDto> UpdateBankDetailsAsync(BankDetailsInput input, CancellationToken ct = default)
    {
        var accountNumber = new string((input.AccountNumber ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        var branch = new string((input.BranchCode ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (accountNumber.Length is < 6 or > 16)
            throw new ArgumentException("Account number must be 6 to 16 digits.");
        if (branch.Length is < 4 or > 6)
            throw new ArgumentException("Branch code must be 4 to 6 digits.");

        var settings = await SettingsAsync(ct);
        var before = Mask(settings.BankAccountNumber);
        settings.BankName = Optional(input.BankName, 100, "Bank") ?? throw new ArgumentException("Bank is required.");
        settings.BankBranchCode = branch;
        settings.BankAccountName = Optional(input.AccountName, 200, "Account name") ?? throw new ArgumentException("Account name is required.");
        settings.BankAccountNumber = accountNumber;
        await _billing.SaveChangesAsync(ct);
        await AuditAsync("BillingSettings", settings.Id, "BankDetailsChanged", $"from={before} to={Mask(accountNumber)}", ct);

        // Changed bank details are the classic fraud here: every trustee hears about it straight away.
        var estate = await _estates.GetAsync(settings.EstateId, ct);
        foreach (var trustee in (await _members.ListMembersAsync(ct)).Where(m => m.Membership.Role == Roles.Trustee && m.Membership.IsActive && m.User.Email is not null))
        {
            try
            {
                await _email.SendAsync(trustee.User.Email!, $"{estate?.Name}: the bank details on statements were changed",
                    $"The bank account printed on {estate?.Name}'s levy statements was changed to {settings.BankName}, account {Mask(accountNumber)}.\n\n" +
                    "If you didn't expect this, contact your managing agent straight away and check the details before any statements go out.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Bank-details alert to a trustee of estate {EstateId} not sent", settings.EstateId);
            }
        }
        return ToDto(settings);
    }

    // ---------- charge types ----------

    public async Task<IReadOnlyList<ChargeTypeDto>> ListChargeTypesAsync(CancellationToken ct = default)
    {
        await SettingsAsync(ct);
        return (await _billing.ListChargeTypesAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<ChargeTypeDto> CreateChargeTypeAsync(ChargeTypeInput input, CancellationToken ct = default)
    {
        await SettingsAsync(ct);
        var existing = await _billing.ListChargeTypesAsync(ct);
        if (existing.Count >= MaxChargeTypes)
            throw new ArgumentException($"An estate can have up to {MaxChargeTypes} charge types.");
        if (input.Method == ChargeMethods.Quota)
            throw new ArgumentException("Only the levy is split by quota; special levies come in their own step.");

        var charge = new ChargeType();
        Apply(charge, input, existing);
        _billing.Add(charge);
        await _billing.SaveChangesAsync(ct);
        await AuditAsync("ChargeType", charge.Id, "Created", $"name={charge.Name} method={charge.Method} amount={charge.Amount}", ct);
        return ToDto(charge);
    }

    public async Task<ChargeTypeDto> UpdateChargeTypeAsync(Guid id, ChargeTypeInput input, CancellationToken ct = default)
    {
        var charge = await _billing.GetChargeTypeAsync(id, ct) ?? throw new KeyNotFoundException("Charge type not found.");
        if (charge.IsLevy && (input.Method != ChargeMethods.Quota || !input.IsActive))
            throw new ArgumentException("The levy is always split by quota and can't be switched off; only its name can change.");
        if (!charge.IsLevy && input.Method != charge.Method)
            throw new ArgumentException("A charge type keeps how it's worked out; add a new one instead.");

        Apply(charge, input, await _billing.ListChargeTypesAsync(ct));
        await _billing.SaveChangesAsync(ct);
        await AuditAsync("ChargeType", charge.Id, "Updated", $"name={charge.Name} amount={charge.Amount} active={charge.IsActive}", ct);
        return ToDto(charge);
    }

    private static void Apply(ChargeType charge, ChargeTypeInput input, IReadOnlyList<ChargeType> all)
    {
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 60)
            throw new ArgumentException("Name is required (up to 60 characters), as it should appear on statements.");
        if (all.Any(c => c.Id != charge.Id && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"There's already a charge called '{name}'.");
        if (!ChargeMethods.All.Contains(input.Method))
            throw new ArgumentException("Choose how the charge is worked out.");
        if (input.Method == ChargeMethods.Flat && input.Amount is not > 0)
            throw new ArgumentException("Give the monthly amount every unit pays.");
        if (input.Amount is { } amount && (amount < 0 || amount > 1_000_000 || decimal.Round(amount, 2) != amount))
            throw new ArgumentException("Amount must be in rands and cents, from 0 to 1 000 000.");

        charge.Name = name;
        charge.Method = input.Method;
        charge.Amount = input.Method == ChargeMethods.Flat ? input.Amount : null;
        charge.IsActive = input.IsActive;
        charge.SortOrder = input.SortOrder;
    }

    public async Task<IReadOnlyList<UnitChargeDto>> ListUnitChargesAsync(Guid chargeTypeId, CancellationToken ct = default)
    {
        await RequirePerUnitAsync(chargeTypeId, ct);
        var amounts = (await _billing.ListUnitChargesAsync(chargeTypeId, ct)).ToDictionary(u => u.UnitId, u => u.Amount);
        return (await _units.ListAsync(ct))
            .Select(u => new UnitChargeDto(u.Id, u.SectionNumber, u.DoorNumber, amounts.GetValueOrDefault(u.Id)))
            .ToList();
    }

    public async Task<IReadOnlyList<UnitChargeDto>> SetUnitChargesAsync(Guid chargeTypeId, IReadOnlyList<UnitAmount> amounts, CancellationToken ct = default)
    {
        var charge = await RequirePerUnitAsync(chargeTypeId, ct);
        var units = (await _units.ListAsync(ct)).Select(u => u.Id).ToHashSet();
        if (amounts.Any(a => !units.Contains(a.UnitId)))
            throw new ArgumentException("One of the units doesn't exist in this estate.");
        if (amounts.Any(a => a.Amount < 0 || a.Amount > 1_000_000 || decimal.Round(a.Amount, 2) != a.Amount))
            throw new ArgumentException("Amounts must be in rands and cents, from 0 to 1 000 000.");

        var current = (await _billing.ListUnitChargesAsync(chargeTypeId, ct)).ToDictionary(u => u.UnitId);
        foreach (var a in amounts)
        {
            if (current.TryGetValue(a.UnitId, out var row))
            {
                if (a.Amount == 0)
                    _billing.Remove(row);
                else
                    row.Amount = a.Amount;
            }
            else if (a.Amount > 0)
                _billing.Add(new UnitCharge { ChargeTypeId = chargeTypeId, UnitId = a.UnitId, Amount = a.Amount });
        }
        await _billing.SaveChangesAsync(ct);
        await AuditAsync("ChargeType", charge.Id, "UnitAmountsSet", $"units={amounts.Count}", ct);
        return await ListUnitChargesAsync(chargeTypeId, ct);
    }

    private async Task<ChargeType> RequirePerUnitAsync(Guid chargeTypeId, CancellationToken ct)
    {
        var charge = await _billing.GetChargeTypeAsync(chargeTypeId, ct) ?? throw new KeyNotFoundException("Charge type not found.");
        return charge.Method == ChargeMethods.PerUnit ? charge : throw new ArgumentException("Only per-unit charges have an amount per unit.");
    }

    // ---------- helpers ----------

    /// <summary>"1234567890" → "******7890": enough to recognise in an alert or the trail.</summary>
    public static string Mask(string? accountNumber) =>
        accountNumber is { Length: > 4 } n ? new string('*', n.Length - 4) + n[^4..] : "none";

    private static string? Optional(string? value, int max, string label)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return trimmed is { Length: var length } && length > max ? throw new ArgumentException($"{label} must be at most {max} characters.") : trimmed;
    }

    private Task AuditAsync(string type, Guid id, string action, string details, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto(type, id.ToString(), action, _user.UserId ?? "system", details), ct);

    private static BillingSettingsDto ToDto(BillingSettings s) => new(
        s.FinancialYearStartMonth, s.BillingDay, s.DueDay, s.LevyDisplay, s.InterestEnabled, s.InterestRatePercent,
        s.QueriesEmail, s.QueriesPhone, s.BankName, s.BankBranchCode, s.BankAccountName, s.BankAccountNumber);

    private static ChargeTypeDto ToDto(ChargeType c) => new(c.Id, c.Name, c.Method, c.Amount, c.IsLevy, c.IsActive, c.SortOrder);
}
