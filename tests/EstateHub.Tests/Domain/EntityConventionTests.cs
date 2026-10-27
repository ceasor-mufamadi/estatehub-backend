using EstateHub.Domain.Entities;
using EstateHub.Tests.Infrastructure;
using FluentAssertions;

namespace EstateHub.Tests.Domain;

// House rules checked against the real EF model, so a new table can't slip past them.
public class EntityConventionTests
{
    // Entities that carry an EstateId but are deliberately NOT tenant-scoped, with the reason.
    private static readonly Dictionary<Type, string> NotTenantScopedOnPurpose = new()
    {
        [typeof(AuditLog)] = "Nullable EstateId (platform events); every read names the estate explicitly.",
        [typeof(EstateFeature)] = "Written by super admin across estates; reads name the estate explicitly.",
    };

    private static IReadOnlyList<Type> EntityTypes()
    {
        using var db = new TestDb().Platform();
        return db.Model.GetEntityTypes().Select(e => e.ClrType).ToList();
    }

    [Fact]
    public void Model_Has_Entities() => EntityTypes().Should().NotBeEmpty();

    [Fact]
    public void Every_Table_Has_Created_And_Updated_Timestamps()
    {
        var missing = EntityTypes().Where(t => !typeof(IHasTimestamps).IsAssignableFrom(t)).Select(t => t.Name);

        missing.Should().BeEmpty("house rule: every table has created_at and updated_at (implement IHasTimestamps)");
    }

    [Fact]
    public void Every_Entity_With_An_EstateId_Is_Tenant_Scoped()
    {
        var problems = EntityTypes()
            .Where(t => t.GetProperty("EstateId") is not null)
            .Where(t => !typeof(ITenantScoped).IsAssignableFrom(t) && !NotTenantScopedOnPurpose.ContainsKey(t))
            .Select(t => t.Name);

        problems.Should().BeEmpty("an EstateId means estate data: implement ITenantScoped, or list it in NotTenantScopedOnPurpose with a reason");
    }
}
