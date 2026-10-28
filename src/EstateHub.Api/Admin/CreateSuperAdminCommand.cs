using System.Text;
using EstateHub.Application.Auth;

namespace EstateHub.Api.Admin;

/// <summary>
/// Creates the first platform operator. There is no public sign-up (AUTH-01), so the very first
/// account has to come from someone with access to the server:
///   docker compose -f docker-compose.dev.yml run --rm -e ASPNETCORE_ENVIRONMENT=Development sdk \
///     dotnet run --project src/EstateHub.Api -- create-super-admin
/// Asks for email, name and password (hidden). The authenticator app is set up at first sign-in.
/// </summary>
public static class CreateSuperAdminCommand
{
    public const string Name = "create-super-admin";

    public static async Task RunAsync(IServiceProvider services)
    {
        var email = Prompt("Email: ");
        var name = Prompt("Name: ");
        var password = PromptHidden($"Password (at least {Application.Services.AuthService.MinPasswordLength} characters): ");
        if (PromptHidden("Repeat password: ") != password)
        {
            Console.Error.WriteLine("The passwords don't match. Nothing was created.");
            Environment.ExitCode = 1;
            return;
        }

        using var scope = services.CreateScope();
        try
        {
            var user = await scope.ServiceProvider.GetRequiredService<IAuthService>().CreateSuperAdminAsync(email, name, password);
            Console.WriteLine($"Super admin {user.Contact} created. Sign in on the web app; you'll set up an authenticator app first.");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"{ex.Message} Nothing was created.");
            Environment.ExitCode = 1;
        }
    }

    private static string Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    private static string PromptHidden(string label)
    {
        Console.Write(label);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                    value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return value.ToString();
    }
}
