using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Unified technician cash receipts (2026-11-27): a receipt now names its context — INSTALLMENT (contract + installment, every existing row through
    /// the column default) or SERVICE_QUOTATION (after-sales quotation + service request). Additive: the contract columns become nullable, three
    /// nullable / defaulted columns, two indexes, two foreign keys and one check constraint that every existing row already satisfies. No row changes.
    /// Down is refused once after-sales receipts exist (they cannot be represented without the new columns): restore from backup instead.
    /// </summary>
    public partial class UnifiedCashReceipts : Migration
    {
        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM cash_receipt_submission WHERE kind <> 'INSTALLMENT') THEN
                    RAISE EXCEPTION 'Down refused: after-sales cash receipts exist and need the service_quotation_id / service_request_id columns. Restore from backup instead.';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "contract_installment_id",
                table: "cash_receipt_submission",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "contract_id",
                table: "cash_receipt_submission",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "cash_receipt_submission",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "INSTALLMENT");

            migrationBuilder.AddColumn<Guid>(
                name: "service_quotation_id",
                table: "cash_receipt_submission",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "service_request_id",
                table: "cash_receipt_submission",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_cash_receipt_submission_service_quotation",
                table: "cash_receipt_submission",
                column: "service_quotation_id");

            migrationBuilder.CreateIndex(
                name: "ix_cash_receipt_submission_service_request_status",
                table: "cash_receipt_submission",
                columns: new[] { "service_request_id", "status" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_cash_receipt_submission_context",
                table: "cash_receipt_submission",
                sql: "(kind = 'INSTALLMENT' AND contract_id IS NOT NULL AND contract_installment_id IS NOT NULL AND service_quotation_id IS NULL AND service_request_id IS NULL) OR (kind = 'SERVICE_QUOTATION' AND service_quotation_id IS NOT NULL AND service_request_id IS NOT NULL AND contract_id IS NULL AND contract_installment_id IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_cash_receipt_submission_service_quotation",
                table: "cash_receipt_submission",
                column: "service_quotation_id",
                principalTable: "service_quotation",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_cash_receipt_submission_service_request",
                table: "cash_receipt_submission",
                column: "service_request_id",
                principalTable: "service_request",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);

            migrationBuilder.DropForeignKey(
                name: "fk_cash_receipt_submission_service_quotation",
                table: "cash_receipt_submission");

            migrationBuilder.DropForeignKey(
                name: "fk_cash_receipt_submission_service_request",
                table: "cash_receipt_submission");

            migrationBuilder.DropIndex(
                name: "ix_cash_receipt_submission_service_quotation",
                table: "cash_receipt_submission");

            migrationBuilder.DropIndex(
                name: "ix_cash_receipt_submission_service_request_status",
                table: "cash_receipt_submission");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cash_receipt_submission_context",
                table: "cash_receipt_submission");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "cash_receipt_submission");

            migrationBuilder.DropColumn(
                name: "service_quotation_id",
                table: "cash_receipt_submission");

            migrationBuilder.DropColumn(
                name: "service_request_id",
                table: "cash_receipt_submission");

            migrationBuilder.AlterColumn<Guid>(
                name: "contract_installment_id",
                table: "cash_receipt_submission",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "contract_id",
                table: "cash_receipt_submission",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
