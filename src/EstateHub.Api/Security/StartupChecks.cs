using EstateHub.Infrastructure;
using Microsoft.Extensions.Options;

namespace EstateHub.Api.Security;

/// <summary>
/// Settings that are only allowed to be missing or relaxed in Development/Testing. Anywhere else the
/// API refuses to start rather than run insecurely.
/// </summary>
public static class StartupChecks
{
    public static void Run(WebApplication app)
    {
        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
            return;

        if (string.IsNullOrWhiteSpace(app.Configuration[DependencyInjection.DataProtectionKeysPath]))
            throw new InvalidOperationException(
                $"{DependencyInjection.DataProtectionKeysPath} is not configured: TOTP secrets need a persistent key ring outside the database (AUTH-18).");

        if (!app.Services.GetRequiredService<IOptions<AuthCookieOptions>>().Value.RefreshCookieSecure)
            throw new InvalidOperationException("Auth:RefreshCookieSecure may only be false in Development.");
    }
}
