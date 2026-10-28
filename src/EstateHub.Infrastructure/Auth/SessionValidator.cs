using EstateHub.Application.Auth;
using Microsoft.Extensions.Caching.Memory;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// "Is this token's session still allowed to do this?", asked on every authenticated call
/// (SECURITY.md layer 4: revocation within ~30 s). The answer is cached per session for 30 seconds;
/// revoking on this server forgets it at once. In-memory: one API instance (SECURITY.md §6).
/// </summary>
public sealed class SessionValidator : ISessionValidator
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IAuthRepository _repo;
    private readonly IMembershipDirectory _directory;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    public SessionValidator(IAuthRepository repo, IMembershipDirectory directory, IMemoryCache cache, TimeProvider clock)
    {
        _repo = repo;
        _directory = directory;
        _cache = cache;
        _clock = clock;
    }

    public async Task<bool> IsValidAsync(Guid sessionId, Guid userId, Guid? estateId, string? role, CancellationToken ct = default)
    {
        var key = Key(sessionId);
        var scope = $"{userId}|{estateId}|{role}";
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(key, out CachedAnswer? cached) && cached!.Scope == scope && now - cached.CheckedAt < CacheFor)
            return cached.Valid;

        var valid = await CheckAsync(sessionId, userId, estateId, role ?? Roles.None, ct);
        _cache.Set(key, new CachedAnswer(scope, valid, now), CacheFor);
        return valid;
    }

    public void Forget(Guid sessionId) => _cache.Remove(Key(sessionId));

    private async Task<bool> CheckAsync(Guid sessionId, Guid userId, Guid? estateId, string role, CancellationToken ct)
    {
        var session = await _repo.GetSessionAsync(sessionId, ct);
        if (session is null || session.UserId != userId || session.RevokedAt is not null || session.ExpiresAt <= _clock.GetUtcNow().UtcDateTime)
            return false;

        var user = await _repo.GetUserAsync(userId, ct);
        if (user is null || user.SecurityStamp != session.SecurityStamp)
            return false;

        var memberships = await _directory.ForUserAsync(userId, ct);
        return AuthScopes.RoleFor(user, AuthScopes.ParseMethods(session.AuthMethods), estateId, memberships) == role;
    }

    private static string Key(Guid sessionId) => $"auth:session:{sessionId}";

    private sealed record CachedAnswer(string Scope, bool Valid, DateTimeOffset CheckedAt);
}
