using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace EstateHub.Application.Services;

// Changing one's own email or mobile (slice 3.4), and scrubbing a former resident's account (POPIA).
public partial class AuthService
{
    public async Task<OtpStep> StartContactChangeAsync(Guid userId, string newContact, bool usable, ClientInfo client, CancellationToken ct = default)
    {
        var user = await _repo.GetUserAsync(userId, ct) ?? throw new AuthFailedException(SignInFailed);
        var isEmail = ContactPoints.LooksLikeEmail(newContact ?? string.Empty);
        var address = isEmail ? ContactPoints.Email(newContact!) : ContactPoints.SaMobile(newContact ?? string.Empty);
        if (address == (isEmail ? user.Email : user.Phone))
            throw new ArgumentException(isEmail ? "That's already your email address." : "That's already your mobile number.");

        // As with every code we send (AUTH-07): an address that belongs to someone else gets a challenge but
        // no message, so the answer never reveals whether it is in use. Confirming such a challenge fails.
        var owner = isEmail ? await _repo.FindUserByEmailAsync(address, ct) : await _repo.FindUserByPhoneAsync(address, ct);
        var deliver = usable && owner is null;

        var channel = isEmail ? OtpChannel.Email : OtpChannel.Sms;
        var (token, code, challenge) = await NewOtpChallengeAsync(OtpPurpose.ContactChange, channel, address, userId, null, client, ct);
        challenge.NewContact = deliver ? address : null;
        await _repo.SaveChangesAsync(ct);
        if (deliver)
            await SendCodeAsync(channel, address, code, ct);

        return new OtpStep(token, channel, ContactPoints.Mask(address), Utc(challenge.ExpiresAt));
    }

    public async Task<ContactChanged> ConfirmContactChangeAsync(Guid userId, string challengeToken, string code, ClientInfo client, CancellationToken ct = default)
    {
        var challenge = await OpenOtpChallengeAsync(challengeToken, OtpPurpose.ContactChange, ct);
        var user = challenge.UserId == userId ? await _repo.GetUserAsync(userId, ct) : null;
        if (user is null || challenge.NewContact is null || !CodeMatches(challenge, code))
        {
            challenge.Attempts++;
            await _repo.SaveChangesAsync(ct);
            throw new AuthFailedException(OtpCodeFailed);
        }

        var isEmail = challenge.Channel == OtpChannel.Email;
        var taken = isEmail ? await _repo.FindUserByEmailAsync(challenge.NewContact, ct) : await _repo.FindUserByPhoneAsync(challenge.NewContact, ct);
        if (taken is not null && taken.Id != user.Id)
            throw new AuthFailedException(OtpCodeFailed);

        var old = isEmail ? user.Email : user.Phone;
        if (isEmail)
            user.Email = challenge.NewContact;
        else
            user.Phone = challenge.NewContact;
        challenge.ConsumedAt = Now;
        await _repo.SaveChangesAsync(ct);
        await AuditAsync(user.Id.ToString(), "ContactChanged", user.Id.ToString(), client, $"channel={challenge.Channel}", ct);

        // The old address hears about it: if this wasn't them, they can act before an intruder settles in.
        if (old is not null)
            await TellOldAddressAsync(user, isEmail, old, challenge.NewContact, ct);
        return new ContactChanged(challenge.Channel, old, challenge.NewContact);
    }

    private async Task TellOldAddressAsync(User user, bool isEmail, string old, string now, CancellationToken ct)
    {
        var what = isEmail ? "email address" : "mobile number";
        var text = $"The {what} on your EstateHub account was changed to {ContactPoints.Mask(now)}. " +
                   "If you didn't do this, contact your managing agent straight away.";
        try
        {
            if (isEmail)
                await _email.SendAsync(old, $"Your EstateHub {what} was changed", text, ct);
            else
                await _sms.SendAsync(old, "EstateHub: " + text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Contact-change notice to user {UserId}'s old address not sent", user.Id);
        }
    }

    public async Task ScrubAccountAsync(Guid userId, string performedBy, CancellationToken ct = default)
    {
        var user = await _repo.GetUserAsync(userId, ct) ?? throw new KeyNotFoundException("Account not found.");
        if (user.IsSuperAdmin)
            throw new ActionNotAllowedException("A platform administrator's account can't be scrubbed this way.");

        user.Email = null;
        user.Phone = null;
        user.DisplayName = "Former resident";
        user.PasswordHash = null;
        user.MfaEnabled = false;
        user.TotpSecretEncrypted = null;
        user.TotpLastUsedStep = null;
        user.Status = UserStatus.Disabled;
        user.SecurityStamp = NewStamp();
        foreach (var session in await _repo.ListOpenSessionsAsync(user.Id, ct))
            Revoke(session, "account_scrubbed");
        foreach (var passkey in await _repo.ListPasskeysAsync(user.Id, ct))
            _repo.Remove(passkey);
        foreach (var device in await _repo.ListKnownDevicesAsync(user.Id, ct))
            _repo.Remove(device);
        await _repo.DeleteRecoveryCodesAsync(user.Id, ct);
        await _repo.SaveChangesAsync(ct);
        await AuditAsync(user.Id.ToString(), "AccountScrubbed", performedBy, null, null, ct);
    }
}
