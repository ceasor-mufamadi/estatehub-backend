using System.Threading.RateLimiting;
using EstateHub.Application.Auth;

namespace EstateHub.Api.Security;

/// <summary>Bound from the "RateLimits" configuration section (SECURITY.md layer 6 defaults).</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int SignedInPerMinute { get; set; } = 300;
    public int AnonymousPerMinute { get; set; } = 20;
}

public static class RateLimitSetup
{
    /// <summary>
    /// ASP.NET's built-in limiter (in-memory: one API instance). Signed-in callers are limited per user,
    /// anonymous callers per client IP. Rejected calls get HTTP 429, which gRPC clients see as UNAVAILABLE.
    /// Must run after UseAuthentication so the user is known.
    /// </summary>
    public static IServiceCollection AddEstateHubRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var userId = http.User.FindFirst(TokenClaims.Subject)?.Value;
                if (userId is not null)
                    return RateLimitPartition.GetFixedWindowLimiter("user:" + userId, _ => PerMinute(limits.SignedInPerMinute));

                var ip = http.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(http);
                return RateLimitPartition.GetFixedWindowLimiter("ip:" + ip, _ => PerMinute(limits.AnonymousPerMinute));
            });
        });

        return services;
    }

    private static FixedWindowRateLimiterOptions PerMinute(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
}
