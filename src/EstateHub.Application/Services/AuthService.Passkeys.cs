using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;

namespace EstateHub.Application.Services;

// Slice 2.5: passkeys (AUTH-03) and the human check after repeated failures (AUTH-05).
public partial class AuthService
{
    public static readonly TimeSpan PasskeyCeremonyLifetime = TimeSpan.FromMinutes(5);
    public const int MaxPasskeysPerUser = 10;

    public const string PasskeyFailed = "That passkey didn't work. Try again, or sign in with your password and authenticator app.";

    public async Task<(string ChallengeToken, string OptionsJson)> BeginPasskeyRegistrationAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _repo.GetUserAsync(userId, ct) ?? throw new AuthFailedException(SessionEnded);
        if (!user.MfaEnabled)
            throw new ArgumentException("Set up an authenticator app first; a passkey is added on top of it.");
        if ((await _repo.ListPasskeysAsync(userId, ct)).Count >= MaxPasskeysPerUser)
            throw new ArgumentException($"You already have {MaxPasskeysPerUser} passkeys. Remove one first.");

        var (options, state) = await _passkeys.CreationOptionsAsync(user);
        return (await NewPasskeyChallengeAsync(PasskeyPurpose.Register, userId, state, ct), options);
    }

    public async Task FinishPasskeyRegistrationAsync(
        Guid userId, string challengeToken, string credentialJson, string name, ClientInfo client, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 100)
            throw new ArgumentException("Give the passkey a name you'll recognise (up to 100 characters), e.g. \"MacBook Touch ID\".");

        var challenge = await OpenPasskeyChallengeAsync(challengeToken, PasskeyPurpose.Register, ct);
        if (challenge.UserId != userId)
            throw new AuthFailedException(StepExpired);
        var user = await _repo.GetUserAsync(userId, ct) ?? throw new AuthFailedException(SessionEnded);

        challenge.ConsumedAt = Now;
        await _repo.SaveChangesAsync(ct);

        var failure = await _passkeys.RegisterAsync(user, credentialJson ?? string.Empty, challenge.State, name);
        if (failure is not null)
            throw new ArgumentException($"That passkey couldn't be added: {failure}");

        await AuditAsync(user.Id.ToString(), "PasskeyAdded", user.Id.ToString(), client, $"name={name}", ct);
        await NotifyAsync(user, "A passkey was added to your EstateHub account",
            $"A passkey called \"{name}\" can now sign in to your account.\n\nIf this wasn't you, remove it under Account and change your password.", ct);
    }

    public async Task<(string ChallengeToken, string OptionsJson)> BeginPasskeySignInAsync(ClientInfo client, CancellationToken ct = default)
    {
        var (options, state) = await _passkeys.RequestOptionsAsync();
        return (await NewPasskeyChallengeAsync(PasskeyPurpose.SignIn, null, state, ct), options);
    }

    public async Task<SignedIn> FinishPasskeySignInAsync(string challengeToken, string credentialJson, ClientInfo client, CancellationToken ct = default)
    {
        var challenge = await OpenPasskeyChallengeAsync(challengeToken, PasskeyPurpose.SignIn, ct);
        challenge.ConsumedAt = Now;
        await _repo.SaveChangesAsync(ct);

        var user = await _passkeys.SignInAsync(credentialJson ?? string.Empty, challenge.State);
        if (user is null)
        {
            _failures.Record(client.IpAddress);
            await AuditAsync("unknown", "SignInFailed", "anonymous", client, "reason=passkey", ct);
            throw new AuthFailedException(PasskeyFailed);
        }

        if (user.Status != UserStatus.Active || IsLockedOut(user))
            throw new AuthFailedException(SignInFailed);

        // A passkey is possession (the device) plus user verification (fingerprint, face, device PIN): MFA on its own.
        ResetFailures(user);
        var signedIn = await StartSessionAsync(user, ["passkey", TokenClaims.MfaMethod], estateId: null, client, ct);
        await AuditAsync(user.Id.ToString(), "SignedIn", user.Id.ToString(), client, "method=passkey", ct);
        await AlertIfSuperAdminAsync(user, client, ct);
        return signedIn;
    }

    public async Task<IReadOnlyList<PasskeyInfo>> ListPasskeysAsync(Guid userId, CancellationToken ct = default) =>
        (await _repo.ListPasskeysAsync(userId, ct))
        .Select(p => new PasskeyInfo(p.Id, p.Name, Utc(p.CreatedAt), p.LastUsedAt is { } used ? Utc(used) : null))
        .ToList();

    public async Task RemovePasskeyAsync(Guid userId, Guid passkeyId, ClientInfo client, CancellationToken ct = default)
    {
        var passkey = (await _repo.ListPasskeysAsync(userId, ct)).FirstOrDefault(p => p.Id == passkeyId)
            ?? throw new KeyNotFoundException("Passkey not found.");
        _repo.Remove(passkey);
        await _repo.SaveChangesAsync(ct);
        await AuditAsync(userId.ToString(), "PasskeyRemoved", userId.ToString(), client, $"name={passkey.Name}", ct);
    }

    /// <summary>
    /// AUTH-05: after repeated failures from an IP address, the anonymous sign-in steps want a passed
    /// Turnstile check from that address. Off when Turnstile isn't configured.
    /// </summary>
    private async Task RequireHumanIfSuspiciousAsync(ClientInfo client, CancellationToken ct)
    {
        if (!_humans.Enabled || !_failures.IsSuspicious(client.IpAddress))
            return;
        if (!await _humans.VerifyAsync(client.HumanCheckToken, client.IpAddress, ct))
            throw new HumanCheckRequiredException();
    }

    private async Task<string> NewPasskeyChallengeAsync(string purpose, Guid? userId, string state, CancellationToken ct)
    {
        var token = AuthSecrets.NewToken();
        _repo.Add(new PasskeyChallenge
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = AuthSecrets.Hash(token),
            State = state,
            ExpiresAt = Now + PasskeyCeremonyLifetime,
        });
        await _repo.SaveChangesAsync(ct);
        return token;
    }

    private async Task<PasskeyChallenge> OpenPasskeyChallengeAsync(string token, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new AuthFailedException(StepExpired);

        var challenge = await _repo.FindPasskeyChallengeAsync(AuthSecrets.Hash(token), ct);
        if (challenge is null || challenge.Purpose != purpose || challenge.ConsumedAt is not null || challenge.ExpiresAt <= Now)
            throw new AuthFailedException(StepExpired);
        return challenge;
    }
}
