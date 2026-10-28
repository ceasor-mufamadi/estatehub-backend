using EstateHub.Domain.Entities;

namespace EstateHub.Application.Auth;

/// <summary>
/// The one rule for what a session's access token may carry, used when issuing tokens and again on
/// every request (ISessionValidator), so a removed membership or role change bites within ~30 s.
/// </summary>
public static class AuthScopes
{
    /// <summary>
    /// The role the user may act as in <paramref name="estateId"/> (null = platform level), or null
    /// when that scope isn't allowed:
    /// - an estate needs an active membership there;
    /// - staff roles (and super admin) only count when the session completed MFA (AUTH-03): a staff
    ///   member can't reach any estate without MFA;
    /// - platform level is super admin for platform operators, otherwise "no role" (estate picker only).
    /// </summary>
    public static string? RoleFor(User user, IReadOnlySet<string> authMethods, Guid? estateId, IReadOnlyList<UserMembership> memberships)
    {
        if (user.Status != UserStatus.Active)
            return null;

        var mfa = authMethods.Contains(TokenClaims.MfaMethod);

        if (estateId is null)
            return user.IsSuperAdmin && mfa ? Roles.SuperAdmin : Roles.None;

        var membership = memberships.FirstOrDefault(m => m.EstateId == estateId);
        if (membership is null || (Roles.IsStaff(membership.Role) && !mfa))
            return null;

        return membership.Role;
    }

    /// <summary>
    /// <see cref="RoleFor"/> for a whole session: during support access (AUTH-13) the session's
    /// impersonated estate gives the read-only support role, while the super admin is still a super
    /// admin with MFA and the 30 minutes haven't run out. Otherwise the usual rule.
    /// </summary>
    public static string? RoleForSession(
        User user, AuthSession session, Guid? estateId, IReadOnlyList<UserMembership> memberships, DateTime now)
    {
        var methods = ParseMethods(session.AuthMethods);
        if (session.ImpersonationEstateId is { } impersonated && estateId == impersonated)
        {
            var allowed = user is { IsSuperAdmin: true, Status: UserStatus.Active }
                          && methods.Contains(TokenClaims.MfaMethod)
                          && session.ImpersonationEndsAt > now;
            return allowed ? Roles.Support : null;
        }

        return RoleFor(user, methods, estateId, memberships);
    }

    /// <summary>
    /// Where a fresh sign-in lands: platform level for super admins and for users with several (or no)
    /// estates (they pick one), straight into the estate when there is exactly one.
    /// </summary>
    public static Guid? DefaultEstate(User user, IReadOnlyList<UserMembership> memberships) =>
        !user.IsSuperAdmin && memberships.Count == 1 ? memberships[0].EstateId : null;

    /// <summary>Staff sign in with MFA and get 12-hour sessions; everyone else gets 30-day sliding ones.</summary>
    public static bool IsStaff(User user, IReadOnlyList<UserMembership> memberships) =>
        user.IsSuperAdmin || memberships.Any(m => Roles.IsStaff(m.Role));

    public static IReadOnlySet<string> ParseMethods(string authMethods) =>
        authMethods.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
}
