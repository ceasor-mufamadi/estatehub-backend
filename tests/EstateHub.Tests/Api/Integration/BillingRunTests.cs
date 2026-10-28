using System.Text;
using EstateHub.Api;
using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Cron.Jobs;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EstateHub.Tests.Api.Integration;

// Slice 4.2 through the real host: budget split by quota to the cent, flat / per-unit / metered charges,
// draft → preview → post (queued, once per month), blockers and warnings, and who may post.
public class BillingRunTests : IClassFixture<EstateHubApiFactory>
{
    private static readonly TimeZoneInfo SouthAfrica = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");
    private readonly EstateHubApiFactory _factory;

    public BillingRunTests(EstateHubApiFactory factory) => _factory = factory;

    private BillingRunsService.BillingRunsServiceClient Runs() => new(_factory.CreateGrpcChannel());
    private BillingService.BillingServiceClient Billing() => new(_factory.CreateGrpcChannel());
    private AccountsService.AccountsServiceClient Accounts() => new(_factory.CreateGrpcChannel());
    private EstateService.EstateServiceClient Estate() => new(_factory.CreateGrpcChannel());
    private PeopleService.PeopleServiceClient People() => new(_factory.CreateGrpcChannel());

    private static string Label() => $"r-{Guid.NewGuid():N}"[..20];
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_factory.Clock.GetUtcNow().UtcDateTime, SouthAfrica));
    private string ThisMonth => Today.ToString("yyyy-MM");
    private string LastMonth => Today.AddMonths(-1).ToString("yyyy-MM");

    private sealed record Setup(Guid EstateId, Metadata Manager, string[] Units, string WaterId);

    /// <summary>
    /// Three units (quotas 33.3333 / 33.3333 / 33.3334, doors 001–003); sections 1 and 2 owned, 3 has no owner.
    /// Budget R 1000 admin + R 200 reserve; CSOS R 37.50 for everyone; extra parking R 150 on section 1; water metered.
    /// </summary>
    private async Task<Setup> EstateAsync()
    {
        var estateId = await _factory.CreateEstateAsync($"Run {Guid.NewGuid():N}");
        var manager = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager));
        var quotas = new[] { "33.3333", "33.3333", "33.3334" };
        var units = new string[3];
        for (var i = 0; i < 3; i++)
            units[i] = (await Estate().CreateUnitAsync(new CreateUnitRequest { Unit = new UnitInput { SectionNumber = i + 1, DoorNumber = $"00{i + 1}", ParticipationQuota = quotas[i] } }, manager)).Id;
        for (var i = 0; i < 2; i++)
            await People().AddOwnerAsync(new AddOwnerRequest
            {
                UnitId = units[i], Person = new PersonRef { NewPerson = new PersonInput { Name = $"Owner {i + 1}", Email = $"o{i}-{Guid.NewGuid():N}@example.co.za" } },
                SharePercent = "100", FromDate = "2020-01-01",
            }, manager);

        await Runs().AddBudgetAsync(new AddBudgetRequest { EffectiveFrom = "2020-01", AdminFundMonthly = "1000", ReserveFundMonthly = "200" }, manager);
        ChargeTypeInput Charge(string name, string method, string amount = "") => new() { Name = name, Method = method, Amount = amount, IsActive = true, SortOrder = 1 };
        await Billing().CreateChargeTypeAsync(new CreateChargeTypeRequest { Charge = Charge("CSOS", "flat", "37.50") }, manager);
        var parking = await Billing().CreateChargeTypeAsync(new CreateChargeTypeRequest { Charge = Charge("Extra parking", "per_unit") }, manager);
        await Billing().SetUnitChargesAsync(new SetUnitChargesRequest { ChargeTypeId = parking.Id, Amounts = { new UnitChargeAmount { UnitId = units[0], Amount = "150" } } }, manager);
        var water = await Billing().CreateChargeTypeAsync(new CreateChargeTypeRequest { Charge = Charge("Water", "metered") }, manager);
        return new Setup(estateId, manager, units, water.Id);
    }

    private static UploadMeteredRequest Water(string period, string waterId, params string[] rows) => new()
    {
        Period = period, ChargeTypeId = waterId, FileName = "water.csv",
        Content = ByteString.CopyFrom(Encoding.UTF8.GetBytes(string.Join("\n", ["Unit,Amount,Reading", .. rows]))),
    };

    private async Task<BillingRun> UntilSettledAsync(string runId, Metadata manager)
    {
        for (var i = 0; i < 200; i++)
        {
            var run = await Runs().GetRunAsync(new GetRunRequest { RunId = runId }, manager);
            if (run.Status is not ("queued" or "posting") && run.Progress is null)
                return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("The run didn't finish.");
    }

    [Fact]
    public async Task The_Draft_Shows_Every_Charge_With_The_Levy_Split_To_The_Cent()
    {
        var s = await EstateAsync();
        (await Runs().UploadMeteredAsync(Water(ThisMonth, s.WaterId, "001,123.45,W-11", "2,210.10"), s.Manager)).Saved.Should().BeTrue();

        var run = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager);
        var preview = await Runs().PreviewRunAsync(new PreviewRunRequest { RunId = run.Id }, s.Manager);

        preview.Blockers.Should().BeEmpty();
        preview.Accounts.Select(a => a.Number).Should().Equal("001", "002");
        preview.Accounts[0].Lines.Select(l => (l.Charge, l.Amount, l.Reference)).Should().Equal(
            ("Levy (admin fund)", "333.33", ""), ("Levy (reserve fund)", "66.67", ""), ("CSOS", "37.50", ""), ("Extra parking", "150.00", ""), ("Water", "123.45", "W-11"));
        preview.Accounts[0].Total.Should().Be("710.95");
        preview.Accounts[1].Total.Should().Be("647.60"); // 333.33 + 66.67 + 37.50 + 210.10
        preview.Warnings.Should().Contain(w => w.Contains("No owner") && w.Contains("003") && w.Contains("R 437.50"),
            "section 3 (largest quota) absorbs the rounding: 333.34 + 66.66 + 37.50 isn't billed");
        preview.GrandTotal.Should().Be("1358.55");
    }

    [Fact]
    public async Task Posting_Writes_The_Entries_Once_And_A_Month_Is_Posted_Only_Once()
    {
        var s = await EstateAsync();
        var run = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager);
        var preview = await Runs().PreviewRunAsync(new PreviewRunRequest { RunId = run.Id }, s.Manager);

        await Runs().PostRunAsync(new PostRunRequest { RunId = run.Id }, s.Manager);
        var posted = await UntilSettledAsync(run.Id, s.Manager);

        posted.Should().BeEquivalentTo(new { Status = "posted", AccountsPosted = 2, TotalPosted = preview.GrandTotal }, o => o.ExcludingMissingMembers());
        var account = await Accounts().GetAccountAsync(new GetAccountRequest { AccountId = preview.Accounts[0].AccountId }, s.Manager);
        account.Account.Balance.Should().Be(preview.Accounts[0].Total);
        account.Entries.Should().OnlyContain(e => e.Reference == ThisMonth && e.Date == posted.BillingDate);

        (await GrpcStatus.ErrorOf(() => Runs().PostRunAsync(new PostRunRequest { RunId = run.Id }, s.Manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already been posted");
        (await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager)).Id.Should().Be(run.Id, "one run per month");
        (await GrpcStatus.ErrorOf(() => Runs().UploadMeteredAsync(Water(ThisMonth, s.WaterId, "001,10"), s.Manager).ResponseAsync))!
            .Status.Detail.Should().Contain("has been posted");
    }

    [Fact]
    public async Task Quotas_That_Dont_Add_Up_And_Future_Months_Block_Posting()
    {
        var s = await EstateAsync();
        await Estate().UpdateUnitAsync(new UpdateUnitRequest { UnitId = s.Units[2], Unit = new UnitInput { SectionNumber = 3, DoorNumber = "003", ParticipationQuota = "30" } }, s.Manager);
        var run = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager);

        (await Runs().PreviewRunAsync(new PreviewRunRequest { RunId = run.Id }, s.Manager)).Blockers.Should().ContainSingle(b => b.Contains("96.6666%, not 100%"));
        (await GrpcStatus.ErrorOf(() => Runs().PostRunAsync(new PostRunRequest { RunId = run.Id }, s.Manager).ResponseAsync))!
            .StatusCode.Should().Be(StatusCode.InvalidArgument);

        var future = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = Today.AddMonths(1).ToString("yyyy-MM") }, s.Manager);
        (await Runs().PreviewRunAsync(new PreviewRunRequest { RunId = future.Id }, s.Manager)).Blockers.Should().Contain(b => b.Contains("can be posted from its billing date"));
    }

    [Fact]
    public async Task A_Metered_File_With_Problems_Saves_Nothing_And_A_Good_One_Replaces_The_Last()
    {
        var s = await EstateAsync();

        var bad = await Runs().UploadMeteredAsync(Water(ThisMonth, s.WaterId, "001,abc", "099,10", "002,5", "2,6"), s.Manager);
        bad.Saved.Should().BeFalse();
        bad.Problems.Should().HaveCount(3).And.Contain(p => p.Contains("'abc'")).And.Contain(p => p.Contains("no unit '099'")).And.Contain(p => p.Contains("also on row"));
        (await Runs().ListMeteredAsync(new ListMeteredRequest { Period = ThisMonth }, s.Manager)).Items.Single().Units.Should().Be(0);

        await Runs().UploadMeteredAsync(Water(ThisMonth, s.WaterId, "001,100", "002,50"), s.Manager);
        await Runs().UploadMeteredAsync(Water(ThisMonth, s.WaterId, "001,120"), s.Manager);

        var summary = (await Runs().ListMeteredAsync(new ListMeteredRequest { Period = ThisMonth }, s.Manager)).Items.Single();
        (summary.Units, summary.Total).Should().Be((1, "120.00"));
        var run = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager);
        (await Runs().PreviewRunAsync(new PreviewRunRequest { RunId = run.Id }, s.Manager)).Warnings.Should().Contain(w => w.StartsWith("Water: no amount uploaded for 002"));
    }

    [Fact]
    public async Task A_Big_Change_Since_Last_Month_Is_Flagged()
    {
        var s = await EstateAsync();
        var last = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = LastMonth }, s.Manager);
        await Runs().PostRunAsync(new PostRunRequest { RunId = last.Id }, s.Manager);
        (await UntilSettledAsync(last.Id, s.Manager)).Status.Should().Be("posted", "an earlier month can still be posted");

        await Runs().UploadMeteredAsync(Water(ThisMonth, s.WaterId, "001,2000", "002,10"), s.Manager);
        var run = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager);
        var preview = await Runs().PreviewRunAsync(new PreviewRunRequest { RunId = run.Id }, s.Manager);

        preview.Accounts[0].PreviousTotal.Should().Be("587.50");
        preview.Accounts[0].Flags.Should().ContainSingle(f => f.Contains("more than half"));
        preview.Accounts[1].Flags.Should().BeEmpty();
    }

    [Fact] // found by the Postgres smoke test: owners from today aren't billed for this month, and the warning says why
    public async Task An_Owner_Starting_After_The_Billing_Date_Is_Billed_From_Next_Month()
    {
        var s = await EstateAsync();
        if (Today.Day == 1)
            return; // the billing date is today: they'd be billed
        var unowned = s.Units[2];
        await People().AddOwnerAsync(new AddOwnerRequest
        {
            UnitId = unowned, Person = new PersonRef { NewPerson = new PersonInput { Name = "New buyer", Email = $"b-{Guid.NewGuid():N}@example.co.za" } },
            SharePercent = "100", FromDate = Today.ToString("yyyy-MM-dd"),
        }, s.Manager);
        var run = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager);

        var preview = await Runs().PreviewRunAsync(new PreviewRunRequest { RunId = run.Id }, s.Manager);

        preview.Accounts.Should().HaveCount(2, "the buyer isn't billed for the month they arrived in");
        preview.Warnings.Should().Contain(w => w.StartsWith($"Owner only from {Today:yyyy-MM-dd} for 003") && w.Contains("billed from next month"));
        preview.Warnings.Should().NotContain(w => w.StartsWith("No owner"));
    }

    [Fact]
    public async Task Budgets_Have_Rules()
    {
        var s = await EstateAsync();

        (await GrpcStatus.ErrorOf(() => Runs().AddBudgetAsync(new AddBudgetRequest { EffectiveFrom = "2020-01", AdminFundMonthly = "1" }, s.Manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already a budget");
        var run = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager);
        await Runs().PostRunAsync(new PostRunRequest { RunId = run.Id }, s.Manager);
        await UntilSettledAsync(run.Id, s.Manager);

        var budget = (await Runs().ListBudgetsAsync(new ListBudgetsRequest(), s.Manager)).Items.Single();
        budget.Used.Should().BeTrue();
        (await GrpcStatus.ErrorOf(() => Runs().RemoveBudgetAsync(new RemoveBudgetRequest { BudgetId = budget.Id }, s.Manager).ResponseAsync))!
            .Status.Detail.Should().Contain("stays as history");
        (await GrpcStatus.ErrorOf(() => Runs().AddBudgetAsync(new AddBudgetRequest { EffectiveFrom = ThisMonth, AdminFundMonthly = "1" }, s.Manager).ResponseAsync))!
            .Status.Detail.Should().Contain("has been posted already");
    }

    [Fact] // AUTH-12: posting needs a fresh MFA; trustees review but don't post
    public async Task Posting_Needs_A_Fresh_Mfa_And_The_Post_Permission()
    {
        var s = await EstateAsync();
        var run = await Runs().CreateDraftAsync(new CreateDraftRequest { Period = ThisMonth }, s.Manager);
        var stale = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), s.EstateId, Roles.Manager, mfa: false));
        var trustee = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), s.EstateId, Roles.Trustee));

        (await GrpcStatus.ErrorOf(() => Runs().PostRunAsync(new PostRunRequest { RunId = run.Id }, stale).ResponseAsync))!
            .Trailers.GetValue(RecentMfaInterceptor.StepUpTrailer).Should().Be("required");
        (await Runs().PreviewRunAsync(new PreviewRunRequest { RunId = run.Id }, trustee)).Accounts.Should().HaveCount(2);
        (await GrpcStatus.Of(() => Runs().PostRunAsync(new PostRunRequest { RunId = run.Id }, trustee).ResponseAsync)).Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task The_Draft_Job_Makes_This_Months_Draft_On_The_Billing_Day_Only_Once()
    {
        var s = await EstateAsync();
        if (Today.Day > 28)
            return; // billing days stop at 28: nothing to make today
        await Billing().UpdateBillingSettingsAsync(new UpdateBillingSettingsRequest
        {
            FinancialYearStartMonth = 3, BillingDay = Today.Day, DueDay = 7, LevyDisplay = "combined", InterestRatePercent = "0",
        }, s.Manager);

        for (var i = 0; i < 2; i++)
        {
            using var scope = _factory.Services.CreateScope();
            await ActivatorUtilities.CreateInstance<LevyBillingDraftJob>(scope.ServiceProvider).ExecuteAsync(CancellationToken.None);
        }

        (await Runs().ListRunsAsync(new ListRunsRequest(), s.Manager)).Items.Should().ContainSingle(r => r.Period == ThisMonth && r.Status == "draft");
    }
}
