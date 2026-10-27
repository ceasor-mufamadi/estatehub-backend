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
}

public interface IBlockRepository
{
    Task AddAsync(Block block, CancellationToken ct = default);

    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<Block>> ListAsync(CancellationToken ct = default);
}
