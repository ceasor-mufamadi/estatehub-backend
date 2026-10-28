using EstateHub.Application.DTOs;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Interfaces;

/// <summary>Platform (super admin) management of estates and their features.</summary>
public interface IEstateService
{
    Task<EstateDto> CreateAsync(string name, string schemeType, string performedBy, CancellationToken ct = default);

    Task<EstateDto?> GetAsync(Guid estateId, CancellationToken ct = default);

    Task<IReadOnlyList<EstateDto>> ListAsync(CancellationToken ct = default);

    Task<EstateDto> SetFeatureAsync(Guid estateId, string featureKey, bool enabled, string performedBy, CancellationToken ct = default);
}

public interface IEstateRepository
{
    Task AddAsync(Estate estate, CancellationToken ct = default);

    Task<Estate?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Estate>> ListAsync(CancellationToken ct = default);

    /// <summary>Case-insensitive: "Riverside" and "riverside " are the same name.</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);

    /// <summary>Tracked, for updates.</summary>
    Task<Estate?> GetForUpdateAsync(Guid id, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IFeatureService
{
    Task<IReadOnlySet<string>> GetEnabledAsync(Guid estateId, CancellationToken ct = default);

    Task<bool> IsEnabledAsync(Guid estateId, string featureKey, CancellationToken ct = default);
}

public interface IFeatureRepository
{
    Task<IReadOnlyDictionary<string, bool>> GetOverridesAsync(Guid estateId, CancellationToken ct = default);

    Task UpsertAsync(Guid estateId, string featureKey, bool enabled, string updatedBy, CancellationToken ct = default);
}

/// <summary>Blocks inside the active estate.</summary>
public interface IBlockService
{
    Task<BlockDto> CreateAsync(string name, string performedBy, CancellationToken ct = default);

    Task<IReadOnlyList<BlockDto>> ListAsync(CancellationToken ct = default);

    Task<BlockDto> RenameAsync(Guid blockId, string name, string performedBy, CancellationToken ct = default);

    /// <summary>Only an empty block (no units) can be removed.</summary>
    Task DeleteAsync(Guid blockId, string performedBy, CancellationToken ct = default);
}

/// <summary>Units (sections) inside the active estate.</summary>
public interface IUnitService
{
    Task<UnitsView> ListAsync(CancellationToken ct = default);

    Task<UnitDto> CreateAsync(UnitInput input, string performedBy, CancellationToken ct = default);

    Task<UnitDto> UpdateAsync(Guid unitId, UnitInput input, string performedBy, CancellationToken ct = default);

    Task DeleteAsync(Guid unitId, string performedBy, CancellationToken ct = default);
}

public interface IUnitRepository
{
    Task<IReadOnlyList<UnitDto>> ListAsync(CancellationToken ct = default);
    Task<Unit?> GetAsync(Guid id, CancellationToken ct = default);
    Task<bool> SectionExistsAsync(int sectionNumber, Guid? exceptUnitId, CancellationToken ct = default);
    Task<bool> AnyInBlockAsync(Guid blockId, CancellationToken ct = default);
    void Add(Unit unit);
    void Remove(Unit unit);
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>The active estate's own details (staff edit them; super admin creates estates).</summary>
public interface IEstateDetailsService
{
    Task<EstateDetailsDto> GetAsync(CancellationToken ct = default);

    Task<EstateDetailsDto> UpdateAsync(string name, string? schemeNumber, string? csosRegistrationNumber, string? address, string performedBy, CancellationToken ct = default);

    /// <summary>POPIA settings: the Information Officer, and whether visitors' ID numbers are recorded (and for how long).</summary>
    Task<EstatePrivacyDto> GetPrivacyAsync(CancellationToken ct = default);

    Task<EstatePrivacyDto> UpdatePrivacyAsync(EstatePrivacyDto settings, string performedBy, CancellationToken ct = default);
}

public record EstatePrivacyDto(string? InformationOfficerName, string? InformationOfficerEmail, bool VisitorIdCapture, int VisitorRetentionDays);

public interface IBlockRepository
{
    Task AddAsync(Block block, CancellationToken ct = default);

    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<Block>> ListAsync(CancellationToken ct = default);

    Task<Block?> GetAsync(Guid id, CancellationToken ct = default);

    Task<bool> NameExistsAsync(string name, Guid? exceptBlockId, CancellationToken ct = default);

    void Remove(Block block);

    Task SaveChangesAsync(CancellationToken ct = default);
}
