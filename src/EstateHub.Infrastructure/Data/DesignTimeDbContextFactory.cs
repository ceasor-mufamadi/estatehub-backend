using EstateHub.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EstateHub.Infrastructure.Data;

/// <summary>
/// Lets `dotnet ef` build the model and create/apply migrations without booting the API.
/// Migrations run as the schema owner (estatehub_migrator), never as the app role.
/// Connection: env var ConnectionStrings__Migrator, else the local dev database from docker-compose.dev.yml.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EstateHubDbContext>
{
    private const string LocalDev =
        "Host=localhost;Port=5432;Database=estatehub_dev;Username=estatehub_migrator;Password=migrator_dev_only";

    public EstateHubDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Migrator") ?? LocalDev;

        var options = new DbContextOptionsBuilder<EstateHubDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new EstateHubDbContext(options, new TenantContext(), TimeProvider.System);
    }
}
