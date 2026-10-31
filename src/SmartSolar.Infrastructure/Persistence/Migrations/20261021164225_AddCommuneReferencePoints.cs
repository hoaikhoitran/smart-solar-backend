using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommuneReferencePoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "reference_latitude",
                table: "administrative_commune",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "reference_longitude",
                table: "administrative_commune",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reference_point_source",
                table: "administrative_commune",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_administrative_commune_reference_point",
                table: "administrative_commune",
                sql: "(reference_latitude IS NULL) = (reference_longitude IS NULL) AND (reference_latitude IS NULL) = (reference_point_source IS NULL) AND (reference_latitude IS NULL OR (reference_latitude BETWEEN -90 AND 90 AND reference_longitude BETWEEN -180 AND 180))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_administrative_commune_reference_point",
                table: "administrative_commune");

            migrationBuilder.DropColumn(
                name: "reference_latitude",
                table: "administrative_commune");

            migrationBuilder.DropColumn(
                name: "reference_longitude",
                table: "administrative_commune");

            migrationBuilder.DropColumn(
                name: "reference_point_source",
                table: "administrative_commune");
        }
    }
}
