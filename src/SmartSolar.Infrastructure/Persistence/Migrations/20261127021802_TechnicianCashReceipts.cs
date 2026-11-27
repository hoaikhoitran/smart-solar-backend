using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Technician cash receipts (2026-11-27): adds cash_receipt_submission only (review history of the photographed physical receipts of CASH
    /// installment payments). No existing table or row changes; an older API build never reads it, so the migration is backward compatible.
    /// </summary>
    public partial class TechnicianCashReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cash_receipt_submission",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    receipt_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    collected_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_receipt_submission", x => x.id);
                    table.CheckConstraint("ck_cash_receipt_submission_amount", "amount > 0");
                    table.CheckConstraint("ck_cash_receipt_submission_number", "length(trim(receipt_number)) > 0");
                    table.CheckConstraint("ck_cash_receipt_submission_review", "(status = 'SUBMITTED') = (reviewed_at IS NULL) AND (status NOT IN ('ACCEPTED', 'REJECTED') OR reviewed_by_user_id IS NOT NULL) AND (status <> 'REJECTED' OR length(trim(coalesce(review_reason, ''))) > 0)");
                    table.CheckConstraint("ck_cash_receipt_submission_status", "status IN ('SUBMITTED', 'ACCEPTED', 'REJECTED', 'WITHDRAWN')");
                    table.ForeignKey(
                        name: "fk_cash_receipt_submission_attempt",
                        column: x => x.payment_attempt_id,
                        principalTable: "payment_attempt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cash_receipt_submission_collected_by",
                        column: x => x.collected_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cash_receipt_submission_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cash_receipt_submission_evidence_file",
                        column: x => x.evidence_file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cash_receipt_submission_installment",
                        column: x => x.contract_installment_id,
                        principalTable: "contract_installment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cash_receipt_submission_reviewed_by",
                        column: x => x.reviewed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_receipt_submission_collected_by_user_id",
                table: "cash_receipt_submission",
                column: "collected_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_receipt_submission_contract_id",
                table: "cash_receipt_submission",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_receipt_submission_evidence_file_id",
                table: "cash_receipt_submission",
                column: "evidence_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_cash_receipt_submission_installment",
                table: "cash_receipt_submission",
                column: "contract_installment_id");

            migrationBuilder.CreateIndex(
                name: "IX_cash_receipt_submission_reviewed_by_user_id",
                table: "cash_receipt_submission",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cash_receipt_submission_status_submitted_at",
                table: "cash_receipt_submission",
                columns: new[] { "status", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ux_cash_receipt_submission_live_number",
                table: "cash_receipt_submission",
                column: "receipt_number",
                unique: true,
                filter: "status IN ('SUBMITTED', 'ACCEPTED')");

            migrationBuilder.CreateIndex(
                name: "ux_cash_receipt_submission_one_accepted",
                table: "cash_receipt_submission",
                columns: new[] { "payment_attempt_id", "status" },
                unique: true,
                filter: "status = 'ACCEPTED'");

            migrationBuilder.CreateIndex(
                name: "ux_cash_receipt_submission_one_open",
                table: "cash_receipt_submission",
                column: "payment_attempt_id",
                unique: true,
                filter: "status = 'SUBMITTED'");

            // Same as every application table: no access through the Supabase Data API roles (the API connects as the owner).
            migrationBuilder.Sql("ALTER TABLE public.cash_receipt_submission ENABLE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_receipt_submission");
        }
    }
}
