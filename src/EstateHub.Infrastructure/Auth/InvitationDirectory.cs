using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// Token hash → estate id, through the <c>estatehub_invitation_estate</c> SECURITY DEFINER function
/// (migration Invitations, see <see cref="Data.InvitationDirectorySql"/>). Returns nothing else about the invitation;
/// the caller then reads it normally inside that estate.
/// </summary>
public sealed class InvitationDirectory : IInvitationDirectory
{
    public const string FunctionName = "estatehub_invitation_estate";

    private readonly EstateHubDbContext _db;

    public InvitationDirectory(EstateHubDbContext db)
    {
        _db = db;
    }

    public async Task<Guid?> EstateForTokenAsync(string tokenHash, CancellationToken ct = default)
    {
        var connection = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {FunctionName}(@token_hash)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "token_hash";
            parameter.Value = tokenHash;
            command.Parameters.Add(parameter);

            return await command.ExecuteScalarAsync(ct) is Guid estateId ? estateId : null;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
