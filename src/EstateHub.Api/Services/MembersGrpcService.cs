using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace EstateHub.Api.Services;

[NoOwnershipRequired("Staff manage the members of their own estate: the tenant layer confines ids to it, and RoleGrants decides which roles each caller may touch.")]
[AlwaysOn("Membership is core: every estate has people.")]
public class MembersGrpcService : MembersService.MembersServiceBase
{
    private readonly IMembersService _members;

    public MembersGrpcService(IMembersService members)
    {
        _members = members;
    }

    [RequiresPermission(Permissions.MembersRead)]
    public override async Task<ListMembersResponse> ListMembers(ListMembersRequest request, ServerCallContext context)
    {
        var view = await _members.ListAsync(context.CancellationToken);
        var response = new ListMembersResponse();
        response.Members.AddRange(view.Members.Select(ToProto));
        response.Invitations.AddRange(view.Invitations.Select(ToProto));
        response.GrantableRoles.AddRange(view.GrantableRoles);
        return response;
    }

    [RequiresPermission(Permissions.MembersManage)]
    [RequiresRecentMfa]
    public override async Task<PendingInvitation> Invite(InviteRequest request, ServerCallContext context) =>
        ToProto(await _members.InviteAsync(
            new Application.Interfaces.InviteRequest(request.Name, request.Email, request.Phone, request.Role), context.CancellationToken));

    [RequiresPermission(Permissions.MembersManage)]
    public override async Task<PendingInvitation> ResendInvitation(ResendInvitationRequest request, ServerCallContext context) =>
        ToProto(await _members.ResendAsync(GrpcRequest.RequiredGuid(request.InvitationId, "invitation_id"), context.CancellationToken));

    [RequiresPermission(Permissions.MembersManage)]
    public override async Task<RevokeInvitationResponse> RevokeInvitation(RevokeInvitationRequest request, ServerCallContext context)
    {
        await _members.RevokeAsync(GrpcRequest.RequiredGuid(request.InvitationId, "invitation_id"), context.CancellationToken);
        return new RevokeInvitationResponse();
    }

    [RequiresPermission(Permissions.MembersManage)]
    [RequiresRecentMfa]
    public override async Task<Member> ChangeRole(ChangeRoleRequest request, ServerCallContext context) =>
        ToProto(await _members.ChangeRoleAsync(GrpcRequest.RequiredGuid(request.MembershipId, "membership_id"), request.Role, context.CancellationToken));

    [RequiresPermission(Permissions.MembersManage)]
    [RequiresRecentMfa]
    public override async Task<RemoveMemberResponse> RemoveMember(RemoveMemberRequest request, ServerCallContext context)
    {
        await _members.RemoveAsync(GrpcRequest.RequiredGuid(request.MembershipId, "membership_id"), context.CancellationToken);
        return new RemoveMemberResponse();
    }

    [RequiresPermission(Permissions.MembersManage)]
    [RequiresRecentMfa]
    public override async Task<ResetMemberMfaResponse> ResetMemberMfa(ResetMemberMfaRequest request, ServerCallContext context)
    {
        await _members.ResetMfaAsync(GrpcRequest.RequiredGuid(request.MembershipId, "membership_id"), context.CancellationToken);
        return new ResetMemberMfaResponse();
    }

    private static Member ToProto(MemberDto m) => new()
    {
        MembershipId = m.MembershipId.ToString(),
        UserId = m.UserId.ToString(),
        Name = m.Name,
        Email = m.Email ?? string.Empty,
        Phone = m.Phone ?? string.Empty,
        Role = m.Role,
        Since = Timestamp.FromDateTime(DateTime.SpecifyKind(m.Since, DateTimeKind.Utc)),
        CanManage = m.CanManage,
        MfaEnabled = m.MfaEnabled,
    };

    private static PendingInvitation ToProto(InvitationDto i) => new()
    {
        Id = i.Id.ToString(),
        Name = i.Name,
        Email = i.Email ?? string.Empty,
        Phone = i.Phone ?? string.Empty,
        Role = i.Role,
        ExpiresAt = Timestamp.FromDateTime(DateTime.SpecifyKind(i.ExpiresAt, DateTimeKind.Utc)),
        LastSentAt = Timestamp.FromDateTime(DateTime.SpecifyKind(i.LastSentAt, DateTimeKind.Utc)),
        SendCount = i.SendCount,
    };
}
