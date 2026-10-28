using EstateHub.Domain.Entities;

namespace EstateHub.Application.Auth;

/// <summary>
/// Sign-in flows (PHASE1.md §2a). Results are plain records; the gRPC layer maps them to protos and
/// moves the refresh token into its HttpOnly cookie (AUTH-10).
/// </summary>
public interface IAuthService
{
    /// <summary>Email + password. Staff (and anyone with MFA) get an MFA step; others are signed in.</summary>
    Task<SignInStep> PasswordSignInAsync(string email, string password, ClientInfo client, CancellationToken ct = default);

    /// <summary>Second step: a 6-digit authenticator code or a recovery code.</summary>
    Task<SignedIn> VerifyMfaAsync(string challengeToken, string code, ClientInfo client, CancellationToken ct = default);

    /// <summary>Staff without MFA yet: a new authenticator secret to scan. Not active until confirmed.</summary>
    Task<TotpEnrolment> BeginTotpEnrolmentAsync(string challengeToken, CancellationToken ct = default);

    /// <summary>Confirms the authenticator with its first code, turns MFA on and signs in. Returns the recovery codes once.</summary>
    Task<SignedIn> ConfirmTotpEnrolmentAsync(string challengeToken, string code, ClientInfo client, CancellationToken ct = default);

    /// <summary>Swaps a refresh token for a new access token and a new refresh token (AUTH-09).</summary>
    Task<SignedIn> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct = default);

    /// <summary>Re-confirms MFA for a signed-in session (step-up, AUTH-12): fresh auth_time, new access token.</summary>
    Task<SignedIn> StepUpAsync(Guid userId, Guid sessionId, string code, ClientInfo client, CancellationToken ct = default);

    /// <summary>Ends the session the refresh token belongs to. Unknown or empty tokens are ignored.</summary>
    Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct = default);

    /// <summary>Ends every session of the user.</summary>
    Task LogoutAllAsync(Guid userId, ClientInfo client, CancellationToken ct = default);

    Task<MyEstates> ListMyEstatesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Moves the session into another estate (or back to platform level with null). New access token.</summary>
    Task<SignedIn> SwitchEstateAsync(Guid userId, Guid sessionId, Guid? estateId, CancellationToken ct = default);

    /// <summary>
    /// Resident sign-in, step 1 (AUTH-04): a 6-digit code to an email address or SA mobile number.
    /// The answer is the same whether or not an account exists (AUTH-07); caps per address and IP (AUTH-06).
    /// </summary>
    Task<OtpStep> RequestOtpAsync(string identifier, ClientInfo client, CancellationToken ct = default);

    /// <summary>Step 2: the code. Staff (or anyone with MFA) continue to an MFA step.</summary>
    Task<SignInStep> VerifyOtpAsync(string challengeToken, string code, ClientInfo client, CancellationToken ct = default);

    /// <summary>What an invitation link is for (estate, role, where the code will go). NOT_FOUND when no longer valid.</summary>
    Task<InvitationInfo> GetInvitationAsync(string invitationToken, CancellationToken ct = default);

    /// <summary>Sends a code to the invited address: accepting proves the invitee controls it (AUTH-01).</summary>
    Task<OtpStep> StartInvitationAsync(string invitationToken, ClientInfo client, CancellationToken ct = default);

    /// <summary>The code (and a new password for staff without one): joins the estate and signs in, or continues to MFA setup.</summary>
    Task<SignInStep> AcceptInvitationAsync(string challengeToken, string code, string? newPassword, ClientInfo client, CancellationToken ct = default);

    /// <summary>The caller's signed-in devices (AUTH-11), newest activity first.</summary>
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct = default);

    /// <summary>Signs out one of the caller's own devices. NOT_FOUND for anyone else's session.</summary>
    Task RevokeSessionAsync(Guid userId, Guid sessionId, ClientInfo client, CancellationToken ct = default);

    /// <summary>Signs out every device except this one.</summary>
    Task<int> SignOutOtherSessionsAsync(Guid userId, Guid currentSessionId, ClientInfo client, CancellationToken ct = default);

    /// <summary>
    /// Needs the current password (if one is set). Every other session ends (security stamp, AUTH-11);
    /// this one carries on. An email confirms the change.
    /// </summary>
    Task ChangePasswordAsync(Guid userId, Guid sessionId, string? currentPassword, string newPassword, ClientInfo client, CancellationToken ct = default);

    /// <summary>Forgot password, step 1: a code to the email/mobile, same answer for unknown addresses (AUTH-07).</summary>
    Task<OtpStep> RequestPasswordResetAsync(string identifier, ClientInfo client, CancellationToken ct = default);

    /// <summary>Step 2: code + new password. Ends every session; staff still continue to MFA (a reset never skips it).</summary>
    Task<SignInStep> ResetPasswordAsync(string challengeToken, string code, string newPassword, ClientInfo client, CancellationToken ct = default);

    /// <summary>
    /// Own email or mobile, step 1: a code to the NEW address. <paramref name="usable"/> false (e.g. someone
    /// else in the estate has it) creates the step without sending anything, so nothing is revealed.
    /// </summary>
    Task<OtpStep> StartContactChangeAsync(Guid userId, string newContact, bool usable, ClientInfo client, CancellationToken ct = default);

    /// <summary>Step 2: the code. Updates the account; the old address is told.</summary>
    Task<ContactChanged> ConfirmContactChangeAsync(Guid userId, string challengeToken, string code, ClientInfo client, CancellationToken ct = default);

    /// <summary>POPIA: a former resident's account loses its contact details, password, MFA, passkeys and sessions, and is disabled.</summary>
    Task ScrubAccountAsync(Guid userId, string performedBy, CancellationToken ct = default);

    /// <summary>Ten new recovery codes; the old ones stop working.</summary>
    Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid userId, ClientInfo client, CancellationToken ct = default);

    /// <summary>
    /// Lost authenticator (AUTH-03): turns MFA off for <paramref name="userId"/> and ends their sessions,
    /// so their next sign-in sets up a new one. Who may do it is decided by the caller (MembersService,
    /// PlatformService). Audited, and the user is told by email.
    /// </summary>
    Task ResetMfaAsync(Guid userId, string performedBy, CancellationToken ct = default);

    /// <summary>
    /// Support access (AUTH-13): the super admin's session looks at one estate read-only for 30 minutes.
    /// Needs a reason; recorded in that estate's audit log (visible on its Activity page) and alerted.
    /// </summary>
    Task<SignedIn> StartSupportAccessAsync(Guid userId, Guid sessionId, Guid estateId, string reason, ClientInfo client, CancellationToken ct = default);

    /// <summary>Ends support access early; the session goes back to platform level.</summary>
    Task<SignedIn> EndSupportAccessAsync(Guid userId, Guid sessionId, ClientInfo client, CancellationToken ct = default);

    /// <summary>A tablet's guards (AUTH-14): who can sign in on it. The device credential comes from its cookie.</summary>
    Task<GuardTablet> GetGuardTabletAsync(string deviceCredential, CancellationToken ct = default);

    /// <summary>A guard's shift on a paired tablet: their PIN (5 tries, then a 15-minute pause). 12-hour session tied to the tablet.</summary>
    Task<SignedIn> GuardSignInAsync(string deviceCredential, Guid guardUserId, string pin, ClientInfo client, CancellationToken ct = default);

    /// <summary>A guard sets their own PIN for the active estate's tablets.</summary>
    Task SetGuardPinAsync(Guid userId, string pin, ClientInfo client, CancellationToken ct = default);

    /// <summary>Signed in with a fresh MFA: options for the browser to create a passkey, and a token for the second step.</summary>
    Task<(string ChallengeToken, string OptionsJson)> BeginPasskeyRegistrationAsync(Guid userId, CancellationToken ct = default);

    Task FinishPasskeyRegistrationAsync(Guid userId, string challengeToken, string credentialJson, string name, ClientInfo client, CancellationToken ct = default);

    /// <summary>Sign in with a passkey, step 1 (anonymous).</summary>
    Task<(string ChallengeToken, string OptionsJson)> BeginPasskeySignInAsync(ClientInfo client, CancellationToken ct = default);

    /// <summary>Step 2: the browser's signed answer. A passkey counts as MFA (it is possession + user verification).</summary>
    Task<SignedIn> FinishPasskeySignInAsync(string challengeToken, string credentialJson, ClientInfo client, CancellationToken ct = default);

    Task<IReadOnlyList<PasskeyInfo>> ListPasskeysAsync(Guid userId, CancellationToken ct = default);

    Task RemovePasskeyAsync(Guid userId, Guid passkeyId, ClientInfo client, CancellationToken ct = default);

    /// <summary>What the "This wasn't me" link is about (device, IP, time). NOT_FOUND when used or expired.</summary>
    Task<SignInReportInfo> GetSignInReportAsync(string reportToken, CancellationToken ct = default);

    /// <summary>
    /// "This wasn't me": signs out every device, cancels the password (choose a new one with "Forgot
    /// your password?"), removes passkeys added since that sign-in, and alerts the operator.
    /// </summary>
    Task SecureAccountAsync(string reportToken, ClientInfo client, CancellationToken ct = default);

    /// <summary>Development/Testing only: signs in a throwaway user with any role, through a real session.</summary>
    Task<SignedIn> DevSignInAsync(string label, Guid? estateId, string role, bool recentMfa, ClientInfo client, CancellationToken ct = default);

    /// <summary>Creates the first platform operator (create-super-admin command). MFA is set up at first sign-in.</summary>
    Task<User> CreateSuperAdminAsync(string email, string displayName, string password, CancellationToken ct = default);
}

/// <summary>
/// Where a call came from, for sessions and the audit log. <see cref="HumanCheckToken"/> is a
/// Cloudflare Turnstile token when the browser sent one (x-human-check header); <see cref="DeviceId"/>
/// the browser's random device id (HttpOnly cookie), used to spot sign-ins from new devices.
/// </summary>
public record ClientInfo(string? IpAddress, string? UserAgent, string? HumanCheckToken = null, string? DeviceId = null);

/// <summary>Either signed in, or an MFA step to complete with <see cref="ChallengeToken"/>.</summary>
public record SignInStep(SignedIn? SignedIn, string? ChallengeToken, bool EnrolmentRequired, DateTimeOffset? ChallengeExpiresAt);

/// <summary>
/// A new access token and what it allows. <see cref="RefreshToken"/> is set when a new one was issued
/// (the caller stores it in the cookie); <see cref="RecoveryCodes"/> only right after MFA enrolment.
/// </summary>
public record SignedIn(
    IssuedToken Access,
    AccessScope Scope,
    string? RefreshToken,
    DateTimeOffset? RefreshExpiresAt,
    IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// What one access token allows: whose, which session, which estate, which role.
/// <see cref="SupportEndsAt"/> is set during support access (AUTH-13).
/// </summary>
public record AccessScope(
    Guid UserId, string Email, Guid SessionId, Guid? EstateId, string Role, IReadOnlyList<string> Permissions, DateTimeOffset? SupportEndsAt = null);

/// <summary>The estate picker: the user's estates, and whether "platform administration" is a choice too.</summary>
public record MyEstates(IReadOnlyList<UserMembership> Items, bool PlatformAccess);

public record TotpEnrolment(string Secret, string OtpAuthUri);

public record SignInReportInfo(string Device, string? IpAddress, DateTimeOffset SignedInAt, bool HadPassword);

public record PasskeyInfo(Guid Id, string Name, DateTimeOffset AddedAt, DateTimeOffset? LastUsedAt);

/// <summary>A paired tablet and the guards who can sign in on it.</summary>
public record GuardTablet(string EstateName, string Label, IReadOnlyList<TabletGuard> Guards);

public record TabletGuard(Guid UserId, string Name, bool HasPin);

/// <summary>One signed-in device, for the sessions list.</summary>
public record SessionInfo(Guid Id, string Device, string? IpAddress, DateTimeOffset SignedInAt, DateTimeOffset LastSeenAt, bool IsCurrent);

/// <summary>"Chrome on macOS" from a User-Agent string: enough for a person to recognise their device.</summary>
public static class DeviceNames
{
    public static string Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Unknown device";

        var ua = userAgent;
        var browser =
            ua.Contains("Edg/") ? "Edge" :
            ua.Contains("OPR/") ? "Opera" :
            ua.Contains("Firefox/") ? "Firefox" :
            ua.Contains("Chrome/") ? "Chrome" :
            ua.Contains("Safari/") ? "Safari" : "Browser";
        var os =
            ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" :
            ua.Contains("Android") ? "Android" :
            ua.Contains("Windows") ? "Windows" :
            ua.Contains("Mac OS X") ? "macOS" :
            ua.Contains("Linux") ? "Linux" : null;
        return os is null ? browser : $"{browser} on {os}";
    }
}

/// <summary>A code was (or, for unknown addresses, seemingly was) sent. <see cref="Destination"/> is masked.</summary>
public record OtpStep(string ChallengeToken, string Channel, string Destination, DateTimeOffset ExpiresAt);

public record ContactChanged(string Channel, string? OldValue, string NewValue);

public record InvitationInfo(
    string EstateName, string Role, string Name, string Channel, string Destination, bool PasswordRequired, DateTimeOffset ExpiresAt);

/// <summary>An active membership of a user, with the estate's name (for the estate picker).</summary>
public record UserMembership(Guid EstateId, string EstateName, string Role);

/// <summary>Sign-in failed. Mapped to UNAUTHENTICATED; the message is shown as is, so it never says which part was wrong.</summary>
public class AuthFailedException(string message) : Exception(message);

/// <summary>A cap was hit (codes, invitations). Mapped to RESOURCE_EXHAUSTED; the message is shown as is.</summary>
public class RateLimitedException(string message) : Exception(message);

/// <summary>Allowed to call the RPC, but not to do this particular thing (e.g. grant a higher role). Mapped to PERMISSION_DENIED.</summary>
public class ActionNotAllowedException(string message) : Exception(message);

/// <summary>Users, sessions, refresh tokens, MFA challenges and recovery codes.</summary>
public interface IAuthRepository
{
    Task<User?> FindUserByEmailAsync(string normalisedEmail, CancellationToken ct = default);
    Task<User?> FindUserByPhoneAsync(string e164, CancellationToken ct = default);
    Task<User?> GetUserAsync(Guid id, CancellationToken ct = default);
    Task<OtpChallenge?> FindOtpChallengeAsync(string tokenHash, CancellationToken ct = default);
    Task<int> CountOtpChallengesAsync(string identifierHash, DateTime since, CancellationToken ct = default);
    Task<int> CountOtpChallengesFromIpAsync(string ipAddress, string? channel, DateTime since, CancellationToken ct = default);

    /// <summary>In the ACTIVE estate (tenant context), so the estate must be known first (IInvitationDirectory).</summary>
    Task<Invitation?> FindInvitationAsync(string tokenHash, CancellationToken ct = default);
    Task<Invitation?> GetInvitationAsync(Guid id, CancellationToken ct = default);
    Task<AuthSession?> GetSessionAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AuthSession>> ListOpenSessionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Open sessions started on a guard tablet (revoking the tablet ends them).</summary>
    Task<IReadOnlyList<AuthSession>> ListOpenDeviceSessionsAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>In the ACTIVE estate (tenant context).</summary>
    Task<GuardDevice?> FindGuardDeviceAsync(string credentialHash, CancellationToken ct = default);
    Task<GuardPin?> FindGuardPinAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Active guards of the ACTIVE estate, with whether they have a PIN.</summary>
    Task<IReadOnlyList<TabletGuard>> ListTabletGuardsAsync(CancellationToken ct = default);

    Task<PasskeyChallenge?> FindPasskeyChallengeAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<UserPasskey>> ListPasskeysAsync(Guid userId, CancellationToken ct = default);
    void Remove(object entity);

    Task<IReadOnlyList<KnownDevice>> ListKnownDevicesAsync(Guid userId, CancellationToken ct = default);
    Task<SignInReport?> FindSignInReportAsync(string tokenHash, CancellationToken ct = default);
    Task<RefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct = default);
    Task<MfaChallenge?> FindChallengeAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<RecoveryCode>> ListUnusedRecoveryCodesAsync(Guid userId, CancellationToken ct = default);
    Task DeleteRecoveryCodesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Creates or updates the user's membership in the ACTIVE estate (tenant context).</summary>
    Task UpsertMembershipAsync(Guid userId, string role, CancellationToken ct = default);

    /// <summary>Links the ACTIVE estate's person record to the user's account (invitation accepted).</summary>
    Task LinkPersonAsync(Guid personId, Guid userId, CancellationToken ct = default);

    void Add<T>(T entity) where T : class;
    Task SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>
/// A user's memberships across ALL estates. Sign-in has to look past the tenant boundary, which
/// row-level security forbids for the app's database role, so this goes through a narrow
/// SECURITY DEFINER function that returns only one user's active memberships (docs/tenancy-and-features.md).
/// </summary>
public interface IMembershipDirectory
{
    Task<IReadOnlyList<UserMembership>> ForUserAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Which estate an invitation token belongs to. Accepting happens before any estate is active, which
/// RLS forbids, so this goes through the narrow estatehub_invitation_estate SECURITY DEFINER function.
/// </summary>
public interface IInvitationDirectory
{
    Task<Guid?> EstateForTokenAsync(string tokenHash, CancellationToken ct = default);
}

/// <summary>
/// Which estate a guard tablet credential or a pairing code belongs to. Both are used before any
/// estate is active, so (like invitations) they go through a narrow SECURITY DEFINER function.
/// </summary>
public interface IDeviceDirectory
{
    Task<Guid?> EstateForDeviceAsync(string credentialHash, CancellationToken ct = default);
    Task<Guid?> EstateForPairingCodeAsync(string codeHash, CancellationToken ct = default);
}

/// <summary>
/// Passkeys (WebAuthn): browser options and verification. Implemented with ASP.NET Core Identity's
/// PasskeyHandler; the state between the two steps is kept by the caller (PasskeyChallenge).
/// </summary>
public interface IPasskeys
{
    Task<(string OptionsJson, string State)> CreationOptionsAsync(User user);

    /// <summary>Null on success, otherwise why the passkey was refused.</summary>
    Task<string?> RegisterAsync(User user, string credentialJson, string state, string name);

    Task<(string OptionsJson, string State)> RequestOptionsAsync();

    /// <summary>The passkey's owner when the signature checks out, otherwise null.</summary>
    Task<User?> SignInAsync(string credentialJson, string state);
}

/// <summary>
/// "Are you a person?" (Cloudflare Turnstile, AUTH-05) after repeated failures from one IP address.
/// Disabled when no secret key is configured.
/// </summary>
public interface IHumanCheck
{
    bool Enabled { get; }

    Task<bool> VerifyAsync(string? token, string? ipAddress, CancellationToken ct = default);
}

/// <summary>Recent sign-in failures per IP address, to decide when to ask for the human check.</summary>
public interface ISignInFailures
{
    void Record(string? ipAddress);

    bool IsSuspicious(string? ipAddress);
}

/// <summary>Mapped to FAILED_PRECONDITION with the "estatehub-challenge: turnstile" trailer: show the check, then retry.</summary>
public class HumanCheckRequiredException() : Exception("Please confirm you're a person, then try again.");

/// <summary>
/// Security alerts (SECURITY.md layer 12) to the operator: ntfy push and/or email, as configured.
/// Repeats of the same alert are held back for a while; a failure to alert never fails the action.
/// Never put codes, tokens or passwords in an alert.
/// </summary>
public interface IAlerts
{
    Task RaiseAsync(string title, string details, CancellationToken ct = default);
}

/// <summary>Per-request check that the token's session is still alive and still allows its scope (≈30 s cache).</summary>
public interface ISessionValidator
{
    Task<bool> IsValidAsync(Guid sessionId, Guid userId, Guid? estateId, string? role, CancellationToken ct = default);

    /// <summary>Drops the cached answer so a revocation applies immediately on this server.</summary>
    void Forget(Guid sessionId);
}

/// <summary>ASP.NET Core Identity's PBKDF2 hasher behind a small interface (AUTH-02).</summary>
public interface IPasswordHashing
{
    string Hash(string password);

    /// <summary>True when it matches. <paramref name="rehash"/> is set when the stored hash uses old parameters.</summary>
    bool Verify(string hash, string password, out bool rehash);
}

/// <summary>Encrypts secrets at rest with ASP.NET Data Protection (AUTH-18).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

/// <summary>Have I Been Pwned range check (k-anonymity: only 5 hash characters leave the server).</summary>
public interface IBreachedPasswords
{
    Task<bool> IsBreachedAsync(string password, CancellationToken ct = default);
}
