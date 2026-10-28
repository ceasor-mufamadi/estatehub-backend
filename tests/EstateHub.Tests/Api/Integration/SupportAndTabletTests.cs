using EstateHub.Api;
using EstateHub.Api.Authorization;
using EstateHub.Api.Security;
using EstateHub.Application.Auth;
using FluentAssertions;
using Grpc.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using AppAuthService = EstateHub.Application.Services.AuthService;
using SignedIn = EstateHub.Api.SignedIn;

namespace EstateHub.Tests.Api.Integration;

// Slice 2.4 through the real host: support access (AUTH-13), guard tablets (AUTH-14), alerts (layer 12).
public class SupportAndTabletTests : IClassFixture<EstateHubApiFactory>
{
    private readonly EstateHubApiFactory _factory;

    public SupportAndTabletTests(EstateHubApiFactory factory) => _factory = factory;

    private AuthService.AuthServiceClient Auth() => new(_factory.CreateGrpcChannel());
    private GuardDeviceService.GuardDeviceServiceClient Devices() => new(_factory.CreateGrpcChannel());
    private EstateService.EstateServiceClient Estate() => new(_factory.CreateGrpcChannel());

    private static string Label() => $"u-{Guid.NewGuid():N}"[..20];

    private async Task<StatusCode> StatusOf<T>(Func<AsyncUnaryCall<T>> call) => await GrpcStatus.Of(() => call().ResponseAsync);

    // ---------- support access (AUTH-13) ----------

    [Fact]
    public async Task Support_Access_Is_Read_Only_Labelled_With_The_Actor_And_Shown_To_The_Estate()
    {
        var estateId = await _factory.CreateEstateAsync($"Support {Guid.NewGuid():N}");
        var admin = EstateHubApiFactory.Bearer(await _factory.TokenAsync("support-admin", null, Roles.SuperAdmin));

        (await StatusOf(() => Auth().StartSupportAccessAsync(new StartSupportAccessRequest { EstateId = estateId.ToString(), Reason = "help" }, admin)))
            .Should().Be(StatusCode.InvalidArgument, "a reason is required");

        var support = await Auth().StartSupportAccessAsync(
            new StartSupportAccessRequest { EstateId = estateId.ToString(), Reason = "Owner reports a missing block" }, admin);

        support.Role.Should().Be(Roles.Support);
        support.EstateId.Should().Be(estateId.ToString());
        support.SupportEndsAt.ToDateTimeOffset().Should().BeCloseTo(_factory.Clock.GetUtcNow() + AppAuthService.SupportAccessLifetime, TimeSpan.FromSeconds(5));
        new JsonWebToken(support.AccessToken).GetPayloadValue<System.Text.Json.JsonElement>(TokenClaims.Actor)
            .GetProperty(TokenClaims.Subject).GetString().Should().NotBeNullOrEmpty("the token says a platform operator is acting");

        var asSupport = EstateHubApiFactory.Bearer(support.AccessToken);
        (await StatusOf(() => Estate().ListBlocksAsync(new ListBlocksRequest(), asSupport))).Should().Be(StatusCode.OK);
        (await StatusOf(() => Estate().CreateBlockAsync(new CreateBlockRequest { Name = "Nope" }, asSupport))).Should().Be(StatusCode.PermissionDenied, "read-only");

        var manager = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager));
        var trail = await new AuditLogService.AuditLogServiceClient(_factory.CreateGrpcChannel())
            .ListAuditLogsAsync(new ListAuditLogsRequest { EntityType = "User" }, manager);
        trail.Items.Should().Contain(e => e.Action == "SupportAccessStarted" && e.Details.Contains("Owner reports a missing block"));
        _factory.Outbox.LastTo(EstateHubApiFactory.AlertsEmail).Should().Contain("Support access started");

        var back = await Auth().EndSupportAccessAsync(new EndSupportAccessRequest(), asSupport);
        back.Role.Should().Be(Roles.SuperAdmin);
        back.EstateId.Should().BeEmpty();
    }

    [Fact]
    public async Task Support_Access_Ends_By_Itself_After_30_Minutes()
    {
        using var factory = new EstateHubApiFactory();
        var estateId = await factory.CreateEstateAsync("Timed support");
        var auth = new AuthService.AuthServiceClient(factory.CreateGrpcChannel());
        var (_, cookie) = await WithCookieAsync(auth.DevSignInAsync(new DevSignInRequest { UserId = "timed-admin", Role = Roles.SuperAdmin, Mfa = true }));
        var (signedIn, current) = await WithCookieAsync(auth.RefreshAsync(new RefreshRequest(), Cookie(cookie!)));
        await auth.StartSupportAccessAsync(new StartSupportAccessRequest { EstateId = estateId.ToString(), Reason = "Checking the levy setup" },
            EstateHubApiFactory.Bearer(signedIn.AccessToken));

        factory.Clock.Now = factory.Clock.Now.AddMinutes(31);
        var after = await auth.RefreshAsync(new RefreshRequest(), Cookie(current!)); // the rotated cookie, as a browser would send

        after.Role.Should().Be(Roles.SuperAdmin);
        after.EstateId.Should().BeEmpty("the session falls back to platform level");
    }

    [Theory]
    [InlineData(Roles.Manager, true, StatusCode.PermissionDenied)]
    [InlineData(Roles.SuperAdmin, false, StatusCode.PermissionDenied)] // MFA over 15 minutes ago: step-up
    public async Task Only_Super_Admins_With_A_Fresh_Mfa_Open_Support_Access(string role, bool recentMfa, StatusCode expected)
    {
        var estateId = await _factory.CreateEstateAsync($"No support {Guid.NewGuid():N}");
        var caller = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), role == Roles.SuperAdmin ? null : estateId, role, recentMfa));

        (await StatusOf(() => Auth().StartSupportAccessAsync(new StartSupportAccessRequest { EstateId = estateId.ToString(), Reason = "Looking at something" }, caller)))
            .Should().Be(expected);
    }

    // ---------- guard tablets (AUTH-14) ----------

    [Fact]
    public async Task A_Tablet_Is_Paired_Once_And_Guards_Sign_In_On_It_With_Their_Pin()
    {
        var estateId = await _factory.CreateEstateAsync($"Gate {Guid.NewGuid():N}");
        var manager = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Manager));
        var guardLabel = Label();
        var guard = EstateHubApiFactory.Bearer(await _factory.TokenAsync(guardLabel, estateId, Roles.Guard));

        var code = await Devices().CreatePairingCodeAsync(new CreatePairingCodeRequest { Label = "Main gate" }, manager);
        code.Code.Should().MatchRegex("^[A-Z2-7]{4}-[A-Z2-7]{4}$");

        var (paired, device) = await WithCookieAsync(Devices().PairDeviceAsync(new PairDeviceRequest { Code = code.Code.ToLowerInvariant() }), SignInResponses.DeviceCookieName);
        paired.Label.Should().Be("Main gate");
        device.Should().NotBeNullOrEmpty();
        (await StatusOf(() => Devices().PairDeviceAsync(new PairDeviceRequest { Code = code.Code }))).Should().Be(StatusCode.Unauthenticated, "a code works once");

        (await StatusOf(() => Auth().SetGuardPinAsync(new SetGuardPinRequest { Pin = "123456" }, guard))).Should().Be(StatusCode.InvalidArgument, "too easy");
        await Auth().SetGuardPinAsync(new SetGuardPinRequest { Pin = "482915" }, guard);

        var tablet = await Devices().GetTabletAsync(new GetTabletRequest(), DeviceCookie(device!));
        var me = tablet.Guards.Single(g => g.Name == guardLabel);
        me.HasPin.Should().BeTrue();

        var shift = await Devices().GuardSignInAsync(new GuardSignInRequest { UserId = me.UserId, Pin = "482915" }, DeviceCookie(device!));
        shift.Role.Should().Be(Roles.Guard);
        shift.EstateId.Should().Be(estateId.ToString());

        (await Devices().ListDevicesAsync(new ListDevicesRequest(), manager)).Items.Should().ContainSingle(d => d.Label == "Main gate");
        var other = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), await _factory.CreateEstateAsync($"Other {Guid.NewGuid():N}"), Roles.Manager));
        (await Devices().ListDevicesAsync(new ListDevicesRequest(), other)).Items.Should().BeEmpty("tablets belong to one estate");

        // Revoking the tablet signs its guards out at once and unpairs it.
        await Devices().RevokeDeviceAsync(new RevokeDeviceRequest { DeviceId = (await Devices().ListDevicesAsync(new ListDevicesRequest(), manager)).Items.Single().Id }, manager);
        (await StatusOf(() => new FeaturesService.FeaturesServiceClient(_factory.CreateGrpcChannel())
            .GetMyContextAsync(new GetMyContextRequest(), EstateHubApiFactory.Bearer(shift.AccessToken)))).Should().Be(StatusCode.Unauthenticated);
        (await StatusOf(() => Devices().GetTabletAsync(new GetTabletRequest(), DeviceCookie(device!)))).Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Five_Wrong_Pins_Pause_The_Pin_For_15_Minutes()
    {
        using var factory = new EstateHubApiFactory();
        var devices = new GuardDeviceService.GuardDeviceServiceClient(factory.CreateGrpcChannel());
        var estateId = await factory.CreateEstateAsync("Pin lockout");
        var manager = EstateHubApiFactory.Bearer(await factory.TokenAsync("lock-mgr", estateId, Roles.Manager));
        await new AuthService.AuthServiceClient(factory.CreateGrpcChannel())
            .SetGuardPinAsync(new SetGuardPinRequest { Pin = "730418" }, EstateHubApiFactory.Bearer(await factory.TokenAsync("lock-guard", estateId, Roles.Guard)));
        var code = await devices.CreatePairingCodeAsync(new CreatePairingCodeRequest { Label = "Back gate" }, manager);
        var (_, device) = await WithCookieAsync(devices.PairDeviceAsync(new PairDeviceRequest { Code = code.Code }), SignInResponses.DeviceCookieName);
        var guardId = (await devices.GetTabletAsync(new GetTabletRequest(), DeviceCookie(device!))).Guards.Single().UserId;

        for (var i = 0; i < AppAuthService.MaxPinAttempts; i++)
            await GrpcStatus.Of(() => devices.GuardSignInAsync(new GuardSignInRequest { UserId = guardId, Pin = "000001" }, DeviceCookie(device!)).ResponseAsync);

        (await GrpcStatus.Of(() => devices.GuardSignInAsync(new GuardSignInRequest { UserId = guardId, Pin = "730418" }, DeviceCookie(device!)).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated, "locked, even with the right PIN");
        factory.Outbox.LastTo(EstateHubApiFactory.AlertsEmail).Should().Contain("Guard PIN locked");

        factory.Clock.Now = factory.Clock.Now.AddMinutes(16);
        (await devices.GuardSignInAsync(new GuardSignInRequest { UserId = guardId, Pin = "730418" }, DeviceCookie(device!))).Role.Should().Be(Roles.Guard);
    }

    [Fact]
    public async Task A_Pairing_Code_Expires_After_15_Minutes()
    {
        using var factory = new EstateHubApiFactory();
        var devices = new GuardDeviceService.GuardDeviceServiceClient(factory.CreateGrpcChannel());
        var estateId = await factory.CreateEstateAsync("Slow pairing");
        var code = await devices.CreatePairingCodeAsync(new CreatePairingCodeRequest { Label = "Late" },
            EstateHubApiFactory.Bearer(await factory.TokenAsync("slow-mgr", estateId, Roles.Manager)));

        factory.Clock.Now = factory.Clock.Now.AddMinutes(16);

        (await GrpcStatus.Of(() => devices.PairDeviceAsync(new PairDeviceRequest { Code = code.Code }).ResponseAsync)).Should().Be(StatusCode.Unauthenticated);
    }

    [Theory]
    [InlineData("000000", true)]
    [InlineData("123456", true)]
    [InlineData("987654", true)]
    [InlineData("482915", false)]
    [InlineData("112233", false)]
    public void Guessable_Pins_Are_Refused(string pin, bool guessable) =>
        AppAuthService.IsGuessablePin(pin).Should().Be(guessable);

    // ---------- alerts (SECURITY.md layer 12) ----------

    [Fact]
    public async Task Probing_For_Permissions_Raises_An_Alert()
    {
        var estateId = await _factory.CreateEstateAsync($"Probe {Guid.NewGuid():N}");
        var owner = EstateHubApiFactory.Bearer(await _factory.TokenAsync(Label(), estateId, Roles.Owner));

        for (var i = 0; i < PermissionHandler.BurstThreshold; i++)
            await GrpcStatus.Of(() => Estate().CreateBlockAsync(new CreateBlockRequest { Name = $"x{i}" }, owner).ResponseAsync);

        _factory.Outbox.LastTo(EstateHubApiFactory.AlertsEmail).Should().Contain("Permission-denied burst");
    }

    // ---------- helpers ----------

    private static async Task<(T Response, string? Cookie)> WithCookieAsync<T>(AsyncUnaryCall<T> call, string name = AuthCookieOptions.Name)
    {
        var response = await call.ResponseAsync;
        var setCookie = (await call.ResponseHeadersAsync).Where(h => h.Key == "set-cookie").Select(h => h.Value)
            .FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal));
        return (response, setCookie?.Split(';')[0].Split('=', 2)[1]);
    }

    private static Metadata Cookie(string value) => new() { { "cookie", $"{AuthCookieOptions.Name}={value}" } };

    private static Metadata DeviceCookie(string value) => new() { { "cookie", $"{SignInResponses.DeviceCookieName}={value}" } };
}
