using System.Text.Json;
using EstateHub.Application.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EstateHub.Infrastructure.Auth;

public sealed class TokenService : ITokenService
{
    private readonly SigningKeyStore _keys;
    private readonly JwtSettings _settings;
    private readonly TimeProvider _clock;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(SigningKeyStore keys, IOptions<JwtSettings> settings, TimeProvider clock)
    {
        _keys = keys;
        _settings = settings.Value;
        _clock = clock;
    }

    public IssuedToken Issue(TokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new ArgumentException("UserId is required.", nameof(request));

        var now = _clock.GetUtcNow();
        var expires = now.AddMinutes(_settings.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [TokenClaims.Subject] = request.UserId,
            [TokenClaims.SessionId] = request.SessionId.ToString(),
            [TokenClaims.Role] = request.Role,
            [TokenClaims.Permission] = request.Permissions.ToArray(),
            [TokenClaims.AuthMethod] = request.AuthMethods.ToArray(),
            [TokenClaims.AuthTime] = request.AuthTime.ToUnixTimeSeconds(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };

        if (request.EstateId is { } estateId)
            claims[TokenClaims.EstateId] = estateId.ToString();
        if (request.Role == Roles.None)
            claims.Remove(TokenClaims.Role);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _keys.SigningCredentials,
        });

        return new IssuedToken(token, expires);
    }

    public string GetJwksJson()
    {
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(_keys.ValidationKey);

        // Written by hand so only public members can ever appear (no "d").
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kty = jwk.Kty, crv = jwk.Crv, x = jwk.X, y = jwk.Y, kid = _keys.KeyId, use = "sig", alg = "ES256" },
            },
        });
    }
}
