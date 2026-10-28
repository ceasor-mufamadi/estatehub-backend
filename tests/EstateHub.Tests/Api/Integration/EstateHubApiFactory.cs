using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Tenancy;
using EstateHub.Infrastructure.Data;
using EstateHub.Tests.Infrastructure;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

            // InMemory has no SQL functions: read memberships directly (RLS is a Postgres-only layer anyway).
            services.RemoveAll<IMembershipDirectory>();
            services.AddScoped<IMembershipDirectory, InMemoryMembershipDirectory>();
            services.RemoveAll<IInvitationDirectory>();
            services.AddScoped<IInvitationDirectory, InMemoryInvitationDirectory>();

            // Messages are recorded, not sent.
            services.RemoveAll<IEmailSender>();
            services.RemoveAll<ISmsSender>();
            services.AddSingleton<IEmailSender>(Outbox);
            services.AddSingleton<ISmsSender>(Outbox);

            // One clock for tokens, sessions, lockouts and step-up, so tests can move time.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.RemoveAll<IBreachedPasswords>();
            services.AddSingleton<IBreachedPasswords>(BreachedPasswords);

            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
    }

    public FakeClock Clock { get; } = new(DateTimeOffset.UtcNow);

    public FakeBreachedPasswords BreachedPasswords { get; } = new();

    public Outbox Outbox { get; } = new();

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

    /// <summary>
    /// An access token from a real session (development sign-in): a user "<label>@dev.estatehub.local"
    /// with a membership in the estate. <paramref name="mfa"/> false = MFA over 15 minutes ago.
    /// </summary>
    public async Task<string> TokenAsync(string label, Guid? estateId, string role, bool mfa = true)
    {
        using var scope = Services.CreateScope();
        var signedIn = await scope.ServiceProvider.GetRequiredService<IAuthService>()
            .DevSignInAsync(label, estateId, role, mfa, new ClientInfo("127.0.0.1", "tests"));
        return signedIn.Access.AccessToken;
    }

    /// <summary>Runs <paramref name="action"/> against the database directly, inside <paramref name="estateId"/> if given.</summary>
    public async Task WithDbAsync(Func<EstateHubDbContext, Task> action, Guid? estateId = null)
    {
        using var scope = Services.CreateScope();
        if (estateId is { } id)
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(id);
        await action(scope.ServiceProvider.GetRequiredService<EstateHubDbContext>());
    }

    public static Metadata Bearer(string token) => new() { { "authorization", $"Bearer {token}" } };

    public async Task<Guid> CreateEstateAsync(string name)
    {
        using var scope = Services.CreateScope();
        var estate = await scope.ServiceProvider.GetRequiredService<IEstateService>().CreateAsync(name, "sectional_title", "test");
        return estate.Id;
    }
}
