using EstateHub.Api;
using EstateHub.Application.Auth;
using FluentAssertions;
using Grpc.Core;

namespace EstateHub.Tests.Api.Integration;

// Slice 3.2 through the real host: people, ownership, occupancy, inviting from a unit, and residents
// seeing only their own units.
public class PeopleTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public PeopleTests(EstateHubApiFactory factory)
    {
        _factory = factory;
        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(61); // fresh hourly code caps per test
    }

    private PeopleService.PeopleServiceClient People() => new(_factory.CreateGrpcChannel());
    private EstateService.EstateServiceClient Estate() => new(_factory.CreateGrpcChannel());
    private AuthService.AuthServiceClient Auth() => new(_factory.CreateGrpcChannel());

    private static string Label() => $"p-{Guid.NewGuid():N}"[..20];
    private static string NewEmail() => $"person-{Guid.NewGuid():N}@example.co.za";

    private async Task<(Guid EstateId, Metadata Manager, string Unit1, string Unit2)> EstateWithUnitsAsync()
    {
        var estateId = await _factory.CreateEstateAsync($"People {Guid.NewGuid():N}");
        var manager = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager));
        var u1 = await Estate().CreateUnitAsync(new CreateUnitRequest { Unit = new UnitInput { SectionNumber = 1, ParticipationQuota = "50" } }, manager);
        var u2 = await Estate().CreateUnitAsync(new CreateUnitRequest { Unit = new UnitInput { SectionNumber = 2, ParticipationQuota = "50" } }, manager);
        return (estateId, manager, u1.Id, u2.Id);
    }

    private static PersonRef New(string name, string email) => new() { NewPerson = new PersonInput { Name = name, Email = email } };

    /// <summary>Invite the person and accept as they would: returns their bearer token.</summary>
    private async Task<Metadata> InviteAndAcceptAsync(Metadata manager, string personId, string email)
    {
        await People().InvitePersonAsync(new InvitePersonRequest { PersonId = personId }, manager);
        var otp = await Auth().StartInvitationAsync(new StartInvitationRequest { InvitationToken = _factory.Outbox.InvitationTokenFor(email) });
        var accepted = await Auth().AcceptInvitationAsync(new AcceptInvitationRequest { ChallengeToken = otp.ChallengeToken, Code = _factory.Outbox.CodeFor(email) });
        return EstateHubApiFactory.Bearer(accepted.SignedIn.AccessToken);
    }

    private async Task<StatusCode> StatusOf<T>(Func<AsyncUnaryCall<T>> call) => await GrpcStatus.Of(() => call().ResponseAsync);

    [Fact]
    public async Task Owners_Shares_Never_Exceed_100_Percent_And_A_Sale_Keeps_The_History()
    {
        var (_, manager, unit, _) = await EstateWithUnitsAsync();

        var detail = await People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit, Person = New("Seller", NewEmail()), SharePercent = "100", FromDate = "2020-01-01" }, manager);
        detail.CurrentOwnerShare.Should().Be("100.00");

        (await GrpcStatus.ErrorOf(() => People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit, Person = New("Too many", NewEmail()), SharePercent = "10" }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("more than 100%");

        var sold = await People().EndOwnershipAsync(new EndOwnershipRequest { OwnershipId = detail.Owners[0].Id, ToDate = "2026-01-31" }, manager);
        var bought = await People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit, Person = New("Buyer", NewEmail()), SharePercent = "100", FromDate = "2026-02-01" }, manager);

        sold.CurrentOwnerShare.Should().Be("0.00");
        bought.Owners.Select(o => (o.Person.Name, o.Current, o.ToDate)).Should().BeEquivalentTo(new[] { ("Buyer", true, ""), ("Seller", false, "2026-01-31") });
    }

    [Fact]
    public async Task Only_A_Current_Owner_Can_Be_An_Owner_Occupier()
    {
        var (_, manager, unit, _) = await EstateWithUnitsAsync();
        var stranger = await People().CreatePersonAsync(new CreatePersonRequest { Person = new PersonInput { Name = "Stranger", Email = NewEmail() } }, manager);

        (await GrpcStatus.ErrorOf(() => People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit, Person = new PersonRef { PersonId = stranger.Id }, Kind = "owner_occupier" }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("isn't an owner");

        var withTenant = await People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit, Person = new PersonRef { PersonId = stranger.Id }, Kind = "tenant" }, manager);
        withTenant.Occupants.Should().ContainSingle(o => o.Person.Name == "Stranger" && o.Kind == "tenant" && o.Current);
    }

    [Fact] // found in hand testing 2026-10-28: a seller ending today and a buyer from today made 180%
    public async Task A_Seller_Whose_Last_Day_Is_Today_And_A_Buyer_From_Today_Cant_Overlap()
    {
        var (_, manager, unit, _) = await EstateWithUnitsAsync();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_factory.Clock.GetUtcNow().UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg")));
        var seller = await People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit, Person = New("Olivia", NewEmail()), SharePercent = "80", FromDate = "2020-01-01" }, manager);
        await People().EndOwnershipAsync(new EndOwnershipRequest { OwnershipId = seller.Owners[0].Id, ToDate = today.ToString("yyyy-MM-dd") }, manager);
        var buyerEmail = NewEmail();

        var error = await GrpcStatus.ErrorOf(() => People().AddOwnerAsync(new AddOwnerRequest
        {
            UnitId = unit, Person = New("Pfunzo", buyerEmail), SharePercent = "100", FromDate = today.ToString("yyyy-MM-dd"),
        }, manager).ResponseAsync);

        error!.Status.Detail.Should().Contain($"Olivia still owns 80% until {today:yyyy-MM-dd}").And.Contain($"Start the new owner on {today.AddDays(1):yyyy-MM-dd}");
        (await People().ListPeopleAsync(new ListPeopleRequest { Search = "Pfunzo" }, manager)).Items.Should().BeEmpty("a refused owner isn't half-created");

        var bought = await People().AddOwnerAsync(new AddOwnerRequest
        {
            UnitId = unit, Person = New("Pfunzo", buyerEmail), SharePercent = "100", FromDate = today.AddDays(1).ToString("yyyy-MM-dd"),
        }, manager);
        bought.Owners.Should().HaveCount(2);
    }

    [Fact] // found in hand testing 2026-10-28: an owner leaving today who stays on as tenant was invited as owner
    public async Task The_Invitation_Role_Follows_What_Carries_On()
    {
        var (_, manager, unit, _) = await EstateWithUnitsAsync();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_factory.Clock.GetUtcNow().UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg")));
        var email = NewEmail();
        var detail = await People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit, Person = New("Olivia", email), SharePercent = "100", FromDate = "2020-01-01" }, manager);
        var olivia = detail.Owners[0].Person.Id;
        await People().EndOwnershipAsync(new EndOwnershipRequest { OwnershipId = detail.Owners[0].Id, ToDate = today.ToString("yyyy-MM-dd") }, manager);
        await People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit, Person = new PersonRef { PersonId = olivia }, Kind = "tenant", FromDate = today.AddDays(1).ToString("yyyy-MM-dd") }, manager);

        await People().InvitePersonAsync(new InvitePersonRequest { PersonId = olivia }, manager);

        _factory.Outbox.LastTo(email).Should().Contain("as tenant");
    }

    [Fact] // owners may hold several units; anyone lives in only one at a time
    public async Task An_Owner_Can_Own_Several_Units_But_A_Person_Lives_In_Only_One()
    {
        var (_, manager, unit1, unit2) = await EstateWithUnitsAsync();
        var owner = await People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit1, Person = New("Investor", NewEmail()), SharePercent = "100" }, manager);
        var investor = owner.Owners[0].Person.Id;

        (await StatusOf(() => People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit2, Person = new PersonRef { PersonId = investor }, SharePercent = "100" }, manager)))
            .Should().Be(StatusCode.OK, "a second unit is fine to own");
        await People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit1, Person = new PersonRef { PersonId = investor }, Kind = "owner_occupier" }, manager);
        (await GrpcStatus.ErrorOf(() => People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit2, Person = new PersonRef { PersonId = investor }, Kind = "owner_occupier" }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already lives in Section 1");

        var tenantDetail = await People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit2, Person = New("Renter", NewEmail()), Kind = "tenant", FromDate = "2024-01-01" }, manager);
        var renter = tenantDetail.Occupants[0];
        (await GrpcStatus.ErrorOf(() => People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit1, Person = new PersonRef { PersonId = renter.Person.Id }, Kind = "tenant" }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("only one unit at a time");

        await People().EndOccupancyAsync(new EndOccupancyRequest { OccupancyId = renter.Id, ToDate = "2025-06-30" }, manager);
        (await StatusOf(() => People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit1, Person = new PersonRef { PersonId = renter.Person.Id }, Kind = "tenant", FromDate = "2025-07-01" }, manager)))
            .Should().Be(StatusCode.OK, "after moving out they can rent another unit");
    }

    [Fact]
    public async Task Each_Email_And_Mobile_Belongs_To_One_Person_Per_Estate()
    {
        var (_, manager, _, _) = await EstateWithUnitsAsync();
        var email = NewEmail();
        await People().CreatePersonAsync(new CreatePersonRequest { Person = new PersonInput { Name = "First", Email = email } }, manager);

        (await GrpcStatus.ErrorOf(() => People().CreatePersonAsync(new CreatePersonRequest { Person = new PersonInput { Name = "Second", Email = email.ToUpperInvariant() } }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already has the email");
    }

    // ---------- residents: their own units, nothing else ----------

    [Fact] // SECURITY.md layer 4: privacy between neighbours
    public async Task An_Invited_Owner_Sees_Their_Unit_And_Nothing_Else()
    {
        var (_, manager, unit1, unit2) = await EstateWithUnitsAsync();
        var email = NewEmail();
        var detail = await People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit1, Person = New("Owner One", email), SharePercent = "100" }, manager);
        await People().AddOwnerAsync(new AddOwnerRequest { UnitId = unit2, Person = New("Neighbour", NewEmail()), SharePercent = "100" }, manager);

        var owner = await InviteAndAcceptAsync(manager, detail.Owners[0].Person.Id, email);

        var mine = await People().MyUnitsAsync(new MyUnitsRequest(), owner);
        mine.Items.Should().ContainSingle(u => u.UnitId == unit1 && u.Relationship == "owner");
        (await People().GetMyUnitAsync(new GetMyUnitRequest { UnitId = unit1 }, owner)).SectionNumber.Should().Be(1);

        (await StatusOf(() => People().GetMyUnitAsync(new GetMyUnitRequest { UnitId = unit2 }, owner))).Should().Be(StatusCode.NotFound, "the neighbour's unit isn't even confirmed to exist");
        (await StatusOf(() => People().GetUnitAsync(new GetUnitRequest { UnitId = unit1 }, owner))).Should().Be(StatusCode.PermissionDenied, "owners and occupants of a unit are staff-only");
        (await StatusOf(() => People().ListPeopleAsync(new ListPeopleRequest(), owner))).Should().Be(StatusCode.PermissionDenied);
        (await StatusOf(() => Estate().ListUnitsAsync(new ListUnitsRequest(), owner))).Should().Be(StatusCode.PermissionDenied);

        var person = (await People().ListPeopleAsync(new ListPeopleRequest { Search = "Owner One" }, manager)).Items.Single();
        person.Person.HasAccount.Should().BeTrue("accepting linked the account to the person record");
    }

    [Fact]
    public async Task A_Tenant_Sees_Only_The_Unit_They_Rent_Until_They_Move_Out()
    {
        var (_, manager, unit1, _) = await EstateWithUnitsAsync();
        var email = NewEmail();
        var detail = await People().AddOccupantAsync(new AddOccupantRequest { UnitId = unit1, Person = New("Tenant", email), Kind = "tenant", FromDate = "2019-03-01" }, manager);
        var tenant = await InviteAndAcceptAsync(manager, detail.Occupants[0].Person.Id, email);

        (await People().MyUnitsAsync(new MyUnitsRequest(), tenant)).Items.Should().ContainSingle(u => u.UnitId == unit1 && u.Relationship == "tenant");

        await People().EndOccupancyAsync(new EndOccupancyRequest { OccupancyId = detail.Occupants[0].Id, ToDate = "2020-01-31" }, manager);

        (await People().MyUnitsAsync(new MyUnitsRequest(), tenant)).Items.Should().BeEmpty("moved out");
        (await StatusOf(() => People().GetMyUnitAsync(new GetMyUnitRequest { UnitId = unit1 }, tenant))).Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task Someone_Who_Already_Has_An_Account_Here_Is_Linked_Without_An_Invitation()
    {
        var (estateId, manager, unit1, _) = await EstateWithUnitsAsync();
        var label = Label();
        await _factory.TokenAsync(label, estateId, Roles.Owner); // an existing member: label@dev.estatehub.local
        var detail = await People().AddOwnerAsync(new AddOwnerRequest
        {
            UnitId = unit1, Person = New("Existing", $"{label}@dev.estatehub.local"), SharePercent = "100",
        }, manager);

        var result = await People().InvitePersonAsync(new InvitePersonRequest { PersonId = detail.Owners[0].Person.Id }, manager);

        result.Linked.Should().BeTrue();
        result.Invited.Should().BeFalse();
        var owner = EstateHubApiFactory.Bearer(await _factory.TokenAsync(label, estateId, Roles.Owner));
        (await People().MyUnitsAsync(new MyUnitsRequest(), owner)).Items.Should().ContainSingle(u => u.UnitId == unit1);
    }

    [Fact]
    public async Task A_Person_Without_A_Unit_Or_Contact_Details_Cant_Be_Invited()
    {
        var (_, manager, _, _) = await EstateWithUnitsAsync();
        var noUnit = await People().CreatePersonAsync(new CreatePersonRequest { Person = new PersonInput { Name = "No unit", Email = NewEmail() } }, manager);
        var noContact = await People().CreatePersonAsync(new CreatePersonRequest { Person = new PersonInput { Name = "No contact" } }, manager);

        (await GrpcStatus.ErrorOf(() => People().InvitePersonAsync(new InvitePersonRequest { PersonId = noUnit.Id }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("owner or occupant of a unit first");
        (await GrpcStatus.ErrorOf(() => People().InvitePersonAsync(new InvitePersonRequest { PersonId = noContact.Id }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("email address or mobile number");
    }

    [Theory]
    [InlineData(Roles.Guard)]
    [InlineData(Roles.Owner)]
    public async Task Guards_And_Residents_Never_See_The_Estates_People(string role)
    {
        var (estateId, _, _, _) = await EstateWithUnitsAsync();
        var caller = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, role));

        (await StatusOf(() => People().ListPeopleAsync(new ListPeopleRequest(), caller))).Should().Be(StatusCode.PermissionDenied);
    }

    [Fact] // SECURITY.md layer 5
    public async Task Another_Estates_Units_And_People_Are_Out_Of_Reach()
    {
        var (_, managerA, unitA, _) = await EstateWithUnitsAsync();
        var (_, managerB, _, _) = await EstateWithUnitsAsync();
        var a = await People().AddOwnerAsync(new AddOwnerRequest { UnitId = unitA, Person = New("A owner", NewEmail()), SharePercent = "100" }, managerA);

        (await StatusOf(() => People().GetUnitAsync(new GetUnitRequest { UnitId = unitA }, managerB))).Should().Be(StatusCode.NotFound);
        (await StatusOf(() => People().AddOwnerAsync(new AddOwnerRequest { UnitId = unitA, Person = New("Sneaky", NewEmail()) }, managerB))).Should().Be(StatusCode.NotFound);
        (await StatusOf(() => People().EndOwnershipAsync(new EndOwnershipRequest { OwnershipId = a.Owners[0].Id, ToDate = "2026-01-01" }, managerB))).Should().Be(StatusCode.NotFound);
        (await People().ListPeopleAsync(new ListPeopleRequest(), managerB)).Items.Should().NotContain(p => p.Person.Name == "A owner");
    }
}

/// <summary>
/// ResourceKind.Unit on its own (other RPCs, e.g. statements in Sprint 4, will rely on it alone):
/// staff see every unit of their estate; residents only units they own or live in today.
/// </summary>
public class UnitAccessRuleTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public UnitAccessRuleTests(EstateHubApiFactory factory) => _factory = factory;

    private sealed class Caller(string userId, params string[] permissions) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public string? UserId => userId;
        public Guid? SessionId => null;
        public Guid? EstateId => null;
        public string? Role => null;
        public IReadOnlySet<string> Permissions { get; } = permissions.ToHashSet();
        public IReadOnlySet<string> AuthMethods { get; } = new HashSet<string>();
        public DateTimeOffset? AuthTime => null;
        public bool HasPermission(string permission) => Permissions.Contains(permission);
    }

    [Fact]
    public async Task Residents_Pass_Only_For_Units_They_Own_Or_Live_In_Today()
    {
        var estateId = await _factory.CreateEstateAsync($"Rule {Guid.NewGuid():N}");
        var owner = Guid.NewGuid();
        Guid mine = Guid.NewGuid(), neighbours = Guid.NewGuid(), sold = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            db.Users.Add(new EstateHub.Domain.Entities.User { Id = owner, Email = $"{owner:N}@example.co.za", DisplayName = "O" });
            foreach (var (id, section) in new[] { (mine, 1), (neighbours, 2), (sold, 3) })
                db.Units.Add(new EstateHub.Domain.Entities.Unit { Id = id, SectionNumber = section, ParticipationQuota = 1 });
            var person = new EstateHub.Domain.Entities.Person { Name = "O", UserId = owner };
            db.People.Add(person);
            db.Ownerships.Add(new EstateHub.Domain.Entities.Ownership { UnitId = mine, PersonId = person.Id, FromDate = new DateOnly(2020, 1, 1) });
            db.Ownerships.Add(new EstateHub.Domain.Entities.Ownership { UnitId = sold, PersonId = person.Id, FromDate = new DateOnly(2020, 1, 1), ToDate = new DateOnly(2021, 1, 1) });
            await db.SaveChangesAsync();
        }, estateId);

        bool Can(ICurrentUser caller, Guid unit) => Check(caller, unit, estateId).GetAwaiter().GetResult();

        var resident = new Caller(owner.ToString());
        Can(resident, mine).Should().BeTrue();
        Can(resident, neighbours).Should().BeFalse("a neighbour's unit");
        Can(resident, sold).Should().BeFalse("a unit they sold");

        var staff = new Caller(Guid.NewGuid().ToString(), Permissions.EstateRead);
        Can(staff, neighbours).Should().BeTrue("staff see every unit of their estate");
    }

    private async Task<bool> Check(ICurrentUser caller, Guid unitId, Guid estateId)
    {
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(_factory.Services);
        var services = scope.ServiceProvider;
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<EstateHub.Application.Tenancy.ITenantContext>(services).Use(estateId);
        var access = new EstateHub.Infrastructure.Auth.ResourceAccess(
            caller,
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<EstateHub.Infrastructure.Data.EstateHubDbContext>(services),
            _factory.Clock);
        return await access.CanAccessAsync(ResourceKind.Unit, unitId.ToString());
    }
}
