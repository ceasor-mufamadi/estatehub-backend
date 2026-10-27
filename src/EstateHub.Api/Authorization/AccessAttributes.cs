using EstateHub.Application.Auth;
using Microsoft.AspNetCore.Authorization;

namespace EstateHub.Api.Authorization;

// Every RPC must declare ALL of these decisions (method or service class; class-level applies to
// every RPC in it). AuthorizationCoverageTests fails the build when one is missing. There is no
// "decide later" escape hatch (SECURITY.md layer 4).
//   1. permission: [RequiresPermission("estatehub.<module>.<read|write>")], [Authorize] or [AllowAnonymous]
//   2. ownership:  [RequiresAccess(...)], [AccessScopedInAction("why")] or [NoOwnershipRequired("why")]
//   3. feature:    [RequiresFeature("key")] or [AlwaysOn("why")]
// Optional: [RequiresRecentMfa] (step-up, AUTH-12) and [PlatformLevel] (may run without an estate).

/// <summary>Requires a permission (a "perm" claim in the caller's token).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public RequiresPermissionAttribute(string permission)
    {
        Permission = permission;
        Policy = PolicyPrefix + permission;
    }

    public string Permission { get; }
}

/// <summary>
/// The caller must own the record identified by a request field (proto field name, e.g.
/// <c>[RequiresAccess(ResourceKind.User, "user_id")]</c>). Checked by OwnershipInterceptor
/// before the RPC runs; a failed check returns NOT_FOUND (never confirms the record exists).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresAccessAttribute(ResourceKind kind, string field) : Attribute
{
    public ResourceKind Kind { get; } = kind;
    public string Field { get; } = field;

    /// <summary>Skip (rather than deny) when the field is empty, for RPCs where empty means "no filter".</summary>
    public bool Optional { get; init; }
}

/// <summary>Ownership is enforced inside the RPC itself (e.g. results narrowed to the caller). The reason is for reviewers.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AccessScopedInActionAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>The RPC exposes no caller-owned data (estate-wide data for staff, login, keys). The reason is for reviewers.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoOwnershipRequiredAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>The RPC belongs to a feature an estate can switch off (FeatureCatalog). Off → FAILED_PRECONDITION.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresFeatureAttribute(string featureKey) : Attribute
{
    public string FeatureKey { get; } = featureKey;
}

/// <summary>The RPC is part of the core product and can't be switched off. The reason is for reviewers.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AlwaysOnAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>Step-up (AUTH-12): the caller must have completed MFA within the last 15 minutes.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequiresRecentMfaAttribute : Attribute
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The RPC may run without an active estate (login, keys, super admin). Without this attribute a
/// token with no estate_id is refused with FAILED_PRECONDITION.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class PlatformLevelAttribute : Attribute;
