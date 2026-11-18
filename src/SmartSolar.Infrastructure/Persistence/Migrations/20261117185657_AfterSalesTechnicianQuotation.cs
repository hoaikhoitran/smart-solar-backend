using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 2026-11-18: after-sales quotations priced and issued by the assigned technician. Additive: one NOT NULL column with default 'SALES' (every
    /// existing row was issued by Sales; an older API build that does not set it keeps writing valid rows) and its check. Down refuses while
    /// technician-issued quotations exist, so no history is dropped.
    /// </summary>
    public partial class AfterSalesTechnicianQuotation : Migration
    {
        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM service_quotation WHERE issued_by_role = 'TECHNICIAN') THEN
                    RAISE EXCEPTION 'technician-issued quotations exist: export them and decide before rolling back (nothing was changed)';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "issued_by_role",
                table: "service_quotation",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "SALES");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_quotation_issued_by_role",
                table: "service_quotation",
                sql: "issued_by_role IN ('SALES', 'TECHNICIAN')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);
            migrationBuilder.DropCheckConstraint(
                name: "ck_service_quotation_issued_by_role",
                table: "service_quotation");

            migrationBuilder.DropColumn(
                name: "issued_by_role",
                table: "service_quotation");
        }
    }
}
