using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Application.Interfaces;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace EstateHub.Application.Services;

/// <summary>
/// Sign-in, sessions and estate switching (PHASE1.md §2a, AUTH-xx in comments). Every outcome that
/// matters for security is written to the audit log (AUTH-16).
/// </summary>
public partial class AuthService : IAuthService
{
    public const int MinPasswordLength = 10;
    public const int MaxPasswordLength = 128;
    public const int MaxFailedAttempts = 5;
    public const int MaxChallengeAttempts = 5;

    public static readonly TimeSpan FirstLockout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromHours(24);
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StaffSessionLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan ResidentSessionLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Two tabs refreshing at the same moment both send the same cookie. Within this window the
    /// second one gets an access token instead of tripping theft detection.
    /// </summary>
    public static readonly TimeSpan RefreshReuseGrace = TimeSpan.FromSeconds(30);

    /// <summary>Step-up window (AUTH-12). The API's RequiresRecentMfaAttribute.MaxAge uses this value.</summary>
    public static readonly TimeSpan RecentMfaMaxAge = TimeSpan.FromMinutes(15);

    /// <summary>The same answer for unknown email, wrong password, disabled or locked account (AUTH-07).</summary>
    public const string SignInFailed = "Those sign-in details didn't work. After several failed attempts, sign-in pauses for a while.";
    public const string CodeFailed = "That code didn't work. Check your authenticator app and try again.";
    public const string StepExpired = "This sign-in step has expired. Please start again.";
    public const string SessionEnded = "Your session has ended. Please sign in again.";

    private const string Issuer = "EstateHub";
    private const string DevEmailDomain = "dev.estatehub.local";
    private const int MaxUserAgentLength = 512;

    // Unknown emails still pay for one password hash, so response times don't reveal which emails exist.
    private readonly Lazy<string> _dummyHash;

    private readonly IAuthRepository _repo;
    private readonly IMembershipDirectory _directory;
    private readonly IEstateRepository _estates;
    private readonly ITokenService _tokens;
    private readonly IPasswordHashing _passwords;
    private readonly ISecretProtector _secrets;
    private readonly IBreachedPasswords _breached;
    private readonly ISessionValidator _validator;
    private readonly ITenantContext _tenant;
    private readonly IAuditLogService _audit;
    private readonly IInvitationDirectory _invitations;
    private readonly IEmailSender _email;
    private readonly ISmsSender _sms;
    private readonly IDeviceDirectory _devices;
    private readonly IAlerts _alerts;
    private readonly IPasskeys _passkeys;
    private readonly IAppLinks _links;
    private readonly IHumanCheck _humans;
    private readonly ISignInFailures _failures;
    private readonly ILogger<AuthService> _logger;
    private readonly TimeProvider _clock;

    public AuthService(
        IAuthRepository repo,
        IMembershipDirectory directory,
        IEstateRepository estates,
        ITokenService tokens,
        IPasswordHashing passwords,
        ISecretProtector secrets,
        IBreachedPasswords breached,
        ISessionValidator validator,
        ITenantContext tenant,
        IAuditLogService audit,
        IInvitationDirectory invitations,
        IEmailSender email,
        ISmsSender sms,
        IDeviceDirectory devices,
        IAlerts alerts,
        IPasskeys passkeys,
        IAppLinks links,
        IHumanCheck humans,
        ISignInFailures failures,
        TimeProvider clock,
        ILogger<AuthService> logger)
    {
        _repo = repo;
        _directory = directory;
        _estates = estates;
        _tokens = tokens;
        _passwords = passwords;
        _secrets = secrets;
        _breached = breached;
        _validator = validator;
        _tenant = tenant;
        _audit = audit;
        _invitations = invitations;
        _email = email;
        _sms = sms;
        _devices = devices;
        _alerts = alerts;
        _passkeys = passkeys;
        _links = links;
        _humans = humans;
        _failures = failures;
        _logger = logger;
        _clock = clock;
        _dummyHash = new Lazy<string>(() => _passwords.Hash(AuthSecrets.NewToken()));
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<SignInStep> PasswordSignInAsync(string email, string password, ClientInfo client, CancellationToken ct = default)
    {
        email = User.NormaliseEmail(email ?? string.Empty);
        password ??= string.Empty;
        if (email.Length == 0 || password.Length == 0 || password.Length > MaxPasswordLength)
            throw new AuthFailedException(SignInFailed);

        await RequireHumanIfSuspiciousAsync(client, ct);

        var user = await _repo.FindUserByEmailAsync(email, ct);
        if (user?.PasswordHash is null || user.Status != UserStatus.Active)
        {
            _passwords.Verify(_dummyHash.Value, password, out _);
            _failures.Record(client.IpAddress);
            await AuditAsync(user?.Id.ToString() ?? "unknown", "SignInFailed", "anonymous", client, $"email={Truncate(email, 254)}", ct);
            throw new AuthFailedException(SignInFailed);
        }

        if (IsLockedOut(user))
        {
            await AuditAsync(user.Id.ToString(), "SignInFailed", user.Id.ToString(), client, "reason=locked_out", ct);
            throw new AuthFailedException(SignInFailed);
        }

        if (!_passwords.Verify(user.PasswordHash, password, out var rehash))
        {
            await RegisterFailureAsync(user, "wrong_password", client, ct);
            throw new AuthFailedException(SignInFailed);
        }

        if (rehash)
            user.PasswordHash = _passwords.Hash(password);

        return await AfterFirstFactorAsync(user, "pwd", landIn: null, client, ct);
    }

    /// <summary>
    /// The first step (password or emailed/texted code) passed. Staff and anyone with MFA continue to
    /// an MFA step (set up first if needed); everyone else is signed in.
    /// </summary>
    private async Task<SignInStep> AfterFirstFactorAsync(User user, string factor, Guid? landIn, ClientInfo client, CancellationToken ct)
    {
        var memberships = await _directory.ForUserAsync(user.Id, ct);
        if (!user.MfaEnabled && !AuthScopes.IsStaff(user, memberships))
        {
            ResetFailures(user);
            var signedIn = await StartSessionAsync(user, [factor], landIn, client, ct);
            await AuditAsync(user.Id.ToString(), "SignedIn", user.Id.ToString(), client, $"method={MethodName(factor)}", ct);
            return new SignInStep(signedIn, null, false, null);
        }

        // Failures are NOT reset yet: a right first step doesn't earn fresh guesses at the second factor.
        var token = AuthSecrets.NewToken();
        var challenge = new MfaChallenge
        {
            UserId = user.Id,
            TokenHash = AuthSecrets.Hash(token),
            IsEnrolment = !user.MfaEnabled,
            FirstFactor = factor,
            LandInEstateId = landIn,
            ExpiresAt = Now + ChallengeLifetime,
            IpAddress = client.IpAddress,
        };
        _repo.Add(challenge);
        await _repo.SaveChangesAsync(ct);

        return new SignInStep(null, token, challenge.IsEnrolment, Utc(challenge.ExpiresAt));
    }

    private Task AlertIfSuperAdminAsync(User user, ClientInfo client, CancellationToken ct) =>
        user.IsSuperAdmin
            ? _alerts.RaiseAsync("Super admin signed in", $"{ContactPoints.Mask(user.Contact)} signed in from {DeviceNames.Describe(client.UserAgent)} (IP {client.IpAddress}).", ct)
            : Task.CompletedTask;

    private static string MethodName(string factor) => factor == "otp" ? "code" : "password";

    public async Task<SignedIn> VerifyMfaAsync(string challengeToken, string code, ClientInfo client, CancellationToken ct = default)
    {
        var challenge = await OpenChallengeAsync(challengeToken, enrolment: false, ct);
        var user = await ActiveUserAsync(challenge.UserId, ct);

        var method = await VerifySecondFactorAsync(user, code ?? string.Empty, ct);
        if (method is null)
        {
            challenge.Attempts++;
            await RegisterFailureAsync(user, "wrong_mfa_code", client, ct);
            throw new AuthFailedException(CodeFailed);
        }

        challenge.ConsumedAt = Now;
        ResetFailures(user);
        var signedIn = await StartSessionAsync(user, [challenge.FirstFactor, TokenClaims.MfaMethod], challenge.LandInEstateId, client, ct);
        await AuditAsync(user.Id.ToString(), "SignedIn", user.Id.ToString(), client, $"method={MethodName(challenge.FirstFactor)}+{method}", ct);
        await AlertIfSuperAdminAsync(user, client, ct);
        return signedIn;
    }

    public async Task<TotpEnrolment> BeginTotpEnrolmentAsync(string challengeToken, CancellationToken ct = default)
    {
        var challenge = await OpenChallengeAsync(challengeToken, enrolment: true, ct);
        var user = await ActiveUserAsync(challenge.UserId, ct);

        var secret = Totp.NewSecret();
        challenge.PendingTotpSecretEncrypted = _secrets.Protect(secret);
        await _repo.SaveChangesAsync(ct);

        return new TotpEnrolment(secret, Totp.OtpAuthUri(Issuer, user.Contact, secret));
    }

    public async Task<SignedIn> ConfirmTotpEnrolmentAsync(string challengeToken, string code, ClientInfo client, CancellationToken ct = default)
    {
        var challenge = await OpenChallengeAsync(challengeToken, enrolment: true, ct);
        var user = await ActiveUserAsync(challenge.UserId, ct);
        if (challenge.PendingTotpSecretEncrypted is null)
            throw new ArgumentException("Start the authenticator setup first.");

        var secret = _secrets.Unprotect(challenge.PendingTotpSecretEncrypted);
        var step = Totp.Verify(secret, code ?? string.Empty, _clock.GetUtcNow(), lastUsedStep: null);
        if (step is null)
        {
            challenge.Attempts++;
            await _repo.SaveChangesAsync(ct);
            throw new AuthFailedException(CodeFailed);
        }

        user.TotpSecretEncrypted = challenge.PendingTotpSecretEncrypted;
        user.TotpLastUsedStep = step;
        user.MfaEnabled = true;
        user.SecurityStamp = NewStamp();
        challenge.ConsumedAt = Now;
        ResetFailures(user);

        var codes = AuthSecrets.NewRecoveryCodes();
        await _repo.DeleteRecoveryCodesAsync(user.Id, ct);
        foreach (var recovery in codes)
            _repo.Add(new RecoveryCode { UserId = user.Id, CodeHash = AuthSecrets.Hash(AuthSecrets.NormaliseRecoveryCode(recovery)) });

        var signedIn = await StartSessionAsync(user, [challenge.FirstFactor, TokenClaims.MfaMethod], challenge.LandInEstateId, client, ct);
        await AuditAsync(user.Id.ToString(), "MfaEnrolled", user.Id.ToString(), client, "method=totp", ct);
        await AuditAsync(user.Id.ToString(), "SignedIn", user.Id.ToString(), client, $"method={MethodName(challenge.FirstFactor)}+totp", ct);
        await AlertIfSuperAdminAsync(user, client, ct);
        return signedIn with { RecoveryCodes = codes };
    }

    public async Task<SignedIn> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new AuthFailedException(SessionEnded);

        var token = await _repo.FindRefreshTokenAsync(AuthSecrets.Hash(refreshToken), ct)
            ?? throw new AuthFailedException(SessionEnded);
        var session = await _repo.GetSessionAsync(token.SessionId, ct)
            ?? throw new AuthFailedException(SessionEnded);
        var user = await _repo.GetUserAsync(session.UserId, ct);

        if (!IsAlive(session, user) || token.ExpiresAt <= Now)
            throw new AuthFailedException(SessionEnded);

        if (token.UsedAt is { } usedAt)
        {
            if (Now - usedAt <= RefreshReuseGrace)
                return await IssueAsync(user!, session, refreshToken: null, ct);

            // An old token came back after it was exchanged: someone else has a copy (AUTH-09).
            Revoke(session, "refresh_token_reused");
            await _repo.SaveChangesAsync(ct);
            await AuditAsync(user!.Id.ToString(), "RefreshTokenReused", user.Id.ToString(), client, $"session={session.Id}", ct);
            await _alerts.RaiseAsync("Stolen sign-in token detected",
                $"An old refresh token for {ContactPoints.Mask(user.Contact)} was used again (IP {client.IpAddress}). That session has been ended.", ct);
            throw new AuthFailedException(SessionEnded);
        }

        session.LastSeenAt = Now;
        session.IpAddress = client.IpAddress;
        session.UserAgent = Truncate(client.UserAgent, MaxUserAgentLength);
        if (!session.IsStaff)
            session.ExpiresAt = Now + ResidentSessionLifetime;

        var next = AuthSecrets.NewToken();
        var replacement = new RefreshToken { SessionId = session.Id, TokenHash = AuthSecrets.Hash(next), ExpiresAt = session.ExpiresAt };
        token.UsedAt = Now;
        token.ReplacedById = replacement.Id;
        _repo.Add(replacement);
        await _repo.SaveChangesAsync(ct);

        return await IssueAsync(user!, session, next, ct);
    }

    public async Task<SignedIn> StepUpAsync(Guid userId, Guid sessionId, string code, ClientInfo client, CancellationToken ct = default)
    {
        var (user, session) = await OwnSessionAsync(userId, sessionId, ct);
        if (!user.MfaEnabled)
            throw new ArgumentException("Set up an authenticator app first.");
        if (IsLockedOut(user))
            throw new AuthFailedException(SignInFailed);

        var method = await VerifySecondFactorAsync(user, code ?? string.Empty, ct);
        if (method is null)
        {
            await RegisterFailureAsync(user, "wrong_step_up_code", client, ct);
            throw new AuthFailedException(CodeFailed);
        }

        ResetFailures(user);
        var methods = AuthScopes.ParseMethods(session.AuthMethods).Append(TokenClaims.MfaMethod).Distinct();
        session.AuthMethods = string.Join(' ', methods);
        session.AuthTime = Now;
        await _repo.SaveChangesAsync(ct);
        _validator.Forget(session.Id);

        await AuditAsync(user.Id.ToString(), "SteppedUp", user.Id.ToString(), client, $"method={method}", ct);
        return await IssueAsync(user, session, refreshToken: null, ct);
    }

    public async Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        var token = await _repo.FindRefreshTokenAsync(AuthSecrets.Hash(refreshToken), ct);
        var session = token is null ? null : await _repo.GetSessionAsync(token.SessionId, ct);
        if (session is null || session.RevokedAt is not null)
            return;

        Revoke(session, "signed_out");
        await _repo.SaveChangesAsync(ct);
        await AuditAsync(session.UserId.ToString(), "SignedOut", session.UserId.ToString(), client, $"session={session.Id}", ct);
    }

    public async Task LogoutAllAsync(Guid userId, ClientInfo client, CancellationToken ct = default)
    {
        var sessions = await _repo.ListOpenSessionsAsync(userId, ct);
        foreach (var session in sessions)
            Revoke(session, "signed_out_everywhere");
        await _repo.SaveChangesAsync(ct);
        await AuditAsync(userId.ToString(), "SignedOutEverywhere", userId.ToString(), client, $"sessions={sessions.Count}", ct);
    }

    public async Task<MyEstates> ListMyEstatesAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _repo.GetUserAsync(userId, ct);
        var memberships = await _directory.ForUserAsync(userId, ct);
        return new MyEstates(memberships, user is { IsSuperAdmin: true, Status: UserStatus.Active });
    }

    public async Task<SignedIn> SwitchEstateAsync(Guid userId, Guid sessionId, Guid? estateId, CancellationToken ct = default)
    {
        var (user, session) = await OwnSessionAsync(userId, sessionId, ct);
        var memberships = await _directory.ForUserAsync(user.Id, ct);

        // NOT_FOUND, not PERMISSION_DENIED: never confirm that an estate exists (SECURITY.md layer 5).
        _ = AuthScopes.RoleFor(user, AuthScopes.ParseMethods(session.AuthMethods), estateId, memberships)
            ?? throw new KeyNotFoundException("Estate not found.");

        session.ActiveEstateId = estateId;
        ClearSupportAccess(session);
        await _repo.SaveChangesAsync(ct);
        _validator.Forget(session.Id);

        await AuditAsync(user.Id.ToString(), "SwitchedEstate", user.Id.ToString(), null, $"estate={estateId?.ToString() ?? "platform"}", ct, estateId);
        return await IssueAsync(user, session, refreshToken: null, ct, memberships);
    }

    public async Task<SignedIn> DevSignInAsync(string label, Guid? estateId, string role, bool recentMfa, ClientInfo client, CancellationToken ct = default)
    {
        label = (label ?? string.Empty).Trim().ToLowerInvariant();
        if (label.Length is 0 or > 50 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("user_id must be 1–50 letters, digits, '-', '_' or '.'.");
        if (!RolePermissions.IsKnownRole(role) || role == Roles.Support)
            throw new ArgumentException($"Unknown role '{role}'. Known: {string.Join(", ", RolePermissions.AllRoles.Where(r => r != Roles.Support))}.");
        if (role == Roles.SuperAdmin)
            estateId = null;
        else if (estateId is null)
            throw new ArgumentException("estate_id is required for every role except super_admin.");
        if (estateId is { } id && await _estates.GetAsync(id, ct) is null)
            throw new KeyNotFoundException("Estate not found.");

        var email = $"{label}@{DevEmailDomain}";
        var user = await _repo.FindUserByEmailAsync(email, ct);
        if (user is null)
        {
            user = new User { Email = email, DisplayName = label };
            _repo.Add(user);
        }

        if (role == Roles.SuperAdmin)
            user.IsSuperAdmin = true;
        await _repo.SaveChangesAsync(ct);

        if (estateId is { } estate)
        {
            _tenant.Use(estate);
            await _repo.UpsertMembershipAsync(user.Id, role, ct);
        }

        // "MFA over 15 minutes ago" keeps the session usable but makes step-up RPCs ask again.
        var authTime = recentMfa ? Now : Now - RecentMfaMaxAge - TimeSpan.FromMinutes(5);
        var signedIn = await StartSessionAsync(user, ["otp", TokenClaims.MfaMethod], estateId, client, ct, authTime);
        await AuditAsync(user.Id.ToString(), "DevSignIn", user.Id.ToString(), client, $"role={role}", ct);
        return signedIn;
    }

    public async Task<User> CreateSuperAdminAsync(string email, string displayName, string password, CancellationToken ct = default)
    {
        email = User.NormaliseEmail(email ?? string.Empty);
        if (email.Length is < 3 or > 254 || !email.Contains('@'))
            throw new ArgumentException("Enter a valid email address.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Name is required.");
        await ValidateNewPasswordAsync(password, ct);
        if (await _repo.FindUserByEmailAsync(email, ct) is not null)
            throw new ArgumentException("A user with that email already exists.");

        var user = new User
        {
            Email = email,
            DisplayName = displayName.Trim(),
            PasswordHash = _passwords.Hash(password),
            IsSuperAdmin = true,
        };
        _repo.Add(user);
        await _repo.SaveChangesAsync(ct);

        await AuditAsync(user.Id.ToString(), "SuperAdminCreated", "system", null, $"email={email}", ct);
        return user;
    }

    /// <summary>AUTH-02: length only (no composition rules), and not a known-breached password.</summary>
    private async Task ValidateNewPasswordAsync(string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new ArgumentException($"Use at least {MinPasswordLength} characters.");
        if (password.Length > MaxPasswordLength)
            throw new ArgumentException($"Use at most {MaxPasswordLength} characters.");
        if (await _breached.IsBreachedAsync(password, ct))
            throw new ArgumentException("This password has appeared in a known data breach. Choose a different one.");
    }

    private async Task<SignedIn> StartSessionAsync(
        User user, string[] methods, Guid? estateId, ClientInfo client, CancellationToken ct, DateTime? authTime = null, Guid? guardDeviceId = null)
    {
        var memberships = await _directory.ForUserAsync(user.Id, ct);
        // A guard's tablet session is a 12-hour shift, like staff sessions (no sliding).
        var isStaff = AuthScopes.IsStaff(user, memberships) || guardDeviceId is not null;

        var session = new AuthSession
        {
            UserId = user.Id,
            ActiveEstateId = estateId ?? AuthScopes.DefaultEstate(user, memberships),
            AuthMethods = string.Join(' ', methods),
            AuthTime = authTime ?? Now,
            IsStaff = isStaff,
            ExpiresAt = Now + (isStaff ? StaffSessionLifetime : ResidentSessionLifetime),
            LastSeenAt = Now,
            SecurityStamp = user.SecurityStamp,
            IpAddress = client.IpAddress,
            UserAgent = Truncate(client.UserAgent, MaxUserAgentLength),
            GuardDeviceId = guardDeviceId,
        };

        var refresh = AuthSecrets.NewToken();
        _repo.Add(session);
        _repo.Add(new RefreshToken { SessionId = session.Id, TokenHash = AuthSecrets.Hash(refresh), ExpiresAt = session.ExpiresAt });
        await _repo.SaveChangesAsync(ct);
        await NotifyNewDeviceAsync(user, session, client, ct);

        return await IssueAsync(user, session, refresh, ct, memberships);
    }

    /// <summary>
    /// An access token for the session's current scope. If the session's estate is no longer allowed
    /// (membership removed), the session drops back to platform level rather than failing.
    /// </summary>
    private async Task<SignedIn> IssueAsync(
        User user, AuthSession session, string? refreshToken, CancellationToken ct, IReadOnlyList<UserMembership>? memberships = null)
    {
        memberships ??= await _directory.ForUserAsync(user.Id, ct);
        var methods = AuthScopes.ParseMethods(session.AuthMethods);

        var role = AuthScopes.RoleForSession(user, session, session.ActiveEstateId, memberships, Now);
        if (role is null)
        {
            // Membership gone or support access over: back to platform level.
            session.ActiveEstateId = null;
            ClearSupportAccess(session);
            await _repo.SaveChangesAsync(ct);
            role = AuthScopes.RoleFor(user, methods, null, memberships) ?? Roles.None;
        }

        var supporting = role == Roles.Support;
        var permissions = RolePermissions.For(role);
        var access = _tokens.Issue(new TokenRequest(
            user.Id.ToString(), session.Id, session.ActiveEstateId, role, permissions, methods.ToArray(), Utc(session.AuthTime),
            ActorId: supporting ? user.Id.ToString() : null));

        return new SignedIn(
            access,
            new AccessScope(user.Id, user.Contact, session.Id, session.ActiveEstateId, role, permissions,
                supporting ? Utc(session.ImpersonationEndsAt!.Value) : null),
            refreshToken,
            refreshToken is null ? null : Utc(session.ExpiresAt),
            []);
    }

    private async Task<MfaChallenge> OpenChallengeAsync(string token, bool enrolment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new AuthFailedException(StepExpired);

        var challenge = await _repo.FindChallengeAsync(AuthSecrets.Hash(token), ct);
        if (challenge is null
            || challenge.IsEnrolment != enrolment
            || challenge.ConsumedAt is not null
            || challenge.ExpiresAt <= Now
            || challenge.Attempts >= MaxChallengeAttempts)
            throw new AuthFailedException(StepExpired);

        return challenge;
    }

    private async Task<User> ActiveUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await _repo.GetUserAsync(userId, ct);
        if (user is null || user.Status != UserStatus.Active || IsLockedOut(user))
            throw new AuthFailedException(SignInFailed);
        return user;
    }

    private async Task<(User User, AuthSession Session)> OwnSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = await _repo.GetSessionAsync(sessionId, ct);
        var user = session is null ? null : await _repo.GetUserAsync(session.UserId, ct);
        if (session is null || session.UserId != userId || !IsAlive(session, user))
            throw new AuthFailedException(SessionEnded);
        return (user!, session);
    }

    /// <summary>"totp" or "recovery_code" when the code is right (and marks it used), otherwise null.</summary>
    private async Task<string?> VerifySecondFactorAsync(User user, string code, CancellationToken ct)
    {
        if (user.TotpSecretEncrypted is not null)
        {
            var step = Totp.Verify(_secrets.Unprotect(user.TotpSecretEncrypted), code, _clock.GetUtcNow(), user.TotpLastUsedStep);
            if (step is not null)
            {
                user.TotpLastUsedStep = step;
                return "totp";
            }
        }

        var normalised = AuthSecrets.NormaliseRecoveryCode(code);
        if (normalised.Length != 16)
            return null;

        var hash = AuthSecrets.Hash(normalised);
        var match = (await _repo.ListUnusedRecoveryCodesAsync(user.Id, ct)).FirstOrDefault(c => c.CodeHash == hash);
        if (match is null)
            return null;

        match.UsedAt = Now;
        return "recovery_code";
    }

    private bool IsAlive(AuthSession session, User? user) =>
        session.RevokedAt is null
        && session.ExpiresAt > Now
        && user is { Status: UserStatus.Active }
        && user.SecurityStamp == session.SecurityStamp;

    private bool IsLockedOut(User user) => user.LockoutEnd is { } end && end > Now;

    /// <summary>AUTH-05: after 5 failures the account pauses for 15 min, then 30, 60 … (max 24 h).</summary>
    private async Task RegisterFailureAsync(User user, string reason, ClientInfo client, CancellationToken ct)
    {
        _failures.Record(client.IpAddress);
        user.FailedSignInCount++;
        var lockedOut = user.FailedSignInCount >= MaxFailedAttempts;
        if (lockedOut)
        {
            user.LockoutCount++;
            var minutes = Math.Min(FirstLockout.TotalMinutes * Math.Pow(2, user.LockoutCount - 1), MaxLockout.TotalMinutes);
            user.LockoutEnd = Now.AddMinutes(minutes);
            user.FailedSignInCount = 0;
        }

        await _repo.SaveChangesAsync(ct);
        await AuditAsync(user.Id.ToString(), "SignInFailed", user.Id.ToString(), client, $"reason={reason}", ct);
        if (lockedOut)
        {
            await AuditAsync(user.Id.ToString(), "LockedOut", "system", client, $"until={user.LockoutEnd:O}", ct);
            await _alerts.RaiseAsync("Account locked",
                $"{ContactPoints.Mask(user.Contact)} is locked until {Sast(user.LockoutEnd!.Value):HH:mm} after repeated failed sign-ins (IP {client.IpAddress}).", ct);
        }
    }

    private static void ResetFailures(User user)
    {
        user.FailedSignInCount = 0;
        user.LockoutCount = 0;
        user.LockoutEnd = null;
    }

    private void Revoke(AuthSession session, string reason)
    {
        session.RevokedAt = Now;
        session.RevokedReason = reason;
        _validator.Forget(session.Id);
    }

    private Task AuditAsync(
        string userId, string action, string performedBy, ClientInfo? client, string? details, CancellationToken ct, Guid? estateId = null) =>
        _audit.CreateAsync(new CreateAuditLogDto("User", userId, action, performedBy, details, client?.IpAddress, estateId), ct);

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
