using EstateHub.Application.Auth;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace EstateHub.Api.Security;

/// <summary>
/// Turns a sign-in result into the gRPC response, moving the refresh token into its HttpOnly cookie
/// (AUTH-10), and handles the guard tablet's device cookie (AUTH-14). Shared by every service that
/// signs someone in, so the cookie rules live in one place.
/// </summary>
public sealed class SignInResponses
{
    public const string DeviceCookieName = "estatehub_device";

    /// <summary>Only GuardDeviceService RPCs receive the tablet's credential.</summary>
    public const string DeviceCookiePath = "/estatehub.devices.v1.GuardDeviceService/";

    /// <summary>A paired tablet stays paired until revoked; the cookie just needs to outlive normal use.</summary>
    public static readonly TimeSpan DeviceCookieLifetime = TimeSpan.FromDays(400);

    private readonly AuthCookieOptions _cookie;
    private readonly ClientIpResolver _ip;

    public SignInResponses(IOptions<AuthCookieOptions> cookie, ClientIpResolver ip)
    {
        _cookie = cookie.Value;
        _ip = ip;
    }

    public SignedIn Complete(Application.Auth.SignedIn signedIn, ServerCallContext context)
    {
        if (signedIn.RefreshToken is { } refresh)
        {
            var http = context.GetHttpContext();
            http.Response.Cookies.Append(AuthCookieOptions.Name, refresh, Options(AuthCookieOptions.Path, signedIn.RefreshExpiresAt));

            // A new session: make sure this browser keeps the device id it signed in with (see Client).
            if (http.Items[NewDeviceIdKey] is string deviceId)
                http.Response.Cookies.Append(DeviceIdCookieName, deviceId, Options(AuthCookieOptions.Path, DateTimeOffset.UtcNow + DeviceCookieLifetime));
        }

        var response = new SignedIn
        {
            AccessToken = signedIn.Access.AccessToken,
            ExpiresAt = Timestamp.FromDateTimeOffset(signedIn.Access.ExpiresAt),
            UserId = signedIn.Scope.UserId.ToString(),
            Email = signedIn.Scope.Email,
            EstateId = signedIn.Scope.EstateId?.ToString() ?? string.Empty,
            Role = signedIn.Scope.Role,
            SupportEndsAt = signedIn.Scope.SupportEndsAt is { } ends ? Timestamp.FromDateTimeOffset(ends) : null,
        };
        response.Permissions.AddRange(signedIn.Scope.Permissions);
        response.RecoveryCodes.AddRange(signedIn.RecoveryCodes);
        return response;
    }

    public static string? ReadRefreshCookie(ServerCallContext context) => context.GetHttpContext().Request.Cookies[AuthCookieOptions.Name];

    public void ClearRefreshCookie(ServerCallContext context) =>
        context.GetHttpContext().Response.Cookies.Delete(AuthCookieOptions.Name, Options(AuthCookieOptions.Path, null));

    public static string? ReadDeviceCookie(ServerCallContext context) => context.GetHttpContext().Request.Cookies[DeviceCookieName];

    public void SetDeviceCookie(ServerCallContext context, string credential, DateTimeOffset now) =>
        context.GetHttpContext().Response.Cookies.Append(DeviceCookieName, credential, Options(DeviceCookiePath, now + DeviceCookieLifetime));

    public void ClearDeviceCookie(ServerCallContext context) =>
        context.GetHttpContext().Response.Cookies.Delete(DeviceCookieName, Options(DeviceCookiePath, null));

    /// <summary>Request header carrying a Cloudflare Turnstile token (AUTH-05), when the browser was asked for one.</summary>
    public const string HumanCheckHeader = "x-human-check";

    /// <summary>Trailer telling the browser to show the human check and repeat the call.</summary>
    public const string ChallengeTrailer = "estatehub-challenge";

    /// <summary>
    /// The browser's random device id (HttpOnly, AuthService path only): how a sign-in from a new device
    /// is recognised (KnownDevice). A browser without one gets a new id with its next session.
    /// </summary>
    public const string DeviceIdCookieName = "estatehub_device_id";
    private const string NewDeviceIdKey = "estatehub.new-device-id";

    public ClientInfo Client(ServerCallContext context)
    {
        var http = context.GetHttpContext();
        var token = http.Request.Headers[HumanCheckHeader].ToString();

        var deviceId = http.Request.Cookies[DeviceIdCookieName];
        if (string.IsNullOrEmpty(deviceId) || deviceId.Length > 100)
        {
            deviceId = AuthSecrets.NewToken();
            http.Items[NewDeviceIdKey] = deviceId;
        }

        return new ClientInfo(_ip.Resolve(http), http.Request.Headers.UserAgent.ToString(), token.Length > 0 ? token : null, deviceId);
    }

    private CookieOptions Options(string path, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = _cookie.RefreshCookieSecure,
        SameSite = SameSiteMode.Strict,
        Path = path,
        Expires = expires,
        IsEssential = true,
    };
}
