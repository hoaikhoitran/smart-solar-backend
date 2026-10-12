using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminUserManagementAndSchedulingHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "auth_version",
                table: "user_account",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "superseded_at",
                table: "technician_travel_estimate",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "earliest_base_departure",
                table: "technician_profile",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_custom_working_schedule",
                table: "technician_profile",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "technician_appointment_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_technician_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_technician_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    new_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_appointment_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_technician_appointment_event_technician_appointment_appoint~",
                        column: x => x.appointment_id,
                        principalTable: "technician_appointment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technician_schedule_recalculation",
                columns: table => new
                {
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_schedule_recalculation", x => new { x.technician_user_id, x.local_date });
                    table.CheckConstraint("ck_technician_schedule_recalculation_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_technician_schedule_recalculation_status", "status IN ('PENDING', 'FAILED')");
                    table.ForeignKey(
                        name: "FK_technician_schedule_recalculation_user_account_technician_u~",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technician_working_period",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_working_period", x => x.id);
                    table.CheckConstraint("ck_technician_working_period_day", "day_of_week BETWEEN 0 AND 6");
                    table.CheckConstraint("ck_technician_working_period_range", "end_time > start_time");
                    table.ForeignKey(
                        name: "FK_technician_working_period_technician_profile_technician_use~",
                        column: x => x.technician_user_id,
                        principalTable: "technician_profile",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_account_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_account_audit", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_account_audit_user_account_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_account_audit_user_account_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scheduling_notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    delivery_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduling_notification", x => x.id);
                    table.ForeignKey(
                        name: "FK_scheduling_notification_technician_appointment_appointment_~",
                        column: x => x.appointment_id,
                        principalTable: "technician_appointment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scheduling_notification_technician_appointment_event_event_~",
                        column: x => x.event_id,
                        principalTable: "technician_appointment_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scheduling_notification_user_account_recipient_user_id",
                        column: x => x.recipient_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduling_notification_appointment_id",
                table: "scheduling_notification",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "IX_scheduling_notification_event_id",
                table: "scheduling_notification",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_scheduling_notification_recipient_created_at",
                table: "scheduling_notification",
                columns: new[] { "recipient_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_technician_appointment_event_appointment_created_at",
                table: "technician_appointment_event",
                columns: new[] { "appointment_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_technician_appointment_event_survey_request_created_at",
                table: "technician_appointment_event",
                columns: new[] { "survey_request_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_technician_schedule_recalculation_next_attempt_at",
                table: "technician_schedule_recalculation",
                column: "next_attempt_at");

            migrationBuilder.CreateIndex(
                name: "ux_technician_working_period_day_start",
                table: "technician_working_period",
                columns: new[] { "technician_user_id", "day_of_week", "start_time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_account_audit_actor_user_id",
                table: "user_account_audit",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_account_audit_target_created_at",
                table: "user_account_audit",
                columns: new[] { "target_user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduling_notification");

            migrationBuilder.DropTable(
                name: "technician_schedule_recalculation");

            migrationBuilder.DropTable(
                name: "technician_working_period");

            migrationBuilder.DropTable(
                name: "user_account_audit");

            migrationBuilder.DropTable(
                name: "technician_appointment_event");

            migrationBuilder.DropColumn(
                name: "auth_version",
                table: "user_account");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                table: "technician_travel_estimate");

            migrationBuilder.DropColumn(
                name: "earliest_base_departure",
                table: "technician_profile");

            migrationBuilder.DropColumn(
                name: "has_custom_working_schedule",
                table: "technician_profile");
        }
    }
}
