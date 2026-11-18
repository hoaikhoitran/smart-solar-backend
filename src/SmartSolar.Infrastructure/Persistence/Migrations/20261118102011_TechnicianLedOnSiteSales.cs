using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Technician-led on-site sales (2026-11-18), additive: payment lines may pay an installation quotation BEFORE its contract exists
    /// (<c>installation_quotation_id</c>, item type INSTALLATION_QUOTATION, one settled payment per quotation), and a Manager's internal approval of an
    /// equipment substitution (INTERNALLY_APPROVED, no customer step). Existing rows are unchanged and still valid; an older API that never writes
    /// the new column keeps inserting valid lines. Down refuses (changes nothing) while rows use the new values.
    /// </summary>
    public partial class TechnicianLedOnSiteSales : Migration
    {
        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM payment_attempt_item WHERE installation_quotation_id IS NOT NULL OR item_type = 'INSTALLATION_QUOTATION')
                   OR EXISTS (SELECT 1 FROM installation_equipment_substitution WHERE status = 'INTERNALLY_APPROVED') THEN
                    RAISE EXCEPTION 'installation-quotation payments or internally approved substitutions exist: export them and decide before rolling back (nothing was changed)';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_target",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_type",
                table: "payment_attempt_item");

            migrationBuilder.DropIndex(
                name: "ux_installation_equipment_substitution_active",
                table: "installation_equipment_substitution");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_equipment_substitution_status",
                table: "installation_equipment_substitution");

            migrationBuilder.AddColumn<Guid>(
                name: "installation_quotation_id",
                table: "payment_attempt_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempt_item_installation_quotation",
                table: "payment_attempt_item",
                column: "installation_quotation_id");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_item_attempt_installation_quotation",
                table: "payment_attempt_item",
                columns: new[] { "payment_attempt_id", "installation_quotation_id" },
                unique: true,
                filter: "installation_quotation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_item_settled_installation_quotation",
                table: "payment_attempt_item",
                column: "installation_quotation_id",
                unique: true,
                filter: "installation_quotation_id IS NOT NULL AND status = 'SETTLED'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_installation_quotation",
                table: "payment_attempt_item",
                sql: "(item_type = 'INSTALLATION_QUOTATION') = (installation_quotation_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_target",
                table: "payment_attempt_item",
                sql: "(CASE WHEN contract_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN subscription_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN quotation_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN installation_quotation_id IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_type",
                table: "payment_attempt_item",
                sql: "item_type IN ('CONTRACT', 'MAINTENANCE_PACKAGE', 'DISASTER_PACKAGE', 'REPAIR_QUOTATION', 'MAINTENANCE_QUOTATION', 'INSTALLATION_QUOTATION')");

            migrationBuilder.CreateIndex(
                name: "ux_installation_equipment_substitution_active",
                table: "installation_equipment_substitution",
                column: "project_id",
                unique: true,
                filter: "status IN ('CUSTOMER_APPROVAL_PENDING', 'APPROVED', 'INTERNALLY_APPROVED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_equipment_substitution_status",
                table: "installation_equipment_substitution",
                sql: "status IN ('CUSTOMER_APPROVAL_PENDING', 'APPROVED', 'CUSTOMER_DECLINED', 'SUPERSEDED', 'INTERNALLY_APPROVED')");

            migrationBuilder.AddForeignKey(
                name: "fk_payment_attempt_item_installation_quotation",
                table: "payment_attempt_item",
                column: "installation_quotation_id",
                principalTable: "quotation",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);
            migrationBuilder.DropForeignKey(
                name: "fk_payment_attempt_item_installation_quotation",
                table: "payment_attempt_item");

            migrationBuilder.DropIndex(
                name: "ix_payment_attempt_item_installation_quotation",
                table: "payment_attempt_item");

            migrationBuilder.DropIndex(
                name: "ux_payment_attempt_item_attempt_installation_quotation",
                table: "payment_attempt_item");

            migrationBuilder.DropIndex(
                name: "ux_payment_attempt_item_settled_installation_quotation",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_installation_quotation",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_target",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_type",
                table: "payment_attempt_item");

            migrationBuilder.DropIndex(
                name: "ux_installation_equipment_substitution_active",
                table: "installation_equipment_substitution");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_equipment_substitution_status",
                table: "installation_equipment_substitution");

            migrationBuilder.DropColumn(
                name: "installation_quotation_id",
                table: "payment_attempt_item");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_target",
                table: "payment_attempt_item",
                sql: "(CASE WHEN contract_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN subscription_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN quotation_id IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_type",
                table: "payment_attempt_item",
                sql: "item_type IN ('CONTRACT', 'MAINTENANCE_PACKAGE', 'DISASTER_PACKAGE', 'REPAIR_QUOTATION', 'MAINTENANCE_QUOTATION')");

            migrationBuilder.CreateIndex(
                name: "ux_installation_equipment_substitution_active",
                table: "installation_equipment_substitution",
                column: "project_id",
                unique: true,
                filter: "status IN ('CUSTOMER_APPROVAL_PENDING', 'APPROVED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_equipment_substitution_status",
                table: "installation_equipment_substitution",
                sql: "status IN ('CUSTOMER_APPROVAL_PENDING', 'APPROVED', 'CUSTOMER_DECLINED', 'SUPERSEDED')");
        }
    }
}
