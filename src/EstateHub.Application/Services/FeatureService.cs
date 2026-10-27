using EstateHub.Application.Features;
using EstateHub.Application.Interfaces;

namespace EstateHub.Application.Services;

public class FeatureService : IFeatureService
{
    private readonly IFeatureRepository _repository;

    public FeatureService(IFeatureRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlySet<string>> GetEnabledAsync(Guid estateId, CancellationToken ct = default) =>
        FeatureCatalog.Resolve(await _repository.GetOverridesAsync(estateId, ct));

    public async Task<bool> IsEnabledAsync(Guid estateId, string featureKey, CancellationToken ct = default) =>
        (await GetEnabledAsync(estateId, ct)).Contains(featureKey);
}
