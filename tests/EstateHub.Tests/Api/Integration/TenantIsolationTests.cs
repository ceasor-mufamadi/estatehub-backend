using EstateHub.Api;
using EstateHub.Application.Auth;
using FluentAssertions;
using Grpc.Core;

namespace EstateHub.Tests.Api.Integration;

// SECURITY.md layer 5 through the real host: two estates, each manager tries to reach the other's data.
public class TenantIsolationTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public TenantIsolationTests(EstateHubApiFactory factory) => _factory = factory;

    private async Task<(Guid EstateA, string TokenA, Guid EstateB, string TokenB)> TwoEstatesAsync()
    {
        var a = await _factory.CreateEstateAsync($"Estate A {Guid.NewGuid():N}");
        var b = await _factory.CreateEstateAsync($"Estate B {Guid.NewGuid():N}");
        return (a, await _factory.TokenAsync("manager-a", a, Roles.Manager), b, await _factory.TokenAsync("manager-b", b, Roles.Manager));
    }

    [Fact]
    public async Task Blocks_Of_One_Estate_Are_Invisible_To_Another()
    {
        var (_, tokenA, _, tokenB) = await TwoEstatesAsync();
        var client = new EstateService.EstateServiceClient(_factory.CreateGrpcChannel());

        await client.CreateBlockAsync(new CreateBlockRequest { Name = "Block A1" }, EstateHubApiFactory.Bearer(tokenA));

        var seenByA = await client.ListBlocksAsync(new ListBlocksRequest(), EstateHubApiFactory.Bearer(tokenA));
        var seenByB = await client.ListBlocksAsync(new ListBlocksRequest(), EstateHubApiFactory.Bearer(tokenB));

        seenByA.Items.Select(i => i.Name).Should().Equal("Block A1");
        seenByB.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Same_Block_Name_Is_Allowed_In_Different_Estates()
    {
        var (_, tokenA, _, tokenB) = await TwoEstatesAsync();
        var client = new EstateService.EstateServiceClient(_factory.CreateGrpcChannel());

        await client.CreateBlockAsync(new CreateBlockRequest { Name = "Block 1" }, EstateHubApiFactory.Bearer(tokenA));
        var inB = await GrpcStatus.Of(() =>
            client.CreateBlockAsync(new CreateBlockRequest { Name = "Block 1" }, EstateHubApiFactory.Bearer(tokenB)).ResponseAsync);

        inB.Should().Be(StatusCode.OK, "uniqueness is per estate; estate A's names must not leak into B's validation");
    }

    [Fact]
    public async Task Another_Estates_Audit_Entry_Is_NotFound()
    {
        var (_, tokenA, _, tokenB) = await TwoEstatesAsync();
        var estates = new EstateService.EstateServiceClient(_factory.CreateGrpcChannel());
        var audit = new AuditLogService.AuditLogServiceClient(_factory.CreateGrpcChannel());

        await estates.CreateBlockAsync(new CreateBlockRequest { Name = "Audited" }, EstateHubApiFactory.Bearer(tokenA));
        var entryInA = (await audit.ListAuditLogsAsync(new ListAuditLogsRequest { EntityType = "Block" }, EstateHubApiFactory.Bearer(tokenA))).Items.Single();

        var fromB = await GrpcStatus.Of(() =>
            audit.GetAuditLogAsync(new GetAuditLogRequest { Id = entryInA.Id }, EstateHubApiFactory.Bearer(tokenB)).ResponseAsync);
        var listB = await audit.ListAuditLogsAsync(new ListAuditLogsRequest { EntityType = "Block" }, EstateHubApiFactory.Bearer(tokenB));

        fromB.Should().Be(StatusCode.NotFound);
        listB.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task My_Context_Describes_Only_The_Tokens_Estate()
    {
        var (estateA, tokenA, _, _) = await TwoEstatesAsync();
        var features = new FeaturesService.FeaturesServiceClient(_factory.CreateGrpcChannel());

        var context = await features.GetMyContextAsync(new GetMyContextRequest(), EstateHubApiFactory.Bearer(tokenA));

        context.EstateId.Should().Be(estateA.ToString());
        context.EstateName.Should().StartWith("Estate A");
        context.Role.Should().Be(Roles.Manager);
        context.EnabledFeatures.Should().Contain("billing");
    }
}
