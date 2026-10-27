using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using Grpc.Core;

namespace EstateHub.Api.Services;

[RequiresPermission(Permissions.PlatformEstatesManage)]
[PlatformLevel]
[NoOwnershipRequired("Platform administration across estates (super admin only).")]
[AlwaysOn("Platform administration.")]
public class PlatformGrpcService : PlatformService.PlatformServiceBase
{
    private readonly IEstateService _estates;
    private readonly ICurrentUser _user;

    public PlatformGrpcService(IEstateService estates, ICurrentUser user)
    {
        _estates = estates;
        _user = user;
    }

    public override async Task<EstateEntry> CreateEstate(CreateEstateRequest request, ServerCallContext context) =>
        ToEntry(await _estates.CreateAsync(request.Name, request.SchemeType, _user.UserId!, context.CancellationToken));

    public override async Task<ListEstatesResponse> ListEstates(ListEstatesRequest request, ServerCallContext context)
    {
        var response = new ListEstatesResponse();
        response.Items.AddRange((await _estates.ListAsync(context.CancellationToken)).Select(ToEntry));
        return response;
    }

    [RequiresRecentMfa]
    public override async Task<EstateEntry> SetEstateFeature(SetEstateFeatureRequest request, ServerCallContext context)
    {
        var estateId = GrpcRequest.RequiredGuid(request.EstateId, "estate_id");
        return ToEntry(await _estates.SetFeatureAsync(
            estateId, request.FeatureKey, request.Enabled, _user.UserId!, context.CancellationToken));
    }

    private static EstateEntry ToEntry(EstateDto dto)
    {
        var entry = new EstateEntry { Id = dto.Id.ToString(), Name = dto.Name, SchemeType = dto.SchemeType };
        entry.EnabledFeatures.AddRange(dto.EnabledFeatures);
        return entry;
    }
}
