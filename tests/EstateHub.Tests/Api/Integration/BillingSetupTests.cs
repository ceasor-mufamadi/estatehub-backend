using EstateHub.Api;
using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.Features;
using IEstateService = EstateHub.Application.Interfaces.IEstateService;
using FluentAssertions;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EstateHub.Tests.Api.Integration;

// Slice 4.1 through the real host: billing settings, bank details (MFA + trustees told), charge types,
// accounts that follow ownership (one per ownership period), opening balances and reversals, and who sees what.
public class BillingSetupTests : IClassFixture<EstateHubApiFactory>
{
    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");
    private readonly EstateHubApiFactory _factory;

    public BillingSetupTests(EstateHubApiFactory factory) => _factory = factory;

    private BillingService.BillingServiceClient Billing() => new(_factory.CreateGrpcChannel());
    private AccountsService.AccountsServiceClient Accounts() => new(_factory.CreateGrpcChannel());
    private EstateService.EstateServiceClient Estate() => new(_factory.CreateGrpcChannel());
    private PeopleService.PeopleServiceClient People() => new(_factory.CreateGrpcChannel());

    private static string Label() => $"b-{Guid.NewGuid():N}"[..20];
    private static string Email(string name) => $"{name}-{Guid.NewGuid():N}@example.co.za";
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_factory.Clock.GetUtcNow().UtcDateTime, SouthAfrica));
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");

    private async Task<(Guid EstateId, Metadata Manager)> EstateAsync()
    {
        var estateId = await _factory.CreateEstateAsync($"Billing {Guid.NewGuid():N}");
        return (estateId, EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager)));
    }

    private async Task<(string UnitId, string OwnershipId)> UnitWithOwnerAsync(Metadata manager, int section, string name, string? email = null, string from = "2020-01-01")
    {
        var unit = await Estate().CreateUnitAsync(new CreateUnitRequest { Unit = new UnitInput { SectionNumber = section, ParticipationQuota = "1", DoorNumber = $"00{section}" } }, manager);
        var detail = await People().AddOwnerAsync(new AddOwnerRequest
        {
            UnitId = unit.Id, Person = new PersonRef { NewPerson = new PersonInput { Name = name, Email = email ?? Email("owner") } }, SharePercent = "100", FromDate = from,
        }, manager);
        return (unit.Id, detail.Owners[0].Id);
    }

    // ---------- settings ----------

    [Fact]
    public async Task Settings_Start_With_Defaults_And_The_Levy_Built_In()
    {
        var (_, manager) = await EstateAsync();

        (await Billing().GetBillingSettingsAsync(new GetBillingSettingsRequest(), manager)).Should().BeEquivalentTo(
            new { BillingDay = 1, DueDay = 7, LevyDisplay = "combined", InterestEnabled = false, FinancialYearStartMonth = 3 }, o => o.ExcludingMissingMembers());
        (await Billing().ListChargeTypesAsync(new ListChargeTypesRequest(), manager)).Items
            .Should().ContainSingle(c => c.IsLevy && c.Name == "Levy" && c.Method == "quota");
    }

    [Theory]
    [InlineData(30, 7, "combined", false, "0", "between 1 and 28")]
    [InlineData(1, 7, "both", false, "0", "one line or split")]
    [InlineData(1, 7, "combined", true, "0", "Give the yearly interest rate")]
    [InlineData(1, 7, "combined", true, "60", "from 0 to 50")]
    public async Task Settings_Are_Checked(int billingDay, int dueDay, string display, bool interest, string rate, string message)
    {
        var (_, manager) = await EstateAsync();

        var error = await GrpcStatus.ErrorOf(() => Billing().UpdateBillingSettingsAsync(new UpdateBillingSettingsRequest
        {
            FinancialYearStartMonth = 3, BillingDay = billingDay, DueDay = dueDay, LevyDisplay = display, InterestEnabled = interest, InterestRatePercent = rate,
        }, manager).ResponseAsync);

        error!.Status.Detail.Should().Contain(message);
    }

    [Fact] // decided 2026-10-28: changed bank details are the classic fraud
    public async Task Bank_Details_Need_A_Fresh_Mfa_And_Every_Trustee_Is_Told()
    {
        var (estateId, manager) = await EstateAsync();
        var trusteeLabel = Label();
        var trustee = EstateHubApiFactory.Bearer(await _factory.TokenAsync(trusteeLabel, estateId, Roles.Trustee));
        var stale = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager, mfa: false));
        var request = new UpdateBankDetailsRequest { BankName = "Test Bank", BranchCode = "123 456", AccountName = "Test Body Corporate", AccountNumber = "1234 567 890" };

        (await GrpcStatus.ErrorOf(() => Billing().UpdateBankDetailsAsync(request, stale).ResponseAsync))!
            .Trailers.GetValue(RecentMfaInterceptor.StepUpTrailer).Should().Be("required");
        (await GrpcStatus.Of(() => Billing().UpdateBankDetailsAsync(request, trustee).ResponseAsync)).Should().Be(StatusCode.PermissionDenied);

        var saved = await Billing().UpdateBankDetailsAsync(request, manager);

        saved.Should().BeEquivalentTo(new { BankBranchCode = "123456", BankAccountNumber = "1234567890" }, o => o.ExcludingMissingMembers());
        var alert = _factory.Outbox.LastTo($"{trusteeLabel}@dev.estatehub.local");
        alert.Should().Contain("******7890").And.NotContain("1234567890", "alerts only show the last digits");
    }

    // ---------- charge types ----------

    [Fact]
    public async Task Charge_Types_Have_Rules()
    {
        var (_, manager) = await EstateAsync();
        ChargeTypeInput Input(string name, string method, string amount = "") => new() { Name = name, Method = method, Amount = amount, IsActive = true, SortOrder = 1 };

        var csos = await Billing().CreateChargeTypeAsync(new CreateChargeTypeRequest { Charge = Input("CSOS", "flat", "37,50") }, manager);
        csos.Amount.Should().Be("37.50");
        (await GrpcStatus.ErrorOf(() => Billing().CreateChargeTypeAsync(new CreateChargeTypeRequest { Charge = Input("Sanitation", "flat") }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("monthly amount");
        (await GrpcStatus.ErrorOf(() => Billing().CreateChargeTypeAsync(new CreateChargeTypeRequest { Charge = Input("csos", "metered") }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already a charge");
        (await GrpcStatus.ErrorOf(() => Billing().CreateChargeTypeAsync(new CreateChargeTypeRequest { Charge = Input("Second levy", "quota") }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("Only the levy");

        var levy = (await Billing().ListChargeTypesAsync(new ListChargeTypesRequest(), manager)).Items.Single(c => c.IsLevy);
        (await GrpcStatus.ErrorOf(() => Billing().UpdateChargeTypeAsync(new UpdateChargeTypeRequest
        {
            ChargeTypeId = levy.Id, Charge = new ChargeTypeInput { Name = "Levy", Method = "quota", IsActive = false },
        }, manager).ResponseAsync))!.Status.Detail.Should().Contain("can't be switched off");
        (await Billing().UpdateChargeTypeAsync(new UpdateChargeTypeRequest
        {
            ChargeTypeId = levy.Id, Charge = new ChargeTypeInput { Name = "Ordinary levy", Method = "quota", IsActive = true },
        }, manager)).Name.Should().Be("Ordinary levy");
    }

    [Fact]
    public async Task Per_Unit_Amounts_Are_Set_And_Cleared()
    {
        var (_, manager) = await EstateAsync();
        var unit = await Estate().CreateUnitAsync(new CreateUnitRequest { Unit = new UnitInput { SectionNumber = 1, ParticipationQuota = "1" } }, manager);
        var parking = await Billing().CreateChargeTypeAsync(new CreateChargeTypeRequest
        {
            Charge = new ChargeTypeInput { Name = "Extra parking", Method = "per_unit", IsActive = true },
        }, manager);

        var set = await Billing().SetUnitChargesAsync(new SetUnitChargesRequest
        {
            ChargeTypeId = parking.Id, Amounts = { new UnitChargeAmount { UnitId = unit.Id, Amount = "150" } },
        }, manager);
        set.Items.Should().ContainSingle(i => i.UnitId == unit.Id && i.Amount == "150.00");

        var cleared = await Billing().SetUnitChargesAsync(new SetUnitChargesRequest
        {
            ChargeTypeId = parking.Id, Amounts = { new UnitChargeAmount { UnitId = unit.Id, Amount = "0" } },
        }, manager);
        cleared.Items.Single().Amount.Should().Be("0.00");
    }

    // ---------- accounts follow ownership ----------

    [Fact] // decided 2026-10-28: one account per ownership period; arrears stay with the former owner
    public async Task A_Sale_Closes_The_Sellers_Account_With_Its_Balance_And_Opens_One_For_The_Buyer()
    {
        var (_, manager) = await EstateAsync();
        var (unitId, sellerOwnership) = await UnitWithOwnerAsync(manager, 3, "Seller");
        var seller = (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = unitId }, manager)).Items.Single();
        seller.Should().BeEquivalentTo(new { Number = "003", OpenedOn = "2020-01-01", ClosedOn = "", Balance = "0.00" }, o => o.ExcludingMissingMembers());
        await Accounts().RecordOpeningBalanceAsync(new RecordOpeningBalanceRequest { AccountId = seller.Id, Amount = "3700", Date = "2025-01-01" }, manager);

        await People().EndOwnershipAsync(new EndOwnershipRequest { OwnershipId = sellerOwnership, ToDate = D(Today.AddDays(-1)) }, manager);
        await People().AddOwnerAsync(new AddOwnerRequest
        {
            UnitId = unitId, Person = new PersonRef { NewPerson = new PersonInput { Name = "Buyer", Email = Email("buyer") } }, SharePercent = "100", FromDate = D(Today),
        }, manager);

        var accounts = (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = unitId }, manager)).Items;
        accounts.Select(a => (a.Holders.Single(), a.ClosedOn, a.Balance)).Should().Equal(
            ("Buyer", "", "0.00"),
            ("Seller", D(Today.AddDays(-1)), "3700.00"));
        (await Accounts().ListAccountsAsync(new ListAccountsRequest { Filter = "former_owners_owing" }, manager)).Items
            .Should().ContainSingle(a => a.Id == seller.Id);
    }

    [Fact]
    public async Task An_Unused_Account_Follows_Corrections_Instead_Of_Piling_Up()
    {
        var (_, manager) = await EstateAsync();
        var (unitId, wrongOwnership) = await UnitWithOwnerAsync(manager, 4, "Typo Owner");

        await People().RemoveOwnershipAsync(new RemoveOwnershipRequest { OwnershipId = wrongOwnership }, manager);
        (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = unitId }, manager)).Items.Should().BeEmpty();

        await People().AddOwnerAsync(new AddOwnerRequest
        {
            UnitId = unitId, Person = new PersonRef { NewPerson = new PersonInput { Name = "Right Owner", Email = Email("right") } }, SharePercent = "100", FromDate = "2020-01-01",
        }, manager);
        (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = unitId }, manager)).Items
            .Should().ContainSingle(a => a.Holders.Single() == "Right Owner" && a.ClosedOn == "");
    }

    [Fact]
    public async Task One_Opening_Balance_Per_Account_And_Each_Entry_Reversed_At_Most_Once()
    {
        var (_, manager) = await EstateAsync();
        var (unitId, _) = await UnitWithOwnerAsync(manager, 5, "Owner");
        var accountId = (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = unitId }, manager)).Items.Single().Id;

        var opened = await Accounts().RecordOpeningBalanceAsync(new RecordOpeningBalanceRequest { AccountId = accountId, Amount = "1 500,50", Date = "2026-01-01" }, manager);
        opened.Account.Balance.Should().Be("1500.50");
        (await GrpcStatus.ErrorOf(() => Accounts().RecordOpeningBalanceAsync(new RecordOpeningBalanceRequest { AccountId = accountId, Amount = "10", Date = "2026-01-01" }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already has an opening balance");

        var reversed = await Accounts().ReverseEntryAsync(new ReverseEntryRequest { EntryId = opened.Entries[0].Id, Reason = "wrong amount" }, manager);
        reversed.Account.Balance.Should().Be("0.00");
        reversed.Entries.Should().Contain(e => e.Description == "Incorrect Opening balance (wrong amount)" && e.Credit == "1500.50");
        reversed.Entries.Single(e => e.Id == opened.Entries[0].Id).IsReversed.Should().BeTrue();
        (await GrpcStatus.ErrorOf(() => Accounts().ReverseEntryAsync(new ReverseEntryRequest { EntryId = opened.Entries[0].Id }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already been reversed");

        var credit = await Accounts().RecordOpeningBalanceAsync(new RecordOpeningBalanceRequest { AccountId = accountId, Amount = "-200", Date = "2026-01-01" }, manager);
        credit.Account.Balance.Should().Be("-200.00");
        (await Accounts().ListAccountsAsync(new ListAccountsRequest { Filter = "in_credit" }, manager)).Items.Should().ContainSingle(a => a.Id == accountId);
    }

    // ---------- who sees what ----------

    [Fact] // privacy between neighbours; a former owner keeps seeing their own old account
    public async Task Owners_See_Their_Own_Accounts_Also_After_Selling_And_Never_A_Neighbours()
    {
        var (estateId, manager) = await EstateAsync();
        var label = Label();
        var owner = EstateHubApiFactory.Bearer(await _factory.TokenAsync(label, estateId, Roles.Owner));
        var (mine, myOwnership) = await UnitWithOwnerAsync(manager, 6, "Me", $"{label}@dev.estatehub.local");
        await _factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == $"{label}@dev.estatehub.local");
            foreach (var p in await db.People.Where(p => p.Email == user.Email).ToListAsync())
                p.UserId = user.Id;
            await db.SaveChangesAsync();
        }, estateId);
        var (neighbour, _) = await UnitWithOwnerAsync(manager, 7, "Neighbour");
        var myAccount = (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = mine }, manager)).Items.Single().Id;
        var theirAccount = (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = neighbour }, manager)).Items.Single().Id;

        (await Accounts().MyAccountsAsync(new MyAccountsRequest(), owner)).Items.Select(a => a.Id).Should().Equal(myAccount);
        (await GrpcStatus.Of(() => Accounts().GetAccountAsync(new GetAccountRequest { AccountId = theirAccount }, owner).ResponseAsync)).Should().Be(StatusCode.NotFound, "never confirms someone else's account exists");
        (await GrpcStatus.Of(() => Accounts().ListAccountsAsync(new ListAccountsRequest(), owner).ResponseAsync)).Should().Be(StatusCode.PermissionDenied);

        await Accounts().RecordOpeningBalanceAsync(new RecordOpeningBalanceRequest { AccountId = myAccount, Amount = "100", Date = "2026-01-01" }, manager);
        await People().EndOwnershipAsync(new EndOwnershipRequest { OwnershipId = myOwnership, ToDate = D(Today.AddDays(-1)) }, manager);

        (await Accounts().GetAccountAsync(new GetAccountRequest { AccountId = myAccount }, owner)).Account.ClosedOn.Should().NotBeEmpty("my old statement stays mine");
    }

    [Fact]
    public async Task Trustees_Read_But_Dont_Change_And_Guards_See_Nothing()
    {
        var (estateId, manager) = await EstateAsync();
        var (unitId, _) = await UnitWithOwnerAsync(manager, 8, "Owner");
        var accountId = (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = unitId }, manager)).Items.Single().Id;
        var trustee = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Trustee));
        var guard = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Guard));

        (await Accounts().ListAccountsAsync(new ListAccountsRequest(), trustee)).Items.Should().ContainSingle();
        (await GrpcStatus.Of(() => Accounts().RecordOpeningBalanceAsync(new RecordOpeningBalanceRequest { AccountId = accountId, Amount = "1", Date = "2026-01-01" }, trustee).ResponseAsync))
            .Should().Be(StatusCode.PermissionDenied);
        (await GrpcStatus.Of(() => Accounts().GetAccountAsync(new GetAccountRequest { AccountId = accountId }, guard).ResponseAsync)).Should().Be(StatusCode.NotFound, "never confirms someone else's account exists");
        (await GrpcStatus.Of(() => Billing().GetBillingSettingsAsync(new GetBillingSettingsRequest(), guard).ResponseAsync)).Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task With_Billing_Switched_Off_The_Calls_Are_Refused()
    {
        var (estateId, manager) = await EstateAsync();
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IEstateService>().SetFeatureAsync(estateId, FeatureCatalog.Billing, false, "test");

        (await GrpcStatus.Of(() => Accounts().ListAccountsAsync(new ListAccountsRequest(), manager).ResponseAsync)).Should().Be(StatusCode.FailedPrecondition);
        (await GrpcStatus.Of(() => Billing().GetBillingSettingsAsync(new GetBillingSettingsRequest(), manager).ResponseAsync)).Should().Be(StatusCode.FailedPrecondition);
    }

    [Fact] // SECURITY.md layer 5
    public async Task Another_Estates_Accounts_Are_Out_Of_Reach()
    {
        var (_, managerA) = await EstateAsync();
        var (_, managerB) = await EstateAsync();
        var (unitId, _) = await UnitWithOwnerAsync(managerA, 9, "A owner");
        var accountId = (await Accounts().ListUnitAccountsAsync(new ListUnitAccountsRequest { UnitId = unitId }, managerA)).Items.Single().Id;

        (await Accounts().ListAccountsAsync(new ListAccountsRequest(), managerB)).Items.Should().BeEmpty();
        (await GrpcStatus.Of(() => Accounts().GetAccountAsync(new GetAccountRequest { AccountId = accountId }, managerB).ResponseAsync)).Should().Be(StatusCode.NotFound, "never confirms someone else's account exists");
    }

    [Fact]
    public async Task A_Unit_With_History_Cant_Be_Removed()
    {
        var (_, manager) = await EstateAsync();
        var (unitId, _) = await UnitWithOwnerAsync(manager, 10, "Owner");

        (await GrpcStatus.ErrorOf(() => Estate().DeleteUnitAsync(new DeleteUnitRequest { UnitId = unitId }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("can't be removed");
    }
}
