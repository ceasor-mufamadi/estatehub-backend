using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Streaming;
using EstateHub.Application.Tenancy;
using EstateHub.Infrastructure.Auth;
using EstateHub.Infrastructure.Cron;
using EstateHub.Infrastructure.Data;
using EstateHub.Infrastructure.Messaging;
using EstateHub.Infrastructure.Repositories;
using EstateHub.Infrastructure.Streaming;
using EstateHub.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EstateHub.Infrastructure;

public static class DependencyInjection
{
    public const string DataProtectionKeysPath = "DataProtection:KeysPath";

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

        // Sign-in (PHASE1.md §2a). Secrets at rest use Data Protection with the key ring in
        // DataProtection:KeysPath, outside the database (AUTH-18); Program.cs refuses to start
        // without it outside Development/Testing.
        var dataProtection = services.AddDataProtection().SetApplicationName("EstateHub");
        if (configuration[DataProtectionKeysPath] is { Length: > 0 } keysPath)
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        services.AddMemoryCache();
        services.AddHttpClient(PwnedPasswordsChecker.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
            client.Timeout = TimeSpan.FromSeconds(3);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EstateHub");
        });
        services.AddSingleton<IPasswordHashing, PasswordHashing>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IBreachedPasswords, PwnedPasswordsChecker>();
        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<IMembershipDirectory, MembershipDirectory>();
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddScoped<IInvitationDirectory, InvitationDirectory>();
        services.AddScoped<IMembersRepository, MembersRepository>();

        // Email (SMTP; Mailpit in development) and SMS (Messaging:SmsProvider).
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<ISmsSender, ConfiguredSmsSender>();
        services.AddHttpClient(ConfiguredSmsSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IAppLinks, AppLinks>();

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
