using EstateHub.Application.Auth;
using EstateHub.Application.Imports;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Streaming;
using EstateHub.Application.Tenancy;
using EstateHub.Infrastructure.Auth;
using EstateHub.Infrastructure.Cron;
using EstateHub.Infrastructure.Data;
using EstateHub.Infrastructure.Imports;
using EstateHub.Infrastructure.Messaging;
using EstateHub.Infrastructure.Repositories;
using EstateHub.Infrastructure.Streaming;
using EstateHub.Infrastructure.Tenancy;
using EstateHub.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
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

        // Tokens (ES256), keys in the database (encrypted). Validation is wired in the API's AuthorizationSetup.
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.AddSingleton<SigningKeyRing>();
        services.AddScoped<SigningKeyRotation>();
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
        services.AddSingleton<EstateHub.Application.Privacy.IIdNumberProtector, IdNumberProtector>();
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

        // Guard tablets and security alerts (slice 2.4).
        services.AddScoped<IDeviceDirectory, DeviceDirectory>();
        services.AddScoped<IGuardDeviceRepository, GuardDeviceRepository>();
        services.Configure<AlertOptions>(configuration.GetSection(AlertOptions.SectionName));
        services.AddSingleton<IAlerts, AlertSender>();
        services.AddHttpClient(AlertSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));

        // Passkeys (slice 2.5): ASP.NET Core Identity's WebAuthn handler over our own user store.
        // The relying party is the web app's domain and its exact origins (Passkeys section), not the
        // API's, because the browser talks WebAuthn with the web app.
        services.AddHttpContextAccessor();
        services.AddIdentityCore<User>();
        services.AddScoped<IUserStore<User>, EstateHubUserStore>();
        services.AddScoped<IPasskeyHandler<User>, PasskeyHandler<User>>();
        var passkeys = configuration.GetSection(PasskeySettings.SectionName).Get<PasskeySettings>() ?? new PasskeySettings();
        services.Configure<IdentityPasskeyOptions>(options =>
        {
            options.ServerDomain = passkeys.ServerDomain;
            options.ValidateOrigin = context => ValueTask.FromResult(!context.CrossOrigin && passkeys.Origins.Contains(context.Origin));
        });
        services.AddScoped<IPasskeys, Passkeys>();

        // Human check after repeated failures (AUTH-05).
        services.Configure<TurnstileOptions>(configuration.GetSection(TurnstileOptions.SectionName));
        services.AddSingleton<IHumanCheck, TurnstileHumanCheck>();
        services.AddSingleton<ISignInFailures, SignInFailures>();
        services.AddHttpClient(TurnstileHumanCheck.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));

        // Live-update publish/subscribe for server-streaming RPCs (in-memory: single API instance).
        services.AddSingleton(typeof(IEventBroadcaster<>), typeof(InMemoryEventBroadcaster<>));

        services.AddSingleton<EstateJobRunner>();

        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IEstateRepository, EstateRepository>();
        services.AddScoped<IFeatureRepository, FeatureRepository>();
        services.AddScoped<IBlockRepository, BlockRepository>();
        services.AddScoped<IUnitRepository, UnitRepository>();
        services.AddScoped<IPeopleRepository, PeopleRepository>();

        // Spreadsheet import (slice 3.3): the worker that runs commits is hosted by the API only (Program.cs).
        services.AddScoped<IImportRepository, ImportRepository>();
        services.AddSingleton<IImportFileReader, SpreadsheetReader>();
        services.AddSingleton<IImportTemplate, ImportTemplate>();
        services.AddSingleton<ImportQueue>();
        services.AddSingleton<IImportQueue>(sp => sp.GetRequiredService<ImportQueue>());
        services.AddSingleton<IImportProgress, ImportProgress>();

        return services;
    }
}
