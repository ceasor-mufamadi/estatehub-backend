using System.Text;
using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;
using FluentAssertions;

namespace EstateHub.Tests.Application;

public class TotpTests
{
    // RFC 6238 appendix B, SHA-1 key "12345678901234567890", 8 digits.
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void Matches_The_Rfc_6238_Test_Vectors(long unixSeconds, string expected) =>
        Totp.Code(Encoding.ASCII.GetBytes("12345678901234567890"), unixSeconds / Totp.StepSeconds, 8).Should().Be(expected);

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_Matches_Rfc_4648(string text, string expected)
    {
        Base32.Encode(Encoding.ASCII.GetBytes(text)).Should().Be(expected);
        Encoding.ASCII.GetString(Base32.Decode(expected)).Should().Be(text);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 15, TimeSpan.Zero);

    private static string CodeAt(string secret, long step) => Totp.Code(Base32.Decode(secret), step, 6);

    [Fact]
    public void Accepts_The_Current_Step_And_One_Either_Side_For_Clock_Drift()
    {
        var secret = Totp.NewSecret();
        var step = Now.ToUnixTimeSeconds() / Totp.StepSeconds;

        Totp.Verify(secret, CodeAt(secret, step), Now, null).Should().Be(step);
        Totp.Verify(secret, CodeAt(secret, step - 1), Now, null).Should().Be(step - 1);
        Totp.Verify(secret, CodeAt(secret, step + 1), Now, null).Should().Be(step + 1);
        Totp.Verify(secret, CodeAt(secret, step - 2), Now, null).Should().BeNull();
        Totp.Verify(secret, CodeAt(secret, step + 2), Now, null).Should().BeNull();
    }

    [Fact]
    public void A_Code_Cannot_Be_Used_Twice()
    {
        var secret = Totp.NewSecret();
        var step = Now.ToUnixTimeSeconds() / Totp.StepSeconds;

        Totp.Verify(secret, CodeAt(secret, step), Now, lastUsedStep: step).Should().BeNull();
        Totp.Verify(secret, CodeAt(secret, step - 1), Now, lastUsedStep: step).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Malformed_Codes_Are_Refused(string code) =>
        Totp.Verify(Totp.NewSecret(), code, Now, null).Should().BeNull();

    [Fact]
    public void OtpAuth_Uri_Is_What_Authenticator_Apps_Expect() =>
        Totp.OtpAuthUri("EstateHub", "a@b.co", "ABC").Should()
            .Be("otpauth://totp/EstateHub:a%40b.co?secret=ABC&issuer=EstateHub&algorithm=SHA1&digits=6&period=30");
}

public class AuthSecretsTests
{
    [Fact]
    public void Ten_Distinct_Recovery_Codes_Of_80_Bits()
    {
        var codes = AuthSecrets.NewRecoveryCodes();

        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        codes.Should().AllSatisfy(c => c.Should().MatchRegex("^[a-z2-7]{4}-[a-z2-7]{4}-[a-z2-7]{4}-[a-z2-7]{4}$"));
    }

    [Fact]
    public void Recovery_Codes_Are_Forgiving_To_Type() =>
        AuthSecrets.NormaliseRecoveryCode(" K7M2 q9xd-4HPA-w3ne ").Should().Be("k7m2q9xd4hpaw3ne");

    [Fact]
    public void Tokens_Are_256_Bit_And_Url_Safe()
    {
        var token = AuthSecrets.NewToken();

        token.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");
        AuthSecrets.Hash(token).Should().HaveLength(64).And.NotBe(token);
    }
}

public class AuthScopesTests
{
    private static readonly Guid EstateA = Guid.NewGuid();
    private static readonly Guid EstateB = Guid.NewGuid();
    private static readonly IReadOnlySet<string> Mfa = new HashSet<string> { "pwd", "mfa" };
    private static readonly IReadOnlySet<string> NoMfa = new HashSet<string> { "otp" };

    private static readonly UserMembership[] ManagerInA = [new(EstateA, "A", Roles.Manager)];
    private static readonly UserMembership[] OwnerInA = [new(EstateA, "A", Roles.Owner)];

    [Fact]
    public void Staff_Roles_Need_Mfa_To_Enter_An_Estate()
    {
        AuthScopes.RoleFor(new User(), Mfa, EstateA, ManagerInA).Should().Be(Roles.Manager);
        AuthScopes.RoleFor(new User(), NoMfa, EstateA, ManagerInA).Should().BeNull();
    }

    [Fact]
    public void Residents_Do_Not_Need_Mfa() =>
        AuthScopes.RoleFor(new User(), NoMfa, EstateA, OwnerInA).Should().Be(Roles.Owner);

    [Fact]
    public void No_Membership_No_Estate() =>
        AuthScopes.RoleFor(new User(), Mfa, EstateB, ManagerInA).Should().BeNull();

    [Fact]
    public void Super_Admin_Is_Platform_Level_Only_And_Only_With_Mfa()
    {
        var admin = new User { IsSuperAdmin = true };

        AuthScopes.RoleFor(admin, Mfa, null, []).Should().Be(Roles.SuperAdmin);
        AuthScopes.RoleFor(admin, NoMfa, null, []).Should().Be(Roles.None);
        AuthScopes.RoleFor(admin, Mfa, EstateA, []).Should().BeNull("no super admin bypass into an estate (SECURITY.md layer 5)");
    }

    [Fact]
    public void Disabled_Users_Get_Nothing() =>
        AuthScopes.RoleFor(new User { Status = UserStatus.Disabled }, Mfa, EstateA, ManagerInA).Should().BeNull();

    [Fact]
    public void One_Estate_Signs_Straight_In_Several_Pick_One()
    {
        AuthScopes.DefaultEstate(new User(), OwnerInA).Should().Be(EstateA);
        AuthScopes.DefaultEstate(new User(), [.. OwnerInA, new(EstateB, "B", Roles.Owner)]).Should().BeNull();
        AuthScopes.DefaultEstate(new User { IsSuperAdmin = true }, OwnerInA).Should().BeNull();
    }
}
