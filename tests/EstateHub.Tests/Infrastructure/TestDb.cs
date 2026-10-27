using EstateHub.Infrastructure.Data;
using EstateHub.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Tests.Infrastructure;

/// <summary>
/// EF InMemory contexts that share one database, each with its own tenant (like two requests from
/// two estates). InMemory honours the query filters and the write guard, not Postgres RLS.
/// </summary>
public sealed class TestDb
{
    private readonly string _name = $"estatehub-tests-{Guid.NewGuid()}";

    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero));

    public EstateHubDbContext For(Guid? estateId)
    {
        var tenant = new TenantContext();
        if (estateId is { } id)
            tenant.Use(id);

        var options = new DbContextOptionsBuilder<EstateHubDbContext>().UseInMemoryDatabase(_name).Options;
        return new EstateHubDbContext(options, tenant, Clock);
    }

    public EstateHubDbContext Platform() => For(null);
}

public sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
