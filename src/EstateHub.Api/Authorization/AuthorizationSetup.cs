using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EstateHub.Api.Authorization;

public static class AuthorizationSetup
{
    /// <summary>
    /// Validates EstateHub's own ES256 access tokens locally (no auth service round-trip) and wires
    /// the permission policies. Always enforced: there is no switch to turn it off (AUTH-17).
    /// </summary>
    public static IServiceCollection AddEstateHubAuthorization(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeyStore, IOptions<JwtSettings>>((options, keys, jwt) =>
            {
                options.MapInboundClaims = false; // keep our claim names: sub, estate_id, role, perm, amr, auth_time
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = keys.ValidationKey,
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    NameClaimType = TokenClaims.Subject,
                    RoleClaimType = TokenClaims.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        return services;
    }
}
