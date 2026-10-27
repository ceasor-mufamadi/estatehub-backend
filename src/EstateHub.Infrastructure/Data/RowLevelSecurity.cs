using Microsoft.EntityFrameworkCore.Migrations;

namespace EstateHub.Infrastructure.Data;

/// <summary>
/// Postgres row-level security for tenant tables. Call <see cref="Enable"/> from the migration
/// that creates a tenant-scoped table (and <see cref="Disable"/> in its Down).
/// </summary>
/// <remarks>
/// Tables are owned by <c>estatehub_migrator</c>; the API connects as <c>estatehub_app</c>, which
/// can read/write rows but can't ALTER the table or switch RLS off (SECURITY.md layer 5).
/// FORCE also applies the policy to the owner, as a second net. Superusers always bypass RLS:
/// neither role is a superuser (docker/postgres/init creates them).
/// With no estate set, NULLIF turns '' into NULL and the policy matches nothing.
/// </remarks>
public static class RowLevelSecurity
{
    public const string PolicyName = "tenant_isolation";

    public static void Enable(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {PolicyName} ON {table}
                USING (estate_id = NULLIF(current_setting('app.estate_id', true), '')::uuid)
                WITH CHECK (estate_id = NULLIF(current_setting('app.estate_id', true), '')::uuid);
            """);
    }

    public static void Disable(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.Sql($"""
            DROP POLICY IF EXISTS {PolicyName} ON {table};
            ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
            """);
    }
}
