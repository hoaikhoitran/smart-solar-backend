using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstalledAssetLotsAndPackageImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference");

            migrationBuilder.AddColumn<string>(
                name: "image_url",
                table: "service_package",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "serial_number",
                table: "installed_asset",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<int>(
                name: "quantity",
                table: "installed_asset",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_package_image_url_https",
                table: "service_package",
                sql: "image_url IS NULL OR image_url LIKE 'https://%'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installed_asset_lot_product",
                table: "installed_asset",
                sql: "serial_number IS NOT NULL OR product_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installed_asset_quantity",
                table: "installed_asset",
                sql: "quantity BETWEEN 1 AND 10000 AND (serial_number IS NULL OR quantity = 1)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference",
                sql: "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT', 'SERVICE_EVIDENCE', 'PRODUCT_IMAGE', 'PACKAGE_IMAGE')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous schema cannot represent a lot (several units without a serial number): refuse instead of inventing serials.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM installed_asset WHERE serial_number IS NULL OR quantity <> 1) THEN
                        RAISE EXCEPTION 'Down refused: installed_asset contains quantity lots without serial numbers; the previous schema needs one serial per device.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_package_image_url_https",
                table: "service_package");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installed_asset_lot_product",
                table: "installed_asset");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installed_asset_quantity",
                table: "installed_asset");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference");

            migrationBuilder.DropColumn(
                name: "image_url",
                table: "service_package");

            migrationBuilder.DropColumn(
                name: "quantity",
                table: "installed_asset");

            migrationBuilder.AlterColumn<string>(
                name: "serial_number",
                table: "installed_asset",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference",
                sql: "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT', 'SERVICE_EVIDENCE')");
        }
    }
}
