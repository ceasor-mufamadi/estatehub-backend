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

    /// <summary>Read-only support access to one estate (AUTH-13).</summary>
    public const string PlatformImpersonate = "platform.impersonate";

    public const string AuditLogRead = "estatehub.audit_log.read";

    public const string EstateRead = "estatehub.estate.read";
    public const string EstateWrite = "estatehub.estate.write";

    public const string MembersRead = "estatehub.members.read";

    /// <summary>Invite, change roles, remove. Which roles may be granted is narrowed by RoleGrants.</summary>
    public const string MembersManage = "estatehub.members.manage";

    /// <summary>The estate's people (owners, tenants) and who owns or occupies which unit. Personal information.</summary>
    public const string PeopleRead = "estatehub.people.read";
    public const string PeopleWrite = "estatehub.people.write";

    /// <summary>Import units, owners and tenants from a spreadsheet, and send their invitations (slice 3.3).</summary>
    public const string ImportsRun = "estatehub.imports.run";

    /// <summary>Billing (Sprint 4): accounts and the ledger, settings, charge types, runs.</summary>
    public const string BillingRead = "estatehub.billing.read";
    public const string BillingWrite = "estatehub.billing.write";

    /// <summary>Post a billing run (needs a fresh MFA, AUTH-12).</summary>
    public const string BillingPost = "estatehub.billing.post";

    /// <summary>Trustees: approve special levies and write-offs (four-eyes).</summary>
    public const string BillingApprove = "estatehub.billing.approve";

    /// <summary>Pair and revoke guard tablets (AUTH-14).</summary>
    public const string DevicesManage = "estatehub.devices.manage";
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

    /// <summary>A super admin's read-only support access to one estate (AUTH-13). Never granted: only StartImpersonation gives it.</summary>
    public const string Support = "support";

    /// <summary>Signed in but not acting in any estate (estate picker only): no permissions.</summary>
    public const string None = "";

    private static readonly HashSet<string> StaffRoles = new(StringComparer.Ordinal) { SuperAdmin, ManagingAgent, Trustee, Manager };

    /// <summary>Roles that can see or change a whole estate: they must use MFA (AUTH-03) and get 12-hour sessions.</summary>
    public static bool IsStaff(string role) => StaffRoles.Contains(role);
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
        [Roles.SuperAdmin] = [Permissions.Unrestricted, Permissions.PlatformEstatesManage, Permissions.PlatformImpersonate],
        [Roles.ManagingAgent] = [Permissions.AuditLogRead, Permissions.EstateRead, Permissions.EstateWrite, Permissions.MembersRead, Permissions.MembersManage, Permissions.DevicesManage, Permissions.PeopleRead, Permissions.PeopleWrite, Permissions.ImportsRun, Permissions.BillingRead, Permissions.BillingWrite, Permissions.BillingPost],
        [Roles.Manager] = [Permissions.AuditLogRead, Permissions.EstateRead, Permissions.EstateWrite, Permissions.MembersRead, Permissions.MembersManage, Permissions.DevicesManage, Permissions.PeopleRead, Permissions.PeopleWrite, Permissions.ImportsRun, Permissions.BillingRead, Permissions.BillingWrite, Permissions.BillingPost],
        [Roles.Trustee] = [Permissions.AuditLogRead, Permissions.EstateRead, Permissions.MembersRead, Permissions.PeopleRead, Permissions.BillingRead, Permissions.BillingApprove],
        // Residents see only their own units (ResourceKind.Unit checks, Sprint 3), never the estate's
        // unit list, other units or the activity log: privacy between neighbours (POPIA).
        [Roles.Owner] = [],
        [Roles.Tenant] = [],
        // Guards need unit numbers and blocks at the gate (is there a unit 12?), never owners' details.
        [Roles.Guard] = [Permissions.EstateRead],
        [Roles.Contractor] = [],
        // Read-only: the same reads as a trustee, nothing that writes (AUTH-13 "read-only by default").
        [Roles.Support] = [Permissions.AuditLogRead, Permissions.EstateRead, Permissions.MembersRead, Permissions.PeopleRead, Permissions.BillingRead],
    };

    public static IReadOnlyCollection<string> AllRoles => Map.Keys;

    public static bool IsKnownRole(string role) => Map.ContainsKey(role);

    public static IReadOnlyList<string> For(string role) =>
        role == Roles.None ? [] :
        Map.TryGetValue(role, out var permissions)
            ? permissions
            : throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
}
