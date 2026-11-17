using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SimplifiedCommercialFlow : Migration
    {
        /// <summary>
        /// 2026-11-17 simplified quotation / contract / payment flow. Additive only: no row is changed or deleted. sales_user_id becomes nullable
        /// (legacy rows keep their owner), origin TECHNICIAN and file purpose PAYMENT_EVIDENCE are allowed, and payment_attempt gets the cash
        /// confirmation columns (null on every existing row, which all are online attempts).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_quotation_origin",
                table: "quotation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference");

            migrationBuilder.AlterColumn<Guid>(
                name: "sales_user_id",
                table: "quotation",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "confirmation_note",
                table: "payment_attempt",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "confirmed_by_user_id",
                table: "payment_attempt",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "evidence_file_id",
                table: "payment_attempt",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "sales_user_id",
                table: "customer_contract",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_quotation_origin",
                table: "quotation",
                sql: "origin IN ('SALES', 'AUTOMATIC', 'TECHNICIAN')");

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_confirmed_by_user_id",
                table: "payment_attempt",
                column: "confirmed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_evidence_file_id",
                table: "payment_attempt",
                column: "evidence_file_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_cash_confirmation",
                table: "payment_attempt",
                sql: "provider <> 'CASH' OR status <> 'PAID' OR (confirmed_by_user_id IS NOT NULL AND provider_reference IS NOT NULL AND evidence_file_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_cash_fields_only_for_cash",
                table: "payment_attempt",
                sql: "provider = 'CASH' OR (confirmed_by_user_id IS NULL AND evidence_file_id IS NULL AND confirmation_note IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference",
                sql: "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT', 'SERVICE_EVIDENCE', 'PRODUCT_IMAGE', 'PACKAGE_IMAGE', 'PAYMENT_EVIDENCE')");

            migrationBuilder.AddForeignKey(
                name: "fk_payment_attempt_confirmed_by",
                table: "payment_attempt",
                column: "confirmed_by_user_id",
                principalTable: "user_account",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_attempt_evidence_file",
                table: "payment_attempt",
                column: "evidence_file_id",
                principalTable: "file_reference",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <summary>
        /// Rollback is refused, changing nothing, while data of the new flow exists that the old schema cannot hold: TECHNICIAN quotations,
        /// CASH payment attempts, PAYMENT_EVIDENCE files, or quotations / contracts without a Sales owner whose survey request has no Sales owner
        /// either. Otherwise the missing sales_user_id values (contracts drafted or created after 2026-11-17 for requests that do have a Sales
        /// owner) are restored from survey_request.assigned_sale_id, which is what the previous version expects, and the columns become NOT NULL
        /// again. See guides/simplified-commercial-flow.md, "Rollback"; this migration never deletes rows.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM quotation WHERE origin = 'TECHNICIAN')
                       OR EXISTS (SELECT 1 FROM quotation q JOIN survey_request s ON s.id = q.survey_request_id
                                  WHERE q.sales_user_id IS NULL AND s.assigned_sale_id IS NULL)
                       OR EXISTS (SELECT 1 FROM customer_contract c JOIN survey_request s ON s.id = c.survey_request_id
                                  WHERE c.sales_user_id IS NULL AND s.assigned_sale_id IS NULL)
                       OR EXISTS (SELECT 1 FROM payment_attempt WHERE provider = 'CASH' OR confirmed_by_user_id IS NOT NULL OR evidence_file_id IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM file_reference WHERE purpose = 'PAYMENT_EVIDENCE') THEN
                        RAISE EXCEPTION 'SimplifiedCommercialFlow rollback refused: technician quotations, CASH payments, PAYMENT_EVIDENCE files or rows without any Sales owner exist. Export and resolve them first; nothing was changed.';
                    END IF;
                END $$;
                UPDATE quotation q SET sales_user_id = s.assigned_sale_id FROM survey_request s
                    WHERE s.id = q.survey_request_id AND q.sales_user_id IS NULL;
                UPDATE customer_contract c SET sales_user_id = s.assigned_sale_id FROM survey_request s
                    WHERE s.id = c.survey_request_id AND c.sales_user_id IS NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_payment_attempt_confirmed_by",
                table: "payment_attempt");

            migrationBuilder.DropForeignKey(
                name: "fk_payment_attempt_evidence_file",
                table: "payment_attempt");

            migrationBuilder.DropCheckConstraint(
                name: "ck_quotation_origin",
                table: "quotation");

            migrationBuilder.DropIndex(
                name: "IX_payment_attempt_confirmed_by_user_id",
                table: "payment_attempt");

            migrationBuilder.DropIndex(
                name: "IX_payment_attempt_evidence_file_id",
                table: "payment_attempt");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_cash_confirmation",
                table: "payment_attempt");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_cash_fields_only_for_cash",
                table: "payment_attempt");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference");

            migrationBuilder.DropColumn(
                name: "confirmation_note",
                table: "payment_attempt");

            migrationBuilder.DropColumn(
                name: "confirmed_by_user_id",
                table: "payment_attempt");

            migrationBuilder.DropColumn(
                name: "evidence_file_id",
                table: "payment_attempt");

            migrationBuilder.AlterColumn<Guid>(
                name: "sales_user_id",
                table: "quotation",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "sales_user_id",
                table: "customer_contract",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_quotation_origin",
                table: "quotation",
                sql: "origin IN ('SALES', 'AUTOMATIC')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference",
                sql: "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT', 'SERVICE_EVIDENCE', 'PRODUCT_IMAGE', 'PACKAGE_IMAGE')");
        }
    }
}
