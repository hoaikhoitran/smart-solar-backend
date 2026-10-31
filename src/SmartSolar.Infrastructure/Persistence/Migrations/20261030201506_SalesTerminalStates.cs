using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesTerminalStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_event_type",
                table: "survey_request_event");

            migrationBuilder.DropIndex(
                name: "IX_survey_request_assigned_sale_id",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_status",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_quotation_status",
                table: "quotation");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "survey_request",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "closed_by_user_id",
                table: "survey_request",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "closure_note",
                table: "survey_request",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "closure_reason_code",
                table: "survey_request",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "close_reason",
                table: "quotation",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "quotation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "closed_by_user_id",
                table: "quotation",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_event_type",
                table: "survey_request_event",
                sql: "event_type IN ('SALES_OWNER_CHANGED', 'REQUEST_WITHDRAWN', 'REQUEST_CLOSED')");

            migrationBuilder.CreateIndex(
                name: "ix_survey_request_assigned_sale_status",
                table: "survey_request",
                columns: new[] { "assigned_sale_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_request_closed_by_user_id",
                table: "survey_request",
                column: "closed_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_closed_fields",
                table: "survey_request",
                sql: "status <> 'Closed' OR (closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL AND closure_reason_code IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_closure_reason",
                table: "survey_request",
                sql: "(closed_at IS NULL) = (closure_reason_code IS NULL) AND (closure_reason_code IS NULL OR closure_reason_code IN ('CUSTOMER_WITHDREW', 'NOT_FEASIBLE', 'CUSTOMER_DECLINED', 'NO_RESPONSE', 'OTHER'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_closure_terminal",
                table: "survey_request",
                sql: "closed_at IS NULL OR status IN ('Cancelled', 'Closed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_status",
                table: "survey_request",
                sql: "status IN ('Pending', 'Scheduled', 'Completed', 'Cancelled', 'Closed')");

            migrationBuilder.CreateIndex(
                name: "IX_quotation_closed_by_user_id",
                table: "quotation",
                column: "closed_by_user_id");

            // A DRAFT cancelled with its request was never issued: the issued-fields rule must not apply to it.
            migrationBuilder.DropCheckConstraint(
                name: "ck_quotation_issued_fields",
                table: "quotation");

            migrationBuilder.AddCheckConstraint(
                name: "ck_quotation_issued_fields",
                table: "quotation",
                sql: "status IN ('DRAFT', 'CANCELLED') OR (issued_at IS NOT NULL AND valid_until IS NOT NULL AND customer_snapshot IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_quotation_closed_fields",
                table: "quotation",
                sql: "(status IN ('REJECTED', 'CANCELLED')) = (closed_at IS NOT NULL AND closed_by_user_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_quotation_status",
                table: "quotation",
                sql: "status IN ('DRAFT', 'ISSUED', 'ACCEPTED', 'REVISION_REQUESTED', 'SUPERSEDED', 'REJECTED', 'CANCELLED')");

            migrationBuilder.AddForeignKey(
                name: "FK_quotation_user_account_closed_by_user_id",
                table: "quotation",
                column: "closed_by_user_id",
                principalTable: "user_account",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_survey_request_closed_by",
                table: "survey_request",
                column: "closed_by_user_id",
                principalTable: "user_account",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_quotation_user_account_closed_by_user_id",
                table: "quotation");

            migrationBuilder.DropForeignKey(
                name: "fk_survey_request_closed_by",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_event_type",
                table: "survey_request_event");

            migrationBuilder.DropIndex(
                name: "ix_survey_request_assigned_sale_status",
                table: "survey_request");

            migrationBuilder.DropIndex(
                name: "IX_survey_request_closed_by_user_id",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_closed_fields",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_closure_reason",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_closure_terminal",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_status",
                table: "survey_request");

            migrationBuilder.DropIndex(
                name: "IX_quotation_closed_by_user_id",
                table: "quotation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_quotation_closed_fields",
                table: "quotation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_quotation_issued_fields",
                table: "quotation");

            migrationBuilder.AddCheckConstraint(
                name: "ck_quotation_issued_fields",
                table: "quotation",
                sql: "status = 'DRAFT' OR (issued_at IS NOT NULL AND valid_until IS NOT NULL AND customer_snapshot IS NOT NULL)");

            migrationBuilder.DropCheckConstraint(
                name: "ck_quotation_status",
                table: "quotation");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "survey_request");

            migrationBuilder.DropColumn(
                name: "closed_by_user_id",
                table: "survey_request");

            migrationBuilder.DropColumn(
                name: "closure_note",
                table: "survey_request");

            migrationBuilder.DropColumn(
                name: "closure_reason_code",
                table: "survey_request");

            migrationBuilder.DropColumn(
                name: "close_reason",
                table: "quotation");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "quotation");

            migrationBuilder.DropColumn(
                name: "closed_by_user_id",
                table: "quotation");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_event_type",
                table: "survey_request_event",
                sql: "event_type IN ('SALES_OWNER_CHANGED')");

            migrationBuilder.CreateIndex(
                name: "IX_survey_request_assigned_sale_id",
                table: "survey_request",
                column: "assigned_sale_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_status",
                table: "survey_request",
                sql: "status IN ('Pending', 'Scheduled', 'Completed', 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_quotation_status",
                table: "quotation",
                sql: "status IN ('DRAFT', 'ISSUED', 'ACCEPTED', 'REVISION_REQUESTED', 'SUPERSEDED')");
        }
    }
}
