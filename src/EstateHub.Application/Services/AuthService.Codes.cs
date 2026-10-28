using System.Security.Cryptography;
using EstateHub.Application.Auth;
using EstateHub.Application.DTOs;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

// One-time codes by email/SMS: resident sign-in (AUTH-04) and accepting invitations (AUTH-01).
public partial class AuthService
{
    public static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(10);
    public const int MaxOtpAttempts = 5;

    // AUTH-06: per address and per IP, per hour. Unknown addresses count too, so probing costs the same.
    public const int MaxCodesPerAddressPerHour = 5;
    public const int MaxCodesPerIpPerHour = 20;
    public const int MaxSmsPerIpPerHour = 10;

    public const string OtpCodeFailed = "That code didn't work. Check the latest message we sent and try again.";
    public const string InvitationInvalid = "This invitation link isn't valid any more. Ask whoever invited you for a new one.";
    public const string TooManyCodes = "Too many codes requested. Please wait an hour and try again.";

    public async Task<OtpStep> RequestOtpAsync(string identifier, ClientInfo client, CancellationToken ct = default)
    {
        identifier = (identifier ?? string.Empty).Trim();
        if (identifier.Length == 0)
            throw new ArgumentException("Enter your email address or mobile number.");

        var (channel, address) = ContactPoints.LooksLikeEmail(identifier)
            ? (OtpChannel.Email, ContactPoints.Email(identifier))
            : (OtpChannel.Sms, ContactPoints.SaMobile(identifier));

        var user = channel == OtpChannel.Email
            ? await _repo.FindUserByEmailAsync(address, ct)
            : await _repo.FindUserByPhoneAsync(address, ct);
        if (user is { Status: not UserStatus.Active })
            user = null;

        var (token, code, challenge) = await NewOtpChallengeAsync(OtpPurpose.SignIn, channel, address, user?.Id, null, client, ct);
        await _repo.SaveChangesAsync(ct);

        // AUTH-07: unknown addresses get the same answer, but nothing is sent.
        if (user is not null)
        {
            await SendCodeAsync(channel, address, code, ct);
            await AuditAsync(user.Id.ToString(), "CodeSent", user.Id.ToString(), client, $"channel={channel}", ct);
        }

        return new OtpStep(token, channel, ContactPoints.Mask(address), Utc(challenge.ExpiresAt));
    }

    public async Task<SignInStep> VerifyOtpAsync(string challengeToken, string code, ClientInfo client, CancellationToken ct = default)
    {
        var challenge = await OpenOtpChallengeAsync(challengeToken, OtpPurpose.SignIn, ct);
        var user = challenge.UserId is { } id ? await _repo.GetUserAsync(id, ct) : null;

        if (user is null || !CodeMatches(challenge, code))
        {
            challenge.Attempts++;
            if (user is not null)
                await RegisterFailureAsync(user, "wrong_code", client, ct);
            else
                await _repo.SaveChangesAsync(ct);
            throw new AuthFailedException(OtpCodeFailed);
        }

        if (user.Status != UserStatus.Active || IsLockedOut(user))
            throw new AuthFailedException(SignInFailed);

        challenge.ConsumedAt = Now;
        return await AfterFirstFactorAsync(user, "otp", landIn: null, client, ct);
    }

    public async Task<InvitationInfo> GetInvitationAsync(string invitationToken, CancellationToken ct = default)
    {
        var invitation = await OpenInvitationAsync(invitationToken, ct);
        var estate = await _estates.GetAsync(invitation.EstateId, ct) ?? throw new KeyNotFoundException(InvitationInvalid);
        var invitee = await FindInviteeAsync(invitation, ct);
        var (channel, address) = Destination(invitation);

        return new InvitationInfo(
            estate.Name,
            invitation.Role,
            invitation.Name,
            channel,
            ContactPoints.Mask(address),
            PasswordRequired: Roles.IsStaff(invitation.Role) && invitee?.PasswordHash is null,
            Utc(invitation.ExpiresAt));
    }

    public async Task<OtpStep> StartInvitationAsync(string invitationToken, ClientInfo client, CancellationToken ct = default)
    {
        var invitation = await OpenInvitationAsync(invitationToken, ct);
        var invitee = await FindInviteeAsync(invitation, ct);
        var (channel, address) = Destination(invitation);

        var (token, code, challenge) = await NewOtpChallengeAsync(OtpPurpose.Invitation, channel, address, invitee?.Id, invitation, client, ct);
        await _repo.SaveChangesAsync(ct);
        await SendCodeAsync(channel, address, code, ct);

        return new OtpStep(token, channel, ContactPoints.Mask(address), Utc(challenge.ExpiresAt));
    }

    public async Task<SignInStep> AcceptInvitationAsync(
        string challengeToken, string code, string? newPassword, ClientInfo client, CancellationToken ct = default)
    {
        var challenge = await OpenOtpChallengeAsync(challengeToken, OtpPurpose.Invitation, ct);
        if (!CodeMatches(challenge, code))
        {
            challenge.Attempts++;
            await _repo.SaveChangesAsync(ct);
            throw new AuthFailedException(OtpCodeFailed);
        }

        // The code proved the address; now load the invitation inside its estate (RLS).
        _tenant.Use(challenge.InvitationEstateId!.Value);
        var invitation = await _repo.GetInvitationAsync(challenge.InvitationId!.Value, ct);
        if (invitation is null || !IsOpen(invitation))
            throw new KeyNotFoundException(InvitationInvalid);

        var user = await FindInviteeAsync(invitation, ct);
        if (user is { Status: not UserStatus.Active })
            throw new AuthFailedException(SignInFailed);

        // Staff sign in with a password + MFA from then on, so they choose the password now.
        // Checked before anything is used up, so a weak password can simply be retried.
        var needsPassword = Roles.IsStaff(invitation.Role) && user?.PasswordHash is null;
        if (needsPassword)
            await ValidateNewPasswordAsync(newPassword ?? string.Empty, ct);

        if (user is null)
        {
            // The other contact detail is kept only if nobody else already uses it.
            var (channel, _) = Destination(invitation);
            var phone = channel == OtpChannel.Email && invitation.Phone is { } p && await _repo.FindUserByPhoneAsync(p, ct) is not null
                ? null
                : invitation.Phone;
            user = new User { Email = invitation.Email, Phone = phone, DisplayName = invitation.Name };
            _repo.Add(user);
        }

        if (needsPassword)
            user.PasswordHash = _passwords.Hash(newPassword!);

        challenge.ConsumedAt = Now;
        invitation.AcceptedAt = Now;
        invitation.AcceptedByUserId = user.Id;
        await _repo.SaveChangesAsync(ct);
        await _repo.UpsertMembershipAsync(user.Id, invitation.Role, ct);

        await _audit.CreateAsync(new CreateAuditLogDto(
            "Invitation", invitation.Id.ToString(), "Accepted", user.Id.ToString(), $"role={invitation.Role}", client.IpAddress), ct);

        return await AfterFirstFactorAsync(user, "otp", landIn: invitation.EstateId, client, ct);
    }

    private async Task<(string Token, string Code, OtpChallenge Challenge)> NewOtpChallengeAsync(
        string purpose, string channel, string address, Guid? userId, Invitation? invitation, ClientInfo client, CancellationToken ct)
    {
        var identifierHash = AuthSecrets.Hash(address);
        var since = Now.AddHours(-1);

        if (await _repo.CountOtpChallengesAsync(identifierHash, since, ct) >= MaxCodesPerAddressPerHour)
            throw new RateLimitedException(TooManyCodes);
        if (client.IpAddress is { } ip)
        {
            if (await _repo.CountOtpChallengesFromIpAsync(ip, null, since, ct) >= MaxCodesPerIpPerHour
                || (channel == OtpChannel.Sms && await _repo.CountOtpChallengesFromIpAsync(ip, OtpChannel.Sms, since, ct) >= MaxSmsPerIpPerHour))
                throw new RateLimitedException(TooManyCodes);
        }

        var token = AuthSecrets.NewToken();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var challenge = new OtpChallenge
        {
            Purpose = purpose,
            Channel = channel,
            IdentifierHash = identifierHash,
            UserId = userId,
            InvitationId = invitation?.Id,
            InvitationEstateId = invitation?.EstateId,
            TokenHash = AuthSecrets.Hash(token),
            ExpiresAt = Now + OtpLifetime,
            IpAddress = client.IpAddress,
        };
        challenge.CodeHash = CodeHash(challenge.Id, code);
        _repo.Add(challenge);
        return (token, code, challenge);
    }

    private async Task<OtpChallenge> OpenOtpChallengeAsync(string token, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new AuthFailedException(StepExpired);

        var challenge = await _repo.FindOtpChallengeAsync(AuthSecrets.Hash(token), ct);
        if (challenge is null
            || challenge.Purpose != purpose
            || challenge.ConsumedAt is not null
            || challenge.ExpiresAt <= Now
            || challenge.Attempts >= MaxOtpAttempts)
            throw new AuthFailedException(StepExpired);

        return challenge;
    }

    private static bool CodeMatches(OtpChallenge challenge, string code)
    {
        code = (code ?? string.Empty).Trim().Replace(" ", string.Empty);
        return code.Length == 6
               && CryptographicOperations.FixedTimeEquals(
                   Convert.FromHexString(CodeHash(challenge.Id, code)), Convert.FromHexString(challenge.CodeHash));
    }

    // 6 digits are only a million guesses offline, so the hash is salted with the challenge id and
    // the real protection is online: 5 attempts, 10 minutes, single use.
    private static string CodeHash(Guid challengeId, string code) => AuthSecrets.Hash($"{challengeId:N}:{code}");

    private async Task SendCodeAsync(string channel, string address, string code, CancellationToken ct)
    {
        var text = $"Your EstateHub code is {code}. It expires in {OtpLifetime.TotalMinutes:0} minutes. Never share it with anyone.";
        if (channel == OtpChannel.Email)
            await _email.SendAsync(address, $"{code} is your EstateHub code", text, ct);
        else
            await _sms.SendAsync(address, text, ct);
    }

    /// <summary>The invitation for a link token, inside its estate, if it can still be accepted.</summary>
    private async Task<Invitation> OpenInvitationAsync(string invitationToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(invitationToken))
            throw new KeyNotFoundException(InvitationInvalid);

        var hash = AuthSecrets.Hash(invitationToken.Trim());
        var estateId = await _invitations.EstateForTokenAsync(hash, ct) ?? throw new KeyNotFoundException(InvitationInvalid);
        _tenant.Use(estateId);

        var invitation = await _repo.FindInvitationAsync(hash, ct);
        if (invitation is null || !IsOpen(invitation))
            throw new KeyNotFoundException(InvitationInvalid);
        return invitation;
    }

    private bool IsOpen(Invitation invitation) =>
        invitation.AcceptedAt is null && invitation.RevokedAt is null && invitation.ExpiresAt > Now;

    /// <summary>
    /// The account at the address the code goes to, and only that one: the code proves control of that
    /// address, not of the invitation's other contact detail (which may belong to someone else).
    /// </summary>
    private async Task<User?> FindInviteeAsync(Invitation invitation, CancellationToken ct)
    {
        var (channel, address) = Destination(invitation);
        return channel == OtpChannel.Email ? await _repo.FindUserByEmailAsync(address, ct) : await _repo.FindUserByPhoneAsync(address, ct);
    }

    /// <summary>Email when there is one, otherwise SMS.</summary>
    private static (string Channel, string Address) Destination(Invitation invitation) =>
        invitation.Email is { } email ? (OtpChannel.Email, email) : (OtpChannel.Sms, invitation.Phone!);
}
