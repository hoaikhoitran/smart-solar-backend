using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractDraftDiscard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_issued_hash",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_published_document",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_status",
                table: "customer_contract");

            migrationBuilder.AddColumn<string>(
                name: "discard_reason",
                table: "customer_contract",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discarded_at",
                table: "customer_contract",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "discarded_by_user_id",
                table: "customer_contract",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_contract_discarded_by_user_id",
                table: "customer_contract",
                column: "discarded_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_discarded",
                table: "customer_contract",
                sql: "(status = 'DISCARDED') = (discarded_at IS NOT NULL AND discarded_by_user_id IS NOT NULL AND discard_reason IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_discarded_never_issued",
                table: "customer_contract",
                sql: "status <> 'DISCARDED' OR (issued_at IS NULL AND signed_at IS NULL AND paid_at IS NULL AND previous_contract_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_issued_hash",
                table: "customer_contract",
                sql: "status IN ('DRAFT', 'DISCARDED') OR (canonical_hash IS NOT NULL AND issued_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_published_document",
                table: "customer_contract",
                sql: "status IN ('DRAFT', 'ISSUED', 'SUPERSEDED', 'DISCARDED') OR (official_document_file_id IS NOT NULL AND official_document_sha256 IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_status",
                table: "customer_contract",
                sql: "status IN ('DRAFT', 'ISSUED', 'AWAITING_SIGNATURE', 'UNDER_REVIEW', 'SIGNATURE_REJECTED', 'SIGNED', 'PAID', 'SUPERSEDED', 'DISCARDED')");

            migrationBuilder.AddForeignKey(
                name: "fk_customer_contract_discarded_by",
                table: "customer_contract",
                column: "discarded_by_user_id",
                principalTable: "user_account",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_customer_contract_discarded_by",
                table: "customer_contract");

            migrationBuilder.DropIndex(
                name: "IX_customer_contract_discarded_by_user_id",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_discarded",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_discarded_never_issued",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_issued_hash",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_published_document",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_status",
                table: "customer_contract");

            migrationBuilder.DropColumn(
                name: "discard_reason",
                table: "customer_contract");

            migrationBuilder.DropColumn(
                name: "discarded_at",
                table: "customer_contract");

            migrationBuilder.DropColumn(
                name: "discarded_by_user_id",
                table: "customer_contract");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_issued_hash",
                table: "customer_contract",
                sql: "status = 'DRAFT' OR (canonical_hash IS NOT NULL AND issued_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_published_document",
                table: "customer_contract",
                sql: "status IN ('DRAFT', 'ISSUED', 'SUPERSEDED') OR (official_document_file_id IS NOT NULL AND official_document_sha256 IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_status",
                table: "customer_contract",
                sql: "status IN ('DRAFT', 'ISSUED', 'AWAITING_SIGNATURE', 'UNDER_REVIEW', 'SIGNATURE_REJECTED', 'SIGNED', 'PAID', 'SUPERSEDED')");
        }
    }
}
