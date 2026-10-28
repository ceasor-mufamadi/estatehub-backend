using EstateHub.Api;
using EstateHub.Application.Auth;
using FluentAssertions;
using Grpc.Core;

namespace EstateHub.Tests.Api.Integration;

// Slice 3.1 through the real host: estate details, blocks, units and participation quotas, and who
// may see them (staff and guards only; residents never see other units).
public class EstateStructureTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public EstateStructureTests(EstateHubApiFactory factory) => _factory = factory;

    private EstateService.EstateServiceClient Estate() => new(_factory.CreateGrpcChannel());

    private static string Label() => $"s-{Guid.NewGuid():N}"[..20];

    private async Task<(Guid EstateId, Metadata Manager)> EstateAsync()
    {
        var estateId = await _factory.CreateEstateAsync($"Structure {Guid.NewGuid():N}");
        return (estateId, EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager)));
    }

    private static CreateUnitRequest Unit(int section, string quota, string door = "", string blockId = "", string size = "") =>
        new() { Unit = new UnitInput { SectionNumber = section, ParticipationQuota = quota, DoorNumber = door, BlockId = blockId, SizeSquareMetres = size } };

    private async Task<StatusCode> StatusOf<T>(Func<AsyncUnaryCall<T>> call) => await GrpcStatus.Of(() => call().ResponseAsync);

    [Fact]
    public async Task Units_Report_Whether_Their_Quotas_Add_Up_To_100_Percent()
    {
        var (_, manager) = await EstateAsync();
        var block = await Estate().CreateBlockAsync(new CreateBlockRequest { Name = "Block A" }, manager);

        await Estate().CreateUnitAsync(Unit(1, "33.3333", door: "A1", blockId: block.Id, size: "84,5"), manager);
        await Estate().CreateUnitAsync(Unit(2, "33.3333"), manager);
        var partial = await Estate().ListUnitsAsync(new ListUnitsRequest(), manager);
        partial.QuotaTotal.Should().Be("66.6666");
        partial.QuotaComplete.Should().BeFalse();

        await Estate().CreateUnitAsync(Unit(3, "33.3334"), manager);
        var full = await Estate().ListUnitsAsync(new ListUnitsRequest(), manager);

        full.QuotaTotal.Should().Be("100.0000");
        full.QuotaComplete.Should().BeTrue();
        full.Items.Select(u => u.SectionNumber).Should().Equal(1, 2, 3);
        var first = full.Items[0];
        (first.DoorNumber, first.BlockName, first.SizeSquareMetres).Should().Be(("A1", "Block A", "84.50"), "comma decimals are accepted");
    }

    [Theory]
    [InlineData(0, "10", "between 1 and")]
    [InlineData(5, "0", "above 0")]
    [InlineData(5, "100.5", "at most 100")]
    [InlineData(5, "2.34567", "at most 4 decimals")]
    [InlineData(5, "abc", "must be a number")]
    public async Task Invalid_Units_Are_Refused_With_A_Clear_Reason(int section, string quota, string message)
    {
        var (_, manager) = await EstateAsync();

        var error = await GrpcStatus.ErrorOf(() => Estate().CreateUnitAsync(Unit(section, quota), manager).ResponseAsync);

        error!.StatusCode.Should().Be(StatusCode.InvalidArgument);
        error.Status.Detail.Should().Contain(message);
    }

    [Fact]
    public async Task A_Section_Number_Is_Used_Once_Per_Estate_But_Other_Estates_Can_Reuse_It()
    {
        var (_, managerA) = await EstateAsync();
        var (_, managerB) = await EstateAsync();
        await Estate().CreateUnitAsync(Unit(7, "10"), managerA);

        (await GrpcStatus.ErrorOf(() => Estate().CreateUnitAsync(Unit(7, "5"), managerA).ResponseAsync))!.Status.Detail.Should().Contain("Section 7 already exists");
        (await StatusOf(() => Estate().CreateUnitAsync(Unit(7, "5"), managerB))).Should().Be(StatusCode.OK);
    }

    [Fact]
    public async Task Units_Can_Be_Changed_And_Removed()
    {
        var (_, manager) = await EstateAsync();
        var unit = await Estate().CreateUnitAsync(Unit(4, "10"), manager);

        var changed = await Estate().UpdateUnitAsync(new UpdateUnitRequest
        {
            UnitId = unit.Id,
            Unit = new UnitInput { SectionNumber = 40, ParticipationQuota = "12.5", DoorNumber = "B4" },
        }, manager);
        changed.SectionNumber.Should().Be(40);
        changed.ParticipationQuota.Should().Be("12.5000");

        await Estate().DeleteUnitAsync(new DeleteUnitRequest { UnitId = unit.Id }, manager);
        (await Estate().ListUnitsAsync(new ListUnitsRequest(), manager)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Blocks_Can_Be_Renamed_And_Only_Empty_Ones_Removed()
    {
        var (_, manager) = await EstateAsync();
        var a = await Estate().CreateBlockAsync(new CreateBlockRequest { Name = "North" }, manager);
        var b = await Estate().CreateBlockAsync(new CreateBlockRequest { Name = "South" }, manager);
        await Estate().CreateUnitAsync(Unit(1, "50", blockId: a.Id), manager);

        (await Estate().RenameBlockAsync(new RenameBlockRequest { BlockId = a.Id, Name = "North Wing" }, manager)).Name.Should().Be("North Wing");
        (await StatusOf(() => Estate().RenameBlockAsync(new RenameBlockRequest { BlockId = b.Id, Name = "North Wing" }, manager)))
            .Should().Be(StatusCode.InvalidArgument, "names are unique");
        (await GrpcStatus.ErrorOf(() => Estate().DeleteBlockAsync(new DeleteBlockRequest { BlockId = a.Id }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("still has units");

        await Estate().DeleteBlockAsync(new DeleteBlockRequest { BlockId = b.Id }, manager);
        (await Estate().ListBlocksAsync(new ListBlocksRequest(), manager)).Items.Select(x => x.Name).Should().Equal("North Wing");
    }

    [Fact]
    public async Task Staff_Keep_The_Estates_Details()
    {
        var (_, manager) = await EstateAsync();

        var details = await Estate().UpdateEstateDetailsAsync(new UpdateEstateDetailsRequest
        {
            Name = $"Renamed {Guid.NewGuid():N}",
            SchemeNumber = "SS 123/2005",
            CsosRegistrationNumber = "CSOS-0042",
            Address = "316 York Avenue, Randburg",
        }, manager);

        (await Estate().GetEstateDetailsAsync(new GetEstateDetailsRequest(), manager)).Should().BeEquivalentTo(details);
        details.SchemeNumber.Should().Be("SS 123/2005");
    }

    // ---------- who sees what ----------

    [Theory] // privacy between neighbours: residents never see the estate's units (POPIA)
    [InlineData(Roles.Owner)]
    [InlineData(Roles.Tenant)]
    [InlineData(Roles.Contractor)]
    public async Task Residents_Cannot_List_The_Estates_Units_Or_Blocks(string role)
    {
        var (estateId, manager) = await EstateAsync();
        await Estate().CreateUnitAsync(Unit(1, "100"), manager);
        var resident = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, role));

        (await StatusOf(() => Estate().ListUnitsAsync(new ListUnitsRequest(), resident))).Should().Be(StatusCode.PermissionDenied);
        (await StatusOf(() => Estate().ListBlocksAsync(new ListBlocksRequest(), resident))).Should().Be(StatusCode.PermissionDenied);
        (await StatusOf(() => Estate().GetEstateDetailsAsync(new GetEstateDetailsRequest(), resident))).Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Guards_See_Unit_Numbers_But_Cannot_Change_Anything()
    {
        var (estateId, manager) = await EstateAsync();
        await Estate().CreateUnitAsync(Unit(12, "100", door: "12"), manager);
        var guard = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Guard));

        (await Estate().ListUnitsAsync(new ListUnitsRequest(), guard)).Items.Should().ContainSingle(u => u.SectionNumber == 12);
        (await StatusOf(() => Estate().CreateUnitAsync(Unit(13, "1"), guard))).Should().Be(StatusCode.PermissionDenied);
    }

    [Fact] // SECURITY.md layer 5
    public async Task Another_Estates_Units_And_Blocks_Are_Invisible_And_Untouchable()
    {
        var (_, managerA) = await EstateAsync();
        var (_, managerB) = await EstateAsync();
        var unitA = await Estate().CreateUnitAsync(Unit(1, "100"), managerA);
        var blockA = await Estate().CreateBlockAsync(new CreateBlockRequest { Name = "A only" }, managerA);

        (await Estate().ListUnitsAsync(new ListUnitsRequest(), managerB)).Items.Should().BeEmpty();
        (await StatusOf(() => Estate().UpdateUnitAsync(new UpdateUnitRequest
        {
            UnitId = unitA.Id,
            Unit = new UnitInput { SectionNumber = 1, ParticipationQuota = "1" },
        }, managerB))).Should().Be(StatusCode.NotFound);
        (await StatusOf(() => Estate().DeleteUnitAsync(new DeleteUnitRequest { UnitId = unitA.Id }, managerB))).Should().Be(StatusCode.NotFound);
        (await GrpcStatus.ErrorOf(() => Estate().CreateUnitAsync(Unit(2, "1", blockId: blockA.Id), managerB).ResponseAsync))!
            .Status.Detail.Should().Contain("doesn't exist in this estate", "another estate's block can't be used either");
    }
}
