using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BillingSetupAndAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_on = table.Column<DateOnly>(type: "date", nullable: false),
                    closed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_accounts_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_accounts_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "billing_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    financial_year_start_month = table.Column<int>(type: "integer", nullable: false),
                    billing_day = table.Column<int>(type: "integer", nullable: false),
                    due_day = table.Column<int>(type: "integer", nullable: false),
                    levy_display = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    interest_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    interest_rate_percent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    queries_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    queries_phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    bank_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    bank_branch_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    bank_account_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    bank_account_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_billing_settings_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "charge_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    is_levy = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge_types", x => x.id);
                    table.ForeignKey(
                        name: "fk_charge_types_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "account_holders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_holders", x => x.id);
                    table.ForeignKey(
                        name: "fk_account_holders_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_account_holders_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_account_holders_people_person_id",
                        column: x => x.person_id,
                        principalTable: "people",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    charge_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    debit = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    credit = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reverses_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                    table.CheckConstraint("ck_ledger_one_side", "(debit >= 0 AND credit >= 0) AND (debit = 0 OR credit = 0) AND (debit + credit > 0)");
                    table.ForeignKey(
                        name: "fk_ledger_entries_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_entries_charge_types_charge_type_id",
                        column: x => x.charge_type_id,
                        principalTable: "charge_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ledger_entries_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "unit_charges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_charges", x => x.id);
                    table.ForeignKey(
                        name: "fk_unit_charges_charge_types_charge_type_id",
                        column: x => x.charge_type_id,
                        principalTable: "charge_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_unit_charges_estates_estate_id",
                        column: x => x.estate_id,
                        principalTable: "estates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_unit_charges_units_unit_id",
                        column: x => x.unit_id,
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_holders_account_id_person_id",
                table: "account_holders",
                columns: new[] { "account_id", "person_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_holders_estate_id",
                table: "account_holders",
                column: "estate_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_holders_person_id",
                table: "account_holders",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_accounts_estate_id_unit_id",
                table: "accounts",
                columns: new[] { "estate_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ux_accounts_one_open_per_unit",
                table: "accounts",
                column: "unit_id",
                unique: true,
                filter: "closed_on IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_billing_settings_estate_id",
                table: "billing_settings",
                column: "estate_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_charge_types_estate_id_name",
                table: "charge_types",
                columns: new[] { "estate_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_charge_types_one_levy",
                table: "charge_types",
                column: "estate_id",
                unique: true,
                filter: "is_levy");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_account_id",
                table: "ledger_entries",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_charge_type_id",
                table: "ledger_entries",
                column: "charge_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_estate_id_account_id_date",
                table: "ledger_entries",
                columns: new[] { "estate_id", "account_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_reverses_entry_id",
                table: "ledger_entries",
                column: "reverses_entry_id",
                unique: true,
                filter: "reverses_entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_unit_charges_charge_type_id_unit_id",
                table: "unit_charges",
                columns: new[] { "charge_type_id", "unit_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_charges_estate_id",
                table: "unit_charges",
                column: "estate_id");

            migrationBuilder.CreateIndex(
                name: "ix_unit_charges_unit_id",
                table: "unit_charges",
                column: "unit_id");

            RowLevelSecurity.Enable(migrationBuilder, "billing_settings");
            RowLevelSecurity.Enable(migrationBuilder, "charge_types");
            RowLevelSecurity.Enable(migrationBuilder, "unit_charges");
            RowLevelSecurity.Enable(migrationBuilder, "accounts");
            RowLevelSecurity.Enable(migrationBuilder, "account_holders");
            RowLevelSecurity.Enable(migrationBuilder, "ledger_entries");

            // The ledger is append-only for the app (like audit_logs): corrections are reversals, never edits.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON ledger_entries FROM estatehub_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RowLevelSecurity.Disable(migrationBuilder, "ledger_entries");
            RowLevelSecurity.Disable(migrationBuilder, "account_holders");
            RowLevelSecurity.Disable(migrationBuilder, "accounts");
            RowLevelSecurity.Disable(migrationBuilder, "unit_charges");
            RowLevelSecurity.Disable(migrationBuilder, "charge_types");
            RowLevelSecurity.Disable(migrationBuilder, "billing_settings");

            migrationBuilder.DropTable(
                name: "account_holders");

            migrationBuilder.DropTable(
                name: "billing_settings");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "unit_charges");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "charge_types");
        }
    }
}
