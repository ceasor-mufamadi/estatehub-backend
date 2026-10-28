using System.Globalization;
using EstateHub.Api.Authorization;
using EstateHub.Api.Security;
using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using PersonInputDto = EstateHub.Application.Interfaces.PersonInput;
using PersonRefDto = EstateHub.Application.Interfaces.PersonRef;

namespace EstateHub.Api.Services;

[NoOwnershipRequired("Staff manage the estate's people (tenant layer confines ids); residents only reach MyUnits/GetMyUnit, which check their own units.")]
[AlwaysOn("People and units are core.")]
public class PeopleGrpcService : PeopleService.PeopleServiceBase
{
    private readonly IPeopleService _people;
    private readonly IResidentService _residents;
    private readonly IPersonDataService _data;
    private readonly SignInResponses _responses;

    public PeopleGrpcService(IPeopleService people, IResidentService residents, IPersonDataService data, SignInResponses responses)
    {
        _people = people;
        _residents = residents;
        _data = data;
        _responses = responses;
    }

    [RequiresPermission(Permissions.PeopleRead)]
    public override async Task<ListPeopleResponse> ListPeople(ListPeopleRequest request, ServerCallContext context)
    {
        var response = new ListPeopleResponse();
        response.Items.AddRange((await _people.ListAsync(request.Search, context.CancellationToken)).Select(p =>
        {
            var summary = new PersonSummary { Person = new Person { Id = p.Id.ToString(), Name = p.Name, Email = p.Email ?? "", Phone = p.Phone ?? "", HasAccount = p.HasAccount } };
            summary.Units.AddRange(p.Units);
            return summary;
        }));
        return response;
    }

    [RequiresPermission(Permissions.PeopleWrite)]
    public override async Task<Person> CreatePerson(CreatePersonRequest request, ServerCallContext context) =>
        ToProto(await _people.CreateAsync(ToInput(request.Person), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleWrite)]
    public override async Task<Person> UpdatePerson(UpdatePersonRequest request, ServerCallContext context) =>
        ToProto(await _people.UpdateAsync(GrpcRequest.RequiredGuid(request.PersonId, "person_id"), ToInput(request.Person), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleRead)]
    [RequiresAccess(ResourceKind.Unit, "unit_id")]
    public override async Task<UnitDetail> GetUnit(GetUnitRequest request, ServerCallContext context) =>
        ToProto(await _people.GetUnitAsync(GrpcRequest.RequiredGuid(request.UnitId, "unit_id"), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleWrite)]
    [RequiresAccess(ResourceKind.Unit, "unit_id")]
    public override async Task<UnitDetail> AddOwner(AddOwnerRequest request, ServerCallContext context) =>
        ToProto(await _people.AddOwnerAsync(
            GrpcRequest.RequiredGuid(request.UnitId, "unit_id"), ToRef(request.Person), ParseDecimal(request.SharePercent, "Share") ?? 100m,
            ParseDate(request.FromDate, "from_date"), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleWrite)]
    public override async Task<UnitDetail> EndOwnership(EndOwnershipRequest request, ServerCallContext context) =>
        ToProto(await _people.EndOwnershipAsync(GrpcRequest.RequiredGuid(request.OwnershipId, "ownership_id"),
            ParseDate(request.ToDate, "to_date") ?? throw GrpcRequest.InvalidArgument("The end date is required."), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleWrite)]
    public override async Task<UnitDetail> RemoveOwnership(RemoveOwnershipRequest request, ServerCallContext context) =>
        ToProto(await _people.RemoveOwnershipAsync(GrpcRequest.RequiredGuid(request.OwnershipId, "ownership_id"), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleWrite)]
    [RequiresAccess(ResourceKind.Unit, "unit_id")]
    public override async Task<UnitDetail> AddOccupant(AddOccupantRequest request, ServerCallContext context) =>
        ToProto(await _people.AddOccupantAsync(GrpcRequest.RequiredGuid(request.UnitId, "unit_id"), ToRef(request.Person), request.Kind,
            ParseDate(request.FromDate, "from_date"), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleWrite)]
    public override async Task<UnitDetail> EndOccupancy(EndOccupancyRequest request, ServerCallContext context) =>
        ToProto(await _people.EndOccupancyAsync(GrpcRequest.RequiredGuid(request.OccupancyId, "occupancy_id"),
            ParseDate(request.ToDate, "to_date") ?? throw GrpcRequest.InvalidArgument("The end date is required."), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleWrite)]
    public override async Task<UnitDetail> RemoveOccupancy(RemoveOccupancyRequest request, ServerCallContext context) =>
        ToProto(await _people.RemoveOccupancyAsync(GrpcRequest.RequiredGuid(request.OccupancyId, "occupancy_id"), context.CancellationToken));

    [RequiresPermission(Permissions.MembersManage)]
    [RequiresRecentMfa]
    public override async Task<InvitePersonResponse> InvitePerson(InvitePersonRequest request, ServerCallContext context)
    {
        var result = await _people.InviteAsync(GrpcRequest.RequiredGuid(request.PersonId, "person_id"), context.CancellationToken);
        return new InvitePersonResponse { Linked = result.Linked, Invited = result.Invited, SentTo = result.To ?? "" };
    }

    [Authorize]
    [AccessScopedInAction("Only the caller's own units (owned or lived in today).")]
    public override async Task<MyUnitsResponse> MyUnits(MyUnitsRequest request, ServerCallContext context)
    {
        var response = new MyUnitsResponse();
        response.Items.AddRange((await _people.MyUnitsAsync(context.CancellationToken)).Select(ToProto));
        return response;
    }

    [Authorize]
    [RequiresAccess(ResourceKind.Unit, "unit_id")]
    public override async Task<MyUnit> GetMyUnit(GetMyUnitRequest request, ServerCallContext context) =>
        ToProto(await _people.GetMyUnitAsync(GrpcRequest.RequiredGuid(request.UnitId, "unit_id"), context.CancellationToken));

    // ---------- resident portal: the caller's own record only ----------

    [Authorize]
    [AccessScopedInAction("The caller's own person record in this estate (by their user id).")]
    public override async Task<MyDetails> GetMyDetails(GetMyDetailsRequest request, ServerCallContext context) =>
        ToProto(await _residents.GetMyDetailsAsync(context.CancellationToken));

    [Authorize]
    [AccessScopedInAction("The caller's own person record in this estate (by their user id).")]
    public override async Task<MyDetails> AcknowledgePrivacyNotice(AcknowledgePrivacyNoticeRequest request, ServerCallContext context) =>
        ToProto(await _residents.AcknowledgePrivacyNoticeAsync(request.Version, context.CancellationToken));

    [Authorize]
    [AccessScopedInAction("The caller's own person record in this estate (by their user id).")]
    public override async Task<MyDetails> UpdateMyPreferences(UpdateMyPreferencesRequest request, ServerCallContext context) =>
        ToProto(await _residents.UpdatePreferencesAsync(request.NoticesByEmail, request.NoticesBySms, context.CancellationToken));

    [Authorize]
    [RequiresRecentSignIn]
    [AccessScopedInAction("The caller's own account and person record.")]
    public override async Task<StartContactChangeResponse> StartContactChange(StartContactChangeRequest request, ServerCallContext context)
    {
        var step = await _residents.StartContactChangeAsync(request.NewContact, _responses.Client(context), context.CancellationToken);
        return new StartContactChangeResponse { ChallengeToken = step.ChallengeToken, Channel = step.Channel, Destination = step.Destination };
    }

    [Authorize]
    [RequiresRecentSignIn]
    [AccessScopedInAction("The caller's own account and person record.")]
    public override async Task<MyDetails> ConfirmContactChange(ConfirmContactChangeRequest request, ServerCallContext context) =>
        ToProto(await _residents.ConfirmContactChangeAsync(request.ChallengeToken, request.Code, _responses.Client(context), context.CancellationToken));

    [Authorize]
    [RequiresRecentSignIn]
    [AccessScopedInAction("Everything held about the caller themselves (POPIA s23).")]
    public override async Task<PersonDataFile> ExportMyData(ExportMyDataRequest request, ServerCallContext context) =>
        ToProto(await _residents.ExportMyDataAsync(context.CancellationToken));

    // ---------- POPIA tools for staff ----------

    [RequiresPermission(Permissions.PeopleWrite)]
    [RequiresRecentMfa]
    public override async Task<PersonDataFile> ExportPerson(ExportPersonRequest request, ServerCallContext context) =>
        ToProto(await _data.ExportAsync(GrpcRequest.RequiredGuid(request.PersonId, "person_id"), context.CancellationToken));

    [RequiresPermission(Permissions.PeopleWrite)]
    [RequiresRecentMfa]
    public override async Task<AnonymisePersonResponse> AnonymisePerson(AnonymisePersonRequest request, ServerCallContext context)
    {
        await _data.AnonymiseAsync(GrpcRequest.RequiredGuid(request.PersonId, "person_id"), request.ConfirmName, context.CancellationToken);
        return new AnonymisePersonResponse();
    }

    private static MyDetails ToProto(MyDetailsDto d)
    {
        var details = new MyDetails
        {
            HasRecord = d.HasRecord,
            Name = d.Name,
            Email = d.Email ?? "",
            Phone = d.Phone ?? "",
            NoticesByEmail = d.NoticesByEmail,
            NoticesBySms = d.NoticesBySms,
            NoticeVersion = d.NoticeVersion,
            NoticeAcknowledged = d.NoticeAcknowledged,
        };
        details.Notice.AddRange(d.Notice.Select(n => new PrivacyNoticeSection { Heading = n.Heading, Text = n.Text }));
        return details;
    }

    private static PersonDataFile ToProto(PersonExportDto file) =>
        new() { FileName = file.FileName, Content = Google.Protobuf.ByteString.CopyFrom(file.Json) };

    private static PersonInputDto ToInput(PersonInput? input) =>
        input is null ? throw GrpcRequest.InvalidArgument("person is required.") : new PersonInputDto(input.Name, input.Email, input.Phone);

    private static PersonRefDto ToRef(PersonRef? reference)
    {
        if (reference is null)
            throw GrpcRequest.InvalidArgument("Choose a person, or enter a new one.");
        var id = GrpcRequest.OptionalGuid(reference.PersonId, "person_id");
        return new PersonRefDto(id, id is null && reference.NewPerson is { } p ? new PersonInputDto(p.Name, p.Email, p.Phone) : null);
    }

    private static DateOnly? ParseDate(string value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : throw GrpcRequest.InvalidArgument($"{field} must be a date like 2026-10-28.");

    private static decimal? ParseDecimal(string value, string label) =>
        string.IsNullOrWhiteSpace(value) ? null
        : decimal.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d
        : throw GrpcRequest.InvalidArgument($"{label} must be a number, e.g. 50.");

    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static Person ToProto(PersonDto p) =>
        new() { Id = p.Id.ToString(), Name = p.Name, Email = p.Email ?? "", Phone = p.Phone ?? "", HasAccount = p.HasAccount };

    private static UnitDetail ToProto(UnitDetailDto d)
    {
        var detail = new UnitDetail
        {
            UnitId = d.Unit.Id.ToString(),
            SectionNumber = d.Unit.SectionNumber,
            DoorNumber = d.Unit.DoorNumber ?? "",
            BlockName = d.Unit.BlockName ?? "",
            ParticipationQuota = d.Unit.ParticipationQuota.ToString("F4", CultureInfo.InvariantCulture),
            CurrentOwnerShare = d.CurrentOwnerShare.ToString("F2", CultureInfo.InvariantCulture),
        };
        detail.Owners.AddRange(d.Owners.Select(o => new OwnershipEntry
        {
            Id = o.Id.ToString(),
            Person = ToProto(o.Person),
            SharePercent = o.SharePercent.ToString("F2", CultureInfo.InvariantCulture),
            FromDate = Date(o.From),
            ToDate = Date(o.To),
            Current = o.IsCurrent,
        }));
        detail.Occupants.AddRange(d.Occupants.Select(o => new OccupancyEntry
        {
            Id = o.Id.ToString(),
            Person = ToProto(o.Person),
            Kind = o.Kind,
            FromDate = Date(o.From),
            ToDate = Date(o.To),
            Current = o.IsCurrent,
        }));
        return detail;
    }

    private static MyUnit ToProto(MyUnitDto u) => new()
    {
        UnitId = u.UnitId.ToString(),
        SectionNumber = u.SectionNumber,
        DoorNumber = u.DoorNumber ?? "",
        BlockName = u.BlockName ?? "",
        Relationship = u.Relationship,
        Since = Date(u.Since),
    };
}
