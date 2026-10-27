using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using Grpc.Core;

namespace EstateHub.Api.Services;

[NoOwnershipRequired("Estate-wide structure; the tenant layer confines it to the caller's estate.")]
[AlwaysOn("Core estate structure.")]
public class EstateGrpcService : EstateService.EstateServiceBase
{
    private readonly IBlockService _blocks;
    private readonly ICurrentUser _user;

    public EstateGrpcService(IBlockService blocks, ICurrentUser user)
    {
        _blocks = blocks;
        _user = user;
    }

    [RequiresPermission(Permissions.EstateWrite)]
    public override async Task<BlockEntry> CreateBlock(CreateBlockRequest request, ServerCallContext context) =>
        ToEntry(await _blocks.CreateAsync(request.Name, _user.UserId!, context.CancellationToken));

    [RequiresPermission(Permissions.EstateRead)]
    public override async Task<ListBlocksResponse> ListBlocks(ListBlocksRequest request, ServerCallContext context)
    {
        var response = new ListBlocksResponse();
        response.Items.AddRange((await _blocks.ListAsync(context.CancellationToken)).Select(ToEntry));
        return response;
    }

    private static BlockEntry ToEntry(BlockDto dto) => new() { Id = dto.Id.ToString(), Name = dto.Name };
}
