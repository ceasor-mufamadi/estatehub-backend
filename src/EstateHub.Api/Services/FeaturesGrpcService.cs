using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace EstateHub.Api.Services;

[Authorize]
[PlatformLevel]
[AccessScopedInAction("Returns only the caller's own context from their token.")]
[AlwaysOn("The app needs this to render at all.")]
public class FeaturesGrpcService : FeaturesService.FeaturesServiceBase
{
    private readonly ICurrentUser _user;
    private readonly IEstateService _estates;
    private readonly IFeatureService _features;

    public FeaturesGrpcService(ICurrentUser user, IEstateService estates, IFeatureService features)
    {
        _user = user;
        _estates = estates;
        _features = features;
    }

    public override async Task<GetMyContextResponse> GetMyContext(GetMyContextRequest request, ServerCallContext context)
    {
        var response = new GetMyContextResponse
        {
            UserId = _user.UserId ?? string.Empty,
            Role = _user.Role ?? string.Empty,
        };
        response.Permissions.AddRange(_user.Permissions.Order(StringComparer.Ordinal));

        if (_user.EstateId is { } estateId)
        {
            var estate = await _estates.GetAsync(estateId, context.CancellationToken)
                ?? throw GrpcRequest.NotFound("Estate not found.");

            response.EstateId = estate.Id.ToString();
            response.EstateName = estate.Name;
            response.EnabledFeatures.AddRange(
                (await _features.GetEnabledAsync(estateId, context.CancellationToken)).Order(StringComparer.Ordinal));
        }

        return response;
    }
}
