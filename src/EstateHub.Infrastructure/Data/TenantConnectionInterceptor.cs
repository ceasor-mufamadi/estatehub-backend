using System.Data.Common;
using EstateHub.Application.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EstateHub.Infrastructure.Data;

/// <summary>
/// Tenant isolation, layer 3 of 3: every time EF opens a Postgres connection, the active estate is
/// written to the session setting <c>app.estate_id</c>. The row-level security policies
/// (<see cref="RowLevelSecurity"/>) only show and accept rows of that estate, so even a query that
/// bypasses the EF filters (raw SQL, IgnoreQueryFilters, a bug) cannot cross estates.
/// Pooled connections are re-stamped on every open, so a previous scope's estate never leaks.
/// </summary>
public sealed class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private const string Sql = "SELECT set_config('app.estate_id', @estate_id, false)";

    private readonly ITenantContext _tenant;

    public TenantConnectionInterceptor(ITenantContext tenant)
    {
        _tenant = tenant;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "estate_id";
        parameter.Value = _tenant.EstateId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);

        return command;
    }
}
