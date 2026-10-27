using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Streaming;
using EstateHub.Application.Tenancy;
using EstateHub.Infrastructure.Auth;
using EstateHub.Infrastructure.Cron;
using EstateHub.Infrastructure.Data;
using EstateHub.Infrastructure.Repositories;
using EstateHub.Infrastructure.Streaming;
using EstateHub.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EstateHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Tenancy: one TenantContext per scope (request or per-estate job scope).
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantConnectionInterceptor>();

        // The API connects as estatehub_app (never the schema owner, never a superuser): see SECURITY.md layer 5.
        services.AddDbContext<EstateHubDbContext>((sp, options) => options
            .UseNpgsql(configuration.GetConnectionString("Postgres"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>()));

        // Tokens (ES256). Validation is wired in the API's AuthorizationSetup.
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.AddSingleton<SigningKeyStore>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddScoped<IResourceAccess, ResourceAccess>();

        // Live-update publish/subscribe for server-streaming RPCs (in-memory: single API instance).
        services.AddSingleton(typeof(IEventBroadcaster<>), typeof(InMemoryEventBroadcaster<>));

        services.AddSingleton<EstateJobRunner>();

        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IEstateRepository, EstateRepository>();
        services.AddScoped<IFeatureRepository, FeatureRepository>();
        services.AddScoped<IBlockRepository, BlockRepository>();

        return services;
    }
}
