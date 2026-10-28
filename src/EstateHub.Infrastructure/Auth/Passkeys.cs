using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// WebAuthn through ASP.NET Core Identity's PasskeyHandler (.NET 10): it builds the browser options
/// and verifies signatures, origins, counters and user verification. We keep its state ourselves
/// (PasskeyChallenge) instead of in Identity's cookies, and store passkeys in our own table.
/// </summary>
public sealed class Passkeys : IPasskeys
{
    private readonly IPasskeyHandler<User> _handler;
    private readonly UserManager<User> _users;
    private readonly IHttpContextAccessor _http;

    public Passkeys(IPasskeyHandler<User> handler, UserManager<User> users, IHttpContextAccessor http)
    {
        _handler = handler;
        _users = users;
        _http = http;
    }

    private HttpContext Http => _http.HttpContext ?? throw new InvalidOperationException("Passkeys need an HTTP request.");

    public async Task<(string OptionsJson, string State)> CreationOptionsAsync(User user)
    {
        var entity = new PasskeyUserEntity { Id = user.Id.ToString(), Name = user.Contact, DisplayName = user.DisplayName };
        var options = await _handler.MakeCreationOptionsAsync(entity, Http);
        return (options.CreationOptionsJson, options.AttestationState ?? string.Empty);
    }

    public async Task<string?> RegisterAsync(User user, string credentialJson, string state, string name)
    {
        var result = await _handler.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = Http,
            CredentialJson = credentialJson,
            AttestationState = state,
        });
        if (!result.Succeeded || result.UserEntity.Id != user.Id.ToString())
            return result.Failure?.Message ?? "That passkey couldn't be verified.";

        result.Passkey.Name = name;
        await _users.AddOrUpdatePasskeyAsync(user, result.Passkey);
        return null;
    }

    public async Task<(string OptionsJson, string State)> RequestOptionsAsync()
    {
        // No user: the browser offers every passkey it has for this site (discoverable credentials).
        var options = await _handler.MakeRequestOptionsAsync(null, Http);
        return (options.RequestOptionsJson, options.AssertionState ?? string.Empty);
    }

    public async Task<User?> SignInAsync(string credentialJson, string state)
    {
        var result = await _handler.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = Http,
            CredentialJson = credentialJson,
            AssertionState = state,
        });
        if (!result.Succeeded)
            return null;

        // Stores the new signature counter (clone detection) and when it was last used.
        await _users.AddOrUpdatePasskeyAsync(result.User, result.Passkey);
        return result.User;
    }
}

/// <summary>
/// The thinnest IUserStore Identity's UserManager accepts, over our users table, plus passkeys in
/// user_passkeys. Only what PasskeyHandler uses; everything else about users lives in AuthService.
/// </summary>
public sealed class EstateHubUserStore : IUserStore<User>, IUserPasskeyStore<User>
{
    private readonly EstateHubDbContext _db;
    private readonly TimeProvider _clock;

    public EstateHubUserStore(EstateHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public Task<string> GetUserIdAsync(User user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
    public Task<string?> GetUserNameAsync(User user, CancellationToken ct) => Task.FromResult<string?>(user.Contact);
    public Task SetUserNameAsync(User user, string? userName, CancellationToken ct) => Task.CompletedTask;
    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken ct) => Task.FromResult<string?>(user.Contact.ToUpperInvariant());
    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;

    public async Task<IdentityResult> UpdateAsync(User user, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
        return IdentityResult.Success;
    }

    // Accounts are created and removed by AuthService, never through Identity.
    public Task<IdentityResult> CreateAsync(User user, CancellationToken ct) => throw new NotSupportedException();
    public Task<IdentityResult> DeleteAsync(User user, CancellationToken ct) => throw new NotSupportedException();

    public Task<User?> FindByIdAsync(string userId, CancellationToken ct) =>
        Guid.TryParse(userId, out var id) ? _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) : Task.FromResult<User?>(null);

    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToUpper() == normalizedUserName, ct);

    public async Task AddOrUpdatePasskeyAsync(User user, UserPasskeyInfo passkey, CancellationToken ct)
    {
        var row = await _db.UserPasskeys.FirstOrDefaultAsync(p => p.UserId == user.Id && p.CredentialId == passkey.CredentialId, ct);
        if (row is null)
        {
            row = new UserPasskey { UserId = user.Id, CredentialId = passkey.CredentialId };
            _db.UserPasskeys.Add(row);
        }
        else
        {
            row.LastUsedAt = _clock.GetUtcNow().UtcDateTime;
        }

        row.PublicKey = passkey.PublicKey;
        row.Name = passkey.Name ?? row.Name;
        row.SignCount = passkey.SignCount;
        row.Transports = string.Join(',', passkey.Transports ?? []);
        row.IsUserVerified = passkey.IsUserVerified;
        row.IsBackupEligible = passkey.IsBackupEligible;
        row.IsBackedUp = passkey.IsBackedUp;
        row.AttestationObject = passkey.AttestationObject;
        row.ClientDataJson = passkey.ClientDataJson;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(User user, CancellationToken ct) =>
        (await _db.UserPasskeys.Where(p => p.UserId == user.Id).ToListAsync(ct)).Select(ToInfo).ToList();

    public async Task<User?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken ct)
    {
        var passkey = await _db.UserPasskeys.FirstOrDefaultAsync(p => p.CredentialId == credentialId, ct);
        return passkey is null ? null : await _db.Users.FirstOrDefaultAsync(u => u.Id == passkey.UserId, ct);
    }

    public async Task<UserPasskeyInfo?> FindPasskeyAsync(User user, byte[] credentialId, CancellationToken ct)
    {
        var passkey = await _db.UserPasskeys.FirstOrDefaultAsync(p => p.UserId == user.Id && p.CredentialId == credentialId, ct);
        return passkey is null ? null : ToInfo(passkey);
    }

    public async Task RemovePasskeyAsync(User user, byte[] credentialId, CancellationToken ct)
    {
        var passkey = await _db.UserPasskeys.FirstOrDefaultAsync(p => p.UserId == user.Id && p.CredentialId == credentialId, ct);
        if (passkey is not null)
        {
            _db.UserPasskeys.Remove(passkey);
            await _db.SaveChangesAsync(ct);
        }
    }

    public void Dispose()
    {
    }

    private static UserPasskeyInfo ToInfo(UserPasskey p) =>
        new(p.CredentialId, p.PublicKey, new DateTimeOffset(DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc)), (uint)p.SignCount,
            p.Transports.Split(',', StringSplitOptions.RemoveEmptyEntries), p.IsUserVerified, p.IsBackupEligible, p.IsBackedUp,
            p.AttestationObject ?? [], p.ClientDataJson ?? [])
        {
            Name = p.Name,
        };
}

/// <summary>Bound from "Passkeys": the web app's domain (WebAuthn relying party) and its exact origins.</summary>
public sealed class PasskeySettings
{
    public const string SectionName = "Passkeys";

    /// <summary>e.g. "localhost" in development, "estatehub.anchorveld.co.za" in production.</summary>
    public string ServerDomain { get; set; } = "localhost";

    /// <summary>e.g. "http://localhost:3000"; production: the https web origin only.</summary>
    public string[] Origins { get; set; } = ["http://localhost:3000"];
}
