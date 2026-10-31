using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomatedInstallationQuotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "quotation",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "SALES");

            migrationBuilder.AddColumn<string>(
                name: "pricing_basis",
                table: "quotation",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_of_materials",
                table: "onsite_survey_report",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_quotation_origin",
                table: "quotation",
                sql: "origin IN ('SALES', 'AUTOMATIC')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_quotation_origin",
                table: "quotation");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "quotation");

            migrationBuilder.DropColumn(
                name: "pricing_basis",
                table: "quotation");

            migrationBuilder.DropColumn(
                name: "bill_of_materials",
                table: "onsite_survey_report");
        }
    }
}
