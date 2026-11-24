using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Per-panel service packages (2026-11-25), additive: catalog pricing unit (existing rows = PER_INSTALLATION_LEGACY, never reinterpreted), quotation
    /// package snapshots, contract package commitments with 30/70 shares, panel coverage allocations, subscription coverage scope (existing rows =
    /// INSTALLATION). An older API build keeps working against the new schema (every new column is nullable or has a default with the old meaning).
    /// The installation-wide overlap exclusion is narrowed to INSTALLATION-scope terms; PANEL-scope units are checked per panel row under the contract lock.
    /// </summary>
    public partial class PerPanelServicePackages : Migration
    {
        public const string UpManualSql = """
            ALTER TABLE service_subscription DROP CONSTRAINT IF EXISTS ex_service_subscription_no_overlap;
            ALTER TABLE service_subscription ADD CONSTRAINT ex_service_subscription_no_overlap
                EXCLUDE USING gist (contract_id WITH =, kind WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
                WHERE (starts_at IS NOT NULL AND status IN ('PAID', 'ACTIVE', 'CANCELLATION_REQUESTED') AND coverage_scope = 'INSTALLATION');
            ALTER TABLE public.quotation_service_package ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.contract_service_package ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.service_coverage_allocation ENABLE ROW LEVEL SECURITY;
            """;

        /// <summary>Down would silently drop money breakdowns and coverage: refused while any per-panel data exists (restore from backup instead).</summary>
        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM quotation_service_package) OR EXISTS (SELECT 1 FROM contract_service_package)
                   OR EXISTS (SELECT 1 FROM service_coverage_allocation) OR EXISTS (SELECT 1 FROM service_subscription WHERE coverage_scope <> 'INSTALLATION')
                   OR EXISTS (SELECT 1 FROM service_package WHERE pricing_unit <> 'PER_INSTALLATION_LEGACY')
                   OR EXISTS (SELECT 1 FROM customer_contract WHERE installation_total IS NOT NULL) THEN
                    RAISE EXCEPTION 'Down refused: per-panel package data exists; the previous schema cannot represent it. Restore from backup instead.';
                END IF;
            END $$;
            """;

        /// <summary>Restores the original installation-wide exclusion (every scope).</summary>
        public const string DownManualSql = """
            ALTER TABLE service_subscription DROP CONSTRAINT IF EXISTS ex_service_subscription_no_overlap;
            ALTER TABLE service_subscription ADD CONSTRAINT ex_service_subscription_no_overlap
                EXCLUDE USING gist (contract_id WITH =, kind WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
                WHERE (starts_at IS NOT NULL AND status IN ('PAID', 'ACTIVE', 'CANCELLATION_REQUESTED'));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "contract_service_package_id",
                table: "service_subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "coverage_scope",
                table: "service_subscription",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "INSTALLATION");

            migrationBuilder.AddColumn<int>(
                name: "covered_units",
                table: "service_subscription",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_price",
                table: "service_subscription",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pricing_unit",
                table: "service_package",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "PER_INSTALLATION_LEGACY");

            migrationBuilder.AddColumn<int>(
                name: "panel_quantity",
                table: "quotation",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "service_packages_total",
                table: "quotation",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "coverage_allocation_id",
                table: "disaster_claim",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "installation_total",
                table: "customer_contract",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "service_packages_total",
                table: "customer_contract",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "quotation_service_package",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    image_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    pricing_unit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    duration_months = table.Column<int>(type: "integer", nullable: false),
                    visit_months = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    covered_disaster_types = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    coverage_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    per_incident_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    term_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    catalog_revision = table.Column<int>(type: "integer", nullable: false),
                    snapshot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_service_package", x => x.id);
                    table.CheckConstraint("ck_quotation_service_package_amount", "unit_price > 0 AND quantity > 0 AND total_amount = unit_price * quantity AND duration_months BETWEEN 1 AND 120");
                    table.CheckConstraint("ck_quotation_service_package_kind", "kind IN ('MAINTENANCE', 'DISASTER_PROTECTION')");
                    table.CheckConstraint("ck_quotation_service_package_pricing_unit", "pricing_unit = 'PER_PANEL'");
                    table.ForeignKey(
                        name: "fk_quotation_service_package_package",
                        column: x => x.package_id,
                        principalTable: "service_package",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quotation_service_package_quotation",
                        column: x => x.quotation_id,
                        principalTable: "quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_coverage_allocation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installed_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    unit_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_coverage_allocation", x => x.id);
                    table.CheckConstraint("ck_service_coverage_allocation_kind", "kind IN ('MAINTENANCE', 'DISASTER_PROTECTION')");
                    table.CheckConstraint("ck_service_coverage_allocation_units", "unit_count > 0");
                    table.ForeignKey(
                        name: "fk_service_coverage_allocation_asset",
                        column: x => x.installed_asset_id,
                        principalTable: "installed_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_coverage_allocation_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_coverage_allocation_subscription",
                        column: x => x.subscription_id,
                        principalTable: "service_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contract_service_package",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_service_package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    image_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    pricing_unit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    duration_months = table.Column<int>(type: "integer", nullable: false),
                    visit_months = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    covered_disaster_types = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    coverage_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    per_incident_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    term_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    catalog_revision = table.Column<int>(type: "integer", nullable: false),
                    snapshot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deposit_allocation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    balance_allocation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_service_package", x => x.id);
                    table.CheckConstraint("ck_contract_service_package_amount", "unit_price > 0 AND quantity > 0 AND total_amount = unit_price * quantity AND deposit_allocation >= 0 AND balance_allocation >= 0 AND deposit_allocation + balance_allocation = total_amount AND duration_months BETWEEN 1 AND 120");
                    table.CheckConstraint("ck_contract_service_package_kind", "kind IN ('MAINTENANCE', 'DISASTER_PROTECTION')");
                    table.CheckConstraint("ck_contract_service_package_provisioned", "(status = 'PROVISIONED') = (subscription_id IS NOT NULL)");
                    table.CheckConstraint("ck_contract_service_package_status", "status IN ('COMMITTED', 'DISCARDED', 'RECONCILIATION_REQUIRED', 'PROVISIONED')");
                    table.ForeignKey(
                        name: "fk_contract_service_package_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_service_package_quotation_package",
                        column: x => x.quotation_service_package_id,
                        principalTable: "quotation_service_package",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_service_subscription_contract_service_package",
                table: "service_subscription",
                column: "contract_service_package_id",
                unique: true,
                filter: "contract_service_package_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_subscription_coverage_scope",
                table: "service_subscription",
                sql: "coverage_scope IN ('INSTALLATION', 'PANEL')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_subscription_panel_units",
                table: "service_subscription",
                sql: "coverage_scope <> 'PANEL' OR (unit_price > 0 AND covered_units > 0 AND price = unit_price * covered_units)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_package_pricing_unit",
                table: "service_package",
                sql: "pricing_unit IN ('PER_PANEL', 'PER_INSTALLATION_LEGACY')");

            migrationBuilder.CreateIndex(
                name: "IX_disaster_claim_coverage_allocation_id",
                table: "disaster_claim",
                column: "coverage_allocation_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_package_breakdown",
                table: "customer_contract",
                sql: "(installation_total IS NULL) = (service_packages_total IS NULL) AND (installation_total IS NULL OR (installation_total > 0 AND service_packages_total > 0 AND installation_total + service_packages_total = total_amount))");

            migrationBuilder.CreateIndex(
                name: "IX_contract_service_package_quotation_service_package_id",
                table: "contract_service_package",
                column: "quotation_service_package_id");

            migrationBuilder.CreateIndex(
                name: "ix_contract_service_package_status",
                table: "contract_service_package",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_contract_service_package_kind",
                table: "contract_service_package",
                columns: new[] { "contract_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotation_service_package_package",
                table: "quotation_service_package",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ux_quotation_service_package_kind",
                table: "quotation_service_package",
                columns: new[] { "quotation_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_coverage_allocation_asset_kind",
                table: "service_coverage_allocation",
                columns: new[] { "installed_asset_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_service_coverage_allocation_contract_kind",
                table: "service_coverage_allocation",
                columns: new[] { "contract_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ux_service_coverage_allocation_subscription_asset",
                table: "service_coverage_allocation",
                columns: new[] { "subscription_id", "installed_asset_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_disaster_claim_coverage_allocation",
                table: "disaster_claim",
                column: "coverage_allocation_id",
                principalTable: "service_coverage_allocation",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_service_subscription_contract_service_package",
                table: "service_subscription",
                column: "contract_service_package_id",
                principalTable: "contract_service_package",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(UpManualSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);
            migrationBuilder.Sql(DownManualSql);

            migrationBuilder.DropForeignKey(
                name: "fk_disaster_claim_coverage_allocation",
                table: "disaster_claim");

            migrationBuilder.DropForeignKey(
                name: "fk_service_subscription_contract_service_package",
                table: "service_subscription");

            migrationBuilder.DropTable(
                name: "contract_service_package");

            migrationBuilder.DropTable(
                name: "service_coverage_allocation");

            migrationBuilder.DropTable(
                name: "quotation_service_package");

            migrationBuilder.DropIndex(
                name: "ux_service_subscription_contract_service_package",
                table: "service_subscription");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_subscription_coverage_scope",
                table: "service_subscription");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_subscription_panel_units",
                table: "service_subscription");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_package_pricing_unit",
                table: "service_package");

            migrationBuilder.DropIndex(
                name: "IX_disaster_claim_coverage_allocation_id",
                table: "disaster_claim");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_package_breakdown",
                table: "customer_contract");

            migrationBuilder.DropColumn(
                name: "contract_service_package_id",
                table: "service_subscription");

            migrationBuilder.DropColumn(
                name: "coverage_scope",
                table: "service_subscription");

            migrationBuilder.DropColumn(
                name: "covered_units",
                table: "service_subscription");

            migrationBuilder.DropColumn(
                name: "unit_price",
                table: "service_subscription");

            migrationBuilder.DropColumn(
                name: "pricing_unit",
                table: "service_package");

            migrationBuilder.DropColumn(
                name: "panel_quantity",
                table: "quotation");

            migrationBuilder.DropColumn(
                name: "service_packages_total",
                table: "quotation");

            migrationBuilder.DropColumn(
                name: "coverage_allocation_id",
                table: "disaster_claim");

            migrationBuilder.DropColumn(
                name: "installation_total",
                table: "customer_contract");

            migrationBuilder.DropColumn(
                name: "service_packages_total",
                table: "customer_contract");
        }
    }
}
