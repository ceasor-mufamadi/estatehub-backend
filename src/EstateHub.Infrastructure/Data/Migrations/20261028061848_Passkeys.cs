using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Passkeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "passkey_challenges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_passkey_challenges", x => x.id);
                    table.ForeignKey(
                        name: "fk_passkey_challenges_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_passkeys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", maxLength: 2048, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sign_count = table.Column<long>(type: "bigint", nullable: false),
                    transports = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_user_verified = table.Column<bool>(type: "boolean", nullable: false),
                    is_backup_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    is_backed_up = table.Column<bool>(type: "boolean", nullable: false),
                    attestation_object = table.Column<byte[]>(type: "bytea", nullable: true),
                    client_data_json = table.Column<byte[]>(type: "bytea", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_passkeys", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_passkeys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_passkey_challenges_expires_at",
                table: "passkey_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_passkey_challenges_token_hash",
                table: "passkey_challenges",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_passkey_challenges_user_id",
                table: "passkey_challenges",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_passkeys_credential_id",
                table: "user_passkeys",
                column: "credential_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_passkeys_user_id",
                table: "user_passkeys",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "passkey_challenges");

            migrationBuilder.DropTable(
                name: "user_passkeys");
        }
    }
}
