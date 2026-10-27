using EstateHub.Application.DTOs;
using EstateHub.Application.Features;
using EstateHub.Application.Interfaces;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

public class EstateService : IEstateService
{
    private const int MaxNameLength = 200;

    private readonly IEstateRepository _estates;
    private readonly IFeatureRepository _features;
    private readonly IFeatureService _featureService;
    private readonly IAuditLogService _audit;

    public EstateService(IEstateRepository estates, IFeatureRepository features, IFeatureService featureService, IAuditLogService audit)
    {
        _estates = estates;
        _features = features;
        _featureService = featureService;
        _audit = audit;
    }

    public async Task<EstateDto> CreateAsync(string name, string schemeType, string performedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        name = name.Trim();
        if (name.Length > MaxNameLength)
            throw new ArgumentException($"Name must be at most {MaxNameLength} characters.", nameof(name));

        schemeType = string.IsNullOrWhiteSpace(schemeType) ? SchemeTypes.SectionalTitle : schemeType.Trim();
        if (!SchemeTypes.All.Contains(schemeType))
            throw new ArgumentException($"Unknown scheme type '{schemeType}'.", nameof(schemeType));

        var estate = new Estate { Name = name, SchemeType = schemeType };
        await _estates.AddAsync(estate, ct);

        await _audit.CreateAsync(
            new CreateAuditLogDto("Estate", estate.Id.ToString(), "Created", performedBy, $"name={name}", EstateId: estate.Id), ct);

        return await ToDtoAsync(estate, ct);
    }

    public async Task<EstateDto?> GetAsync(Guid estateId, CancellationToken ct = default)
    {
        var estate = await _estates.GetAsync(estateId, ct);
        return estate is null ? null : await ToDtoAsync(estate, ct);
    }

    public async Task<IReadOnlyList<EstateDto>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<EstateDto>();
        foreach (var estate in await _estates.ListAsync(ct))
            result.Add(await ToDtoAsync(estate, ct));
        return result;
    }

    public async Task<EstateDto> SetFeatureAsync(
        Guid estateId, string featureKey, bool enabled, string performedBy, CancellationToken ct = default)
    {
        if (!FeatureCatalog.Exists(featureKey))
            throw new ArgumentException($"Unknown feature '{featureKey}'.", nameof(featureKey));

        var estate = await _estates.GetAsync(estateId, ct)
            ?? throw new KeyNotFoundException($"Estate '{estateId}' was not found.");

        if (enabled)
        {
            var current = await _featureService.GetEnabledAsync(estateId, ct);
            var missing = FeatureCatalog.Get(featureKey).Requires.Where(r => !current.Contains(r)).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"'{featureKey}' needs {string.Join(", ", missing)} enabled first.", nameof(featureKey));
        }

        await _features.UpsertAsync(estateId, featureKey, enabled, performedBy, ct);

        await _audit.CreateAsync(new CreateAuditLogDto(
            "EstateFeature", $"{estateId}:{featureKey}", enabled ? "Enabled" : "Disabled", performedBy, EstateId: estateId), ct);

        return await ToDtoAsync(estate, ct);
    }

    private async Task<EstateDto> ToDtoAsync(Estate estate, CancellationToken ct) =>
        new(estate.Id, estate.Name, estate.SchemeType,
            (await _featureService.GetEnabledAsync(estate.Id, ct)).Order(StringComparer.Ordinal).ToList());
}
