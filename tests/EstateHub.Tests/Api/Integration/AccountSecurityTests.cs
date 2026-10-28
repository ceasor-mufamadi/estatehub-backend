using EstateHub.Api;
using EstateHub.Application.Auth;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Auth;
using EstateHub.Infrastructure.Cron.Jobs;
using EstateHub.Infrastructure.Data;
using FluentAssertions;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AppAuthService = EstateHub.Application.Services.AuthService;

namespace EstateHub.Tests.Api.Integration;

// Slice 2.3 through the real host: sessions, passwords, recovery codes, MFA reset, security emails,
// signing-key rotation and the cleanup job.
public class AccountSecurityTests : IClassFixture<EstateHubApiFactory>
{
    private const string Password = "correct horse battery";
    private const string Chrome = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";
    private const string AndroidChrome = "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Mobile Safari/537.36";

    private readonly EstateHubApiFactory _factory;

    public AccountSecurityTests(EstateHubApiFactory factory)
    {
        _factory = factory;
        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(61); // fresh hourly caps per test
    }

    private AuthService.AuthServiceClient Auth() => new(_factory.CreateGrpcChannel());

    private static string NewEmail() => $"acct-{Guid.NewGuid():N}@example.co.za";

    private async Task<Guid> CreateUserAsync(string email, Guid? estateId = null, string? role = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EstateHubDbContext>();
        var user = new User { Email = email, DisplayName = "Test", PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHashing>().Hash(Password) };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        if (estateId is { } estate)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Use(estate);
            db.Memberships.Add(new Membership { UserId = user.Id, Role = role! });
            await db.SaveChangesAsync();
        }

        return user.Id;
    }

    /// <summary>
    /// A resident with a password signs in from the given browser (no MFA step for residents).
    /// <paramref name="deviceId"/> is the browser's device-id cookie; by default each user agent is its own browser.
    /// </summary>
    private async Task<global::EstateHub.Application.Auth.SignedIn> SignInAsync(string email, string userAgent, string? deviceId = null, string password = Password)
    {
        using var scope = _factory.Services.CreateScope();
        var step = await scope.ServiceProvider.GetRequiredService<IAuthService>()
            .PasswordSignInAsync(email, password, new ClientInfo("10.0.0.1", userAgent, DeviceId: deviceId ?? $"browser:{userAgent}"));
        return step.SignedIn!;
    }

    private static Metadata Bearer(global::EstateHub.Application.Auth.SignedIn s) => EstateHubApiFactory.Bearer(s.Access.AccessToken);

    private async Task<StatusCode> ContextStatusAsync(global::EstateHub.Application.Auth.SignedIn s) =>
        await GrpcStatus.Of(() => new FeaturesService.FeaturesServiceClient(_factory.CreateGrpcChannel())
            .GetMyContextAsync(new GetMyContextRequest(), Bearer(s)).ResponseAsync);

    private async Task<Guid> ResidentAsync(string email)
    {
        var estateId = await _factory.CreateEstateAsync($"Acct {Guid.NewGuid():N}");
        return await CreateUserAsync(email, estateId, Roles.Owner);
    }

    // ---------- sessions ----------

    [Fact] // AUTH-11
    public async Task Users_See_Their_Devices_And_Sign_Them_Out()
    {
        var email = NewEmail();
        await ResidentAsync(email);
        var laptop = await SignInAsync(email, Chrome);
        var phone = await SignInAsync(email, AndroidChrome);

        var sessions = await Auth().ListSessionsAsync(new ListSessionsRequest(), Bearer(laptop));
        sessions.Items.Select(s => (s.Device, s.Current)).Should().BeEquivalentTo(new[] { ("Chrome on macOS", true), ("Chrome on Android", false) });

        await Auth().RevokeSessionAsync(new RevokeSessionRequest { SessionId = phone.Scope.SessionId.ToString() }, Bearer(laptop));

        (await ContextStatusAsync(phone)).Should().Be(StatusCode.Unauthenticated);
        (await ContextStatusAsync(laptop)).Should().Be(StatusCode.OK);
    }

    [Fact]
    public async Task Nobody_Can_Sign_Out_Someone_Elses_Device()
    {
        var mine = NewEmail();
        var theirs = NewEmail();
        await ResidentAsync(mine);
        await ResidentAsync(theirs);
        var me = await SignInAsync(mine, Chrome);
        var them = await SignInAsync(theirs, Chrome);

        (await GrpcStatus.Of(() => Auth().RevokeSessionAsync(new RevokeSessionRequest { SessionId = them.Scope.SessionId.ToString() }, Bearer(me)).ResponseAsync))
            .Should().Be(StatusCode.NotFound);
        (await ContextStatusAsync(them)).Should().Be(StatusCode.OK);
    }

    [Fact]
    public async Task Sign_Out_Everywhere_Else_Keeps_This_Device()
    {
        var email = NewEmail();
        await ResidentAsync(email);
        var here = await SignInAsync(email, Chrome);
        var other = await SignInAsync(email, AndroidChrome);

        (await Auth().SignOutOtherSessionsAsync(new SignOutOtherSessionsRequest(), Bearer(here))).SignedOut.Should().Be(1);

        (await ContextStatusAsync(here)).Should().Be(StatusCode.OK);
        (await ContextStatusAsync(other)).Should().Be(StatusCode.Unauthenticated);
    }

    // ---------- passwords ----------

    [Fact] // AUTH-11: a password change ends every other session
    public async Task Changing_The_Password_Needs_The_Current_One_And_Signs_Out_Other_Devices()
    {
        var email = NewEmail();
        await ResidentAsync(email);
        var here = await SignInAsync(email, Chrome);
        var other = await SignInAsync(email, AndroidChrome);

        (await GrpcStatus.Of(() => Auth().ChangePasswordAsync(new ChangePasswordRequest { CurrentPassword = "not it at all", NewPassword = "a brand new passphrase" }, Bearer(here)).ResponseAsync))
            .Should().Be(StatusCode.InvalidArgument);

        await Auth().ChangePasswordAsync(new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "a brand new passphrase" }, Bearer(here));
        _factory.Clock.Now = _factory.Clock.Now.AddSeconds(31);

        (await ContextStatusAsync(here)).Should().Be(StatusCode.OK, "this device carries on");
        (await ContextStatusAsync(other)).Should().Be(StatusCode.Unauthenticated);
        _factory.Outbox.LastTo(email).Should().Contain("password was changed");
    }

    [Fact] // AUTH-07 + AUTH-03: a reset never skips MFA
    public async Task Forgot_Password_Is_Enumeration_Safe_Ends_Sessions_And_Keeps_Mfa_For_Staff()
    {
        var estateId = await _factory.CreateEstateAsync($"Reset {Guid.NewGuid():N}");
        var email = NewEmail();
        var userId = await CreateUserAsync(email, estateId, Roles.Manager);
        await _factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.MfaEnabled = true; // as if set up earlier
            user.LockoutEnd = _factory.Clock.Now.UtcDateTime.AddMinutes(10);
            await db.SaveChangesAsync();
        });

        var unknown = await Auth().RequestPasswordResetAsync(new RequestPasswordResetRequest { Identifier = NewEmail() });
        var known = await Auth().RequestPasswordResetAsync(new RequestPasswordResetRequest { Identifier = email });
        unknown.Channel.Should().Be(known.Channel);

        (await GrpcStatus.Of(() => Auth().ResetPasswordAsync(new ResetPasswordRequest { ChallengeToken = known.ChallengeToken, Code = _factory.Outbox.CodeFor(email), NewPassword = "password1234" }).ResponseAsync))
            .Should().Be(StatusCode.InvalidArgument, "breached");

        var step = await Auth().ResetPasswordAsync(new ResetPasswordRequest
        {
            ChallengeToken = known.ChallengeToken,
            Code = _factory.Outbox.CodeFor(email),
            NewPassword = "another long passphrase",
        });

        step.StepCase.Should().Be(SignInStepResponse.StepOneofCase.Mfa, "staff continue to their authenticator");
        step.Mfa.EnrolmentRequired.Should().BeFalse();
        (await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = "another long passphrase" })).StepCase
            .Should().Be(PasswordSignInResponse.StepOneofCase.Mfa, "the new password works and the lockout was lifted");
    }

    // ---------- MFA ----------

    [Fact] // AUTH-03
    public async Task Recovery_Codes_Can_Be_Replaced_After_A_Fresh_Mfa()
    {
        var dev = await _factory.TokenAsync("codes-admin", null, Roles.SuperAdmin);
        await _factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == "codes-admin@dev.estatehub.local");
            user.MfaEnabled = true;
            await db.SaveChangesAsync();
        });

        var codes = await Auth().RegenerateRecoveryCodesAsync(new RegenerateRecoveryCodesRequest(), EstateHubApiFactory.Bearer(dev));
        codes.RecoveryCodes.Should().HaveCount(10);

        var stale = await _factory.TokenAsync("codes-admin", null, Roles.SuperAdmin, mfa: false);
        (await GrpcStatus.ErrorOf(() => Auth().RegenerateRecoveryCodesAsync(new RegenerateRecoveryCodesRequest(), EstateHubApiFactory.Bearer(stale)).ResponseAsync))!
            .StatusCode.Should().Be(StatusCode.PermissionDenied, "needs MFA within 15 minutes");
    }

    [Theory] // AUTH-03 + SECURITY.md layer 4
    [InlineData(Roles.ManagingAgent, Roles.Manager, StatusCode.OK)]
    [InlineData(Roles.Manager, Roles.Manager, StatusCode.PermissionDenied)]
    [InlineData(Roles.Manager, Roles.Owner, StatusCode.OK)]
    public async Task A_Lost_Authenticator_Is_Reset_By_Someone_Above_That_Role(string actor, string target, StatusCode expected)
    {
        var estateId = await _factory.CreateEstateAsync($"Mfa reset {Guid.NewGuid():N}");
        var email = NewEmail();
        var userId = await CreateUserAsync(email, estateId, target);
        await _factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == userId)).MfaEnabled = true;
            await db.SaveChangesAsync();
        });
        var caller = EstateHubApiFactory.Bearer(await _factory.TokenAsync($"r-{Guid.NewGuid():N}"[..20], estateId, actor));
        var members = new MembersService.MembersServiceClient(_factory.CreateGrpcChannel());
        var member = (await members.ListMembersAsync(new ListMembersRequest(), caller)).Members.Single(m => m.Email == email);
        member.MfaEnabled.Should().BeTrue();

        var status = await GrpcStatus.Of(() => members.ResetMemberMfaAsync(new ResetMemberMfaRequest { MembershipId = member.MembershipId }, caller).ResponseAsync);

        status.Should().Be(expected);
        if (expected == StatusCode.OK)
        {
            var next = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password });
            if (Roles.IsStaff(target))
                next.Mfa.EnrolmentRequired.Should().BeTrue("staff set up a new authenticator at the next sign-in");
            else
                next.StepCase.Should().Be(PasswordSignInResponse.StepOneofCase.SignedIn, "residents don't need one");
            _factory.Outbox.LastTo(email).Should().Contain("authenticator was reset");
        }
    }

    [Fact]
    public async Task Super_Admins_Reset_Other_Accounts_But_Never_Their_Own()
    {
        var target = NewEmail();
        var userId = await CreateUserAsync(target);
        await _factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == userId)).MfaEnabled = true;
            await db.SaveChangesAsync();
        });
        var admin = EstateHubApiFactory.Bearer(await _factory.TokenAsync("reset-admin", null, Roles.SuperAdmin));
        var platform = new PlatformService.PlatformServiceClient(_factory.CreateGrpcChannel());

        await platform.ResetUserMfaAsync(new ResetUserMfaRequest { Email = target }, admin);
        (await GrpcStatus.Of(() => platform.ResetUserMfaAsync(new ResetUserMfaRequest { Email = "reset-admin@dev.estatehub.local" }, admin).ResponseAsync))
            .Should().Be(StatusCode.PermissionDenied);
    }

    // ---------- security emails ----------

    [Fact] // SECURITY.md layer 3
    public async Task A_New_Browser_Gets_An_Email_Even_With_The_Same_Name_A_Known_One_Does_Not()
    {
        var email = NewEmail();
        await ResidentAsync(email);

        await SignInAsync(email, Chrome, deviceId: "laptop");
        _factory.Outbox.CountTo(email).Should().Be(0, "no email for the very first device");

        await SignInAsync(email, Chrome, deviceId: "laptop");
        _factory.Outbox.CountTo(email).Should().Be(0, "same browser as before");

        // A private window, or a thief on the same kind of computer: same browser name, no device cookie.
        await SignInAsync(email, Chrome, deviceId: "private-window");
        _factory.Outbox.LastTo(email).Should().Contain("Chrome on macOS").And.Contain("NOT YOU?").And.Contain("/secure-account#");
    }

    [Fact] // "This wasn't me" (TESTING.md S2-54 follow-up)
    public async Task This_Wasnt_Me_Signs_Out_Everywhere_Cancels_The_Password_And_Alerts()
    {
        var email = NewEmail();
        await ResidentAsync(email);
        var mine = await SignInAsync(email, Chrome, deviceId: "laptop");
        var intruder = await SignInAsync(email, AndroidChrome, deviceId: "intruder-phone");
        var token = System.Text.RegularExpressions.Regex.Match(_factory.Outbox.LastTo(email), @"/secure-account#([A-Za-z0-9_-]+)").Groups[1].Value;

        var report = await Auth().GetSignInReportAsync(new GetSignInReportRequest { ReportToken = token });
        report.Device.Should().Be("Chrome on Android");
        report.HadPassword.Should().BeTrue();

        await Auth().SecureAccountAsync(new SecureAccountRequest { ReportToken = token });

        (await ContextStatusAsync(intruder)).Should().Be(StatusCode.Unauthenticated);
        (await ContextStatusAsync(mine)).Should().Be(StatusCode.Unauthenticated, "every device, including the user's own");
        (await GrpcStatus.Of(() => Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password }).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated, "the old password no longer works");
        (await GrpcStatus.Of(() => Auth().SecureAccountAsync(new SecureAccountRequest { ReportToken = token }).ResponseAsync))
            .Should().Be(StatusCode.NotFound, "the link works once");
        _factory.Outbox.LastTo(EstateHubApiFactory.AlertsEmail).Should().Contain("wasn't them");
        _factory.Outbox.LastTo(email).Should().Contain("Forgot your password?");

        var reset = await Auth().RequestPasswordResetAsync(new RequestPasswordResetRequest { Identifier = email });
        await Auth().ResetPasswordAsync(new ResetPasswordRequest
        {
            ChallengeToken = reset.ChallengeToken,
            Code = _factory.Outbox.CodeFor(email),
            NewPassword = "a fresh long passphrase",
        });
        (await SignInAsync(email, Chrome, deviceId: "laptop", password: "a fresh long passphrase")).Should().NotBeNull("a new password brings the account back");
    }

    // ---------- signing keys (AUTH-08) ----------

    [Fact]
    public async Task Routine_Rotation_Publishes_Both_Keys_And_Old_Tokens_Keep_Working()
    {
        using var factory = new EstateHubApiFactory();
        var before = await factory.TokenAsync("rotate-1", null, Roles.SuperAdmin);
        var auth = new AuthService.AuthServiceClient(factory.CreateGrpcChannel());
        (await auth.GetJwksAsync(new GetJwksRequest())).JwksJson.Should().Contain("\"kid\"");

        factory.Clock.Now = factory.Clock.Now.Add(SigningKeyRotation.RotateEvery);
        await RotateAsync(factory, immediately: false);

        var jwks = (await auth.GetJwksAsync(new GetJwksRequest())).JwksJson;
        System.Text.RegularExpressions.Regex.Matches(jwks, "\"kid\"").Count.Should().Be(2, "old and new during the overlap");
        jwks.Should().NotContain("\"d\"");
        var after = await factory.TokenAsync("rotate-1", null, Roles.SuperAdmin);
        after.Should().NotBe(before);
        (await GrpcStatus.Of(() => new FeaturesService.FeaturesServiceClient(factory.CreateGrpcChannel())
            .GetMyContextAsync(new GetMyContextRequest(), EstateHubApiFactory.Bearer(after)).ResponseAsync)).Should().Be(StatusCode.OK);
    }

    [Fact] // SECURITY.md §7
    public async Task Incident_Rotation_Kills_Every_Existing_Access_Token()
    {
        using var factory = new EstateHubApiFactory();
        var before = await factory.TokenAsync("rotate-2", null, Roles.SuperAdmin);

        await RotateAsync(factory, immediately: true);

        (await GrpcStatus.Of(() => new FeaturesService.FeaturesServiceClient(factory.CreateGrpcChannel())
            .GetMyContextAsync(new GetMyContextRequest(), EstateHubApiFactory.Bearer(before)).ResponseAsync)).Should().Be(StatusCode.Unauthenticated);
    }

    private static async Task RotateAsync(EstateHubApiFactory factory, bool immediately)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SigningKeyRotation>().RotateAsync(immediately);
        await factory.Services.GetRequiredService<SigningKeyRing>().ReloadAsync();
    }

    // ---------- ⏰ cleanup ----------

    [Fact]
    public async Task The_Cleanup_Job_Removes_Old_Codes_Tokens_And_Sessions_Only()
    {
        using var factory = new EstateHubApiFactory();
        var email = NewEmail();
        await factory.WithDbAsync(async db =>
        {
            db.Users.Add(new User { Email = email, DisplayName = "Old" });
            await db.SaveChangesAsync();
        });
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAuthService>().RequestOtpAsync(email, new ClientInfo("10.0.0.9", "tests"));
        await factory.TokenAsync("cleanup-old", null, Roles.SuperAdmin);

        factory.Clock.Now = factory.Clock.Now.Add(AuthCleanupJob.KeepEndedSessions).AddDays(2);
        var fresh = await factory.TokenAsync("cleanup-new", null, Roles.SuperAdmin);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EstateHubDbContext>();
            await new AuthCleanupJob(db, factory.Clock, NullLogger<AuthCleanupJob>.Instance).ExecuteAsync(CancellationToken.None);

            (await db.OtpChallenges.CountAsync()).Should().Be(0);
            (await db.AuthSessions.CountAsync()).Should().Be(1, "only the recent session is left");
        }

        (await GrpcStatus.Of(() => new FeaturesService.FeaturesServiceClient(factory.CreateGrpcChannel())
            .GetMyContextAsync(new GetMyContextRequest(), EstateHubApiFactory.Bearer(fresh)).ResponseAsync)).Should().Be(StatusCode.OK);
    }
}
