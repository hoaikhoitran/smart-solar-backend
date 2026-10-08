using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSolarSimulation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "geometry_version",
                table: "pre_survey",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "obstacles",
                table: "pre_survey",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "pre_survey",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "selected_simulation_id",
                table: "pre_survey",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "surface_length_m",
                table: "pre_survey",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "surface_width_m",
                table: "pre_survey",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "solar_simulation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pre_survey_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pre_survey_geometry_version = table.Column<int>(type: "integer", nullable: false),
                    input_fingerprint = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: false),
                    is_reusable = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    energy_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    climate_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    algorithm_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_sku = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    product_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    product_brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    product_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    product_rated_power_w = table.Column<decimal>(type: "numeric", nullable: false),
                    product_width_mm = table.Column<decimal>(type: "numeric", nullable: false),
                    product_height_mm = table.Column<decimal>(type: "numeric", nullable: false),
                    product_installation_spec = table.Column<string>(type: "jsonb", nullable: true),
                    surface_length_m = table.Column<decimal>(type: "numeric", nullable: false),
                    surface_width_m = table.Column<decimal>(type: "numeric", nullable: false),
                    surface_tilt_degree = table.Column<decimal>(type: "numeric", nullable: false),
                    surface_azimuth_degree = table.Column<decimal>(type: "numeric", nullable: false),
                    latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    mounting_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    panel_tilt_degree = table.Column<decimal>(type: "numeric", nullable: false),
                    panel_azimuth_degree = table.Column<decimal>(type: "numeric", nullable: false),
                    layout_orientation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    panel_count = table.Column<int>(type: "integer", nullable: false),
                    installed_capacity_kwp = table.Column<decimal>(type: "numeric", nullable: false),
                    gross_surface_area_m2 = table.Column<decimal>(type: "numeric", nullable: false),
                    obstacle_occupied_area_m2 = table.Column<decimal>(type: "numeric", nullable: false),
                    available_surface_area_m2 = table.Column<decimal>(type: "numeric", nullable: false),
                    installable_area_m2 = table.Column<decimal>(type: "numeric", nullable: false),
                    panel_covered_area_m2 = table.Column<decimal>(type: "numeric", nullable: false),
                    total_module_area_m2 = table.Column<decimal>(type: "numeric", nullable: false),
                    annual_energy_kwh = table.Column<decimal>(type: "numeric", nullable: true),
                    specific_yield_kwh_per_kwp_year = table.Column<decimal>(type: "numeric", nullable: true),
                    obstacles = table.Column<string>(type: "jsonb", nullable: false),
                    installation = table.Column<string>(type: "jsonb", nullable: false),
                    layout = table.Column<string>(type: "jsonb", nullable: false),
                    warnings = table.Column<string>(type: "jsonb", nullable: false),
                    energy = table.Column<string>(type: "jsonb", nullable: false),
                    climate = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_solar_simulation", x => x.id);
                    table.UniqueConstraint("ak_solar_simulation_pre_survey_id_id", x => new { x.pre_survey_id, x.id });
                    table.CheckConstraint("ck_solar_simulation_climate_status", "climate_status IN ('SUCCEEDED', 'UNAVAILABLE', 'FAILED')");
                    table.CheckConstraint("ck_solar_simulation_energy_status", "energy_status IN ('SUCCEEDED', 'NOT_APPLICABLE', 'UNAVAILABLE', 'FAILED')");
                    table.CheckConstraint("ck_solar_simulation_geometry_version_non_negative", "pre_survey_geometry_version >= 0");
                    table.CheckConstraint("ck_solar_simulation_mounting_type", "mounting_type IN ('FLUSH', 'RACK')");
                    table.CheckConstraint("ck_solar_simulation_panel_count_non_negative", "panel_count >= 0");
                    table.CheckConstraint("ck_solar_simulation_status", "status IN ('COMPLETED', 'PARTIALLY_COMPLETED')");
                    table.ForeignKey(
                        name: "FK_solar_simulation_pre_survey_pre_survey_id",
                        column: x => x.pre_survey_id,
                        principalTable: "pre_survey",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_solar_simulation_product_product_id",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_solar_simulation_user_account_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pre_survey_id_selected_simulation_id",
                table: "pre_survey",
                columns: new[] { "id", "selected_simulation_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_pre_survey_surface_dimensions_paired",
                table: "pre_survey",
                sql: "(surface_length_m IS NULL) = (surface_width_m IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pre_survey_versions_non_negative",
                table: "pre_survey",
                sql: "geometry_version >= 0 AND revision >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_solar_simulation_created_by_user_id",
                table: "solar_simulation",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_solar_simulation_pre_survey_id_created_at",
                table: "solar_simulation",
                columns: new[] { "pre_survey_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_solar_simulation_product_id",
                table: "solar_simulation",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_solar_simulation_reusable_fingerprint",
                table: "solar_simulation",
                columns: new[] { "pre_survey_id", "input_fingerprint" },
                unique: true,
                filter: "\"is_reusable\"");

            migrationBuilder.AddForeignKey(
                name: "fk_pre_survey_selected_simulation_same_pre_survey",
                table: "pre_survey",
                columns: new[] { "id", "selected_simulation_id" },
                principalTable: "solar_simulation",
                principalColumns: new[] { "pre_survey_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_pre_survey_selected_simulation_same_pre_survey",
                table: "pre_survey");

            migrationBuilder.DropTable(
                name: "solar_simulation");

            migrationBuilder.DropIndex(
                name: "IX_pre_survey_id_selected_simulation_id",
                table: "pre_survey");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pre_survey_surface_dimensions_paired",
                table: "pre_survey");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pre_survey_versions_non_negative",
                table: "pre_survey");

            migrationBuilder.DropColumn(
                name: "geometry_version",
                table: "pre_survey");

            migrationBuilder.DropColumn(
                name: "obstacles",
                table: "pre_survey");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "pre_survey");

            migrationBuilder.DropColumn(
                name: "selected_simulation_id",
                table: "pre_survey");

            migrationBuilder.DropColumn(
                name: "surface_length_m",
                table: "pre_survey");

            migrationBuilder.DropColumn(
                name: "surface_width_m",
                table: "pre_survey");
        }
    }
}
