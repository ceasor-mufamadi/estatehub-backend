using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PeopleAndOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "person_id",
                table: "invitations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "people",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_people", x => x.id);
                    table.ForeignKey(
                        name: "fk_people_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_people_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "occupancies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_occupancies", x => x.id);
                    table.ForeignKey(
                        name: "fk_occupancies_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_occupancies_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_occupancies_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ownerships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ownerships", x => x.id);
                    table.ForeignKey(
                        name: "fk_ownerships_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ownerships_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ownerships_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_occupancies_estate_id_person_id",
                table: "occupancies",
                columns: new[] { "estate_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "ix_occupancies_estate_id_unit_id",
                table: "occupancies",
                columns: new[] { "estate_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_occupancies_person_id",
                table: "occupancies",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_occupancies_unit_id",
                table: "occupancies",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_ownerships_estate_id_person_id",
                table: "ownerships",
                columns: new[] { "estate_id", "person_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ownerships_estate_id_unit_id",
                table: "ownerships",
                columns: new[] { "estate_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ownerships_person_id",
                table: "ownerships",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_ownerships_unit_id",
                table: "ownerships",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_people_estate_id_email",
                table: "people",
                columns: new[] { "estate_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_people_estate_id_phone",
                table: "people",
                columns: new[] { "estate_id", "phone" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_people_estate_id_user_id",
                table: "people",
                columns: new[] { "estate_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_people_user_id",
                table: "people",
                column: "user_id");

            RowLevelSecurity.Enable(migrationBuilder, "people");
            RowLevelSecurity.Enable(migrationBuilder, "ownerships");
            RowLevelSecurity.Enable(migrationBuilder, "occupancies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RowLevelSecurity.Disable(migrationBuilder, "occupancies");
            RowLevelSecurity.Disable(migrationBuilder, "ownerships");
            RowLevelSecurity.Disable(migrationBuilder, "people");

            migrationBuilder.DropTable(
                name: "occupancies");

            migrationBuilder.DropTable(
                name: "ownerships");

            migrationBuilder.DropTable(
                name: "people");

            migrationBuilder.DropColumn(
                name: "person_id",
                table: "invitations");
        }
    }
}
