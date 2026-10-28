using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Units : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address",
                table: "estates",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "csos_registration_number",
                table: "estates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scheme_number",
                table: "estates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "units",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    block_id = table.Column<Guid>(type: "uuid", nullable: true),
                    section_number = table.Column<int>(type: "integer", nullable: false),
                    door_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    participation_quota = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    size_square_metres = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_units", x => x.id);
                    table.ForeignKey(
                        name: "fk_units_blocks_block_id",
                        column: x => x.block_id,
                        principalTable: "blocks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_units_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_units_block_id",
                table: "units",
                column: "block_id");

            migrationBuilder.CreateIndex(
                name: "ix_units_estate_id_section_number",
                table: "units",
                columns: new[] { "estate_id", "section_number" },
                unique: true);

            RowLevelSecurity.Enable(migrationBuilder, "units");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RowLevelSecurity.Disable(migrationBuilder, "units");

            migrationBuilder.DropTable(
                name: "units");

            migrationBuilder.DropColumn(
                name: "address",
                table: "estates");

            migrationBuilder.DropColumn(
                name: "csos_registration_number",
                table: "estates");

            migrationBuilder.DropColumn(
                name: "scheme_number",
                table: "estates");
        }
    }
}
