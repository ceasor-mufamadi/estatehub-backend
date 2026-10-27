namespace EstateHub.Application.Auth;

/// <summary>
/// Permission names carried in the token's "perm" claim. Estate permissions are
/// estatehub.&lt;module&gt;.&lt;read|write&gt; (plus named actions where read/write is too coarse);
/// platform permissions are platform.*. Add new ones here and grant them in <see cref="RolePermissions"/>.
/// </summary>
public static class Permissions
{
    /// <summary>Passes every permission and ownership check. Super admin only.</summary>
    public const string Unrestricted = "platform.access.all";

    public const string PlatformEstatesManage = "platform.estates.manage";

    public const string AuditLogRead = "estatehub.audit_log.read";

    public const string EstateRead = "estatehub.estate.read";
    public const string EstateWrite = "estatehub.estate.write";
}

public static class Roles
{
    public const string SuperAdmin = "super_admin";
    public const string ManagingAgent = "managing_agent";
    public const string Trustee = "trustee";
    public const string Manager = "manager";
    public const string Owner = "owner";
    public const string Tenant = "tenant";
    public const string Guard = "guard";
    public const string Contractor = "contractor";
}

/// <summary>
/// Role → permission sets, defined in code for Phase 1 (PHASE1.md §2a) so they are reviewed and
/// tested like any other code. Per-estate custom roles can replace this later without changing
/// the token format.
/// </summary>
public static class RolePermissions
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [Roles.SuperAdmin] = [Permissions.Unrestricted, Permissions.PlatformEstatesManage],
        [Roles.ManagingAgent] = [Permissions.AuditLogRead, Permissions.EstateRead, Permissions.EstateWrite],
        [Roles.Manager] = [Permissions.AuditLogRead, Permissions.EstateRead, Permissions.EstateWrite],
        [Roles.Trustee] = [Permissions.AuditLogRead, Permissions.EstateRead],
        [Roles.Owner] = [Permissions.EstateRead],
        [Roles.Tenant] = [Permissions.EstateRead],
        [Roles.Guard] = [Permissions.EstateRead],
        [Roles.Contractor] = [],
    };

    public static IReadOnlyCollection<string> AllRoles => Map.Keys;

    public static bool IsKnownRole(string role) => Map.ContainsKey(role);

    public static IReadOnlyList<string> For(string role) =>
        Map.TryGetValue(role, out var permissions)
            ? permissions
            : throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
}
