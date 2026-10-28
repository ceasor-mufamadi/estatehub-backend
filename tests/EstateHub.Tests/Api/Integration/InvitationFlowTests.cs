using EstateHub.Api;
using EstateHub.Application.Auth;
using FluentAssertions;
using Grpc.Core;
using AppAuthService = EstateHub.Application.Services.AuthService;

namespace EstateHub.Tests.Api.Integration;

// Slice 2.2 through the real host: invitations (AUTH-01), codes by email/SMS (AUTH-04, -06, -07),
// members and the role hierarchy (SECURITY.md layer 4). Messages land in the factory's Outbox.
public class InvitationFlowTests : IClassFixture<EstateHubApiFactory>
{
    private const string Password = "correct horse battery";

    private readonly EstateHubApiFactory _factory;

    public InvitationFlowTests(EstateHubApiFactory factory)
    {
        _factory = factory;
        // Every test starts in a fresh hour, so the hourly caps of earlier tests don't carry over.
        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(61);
    }

    private AuthService.AuthServiceClient Auth() => new(_factory.CreateGrpcChannel());
    private MembersService.MembersServiceClient Members() => new(_factory.CreateGrpcChannel());

    private static string NewEmail() => $"person-{Guid.NewGuid():N}@example.co.za";

    private static string NewPhone() => "+2782" + Random.Shared.Next(1_000_000, 9_999_999);

    private async Task<(Guid EstateId, Metadata Manager)> EstateWithManagerAsync()
    {
        var estateId = await _factory.CreateEstateAsync($"Invites {Guid.NewGuid():N}");
        return (estateId, EstateHubApiFactory.Bearer(await _factory.TokenAsync($"mgr-{Guid.NewGuid():N}"[..20], estateId, Roles.Manager)));
    }

    private async Task<SignInStepResponse> AcceptAsync(string address, string? password = null)
    {
        var token = _factory.Outbox.InvitationTokenFor(address);
        var otp = await Auth().StartInvitationAsync(new StartInvitationRequest { InvitationToken = token });
        return await Auth().AcceptInvitationAsync(new AcceptInvitationRequest
        {
            ChallengeToken = otp.ChallengeToken,
            Code = _factory.Outbox.CodeFor(address),
            NewPassword = password ?? string.Empty,
        });
    }

    // ---------- invitations ----------

    [Fact] // AUTH-01
    public async Task A_Manager_Invites_An_Owner_Who_Accepts_With_A_Code_And_Lands_In_The_Estate()
    {
        var (estateId, manager) = await EstateWithManagerAsync();
        var email = NewEmail();

        await Members().InviteAsync(new InviteRequest { Name = "Thandi", Email = email, Role = Roles.Owner }, manager);
        var token = _factory.Outbox.InvitationTokenFor(email);

        var info = await Auth().GetInvitationAsync(new GetInvitationRequest { InvitationToken = token });
        info.Role.Should().Be(Roles.Owner);
        info.Destination.Should().NotBe(email).And.EndWith("@example.co.za", "the address is masked");
        info.PasswordRequired.Should().BeFalse();

        var otp = await Auth().StartInvitationAsync(new StartInvitationRequest { InvitationToken = token });
        (await GrpcStatus.Of(() => Auth().AcceptInvitationAsync(new AcceptInvitationRequest { ChallengeToken = otp.ChallengeToken, Code = "000000" }).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated);

        var accepted = await Auth().AcceptInvitationAsync(new AcceptInvitationRequest { ChallengeToken = otp.ChallengeToken, Code = _factory.Outbox.CodeFor(email) });

        accepted.SignedIn.EstateId.Should().Be(estateId.ToString());
        accepted.SignedIn.Role.Should().Be(Roles.Owner);
        (await GrpcStatus.Of(() => Auth().GetInvitationAsync(new GetInvitationRequest { InvitationToken = token }).ResponseAsync))
            .Should().Be(StatusCode.NotFound, "an invitation works once");
    }

    [Fact] // AUTH-01: bound to the invited address
    public async Task A_Forwarded_Link_Is_Useless_Without_The_Code_Sent_To_The_Invited_Address()
    {
        var (_, manager) = await EstateWithManagerAsync();
        var email = NewEmail();
        await Members().InviteAsync(new InviteRequest { Name = "Sipho", Email = email, Role = Roles.Tenant }, manager);

        var otp = await Auth().StartInvitationAsync(new StartInvitationRequest { InvitationToken = _factory.Outbox.InvitationTokenFor(email) });

        otp.Destination.Should().NotBe(email);
        _factory.Outbox.CodeFor(email).Should().MatchRegex(@"^\d{6}$", "the code goes to the invited address only");
    }

    [Fact]
    public async Task An_Invitation_By_Sms_Creates_A_Phone_Only_Account()
    {
        var (estateId, manager) = await EstateWithManagerAsync();
        var phone = NewPhone();

        await Members().InviteAsync(new InviteRequest { Name = "Lerato", Phone = "0" + phone[3..], Role = Roles.Owner }, manager);
        var accepted = await AcceptAsync(phone);

        accepted.SignedIn.EstateId.Should().Be(estateId.ToString());
        accepted.SignedIn.Email.Should().Be(phone, "with no email, the phone number identifies the account");
    }

    [Fact] // SECURITY.md layer 4 + AUTH-03 + AUTH-02
    public async Task Staff_Invited_From_The_Platform_Choose_A_Password_And_Set_Up_Mfa()
    {
        var estateId = await _factory.CreateEstateAsync($"Platform {Guid.NewGuid():N}");
        var admin = EstateHubApiFactory.Bearer(await _factory.TokenAsync("platform-admin", null, Roles.SuperAdmin));
        var email = NewEmail();

        await new PlatformService.PlatformServiceClient(_factory.CreateGrpcChannel())
            .InviteToEstateAsync(new InviteToEstateRequest { EstateId = estateId.ToString(), Name = "Nomsa", Email = email, Role = Roles.Manager }, admin);

        (await Auth().GetInvitationAsync(new GetInvitationRequest { InvitationToken = _factory.Outbox.InvitationTokenFor(email) })).PasswordRequired.Should().BeTrue();
        var token = _factory.Outbox.InvitationTokenFor(email);
        var otp = await Auth().StartInvitationAsync(new StartInvitationRequest { InvitationToken = token });
        var code = _factory.Outbox.CodeFor(email);

        (await GrpcStatus.Of(() => Auth().AcceptInvitationAsync(new AcceptInvitationRequest { ChallengeToken = otp.ChallengeToken, Code = code }).ResponseAsync))
            .Should().Be(StatusCode.InvalidArgument, "staff must choose a password");
        (await GrpcStatus.Of(() => Auth().AcceptInvitationAsync(new AcceptInvitationRequest { ChallengeToken = otp.ChallengeToken, Code = code, NewPassword = "password1234" }).ResponseAsync))
            .Should().Be(StatusCode.InvalidArgument, "breached passwords are refused");

        var accepted = await Auth().AcceptInvitationAsync(new AcceptInvitationRequest { ChallengeToken = otp.ChallengeToken, Code = code, NewPassword = Password });
        accepted.StepCase.Should().Be(SignInStepResponse.StepOneofCase.Mfa);
        accepted.Mfa.EnrolmentRequired.Should().BeTrue();

        var setup = await Auth().BeginTotpEnrolmentAsync(new BeginTotpEnrolmentRequest { ChallengeToken = accepted.Mfa.ChallengeToken });
        var signedIn = await Auth().ConfirmTotpEnrolmentAsync(new ConfirmTotpEnrolmentRequest
        {
            ChallengeToken = accepted.Mfa.ChallengeToken,
            Code = Totp.Code(Base32.Decode(setup.Secret), _factory.Clock.GetUtcNow().ToUnixTimeSeconds() / Totp.StepSeconds, 6),
        });
        signedIn.EstateId.Should().Be(estateId.ToString(), "they land in the estate they were invited to");
        signedIn.Role.Should().Be(Roles.Manager);
    }

    [Theory] // SECURITY.md layer 4
    [InlineData(Roles.Manager, Roles.Manager, StatusCode.PermissionDenied)]
    [InlineData(Roles.Manager, Roles.Trustee, StatusCode.PermissionDenied)]
    [InlineData(Roles.Trustee, Roles.Owner, StatusCode.PermissionDenied)]
    [InlineData(Roles.Owner, Roles.Tenant, StatusCode.PermissionDenied)]
    [InlineData(Roles.ManagingAgent, Roles.Manager, StatusCode.OK)]
    public async Task Nobody_Invites_Above_Their_Own_Role(string actor, string role, StatusCode expected)
    {
        var estateId = await _factory.CreateEstateAsync($"Grants {Guid.NewGuid():N}");
        var caller = EstateHubApiFactory.Bearer(await _factory.TokenAsync($"g-{Guid.NewGuid():N}"[..20], estateId, actor));

        (await GrpcStatus.Of(() => Members().InviteAsync(new InviteRequest { Name = "X", Email = NewEmail(), Role = role }, caller).ResponseAsync))
            .Should().Be(expected);
    }

    [Fact]
    public async Task Revoked_And_Replaced_Links_Stop_Working()
    {
        var (_, manager) = await EstateWithManagerAsync();
        var revokedEmail = NewEmail();
        var resentEmail = NewEmail();

        var revoked = await Members().InviteAsync(new InviteRequest { Name = "A", Email = revokedEmail, Role = Roles.Owner }, manager);
        await Members().RevokeInvitationAsync(new RevokeInvitationRequest { InvitationId = revoked.Id }, manager);
        (await GrpcStatus.Of(() => Auth().GetInvitationAsync(new GetInvitationRequest { InvitationToken = _factory.Outbox.InvitationTokenFor(revokedEmail) }).ResponseAsync))
            .Should().Be(StatusCode.NotFound);

        var resent = await Members().InviteAsync(new InviteRequest { Name = "B", Email = resentEmail, Role = Roles.Owner }, manager);
        var firstLink = _factory.Outbox.InvitationTokenFor(resentEmail);
        (await GrpcStatus.Of(() => Members().ResendInvitationAsync(new ResendInvitationRequest { InvitationId = resent.Id }, manager).ResponseAsync))
            .Should().Be(StatusCode.ResourceExhausted, "not twice within a minute");

        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(2);
        await Members().ResendInvitationAsync(new ResendInvitationRequest { InvitationId = resent.Id }, manager);

        (await GrpcStatus.Of(() => Auth().GetInvitationAsync(new GetInvitationRequest { InvitationToken = firstLink }).ResponseAsync)).Should().Be(StatusCode.NotFound);
        (await AcceptAsync(resentEmail)).SignedIn.Role.Should().Be(Roles.Owner);
    }

    [Fact]
    public async Task An_Invitation_Expires_After_7_Days()
    {
        var (_, manager) = await EstateWithManagerAsync();
        var email = NewEmail();
        await Members().InviteAsync(new InviteRequest { Name = "Late", Email = email, Role = Roles.Owner }, manager);

        _factory.Clock.Now = _factory.Clock.Now.AddDays(7).AddMinutes(1);

        (await GrpcStatus.Of(() => Auth().GetInvitationAsync(new GetInvitationRequest { InvitationToken = _factory.Outbox.InvitationTokenFor(email) }).ResponseAsync))
            .Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task Members_And_Pending_Invitations_Are_Not_Duplicated()
    {
        var (_, manager) = await EstateWithManagerAsync();
        var email = NewEmail();
        await Members().InviteAsync(new InviteRequest { Name = "Once", Email = email, Role = Roles.Owner }, manager);

        (await GrpcStatus.ErrorOf(() => Members().InviteAsync(new InviteRequest { Name = "Twice", Email = email.ToUpperInvariant(), Role = Roles.Owner }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("pending invitation");

        await AcceptAsync(email);
        (await GrpcStatus.ErrorOf(() => Members().InviteAsync(new InviteRequest { Name = "Again", Email = email, Role = Roles.Tenant }, manager).ResponseAsync))!
            .Status.Detail.Should().Contain("already a member");
    }

    // ---------- members ----------

    [Fact] // SECURITY.md layer 4: role changes and removal bite within ~30 s
    public async Task Role_Changes_And_Removal_Take_Effect_And_Staff_Cannot_Touch_Themselves()
    {
        var (_, manager) = await EstateWithManagerAsync();
        var email = NewEmail();
        await Members().InviteAsync(new InviteRequest { Name = "Changer", Email = email, Role = Roles.Owner }, manager);
        var owner = EstateHubApiFactory.Bearer((await AcceptAsync(email)).SignedIn.AccessToken);

        var list = await Members().ListMembersAsync(new ListMembersRequest(), manager);
        list.GrantableRoles.Should().BeEquivalentTo(Roles.Owner, Roles.Tenant, Roles.Guard, Roles.Contractor);
        var me = list.Members.Single(m => !m.CanManage);
        var them = list.Members.Single(m => m.Email == email);
        them.CanManage.Should().BeTrue();

        (await GrpcStatus.Of(() => Members().ChangeRoleAsync(new ChangeRoleRequest { MembershipId = me.MembershipId, Role = Roles.Owner }, manager).ResponseAsync))
            .Should().Be(StatusCode.PermissionDenied, "nobody changes their own role");

        (await Members().ChangeRoleAsync(new ChangeRoleRequest { MembershipId = them.MembershipId, Role = Roles.Tenant }, manager)).Role.Should().Be(Roles.Tenant);
        _factory.Clock.Now = _factory.Clock.Now.AddSeconds(31);
        (await GrpcStatus.Of(() => new FeaturesService.FeaturesServiceClient(_factory.CreateGrpcChannel())
            .GetMyContextAsync(new GetMyContextRequest(), owner).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated, "their token still says owner; a refresh gives them the new role");

        await Members().RemoveMemberAsync(new RemoveMemberRequest { MembershipId = them.MembershipId }, manager);
        (await Members().ListMembersAsync(new ListMembersRequest(), manager)).Members.Should().NotContain(m => m.Email == email);
    }

    [Fact]
    public async Task Trustees_See_Members_But_Cannot_Change_Them()
    {
        var estateId = await _factory.CreateEstateAsync($"Trustee {Guid.NewGuid():N}");
        var trustee = EstateHubApiFactory.Bearer(await _factory.TokenAsync($"t-{Guid.NewGuid():N}"[..20], estateId, Roles.Trustee));

        var list = await Members().ListMembersAsync(new ListMembersRequest(), trustee);

        list.GrantableRoles.Should().BeEmpty();
        list.Members.Should().OnlyContain(m => !m.CanManage);
    }

    // ---------- sign-in with a code ----------

    [Fact] // AUTH-04
    public async Task A_Resident_Signs_In_With_An_Emailed_Code()
    {
        var (estateId, manager) = await EstateWithManagerAsync();
        var email = NewEmail();
        await Members().InviteAsync(new InviteRequest { Name = "Resident", Email = email, Role = Roles.Owner }, manager);
        await AcceptAsync(email);

        var otp = await Auth().RequestOtpAsync(new RequestOtpRequest { Identifier = email.ToUpperInvariant() });
        var step = await Auth().VerifyOtpAsync(new VerifyOtpRequest { ChallengeToken = otp.ChallengeToken, Code = _factory.Outbox.CodeFor(email) });

        step.SignedIn.EstateId.Should().Be(estateId.ToString());
        (await GrpcStatus.Of(() => Auth().VerifyOtpAsync(new VerifyOtpRequest { ChallengeToken = otp.ChallengeToken, Code = _factory.Outbox.CodeFor(email) }).ResponseAsync))
            .Should().Be(StatusCode.Unauthenticated, "a code works once");
    }

    [Fact] // AUTH-07
    public async Task Unknown_Addresses_Get_The_Same_Answer_And_Nothing_Is_Sent()
    {
        var unknown = NewEmail();

        var otp = await Auth().RequestOtpAsync(new RequestOtpRequest { Identifier = unknown });

        otp.ChallengeToken.Should().NotBeEmpty();
        otp.Channel.Should().Be("email");
        _factory.Outbox.CountTo(unknown).Should().Be(0);
        (await GrpcStatus.ErrorOf(() => Auth().VerifyOtpAsync(new VerifyOtpRequest { ChallengeToken = otp.ChallengeToken, Code = "123456" }).ResponseAsync))!
            .Status.Detail.Should().Be(AppAuthService.OtpCodeFailed);
    }

    [Fact] // AUTH-04: 10 minutes, 5 attempts
    public async Task Codes_Expire_And_Allow_Five_Attempts()
    {
        var (_, manager) = await EstateWithManagerAsync();
        var email = NewEmail();
        await Members().InviteAsync(new InviteRequest { Name = "Tries", Email = email, Role = Roles.Owner }, manager);
        await AcceptAsync(email);

        var otp = await Auth().RequestOtpAsync(new RequestOtpRequest { Identifier = email });
        for (var i = 0; i < AppAuthService.MaxOtpAttempts; i++)
            await GrpcStatus.Of(() => Auth().VerifyOtpAsync(new VerifyOtpRequest { ChallengeToken = otp.ChallengeToken, Code = "000000" }).ResponseAsync);
        (await GrpcStatus.ErrorOf(() => Auth().VerifyOtpAsync(new VerifyOtpRequest { ChallengeToken = otp.ChallengeToken, Code = _factory.Outbox.CodeFor(email) }).ResponseAsync))!
            .Status.Detail.Should().Be(AppAuthService.StepExpired, "after 5 wrong tries even the right code is refused");

        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(16); // also clears the lockout from those 5 failures
        var late = await Auth().RequestOtpAsync(new RequestOtpRequest { Identifier = email });
        _factory.Clock.Now = _factory.Clock.Now.AddMinutes(11);
        (await GrpcStatus.ErrorOf(() => Auth().VerifyOtpAsync(new VerifyOtpRequest { ChallengeToken = late.ChallengeToken, Code = _factory.Outbox.CodeFor(email) }).ResponseAsync))!
            .Status.Detail.Should().Be(AppAuthService.StepExpired);
    }

    [Fact] // AUTH-06
    public async Task Codes_Are_Capped_Per_Address_And_Only_Sa_Numbers_Get_Sms()
    {
        var address = NewEmail();
        for (var i = 0; i < AppAuthService.MaxCodesPerAddressPerHour; i++)
            await Auth().RequestOtpAsync(new RequestOtpRequest { Identifier = address });

        (await GrpcStatus.Of(() => Auth().RequestOtpAsync(new RequestOtpRequest { Identifier = address }).ResponseAsync))
            .Should().Be(StatusCode.ResourceExhausted);
        (await GrpcStatus.Of(() => Auth().RequestOtpAsync(new RequestOtpRequest { Identifier = "+44 7700 900123" }).ResponseAsync))
            .Should().Be(StatusCode.InvalidArgument);
    }
}
