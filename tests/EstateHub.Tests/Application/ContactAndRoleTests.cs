using EstateHub.Application.Auth;
using FluentAssertions;

namespace EstateHub.Tests.Application;

public class ContactPointsTests
{
    [Theory]
    [InlineData("082 123 4567", "+27821234567")]
    [InlineData("0821234567", "+27821234567")]
    [InlineData("+27 82 123 4567", "+27821234567")]
    [InlineData("27821234567", "+27821234567")]
    [InlineData("071-555-0000", "+27715550000")]
    [InlineData("060 000 0000", "+27600000000")]
    public void South_African_Mobiles_Are_Stored_As_E164(string input, string expected) =>
        ContactPoints.SaMobile(input).Should().Be(expected);

    [Theory]
    [InlineData("+44 7700 900123")] // foreign: no SMS pumping to premium numbers (AUTH-06)
    [InlineData("011 555 1234")]    // landline
    [InlineData("12345")]
    [InlineData("")]
    public void Anything_Else_Is_Refused(string input) =>
        FluentActions.Invoking(() => ContactPoints.SaMobile(input)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(" Thandi@Example.CO.za ", "thandi@example.co.za")]
    public void Emails_Are_Normalised(string input, string expected) =>
        ContactPoints.Email(input).Should().Be(expected);

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("a@b")]
    [InlineData("a@@b.co")]
    [InlineData("a b@c.co")]
    public void Invalid_Emails_Are_Refused(string input) =>
        FluentActions.Invoking(() => ContactPoints.Email(input)).Should().Throw<ArgumentException>();

    [Fact]
    public void Masks_Show_Enough_To_Recognise_But_Not_To_Harvest()
    {
        ContactPoints.Mask("thandi@example.co.za").Should().Be("t****i@example.co.za");
        ContactPoints.Mask("+27821234567").Should().Be("+27 ** *** 4567");
    }
}

public class RoleGrantsTests
{
    [Theory]
    [InlineData(Roles.Manager, Roles.Owner, true)]
    [InlineData(Roles.Manager, Roles.Guard, true)]
    [InlineData(Roles.Manager, Roles.Manager, false)]
    [InlineData(Roles.Manager, Roles.Trustee, false)]
    [InlineData(Roles.ManagingAgent, Roles.Manager, true)]
    [InlineData(Roles.ManagingAgent, Roles.Trustee, true)]
    [InlineData(Roles.ManagingAgent, Roles.ManagingAgent, false)]
    [InlineData(Roles.SuperAdmin, Roles.ManagingAgent, true)]
    [InlineData(Roles.SuperAdmin, Roles.Owner, false)]
    [InlineData(Roles.Trustee, Roles.Owner, false)]
    [InlineData(Roles.Owner, Roles.Tenant, false)]
    public void Nobody_Grants_Above_Their_Own_Role(string actor, string role, bool allowed) =>
        RoleGrants.CanGrant(actor, role).Should().Be(allowed);

    [Fact]
    public void Super_Admin_Can_Never_Be_Granted() =>
        RolePermissions.AllRoles.Should().AllSatisfy(actor => RoleGrants.CanGrant(actor, Roles.SuperAdmin).Should().BeFalse());
}

public class DeviceNamesTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36", "Chrome on macOS")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari on iOS")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36 Edg/152.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0", "Firefox on Linux")]
    [InlineData("", "Unknown device")]
    public void Describes_Devices_In_Plain_Words(string userAgent, string expected) =>
        DeviceNames.Describe(userAgent).Should().Be(expected);
}
