using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// Ownership rules for [RequiresAccess]. Estate scoping is not done here: the tenant layer already
/// confines every query to the token's estate.
/// - User: the caller themselves.
/// - Unit: staff (estate read) see every unit; residents only units they own or live in today
///   (SECURITY.md layer 4: privacy between neighbours).
/// </summary>
public sealed class ResourceAccess : IResourceAccess
{
    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    private readonly ICurrentUser _user;
    private readonly EstateHubDbContext _db;
    private readonly TimeProvider _clock;

    public ResourceAccess(ICurrentUser user, EstateHubDbContext db, TimeProvider clock)
    {
        _user = user;
        _db = db;
        _clock = clock;
    }

    public bool IsUnrestricted => _user.HasPermission(Permissions.Unrestricted);

    public async Task<bool> CanAccessAsync(ResourceKind kind, string id, CancellationToken ct = default)
    {
        if (!_user.IsAuthenticated || string.IsNullOrWhiteSpace(id))
            return false;
        if (IsUnrestricted)
            return true;

        return kind switch
        {
            ResourceKind.User => string.Equals(id.Trim(), _user.UserId, StringComparison.Ordinal),
            ResourceKind.Unit => Guid.TryParse(id, out var unitId) && await CanSeeUnitAsync(unitId, ct),
            _ => false,
        };
    }

    private async Task<bool> CanSeeUnitAsync(Guid unitId, CancellationToken ct)
    {
        if (_user.HasPermission(Permissions.EstateRead))
            return await _db.Units.AnyAsync(u => u.Id == unitId, ct);
        if (!Guid.TryParse(_user.UserId, out var userId))
            return false;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, SouthAfrica));
        var mine = _db.People.Where(p => p.UserId == userId).Select(p => p.Id);
        return await _db.Ownerships.AnyAsync(o => o.UnitId == unitId && mine.Contains(o.PersonId) && o.FromDate <= today && (o.ToDate == null || o.ToDate >= today), ct)
               || await _db.Occupancies.AnyAsync(o => o.UnitId == unitId && mine.Contains(o.PersonId) && o.FromDate <= today && (o.ToDate == null || o.ToDate >= today), ct);
    }
}
