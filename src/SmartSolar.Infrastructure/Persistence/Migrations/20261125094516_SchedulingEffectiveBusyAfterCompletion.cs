using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Effective busy intervals after early completion (2026-11-25). Adds installation_assignment.work_reported_at (nullable; NULL = the reserved
    /// interval is held, the previous behaviour) and narrows the two technician double-booking exclusions to the EFFECTIVE interval: a COMPLETED survey
    /// appointment and a reported-complete installation assignment hold [starts_at, finish) instead of [starts_at, ends_at) (an empty range when the work
    /// finished before the reserved start). starts_at / ends_at are never changed. Without this the database would refuse bookings the application
    /// correctly allows in the released time. Existing rows satisfy the new (weaker) constraints; an older API build is unaffected (it never sets
    /// work_reported_at and checks the full interval itself).
    /// </summary>
    public partial class SchedulingEffectiveBusyAfterCompletion : Migration
    {
        public const string UpManualSql = """
            ALTER TABLE technician_appointment DROP CONSTRAINT IF EXISTS ex_technician_appointment_no_overlap;
            ALTER TABLE technician_appointment ADD CONSTRAINT ex_technician_appointment_no_overlap
                EXCLUDE USING gist (reserved_technician_user_id WITH =, tstzrange(starts_at,
                    CASE WHEN status = 'COMPLETED' AND completed_at IS NOT NULL AND completed_at < ends_at THEN GREATEST(starts_at, completed_at) ELSE ends_at END,
                    '[)') WITH &&)
                WHERE (status <> 'CANCELLED');
            ALTER TABLE installation_assignment DROP CONSTRAINT IF EXISTS ex_installation_assignment_no_overlap;
            ALTER TABLE installation_assignment ADD CONSTRAINT ex_installation_assignment_no_overlap
                EXCLUDE USING gist (technician_user_id WITH =, tstzrange(starts_at,
                    CASE WHEN work_reported_at IS NOT NULL AND work_reported_at < ends_at THEN GREATEST(starts_at, work_reported_at) ELSE ends_at END,
                    '[)') WITH &&)
                WHERE (status IN ('PENDING', 'ACCEPTED'));
            """;

        /// <summary>Down restores the full-interval exclusions; it is refused (nothing changed) when rows booked in released time would violate them.</summary>
        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM technician_appointment a JOIN technician_appointment b
                               ON a.id < b.id AND a.reserved_technician_user_id = b.reserved_technician_user_id
                           WHERE a.status <> 'CANCELLED' AND b.status <> 'CANCELLED'
                             AND tstzrange(a.starts_at, a.ends_at, '[)') && tstzrange(b.starts_at, b.ends_at, '[)'))
                   OR EXISTS (SELECT 1 FROM installation_assignment a JOIN installation_assignment b
                               ON a.id < b.id AND a.technician_user_id = b.technician_user_id
                           WHERE a.status IN ('PENDING', 'ACCEPTED') AND b.status IN ('PENDING', 'ACCEPTED')
                             AND tstzrange(a.starts_at, a.ends_at, '[)') && tstzrange(b.starts_at, b.ends_at, '[)')) THEN
                    RAISE EXCEPTION 'Down refused: work was booked in time released by an early completion; the full-interval exclusion cannot be restored. Restore from backup instead.';
                END IF;
            END $$;
            """;

        public const string DownManualSql = """
            ALTER TABLE technician_appointment DROP CONSTRAINT IF EXISTS ex_technician_appointment_no_overlap;
            ALTER TABLE technician_appointment ADD CONSTRAINT ex_technician_appointment_no_overlap
                EXCLUDE USING gist (reserved_technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
                WHERE (status <> 'CANCELLED');
            ALTER TABLE installation_assignment DROP CONSTRAINT IF EXISTS ex_installation_assignment_no_overlap;
            ALTER TABLE installation_assignment ADD CONSTRAINT ex_installation_assignment_no_overlap
                EXCLUDE USING gist (technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
                WHERE (status IN ('PENDING', 'ACCEPTED'));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "work_reported_at",
                table: "installation_assignment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(UpManualSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);
            migrationBuilder.Sql(DownManualSql);

            migrationBuilder.DropColumn(
                name: "work_reported_at",
                table: "installation_assignment");
        }
    }
}
