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

    /// <summary>Development/Testing only: signs in a throwaway user with any role, through a real session.</summary>
    Task<SignedIn> DevSignInAsync(string label, Guid? estateId, string role, bool recentMfa, ClientInfo client, CancellationToken ct = default);

    /// <summary>Creates the first platform operator (create-super-admin command). MFA is set up at first sign-in.</summary>
    Task<User> CreateSuperAdminAsync(string email, string displayName, string password, CancellationToken ct = default);
}

/// <summary>Where a call came from, for sessions and the audit log.</summary>
public record ClientInfo(string? IpAddress, string? UserAgent);

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

/// <summary>What one access token allows: whose, which session, which estate, which role.</summary>
public record AccessScope(Guid UserId, string Email, Guid SessionId, Guid? EstateId, string Role, IReadOnlyList<string> Permissions);

/// <summary>The estate picker: the user's estates, and whether "platform administration" is a choice too.</summary>
public record MyEstates(IReadOnlyList<UserMembership> Items, bool PlatformAccess);

public record TotpEnrolment(string Secret, string OtpAuthUri);

/// <summary>A code was (or, for unknown addresses, seemingly was) sent. <see cref="Destination"/> is masked.</summary>
public record OtpStep(string ChallengeToken, string Channel, string Destination, DateTimeOffset ExpiresAt);

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
    Task<RefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct = default);
    Task<MfaChallenge?> FindChallengeAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<RecoveryCode>> ListUnusedRecoveryCodesAsync(Guid userId, CancellationToken ct = default);
    Task DeleteRecoveryCodesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Creates or updates the user's membership in the ACTIVE estate (tenant context).</summary>
    Task UpsertMembershipAsync(Guid userId, string role, CancellationToken ct = default);

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
