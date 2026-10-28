using System.Text;
using EstateHub.Api;
using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Cron.Jobs;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ImportInviteStatuses = EstateHub.Domain.Entities.ImportInviteStatuses;
using Invitation = EstateHub.Domain.Entities.Invitation;
using MembersService = EstateHub.Application.Services.MembersService;
using TemplateWriter = EstateHub.Infrastructure.Imports.ImportTemplate;

namespace EstateHub.Tests.Api.Integration;

// Slice 3.3 through the real host: template, preview, queued commit with progress, invitations within
// the estate's hourly cap (sent later by the ⏰ job), and who may import.
public class ImportTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public ImportTests(EstateHubApiFactory factory) => _factory = factory;

    private ImportService.ImportServiceClient Imports() => new(_factory.CreateGrpcChannel());
    private EstateService.EstateServiceClient Estate() => new(_factory.CreateGrpcChannel());
    private PeopleService.PeopleServiceClient People() => new(_factory.CreateGrpcChannel());

    private static string Label() => $"i-{Guid.NewGuid():N}"[..20];

    private async Task<(Guid EstateId, Metadata Manager)> EstateAsync()
    {
        var estateId = await _factory.CreateEstateAsync($"Import {Guid.NewGuid():N}");
        return (estateId, EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager)));
    }

    private static string Email(string name) => $"{name}-{Guid.NewGuid():N}@example.co.za";

    private static PreviewImportRequest Csv(params string[] lines) => new()
    {
        FileName = "units.csv",
        Content = ByteString.CopyFrom(Encoding.UTF8.GetBytes(string.Join("\n", ["Section,PQ %,Block,Owner name,Owner email,Owner share %,Tenant name,Tenant email", .. lines]))),
    };

    private async Task<ImportBatch> UntilFinishedAsync(string id, Metadata manager)
    {
        for (var i = 0; i < 200; i++)
        {
            var batch = await Imports().GetImportAsync(new GetImportRequest { ImportId = id }, manager);
            // "done" is set when the transaction commits; the first invitations go out right after (progress shown).
            if (batch.Status is not ("queued" or "running") && batch.Progress is null)
                return batch;
            await Task.Delay(50);
        }
        throw new TimeoutException("The import didn't finish.");
    }

    [Fact]
    public async Task The_Template_Is_An_Excel_Workbook_With_The_Columns()
    {
        var (_, manager) = await EstateAsync();

        var template = await Imports().GetImportTemplateAsync(new GetImportTemplateRequest(), manager);

        template.FileName.Should().Be("estatehub-import.xlsx");
        template.Content.Span[..2].ToArray().Should().Equal((byte)'P', (byte)'K');
    }

    [Fact]
    public async Task A_Good_File_Is_Previewed_Then_Imported_In_One_Go_With_Invitations()
    {
        var (_, manager) = await EstateAsync();
        var (thandi, pieter, sipho) = (Email("thandi"), Email("pieter"), Email("sipho"));
        var file = new PreviewImportRequest
        {
            FileName = "units.xlsx",
            Content = ByteString.CopyFrom(TemplateWriter.Build(
            [
                ["1", "A1", "Block A", "50", "84,5", "Thandi Mokoena", thandi, "", "", "2019-03-01", "yes", "", "", "", ""],
                ["2", "A2", "Block A", "50", "", "Pieter van Wyk", pieter, "", "", "", "no", "Sipho Dlamini", sipho, "", "2024-02-01"],
                ["3", "", "", "", "", "Thandi Mokoena", thandi],
            ])),
        };

        var invalid = await Imports().PreviewImportAsync(file, manager);
        invalid.Status.Should().Be("invalid");
        invalid.Problems.Should().ContainSingle(p => p.Row == 4 && p.Column == "PQ %");
        (await GrpcStatus.ErrorOf(() => Imports().CommitImportAsync(new CommitImportRequest { ImportId = invalid.Id }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("has problems");

        file.Content = ByteString.CopyFrom(TemplateWriter.Build(
        [
            ["1", "A1", "Block A", "50", "84,5", "Thandi Mokoena", thandi, "", "", "2019-03-01", "yes", "", "", "", ""],
            ["2", "A2", "Block A", "50", "", "Pieter van Wyk", pieter, "", "", "", "no", "Sipho Dlamini", sipho, "", "2024-02-01"],
        ]));
        var preview = await Imports().PreviewImportAsync(file, manager);
        preview.Status.Should().Be("ready");
        preview.Summary.Should().BeEquivalentTo(new ImportSummary
        {
            Units = 2, NewBlocks = 1, NewPeople = 3, Ownerships = 2, Occupancies = 2, Invitations = 3, QuotaTotalAfter = "100.0000", QuotaComplete = true,
        });
        preview.Preview.Select(u => u.Owners).Should().Equal("Thandi Mokoena (100%)", "Pieter van Wyk (100%)");
        (await Estate().ListUnitsAsync(new ListUnitsRequest(), manager)).Items.Should().BeEmpty("the preview saves nothing");

        var queued = await Imports().CommitImportAsync(new CommitImportRequest { ImportId = preview.Id, SendInvitations = true }, manager);
        queued.Status.Should().BeOneOf("queued", "running", "done");
        var done = await UntilFinishedAsync(preview.Id, manager);

        done.Status.Should().Be("done");
        done.Invites.Should().BeEquivalentTo(new { Pending = 0, Sent = 3, Failed = 0 }, o => o.ExcludingMissingMembers());
        var units = await Estate().ListUnitsAsync(new ListUnitsRequest(), manager);
        units.Items.Select(u => (u.SectionNumber, u.BlockName, u.SizeSquareMetres)).Should().Equal((1, "Block A", "84.50"), (2, "Block A", ""));
        units.QuotaComplete.Should().BeTrue();
        var unit2 = await People().GetUnitAsync(new GetUnitRequest { UnitId = units.Items[1].Id }, manager);
        unit2.Occupants.Should().ContainSingle(o => o.Person.Name == "Sipho Dlamini" && o.Kind == "tenant" && o.FromDate == "2024-02-01");
        _factory.Outbox.CountTo(sipho).Should().Be(1, "the tenant got a portal invitation");

        (await GrpcStatus.ErrorOf(() => Imports().CommitImportAsync(new CommitImportRequest { ImportId = preview.Id }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already been done");
    }

    [Fact]
    public async Task Progress_Streams_Until_The_Import_Is_Done()
    {
        var (_, manager) = await EstateAsync();
        var preview = await Imports().PreviewImportAsync(Csv(Enumerable.Range(1, 400).Select(i => $"{i},0.25,,Owner {i}").ToArray()), manager);
        await Imports().CommitImportAsync(new CommitImportRequest { ImportId = preview.Id }, manager);

        using var watch = Imports().WatchImport(new WatchImportRequest { ImportId = preview.Id }, manager);
        var batches = new List<ImportBatch>();
        while (await watch.ResponseStream.MoveNext(CancellationToken.None))
            if (watch.ResponseStream.Current.Batch is { } b)
                batches.Add(b);

        batches.Should().NotBeEmpty();
        batches[^1].Status.Should().Be("done", "the stream ends once the import is done");
        (await Estate().ListUnitsAsync(new ListUnitsRequest(), manager)).Items.Should().HaveCount(400);
    }

    [Fact]
    public async Task If_The_Estate_Changed_Since_The_Preview_Nothing_Is_Imported()
    {
        var (_, manager) = await EstateAsync();
        var preview = await Imports().PreviewImportAsync(Csv("1,50", "2,50"), manager);
        await Estate().CreateUnitAsync(new CreateUnitRequest { Unit = new UnitInput { SectionNumber = 2, ParticipationQuota = "10" } }, manager);

        await Imports().CommitImportAsync(new CommitImportRequest { ImportId = preview.Id }, manager);
        var failed = await UntilFinishedAsync(preview.Id, manager);

        failed.Status.Should().Be("failed");
        failed.Problems.Should().Contain(p => p.Message.Contains("Section 2 already exists"));
        (await Estate().ListUnitsAsync(new ListUnitsRequest(), manager)).Items.Select(u => u.SectionNumber).Should().Equal(2);
    }

    [Fact] // decided 2026-10-28: invitations stay within the estate's 100 per hour
    public async Task Invitations_Beyond_The_Hourly_Cap_Wait_For_The_Job()
    {
        var (estateId, manager) = await EstateAsync();
        await _factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < MembersService.MaxInvitationsPerEstatePerHour - 1; i++)
                db.Add(new Invitation { Name = "x", Email = $"x{i}@example.co.za", Role = Roles.Owner, TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(7), LastSentAt = _factory.Clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
        }, estateId);

        var preview = await Imports().PreviewImportAsync(Csv("1,50,,A,a-" + Email("a"), "2,50,,B,b-" + Email("b")), manager);
        await Imports().CommitImportAsync(new CommitImportRequest { ImportId = preview.Id, SendInvitations = true }, manager);
        var done = await UntilFinishedAsync(preview.Id, manager);

        done.Invites.Should().BeEquivalentTo(new { Sent = 1, Pending = 1 }, o => o.ExcludingMissingMembers());
        done.Invites.NextBatchBy.Should().NotBeNull();

        // An hour later the cap has room again: the job sends the rest.
        await _factory.WithDbAsync(async db =>
        {
            foreach (var invitation in await db.Invitations.ToListAsync())
                invitation.LastSentAt = _factory.Clock.GetUtcNow().UtcDateTime.AddHours(-2);
            await db.SaveChangesAsync();
        }, estateId);
        await RunJobAsync();

        (await Imports().GetImportAsync(new GetImportRequest { ImportId = preview.Id }, manager)).Invites
            .Should().BeEquivalentTo(new { Sent = 2, Pending = 0 }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task The_Job_Pauses_If_The_Person_Who_Imported_Has_Lost_Access()
    {
        var (estateId, manager) = await EstateAsync();
        await _factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < MembersService.MaxInvitationsPerEstatePerHour; i++)
                db.Add(new Invitation { Name = "x", Email = $"x{i}@example.co.za", Role = Roles.Owner, TokenHash = Guid.NewGuid().ToString("N"), ExpiresAt = DateTime.UtcNow.AddDays(7), LastSentAt = _factory.Clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
        }, estateId);
        var preview = await Imports().PreviewImportAsync(Csv("1,100,,A," + Email("a")), manager);
        await Imports().CommitImportAsync(new CommitImportRequest { ImportId = preview.Id, SendInvitations = true }, manager);
        (await UntilFinishedAsync(preview.Id, manager)).Invites.Pending.Should().Be(1);

        await _factory.WithDbAsync(async db =>
        {
            foreach (var invitation in await db.Invitations.ToListAsync())
                invitation.LastSentAt = DateTime.UtcNow.AddHours(-2);
            foreach (var membership in await db.Memberships.ToListAsync())
                membership.IsActive = false;
            await db.SaveChangesAsync();
        }, estateId);
        await RunJobAsync();

        await _factory.WithDbAsync(async db =>
        {
            var batch = await db.ImportBatches.SingleAsync();
            batch.Message.Should().Contain("Invitations paused");
            (await db.ImportInvites.SingleAsync()).Status.Should().Be(ImportInviteStatuses.Pending);
        }, estateId);
    }

    [Fact]
    public async Task One_Import_Runs_At_A_Time_And_Repeat_Clicks_Reuse_It()
    {
        var (_, manager) = await EstateAsync();
        var first = await Imports().PreviewImportAsync(Csv(Enumerable.Range(1, 1000).Select(i => $"{i},0.1").ToArray()), manager);
        var second = await Imports().PreviewImportAsync(Csv("2001,1"), manager);

        var commit = await Imports().CommitImportAsync(new CommitImportRequest { ImportId = first.Id }, manager);
        if (commit.Status is "queued" or "running")
        {
            (await Imports().CommitImportAsync(new CommitImportRequest { ImportId = first.Id }, manager)).Id.Should().Be(first.Id);
            var busy = await GrpcStatus.ErrorOf(() => Imports().CommitImportAsync(new CommitImportRequest { ImportId = second.Id }, manager).ResponseAsync);
            if (busy is not null) // the first may have finished in between
                busy.Status.Detail.Should().Contain("Another import");
        }
        (await UntilFinishedAsync(first.Id, manager)).Status.Should().Be("done");
    }

    [Fact]
    public async Task Committing_Needs_A_Recent_Mfa()
    {
        var (estateId, manager) = await EstateAsync();
        var preview = await Imports().PreviewImportAsync(Csv("1,100"), manager);
        var stale = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager, mfa: false));

        var error = await GrpcStatus.ErrorOf(() => Imports().CommitImportAsync(new CommitImportRequest { ImportId = preview.Id }, stale).ResponseAsync);

        error!.Trailers.GetValue(RecentMfaInterceptor.StepUpTrailer).Should().Be("required");
    }

    [Fact]
    public async Task Discarding_Drops_The_Rows()
    {
        var (estateId, manager) = await EstateAsync();
        var preview = await Imports().PreviewImportAsync(Csv("1,100,,Thandi," + Email("t")), manager);

        await Imports().DiscardImportAsync(new DiscardImportRequest { ImportId = preview.Id }, manager);

        await _factory.WithDbAsync(async db =>
            (await db.ImportBatches.SingleAsync()).Should().BeEquivalentTo(new { Status = "discarded", RowsJson = "[]" }, o => o.ExcludingMissingMembers()), estateId);
        (await Imports().ListImportsAsync(new ListImportsRequest(), manager)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Files_Over_5_MB_Are_Refused_With_A_Reason_And_Huge_Messages_At_The_Door()
    {
        var (_, manager) = await EstateAsync();

        var tooBig = await GrpcStatus.ErrorOf(() => Imports().PreviewImportAsync(
            new PreviewImportRequest { FileName = "big.csv", Content = ByteString.CopyFrom(new byte[6 * 1024 * 1024]) }, manager).ResponseAsync);
        tooBig!.StatusCode.Should().Be(StatusCode.InvalidArgument, "an 8 MB message limit lets the friendly reason through");
        tooBig.Status.Detail.Should().Contain("limit is 5 MB");

        (await GrpcStatus.Of(() => Imports().PreviewImportAsync(
            new PreviewImportRequest { FileName = "huge.csv", Content = ByteString.CopyFrom(new byte[9 * 1024 * 1024]) }, manager).ResponseAsync))
            .Should().Be(StatusCode.ResourceExhausted);
    }

    [Theory]
    [InlineData(Roles.Trustee)]
    [InlineData(Roles.Guard)]
    [InlineData(Roles.Owner)]
    [InlineData(Roles.Tenant)]
    public async Task Only_Managers_And_Managing_Agents_Import(string role)
    {
        var (estateId, _) = await EstateAsync();
        var caller = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, role));

        (await GrpcStatus.Of(() => Imports().PreviewImportAsync(Csv("1,100"), caller).ResponseAsync)).Should().Be(StatusCode.PermissionDenied);
        (await GrpcStatus.Of(() => Imports().GetImportTemplateAsync(new GetImportTemplateRequest(), caller).ResponseAsync)).Should().Be(StatusCode.PermissionDenied);
    }

    [Fact] // SECURITY.md layer 5
    public async Task Another_Estates_Imports_Are_Invisible()
    {
        var (_, managerA) = await EstateAsync();
        var (_, managerB) = await EstateAsync();
        var preview = await Imports().PreviewImportAsync(Csv("1,100"), managerA);

        (await GrpcStatus.Of(() => Imports().GetImportAsync(new GetImportRequest { ImportId = preview.Id }, managerB).ResponseAsync)).Should().Be(StatusCode.NotFound);
        (await GrpcStatus.Of(() => Imports().CommitImportAsync(new CommitImportRequest { ImportId = preview.Id }, managerB).ResponseAsync)).Should().Be(StatusCode.NotFound);
        (await Imports().ListImportsAsync(new ListImportsRequest(), managerB)).Items.Should().BeEmpty();
    }

    private async Task RunJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await ActivatorUtilities.CreateInstance<ImportInvitationsJob>(scope.ServiceProvider).ExecuteAsync(CancellationToken.None);
    }
}
