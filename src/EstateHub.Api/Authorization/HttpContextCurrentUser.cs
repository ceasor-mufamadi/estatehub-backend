using System.Globalization;
using System.Security.Claims;
using EstateHub.Application.Auth;

namespace EstateHub.Api.Authorization;

/// <summary><see cref="ICurrentUser"/> over the validated access token on the current request.</summary>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly ClaimsPrincipal _principal;

    public HttpContextCurrentUser(IHttpContextAccessor accessor)
    {
        _principal = accessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        Permissions = Values(TokenClaims.Permission);
        AuthMethods = Values(TokenClaims.AuthMethod);
    }

    public bool IsAuthenticated => _principal.Identity?.IsAuthenticated == true;

    public string? UserId => First(TokenClaims.Subject);

    public Guid? SessionId => Guid.TryParse(First(TokenClaims.SessionId), out var id) ? id : null;

    public Guid? EstateId => Guid.TryParse(First(TokenClaims.EstateId), out var id) ? id : null;

    public string? Role => First(TokenClaims.Role);

    public IReadOnlySet<string> Permissions { get; }

    public IReadOnlySet<string> AuthMethods { get; }

    public DateTimeOffset? AuthTime =>
        long.TryParse(First(TokenClaims.AuthTime), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    public bool HasPermission(string permission) => Permissions.Contains(permission);

    private string? First(string type) => _principal.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;

    private HashSet<string> Values(string type) =>
        _principal.FindAll(type).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
}
