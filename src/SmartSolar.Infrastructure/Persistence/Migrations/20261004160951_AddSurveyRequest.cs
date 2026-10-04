using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSurveyRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "roof_material",
                table: "property_site");

            migrationBuilder.DropColumn(
                name: "roof_type",
                table: "property_site");

            migrationBuilder.AddColumn<string>(
                name: "installation_surface_type",
                table: "property_site",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "surface_material",
                table: "property_site",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "survey_request",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pre_survey_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sales_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_request", x => x.id);
                    table.ForeignKey(
                        name: "FK_survey_request_pre_survey_pre_survey_id",
                        column: x => x.pre_survey_id,
                        principalTable: "pre_survey",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_survey_request_user_account_assigned_sale_id",
                        column: x => x.assigned_sale_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_survey_request_assigned_sale_id",
                table: "survey_request",
                column: "assigned_sale_id");

            migrationBuilder.CreateIndex(
                name: "IX_survey_request_pre_survey_id",
                table: "survey_request",
                column: "pre_survey_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "survey_request");

            migrationBuilder.DropColumn(
                name: "installation_surface_type",
                table: "property_site");

            migrationBuilder.DropColumn(
                name: "surface_material",
                table: "property_site");

            migrationBuilder.AddColumn<string>(
                name: "roof_material",
                table: "property_site",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "roof_type",
                table: "property_site",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
