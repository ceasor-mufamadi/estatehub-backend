using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class GuardTabletsAndSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "guard_device_id",
                table: "auth_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "impersonation_ends_at",
                table: "auth_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "impersonation_estate_id",
                table: "auth_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "impersonation_reason",
                table: "auth_sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "guard_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    credential_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    paired_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guard_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_guard_devices_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guard_pins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pin_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guard_pins", x => x.id);
                    table.ForeignKey(
                        name: "fk_guard_pins_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_guard_pins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pairing_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pairing_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_pairing_codes_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_guard_device_id",
                table: "auth_sessions",
                column: "guard_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_guard_devices_credential_hash",
                table: "guard_devices",
                column: "credential_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guard_devices_estate_id",
                table: "guard_devices",
                column: "estate_id");

            migrationBuilder.CreateIndex(
                name: "ix_guard_pins_estate_id_user_id",
                table: "guard_pins",
                columns: new[] { "estate_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guard_pins_user_id",
                table: "guard_pins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pairing_codes_code_hash",
                table: "pairing_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pairing_codes_estate_id",
                table: "pairing_codes",
                column: "estate_id");

            RowLevelSecurity.Enable(migrationBuilder, "guard_devices");
            RowLevelSecurity.Enable(migrationBuilder, "pairing_codes");
            RowLevelSecurity.Enable(migrationBuilder, "guard_pins");
            DeviceDirectorySql.Create(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DeviceDirectorySql.Drop(migrationBuilder);
            RowLevelSecurity.Disable(migrationBuilder, "guard_pins");
            RowLevelSecurity.Disable(migrationBuilder, "pairing_codes");
            RowLevelSecurity.Disable(migrationBuilder, "guard_devices");

            migrationBuilder.DropTable(
                name: "guard_devices");

            migrationBuilder.DropTable(
                name: "guard_pins");

            migrationBuilder.DropTable(
                name: "pairing_codes");

            migrationBuilder.DropIndex(
                name: "ix_auth_sessions_guard_device_id",
                table: "auth_sessions");

            migrationBuilder.DropColumn(
                name: "guard_device_id",
                table: "auth_sessions");

            migrationBuilder.DropColumn(
                name: "impersonation_ends_at",
                table: "auth_sessions");

            migrationBuilder.DropColumn(
                name: "impersonation_estate_id",
                table: "auth_sessions");

            migrationBuilder.DropColumn(
                name: "impersonation_reason",
                table: "auth_sessions");
        }
    }
}
