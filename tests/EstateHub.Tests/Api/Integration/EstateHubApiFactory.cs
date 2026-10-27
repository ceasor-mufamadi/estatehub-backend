using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using EstateHub.Infrastructure.Data;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EstateHub.Tests.Api.Integration;

/// <summary>
/// Boots the real EstateHub.Api host in-memory (TestServer) with Postgres swapped for EF InMemory,
/// so tests exercise the actual Program.cs wiring: DI, interceptors, auth, gRPC endpoint mapping.
/// InMemory honours the EF tenant filters but not Postgres RLS (see SECURITY.md §6).
/// </summary>
public class EstateHubApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"estatehub-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimits:SignedInPerMinute", "100000");
        builder.UseSetting("RateLimits:AnonymousPerMinute", "100000");

        builder.ConfigureServices(services =>
        {
            // Drop the Npgsql registration from AddInfrastructure before adding InMemory,
            // otherwise EF complains about two database providers.
            var npgsqlRegistrations = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<EstateHubDbContext>)
                         || d.ServiceType == typeof(IDbContextOptionsConfiguration<EstateHubDbContext>))
                .ToList();
            foreach (var descriptor in npgsqlRegistrations)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<EstateHubDbContext>(o => o.UseInMemoryDatabase(_databaseName));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Fail fast on missing or captive dependencies, as Development does by default.
        builder.UseDefaultServiceProvider(o =>
        {
            o.ValidateOnBuild = true;
            o.ValidateScopes = true;
        });

        return base.CreateHost(builder);
    }

    public GrpcChannel CreateGrpcChannel() =>
        GrpcChannel.ForAddress(Server.BaseAddress, new GrpcChannelOptions { HttpHandler = Server.CreateHandler() });

    /// <summary>A real token from the app's own TokenService (same keys the API validates with).</summary>
    public string Token(string userId, Guid? estateId, string role, bool mfa = false)
    {
        var tokens = Services.GetRequiredService<ITokenService>();
        string[] methods = mfa ? ["otp", TokenClaims.MfaMethod] : ["otp"];
        return tokens.Issue(new TokenRequest(userId, estateId, role, RolePermissions.For(role), methods, DateTimeOffset.UtcNow)).AccessToken;
    }

    public static Metadata Bearer(string token) => new() { { "authorization", $"Bearer {token}" } };

    public async Task<Guid> CreateEstateAsync(string name)
    {
        using var scope = Services.CreateScope();
        var estate = await scope.ServiceProvider.GetRequiredService<IEstateService>().CreateAsync(name, "sectional_title", "test");
        return estate.Id;
    }
}
