using EstateHub.Application.Interfaces;
using EstateHub.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EstateHub.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IFeatureService, FeatureService>();
        services.AddScoped<IEstateService, EstateService>();
        services.AddScoped<IBlockService, BlockService>();

        return services;
    }
}
