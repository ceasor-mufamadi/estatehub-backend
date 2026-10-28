using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// Units of the active estate. The participation quota (PQ) is a percentage with up to 4 decimals, as
/// on sectional plans; the list reports whether the quotas add up to 100%, which billing needs.
/// </summary>
public class UnitService : IUnitService
{
    public const decimal FullQuota = 100m;
    public const int MaxSectionNumber = 99_999;

    private readonly IUnitRepository _units;
    private readonly IBlockRepository _blocks;
    private readonly IAuditLogService _audit;

    public UnitService(IUnitRepository units, IBlockRepository blocks, IAuditLogService audit)
    {
        _units = units;
        _blocks = blocks;
        _audit = audit;
    }

    public async Task<UnitsView> ListAsync(CancellationToken ct = default)
    {
        var units = await _units.ListAsync(ct);
        var total = units.Sum(u => u.ParticipationQuota);
        return new UnitsView(units, total, units.Count > 0 && total == FullQuota);
    }

    public async Task<UnitDto> CreateAsync(UnitInput input, string performedBy, CancellationToken ct = default)
    {
        var (door, blockName) = await ValidateAsync(input, exceptUnitId: null, ct);
        var unit = new Unit
        {
            SectionNumber = input.SectionNumber,
            DoorNumber = door,
            BlockId = input.BlockId,
            ParticipationQuota = input.ParticipationQuota,
            SizeSquareMetres = input.SizeSquareMetres,
        };
        _units.Add(unit);
        await _units.SaveChangesAsync(ct);

        await _audit.CreateAsync(new CreateAuditLogDto("Unit", unit.Id.ToString(), "Created", performedBy,
            $"section={unit.SectionNumber} pq={unit.ParticipationQuota}"), ct);
        return ToDto(unit, blockName);
    }

    public async Task<UnitDto> UpdateAsync(Guid unitId, UnitInput input, string performedBy, CancellationToken ct = default)
    {
        var unit = await _units.GetAsync(unitId, ct) ?? throw new KeyNotFoundException("Unit not found.");
        var (door, blockName) = await ValidateAsync(input, unitId, ct);

        var before = $"section={unit.SectionNumber} pq={unit.ParticipationQuota}";
        unit.SectionNumber = input.SectionNumber;
        unit.DoorNumber = door;
        unit.BlockId = input.BlockId;
        unit.ParticipationQuota = input.ParticipationQuota;
        unit.SizeSquareMetres = input.SizeSquareMetres;
        await _units.SaveChangesAsync(ct);

        await _audit.CreateAsync(new CreateAuditLogDto("Unit", unit.Id.ToString(), "Updated", performedBy,
            $"{before} → section={unit.SectionNumber} pq={unit.ParticipationQuota}"), ct);
        return ToDto(unit, blockName);
    }

    public async Task DeleteAsync(Guid unitId, string performedBy, CancellationToken ct = default)
    {
        var unit = await _units.GetAsync(unitId, ct) ?? throw new KeyNotFoundException("Unit not found.");
        _units.Remove(unit);
        await _units.SaveChangesAsync(ct);
        await _audit.CreateAsync(new CreateAuditLogDto("Unit", unit.Id.ToString(), "Removed", performedBy,
            $"section={unit.SectionNumber} pq={unit.ParticipationQuota}"), ct);
    }

    private async Task<(string? Door, string? BlockName)> ValidateAsync(UnitInput input, Guid? exceptUnitId, CancellationToken ct)
    {
        if (input.SectionNumber is < 1 or > MaxSectionNumber)
            throw new ArgumentException($"Section number must be between 1 and {MaxSectionNumber}.");
        if (await _units.SectionExistsAsync(input.SectionNumber, exceptUnitId, ct))
            throw new ArgumentException($"Section {input.SectionNumber} already exists in this estate.");

        var door = string.IsNullOrWhiteSpace(input.DoorNumber) ? null : input.DoorNumber.Trim();
        if (door is { Length: > 20 })
            throw new ArgumentException("Door number must be at most 20 characters.");

        // Sectional plans give quotas as percentages to four decimals.
        if (input.ParticipationQuota is <= 0 or > FullQuota)
            throw new ArgumentException("Participation quota must be a percentage above 0 and at most 100.");
        if (decimal.Round(input.ParticipationQuota, 4) != input.ParticipationQuota)
            throw new ArgumentException("Participation quota can have at most 4 decimals (e.g. 2.3456).");

        if (input.SizeSquareMetres is { } size && size is <= 0 or > 100_000)
            throw new ArgumentException("Size must be above 0 and at most 100 000 m².");

        string? blockName = null;
        if (input.BlockId is { } blockId)
            blockName = (await _blocks.GetAsync(blockId, ct))?.Name ?? throw new ArgumentException("That block doesn't exist in this estate.");

        return (door, blockName);
    }

    private static UnitDto ToDto(Unit u, string? blockName) =>
        new(u.Id, u.SectionNumber, u.DoorNumber, u.BlockId, blockName, u.ParticipationQuota, u.SizeSquareMetres);
}

/// <summary>The active estate's own details: name, scheme number, CSOS registration, address.</summary>
public class EstateDetailsService : IEstateDetailsService
{
    private readonly IEstateRepository _estates;
    private readonly ITenantContext _tenant;
    private readonly IAuditLogService _audit;

    public EstateDetailsService(IEstateRepository estates, ITenantContext tenant, IAuditLogService audit)
    {
        _estates = estates;
        _tenant = tenant;
        _audit = audit;
    }

    private Guid EstateId => _tenant.EstateId ?? throw new InvalidOperationException("No active estate.");

    public async Task<EstateDetailsDto> GetAsync(CancellationToken ct = default) =>
        ToDto(await _estates.GetAsync(EstateId, ct) ?? throw new KeyNotFoundException("Estate not found."));

    public async Task<EstateDetailsDto> UpdateAsync(
        string name, string? schemeNumber, string? csosRegistrationNumber, string? address, string performedBy, CancellationToken ct = default)
    {
        var estate = await _estates.GetForUpdateAsync(EstateId, ct) ?? throw new KeyNotFoundException("Estate not found.");

        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 200)
            throw new ArgumentException("Name is required (up to 200 characters).");
        if (!string.Equals(name, estate.Name, StringComparison.OrdinalIgnoreCase) && await _estates.NameExistsAsync(name, ct))
            throw new ArgumentException($"An estate named '{name}' already exists. Add the suburb or town to tell them apart.");

        estate.Name = name;
        estate.SchemeNumber = Optional(schemeNumber, 50, "Scheme number");
        estate.CsosRegistrationNumber = Optional(csosRegistrationNumber, 50, "CSOS registration number");
        estate.Address = Optional(address, 500, "Address");
        await _estates.SaveChangesAsync(ct);

        await _audit.CreateAsync(new CreateAuditLogDto("Estate", estate.Id.ToString(), "DetailsUpdated", performedBy,
            $"name={estate.Name} scheme={estate.SchemeNumber} csos={estate.CsosRegistrationNumber}"), ct);
        return ToDto(estate);
    }

    public const int MinRetentionDays = 30;
    public const int MaxRetentionDays = 365;

    public async Task<EstatePrivacyDto> GetPrivacyAsync(CancellationToken ct = default) =>
        ToPrivacy(await _estates.GetAsync(EstateId, ct) ?? throw new KeyNotFoundException("Estate not found."));

    public async Task<EstatePrivacyDto> UpdatePrivacyAsync(EstatePrivacyDto settings, string performedBy, CancellationToken ct = default)
    {
        var estate = await _estates.GetForUpdateAsync(EstateId, ct) ?? throw new KeyNotFoundException("Estate not found.");
        if (settings.VisitorRetentionDays is < MinRetentionDays or > MaxRetentionDays)
            throw new ArgumentException($"Keep visitor records for {MinRetentionDays} to {MaxRetentionDays} days.");

        var officerEmail = Optional(settings.InformationOfficerEmail, 254, "Information Officer email");
        estate.InformationOfficerName = Optional(settings.InformationOfficerName, 200, "Information Officer name");
        estate.InformationOfficerEmail = officerEmail is null ? null : Auth.ContactPoints.Email(officerEmail);
        var before = $"visitorIds={estate.VisitorIdCapture} retention={estate.VisitorRetentionDays}";
        estate.VisitorIdCapture = settings.VisitorIdCapture;
        estate.VisitorRetentionDays = settings.VisitorRetentionDays;
        await _estates.SaveChangesAsync(ct);

        await _audit.CreateAsync(new CreateAuditLogDto("Estate", estate.Id.ToString(), "PrivacyUpdated", performedBy,
            $"{before} → visitorIds={estate.VisitorIdCapture} retention={estate.VisitorRetentionDays} officer={(estate.InformationOfficerName is null ? "none" : "set")}"), ct);
        return ToPrivacy(estate);
    }

    private static EstatePrivacyDto ToPrivacy(Estate e) =>
        new(e.InformationOfficerName, e.InformationOfficerEmail, e.VisitorIdCapture, e.VisitorRetentionDays);

    private static string? Optional(string? value, int max, string label)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return trimmed is { Length: var length } && length > max ? throw new ArgumentException($"{label} must be at most {max} characters.") : trimmed;
    }

    private static EstateDetailsDto ToDto(Estate e) => new(e.Id, e.Name, e.SchemeType, e.SchemeNumber, e.CsosRegistrationNumber, e.Address);
}
