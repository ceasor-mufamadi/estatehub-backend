using EstateHub.Api.Authorization;
using EstateHub.Api.Security;
using EstateHub.Application.Auth;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using QRCoder;

namespace EstateHub.Api.Services;

// Each RPC states its own [AllowAnonymous]/[Authorize]: a class-level [AllowAnonymous] would
// override a method-level [Authorize].
[PlatformLevel]
[NoOwnershipRequired("Sign-in, sessions and keys: only ever the caller's own account.")]
[AlwaysOn("Authentication can't be switched off.")]
public class AuthGrpcService : AuthService.AuthServiceBase
{
    private readonly IAuthService _auth;
    private readonly ICurrentUser _user;
    private readonly ITokenService _tokens;
    private readonly ClientIpResolver _ip;
    private readonly AuthCookieOptions _cookie;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthGrpcService> _logger;

    public AuthGrpcService(
        IAuthService auth,
        ICurrentUser user,
        ITokenService tokens,
        ClientIpResolver ip,
        IOptions<AuthCookieOptions> cookie,
        IHostEnvironment environment,
        ILogger<AuthGrpcService> logger)
    {
        _auth = auth;
        _user = user;
        _tokens = tokens;
        _ip = ip;
        _cookie = cookie.Value;
        _environment = environment;
        _logger = logger;
    }

    [AllowAnonymous]
    public override async Task<PasswordSignInResponse> PasswordSignIn(PasswordSignInRequest request, ServerCallContext context)
    {
        var step = await _auth.PasswordSignInAsync(request.Email, request.Password, Client(context), context.CancellationToken);
        if (step.SignedIn is { } signedIn)
            return new PasswordSignInResponse { SignedIn = Complete(signedIn, context) };

        return new PasswordSignInResponse { Mfa = ToMfaStep(step) };
    }

    [AllowAnonymous]
    public override async Task<OtpStep> RequestOtp(RequestOtpRequest request, ServerCallContext context) =>
        ToProto(await _auth.RequestOtpAsync(request.Identifier, Client(context), context.CancellationToken));

    [AllowAnonymous]
    public override async Task<SignInStepResponse> VerifyOtp(VerifyOtpRequest request, ServerCallContext context) =>
        ToProto(await _auth.VerifyOtpAsync(request.ChallengeToken, request.Code, Client(context), context.CancellationToken), context);

    [AllowAnonymous]
    public override async Task<GetInvitationResponse> GetInvitation(GetInvitationRequest request, ServerCallContext context)
    {
        var info = await _auth.GetInvitationAsync(request.InvitationToken, context.CancellationToken);
        return new GetInvitationResponse
        {
            EstateName = info.EstateName,
            Role = info.Role,
            Name = info.Name,
            Channel = info.Channel,
            Destination = info.Destination,
            PasswordRequired = info.PasswordRequired,
            ExpiresAt = Timestamp.FromDateTimeOffset(info.ExpiresAt),
        };
    }

    [AllowAnonymous]
    public override async Task<OtpStep> StartInvitation(StartInvitationRequest request, ServerCallContext context) =>
        ToProto(await _auth.StartInvitationAsync(request.InvitationToken, Client(context), context.CancellationToken));

    [AllowAnonymous]
    public override async Task<SignInStepResponse> AcceptInvitation(AcceptInvitationRequest request, ServerCallContext context) =>
        ToProto(await _auth.AcceptInvitationAsync(
            request.ChallengeToken, request.Code, NullIfEmpty(request.NewPassword), Client(context), context.CancellationToken), context);

    [AllowAnonymous]
    public override async Task<SignedIn> VerifyMfa(VerifyMfaRequest request, ServerCallContext context) =>
        Complete(await _auth.VerifyMfaAsync(request.ChallengeToken, request.Code, Client(context), context.CancellationToken), context);

    [AllowAnonymous]
    public override async Task<BeginTotpEnrolmentResponse> BeginTotpEnrolment(BeginTotpEnrolmentRequest request, ServerCallContext context)
    {
        var enrolment = await _auth.BeginTotpEnrolmentAsync(request.ChallengeToken, context.CancellationToken);

        using var qr = QRCodeGenerator.GenerateQrCode(enrolment.OtpAuthUri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qr).GetGraphic(6);

        return new BeginTotpEnrolmentResponse
        {
            Secret = enrolment.Secret,
            OtpauthUri = enrolment.OtpAuthUri,
            QrCodeDataUrl = "data:image/png;base64," + Convert.ToBase64String(png),
        };
    }

    [AllowAnonymous]
    public override async Task<SignedIn> ConfirmTotpEnrolment(ConfirmTotpEnrolmentRequest request, ServerCallContext context) =>
        Complete(await _auth.ConfirmTotpEnrolmentAsync(request.ChallengeToken, request.Code, Client(context), context.CancellationToken), context);

    [AllowAnonymous]
    public override async Task<SignedIn> Refresh(RefreshRequest request, ServerCallContext context)
    {
        try
        {
            return Complete(await _auth.RefreshAsync(ReadCookie(context) ?? string.Empty, Client(context), context.CancellationToken), context);
        }
        catch (AuthFailedException)
        {
            ClearCookie(context);
            throw;
        }
    }

    [Authorize]
    public override async Task<SignedIn> StepUp(StepUpRequest request, ServerCallContext context) =>
        Complete(await _auth.StepUpAsync(UserId(), SessionId(), request.Code, Client(context), context.CancellationToken), context);

    [AllowAnonymous]
    public override async Task<LogoutResponse> Logout(LogoutRequest request, ServerCallContext context)
    {
        await _auth.LogoutAsync(ReadCookie(context), Client(context), context.CancellationToken);
        ClearCookie(context);
        return new LogoutResponse();
    }

    [Authorize]
    public override async Task<LogoutAllResponse> LogoutAll(LogoutAllRequest request, ServerCallContext context)
    {
        await _auth.LogoutAllAsync(UserId(), Client(context), context.CancellationToken);
        ClearCookie(context);
        return new LogoutAllResponse();
    }

    [Authorize]
    public override async Task<ListMyEstatesResponse> ListMyEstates(ListMyEstatesRequest request, ServerCallContext context)
    {
        var mine = await _auth.ListMyEstatesAsync(UserId(), context.CancellationToken);
        var response = new ListMyEstatesResponse { PlatformAccess = mine.PlatformAccess };
        response.Items.AddRange(mine.Items.Select(m => new MyEstate
        {
            EstateId = m.EstateId.ToString(),
            EstateName = m.EstateName,
            Role = m.Role,
        }));
        return response;
    }

    [Authorize]
    public override async Task<SignedIn> SwitchEstate(SwitchEstateRequest request, ServerCallContext context)
    {
        var estateId = GrpcRequest.OptionalGuid(request.EstateId, "estate_id");
        return Complete(await _auth.SwitchEstateAsync(UserId(), SessionId(), estateId, context.CancellationToken), context);
    }

    [AllowAnonymous]
    public override async Task<SignedIn> DevSignIn(DevSignInRequest request, ServerCallContext context)
    {
        if (!_environment.IsDevelopment() && !_environment.IsEnvironment("Testing"))
            throw new RpcException(new Status(StatusCode.Unimplemented, "Not available."));

        var estateId = GrpcRequest.OptionalGuid(request.EstateId, "estate_id");
        var signedIn = await _auth.DevSignInAsync(request.UserId, estateId, request.Role, request.Mfa, Client(context), context.CancellationToken);
        _logger.LogWarning("DevSignIn: {Role} session for {Label} (development only)", request.Role, request.UserId);
        return Complete(signedIn, context);
    }

    [AllowAnonymous]
    public override Task<GetJwksResponse> GetJwks(GetJwksRequest request, ServerCallContext context) =>
        Task.FromResult(new GetJwksResponse { JwksJson = _tokens.GetJwksJson() });

    private static OtpStep ToProto(Application.Auth.OtpStep step) => new()
    {
        ChallengeToken = step.ChallengeToken,
        Channel = step.Channel,
        Destination = step.Destination,
        ExpiresAt = Timestamp.FromDateTimeOffset(step.ExpiresAt),
    };

    private SignInStepResponse ToProto(SignInStep step, ServerCallContext context) =>
        step.SignedIn is { } signedIn
            ? new SignInStepResponse { SignedIn = Complete(signedIn, context) }
            : new SignInStepResponse { Mfa = ToMfaStep(step) };

    private static MfaStep ToMfaStep(SignInStep step) => new()
    {
        ChallengeToken = step.ChallengeToken,
        EnrolmentRequired = step.EnrolmentRequired,
        ExpiresAt = Timestamp.FromDateTimeOffset(step.ChallengeExpiresAt!.Value),
    };

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Moves a new refresh token into the cookie and maps the rest to the response.</summary>
    private SignedIn Complete(Application.Auth.SignedIn signedIn, ServerCallContext context)
    {
        if (signedIn.RefreshToken is { } refresh)
            context.GetHttpContext().Response.Cookies.Append(AuthCookieOptions.Name, refresh, CookieOptions(signedIn.RefreshExpiresAt));

        var response = new SignedIn
        {
            AccessToken = signedIn.Access.AccessToken,
            ExpiresAt = Timestamp.FromDateTimeOffset(signedIn.Access.ExpiresAt),
            UserId = signedIn.Scope.UserId.ToString(),
            Email = signedIn.Scope.Email,
            EstateId = signedIn.Scope.EstateId?.ToString() ?? string.Empty,
            Role = signedIn.Scope.Role,
        };
        response.Permissions.AddRange(signedIn.Scope.Permissions);
        response.RecoveryCodes.AddRange(signedIn.RecoveryCodes);
        return response;
    }

    private static string? ReadCookie(ServerCallContext context) =>
        context.GetHttpContext().Request.Cookies[AuthCookieOptions.Name];

    private void ClearCookie(ServerCallContext context) =>
        context.GetHttpContext().Response.Cookies.Delete(AuthCookieOptions.Name, CookieOptions(expires: null));

    private CookieOptions CookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = _cookie.RefreshCookieSecure,
        SameSite = SameSiteMode.Strict,
        Path = AuthCookieOptions.Path,
        Expires = expires,
        IsEssential = true,
    };

    private ClientInfo Client(ServerCallContext context)
    {
        var http = context.GetHttpContext();
        return new ClientInfo(_ip.Resolve(http), http.Request.Headers.UserAgent.ToString());
    }

    // [Authorize] guarantees a validated token, and every token EstateHub issues carries both.
    private Guid UserId() => Guid.Parse(_user.UserId!);

    private Guid SessionId() => _user.SessionId ?? throw new AuthFailedException(Application.Services.AuthService.SessionEnded);
}
