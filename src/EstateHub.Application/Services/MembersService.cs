using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

/// <summary>
/// Members of the active estate and invitations (AUTH-01). Who may grant which role is RoleGrants;
/// nobody changes or removes themselves. Every change is audited in the estate's trail.
/// </summary>
public class MembersService : IMembersService
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
    public const int MaxInvitationsPerEstatePerHour = 100;
    public const int MaxSendsPerInvitation = 5;
    public static readonly TimeSpan MinTimeBetweenSends = TimeSpan.FromMinutes(1);

    private readonly IMembersRepository _members;
    private readonly IAuthRepository _users;
    private readonly IEstateRepository _estates;
    private readonly ICurrentUser _user;
    private readonly ITenantContext _tenant;
    private readonly IEmailSender _email;
    private readonly ISmsSender _sms;
    private readonly IAppLinks _links;
    private readonly IAuditLogService _audit;
    private readonly IAuthService _auth;
    private readonly IAlerts _alerts;
    private readonly TimeProvider _clock;

    public MembersService(
        IMembersRepository members,
        IAuthRepository users,
        IEstateRepository estates,
        ICurrentUser user,
        ITenantContext tenant,
        IEmailSender email,
        ISmsSender sms,
        IAppLinks links,
        IAuditLogService audit,
        IAuthService auth,
        IAlerts alerts,
        TimeProvider clock)
    {
        _members = members;
        _users = users;
        _estates = estates;
        _user = user;
        _tenant = tenant;
        _email = email;
        _sms = sms;
        _links = links;
        _audit = audit;
        _auth = auth;
        _alerts = alerts;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task ResetMfaAsync(Guid membershipId, CancellationToken ct = default)
    {
        var row = await OwnedMemberAsync(membershipId, ct);
        if (!row.User.MfaEnabled)
            throw new ArgumentException($"{row.User.DisplayName} has no authenticator set up.");

        await _auth.ResetMfaAsync(row.User.Id, _user.UserId!, ct);
        await AuditAsync("Membership", row.Membership.Id, "MfaReset", $"user={row.User.Id}", ct);
    }

    public async Task ResetAccountMfaAsync(string email, CancellationToken ct = default)
    {
        var user = await _users.FindUserByEmailAsync(ContactPoints.Email(email), ct) ?? throw new KeyNotFoundException("No account uses that email.");
        if (user.Id == ActorId)
            throw new ActionNotAllowedException("You can't reset your own authenticator. Ask another super admin, or use a recovery code.");
        if (!user.MfaEnabled)
            throw new ArgumentException("That account has no authenticator set up.");

        await _auth.ResetMfaAsync(user.Id, _user.UserId!, ct);
    }

    private Guid ActorId => Guid.Parse(_user.UserId ?? throw new InvalidOperationException("No caller."));

    public async Task<MembersView> ListAsync(CancellationToken ct = default)
    {
        var actor = _user.UserId;
        var members = (await _members.ListMembersAsync(ct))
            .Select(row => ToDto(row, canManage: row.User.Id.ToString() != actor && RoleGrants.CanGrant(_user.Role, row.Membership.Role)))
            .ToList();
        var invitations = (await _members.ListPendingInvitationsAsync(Now, ct)).Select(ToDto).ToList();
        return new MembersView(members, invitations, RoleGrants.GrantableBy(_user.Role));
    }

    public Task<InvitationDto> InviteAsync(InviteRequest request, CancellationToken ct = default) =>
        CreateInvitationAsync(_tenant.EstateId ?? throw new InvalidOperationException("No active estate."), _user.Role, request, ct);

    public async Task<InvitationDto> InviteToEstateAsync(Guid estateId, InviteRequest request, CancellationToken ct = default)
    {
        if (await _estates.GetAsync(estateId, ct) is null)
            throw new KeyNotFoundException("Estate not found.");

        // Platform call: open the estate for this scope so the invitation is written (and checked) inside it.
        _tenant.Use(estateId);
        return await CreateInvitationAsync(estateId, Roles.SuperAdmin, request, ct);
    }

    public async Task<InvitationDto> ResendAsync(Guid invitationId, CancellationToken ct = default)
    {
        var invitation = await OpenInvitationAsync(invitationId, ct);
        EnsureMayGrant(invitation.Role);
        if (invitation.SendCount >= MaxSendsPerInvitation)
            throw new RateLimitedException($"This invitation has been sent {MaxSendsPerInvitation} times. Revoke it and invite again.");
        if (Now - invitation.LastSentAt < MinTimeBetweenSends)
            throw new RateLimitedException("It was sent less than a minute ago. Please wait a moment.");

        // A new token: the previous link stops working.
        var token = AuthSecrets.NewToken();
        invitation.TokenHash = AuthSecrets.Hash(token);
        invitation.ExpiresAt = Now + InvitationLifetime;
        invitation.SendCount++;
        invitation.LastSentAt = Now;
        await _members.SaveChangesAsync(ct);

        await SendAsync(invitation, token, ct);
        await AuditAsync("Invitation", invitation.Id, "Resent", $"send={invitation.SendCount}", ct);
        return ToDto(invitation);
    }

    public async Task RevokeAsync(Guid invitationId, CancellationToken ct = default)
    {
        var invitation = await OpenInvitationAsync(invitationId, ct);
        EnsureMayGrant(invitation.Role);

        invitation.RevokedAt = Now;
        await _members.SaveChangesAsync(ct);
        await AuditAsync("Invitation", invitation.Id, "Revoked", $"role={invitation.Role}", ct);
    }

    public async Task<MemberDto> ChangeRoleAsync(Guid membershipId, string role, CancellationToken ct = default)
    {
        var row = await OwnedMemberAsync(membershipId, ct);
        role = (role ?? string.Empty).Trim();
        if (!RolePermissions.IsKnownRole(role))
            throw new ArgumentException($"Unknown role '{role}'.");
        EnsureMayGrant(role);

        var previous = row.Membership.Role;
        row.Membership.Role = role;
        await _members.SaveChangesAsync(ct);

        // The member's current token now names the old role, so their next call fails the session check
        // and the app refreshes into the new role (within ~30 s, SECURITY.md layer 4).
        await AuditAsync("Membership", row.Membership.Id, "RoleChanged", $"user={row.User.Id} from={previous} to={role}", ct);
        if (Roles.IsStaff(role) && !Roles.IsStaff(previous))
            await _alerts.RaiseAsync("New staff member", $"{ContactPoints.Mask(row.User.Contact)} was made {role}.", ct);
        return ToDto(row, canManage: true);
    }

    public async Task RemoveAsync(Guid membershipId, CancellationToken ct = default)
    {
        var row = await OwnedMemberAsync(membershipId, ct);

        row.Membership.IsActive = false;
        await _members.SaveChangesAsync(ct);
        await AuditAsync("Membership", row.Membership.Id, "Removed", $"user={row.User.Id} role={row.Membership.Role}", ct);
    }

    private async Task<InvitationDto> CreateInvitationAsync(Guid estateId, string? actorRole, InviteRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            throw new ArgumentException("Name is required.");
        if (name.Length > 200)
            throw new ArgumentException("Name must be at most 200 characters.");

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : ContactPoints.Email(request.Email);
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : ContactPoints.SaMobile(request.Phone);
        if (email is null && phone is null)
            throw new ArgumentException("Enter an email address or a mobile number.");

        var role = (request.Role ?? string.Empty).Trim();
        if (!RolePermissions.IsKnownRole(role))
            throw new ArgumentException($"Unknown role '{role}'.");
        if (!RoleGrants.CanGrant(actorRole, role))
            throw new ActionNotAllowedException($"You can't give the {role} role.");

        // Matched on the address the invitation will go to (email first), as acceptance is.
        var existing = email is not null ? await _users.FindUserByEmailAsync(email, ct) : await _users.FindUserByPhoneAsync(phone!, ct);
        if (existing is not null && await _members.FindMembershipAsync(existing.Id, ct) is { IsActive: true })
            throw new ArgumentException($"{email ?? phone} is already a member of this estate. Change their role instead.");
        if (await _members.HasPendingInvitationAsync(email, phone, Now, ct))
            throw new ArgumentException($"There's already a pending invitation for {email ?? phone}. Resend it instead.");
        if (await _members.CountInvitationsSentSinceAsync(Now.AddHours(-1), ct) >= MaxInvitationsPerEstatePerHour)
            throw new RateLimitedException("This estate has sent a lot of invitations in the last hour. Please try again later.");

        var token = AuthSecrets.NewToken();
        var invitation = new Invitation
        {
            EstateId = estateId,
            Name = name,
            Email = email,
            Phone = phone,
            Role = role,
            TokenHash = AuthSecrets.Hash(token),
            ExpiresAt = Now + InvitationLifetime,
            InvitedByUserId = ActorId,
            PersonId = request.PersonId,
            SendCount = 1,
            LastSentAt = Now,
        };
        _members.Add(invitation);
        await _members.SaveChangesAsync(ct);

        await SendAsync(invitation, token, ct);
        await AuditAsync("Invitation", invitation.Id, "Invited", $"role={role} by={(email is not null ? "email" : "sms")} person={request.PersonId}", ct); // ids only (POPIA)
        return ToDto(invitation);
    }

    public async Task<int> InvitationAllowanceAsync(CancellationToken ct = default) =>
        Math.Max(0, MaxInvitationsPerEstatePerHour - await _members.CountInvitationsSentSinceAsync(Now.AddHours(-1), ct));

    private async Task SendAsync(Invitation invitation, string token, CancellationToken ct)
    {
        var estate = await _estates.GetAsync(invitation.EstateId, ct);
        var link = _links.Invitation(token);
        var role = invitation.Role.Replace('_', ' ');

        if (invitation.Email is { } email)
        {
            await _email.SendAsync(
                email,
                $"You're invited to {estate?.Name} on EstateHub",
                $"Hi {invitation.Name},\n\nYou've been invited to join {estate?.Name} on EstateHub as {role}.\n\n" +
                $"Open this link to accept (valid for {InvitationLifetime.TotalDays:0} days):\n{link}\n\n" +
                "We'll send a code to this email address to confirm it's you. If you weren't expecting this, you can ignore it.",
                ct);
        }
        else
        {
            await _sms.SendAsync(invitation.Phone!, $"EstateHub: you're invited to {estate?.Name} as {role}. Accept within 7 days: {link}", ct);
        }
    }

    private async Task<Invitation> OpenInvitationAsync(Guid id, CancellationToken ct)
    {
        var invitation = await _members.GetInvitationAsync(id, ct);
        if (invitation is null || invitation.AcceptedAt is not null || invitation.RevokedAt is not null)
            throw new KeyNotFoundException("Invitation not found.");
        return invitation;
    }

    /// <summary>A member the caller may manage: never themselves, never a role above what they can grant.</summary>
    private async Task<MemberRow> OwnedMemberAsync(Guid membershipId, CancellationToken ct)
    {
        var row = await _members.GetMemberAsync(membershipId, ct) ?? throw new KeyNotFoundException("Member not found.");
        if (row.User.Id == ActorId)
            throw new ActionNotAllowedException("You can't change your own role or remove yourself. Ask another manager.");
        EnsureMayGrant(row.Membership.Role);
        return row;
    }

    private void EnsureMayGrant(string role)
    {
        if (!RoleGrants.CanGrant(_user.Role, role))
            throw new ActionNotAllowedException($"You can't manage the {role} role.");
    }

    private Task AuditAsync(string entityType, Guid entityId, string action, string details, CancellationToken ct) =>
        _audit.CreateAsync(new CreateAuditLogDto(entityType, entityId.ToString(), action, _user.UserId!, details), ct);

    private static MemberDto ToDto(MemberRow row, bool canManage) => new(
        row.Membership.Id, row.User.Id, row.User.DisplayName, row.User.Email, row.User.Phone, row.Membership.Role, row.Membership.CreatedAt, canManage,
        row.User.MfaEnabled);

    private static InvitationDto ToDto(Invitation i) => new(i.Id, i.Name, i.Email, i.Phone, i.Role, i.ExpiresAt, i.LastSentAt, i.SendCount);
}
