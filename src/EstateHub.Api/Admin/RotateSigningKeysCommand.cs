using EstateHub.Infrastructure.Auth;

namespace EstateHub.Api.Admin;

/// <summary>
/// Incident response (SECURITY.md §7): a new signing key and every other key retired at once, so
/// every access token in circulation stops working (the API picks the change up within 5 minutes;
/// restart it to apply at once). Routine rotation is ⏰ SigningKeyRotationJob's job, not this.
///   docker compose -f docker-compose.dev.yml run --rm -e ASPNETCORE_ENVIRONMENT=Development sdk \
///     dotnet run --project src/EstateHub.Api -- rotate-signing-keys
/// </summary>
public static class RotateSigningKeysCommand
{
    public const string Name = "rotate-signing-keys";

    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var (_, retired) = await scope.ServiceProvider.GetRequiredService<SigningKeyRotation>().RotateAsync(immediately: true);
        Console.WriteLine($"New signing key created; {retired} old key(s) retired. Every existing access token is now invalid.");
    }
}
