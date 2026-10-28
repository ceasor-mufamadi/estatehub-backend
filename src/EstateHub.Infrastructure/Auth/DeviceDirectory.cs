using EstateHub.Application.Auth;
using EstateHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EstateHub.Infrastructure.Auth;

/// <summary>
/// Tablet credential / pairing code hash → estate id, through the <c>estatehub_device_estate</c>
/// SECURITY DEFINER function (see <see cref="Data.DeviceDirectorySql"/>). Same pattern as invitations.
/// </summary>
public sealed class DeviceDirectory : IDeviceDirectory
{
    public const string FunctionName = "estatehub_device_estate";

    private readonly EstateHubDbContext _db;

    public DeviceDirectory(EstateHubDbContext db)
    {
        _db = db;
    }

    public Task<Guid?> EstateForDeviceAsync(string credentialHash, CancellationToken ct = default) => LookupAsync("device", credentialHash, ct);

    public Task<Guid?> EstateForPairingCodeAsync(string codeHash, CancellationToken ct = default) => LookupAsync("pairing", codeHash, ct);

    private async Task<Guid?> LookupAsync(string kind, string hash, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {FunctionName}(@kind, @hash)";
            foreach (var (name, value) in new[] { ("kind", kind), ("hash", hash) })
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            return await command.ExecuteScalarAsync(ct) is Guid estateId ? estateId : null;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}
