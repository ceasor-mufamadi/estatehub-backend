using EstateHub.Domain.Entities;

namespace EstateHub.Application.Interfaces;

/// <summary>People in the active estate: members, pending invitations, role changes (PHASE1.md §2a MembersService).</summary>
public interface IMembersService
{
    Task<MembersView> ListAsync(CancellationToken ct = default);

    /// <summary>Invite into the active estate, as the caller's role allows (RoleGrants).</summary>
    Task<InvitationDto> InviteAsync(InviteRequest request, CancellationToken ct = default);

    /// <summary>Super admin, from the platform: the first managing agent, manager or trustee of an estate.</summary>
    Task<InvitationDto> InviteToEstateAsync(Guid estateId, InviteRequest request, CancellationToken ct = default);

    /// <summary>Sends a fresh link (the old one stops working) and extends it by 7 days.</summary>
    Task<InvitationDto> ResendAsync(Guid invitationId, CancellationToken ct = default);

    Task RevokeAsync(Guid invitationId, CancellationToken ct = default);

    Task<MemberDto> ChangeRoleAsync(Guid membershipId, string role, CancellationToken ct = default);

    Task RemoveAsync(Guid membershipId, CancellationToken ct = default);

    /// <summary>Lost authenticator: reset a member's MFA (only for roles the caller may grant, never themselves).</summary>
    Task ResetMfaAsync(Guid membershipId, CancellationToken ct = default);

    /// <summary>Super admin, from the platform: reset any account's MFA by email (e.g. a managing agent's), never their own.</summary>
    Task ResetAccountMfaAsync(string email, CancellationToken ct = default);

    /// <summary>How many more invitations the active estate may send in the current hour (the hourly cap).</summary>
    Task<int> InvitationAllowanceAsync(CancellationToken ct = default);
}

/// <summary><paramref name="PersonId"/>: the estate's person record this is for (accepting links it to the account).</summary>
public record InviteRequest(string Name, string? Email, string? Phone, string Role, Guid? PersonId = null);

public record MemberDto(Guid MembershipId, Guid UserId, string Name, string? Email, string? Phone, string Role, DateTime Since, bool CanManage, bool MfaEnabled);

public record InvitationDto(Guid Id, string Name, string? Email, string? Phone, string Role, DateTime ExpiresAt, DateTime LastSentAt, int SendCount);

public record MembersView(IReadOnlyList<MemberDto> Members, IReadOnlyList<InvitationDto> Invitations, IReadOnlyList<string> GrantableRoles);

/// <summary>A member row with the user's details (users aren't tenant-scoped; memberships are).</summary>
public record MemberRow(Membership Membership, User User);

public interface IMembersRepository
{
    /// <summary>Active memberships of the active estate, with their users.</summary>
    Task<IReadOnlyList<MemberRow>> ListMembersAsync(CancellationToken ct = default);
    Task<MemberRow?> GetMemberAsync(Guid membershipId, CancellationToken ct = default);
    Task<Membership?> FindMembershipAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Invitation>> ListPendingInvitationsAsync(DateTime now, CancellationToken ct = default);
    Task<Invitation?> GetInvitationAsync(Guid id, CancellationToken ct = default);
    Task<bool> HasPendingInvitationAsync(string? email, string? phone, DateTime now, CancellationToken ct = default);
    Task<int> CountInvitationsSentSinceAsync(DateTime since, CancellationToken ct = default);
    Task<IReadOnlyList<Invitation>> InvitationsForPersonAsync(Guid personId, CancellationToken ct = default);
    void Add(Invitation invitation);
    Task SaveChangesAsync(CancellationToken ct = default);
}
