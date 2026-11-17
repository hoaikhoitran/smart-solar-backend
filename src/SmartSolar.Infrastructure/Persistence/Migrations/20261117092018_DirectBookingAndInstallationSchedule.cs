using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 2026-11-17: direct on-site survey booking (survey requests without a pre-survey; the site is stored on the request) and
    /// technician-agreed installation schedules. Additive: existing survey requests get <c>source = PRE_SURVEY</c> and their pre-survey's
    /// property as <c>property_site_id</c> (backfilled before NOT NULL; the migration fails, changing nothing, if a request has no
    /// pre-survey property); <c>installation_project.manager_user_id</c> becomes nullable (system-created projects). No row is deleted.
    /// </summary>
    public partial class DirectBookingAndInstallationSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "pre_survey_id",
                table: "survey_request",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "contact_name",
                table: "survey_request",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_phone",
                table: "survey_request",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "property_site_id",
                table: "survey_request",
                type: "uuid",
                nullable: true);

            // Every existing request comes from a submitted pre-survey: copy its property, then require the column.
            migrationBuilder.Sql("""
                UPDATE survey_request s SET property_site_id = p.property_id
                FROM pre_survey p WHERE p.id = s.pre_survey_id AND s.property_site_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "property_site_id",
                table: "survey_request",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "survey_request",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "PRE_SURVEY");

            migrationBuilder.AlterColumn<Guid>(
                name: "manager_user_id",
                table: "installation_project",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "installation_schedule_proposal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    customer_agreement_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    change_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    replaces_proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reschedule_reason_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    reschedule_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reschedule_required_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    customer_change_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    customer_change_request_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_schedule_proposal", x => x.id);
                    table.CheckConstraint("ck_installation_schedule_proposal_cancelled", "(status = 'CANCELLED') = (cancelled_at IS NOT NULL) AND (status <> 'CANCELLED' OR cancellation_reason IS NOT NULL)");
                    table.CheckConstraint("ck_installation_schedule_proposal_confirmed", "status <> 'CONFIRMED' OR (project_id IS NOT NULL AND confirmed_at IS NOT NULL)");
                    table.CheckConstraint("ck_installation_schedule_proposal_interval", "ends_at > starts_at");
                    table.CheckConstraint("ck_installation_schedule_proposal_reschedule", "status <> 'RESCHEDULE_REQUIRED' OR (reschedule_reason_code IS NOT NULL AND reschedule_required_at IS NOT NULL)");
                    table.CheckConstraint("ck_installation_schedule_proposal_reschedule_reason", "reschedule_reason_code IS NULL OR reschedule_reason_code IN ('TECHNICIAN_UNAVAILABLE', 'INVALID_SCHEDULE', 'COVERAGE_CHANGED', 'PROJECT_NOT_ASSIGNABLE')");
                    table.CheckConstraint("ck_installation_schedule_proposal_status", "status IN ('PLANNED', 'CONFIRMED', 'RESCHEDULE_REQUIRED', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_cancelled_by",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_created_by",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_customer",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_project",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_property_site",
                        column: x => x.property_site_id,
                        principalTable: "property_site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_replaces",
                        column: x => x.replaces_proposal_id,
                        principalTable: "installation_schedule_proposal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_survey_request",
                        column: x => x.survey_request_id,
                        principalTable: "survey_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_schedule_proposal_technician",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_survey_request_property_site",
                table: "survey_request",
                column: "property_site_id");

            migrationBuilder.CreateIndex(
                name: "ux_survey_request_one_open_direct_booking",
                table: "survey_request",
                column: "property_site_id",
                unique: true,
                filter: "source = 'DIRECT_BOOKING' AND status IN ('Pending', 'Scheduled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_source",
                table: "survey_request",
                sql: "source IN ('PRE_SURVEY', 'DIRECT_BOOKING')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_source_pre_survey",
                table: "survey_request",
                sql: "(source = 'PRE_SURVEY') = (pre_survey_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_installation_schedule_proposal_cancelled_by_user_id",
                table: "installation_schedule_proposal",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_schedule_proposal_created_by_user_id",
                table: "installation_schedule_proposal",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_schedule_proposal_customer_status",
                table: "installation_schedule_proposal",
                columns: new[] { "customer_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_installation_schedule_proposal_project_id",
                table: "installation_schedule_proposal",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_schedule_proposal_property_site_id",
                table: "installation_schedule_proposal",
                column: "property_site_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_schedule_proposal_replaces_proposal_id",
                table: "installation_schedule_proposal",
                column: "replaces_proposal_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_schedule_proposal_survey_request_id",
                table: "installation_schedule_proposal",
                column: "survey_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_schedule_proposal_technician_status",
                table: "installation_schedule_proposal",
                columns: new[] { "technician_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_installation_schedule_proposal_one_live",
                table: "installation_schedule_proposal",
                column: "contract_id",
                unique: true,
                filter: "status IN ('PLANNED', 'CONFIRMED', 'RESCHEDULE_REQUIRED')");

            migrationBuilder.AddForeignKey(
                name: "fk_survey_request_property_site",
                table: "survey_request",
                column: "property_site_id",
                principalTable: "property_site",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        /// <summary>
        /// Rollback is refused, changing nothing, while data the previous schema cannot hold exists: DIRECT_BOOKING survey requests (no
        /// pre-survey), installation schedules, or projects created by the system (no Manager). Export and resolve them first; rows are never deleted.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM survey_request WHERE pre_survey_id IS NULL OR source <> 'PRE_SURVEY')
                       OR EXISTS (SELECT 1 FROM installation_schedule_proposal)
                       OR EXISTS (SELECT 1 FROM installation_project WHERE manager_user_id IS NULL) THEN
                        RAISE EXCEPTION 'DirectBookingAndInstallationSchedule rollback refused: direct-booking survey requests, installation schedules or system-created projects exist. Export and resolve them first; nothing was changed.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_survey_request_property_site",
                table: "survey_request");

            migrationBuilder.DropTable(
                name: "installation_schedule_proposal");

            migrationBuilder.DropIndex(
                name: "ix_survey_request_property_site",
                table: "survey_request");

            migrationBuilder.DropIndex(
                name: "ux_survey_request_one_open_direct_booking",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_source",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_source_pre_survey",
                table: "survey_request");

            migrationBuilder.DropColumn(
                name: "contact_name",
                table: "survey_request");

            migrationBuilder.DropColumn(
                name: "contact_phone",
                table: "survey_request");

            migrationBuilder.DropColumn(
                name: "property_site_id",
                table: "survey_request");

            migrationBuilder.DropColumn(
                name: "source",
                table: "survey_request");

            migrationBuilder.AlterColumn<Guid>(
                name: "pre_survey_id",
                table: "survey_request",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "manager_user_id",
                table: "installation_project",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
