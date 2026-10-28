namespace EstateHub.Application.Auth;

/// <summary>Issues EstateHub access tokens (ES256 JWTs) and publishes the public keys that verify them.</summary>
public interface ITokenService
{
    IssuedToken Issue(TokenRequest request);

    /// <summary>JSON Web Key Set of the current public signing keys (for the edge gateway, later).</summary>
    string GetJwksJson();
}

public record TokenRequest(
    string UserId,
    Guid SessionId,
    Guid? EstateId,
    string Role,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<string> AuthMethods,
    DateTimeOffset AuthTime,
    string? ActorId = null);

public record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>JWT claim names EstateHub issues and reads. Kept short and stable: they are part of the API.</summary>
public static class TokenClaims
{
    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string EstateId = "estate_id";
    public const string Role = "role";
    public const string Permission = "perm";
    public const string AuthMethod = "amr";
    public const string AuthTime = "auth_time";

    /// <summary>RFC 8693 actor: {"sub": "&lt;super admin id&gt;"} on support-access tokens (AUTH-13).</summary>
    public const string Actor = "act";

    public const string MfaMethod = "mfa";
}
