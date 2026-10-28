namespace EstateHub.Application.Auth;

/// <summary>The caller of the current request, from the validated EstateHub access token.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>JWT "sub".</summary>
    string? UserId { get; }

    /// <summary>JWT "sid": the session this token belongs to.</summary>
    Guid? SessionId { get; }

    /// <summary>JWT "estate_id": the estate this token is scoped to. Null for platform-level tokens.</summary>
    Guid? EstateId { get; }

    /// <summary>JWT "role": the caller's role in <see cref="EstateId"/>.</summary>
    string? Role { get; }

    IReadOnlySet<string> Permissions { get; }

    /// <summary>JWT "amr": how the user authenticated (otp, pwd, mfa).</summary>
    IReadOnlySet<string> AuthMethods { get; }

    /// <summary>JWT "auth_time": when the user last actively authenticated (drives step-up MFA).</summary>
    DateTimeOffset? AuthTime { get; }

    bool HasPermission(string permission);
}

/// <summary>Record types an ownership check can be declared for (see <see cref="IResourceAccess"/>).</summary>
public enum ResourceKind
{
    /// <summary>A user id: the caller themselves.</summary>
    User,

    /// <summary>
    /// A unit id: staff (estate read) see every unit of their estate; residents only units they own or
    /// live in today. Privacy between neighbours (SECURITY.md layer 4).
    /// </summary>
    Unit,

    /// <summary>
    /// A billing account id: staff with billing read see every account of their estate; residents only
    /// accounts they hold (current or former owner).
    /// </summary>
    Account,
}

/// <summary>Answers "may the current caller see/change this record?" for ownership checks.</summary>
public interface IResourceAccess
{
    /// <summary>True for callers holding the unrestricted permission (super admin).</summary>
    bool IsUnrestricted { get; }

    Task<bool> CanAccessAsync(ResourceKind kind, string id, CancellationToken ct = default);
}
