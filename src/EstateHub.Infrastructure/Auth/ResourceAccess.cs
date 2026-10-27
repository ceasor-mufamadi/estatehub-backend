using EstateHub.Application.Auth;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// Ownership rules. Only ResourceKind.User exists in Sprint 1; Unit (owns/occupies the unit)
/// arrives with units in Sprint 3. Estate scoping is not done here: the tenant layer already
/// confines every query to the token's estate.
/// </summary>
public sealed class ResourceAccess : IResourceAccess
{
    private readonly ICurrentUser _user;

    public ResourceAccess(ICurrentUser user)
    {
        _user = user;
    }

    public bool IsUnrestricted => _user.HasPermission(Permissions.Unrestricted);

    public Task<bool> CanAccessAsync(ResourceKind kind, string id, CancellationToken ct = default)
    {
        if (!_user.IsAuthenticated || string.IsNullOrWhiteSpace(id))
            return Task.FromResult(false);
        if (IsUnrestricted)
            return Task.FromResult(true);

        return Task.FromResult(kind switch
        {
            ResourceKind.User => string.Equals(id.Trim(), _user.UserId, StringComparison.Ordinal),
            _ => false,
        });
    }
}
