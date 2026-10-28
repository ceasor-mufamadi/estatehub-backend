namespace EstateHub.Application.Auth;

/// <summary>
/// Background work done on someone's behalf (e.g. an import's invitations): set <see cref="User"/> at
/// the start of the scope, before anything resolves <see cref="ICurrentUser"/>. Requests never set it.
/// </summary>
public sealed class CallerOverride
{
    public ICurrentUser? User { get; set; }
}

/// <summary>A fixed caller: who, in which estate, with which role (permissions follow the role).</summary>
public sealed class SnapshotCurrentUser : ICurrentUser
{
    public static readonly SnapshotCurrentUser Nobody = new(null, null, Roles.None);

    public SnapshotCurrentUser(string? userId, Guid? estateId, string role)
    {
        UserId = userId;
        EstateId = estateId;
        Role = role;
        Permissions = RolePermissions.For(role).ToHashSet(StringComparer.Ordinal);
    }

    public bool IsAuthenticated => UserId is not null;
    public string? UserId { get; }
    public Guid? SessionId => null;
    public Guid? EstateId { get; }
    public string? Role { get; }
    public IReadOnlySet<string> Permissions { get; }
    public IReadOnlySet<string> AuthMethods { get; } = new HashSet<string>();
    public DateTimeOffset? AuthTime => null;
    public bool HasPermission(string permission) => Permissions.Contains(permission);
}
