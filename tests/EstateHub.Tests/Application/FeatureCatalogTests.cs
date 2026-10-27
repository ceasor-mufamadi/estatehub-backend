using EstateHub.Application.Auth;
using EstateHub.Application.Features;
using FluentAssertions;

namespace EstateHub.Tests.Application;

public class FeatureCatalogTests
{
    [Fact]
    public void Defaults_Apply_Without_Overrides()
    {
        var enabled = FeatureCatalog.Resolve(new Dictionary<string, bool>());

        enabled.Should().Contain([FeatureCatalog.Billing, FeatureCatalog.Maintenance, FeatureCatalog.Notices, FeatureCatalog.VisitorRegister]);
        enabled.Should().NotContain([FeatureCatalog.ShortStays, FeatureCatalog.Biometrics, FeatureCatalog.Parking]);
    }

    [Fact]
    public void Overrides_Win_Over_Defaults()
    {
        var enabled = FeatureCatalog.Resolve(new Dictionary<string, bool>
        {
            [FeatureCatalog.Parking] = true,
            [FeatureCatalog.Maintenance] = false,
        });

        enabled.Should().Contain(FeatureCatalog.Parking).And.NotContain(FeatureCatalog.Maintenance);
    }

    [Fact]
    public void A_Feature_With_A_Missing_Dependency_Is_Off_Transitively()
    {
        // biometrics → gate_control → visitor_register; switching visitor_register off kills both.
        var enabled = FeatureCatalog.Resolve(new Dictionary<string, bool>
        {
            [FeatureCatalog.Biometrics] = true,
            [FeatureCatalog.GateControl] = true,
            [FeatureCatalog.VisitorRegister] = false,
        });

        enabled.Should().NotContain([FeatureCatalog.Biometrics, FeatureCatalog.GateControl]);
    }

    [Fact]
    public void Every_Dependency_Is_A_Known_Feature() =>
        FeatureCatalog.All.SelectMany(f => f.Requires).Should().OnlyContain(r => FeatureCatalog.Exists(r));
}

public class RolePermissionsTests
{
    [Fact]
    public void Only_Super_Admin_Is_Unrestricted() =>
        RolePermissions.AllRoles.Where(r => RolePermissions.For(r).Contains(Permissions.Unrestricted))
            .Should().Equal(Roles.SuperAdmin);

    [Fact]
    public void Residents_Cannot_Write_Estate_Structure_Or_Read_The_Audit_Trail()
    {
        foreach (var role in new[] { Roles.Owner, Roles.Tenant, Roles.Guard, Roles.Contractor })
        {
            RolePermissions.For(role).Should().NotContain([Permissions.EstateWrite, Permissions.AuditLogRead], role);
        }
    }

    [Fact]
    public void Unknown_Role_Throws() =>
        FluentActions.Invoking(() => RolePermissions.For("janitor")).Should().Throw<ArgumentException>();
}
