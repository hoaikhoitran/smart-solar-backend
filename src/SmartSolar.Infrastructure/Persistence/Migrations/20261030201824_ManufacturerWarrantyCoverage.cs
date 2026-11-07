using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturerWarrantyCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_service_item_coverage",
                table: "service_item");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "equipment_warranty_ends_at",
                table: "service_item",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "equipment_warranty_months",
                table: "service_item",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "equipment_warranty_source",
                table: "service_item",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "warranty_ends_at",
                table: "installed_asset",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warranty_source",
                table: "installed_asset",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "warranty_starts_at",
                table: "installed_asset",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_item_coverage",
                table: "service_item",
                sql: "coverage IN ('PENDING', 'INSTALLATION_WARRANTY', 'REPAIR_WARRANTY', 'DISASTER_PROTECTION', 'CHARGEABLE', 'MANUFACTURER_WARRANTY')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_item_equipment_warranty_source",
                table: "service_item",
                sql: "equipment_warranty_source IS NULL OR equipment_warranty_source IN ('CATALOG_PRODUCT', 'LEGACY_UNVERIFIED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_item_manufacturer_warranty",
                table: "service_item",
                sql: "coverage <> 'MANUFACTURER_WARRANTY' OR equipment_warranty_ends_at IS NOT NULL");

            // Backfill: durations recorded before their source existed were typed by a technician or defaulted; they cannot be
            // verified now, so they are labelled as such (never as catalog data). Required before ck_installed_asset_warranty_source.
            migrationBuilder.Sql("UPDATE installed_asset SET warranty_source = 'LEGACY_UNVERIFIED' WHERE warranty_months IS NOT NULL AND warranty_source IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installed_asset_warranty_period",
                table: "installed_asset",
                sql: "warranty_ends_at IS NULL OR (warranty_starts_at IS NOT NULL AND warranty_months IS NOT NULL AND warranty_ends_at > warranty_starts_at)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installed_asset_warranty_source",
                table: "installed_asset",
                sql: "(warranty_months IS NULL) = (warranty_source IS NULL) AND (warranty_source IS NULL OR warranty_source IN ('CATALOG_PRODUCT', 'LEGACY_UNVERIFIED'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_service_item_coverage",
                table: "service_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_item_equipment_warranty_source",
                table: "service_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_item_manufacturer_warranty",
                table: "service_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installed_asset_warranty_period",
                table: "installed_asset");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installed_asset_warranty_source",
                table: "installed_asset");

            migrationBuilder.DropColumn(
                name: "equipment_warranty_ends_at",
                table: "service_item");

            migrationBuilder.DropColumn(
                name: "equipment_warranty_months",
                table: "service_item");

            migrationBuilder.DropColumn(
                name: "equipment_warranty_source",
                table: "service_item");

            migrationBuilder.DropColumn(
                name: "warranty_ends_at",
                table: "installed_asset");

            migrationBuilder.DropColumn(
                name: "warranty_source",
                table: "installed_asset");

            migrationBuilder.DropColumn(
                name: "warranty_starts_at",
                table: "installed_asset");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_item_coverage",
                table: "service_item",
                sql: "coverage IN ('PENDING', 'INSTALLATION_WARRANTY', 'REPAIR_WARRANTY', 'DISASTER_PROTECTION', 'CHARGEABLE')");
        }
    }
}
