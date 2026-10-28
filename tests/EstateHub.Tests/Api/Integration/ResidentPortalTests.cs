using System.Text.Json;
using EstateHub.Api;
using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.Privacy;
using FluentAssertions;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Tests.Api.Integration;

// Slice 3.4 through the real host: privacy notice, own details and contact changes, own data, and the
// staff POPIA tools (export, anonymise), plus the estate's privacy settings.
public class ResidentPortalTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public ResidentPortalTests(EstateHubApiFactory factory) => _factory = factory;

    private PeopleService.PeopleServiceClient People() => new(_factory.CreateGrpcChannel());
    private EstateService.EstateServiceClient Estate() => new(_factory.CreateGrpcChannel());
    private AuthService.AuthServiceClient Auth() => new(_factory.CreateGrpcChannel());

    private static string Label() => $"r-{Guid.NewGuid():N}"[..20];
    private static string Email(string name) => $"{name}-{Guid.NewGuid():N}@example.co.za";

    private record Resident(Metadata Token, string PersonId, string UserId, string Email, string OwnershipId, string UnitId);

    private async Task<(Guid EstateId, Metadata Manager)> EstateAsync()
    {
        var estateId = await _factory.CreateEstateAsync($"Portal {Guid.NewGuid():N}");
        return (estateId, EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager)));
    }

    /// <summary>An owner of a new unit, signed in (recently, unless <paramref name="recent"/> is false) and linked to their person record.</summary>
    private async Task<Resident> ResidentAsync(Guid estateId, Metadata manager, int section = 1, bool recent = true)
    {
        var label = Label();
        var token = EstateHubApiFactory.Bearer(await _factory.TokenAsync(label, estateId, Roles.Owner, mfa: recent));
        var email = $"{label}@dev.estatehub.local";
        var unit = await Estate().CreateUnitAsync(new CreateUnitRequest { Unit = new UnitInput { SectionNumber = section, ParticipationQuota = "10" } }, manager);
        var detail = await People().AddOwnerAsync(new AddOwnerRequest
        {
            UnitId = unit.Id, Person = new PersonRef { NewPerson = new PersonInput { Name = "Thandi Owner", Email = email } }, SharePercent = "100", FromDate = "2020-01-01",
        }, manager);
        var personId = detail.Owners[0].Person.Id;
        string userId = "";
        await _factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == email);
            userId = user.Id.ToString();
            (await db.People.SingleAsync(p => p.Id == Guid.Parse(personId))).UserId = user.Id;
            await db.SaveChangesAsync();
        }, estateId);
        return new Resident(token, personId, userId, email, detail.Owners[0].Id, unit.Id);
    }

    [Fact]
    public async Task A_Resident_Acknowledges_The_Current_Privacy_Notice_Once()
    {
        var (estateId, manager) = await EstateAsync();
        var me = await ResidentAsync(estateId, manager);

        var details = await People().GetMyDetailsAsync(new GetMyDetailsRequest(), me.Token);
        details.Should().BeEquivalentTo(new { HasRecord = true, Name = "Thandi Owner", NoticeAcknowledged = false, NoticeVersion = PrivacyNotice.Version }, o => o.ExcludingMissingMembers());
        details.Notice.Should().NotBeEmpty();

        (await GrpcStatus.ErrorOf(() => People().AcknowledgePrivacyNoticeAsync(new AcknowledgePrivacyNoticeRequest { Version = "2020-01-01" }, me.Token).ResponseAsync))!
            .Status.Detail.Should().Contain("has changed");
        (await People().AcknowledgePrivacyNoticeAsync(new AcknowledgePrivacyNoticeRequest { Version = PrivacyNotice.Version }, me.Token))
            .NoticeAcknowledged.Should().BeTrue();
    }

    [Fact]
    public async Task Staff_Without_A_Person_Record_Have_Nothing_To_Acknowledge()
    {
        var (_, manager) = await EstateAsync();

        var details = await People().GetMyDetailsAsync(new GetMyDetailsRequest(), manager);

        details.HasRecord.Should().BeFalse();
        details.NoticeAcknowledged.Should().BeTrue();
    }

    [Fact]
    public async Task Preferences_Need_The_Contact_Detail_They_Use()
    {
        var (estateId, manager) = await EstateAsync();
        var me = await ResidentAsync(estateId, manager);

        (await GrpcStatus.ErrorOf(() => People().UpdateMyPreferencesAsync(new UpdateMyPreferencesRequest { NoticesByEmail = true, NoticesBySms = true }, me.Token).ResponseAsync))!
            .Status.Detail.Should().Contain("Add a mobile number first");
        (await People().UpdateMyPreferencesAsync(new UpdateMyPreferencesRequest { NoticesByEmail = false }, me.Token))
            .Should().BeEquivalentTo(new { NoticesByEmail = false, NoticesBySms = false }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task Changing_Email_Is_Confirmed_At_The_New_Address_And_Told_To_The_Old_One()
    {
        var (estateId, manager) = await EstateAsync();
        var me = await ResidentAsync(estateId, manager);
        var newEmail = Email("new");

        var step = await People().StartContactChangeAsync(new StartContactChangeRequest { NewContact = newEmail }, me.Token);
        step.Channel.Should().Be("email");
        (await GrpcStatus.Of(() => People().ConfirmContactChangeAsync(new ConfirmContactChangeRequest { ChallengeToken = step.ChallengeToken, Code = "000000" }, me.Token).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated);

        var details = await People().ConfirmContactChangeAsync(new ConfirmContactChangeRequest { ChallengeToken = step.ChallengeToken, Code = _factory.Outbox.CodeFor(newEmail) }, me.Token);

        details.Email.Should().Be(newEmail);
        _factory.Outbox.LastTo(me.Email).Should().Contain("was changed to");
        await _factory.WithDbAsync(async db => (await db.Users.SingleAsync(u => u.Id == Guid.Parse(me.UserId))).Email.Should().Be(newEmail, "they now sign in with it"));
    }

    [Fact]
    public async Task An_Address_Someone_Else_Here_Uses_Gets_No_Code_And_Reveals_Nothing()
    {
        var (estateId, manager) = await EstateAsync();
        var me = await ResidentAsync(estateId, manager);
        var neighbour = Email("neighbour");
        await People().CreatePersonAsync(new CreatePersonRequest { Person = new PersonInput { Name = "Neighbour", Email = neighbour } }, manager);

        var step = await People().StartContactChangeAsync(new StartContactChangeRequest { NewContact = neighbour }, me.Token);

        step.Destination.Should().NotBeEmpty("the answer looks the same as for a free address");
        _factory.Outbox.CountTo(neighbour).Should().Be(0);
    }

    [Fact]
    public async Task Contact_Changes_And_Own_Data_Need_A_Recent_Sign_In()
    {
        var (estateId, manager) = await EstateAsync();
        var stale = await ResidentAsync(estateId, manager, recent: false);

        var error = await GrpcStatus.ErrorOf(() => People().ExportMyDataAsync(new ExportMyDataRequest(), stale.Token).ResponseAsync);
        error!.StatusCode.Should().Be(StatusCode.PermissionDenied);
        error.Trailers.GetValue(RecentMfaInterceptor.StepUpTrailer).Should().Be(RecentMfaInterceptor.SignInAgain);
        (await GrpcStatus.Of(() => People().StartContactChangeAsync(new StartContactChangeRequest { NewContact = Email("x") }, stale.Token).ResponseAsync))
            .Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task A_Resident_Downloads_Everything_Held_About_Them()
    {
        var (estateId, manager) = await EstateAsync();
        var me = await ResidentAsync(estateId, manager, section: 7);

        var file = await People().ExportMyDataAsync(new ExportMyDataRequest(), me.Token);

        file.FileName.Should().StartWith("estatehub-personal-data-");
        using var json = JsonDocument.Parse(file.Content.ToByteArray());
        json.RootElement.GetProperty("person").GetProperty("name").GetString().Should().Be("Thandi Owner");
        json.RootElement.GetProperty("owns")[0].GetProperty("unit").GetString().Should().Be("Section 7");
        json.RootElement.GetProperty("account").GetProperty("email").GetString().Should().Be(me.Email);
        json.RootElement.GetProperty("activity").EnumerateArray().Select(a => a.GetProperty("action").GetString()).Should().Contain(["Created", "Added"]);
    }

    [Fact]
    public async Task Staff_Export_A_Person_With_A_Fresh_Mfa_And_Residents_Cant()
    {
        var (estateId, manager) = await EstateAsync();
        var me = await ResidentAsync(estateId, manager);
        var staleManager = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager, mfa: false));

        (await People().ExportPersonAsync(new ExportPersonRequest { PersonId = me.PersonId }, manager)).Content.Length.Should().BePositive();
        (await GrpcStatus.ErrorOf(() => People().ExportPersonAsync(new ExportPersonRequest { PersonId = me.PersonId }, staleManager).ResponseAsync))!
            .Trailers.GetValue(RecentMfaInterceptor.StepUpTrailer).Should().Be("required");
        (await GrpcStatus.Of(() => People().ExportPersonAsync(new ExportPersonRequest { PersonId = me.PersonId }, me.Token).ResponseAsync))
            .Should().Be(StatusCode.PermissionDenied);
        (await GrpcStatus.Of(() => People().AnonymisePersonAsync(new AnonymisePersonRequest { PersonId = me.PersonId, ConfirmName = "Thandi Owner" }, me.Token).ResponseAsync))
            .Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Only_A_Former_Resident_Is_Anonymised_And_It_Removes_Them_Everywhere()
    {
        var (estateId, manager) = await EstateAsync();
        var me = await ResidentAsync(estateId, manager);

        (await GrpcStatus.ErrorOf(() => People().AnonymisePersonAsync(new AnonymisePersonRequest { PersonId = me.PersonId, ConfirmName = "Thandi Owner" }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("still owns or lives");

        await People().EndOwnershipAsync(new EndOwnershipRequest { OwnershipId = me.OwnershipId, ToDate = "2024-12-31" }, manager);
        (await GrpcStatus.ErrorOf(() => People().AnonymisePersonAsync(new AnonymisePersonRequest { PersonId = me.PersonId, ConfirmName = "Thandi" }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("Type the person's name");

        await People().AnonymisePersonAsync(new AnonymisePersonRequest { PersonId = me.PersonId, ConfirmName = " thandi owner " }, manager);

        var unit = await People().GetUnitAsync(new GetUnitRequest { UnitId = me.UnitId }, manager);
        unit.Owners.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { FromDate = "2020-01-01", ToDate = "2024-12-31" }, o => o.ExcludingMissingMembers(), "the history stays");
        unit.Owners[0].Person.Name.Should().StartWith("Former resident ");
        unit.Owners[0].Person.Email.Should().BeEmpty();

        await _factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == Guid.Parse(me.UserId));
            user.Should().BeEquivalentTo(new { Email = (string?)null, Status = "disabled", PasswordHash = (string?)null }, o => o.ExcludingMissingMembers());
            var trail = await db.AuditLogs.Where(a => a.EntityId == me.PersonId).ToListAsync();
            trail.Select(a => a.Action).Should().Contain(["Created", "Anonymised"]);
            trail.Should().NotContain(a => a.Details != null && a.Details.Contains("Thandi"), "the audit log names people by id only");
        }, estateId);
        (await GrpcStatus.Of(() => People().GetMyDetailsAsync(new GetMyDetailsRequest(), me.Token).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated, "their sessions ended with the account");
    }

    [Fact]
    public async Task Privacy_Settings_Default_To_No_Id_Numbers_And_90_Days()
    {
        var (_, manager) = await EstateAsync();

        (await Estate().GetPrivacySettingsAsync(new GetPrivacySettingsRequest(), manager))
            .Should().BeEquivalentTo(new { VisitorIdCapture = false, VisitorRetentionDays = 90 }, o => o.ExcludingMissingMembers());

        (await GrpcStatus.ErrorOf(() => Estate().UpdatePrivacySettingsAsync(new UpdatePrivacySettingsRequest
        {
            Settings = new PrivacySettings { VisitorRetentionDays = 10 },
        }, manager).ResponseAsync))!.Status.Detail.Should().Contain("30 to 365 days");

        var saved = await Estate().UpdatePrivacySettingsAsync(new UpdatePrivacySettingsRequest
        {
            Settings = new PrivacySettings { InformationOfficerName = "Naledi Khumalo", InformationOfficerEmail = "IO@Example.co.za", VisitorIdCapture = true, VisitorRetentionDays = 120 },
        }, manager);
        saved.Should().BeEquivalentTo(new { InformationOfficerEmail = "io@example.co.za", VisitorIdCapture = true, VisitorRetentionDays = 120 }, o => o.ExcludingMissingMembers());
        (await People().GetMyDetailsAsync(new GetMyDetailsRequest(), manager)).Notice.Last().Text.Should().Contain("Naledi Khumalo");
    }

    [Fact]
    public async Task Changing_Privacy_Settings_Needs_A_Fresh_Mfa_And_Staff_Who_May_Write()
    {
        var (estateId, _) = await EstateAsync();
        var stale = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager, mfa: false));
        var trustee = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Trustee));
        var request = new UpdatePrivacySettingsRequest { Settings = new PrivacySettings { VisitorIdCapture = true, VisitorRetentionDays = 90 } };

        (await GrpcStatus.ErrorOf(() => Estate().UpdatePrivacySettingsAsync(request, stale).ResponseAsync))!
            .Trailers.GetValue(RecentMfaInterceptor.StepUpTrailer).Should().Be("required");
        (await GrpcStatus.Of(() => Estate().UpdatePrivacySettingsAsync(request, trustee).ResponseAsync)).Should().Be(StatusCode.PermissionDenied);
    }
}
