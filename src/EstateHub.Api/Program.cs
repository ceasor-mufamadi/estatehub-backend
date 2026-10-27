using EstateHub.Api.Authorization;
using EstateHub.Api.Errors;
using EstateHub.Api.Security;
using EstateHub.Api.Services;
using EstateHub.Api.Streaming;
using EstateHub.Application;
using EstateHub.Application.Cron;
using EstateHub.Infrastructure;
using EstateHub.Infrastructure.Cron;
using Grpc.AspNetCore.Web;
using Microsoft.AspNetCore.Http.Timeouts;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

// CreateBuilder already loads appsettings.json, appsettings.{Environment}.json, user secrets
// (Development) and environment variables, in that precedence order.
var builder = WebApplication.CreateBuilder(args);

// Same image, two roles (as chieta-disbursement-service): "api" (default) serves gRPC,
// "jobs" runs the cron jobs. docker-compose.prod.yml starts one container of each.
var runMode =
    Environment.GetEnvironmentVariable("RUN_MODE")
    ?? builder.Configuration["Runtime:RunMode"]
    ?? "api";

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();

if (runMode == "jobs")
{
    // Register job types as scoped
    var jobTypes = typeof(EstateJobRunner).Assembly
        .GetTypes()
        .Where(t =>
            typeof(ICronJob).IsAssignableFrom(t) &&
            !t.IsInterface &&
            !t.IsAbstract);

    foreach (var jobType in jobTypes)
    {
        builder.Services.AddScoped(jobType);
    }
    // CronJobHostedService remains singleton
    builder.Services.AddSingleton<CronJobHostedService>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<CronJobHostedService>());
}
else
{
    // The "Http" and "Grpc" Kestrel endpoints (see appsettings.json) are split across two
    // ports because a single cleartext port can't ALPN-negotiate HTTP/1.1 vs HTTP/2 without
    // TLS — Kestrel always falls back to HTTP/1.1 in that case, which silently breaks gRPC.

    // Validates EstateHub's own ES256 tokens; deny by default (SECURITY.md layers 3–4).
    builder.Services.AddEstateHubAuthorization();

    builder.Services.Configure<ReverseProxyOptions>(builder.Configuration.GetSection(ReverseProxyOptions.SectionName));
    builder.Services.AddSingleton<ClientIpResolver>();
    builder.Services.AddEstateHubRateLimits(builder.Configuration);

    // Every call gets 30 s; streams opt out with [DisableRequestTimeout] (SECURITY.md layer 6).
    builder.Services.AddRequestTimeouts(o => o.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(30) });

    builder.Services.Configure<StreamingOptions>(builder.Configuration.GetSection(StreamingOptions.SectionName));
    builder.Services.AddSingleton<StreamConnectionLimiter>();

    // Interceptor order matters: errors outermost, then tenant → feature → step-up MFA → ownership.
    builder.Services.AddGrpc(o =>
    {
        o.MaxReceiveMessageSize = 1024 * 1024;
        o.Interceptors.Add<ErrorInterceptor>();
        o.Interceptors.Add<TenantInterceptor>();
        o.Interceptors.Add<FeatureInterceptor>();
        o.Interceptors.Add<RecentMfaInterceptor>();
        o.Interceptors.Add<OwnershipInterceptor>();
    });
    builder.Services.AddGrpcReflection();

    // Browsers call EstateHub with gRPC-Web on the HTTP/1.1 "Http" endpoint; native gRPC clients use the
    // HTTP/2 "Grpc" endpoint. CORS: exact origins only, never a wildcard (SECURITY.md layer 2).
    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .WithMethods("POST", "OPTIONS")
        .AllowCredentials()
        .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding", "Grpc-Status-Details-Bin",
            RecentMfaInterceptor.StepUpTrailer)));
}

var app = builder.Build();

if (runMode != "jobs")
{
    app.UseCors();
    // Every gRPC service also answers gRPC-Web (application/grpc-web and application/grpc-web-text).
    app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
    app.UseAuthentication();
    app.UseRateLimiter();
    app.UseRequestTimeouts();
    app.UseAuthorization();

    app.MapGrpcService<AuthGrpcService>();
    app.MapGrpcService<FeaturesGrpcService>();
    app.MapGrpcService<PlatformGrpcService>();
    app.MapGrpcService<EstateGrpcService>();
    app.MapGrpcService<AuditLogGrpcService>();

    if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Docker")
    {
        app.MapGrpcReflectionService().AllowAnonymous();
    }
}

app.MapGet("/", () => $"EstateHub {runMode} running").AllowAnonymous();

app.Run();

// Exposed for integration tests.
public partial class Program;
