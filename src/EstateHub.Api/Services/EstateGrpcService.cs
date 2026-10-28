using System.Globalization;
using EstateHub.Api.Authorization;
using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using Grpc.Core;
using UnitInputDto = EstateHub.Application.DTOs.UnitInput;

namespace EstateHub.Api.Services;

[NoOwnershipRequired("Estate-wide structure for staff (and unit numbers for guards); the tenant layer confines it to the caller's estate. Residents have no access.")]
[AlwaysOn("Core estate structure.")]
public class EstateGrpcService : EstateService.EstateServiceBase
{
    private readonly IBlockService _blocks;
    private readonly IUnitService _units;
    private readonly IEstateDetailsService _details;
    private readonly ICurrentUser _user;

    public EstateGrpcService(IBlockService blocks, IUnitService units, IEstateDetailsService details, ICurrentUser user)
    {
        _blocks = blocks;
        _units = units;
        _details = details;
        _user = user;
    }

    [RequiresPermission(Permissions.EstateRead)]
    public override async Task<EstateDetails> GetEstateDetails(GetEstateDetailsRequest request, ServerCallContext context) =>
        ToProto(await _details.GetAsync(context.CancellationToken));

    [RequiresPermission(Permissions.EstateWrite)]
    public override async Task<EstateDetails> UpdateEstateDetails(UpdateEstateDetailsRequest request, ServerCallContext context) =>
        ToProto(await _details.UpdateAsync(
            request.Name, request.SchemeNumber, request.CsosRegistrationNumber, request.Address, _user.UserId!, context.CancellationToken));

    [RequiresPermission(Permissions.EstateRead)]
    public override async Task<PrivacySettings> GetPrivacySettings(GetPrivacySettingsRequest request, ServerCallContext context) =>
        ToProto(await _details.GetPrivacyAsync(context.CancellationToken));

    [RequiresPermission(Permissions.EstateWrite)]
    [RequiresRecentMfa]
    public override async Task<PrivacySettings> UpdatePrivacySettings(UpdatePrivacySettingsRequest request, ServerCallContext context)
    {
        var s = request.Settings ?? throw GrpcRequest.InvalidArgument("settings are required.");
        return ToProto(await _details.UpdatePrivacyAsync(
            new EstatePrivacyDto(s.InformationOfficerName, s.InformationOfficerEmail, s.VisitorIdCapture, s.VisitorRetentionDays),
            _user.UserId!, context.CancellationToken));
    }

    private static PrivacySettings ToProto(EstatePrivacyDto p) => new()
    {
        InformationOfficerName = p.InformationOfficerName ?? "",
        InformationOfficerEmail = p.InformationOfficerEmail ?? "",
        VisitorIdCapture = p.VisitorIdCapture,
        VisitorRetentionDays = p.VisitorRetentionDays,
    };

    [RequiresPermission(Permissions.EstateWrite)]
    public override async Task<BlockEntry> CreateBlock(CreateBlockRequest request, ServerCallContext context) =>
        ToEntry(await _blocks.CreateAsync(request.Name, _user.UserId!, context.CancellationToken));

    [RequiresPermission(Permissions.EstateRead)]
    public override async Task<ListBlocksResponse> ListBlocks(ListBlocksRequest request, ServerCallContext context)
    {
        var response = new ListBlocksResponse();
        response.Items.AddRange((await _blocks.ListAsync(context.CancellationToken)).Select(ToEntry));
        return response;
    }

    [RequiresPermission(Permissions.EstateWrite)]
    public override async Task<BlockEntry> RenameBlock(RenameBlockRequest request, ServerCallContext context) =>
        ToEntry(await _blocks.RenameAsync(GrpcRequest.RequiredGuid(request.BlockId, "block_id"), request.Name, _user.UserId!, context.CancellationToken));

    [RequiresPermission(Permissions.EstateWrite)]
    public override async Task<DeleteBlockResponse> DeleteBlock(DeleteBlockRequest request, ServerCallContext context)
    {
        await _blocks.DeleteAsync(GrpcRequest.RequiredGuid(request.BlockId, "block_id"), _user.UserId!, context.CancellationToken);
        return new DeleteBlockResponse();
    }

    [RequiresPermission(Permissions.EstateRead)]
    public override async Task<ListUnitsResponse> ListUnits(ListUnitsRequest request, ServerCallContext context)
    {
        var view = await _units.ListAsync(context.CancellationToken);
        var response = new ListUnitsResponse { QuotaTotal = Decimal(view.QuotaTotal, 4), QuotaComplete = view.QuotaComplete };
        response.Items.AddRange(view.Units.Select(ToProto));
        return response;
    }

    [RequiresPermission(Permissions.EstateWrite)]
    public override async Task<UnitEntry> CreateUnit(CreateUnitRequest request, ServerCallContext context) =>
        ToProto(await _units.CreateAsync(ToInput(request.Unit), _user.UserId!, context.CancellationToken));

    [RequiresPermission(Permissions.EstateWrite)]
    public override async Task<UnitEntry> UpdateUnit(UpdateUnitRequest request, ServerCallContext context) =>
        ToProto(await _units.UpdateAsync(GrpcRequest.RequiredGuid(request.UnitId, "unit_id"), ToInput(request.Unit), _user.UserId!, context.CancellationToken));

    [RequiresPermission(Permissions.EstateWrite)]
    public override async Task<DeleteUnitResponse> DeleteUnit(DeleteUnitRequest request, ServerCallContext context)
    {
        await _units.DeleteAsync(GrpcRequest.RequiredGuid(request.UnitId, "unit_id"), _user.UserId!, context.CancellationToken);
        return new DeleteUnitResponse();
    }

    private static UnitInputDto ToInput(UnitInput? input)
    {
        if (input is null)
            throw GrpcRequest.InvalidArgument("unit is required.");
        return new UnitInputDto(
            input.SectionNumber,
            input.DoorNumber,
            GrpcRequest.OptionalGuid(input.BlockId, "block_id"),
            ParseDecimal(input.ParticipationQuota, "Participation quota") ?? throw GrpcRequest.InvalidArgument("Participation quota is required."),
            ParseDecimal(input.SizeSquareMetres, "Size"));
    }

    /// <summary>Accepts "2.3456" and "2,3456" (South African comma decimals); never parses via floating point.</summary>
    private static decimal? ParseDecimal(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return decimal.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw GrpcRequest.InvalidArgument($"{label} must be a number, e.g. 2.3456.");
    }

    private static string Decimal(decimal value, int places) => value.ToString($"F{places}", CultureInfo.InvariantCulture);

    private static UnitEntry ToProto(UnitDto u) => new()
    {
        Id = u.Id.ToString(),
        SectionNumber = u.SectionNumber,
        DoorNumber = u.DoorNumber ?? string.Empty,
        BlockId = u.BlockId?.ToString() ?? string.Empty,
        BlockName = u.BlockName ?? string.Empty,
        ParticipationQuota = Decimal(u.ParticipationQuota, 4),
        SizeSquareMetres = u.SizeSquareMetres is { } size ? Decimal(size, 2) : string.Empty,
    };

    private static EstateDetails ToProto(EstateDetailsDto d) => new()
    {
        Id = d.Id.ToString(),
        Name = d.Name,
        SchemeType = d.SchemeType,
        SchemeNumber = d.SchemeNumber ?? string.Empty,
        CsosRegistrationNumber = d.CsosRegistrationNumber ?? string.Empty,
        Address = d.Address ?? string.Empty,
    };

    private static BlockEntry ToEntry(BlockDto dto) => new() { Id = dto.Id.ToString(), Name = dto.Name };
}
