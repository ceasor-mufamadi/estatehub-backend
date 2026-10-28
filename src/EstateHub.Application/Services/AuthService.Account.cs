using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace EstateHub.Application.Services;

// Account security (slice 2.3): sessions, passwords, recovery codes, MFA reset, security emails.
public partial class AuthService
{
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct = default)
    {
        var sessions = await _repo.ListOpenSessionsAsync(userId, ct);
        return sessions
            .Where(s => s.ExpiresAt > Now)
            .OrderByDescending(s => s.Id == currentSessionId).ThenByDescending(s => s.LastSeenAt)
            .Select(s => new SessionInfo(s.Id, DeviceNames.Describe(s.UserAgent), s.IpAddress, Utc(s.CreatedAt), Utc(s.LastSeenAt), s.Id == currentSessionId))
            .ToList();
    }

    public async Task RevokeSessionAsync(Guid userId, Guid sessionId, ClientInfo client, CancellationToken ct = default)
    {
        var session = await _repo.GetSessionAsync(sessionId, ct);
        if (session is null || session.UserId != userId || session.RevokedAt is not null)
            throw new KeyNotFoundException("Session not found.");

        Revoke(session, "signed_out_by_user");
        await _repo.SaveChangesAsync(ct);
        await AuditAsync(userId.ToString(), "SessionRevoked", userId.ToString(), client, $"session={sessionId}", ct);
    }

    public async Task<int> SignOutOtherSessionsAsync(Guid userId, Guid currentSessionId, ClientInfo client, CancellationToken ct = default)
    {
        var others = (await _repo.ListOpenSessionsAsync(userId, ct)).Where(s => s.Id != currentSessionId).ToList();
        foreach (var session in others)
            Revoke(session, "signed_out_elsewhere");
        await _repo.SaveChangesAsync(ct);
        await AuditAsync(userId.ToString(), "SignedOutOtherSessions", userId.ToString(), client, $"sessions={others.Count}", ct);
        return others.Count;
    }

    public async Task ChangePasswordAsync(
        Guid userId, Guid sessionId, string? currentPassword, string newPassword, ClientInfo client, CancellationToken ct = default)
    {
        var (user, session) = await OwnSessionAsync(userId, sessionId, ct);

        if (user.PasswordHash is not null && !_passwords.Verify(user.PasswordHash, currentPassword ?? string.Empty, out _))
        {
            await RegisterFailureAsync(user, "wrong_current_password", client, ct);
            throw new ArgumentException("Your current password isn't right.");
        }

        await ValidateNewPasswordAsync(newPassword, ct);
        user.PasswordHash = _passwords.Hash(newPassword);

        // A new stamp ends every other session (AUTH-11); this one is moved onto it and carries on.
        user.SecurityStamp = NewStamp();
        session.SecurityStamp = user.SecurityStamp;
        await _repo.SaveChangesAsync(ct);
        _validator.Forget(session.Id);

        await AuditAsync(user.Id.ToString(), "PasswordChanged", user.Id.ToString(), client, null, ct);
        await NotifyAsync(user, "Your EstateHub password was changed",
            "Your password was just changed, and every other device was signed out.\n\n" +
            "If this wasn't you, reset your password now from the sign-in page and contact your managing agent.", ct);
    }

    public Task<OtpStep> RequestPasswordResetAsync(string identifier, ClientInfo client, CancellationToken ct = default) =>
        IssueCodeAsync(OtpPurpose.PasswordReset, identifier, client, ct);

    public async Task<SignInStep> ResetPasswordAsync(
        string challengeToken, string code, string newPassword, ClientInfo client, CancellationToken ct = default)
    {
        var challenge = await OpenOtpChallengeAsync(challengeToken, OtpPurpose.PasswordReset, ct);
        var user = challenge.UserId is { } id ? await _repo.GetUserAsync(id, ct) : null;
        if (user is null || !CodeMatches(challenge, code))
        {
            challenge.Attempts++;
            await _repo.SaveChangesAsync(ct);
            throw new AuthFailedException(OtpCodeFailed);
        }

        if (user.Status != UserStatus.Active)
            throw new AuthFailedException(SignInFailed);

        // Checked before the code is used up, so a weak password can simply be retried.
        await ValidateNewPasswordAsync(newPassword, ct);

        challenge.ConsumedAt = Now;
        user.PasswordHash = _passwords.Hash(newPassword);
        user.SecurityStamp = NewStamp(); // every existing session ends
        ResetFailures(user);             // proving the address also lifts a lockout
        foreach (var session in await _repo.ListOpenSessionsAsync(user.Id, ct))
            Revoke(session, "password_reset");
        await _repo.SaveChangesAsync(ct);

        await AuditAsync(user.Id.ToString(), "PasswordReset", user.Id.ToString(), client, null, ct);
        await NotifyAsync(user, "Your EstateHub password was reset",
            "Your password was reset and every device was signed out.\n\n" +
            "If this wasn't you, someone can read your email: secure it, then reset your EstateHub password again.", ct);

        // The code proved the address; staff still need their authenticator (a reset never skips MFA).
        return await AfterFirstFactorAsync(user, "otp", landIn: null, client, ct);
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid userId, ClientInfo client, CancellationToken ct = default)
    {
        var user = await _repo.GetUserAsync(userId, ct) ?? throw new AuthFailedException(SessionEnded);
        if (!user.MfaEnabled)
            throw new ArgumentException("Set up an authenticator app first.");

        var codes = AuthSecrets.NewRecoveryCodes();
        await _repo.DeleteRecoveryCodesAsync(user.Id, ct);
        foreach (var recovery in codes)
            _repo.Add(new RecoveryCode { UserId = user.Id, CodeHash = AuthSecrets.Hash(AuthSecrets.NormaliseRecoveryCode(recovery)) });
        await _repo.SaveChangesAsync(ct);

        await AuditAsync(user.Id.ToString(), "RecoveryCodesRegenerated", user.Id.ToString(), client, null, ct);
        await NotifyAsync(user, "New EstateHub recovery codes",
            "New recovery codes were created for your account; the old ones no longer work.\n\nIf this wasn't you, contact your managing agent now.", ct);
        return codes;
    }

    public async Task ResetMfaAsync(Guid userId, string performedBy, CancellationToken ct = default)
    {
        var user = await _repo.GetUserAsync(userId, ct) ?? throw new KeyNotFoundException("User not found.");

        user.MfaEnabled = false;
        user.TotpSecretEncrypted = null;
        user.TotpLastUsedStep = null;
        user.SecurityStamp = NewStamp(); // every session ends; the next sign-in sets up a new authenticator
        await _repo.DeleteRecoveryCodesAsync(user.Id, ct);
        foreach (var session in await _repo.ListOpenSessionsAsync(user.Id, ct))
            Revoke(session, "mfa_reset");
        await _repo.SaveChangesAsync(ct);

        await AuditAsync(user.Id.ToString(), "MfaReset", performedBy, null, null, ct);
        await _alerts.RaiseAsync("Authenticator reset", $"The authenticator of {ContactPoints.Mask(user.Contact)} was reset by {performedBy}.", ct);
        await NotifyAsync(user, "Your EstateHub authenticator was reset",
            "Your authenticator app was reset by an administrator, and you were signed out everywhere.\n\n" +
            "At your next sign-in you'll set up an authenticator app again. If you didn't ask for this, contact your managing agent now.", ct);
    }

    /// <summary>
    /// A code for <paramref name="purpose"/> to an email or SA mobile (AUTH-04). Unknown or disabled
    /// addresses get the same answer and nothing is sent (AUTH-07); every request counts towards the caps.
    /// </summary>
    private async Task<OtpStep> IssueCodeAsync(string purpose, string identifier, ClientInfo client, CancellationToken ct)
    {
        identifier = (identifier ?? string.Empty).Trim();
        if (identifier.Length == 0)
            throw new ArgumentException("Enter your email address or mobile number.");

        var (channel, address) = ContactPoints.LooksLikeEmail(identifier)
            ? (OtpChannel.Email, ContactPoints.Email(identifier))
            : (OtpChannel.Sms, ContactPoints.SaMobile(identifier));
        await RequireHumanIfSuspiciousAsync(client, ct);

        var user = channel == OtpChannel.Email
            ? await _repo.FindUserByEmailAsync(address, ct)
            : await _repo.FindUserByPhoneAsync(address, ct);
        if (user is { Status: not UserStatus.Active })
            user = null;

        var (token, code, challenge) = await NewOtpChallengeAsync(purpose, channel, address, user?.Id, null, client, ct);
        await _repo.SaveChangesAsync(ct);

        if (user is not null)
        {
            await SendCodeAsync(channel, address, code, ct);
            await AuditAsync(user.Id.ToString(), "CodeSent", user.Id.ToString(), client, $"channel={channel} purpose={purpose}", ct);
        }

        return new OtpStep(token, channel, ContactPoints.Mask(address), Utc(challenge.ExpiresAt));
    }

    public static readonly TimeSpan SignInReportLifetime = TimeSpan.FromDays(7);
    public const string SignInReportInvalid = "This link has expired or was already used. If you still don't recognise a sign-in, sign in and choose \"Sign out everywhere else\" under Account, then change your password.";

    /// <summary>
    /// "New sign-in" email (SECURITY.md layer 3) when this account signs in from a browser it hasn't
    /// used before (its random device id isn't known), with a one-click "This wasn't me" link. Not for
    /// the account's very first device, guard tablets (shared) or development accounts.
    /// </summary>
    private async Task NotifyNewDeviceAsync(User user, AuthSession session, ClientInfo client, CancellationToken ct)
    {
        if (session.GuardDeviceId is not null)
            return;

        var hash = AuthSecrets.Hash(client.DeviceId ?? AuthSecrets.NewToken());
        var known = await _repo.ListKnownDevicesAsync(user.Id, ct);
        var device = DeviceNames.Describe(session.UserAgent);
        var match = known.FirstOrDefault(d => d.DeviceHash == hash);
        if (match is not null)
        {
            match.LastSeenAt = Now;
            await _repo.SaveChangesAsync(ct);
            return;
        }

        _repo.Add(new KnownDevice { UserId = user.Id, DeviceHash = hash, Label = Truncate(device, 100)!, LastSeenAt = Now });
        await _repo.SaveChangesAsync(ct);

        if (known.Count == 0 || user.Email is null || user.Email.EndsWith("@" + DevEmailDomain, StringComparison.Ordinal))
            return;

        var token = AuthSecrets.NewToken();
        _repo.Add(new SignInReport
        {
            UserId = user.Id,
            SessionId = session.Id,
            TokenHash = AuthSecrets.Hash(token),
            Device = Truncate(device, 100)!,
            IpAddress = session.IpAddress,
            ExpiresAt = Now + SignInReportLifetime,
        });
        await _repo.SaveChangesAsync(ct);

        await NotifyAsync(user, "New sign-in to your EstateHub account",
            $"Your account was just used to sign in on {device}" + (session.IpAddress is { } ip ? $" (IP address {ip})" : "") +
            $" at {Sast(session.CreatedAt):HH:mm} on {Sast(session.CreatedAt):d MMMM yyyy}.\n\n" +
            "If this was you, there's nothing to do.\n\n" +
            $"NOT YOU? Open this link to secure your account straight away:\n{_links.SecureAccount(token)}\n\n" +
            "It signs out every device (including the one above), cancels your password so you choose a new one, " +
            "and tells the EstateHub team. The link works once, for 7 days.", ct);
    }

    public async Task<SignInReportInfo> GetSignInReportAsync(string reportToken, CancellationToken ct = default)
    {
        var report = await OpenSignInReportAsync(reportToken, ct);
        var user = await _repo.GetUserAsync(report.UserId, ct) ?? throw new KeyNotFoundException(SignInReportInvalid);
        return new SignInReportInfo(report.Device, report.IpAddress, Utc(report.CreatedAt), user.PasswordHash is not null);
    }

    public async Task SecureAccountAsync(string reportToken, ClientInfo client, CancellationToken ct = default)
    {
        var report = await OpenSignInReportAsync(reportToken, ct);
        var user = await _repo.GetUserAsync(report.UserId, ct) ?? throw new KeyNotFoundException(SignInReportInvalid);

        report.UsedAt = Now;
        user.SecurityStamp = NewStamp();   // every session, everywhere, ends
        user.PasswordHash = null;          // whoever knows it is locked out; "Forgot your password?" sets a new one
        foreach (var session in await _repo.ListOpenSessionsAsync(user.Id, ct))
            Revoke(session, "reported_not_me");
        foreach (var passkey in (await _repo.ListPasskeysAsync(user.Id, ct)).Where(p => p.CreatedAt >= report.CreatedAt))
            _repo.Remove(passkey);         // an intruder with a fresh MFA could have added one
        foreach (var device in await _repo.ListKnownDevicesAsync(user.Id, ct))
            _repo.Remove(device);          // every browser counts as new again, so each next sign-in is emailed
        await _repo.SaveChangesAsync(ct);

        await AuditAsync(user.Id.ToString(), "SignInReported", user.Id.ToString(), client,
            $"device={report.Device} ip={report.IpAddress} session={report.SessionId}", ct);
        await _alerts.RaiseAsync("Sign-in reported as not the user",
            $"{ContactPoints.Mask(user.Contact)} said a sign-in on {report.Device} (IP {report.IpAddress}) wasn't them. " +
            "Their sessions were ended and their password cancelled. Check the audit log for that session.", ct);
        await NotifyAsync(user, "Your EstateHub account is secured",
            "Every device has been signed out and your password has been cancelled.\n\n" +
            "Next: on the sign-in page choose \"Forgot your password?\" and set a new one. If you use an authenticator " +
            "app and think someone else may have it, ask your managing agent to reset it.", ct);
    }

    private async Task<SignInReport> OpenSignInReportAsync(string reportToken, CancellationToken ct)
    {
        var report = string.IsNullOrWhiteSpace(reportToken) ? null : await _repo.FindSignInReportAsync(AuthSecrets.Hash(reportToken.Trim()), ct);
        if (report is null || report.UsedAt is not null || report.ExpiresAt <= Now)
            throw new KeyNotFoundException(SignInReportInvalid);
        return report;
    }

    /// <summary>Security emails go to the account's email (texts cost money and aren't needed for these). A failure never blocks the action.</summary>
    private async Task NotifyAsync(User user, string subject, string text, CancellationToken ct)
    {
        if (user.Email is null)
            return;

        try
        {
            await _email.SendAsync(user.Email, subject, text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Security email to user {UserId} not sent: {Subject}", user.Id, subject);
        }
    }
}
