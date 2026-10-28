using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class OneHomePerPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_occupancies_one_open_home",
                table: "occupancies",
                column: "person_id",
                unique: true,
                filter: "to_date IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_occupancies_one_open_home",
                table: "occupancies");
        }
    }
}
