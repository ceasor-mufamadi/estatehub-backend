using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Imports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    rows_json = table.Column<string>(type: "jsonb", nullable: false),
                    problems_json = table.Column<string>(type: "jsonb", nullable: false),
                    summary_json = table.Column<string>(type: "jsonb", nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    send_invitations = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    committed_by_role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    committed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_invite_batch_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_batches_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_invites", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_invites_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_invites_import_batches_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_invites_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_estate_id_created_at",
                table: "import_batches",
                columns: new[] { "estate_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_import_batches_one_active",
                table: "import_batches",
                column: "estate_id",
                unique: true,
                filter: "status IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_import_invites_estate_id",
                table: "import_invites",
                column: "estate_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_invites_import_batch_id_status",
                table: "import_invites",
                columns: new[] { "import_batch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_import_invites_person_id",
                table: "import_invites",
                column: "person_id");

            RowLevelSecurity.Enable(migrationBuilder, "import_batches");
            RowLevelSecurity.Enable(migrationBuilder, "import_invites");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RowLevelSecurity.Disable(migrationBuilder, "import_invites");
            RowLevelSecurity.Disable(migrationBuilder, "import_batches");

            migrationBuilder.DropTable(
                name: "import_invites");

            migrationBuilder.DropTable(
                name: "import_batches");
        }
    }
}
