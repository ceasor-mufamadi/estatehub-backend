using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// A user's active memberships in every estate, through the <c>estatehub_user_memberships</c>
/// SECURITY DEFINER function (migration AuthCore). The function runs as the table owner, which a
/// separate read-only policy lets see memberships across estates; the app role itself still can't.
/// It only ever answers for one user id, and returns estate id, name and role, nothing else.
/// </summary>
public sealed class MembershipDirectory : IMembershipDirectory
{
    public const string FunctionName = "estatehub_user_memberships";

    private readonly EstateHubDbContext _db;

    public MembershipDirectory(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<UserMembership>> ForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var connection = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT estate_id, estate_name, role FROM {FunctionName}(@user_id)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "user_id";
            parameter.Value = userId;
            command.Parameters.Add(parameter);

            var result = new List<UserMembership>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new UserMembership(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            return result;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
