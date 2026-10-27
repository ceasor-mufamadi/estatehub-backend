using EstateHub.Application.Auth;
using Microsoft.AspNetCore.Authorization;

namespace EstateHub.Api.Authorization;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Builds a policy for any "perm:&lt;name&gt;" on demand, so permissions never need registering in
/// Program.cs. Owns the fallback policy too: an RPC with no attribute at all still requires a
/// signed-in caller (deny by default). Unlike ETQA there is no Enforce switch (AUTH-17).
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private static readonly AuthorizationPolicy SignedIn = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(RequiresPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
            return _fallback.GetPolicyAsync(policyName);

        var permission = policyName[RequiresPermissionAttribute.PolicyPrefix.Length..];
        return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build());
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(SignedIn);
}

/// <summary>Succeeds when the token carries the permission, or the unrestricted (super admin) one.</summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly ILogger<PermissionHandler> _logger;

    public PermissionHandler(ILogger<PermissionHandler> logger)
    {
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var granted = context.User.FindAll(TokenClaims.Permission).Select(c => c.Value)
            .Any(p => string.Equals(p, requirement.Permission, StringComparison.Ordinal)
                   || string.Equals(p, Permissions.Unrestricted, StringComparison.Ordinal));

        if (granted)
            context.Succeed(requirement);
        else
            _logger.LogInformation("Denied {Permission} for {User}", requirement.Permission,
                context.User.FindFirst(TokenClaims.Subject)?.Value ?? "anonymous");

        return Task.CompletedTask;
    }
}
