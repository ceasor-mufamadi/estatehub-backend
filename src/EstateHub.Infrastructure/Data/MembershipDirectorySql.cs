using EstateHub.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EstateHub.Infrastructure.Data;

/// <summary>
/// The one sanctioned way across the tenant boundary: sign-in must list a user's estates before any
/// estate is active, which row-level security (correctly) forbids for <c>estatehub_app</c>.
/// </summary>
/// <remarks>
/// - <c>estatehub_user_memberships(user_id)</c> is SECURITY DEFINER: it runs as its owner,
///   <c>estatehub_migrator</c>, with a pinned search_path (no hijacking through a planted object).
/// - FORCE RLS applies to the owner too, so a second, SELECT-only policy lets exactly that role read
///   memberships. The app role is never the migrator, so the tenant policy still binds it everywhere else.
/// - It returns one user's ACTIVE memberships only (estate id, name, role). Only the app role may call it.
/// </remarks>
public static class MembershipDirectorySql
{
    private const string Policy = "membership_directory";

    public static void Create(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            CREATE POLICY {Policy} ON memberships FOR SELECT
                USING (current_user = 'estatehub_migrator');

            CREATE FUNCTION {MembershipDirectory.FunctionName}(p_user_id uuid)
            RETURNS TABLE (estate_id uuid, estate_name text, role text)
            LANGUAGE sql STABLE SECURITY DEFINER
            SET search_path = public, pg_temp
            AS $$
                SELECT m.estate_id, e.name::text, m.role::text
                FROM memberships m
                JOIN estates e ON e.id = m.estate_id
                WHERE m.user_id = p_user_id AND m.is_active
                ORDER BY e.name
            $$;

            REVOKE ALL ON FUNCTION {MembershipDirectory.FunctionName}(uuid) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION {MembershipDirectory.FunctionName}(uuid) TO estatehub_app;
            """);
    }

    public static void Drop(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            DROP FUNCTION IF EXISTS {MembershipDirectory.FunctionName}(uuid);
            DROP POLICY IF EXISTS {Policy} ON memberships;
            """);
    }
}

/// <summary>
/// Accepting an invitation happens before any estate is active, so RLS hides the row.
/// <c>estatehub_invitation_estate(token_hash)</c> (SECURITY DEFINER, same rules as above) returns only
/// the estate id of the invitation with that token hash; the app then reads it inside that estate.
/// Knowing the hash requires the link token itself (256-bit), so the function can't be used to browse.
/// </summary>
public static class InvitationDirectorySql
{
    private const string Policy = "invitation_directory";

    public static void Create(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            CREATE POLICY {Policy} ON invitations FOR SELECT
                USING (current_user = 'estatehub_migrator');

            CREATE FUNCTION {InvitationDirectory.FunctionName}(p_token_hash text)
            RETURNS uuid
            LANGUAGE sql STABLE SECURITY DEFINER
            SET search_path = public, pg_temp
            AS $$
                SELECT estate_id FROM invitations WHERE token_hash = p_token_hash
            $$;

            REVOKE ALL ON FUNCTION {InvitationDirectory.FunctionName}(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION {InvitationDirectory.FunctionName}(text) TO estatehub_app;
            """);
    }

    public static void Drop(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            DROP FUNCTION IF EXISTS {InvitationDirectory.FunctionName}(text);
            DROP POLICY IF EXISTS {Policy} ON invitations;
            """);
    }
}

/// <summary>
/// Guard tablets (AUTH-14) are recognised, and paired, before any estate is active.
/// <c>estatehub_device_estate(kind, hash)</c> returns only the estate id for a pairing-code hash
/// ('pairing') or a tablet-credential hash ('device'); the hashes come from long random secrets.
/// Same owner, pinned search_path and EXECUTE rules as the other two functions.
/// </summary>
public static class DeviceDirectorySql
{
    public static void Create(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            CREATE POLICY device_directory ON guard_devices FOR SELECT USING (current_user = 'estatehub_migrator');
            CREATE POLICY device_directory ON pairing_codes FOR SELECT USING (current_user = 'estatehub_migrator');

            CREATE FUNCTION {DeviceDirectory.FunctionName}(p_kind text, p_hash text)
            RETURNS uuid
            LANGUAGE sql STABLE SECURITY DEFINER
            SET search_path = public, pg_temp
            AS $$
                SELECT CASE p_kind
                    WHEN 'pairing' THEN (SELECT estate_id FROM pairing_codes WHERE code_hash = p_hash)
                    WHEN 'device' THEN (SELECT estate_id FROM guard_devices WHERE credential_hash = p_hash)
                END
            $$;

            REVOKE ALL ON FUNCTION {DeviceDirectory.FunctionName}(text, text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION {DeviceDirectory.FunctionName}(text, text) TO estatehub_app;
            """);
    }

    public static void Drop(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            DROP FUNCTION IF EXISTS {DeviceDirectory.FunctionName}(text, text);
            DROP POLICY IF EXISTS device_directory ON guard_devices;
            DROP POLICY IF EXISTS device_directory ON pairing_codes;
            """);
    }
}
