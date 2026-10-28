using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            .Configure<SigningKeyRing, IOptions<JwtSettings>, TimeProvider>((options, keys, jwt, clock) =>
            {
                var skew = TimeSpan.FromSeconds(30);
                options.MapInboundClaims = false; // keep our claim names: sub, estate_id, role, perm, amr, auth_time
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    // All live keys: during a rotation tokens signed by either one are valid.
                    IssuerSigningKeyResolver = (_, _, _, _) => keys.ValidationKeys,
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    NameClaimType = TokenClaims.Subject,
                    RoleClaimType = TokenClaims.Role,
                    ClockSkew = skew,
                    // Lifetime against the app's TimeProvider (the same clock sessions, lockouts and
                    // step-up use), so all time rules agree, and tests can move time.
                    LifetimeValidator = (notBefore, expires, _, _) =>
                    {
                        var now = clock.GetUtcNow().UtcDateTime;
                        return expires is { } exp && exp.ToUniversalTime() > now - skew
                               && (notBefore is not { } nbf || nbf.ToUniversalTime() <= now + skew);
                    },
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.AddHttpContextAccessor();
        // The request's caller, unless background work in this scope acts for someone (CallerOverride).
        services.RemoveAll<ICurrentUser>();
        services.AddScoped<ICurrentUser>(sp =>
            sp.GetRequiredService<CallerOverride>().User ?? new HttpContextCurrentUser(sp.GetRequiredService<IHttpContextAccessor>()));
        return services;
    }
}
