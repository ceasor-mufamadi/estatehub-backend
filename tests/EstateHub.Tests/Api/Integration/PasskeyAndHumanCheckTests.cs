using EstateHub.Api;
using EstateHub.Api.Security;
using EstateHub.Application.Auth;
using EstateHub.Domain.Entities;
using EstateHub.Infrastructure.Data;
using EstateHub.Tests.Infrastructure;
using FluentAssertions;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AppAuthService = EstateHub.Application.Services.AuthService;

namespace EstateHub.Tests.Api.Integration;

// Slice 2.5 through the real host: passkeys with a software authenticator (AUTH-03), and the
// human check after repeated failures (AUTH-05).
public class PasskeyAndHumanCheckTests : IClassFixture<EstateHubApiFactory>
{
    private const string Password = "correct horse battery";

    private readonly EstateHubApiFactory _factory;

    public PasskeyAndHumanCheckTests(EstateHubApiFactory factory) => _factory = factory;

    private AuthService.AuthServiceClient Auth() => new(_factory.CreateGrpcChannel());

    /// <summary>A super admin with an authenticator app and a fresh MFA token.</summary>
    private async Task<(Metadata Bearer, string Email)> StaffAsync()
    {
        var label = $"pk-{Guid.NewGuid():N}"[..20];
        var token = await _factory.TokenAsync(label, null, Roles.SuperAdmin);
        await _factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Email == $"{label}@dev.estatehub.local")).MfaEnabled = true;
            await db.SaveChangesAsync();
        });
        return (EstateHubApiFactory.Bearer(token), $"{label}@dev.estatehub.local");
    }

    private async Task RegisterAsync(SoftwareAuthenticator device, Metadata bearer, string name = "Test laptop")
    {
        var options = await Auth().BeginPasskeyRegistrationAsync(new BeginPasskeyRegistrationRequest(), bearer);
        await Auth().FinishPasskeyRegistrationAsync(new FinishPasskeyRegistrationRequest
        {
            ChallengeToken = options.ChallengeToken,
            CredentialJson = device.Create(options.OptionsJson),
            Name = name,
        }, bearer);
    }

    private async Task<StatusCode> StatusOf<T>(Func<AsyncUnaryCall<T>> call) => await GrpcStatus.Of(() => call().ResponseAsync);

    // ---------- passkeys ----------

    [Fact] // AUTH-03
    public async Task A_Registered_Passkey_Signs_In_With_Mfa()
    {
        var (bearer, email) = await StaffAsync();
        using var device = new SoftwareAuthenticator();
        await RegisterAsync(device, bearer, "MacBook Touch ID");

        var passkeys = await Auth().ListPasskeysAsync(new ListPasskeysRequest(), bearer);
        passkeys.Items.Should().ContainSingle(p => p.Name == "MacBook Touch ID");
        _factory.Outbox.LastTo(email).Should().Contain("passkey");

        var options = await Auth().BeginPasskeySignInAsync(new BeginPasskeySignInRequest());
        var signedIn = await Auth().FinishPasskeySignInAsync(new FinishPasskeySignInRequest
        {
            ChallengeToken = options.ChallengeToken,
            CredentialJson = device.Get(options.OptionsJson),
        });

        signedIn.Role.Should().Be(Roles.SuperAdmin, "a passkey counts as MFA, so staff permissions apply");
        await _factory.WithDbAsync(async db => (await db.UserPasskeys.SingleAsync(p => p.Name == "MacBook Touch ID")).SignCount.Should().Be(1));
    }

    [Fact]
    public async Task A_Passkey_Answer_From_Another_Website_Is_Refused()
    {
        var (bearer, _) = await StaffAsync();
        using var device = new SoftwareAuthenticator();
        await RegisterAsync(device, bearer);

        var options = await Auth().BeginPasskeySignInAsync(new BeginPasskeySignInRequest());
        var phished = device.Get(options.OptionsJson, origin: "https://estatehub-login.example");

        var error = await GrpcStatus.ErrorOf(() => Auth().FinishPasskeySignInAsync(new FinishPasskeySignInRequest
        {
            ChallengeToken = options.ChallengeToken,
            CredentialJson = phished,
        }).ResponseAsync);
        error!.StatusCode.Should().Be(StatusCode.Unauthenticated, "the origin check is what makes passkeys phishing-resistant");
        error.Status.Detail.Should().Be(AppAuthService.PasskeyFailed);
    }

    [Fact]
    public async Task A_Passkey_Ceremony_Works_Once()
    {
        var (bearer, _) = await StaffAsync();
        using var device = new SoftwareAuthenticator();
        await RegisterAsync(device, bearer);
        var options = await Auth().BeginPasskeySignInAsync(new BeginPasskeySignInRequest());
        var answer = device.Get(options.OptionsJson);

        await Auth().FinishPasskeySignInAsync(new FinishPasskeySignInRequest { ChallengeToken = options.ChallengeToken, CredentialJson = answer });

        (await StatusOf(() => Auth().FinishPasskeySignInAsync(new FinishPasskeySignInRequest { ChallengeToken = options.ChallengeToken, CredentialJson = answer })))
            .Should().Be(StatusCode.Unauthenticated, "a replayed answer is refused");
    }

    [Fact]
    public async Task Adding_A_Passkey_Needs_A_Fresh_Mfa_And_Removing_One_Stops_It_Working()
    {
        var (bearer, email) = await StaffAsync();
        var label = email.Split('@')[0];
        var stale = EstateHubApiFactory.Bearer(await _factory.TokenAsync(label, null, Roles.SuperAdmin, mfa: false));
        (await StatusOf(() => Auth().BeginPasskeyRegistrationAsync(new BeginPasskeyRegistrationRequest(), stale))).Should().Be(StatusCode.PermissionDenied);

        using var device = new SoftwareAuthenticator();
        await RegisterAsync(device, bearer);
        var id = (await Auth().ListPasskeysAsync(new ListPasskeysRequest(), bearer)).Items.Single().Id;
        await Auth().RemovePasskeyAsync(new RemovePasskeyRequest { PasskeyId = id }, bearer);

        var options = await Auth().BeginPasskeySignInAsync(new BeginPasskeySignInRequest());
        (await StatusOf(() => Auth().FinishPasskeySignInAsync(new FinishPasskeySignInRequest
        {
            ChallengeToken = options.ChallengeToken,
            CredentialJson = device.Get(options.OptionsJson),
        }))).Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Residents_Without_An_Authenticator_Cannot_Add_A_Passkey_Yet()
    {
        var estateId = await _factory.CreateEstateAsync($"Pk {Guid.NewGuid():N}");
        var owner = EstateHubApiFactory.Bearer(await _factory.TokenAsync($"pk-o-{Guid.NewGuid():N}"[..20], estateId, Roles.Owner));

        // A passkey is added on top of an authenticator app, which residents don't have.
        var error = await GrpcStatus.ErrorOf(() => Auth().BeginPasskeyRegistrationAsync(new BeginPasskeyRegistrationRequest(), owner).ResponseAsync);
        error!.StatusCode.Should().Be(StatusCode.InvalidArgument);
        error.Status.Detail.Should().Contain("authenticator app first");
    }

    // ---------- human check (AUTH-05) ----------

    [Fact]
    public async Task After_Three_Failures_Sign_In_Asks_For_The_Human_Check()
    {
        using var factory = new EstateHubApiFactory();
        factory.HumanCheck.Enabled = true;
        var auth = new AuthService.AuthServiceClient(factory.CreateGrpcChannel());
        var email = "human@example.co.za";
        await factory.WithDbAsync(async db =>
        {
            db.Users.Add(new User
            {
                Email = email,
                DisplayName = "Human",
                PasswordHash = factory.Services.GetRequiredService<IPasswordHashing>().Hash(Password),
            });
            await db.SaveChangesAsync();
        });

        for (var i = 0; i < 3; i++)
            (await GrpcStatus.Of(() => auth.PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = $"wrong {i} password" }).ResponseAsync))
                .Should().Be(StatusCode.Unauthenticated);

        var asked = await GrpcStatus.ErrorOf(() => auth.PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password }).ResponseAsync);
        asked!.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        asked.Trailers.GetValue(SignInResponses.ChallengeTrailer).Should().Be("turnstile");
        (await GrpcStatus.Of(() => auth.RequestOtpAsync(new RequestOtpRequest { Identifier = email }).ResponseAsync))
            .Should().Be(StatusCode.FailedPrecondition, "codes too: they're how SMS pumping works");

        var wrongToken = new Metadata { { SignInResponses.HumanCheckHeader, "bot" } };
        (await GrpcStatus.Of(() => auth.PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password }, wrongToken).ResponseAsync))
            .Should().Be(StatusCode.FailedPrecondition);

        var passed = new Metadata { { SignInResponses.HumanCheckHeader, FakeHumanCheck.PassingToken } };
        (await auth.PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password }, passed)).StepCase
            .Should().Be(PasswordSignInResponse.StepOneofCase.SignedIn);

        factory.Clock.Now = factory.Clock.Now.AddMinutes(16);
        (await auth.PasswordSignInAsync(new PasswordSignInRequest { Email = email, Password = Password })).StepCase
            .Should().Be(PasswordSignInResponse.StepOneofCase.SignedIn, "15 minutes later the address is trusted again");
    }

    [Fact]
    public async Task Without_A_Turnstile_Key_Nobody_Is_Asked()
    {
        using var factory = new EstateHubApiFactory(); // HumanCheck.Enabled = false, as in a setup without the key
        var auth = new AuthService.AuthServiceClient(factory.CreateGrpcChannel());

        for (var i = 0; i < 5; i++)
            (await GrpcStatus.Of(() => auth.PasswordSignInAsync(new PasswordSignInRequest { Email = "nobody@example.co.za", Password = "wrong password!" }).ResponseAsync))
                .Should().Be(StatusCode.Unauthenticated);
    }
}
