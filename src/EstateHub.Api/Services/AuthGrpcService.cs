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
    private readonly SignInResponses _responses;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthGrpcService> _logger;

    public AuthGrpcService(
        IAuthService auth,
        ICurrentUser user,
        ITokenService tokens,
        SignInResponses responses,
        IHostEnvironment environment,
        ILogger<AuthGrpcService> logger)
    {
        _auth = auth;
        _user = user;
        _tokens = tokens;
        _responses = responses;
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

    [Authorize]
    public override async Task<ListSessionsResponse> ListSessions(ListSessionsRequest request, ServerCallContext context)
    {
        var response = new ListSessionsResponse();
        response.Items.AddRange((await _auth.ListSessionsAsync(UserId(), SessionId(), context.CancellationToken)).Select(s => new SessionEntry
        {
            Id = s.Id.ToString(),
            Device = s.Device,
            IpAddress = s.IpAddress ?? string.Empty,
            SignedInAt = Timestamp.FromDateTimeOffset(s.SignedInAt),
            LastSeenAt = Timestamp.FromDateTimeOffset(s.LastSeenAt),
            Current = s.IsCurrent,
        }));
        return response;
    }

    [Authorize]
    public override async Task<RevokeSessionResponse> RevokeSession(RevokeSessionRequest request, ServerCallContext context)
    {
        await _auth.RevokeSessionAsync(UserId(), GrpcRequest.RequiredGuid(request.SessionId, "session_id"), Client(context), context.CancellationToken);
        return new RevokeSessionResponse();
    }

    [Authorize]
    public override async Task<SignOutOtherSessionsResponse> SignOutOtherSessions(SignOutOtherSessionsRequest request, ServerCallContext context) =>
        new() { SignedOut = await _auth.SignOutOtherSessionsAsync(UserId(), SessionId(), Client(context), context.CancellationToken) };

    [Authorize]
    public override async Task<ChangePasswordResponse> ChangePassword(ChangePasswordRequest request, ServerCallContext context)
    {
        await _auth.ChangePasswordAsync(
            UserId(), SessionId(), NullIfEmpty(request.CurrentPassword), request.NewPassword, Client(context), context.CancellationToken);
        return new ChangePasswordResponse();
    }

    [AllowAnonymous]
    public override async Task<OtpStep> RequestPasswordReset(RequestPasswordResetRequest request, ServerCallContext context) =>
        ToProto(await _auth.RequestPasswordResetAsync(request.Identifier, Client(context), context.CancellationToken));

    [AllowAnonymous]
    public override async Task<SignInStepResponse> ResetPassword(ResetPasswordRequest request, ServerCallContext context) =>
        ToProto(await _auth.ResetPasswordAsync(request.ChallengeToken, request.Code, request.NewPassword, Client(context), context.CancellationToken), context);

    [Authorize]
    [RequiresRecentMfa]
    public override async Task<RegenerateRecoveryCodesResponse> RegenerateRecoveryCodes(RegenerateRecoveryCodesRequest request, ServerCallContext context)
    {
        var response = new RegenerateRecoveryCodesResponse();
        response.RecoveryCodes.AddRange(await _auth.RegenerateRecoveryCodesAsync(UserId(), Client(context), context.CancellationToken));
        return response;
    }

    [RequiresPermission(Permissions.PlatformImpersonate)]
    [RequiresRecentMfa]
    public override async Task<SignedIn> StartSupportAccess(StartSupportAccessRequest request, ServerCallContext context)
    {
        var estateId = GrpcRequest.RequiredGuid(request.EstateId, "estate_id");
        return Complete(await _auth.StartSupportAccessAsync(UserId(), SessionId(), estateId, request.Reason, Client(context), context.CancellationToken), context);
    }

    [Authorize]
    public override async Task<SignedIn> EndSupportAccess(EndSupportAccessRequest request, ServerCallContext context) =>
        Complete(await _auth.EndSupportAccessAsync(UserId(), SessionId(), Client(context), context.CancellationToken), context);

    [Authorize]
    public override async Task<SetGuardPinResponse> SetGuardPin(SetGuardPinRequest request, ServerCallContext context)
    {
        await _auth.SetGuardPinAsync(UserId(), request.Pin, Client(context), context.CancellationToken);
        return new SetGuardPinResponse();
    }

    [Authorize]
    [RequiresRecentMfa]
    public override async Task<PasskeyOptions> BeginPasskeyRegistration(BeginPasskeyRegistrationRequest request, ServerCallContext context)
    {
        var (token, options) = await _auth.BeginPasskeyRegistrationAsync(UserId(), context.CancellationToken);
        return new PasskeyOptions { ChallengeToken = token, OptionsJson = options };
    }

    [Authorize]
    [RequiresRecentMfa]
    public override async Task<FinishPasskeyRegistrationResponse> FinishPasskeyRegistration(FinishPasskeyRegistrationRequest request, ServerCallContext context)
    {
        await _auth.FinishPasskeyRegistrationAsync(UserId(), request.ChallengeToken, request.CredentialJson, request.Name, Client(context), context.CancellationToken);
        return new FinishPasskeyRegistrationResponse();
    }

    [AllowAnonymous]
    public override async Task<PasskeyOptions> BeginPasskeySignIn(BeginPasskeySignInRequest request, ServerCallContext context)
    {
        var (token, options) = await _auth.BeginPasskeySignInAsync(Client(context), context.CancellationToken);
        return new PasskeyOptions { ChallengeToken = token, OptionsJson = options };
    }

    [AllowAnonymous]
    public override async Task<SignedIn> FinishPasskeySignIn(FinishPasskeySignInRequest request, ServerCallContext context) =>
        Complete(await _auth.FinishPasskeySignInAsync(request.ChallengeToken, request.CredentialJson, Client(context), context.CancellationToken), context);

    [Authorize]
    public override async Task<ListPasskeysResponse> ListPasskeys(ListPasskeysRequest request, ServerCallContext context)
    {
        var response = new ListPasskeysResponse();
        response.Items.AddRange((await _auth.ListPasskeysAsync(UserId(), context.CancellationToken)).Select(p => new PasskeyEntry
        {
            Id = p.Id.ToString(),
            Name = p.Name,
            AddedAt = Timestamp.FromDateTimeOffset(p.AddedAt),
            LastUsedAt = p.LastUsedAt is { } used ? Timestamp.FromDateTimeOffset(used) : null,
        }));
        return response;
    }

    [Authorize]
    [RequiresRecentMfa]
    public override async Task<RemovePasskeyResponse> RemovePasskey(RemovePasskeyRequest request, ServerCallContext context)
    {
        await _auth.RemovePasskeyAsync(UserId(), GrpcRequest.RequiredGuid(request.PasskeyId, "passkey_id"), Client(context), context.CancellationToken);
        return new RemovePasskeyResponse();
    }

    [AllowAnonymous]
    public override async Task<GetSignInReportResponse> GetSignInReport(GetSignInReportRequest request, ServerCallContext context)
    {
        var report = await _auth.GetSignInReportAsync(request.ReportToken, context.CancellationToken);
        return new GetSignInReportResponse
        {
            Device = report.Device,
            IpAddress = report.IpAddress ?? string.Empty,
            SignedInAt = Timestamp.FromDateTimeOffset(report.SignedInAt),
            HadPassword = report.HadPassword,
        };
    }

    [AllowAnonymous]
    public override async Task<SecureAccountResponse> SecureAccount(SecureAccountRequest request, ServerCallContext context)
    {
        await _auth.SecureAccountAsync(request.ReportToken, Client(context), context.CancellationToken);
        ClearCookie(context); // if it's opened in a signed-in browser, that session is over too
        return new SecureAccountResponse();
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

    private SignedIn Complete(Application.Auth.SignedIn signedIn, ServerCallContext context) => _responses.Complete(signedIn, context);

    private static string? ReadCookie(ServerCallContext context) => SignInResponses.ReadRefreshCookie(context);

    private void ClearCookie(ServerCallContext context) => _responses.ClearRefreshCookie(context);

    private ClientInfo Client(ServerCallContext context) => _responses.Client(context);

    // [Authorize] guarantees a validated token, and every token EstateHub issues carries both.
    private Guid UserId() => Guid.Parse(_user.UserId!);

    private Guid SessionId() => _user.SessionId ?? throw new AuthFailedException(Application.Services.AuthService.SessionEnded);
}
