using EstateHub.Application.Auth;
using EstateHub.Application.Imports;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EstateHub.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IFeatureService, FeatureService>();
        services.AddScoped<IEstateService, EstateService>();
        services.AddScoped<IBlockService, BlockService>();
        services.AddScoped<IUnitService, UnitService>();
        services.AddScoped<IPeopleService, PeopleService>();
        services.AddScoped<IEstateDetailsService, EstateDetailsService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IMembersService, MembersService>();
        services.AddScoped<IGuardDeviceService, GuardDeviceService>();
        services.AddScoped<IImportService, ImportService>();
        services.AddScoped<IResidentService, ResidentService>();
        services.AddScoped<IPersonDataService, PersonDataService>();

        // Who is acting: the API replaces this with the request's caller; background work sets CallerOverride.
        services.AddScoped<CallerOverride>();
        services.TryAddScoped<ICurrentUser>(sp => sp.GetRequiredService<CallerOverride>().User ?? SnapshotCurrentUser.Nobody);

        return services;
    }
}
