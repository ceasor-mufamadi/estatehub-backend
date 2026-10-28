using EstateHub.Api;
using EstateHub.Api.Authorization;
using EstateHub.Api.Security;
using EstateHub.Application.Auth;
using EstateHub.Application.Features;
using EstateHub.Application.Tenancy;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using FluentAssertions;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AppAuthService = EstateHub.Application.Services.AuthService;
using SignedIn = EstateHub.Api.SignedIn;

namespace EstateHub.Tests.Api.Integration;

// PHASE1.md §2a through the real host: AUTH-xx numbers in the test names' comments.
// The factory's clock is shared by this class, so tests only ever move it forward.
public class AuthFlowTests : IClassFixture<EstateHubApiFactory>
{
    private const string Password = "correct horse battery";

    private readonly EstateHubApiFactory _factory;

    public AuthFlowTests(EstateHubApiFactory factory) => _factory = factory;

    private AuthService.AuthServiceClient Auth() => new(_factory.CreateGrpcChannel());

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.co.za";

    // ---------- helpers ----------

    private async Task<Guid> CreateUserAsync(string email, Guid? estateId = null, string? role = null, bool superAdmin = false)
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EstateHubDbContext>();

        var user = new User
        {
            Email = email,
            DisplayName = "Test",
            PasswordHash = services.GetRequiredService<IPasswordHashing>().Hash(Password),
            IsSuperAdmin = superAdmin,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        if (estateId is { } estate)
        {
            services.GetRequiredService<ITenantContext>().Use(estate);
            db.Memberships.Add(new Membership { UserId = user.Id, Role = role! });
            await db.SaveChangesAsync();
        }

        return user.Id;
    }

    private string CodeNow(string secret) =>
        Totp.Code(Base32.Decode(secret), _factory.Clock.GetUtcNow().ToUnixTimeSeconds() / Totp.StepSeconds, 6);

    /// <summary>Codes are single use per 30-second step: move to the next step before asking for another.</summary>
    private string NextCode(string secret)
    {
        _factory.Clock.Now = _factory.Clock.Now.AddSeconds(Totp.StepSeconds);
        return CodeNow(secret);
    }

    private sealed record Enrolled(string Email, string Secret, IReadOnlyList<string> RecoveryCodes, SignedIn SignedIn, string Cookie);

    /// <summary>A super admin who has completed first sign-in (authenticator set up).</summary>
    private async Task<Enrolled> EnrolledSuperAdminAsync()
    {
        var email = NewEmail();
        await CreateUserAsync(email, superAdmin: true);
        var step = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password });
        var setup = await Auth().BeginTotpEnrolmentAsync(new BeginTotpEnrolmentRequest { ChallengeToken = step.Mfa.ChallengeToken });
        var (signedIn, cookie) = await WithCookieAsync(Auth().ConfirmTotpEnrolmentAsync(
            new ConfirmTotpEnrolmentRequest { ChallengeToken = step.Mfa.ChallengeToken, Code = CodeNow(setup.Secret) }));
        return new Enrolled(email, setup.Secret, signedIn.RecoveryCodes, signedIn, cookie!);
    }

    private static async Task<(T Response, string? Cookie)> WithCookieAsync<T>(AsyncUnaryCall<T> call)
    {
        var response = await call.ResponseAsync;
        var setCookie = (await call.ResponseHeadersAsync).GetValue("set-cookie");
        return (response, setCookie?.Split(';')[0].Split('=', 2)[1]);
    }

    private static async Task<string?> SetCookieHeaderAsync<T>(AsyncUnaryCall<T> call)
    {
        await call.ResponseAsync;
        return (await call.ResponseHeadersAsync).GetValue("set-cookie");
    }

    private static Metadata Cookie(string value) => new() { { "cookie", $"{AuthCookieOptions.Name}={value}" } };

    private Task<(SignedIn Response, string? Cookie)> RefreshAsync(string cookie) =>
        WithCookieAsync(Auth().RefreshAsync(new RefreshRequest(), Cookie(cookie)));

    private async Task<(SignedIn Response, string Cookie)> DevSignInAsync(string label, Guid? estateId, string role)
    {
        var (response, cookie) = await WithCookieAsync(Auth().DevSignInAsync(new DevSignInRequest
        {
            UserId = label,
            EstateId = estateId?.ToString() ?? string.Empty,
            Role = role,
            Mfa = true,
        }));
        return (response, cookie!);
    }

    private async Task<StatusCode> MyContextStatusAsync(string accessToken) =>
        await GrpcStatus.Of(() => new FeaturesService.FeaturesServiceClient(_factory.CreateGrpcChannel())
            .GetMyContextAsync(new GetMyContextRequest(), EstateHubApiFactory.Bearer(accessToken)).ResponseAsync);

    // ---------- password + MFA ----------

    [Fact] // AUTH-07
    public async Task Unknown_Email_And_Wrong_Password_Get_The_Same_Answer()
    {
        var email = NewEmail();
        await CreateUserAsync(email);

        var unknown = await GrpcStatus.ErrorOf(() => Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = NewEmail(), Password = Password }).ResponseAsync);
        var wrong = await GrpcStatus.ErrorOf(() => Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = "wrong password!" }).ResponseAsync);

        unknown!.StatusCode.Should().Be(StatusCode.Unauthenticated);
        wrong!.StatusCode.Should().Be(StatusCode.Unauthenticated);
        wrong.Status.Detail.Should().Be(unknown.Status.Detail).And.Be(AppAuthService.SignInFailed);
    }

    [Fact] // AUTH-03, AUTH-10
    public async Task Staff_First_Sign_In_Sets_Up_The_Authenticator_Then_Signs_In()
    {
        var email = NewEmail();
        await CreateUserAsync(email, superAdmin: true);

        var step = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password });
        step.StepCase.Should().Be(PasswordSignInResponse.StepOneofCase.Mfa);
        step.Mfa.EnrolmentRequired.Should().BeTrue();

        (await GrpcStatus.Of(() => Auth().VerifyMfaAsync(new VerifyMfaRequest { ChallengeToken = step.Mfa.ChallengeToken, Code = "123456" }).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated, "an enrolment step can't be used to verify a code");

        var setup = await Auth().BeginTotpEnrolmentAsync(new BeginTotpEnrolmentRequest { ChallengeToken = step.Mfa.ChallengeToken });
        setup.QrCodeDataUrl.Should().StartWith("data:image/png;base64,");
        setup.OtpauthUri.Should().StartWith("otpauth://totp/EstateHub:").And.Contain(setup.Secret);

        var call = Auth().ConfirmTotpEnrolmentAsync(new ConfirmTotpEnrolmentRequest { ChallengeToken = step.Mfa.ChallengeToken, Code = CodeNow(setup.Secret) });
        var setCookie = await SetCookieHeaderAsync(call);
        var signedIn = await call.ResponseAsync;

        signedIn.Role.Should().Be(Roles.SuperAdmin);
        signedIn.EstateId.Should().BeEmpty();
        signedIn.RecoveryCodes.Should().HaveCount(10);
        setCookie.Should().NotBeNull();
        setCookie!.ToLowerInvariant().Should().Contain("httponly").And.Contain("samesite=strict").And.Contain("secure")
            .And.Contain($"path={AuthCookieOptions.Path.ToLowerInvariant()}");

        var estates = await new PlatformService.PlatformServiceClient(_factory.CreateGrpcChannel())
            .ListEstatesAsync(new ListEstatesRequest(), EstateHubApiFactory.Bearer(signedIn.AccessToken));
        estates.Should().NotBeNull();
    }

    [Fact] // AUTH-03: a code works once, then not again
    public async Task Staff_Sign_In_Needs_A_Fresh_Authenticator_Code()
    {
        var admin = await EnrolledSuperAdminAsync();

        var code = NextCode(admin.Secret);
        var first = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = admin.Email, Password = Password });
        first.Mfa.EnrolmentRequired.Should().BeFalse();
        (await Auth().VerifyMfaAsync(new VerifyMfaRequest { ChallengeToken = first.Mfa.ChallengeToken, Code = code })).Role.Should().Be(Roles.SuperAdmin);

        var second = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = admin.Email, Password = Password });
        (await GrpcStatus.Of(() => Auth().VerifyMfaAsync(new VerifyMfaRequest { ChallengeToken = second.Mfa.ChallengeToken, Code = code }).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated, "the same code can't be replayed");
        (await Auth().VerifyMfaAsync(new VerifyMfaRequest { ChallengeToken = second.Mfa.ChallengeToken, Code = NextCode(admin.Secret) }))
            .Role.Should().Be(Roles.SuperAdmin);
    }

    [Fact] // AUTH-03: recovery codes are single use
    public async Task A_Recovery_Code_Works_Once()
    {
        var admin = await EnrolledSuperAdminAsync();
        var recovery = admin.RecoveryCodes[3].ToUpperInvariant();

        var first = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = admin.Email, Password = Password });
        (await Auth().VerifyMfaAsync(new VerifyMfaRequest { ChallengeToken = first.Mfa.ChallengeToken, Code = recovery })).Role.Should().Be(Roles.SuperAdmin);

        var second = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = admin.Email, Password = Password });
        (await GrpcStatus.Of(() => Auth().VerifyMfaAsync(new VerifyMfaRequest { ChallengeToken = second.Mfa.ChallengeToken, Code = recovery }).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated);
    }

    [Fact] // AUTH-05
    public async Task Five_Wrong_Passwords_Pause_Sign_In_For_15_Minutes()
    {
        var email = NewEmail();
        var userId = await CreateUserAsync(email);

        for (var i = 0; i < AppAuthService.MaxFailedAttempts; i++)
            await GrpcStatus.Of(() => Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = $"wrong-{i}-password" }).ResponseAsync);

        (await GrpcStatus.Of(() => Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password }).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated, "even the right password is refused while locked");

        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(16);
        (await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password })).StepCase
            .Should().Be(PasswordSignInResponse.StepOneofCase.SignedIn);

        var audit = new List<string>();
        await _factory.WithDbAsync(async db => audit = await db.AuditLogs.Where(a => a.EntityId == userId.ToString() && a.Action == "SignInFailed" && a.Details == "reason=wrong_password")
            .Select(a => a.Action).ToListAsync());
        audit.Should().HaveCount(AppAuthService.MaxFailedAttempts); // AUTH-16
    }

    [Fact]
    public async Task An_Mfa_Step_Expires_After_Five_Minutes()
    {
        var admin = await EnrolledSuperAdminAsync();
        var step = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = admin.Email, Password = Password });

        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(6);

        var error = await GrpcStatus.ErrorOf(() => Auth().VerifyMfaAsync(new VerifyMfaRequest { ChallengeToken = step.Mfa.ChallengeToken, Code = CodeNow(admin.Secret) }).ResponseAsync);
        error!.Status.Detail.Should().Be(AppAuthService.StepExpired);
    }

    [Fact] // AUTH-03: a staff member can't reach any estate without MFA
    public async Task Estate_Staff_Must_Set_Up_Mfa_Before_Signing_In()
    {
        var estateId = await _factory.CreateEstateAsync($"Staff {Guid.NewGuid():N}");
        var email = NewEmail();
        await CreateUserAsync(email, estateId, Roles.Manager);

        var step = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password });

        step.StepCase.Should().Be(PasswordSignInResponse.StepOneofCase.Mfa);
        step.Mfa.EnrolmentRequired.Should().BeTrue();
    }

    [Fact]
    public async Task A_Resident_With_One_Estate_Signs_Straight_Into_It()
    {
        var estateId = await _factory.CreateEstateAsync($"Resident {Guid.NewGuid():N}");
        var email = NewEmail();
        await CreateUserAsync(email, estateId, Roles.Owner);

        var step = await Auth().PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password });

        step.SignedIn.EstateId.Should().Be(estateId.ToString());
        step.SignedIn.Role.Should().Be(Roles.Owner);
    }

    // ---------- refresh, sign-out, sessions ----------

    [Fact] // AUTH-09
    public async Task Refresh_Rotates_The_Cookie_And_A_Stolen_Old_One_Ends_The_Session()
    {
        var estateId = await _factory.CreateEstateAsync($"Refresh {Guid.NewGuid():N}");
        var (signedIn, cookie1) = await DevSignInAsync("refresher", estateId, Roles.Owner);

        var (refreshed, cookie2) = await RefreshAsync(cookie1);
        cookie2.Should().NotBeNull().And.NotBe(cookie1);
        refreshed.EstateId.Should().Be(estateId.ToString());

        var (_, sameMoment) = await RefreshAsync(cookie1);
        sameMoment.Should().BeNull("a second tab refreshing at the same moment gets an access token, not a new cookie");

        _factory.Clock.Now = _factory.Clock.Now + AppAuthService.RefreshReuseGrace + TimeSpan.FromSeconds(1);
        (await GrpcStatus.Of(() => RefreshAsync(cookie1))).Should().Be(StatusCode.Unauthenticated, "an old token coming back means it was copied");
        (await GrpcStatus.Of(() => RefreshAsync(cookie2!))).Should().Be(StatusCode.Unauthenticated, "the whole session is revoked");
        (await MyContextStatusAsync(refreshed.AccessToken)).Should().Be(StatusCode.Unauthenticated);
        (await MyContextStatusAsync(signedIn.AccessToken)).Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Refresh_Without_A_Cookie_Is_Unauthenticated() =>
        (await GrpcStatus.Of(() => Auth().RefreshAsync(new RefreshRequest()).ResponseAsync)).Should().Be(StatusCode.Unauthenticated);

    [Fact] // AUTH-11
    public async Task Sign_Out_Ends_The_Session_At_Once()
    {
        var estateId = await _factory.CreateEstateAsync($"Logout {Guid.NewGuid():N}");
        var (signedIn, cookie) = await DevSignInAsync("leaver", estateId, Roles.Owner);
        (await MyContextStatusAsync(signedIn.AccessToken)).Should().Be(StatusCode.OK);

        var setCookie = await SetCookieHeaderAsync(Auth().LogoutAsync(new LogoutRequest(), Cookie(cookie)));

        setCookie.Should().Contain("expires=Thu, 01 Jan 1970", "the browser drops the cookie");
        (await MyContextStatusAsync(signedIn.AccessToken)).Should().Be(StatusCode.Unauthenticated);
        (await GrpcStatus.Of(() => RefreshAsync(cookie))).Should().Be(StatusCode.Unauthenticated);
    }

    [Fact] // AUTH-11
    public async Task Sign_Out_Everywhere_Ends_Every_Session()
    {
        var estateId = await _factory.CreateEstateAsync($"Everywhere {Guid.NewGuid():N}");
        var (laptop, _) = await DevSignInAsync("two-devices", estateId, Roles.Owner);
        var (phone, phoneCookie) = await DevSignInAsync("two-devices", estateId, Roles.Owner);

        await Auth().LogoutAllAsync(new LogoutAllRequest(), EstateHubApiFactory.Bearer(laptop.AccessToken));

        (await MyContextStatusAsync(phone.AccessToken)).Should().Be(StatusCode.Unauthenticated);
        (await GrpcStatus.Of(() => RefreshAsync(phoneCookie))).Should().Be(StatusCode.Unauthenticated);
    }

    [Fact] // AUTH-09
    public async Task Staff_Sessions_End_After_12_Hours_Resident_Sessions_Slide_For_30_Days()
    {
        var estateId = await _factory.CreateEstateAsync($"Lifetimes {Guid.NewGuid():N}");
        var (_, staffCookie) = await DevSignInAsync("staff-life", estateId, Roles.Manager);
        var (_, residentCookie) = await DevSignInAsync("resident-life", estateId, Roles.Owner);

        _factory.Clock.Now = _factory.Clock.Now.AddHours(11);
        var (_, renewed) = await RefreshAsync(staffCookie);
        staffCookie = renewed!;
        _factory.Clock.Now = _factory.Clock.Now.AddHours(2);
        (await GrpcStatus.Of(() => RefreshAsync(staffCookie))).Should().Be(StatusCode.Unauthenticated, "12 hours from sign-in, however active");

        for (var i = 0; i < 3; i++)
        {
            _factory.Clock.Now = _factory.Clock.Now.AddDays(25);
            var (_, next) = await RefreshAsync(residentCookie);
            residentCookie = next!;
        }

        _factory.Clock.Now = _factory.Clock.Now.AddDays(31);
        (await GrpcStatus.Of(() => RefreshAsync(residentCookie))).Should().Be(StatusCode.Unauthenticated, "30 days without use");
    }

    // ---------- estates ----------

    [Fact]
    public async Task Switching_Estate_Needs_A_Membership_There()
    {
        var a = await _factory.CreateEstateAsync($"Switch A {Guid.NewGuid():N}");
        var b = await _factory.CreateEstateAsync($"Switch B {Guid.NewGuid():N}");
        var other = await _factory.CreateEstateAsync($"Switch C {Guid.NewGuid():N}");
        await DevSignInAsync("switcher", b, Roles.Owner);
        var (inA, _) = await DevSignInAsync("switcher", a, Roles.Manager);

        var mine = await Auth().ListMyEstatesAsync(new ListMyEstatesRequest(), EstateHubApiFactory.Bearer(inA.AccessToken));
        mine.Items.Select(e => (e.EstateId, e.Role)).Should().BeEquivalentTo(new[] { (a.ToString(), Roles.Manager), (b.ToString(), Roles.Owner) });

        var inB = await Auth().SwitchEstateAsync(new SwitchEstateRequest { EstateId = b.ToString() }, EstateHubApiFactory.Bearer(inA.AccessToken));
        inB.EstateId.Should().Be(b.ToString());
        inB.Role.Should().Be(Roles.Owner);
        (await MyContextStatusAsync(inB.AccessToken)).Should().Be(StatusCode.OK);

        (await GrpcStatus.Of(() => Auth().SwitchEstateAsync(new SwitchEstateRequest { EstateId = other.ToString() }, EstateHubApiFactory.Bearer(inB.AccessToken)).ResponseAsync))
            .Should().Be(StatusCode.NotFound, "never confirm an estate the caller isn't part of");

        var platform = await Auth().SwitchEstateAsync(new SwitchEstateRequest(), EstateHubApiFactory.Bearer(inB.AccessToken));
        platform.EstateId.Should().BeEmpty();
        platform.Role.Should().BeEmpty();
        platform.Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Super_Admin_Who_Opened_An_Estate_Can_Switch_Back_To_Platform_Administration()
    {
        var estateId = await _factory.CreateEstateAsync($"Back {Guid.NewGuid():N}");
        await DevSignInAsync("boss", null, Roles.SuperAdmin);
        var (asManager, _) = await DevSignInAsync("boss", estateId, Roles.Manager); // "Open as manager"

        asManager.Email.Should().Be("boss@dev.estatehub.local");
        asManager.Role.Should().Be(Roles.Manager);
        var mine = await Auth().ListMyEstatesAsync(new ListMyEstatesRequest(), EstateHubApiFactory.Bearer(asManager.AccessToken));
        mine.PlatformAccess.Should().BeTrue();
        mine.Items.Select(e => e.EstateId).Should().Contain(estateId.ToString());

        var platform = await Auth().SwitchEstateAsync(new SwitchEstateRequest(), EstateHubApiFactory.Bearer(asManager.AccessToken));
        platform.Role.Should().Be(Roles.SuperAdmin);
        platform.EstateId.Should().BeEmpty();
    }

    [Fact]
    public async Task Others_Have_No_Platform_Administration_Choice()
    {
        var estateId = await _factory.CreateEstateAsync($"No platform {Guid.NewGuid():N}");
        var (owner, _) = await DevSignInAsync("plain-owner", estateId, Roles.Owner);

        (await Auth().ListMyEstatesAsync(new ListMyEstatesRequest(), EstateHubApiFactory.Bearer(owner.AccessToken))).PlatformAccess.Should().BeFalse();
    }

    [Fact] // SECURITY.md layer 4: revocation within ~30 s
    public async Task A_Removed_Membership_Stops_Access_Within_30_Seconds()
    {
        var estateId = await _factory.CreateEstateAsync($"Removed {Guid.NewGuid():N}");
        var (signedIn, cookie) = await DevSignInAsync("removed", estateId, Roles.Owner);
        (await MyContextStatusAsync(signedIn.AccessToken)).Should().Be(StatusCode.OK);

        await _factory.WithDbAsync(async db =>
        {
            db.Memberships.Remove(await db.Memberships.SingleAsync());
            await db.SaveChangesAsync();
        }, estateId);
        _factory.Clock.Now = _factory.Clock.Now.AddSeconds(31);

        (await MyContextStatusAsync(signedIn.AccessToken)).Should().Be(StatusCode.Unauthenticated);
        var (refreshed, _) = await RefreshAsync(cookie);
        refreshed.EstateId.Should().BeEmpty("the session falls back to platform level instead of keeping the estate");
    }

    // ---------- step-up ----------

    [Fact] // AUTH-12
    public async Task Sensitive_Actions_Ask_For_The_Authenticator_Again_After_15_Minutes()
    {
        var admin = await EnrolledSuperAdminAsync();
        var estateId = await _factory.CreateEstateAsync($"Step up {Guid.NewGuid():N}");
        var platform = new PlatformService.PlatformServiceClient(_factory.CreateGrpcChannel());
        var toggle = new SetEstateFeatureRequest { EstateId = estateId.ToString(), FeatureKey = FeatureCatalog.Parking, Enabled = true };

        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(16);
        var (later, _) = await RefreshAsync(admin.Cookie);

        var error = await GrpcStatus.ErrorOf(() => platform.SetEstateFeatureAsync(toggle, EstateHubApiFactory.Bearer(later.AccessToken)).ResponseAsync);
        error!.Trailers.GetValue(RecentMfaInterceptor.StepUpTrailer).Should().Be("required");

        var confirmed = await Auth().StepUpAsync(new StepUpRequest { Code = NextCode(admin.Secret) }, EstateHubApiFactory.Bearer(later.AccessToken));
        (await platform.SetEstateFeatureAsync(toggle, EstateHubApiFactory.Bearer(confirmed.AccessToken))).EnabledFeatures.Should().Contain(FeatureCatalog.Parking);
    }

    // ---------- first super admin ----------

    [Theory] // AUTH-02
    [InlineData("short", "at least 10")]
    [InlineData("password1234", "data breach")]
    public async Task Weak_Passwords_Are_Refused(string password, string message)
    {
        using var scope = _factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var act = () => auth.CreateSuperAdminAsync(NewEmail(), "Admin", password);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage($"*{message}*");
    }

    [Fact]
    public async Task A_Super_Admin_Email_Can_Only_Be_Used_Once()
    {
        using var scope = _factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var email = NewEmail();
        await auth.CreateSuperAdminAsync(email, "Admin", Password);

        var again = () => auth.CreateSuperAdminAsync(email.ToUpperInvariant(), "Admin", Password);

        await again.Should().ThrowAsync<ArgumentException>().WithMessage("*already exists*");
    }
}
