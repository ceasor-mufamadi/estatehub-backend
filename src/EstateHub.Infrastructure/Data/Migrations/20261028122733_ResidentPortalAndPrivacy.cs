using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstateHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResidentPortalAndPrivacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "anonymised_at",
                table: "people",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "notices_by_email",
                table: "people",
                type: "boolean",
                nullable: false,
                defaultValue: true); // existing people keep getting notices by email

            migrationBuilder.AddColumn<bool>(
                name: "notices_by_sms",
                table: "people",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "privacy_acknowledged_at",
                table: "people",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "privacy_notice_version",
                table: "people",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "new_contact",
                table: "otp_challenges",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "information_officer_email",
                table: "estates",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "information_officer_name",
                table: "estates",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "visitor_id_capture",
                table: "estates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "visitor_retention_days",
                table: "estates",
                type: "integer",
                nullable: false,
                defaultValue: 90); // POPIA default (decided 2026-10-28)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "anonymised_at",
                table: "people");

            migrationBuilder.DropColumn(
                name: "notices_by_email",
                table: "people");

            migrationBuilder.DropColumn(
                name: "notices_by_sms",
                table: "people");

            migrationBuilder.DropColumn(
                name: "privacy_acknowledged_at",
                table: "people");

            migrationBuilder.DropColumn(
                name: "privacy_notice_version",
                table: "people");

            migrationBuilder.DropColumn(
                name: "new_contact",
                table: "otp_challenges");

            migrationBuilder.DropColumn(
                name: "information_officer_email",
                table: "estates");

            migrationBuilder.DropColumn(
                name: "information_officer_name",
                table: "estates");

            migrationBuilder.DropColumn(
                name: "visitor_id_capture",
                table: "estates");

            migrationBuilder.DropColumn(
                name: "visitor_retention_days",
                table: "estates");
        }
    }
}
