using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Overtime protection (2026-11-25): technician_schedule_conflict, the durable Manager/Admin dispatch queue and history of commitments collided by
    /// work still in execution past its reserved end. Needed because the collision is time-dependent (it cannot be an index condition: now() is not
    /// immutable) and must be recorded exactly once per (overrunning work, affected commitment) across concurrent worker runs (unique index).
    /// Additive: a new table only; no existing row or constraint changes. Down drops the table (conflict history is lost — back it up first).
    /// </summary>
    public partial class SchedulingOvertimeConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "technician_schedule_conflict",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overrun_kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    overrun_id = table.Column<Guid>(type: "uuid", nullable: false),
                    overrun_reserved_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    affected_kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    affected_id = table.Column<Guid>(type: "uuid", nullable: false),
                    affected_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    affected_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_schedule_conflict", x => x.id);
                    table.CheckConstraint("ck_technician_schedule_conflict_distinct", "overrun_id <> affected_id");
                    table.CheckConstraint("ck_technician_schedule_conflict_kinds", "overrun_kind IN ('SURVEY_APPOINTMENT', 'INSTALLATION_ASSIGNMENT') AND affected_kind IN ('SURVEY_APPOINTMENT', 'INSTALLATION_ASSIGNMENT')");
                    table.CheckConstraint("ck_technician_schedule_conflict_status", "status IN ('OPEN', 'RESOLVED') AND (status = 'OPEN') = (resolved_at IS NULL)");
                    table.ForeignKey(
                        name: "fk_technician_schedule_conflict_technician",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_technician_schedule_conflict_status_technician",
                table: "technician_schedule_conflict",
                columns: new[] { "status", "technician_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_technician_schedule_conflict_technician_user_id",
                table: "technician_schedule_conflict",
                column: "technician_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_technician_schedule_conflict_pair",
                table: "technician_schedule_conflict",
                columns: new[] { "overrun_id", "affected_id" },
                unique: true);

            migrationBuilder.Sql("ALTER TABLE public.technician_schedule_conflict ENABLE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "technician_schedule_conflict");
        }
    }
}
