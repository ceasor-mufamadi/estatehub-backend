using System.Security.Cryptography;
using EstateHub.Api;
using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.Features;
using FluentAssertions;
using Grpc.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EstateHub.Tests.Api.Integration;

// SECURITY.md layers 3–4 against the real host: always enforced, deny by default.
public class AuthorizationEnforcedTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public AuthorizationEnforcedTests(EstateHubApiFactory factory) => _factory = factory;

    private EstateService.EstateServiceClient Estate() => new(_factory.CreateGrpcChannel());
    private PlatformService.PlatformServiceClient Platform() => new(_factory.CreateGrpcChannel());

    private async Task<StatusCode> CreateBlockAs(string? token) =>
        await GrpcStatus.Of(() => Estate().CreateBlockAsync(
            new CreateBlockRequest { Name = $"Block {Guid.NewGuid():N}" },
            token is null ? null : EstateHubApiFactory.Bearer(token)).ResponseAsync);

    [Fact]
    public async Task No_Token_Is_Unauthenticated() =>
        (await CreateBlockAs(null)).Should().Be(StatusCode.Unauthenticated);

    [Fact]
    public async Task Tampered_Token_Is_Unauthenticated()
    {
        var estateId = await _factory.CreateEstateAsync("Tamper Estate");
        var token = _factory.Token("user-1", estateId, Roles.Manager);

        (await CreateBlockAs(token[..^4] + "abcd")).Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Token_Signed_With_Another_Key_Is_Unauthenticated()
    {
        var estateId = await _factory.CreateEstateAsync("Forged Estate");
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "estatehub",
            Audience = "estatehub",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "attacker",
                ["estate_id"] = estateId.ToString(),
                ["role"] = Roles.SuperAdmin,
                ["perm"] = new[] { Permissions.Unrestricted },
            },
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(attackerKey), SecurityAlgorithms.EcdsaSha256),
        });

        (await CreateBlockAs(forged)).Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Role_Without_The_Permission_Is_PermissionDenied()
    {
        var estateId = await _factory.CreateEstateAsync("Owner Estate");

        (await CreateBlockAs(_factory.Token("owner-1", estateId, Roles.Owner))).Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Role_With_The_Permission_Is_Allowed()
    {
        var estateId = await _factory.CreateEstateAsync("Manager Estate");

        (await CreateBlockAs(_factory.Token("manager-1", estateId, Roles.Manager))).Should().Be(StatusCode.OK);
    }

    [Fact]
    public async Task Estate_Rpc_Without_An_Estate_In_The_Token_Is_FailedPrecondition() =>
        (await CreateBlockAs(_factory.Token("manager-1", estateId: null, Roles.Manager))).Should().Be(StatusCode.FailedPrecondition);

    [Fact]
    public async Task Estate_Staff_Cannot_Use_Platform_Rpcs()
    {
        var estateId = await _factory.CreateEstateAsync("Platform Probe");
        var token = _factory.Token("manager-1", estateId, Roles.Manager, mfa: true);

        (await GrpcStatus.Of(() => Platform().ListEstatesAsync(new ListEstatesRequest(), EstateHubApiFactory.Bearer(token)).ResponseAsync))
            .Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Sensitive_Rpc_Without_Recent_Mfa_Asks_For_Step_Up()
    {
        var estateId = await _factory.CreateEstateAsync("Step Up Estate");
        var token = _factory.Token("admin", estateId: null, Roles.SuperAdmin, mfa: false);

        var error = await GrpcStatus.ErrorOf(() => Platform().SetEstateFeatureAsync(
            new SetEstateFeatureRequest { EstateId = estateId.ToString(), FeatureKey = FeatureCatalog.Parking, Enabled = true },
            EstateHubApiFactory.Bearer(token)).ResponseAsync);

        error.Should().NotBeNull();
        error!.StatusCode.Should().Be(StatusCode.PermissionDenied);
        error.Trailers.GetValue(RecentMfaInterceptor.StepUpTrailer).Should().Be("required");
    }

    [Fact]
    public async Task Sensitive_Rpc_With_Recent_Mfa_Is_Allowed()
    {
        var estateId = await _factory.CreateEstateAsync("Mfa Estate");
        var token = _factory.Token("admin", estateId: null, Roles.SuperAdmin, mfa: true);

        var estate = await Platform().SetEstateFeatureAsync(
            new SetEstateFeatureRequest { EstateId = estateId.ToString(), FeatureKey = FeatureCatalog.Parking, Enabled = true },
            EstateHubApiFactory.Bearer(token));

        estate.EnabledFeatures.Should().Contain(FeatureCatalog.Parking);
    }

    [Fact]
    public async Task DevSignIn_Is_Anonymous_In_Testing_And_Its_Token_Works()
    {
        var estateId = await _factory.CreateEstateAsync("Dev Estate");
        var auth = new AuthService.AuthServiceClient(_factory.CreateGrpcChannel());

        var signedIn = await auth.DevSignInAsync(new DevSignInRequest { UserId = "dev-1", EstateId = estateId.ToString(), Role = Roles.Manager });

        (await CreateBlockAs(signedIn.AccessToken)).Should().Be(StatusCode.OK);
    }

    [Fact]
    public async Task Jwks_Publishes_Only_The_Public_Key()
    {
        var auth = new AuthService.AuthServiceClient(_factory.CreateGrpcChannel());

        var jwks = await auth.GetJwksAsync(new GetJwksRequest());

        jwks.JwksJson.Should().Contain("\"ES256\"").And.NotContain("\"d\"");
    }

    [Fact]
    public async Task Errors_Do_Not_Leak_Internals()
    {
        var estateId = await _factory.CreateEstateAsync("Validation Estate");
        var token = _factory.Token("manager-1", estateId, Roles.Manager);

        var error = await GrpcStatus.ErrorOf(() => Estate().CreateBlockAsync(new CreateBlockRequest { Name = "" }, EstateHubApiFactory.Bearer(token)).ResponseAsync);

        error!.StatusCode.Should().Be(StatusCode.InvalidArgument);
        error.Status.Detail.Should().Be("Name is required. (Parameter 'name')");
    }
}
