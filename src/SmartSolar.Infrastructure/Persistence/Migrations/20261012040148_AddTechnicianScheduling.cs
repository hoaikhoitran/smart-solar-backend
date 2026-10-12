using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTechnicianScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "technician_appointment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    property_site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reserved_technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_technician_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_by_sale_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    customer_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    booked_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    booked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    replaced_by_appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_appointment", x => x.id);
                    table.CheckConstraint("ck_technician_appointment_acknowledged", "status <> 'ACKNOWLEDGED' OR acknowledged_at IS NOT NULL");
                    table.CheckConstraint("ck_technician_appointment_assignment", "status NOT IN ('ASSIGNED', 'ACKNOWLEDGED') OR (assigned_technician_user_id IS NOT NULL AND assigned_technician_user_id = reserved_technician_user_id AND assigned_at IS NOT NULL)");
                    table.CheckConstraint("ck_technician_appointment_cancelled_at", "(status = 'CANCELLED') = (cancelled_at IS NOT NULL)");
                    table.CheckConstraint("ck_technician_appointment_pre_survey_link", "appointment_type <> 'PRE_SURVEY' OR survey_request_id IS NOT NULL");
                    table.CheckConstraint("ck_technician_appointment_range", "ends_at > starts_at");
                    table.CheckConstraint("ck_technician_appointment_status", "status IN ('BOOKED', 'ASSIGNED', 'ACKNOWLEDGED', 'CANCELLED')");
                    table.CheckConstraint("ck_technician_appointment_type", "appointment_type IN ('PRE_SURVEY')");
                    table.ForeignKey(
                        name: "FK_technician_appointment_property_site_property_site_id",
                        column: x => x.property_site_id,
                        principalTable: "property_site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_appointment_survey_request_survey_request_id",
                        column: x => x.survey_request_id,
                        principalTable: "survey_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_appointment_technician_appointment_replaced_by_a~",
                        column: x => x.replaced_by_appointment_id,
                        principalTable: "technician_appointment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_appointment_user_account_assigned_by_sale_user_id",
                        column: x => x.assigned_by_sale_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_appointment_user_account_assigned_technician_use~",
                        column: x => x.assigned_technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_appointment_user_account_booked_by_user_id",
                        column: x => x.booked_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_appointment_user_account_reserved_technician_use~",
                        column: x => x.reserved_technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technician_profile",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_latitude = table.Column<decimal>(type: "numeric", nullable: true),
                    base_longitude = table.Column<decimal>(type: "numeric", nullable: true),
                    base_label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_scheduling_active = table.Column<bool>(type: "boolean", nullable: false),
                    max_appointments_per_day = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_profile", x => x.user_id);
                    table.CheckConstraint("ck_technician_profile_base_paired", "(base_latitude IS NULL) = (base_longitude IS NULL)");
                    table.CheckConstraint("ck_technician_profile_max_per_day_positive", "max_appointments_per_day IS NULL OR max_appointments_per_day > 0");
                    table.ForeignKey(
                        name: "FK_technician_profile_user_account_user_id",
                        column: x => x.user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technician_unavailable_period",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_unavailable_period", x => x.id);
                    table.CheckConstraint("ck_technician_unavailable_period_range", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "FK_technician_unavailable_period_user_account_technician_user_~",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technician_travel_estimate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    context = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    origin_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    origin_appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin_latitude = table.Column<decimal>(type: "numeric", nullable: false),
                    origin_longitude = table.Column<decimal>(type: "numeric", nullable: false),
                    destination_latitude = table.Column<decimal>(type: "numeric", nullable: false),
                    destination_longitude = table.Column<decimal>(type: "numeric", nullable: false),
                    departure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    estimated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    distance_meters = table.Column<decimal>(type: "numeric", nullable: true),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    safety_buffer_minutes = table.Column<int>(type: "integer", nullable: false),
                    estimated_arrival_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    provider_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    vehicle = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    congestion_summary = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_travel_estimate", x => x.id);
                    table.CheckConstraint("ck_technician_travel_estimate_non_negative", "(distance_meters IS NULL OR distance_meters >= 0) AND (duration_seconds IS NULL OR duration_seconds >= 0) AND safety_buffer_minutes >= 0");
                    table.CheckConstraint("ck_technician_travel_estimate_ok_has_values", "status <> 'OK' OR (distance_meters IS NOT NULL AND duration_seconds IS NOT NULL)");
                    table.CheckConstraint("ck_technician_travel_estimate_origin_kind", "origin_kind IN ('BASE', 'APPOINTMENT')");
                    table.ForeignKey(
                        name: "FK_technician_travel_estimate_technician_appointment_appointme~",
                        column: x => x.appointment_id,
                        principalTable: "technician_appointment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_travel_estimate_technician_appointment_origin_ap~",
                        column: x => x.origin_appointment_id,
                        principalTable: "technician_appointment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_travel_estimate_user_account_technician_user_id",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_technician_appointment_assigned_by_sale_user_id",
                table: "technician_appointment",
                column: "assigned_by_sale_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_technician_appointment_assigned_starts_at",
                table: "technician_appointment",
                columns: new[] { "assigned_technician_user_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "IX_technician_appointment_booked_by_user_id",
                table: "technician_appointment",
                column: "booked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_appointment_property_site_id",
                table: "technician_appointment",
                column: "property_site_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_appointment_replaced_by_appointment_id",
                table: "technician_appointment",
                column: "replaced_by_appointment_id");

            migrationBuilder.CreateIndex(
                name: "ix_technician_appointment_reserved_starts_at",
                table: "technician_appointment",
                columns: new[] { "reserved_technician_user_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ux_technician_appointment_active_survey_request",
                table: "technician_appointment",
                column: "survey_request_id",
                unique: true,
                filter: "status <> 'CANCELLED'");

            migrationBuilder.CreateIndex(
                name: "ix_technician_travel_estimate_appointment_estimated_at",
                table: "technician_travel_estimate",
                columns: new[] { "appointment_id", "estimated_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_technician_travel_estimate_origin_appointment_id",
                table: "technician_travel_estimate",
                column: "origin_appointment_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_travel_estimate_technician_user_id",
                table: "technician_travel_estimate",
                column: "technician_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_technician_unavailable_period_technician_starts_at",
                table: "technician_unavailable_period",
                columns: new[] { "technician_user_id", "starts_at" });

            // Hand-written (EF Core cannot model exclusion constraints): a technician can never hold
            // two active (non-cancelled) appointments whose [starts_at, ends_at) intervals overlap.
            // This is the database-level guard against double booking for every write path
            // (booking, assignment, reassignment, rescheduling). btree_gist provides the "=" operator
            // class for uuid inside a GiST index.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql(
                "ALTER TABLE technician_appointment ADD CONSTRAINT ex_technician_appointment_no_overlap " +
                "EXCLUDE USING gist (reserved_technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&) " +
                "WHERE (status <> 'CANCELLED');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The btree_gist extension is left installed: other objects may depend on it.
            migrationBuilder.Sql("ALTER TABLE technician_appointment DROP CONSTRAINT IF EXISTS ex_technician_appointment_no_overlap;");

            migrationBuilder.DropTable(
                name: "technician_profile");

            migrationBuilder.DropTable(
                name: "technician_travel_estimate");

            migrationBuilder.DropTable(
                name: "technician_unavailable_period");

            migrationBuilder.DropTable(
                name: "technician_appointment");
        }
    }
}
