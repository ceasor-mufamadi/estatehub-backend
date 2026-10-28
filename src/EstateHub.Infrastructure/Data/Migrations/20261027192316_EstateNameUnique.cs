using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EstateNameUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One estate per name, ignoring case ("Riverside" = "riverside"). EstateService checks first
            // and explains; this index is the backstop for two creates at the same moment. EF can't model
            // an expression index, so it lives here only (EstateRepository.NameExistsAsync matches it).
            // Fails if duplicates already exist: resolve them first (the dev database was cleaned 2026-10-27).
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_estates_name_ci ON estates (lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_estates_name_ci;");
        }
    }
}
