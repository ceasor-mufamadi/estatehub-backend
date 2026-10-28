using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BillingRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    billing_date = table.Column<DateOnly>(type: "date", nullable: false),
                    total_posted = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    accounts_posted = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    posted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    posted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_billing_runs_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "levy_budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    admin_fund_monthly = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    reserve_fund_monthly = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_levy_budgets", x => x.id);
                    table.ForeignKey(
                        name: "fk_levy_budgets_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "metered_amounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    charge_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metered_amounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_metered_amounts_charge_types_charge_type_id",
                        column: x => x.charge_type_id,
                        principalTable: "charge_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_metered_amounts_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_metered_amounts_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_source_id",
                table: "ledger_entries",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_billing_runs_estate_id_period",
                table: "billing_runs",
                columns: new[] { "estate_id", "period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_levy_budgets_estate_id_effective_from",
                table: "levy_budgets",
                columns: new[] { "estate_id", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_metered_amounts_charge_type_id",
                table: "metered_amounts",
                column: "charge_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_metered_amounts_estate_id_period_charge_type_id_unit_id",
                table: "metered_amounts",
                columns: new[] { "estate_id", "period", "charge_type_id", "unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_metered_amounts_unit_id",
                table: "metered_amounts",
                column: "unit_id");

            RowLevelSecurity.Enable(migrationBuilder, "levy_budgets");
            RowLevelSecurity.Enable(migrationBuilder, "metered_amounts");
            RowLevelSecurity.Enable(migrationBuilder, "billing_runs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RowLevelSecurity.Disable(migrationBuilder, "billing_runs");
            RowLevelSecurity.Disable(migrationBuilder, "metered_amounts");
            RowLevelSecurity.Disable(migrationBuilder, "levy_budgets");

            migrationBuilder.DropTable(
                name: "billing_runs");

            migrationBuilder.DropTable(
                name: "levy_budgets");

            migrationBuilder.DropTable(
                name: "metered_amounts");

            migrationBuilder.DropIndex(
                name: "ix_ledger_entries_source_id",
                table: "ledger_entries");
        }
    }
}
