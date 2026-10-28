using EstateHub.Api.Authorization;
using EstateHub.Api.Security;
using EstateHub.Application.Auth;
using EstateHub.Application.Interfaces;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace EstateHub.Api.Services;

// Manager RPCs act in the caller's estate; tablet RPCs are anonymous and find their estate from the
// tablet's credential (cookie), so they're [PlatformLevel].
[NoOwnershipRequired("Estate staff manage their estate's tablets (tenant layer confines ids); tablets only ever see their own estate.")]
[AlwaysOn("Guard tablets are part of sign-in (AUTH-14); the visitor register they serve is toggled separately.")]
public class GuardDeviceGrpcService : GuardDeviceService.GuardDeviceServiceBase
{
    private readonly IGuardDeviceService _devices;
    private readonly IAuthService _auth;
    private readonly SignInResponses _responses;
    private readonly TimeProvider _clock;

    public GuardDeviceGrpcService(IGuardDeviceService devices, IAuthService auth, SignInResponses responses, TimeProvider clock)
    {
        _devices = devices;
        _auth = auth;
        _responses = responses;
        _clock = clock;
    }

    [RequiresPermission(Permissions.DevicesManage)]
    public override async Task<PairingCodeResponse> CreatePairingCode(CreatePairingCodeRequest request, ServerCallContext context)
    {
        var code = await _devices.CreatePairingCodeAsync(request.Label, context.CancellationToken);
        return new PairingCodeResponse { Code = code.Code, Label = code.Label, ExpiresAt = Utc(code.ExpiresAt) };
    }

    [RequiresPermission(Permissions.DevicesManage)]
    public override async Task<ListDevicesResponse> ListDevices(ListDevicesRequest request, ServerCallContext context)
    {
        var response = new ListDevicesResponse();
        response.Items.AddRange((await _devices.ListAsync(context.CancellationToken)).Select(d => new GuardDeviceEntry
        {
            Id = d.Id.ToString(),
            Label = d.Label,
            PairedAt = Utc(d.PairedAt),
            LastSeenAt = Utc(d.LastSeenAt),
        }));
        return response;
    }

    [RequiresPermission(Permissions.DevicesManage)]
    public override async Task<RevokeDeviceResponse> RevokeDevice(RevokeDeviceRequest request, ServerCallContext context)
    {
        await _devices.RevokeAsync(GrpcRequest.RequiredGuid(request.DeviceId, "device_id"), context.CancellationToken);
        return new RevokeDeviceResponse();
    }

    [AllowAnonymous]
    [PlatformLevel]
    public override async Task<PairDeviceResponse> PairDevice(PairDeviceRequest request, ServerCallContext context)
    {
        var paired = await _devices.PairAsync(request.Code, context.CancellationToken);
        _responses.SetDeviceCookie(context, paired.Credential, _clock.GetUtcNow());
        return new PairDeviceResponse { EstateName = paired.EstateName, Label = paired.Label };
    }

    [AllowAnonymous]
    [PlatformLevel]
    public override async Task<GetTabletResponse> GetTablet(GetTabletRequest request, ServerCallContext context)
    {
        try
        {
            var tablet = await _auth.GetGuardTabletAsync(SignInResponses.ReadDeviceCookie(context) ?? string.Empty, context.CancellationToken);
            var response = new GetTabletResponse { EstateName = tablet.EstateName, Label = tablet.Label };
            response.Guards.AddRange(tablet.Guards.Select(g => new TabletGuard { UserId = g.UserId.ToString(), Name = g.Name, HasPin = g.HasPin }));
            return response;
        }
        catch (AuthFailedException)
        {
            _responses.ClearDeviceCookie(context); // revoked or unknown: back to the pairing screen
            throw;
        }
    }

    [AllowAnonymous]
    [PlatformLevel]
    public override async Task<SignedIn> GuardSignIn(GuardSignInRequest request, ServerCallContext context) =>
        _responses.Complete(await _auth.GuardSignInAsync(
            SignInResponses.ReadDeviceCookie(context) ?? string.Empty,
            GrpcRequest.RequiredGuid(request.UserId, "user_id"),
            request.Pin,
            _responses.Client(context),
            context.CancellationToken), context);

    private static Timestamp Utc(DateTime value) => Timestamp.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
