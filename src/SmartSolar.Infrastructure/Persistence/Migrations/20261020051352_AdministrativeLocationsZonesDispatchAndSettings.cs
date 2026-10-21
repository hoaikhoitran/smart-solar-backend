using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdministrativeLocationsZonesDispatchAndSettings : Migration
    {
        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM technician_appointment WHERE assignment_source = 'MANAGER_DISPATCH') THEN
                    RAISE EXCEPTION 'Down refused: technician_appointment rows with assignment_source MANAGER_DISPATCH exist; '
                                    'the previous schema cannot represent them. Restore from backup instead.';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_assignment_source",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_site_district_code",
                table: "property_site");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_site_ward_code",
                table: "property_site");

            migrationBuilder.AddColumn<Guid>(
                name: "operational_zone_id",
                table: "technician_appointment",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_technician_appointment_operational_zone_id",
                table: "technician_appointment",
                column: "operational_zone_id");

            migrationBuilder.AddColumn<Guid>(
                name: "operational_zone_id",
                table: "installation_project",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_installation_project_operational_zone_id",
                table: "installation_project",
                column: "operational_zone_id");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "acceptance_reminder_sent_at",
                table: "technician_appointment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "acceptance_window_minutes",
                table: "technician_appointment",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cross_zone_authorization_id",
                table: "technician_appointment",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "settings_version",
                table: "technician_appointment",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "commune_code",
                table: "property_site",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "administrative_catalog_import",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    files_sha256 = table.Column<string>(type: "jsonb", nullable: false),
                    counts = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrative_catalog_import", x => x.id);
                    table.CheckConstraint("ck_administrative_catalog_import_applied", "(status = 'APPLIED') = (applied_at IS NOT NULL)");
                    table.CheckConstraint("ck_administrative_catalog_import_status", "status IN ('VALIDATED', 'APPLIED', 'REJECTED')");
                });

            migrationBuilder.CreateTable(
                name: "administrative_province",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    unit_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    dataset_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrative_province", x => x.code);
                    table.CheckConstraint("ck_administrative_province_code", "length(code) = 2 AND substr(code, 1, 1) BETWEEN '0' AND '9' AND substr(code, 2, 1) BETWEEN '0' AND '9'");
                    table.CheckConstraint("ck_administrative_province_unit_type", "unit_type IN ('PROVINCE', 'MUNICIPALITY')");
                    table.CheckConstraint("ck_administrative_province_validity", "valid_to IS NULL OR valid_to > valid_from");
                });

            migrationBuilder.CreateTable(
                name: "operational_zone",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_serviceable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    responsible_manager_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_zone", x => x.id);
                    table.CheckConstraint("ck_operational_zone_code", "length(code) BETWEEN 2 AND 50 AND code = upper(code) AND code NOT LIKE '% %' AND code NOT LIKE '-%'");
                    table.ForeignKey(
                        name: "fk_operational_zone_created_by",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_operational_zone_responsible_manager",
                        column: x => x.responsible_manager_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_operational_zone_updated_by",
                        column: x => x.updated_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "system_setting",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value_int = table.Column<int>(type: "integer", nullable: false),
                    unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_setting", x => x.key);
                    table.CheckConstraint("ck_system_setting_key", "length(key) > 0");
                    table.CheckConstraint("ck_system_setting_unit", "unit IN ('MINUTES', 'HOURS', 'DAYS')");
                    table.CheckConstraint("ck_system_setting_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_system_setting_user_account_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "system_setting_revision",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    old_value = table.Column<int>(type: "integer", nullable: false),
                    new_value = table.Column<int>(type: "integer", nullable: false),
                    unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_setting_revision", x => x.id);
                    table.CheckConstraint("ck_system_setting_revision_unit", "unit IN ('MINUTES', 'HOURS', 'DAYS')");
                    table.CheckConstraint("ck_system_setting_revision_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_system_setting_revision_user_account_changed_by",
                        column: x => x.changed_by,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "system_settings_version",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_settings_version", x => x.id);
                    table.CheckConstraint("ck_system_settings_version_singleton", "id = 1");
                    table.CheckConstraint("ck_system_settings_version_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_system_settings_version_user_account_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "administrative_commune",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    province_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    search_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    unit_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    dataset_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrative_commune", x => x.code);
                    table.UniqueConstraint("ux_administrative_commune_province_code", x => new { x.province_code, x.code });
                    table.CheckConstraint("ck_administrative_commune_code", "length(code) = 5 AND substr(code, 1, 1) BETWEEN '0' AND '9' AND substr(code, 2, 1) BETWEEN '0' AND '9' AND substr(code, 3, 1) BETWEEN '0' AND '9' AND substr(code, 4, 1) BETWEEN '0' AND '9' AND substr(code, 5, 1) BETWEEN '0' AND '9'");
                    table.CheckConstraint("ck_administrative_commune_unit_type", "unit_type IN ('WARD', 'COMMUNE', 'SPECIAL_ZONE')");
                    table.CheckConstraint("ck_administrative_commune_validity", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_administrative_commune_province",
                        column: x => x.province_code,
                        principalTable: "administrative_province",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dispatch_request",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operational_zone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    customer_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    review_sla_minutes = table.Column<int>(type: "integer", nullable: false),
                    review_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    escalation_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reminder_interval_minutes = table.Column<int>(type: "integer", nullable: false),
                    settings_version = table.Column<int>(type: "integer", nullable: false),
                    overdue_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_reminder_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reminder_count = table.Column<int>(type: "integer", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    resolution_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    released_appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatch_request", x => x.id);
                    table.CheckConstraint("ck_dispatch_request_reason", "reason IN ('NO_ZONE_COVERAGE', 'ZONE_NOT_SERVICEABLE', 'NO_ZONE_TECHNICIAN', 'NO_AVAILABLE_TECHNICIAN', 'SCHEDULE_CONFLICT', 'ASSIGNMENT_RELEASED')");
                    table.CheckConstraint("ck_dispatch_request_reminders", "reminder_interval_minutes >= 0 AND reminder_count >= 0");
                    table.CheckConstraint("ck_dispatch_request_resolution", "resolution IS NULL OR resolution IN ('ASSIGNED', 'UNSERVICEABLE', 'CLOSED_BY_STAFF', 'CANCELLED_BY_CUSTOMER')");
                    table.CheckConstraint("ck_dispatch_request_resolved", "(status IN ('OPEN', 'PROPOSAL_PENDING')) = (resolved_at IS NULL AND resolution IS NULL)");
                    table.CheckConstraint("ck_dispatch_request_sla", "review_sla_minutes > 0 AND review_due_at > created_at AND escalation_due_at >= review_due_at");
                    table.CheckConstraint("ck_dispatch_request_status", "status IN ('OPEN', 'PROPOSAL_PENDING', 'ASSIGNED', 'UNSERVICEABLE', 'CLOSED')");
                    table.CheckConstraint("ck_dispatch_request_terminal", "(status <> 'ASSIGNED' OR (resolution = 'ASSIGNED' AND appointment_id IS NOT NULL)) AND (status <> 'UNSERVICEABLE' OR resolution = 'UNSERVICEABLE') AND (status <> 'CLOSED' OR resolution IN ('CLOSED_BY_STAFF', 'CANCELLED_BY_CUSTOMER'))");
                    table.ForeignKey(
                        name: "FK_dispatch_request_property_site_property_site_id",
                        column: x => x.property_site_id,
                        principalTable: "property_site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dispatch_request_survey_request_survey_request_id",
                        column: x => x.survey_request_id,
                        principalTable: "survey_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dispatch_request_user_account_customer_user_id",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dispatch_request_user_account_resolved_by_user_id",
                        column: x => x.resolved_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dispatch_request_appointment",
                        column: x => x.appointment_id,
                        principalTable: "technician_appointment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dispatch_request_operational_zone",
                        column: x => x.operational_zone_id,
                        principalTable: "operational_zone",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dispatch_request_released_appointment",
                        column: x => x.released_appointment_id,
                        principalTable: "technician_appointment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technician_zone_membership",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_zone_membership", x => x.id);
                    table.CheckConstraint("ck_technician_zone_membership_removed", "removed_at IS NULL OR removed_by_user_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_technician_zone_membership_assigned_by",
                        column: x => x.assigned_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_technician_zone_membership_removed_by",
                        column: x => x.removed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_technician_zone_membership_technician_profile",
                        column: x => x.technician_user_id,
                        principalTable: "technician_profile",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_technician_zone_membership_zone",
                        column: x => x.zone_id,
                        principalTable: "operational_zone",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "administrative_legacy_commune_map",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    commune_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    legacy_commune_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    legacy_commune_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    legacy_commune_search_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    legacy_district_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    legacy_district_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    legacy_district_search_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    legacy_province_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    legacy_province_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    merge_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    match_key = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    dataset_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrative_legacy_commune_map", x => x.id);
                    table.CheckConstraint("ck_administrative_legacy_commune_map_commune_pair", "(legacy_commune_code IS NULL) = (legacy_commune_name IS NULL) AND (legacy_commune_name IS NULL) = (legacy_commune_search_name IS NULL)");
                    table.CheckConstraint("ck_administrative_legacy_commune_map_legacy_commune_code", "legacy_commune_code IS NULL OR (length(legacy_commune_code) = 5 AND substr(legacy_commune_code, 1, 1) BETWEEN '0' AND '9' AND substr(legacy_commune_code, 2, 1) BETWEEN '0' AND '9' AND substr(legacy_commune_code, 3, 1) BETWEEN '0' AND '9' AND substr(legacy_commune_code, 4, 1) BETWEEN '0' AND '9' AND substr(legacy_commune_code, 5, 1) BETWEEN '0' AND '9')");
                    table.CheckConstraint("ck_administrative_legacy_commune_map_legacy_district_code", "length(legacy_district_code) = 3 AND substr(legacy_district_code, 1, 1) BETWEEN '0' AND '9' AND substr(legacy_district_code, 2, 1) BETWEEN '0' AND '9' AND substr(legacy_district_code, 3, 1) BETWEEN '0' AND '9'");
                    table.CheckConstraint("ck_administrative_legacy_commune_map_legacy_province_code", "length(legacy_province_code) = 2 AND substr(legacy_province_code, 1, 1) BETWEEN '0' AND '9' AND substr(legacy_province_code, 2, 1) BETWEEN '0' AND '9'");
                    table.CheckConstraint("ck_administrative_legacy_commune_map_match_key", "match_key = legacy_district_code || ':' || COALESCE(legacy_commune_code, '-')");
                    table.CheckConstraint("ck_administrative_legacy_commune_map_merge_type", "merge_type IN ('WHOLE', 'PARTIAL', 'UNCHANGED')");
                    table.ForeignKey(
                        name: "fk_administrative_legacy_commune_map_commune",
                        column: x => x.commune_code,
                        principalTable: "administrative_commune",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "operational_zone_commune",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commune_code = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    removal_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_zone_commune", x => x.id);
                    table.CheckConstraint("ck_operational_zone_commune_removed", "(removed_at IS NULL) = (removed_by_user_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_operational_zone_commune_assigned_by",
                        column: x => x.assigned_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_operational_zone_commune_commune",
                        column: x => x.commune_code,
                        principalTable: "administrative_commune",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_operational_zone_commune_removed_by",
                        column: x => x.removed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_operational_zone_commune_zone",
                        column: x => x.zone_id,
                        principalTable: "operational_zone",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cross_zone_authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatch_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    installation_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_zone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    authorized_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cross_zone_authorization", x => x.id);
                    table.CheckConstraint("ck_cross_zone_authorization_reason", "length(reason) > 0");
                    table.CheckConstraint("ck_cross_zone_authorization_subject", "(dispatch_request_id IS NULL) <> (installation_project_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_cross_zone_authorization_dispatch_request_dispatch_request_~",
                        column: x => x.dispatch_request_id,
                        principalTable: "dispatch_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cross_zone_authorization_user_account_authorized_by_user_id",
                        column: x => x.authorized_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cross_zone_authorization_user_account_technician_user_id",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cross_zone_authorization_installation_project",
                        column: x => x.installation_project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cross_zone_authorization_target_zone",
                        column: x => x.target_zone_id,
                        principalTable: "operational_zone",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dispatch_preferred_window",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatch_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatch_preferred_window", x => x.id);
                    table.CheckConstraint("ck_dispatch_preferred_window_range", "ends_at > starts_at");
                    table.ForeignKey(
                        name: "fk_dispatch_preferred_window_dispatch_request",
                        column: x => x.dispatch_request_id,
                        principalTable: "dispatch_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dispatch_proposal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatch_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cross_zone = table.Column<bool>(type: "boolean", nullable: false),
                    cross_zone_authorization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    response_window_minutes = table.Column<int>(type: "integer", nullable: false),
                    settings_version = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatch_proposal", x => x.id);
                    table.CheckConstraint("ck_dispatch_proposal_cross_zone", "cross_zone = (cross_zone_authorization_id IS NOT NULL)");
                    table.CheckConstraint("ck_dispatch_proposal_expiry", "expires_at <= starts_at AND expires_at > created_at AND response_window_minutes > 0");
                    table.CheckConstraint("ck_dispatch_proposal_range", "ends_at > starts_at");
                    table.CheckConstraint("ck_dispatch_proposal_responded", "status IN ('PROPOSED', 'EXPIRED') OR responded_at IS NOT NULL");
                    table.CheckConstraint("ck_dispatch_proposal_status", "status IN ('PROPOSED', 'ACCEPTED', 'DECLINED', 'EXPIRED', 'WITHDRAWN', 'FAILED')");
                    table.ForeignKey(
                        name: "FK_dispatch_proposal_dispatch_request_dispatch_request_id",
                        column: x => x.dispatch_request_id,
                        principalTable: "dispatch_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dispatch_proposal_user_account_proposed_by_user_id",
                        column: x => x.proposed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dispatch_proposal_user_account_technician_user_id",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dispatch_proposal_cross_zone_authorization",
                        column: x => x.cross_zone_authorization_id,
                        principalTable: "cross_zone_authorization",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dispatch_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatch_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatch_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_dispatch_event_dispatch_request_dispatch_request_id",
                        column: x => x.dispatch_request_id,
                        principalTable: "dispatch_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_dispatch_event_proposal",
                        column: x => x.proposal_id,
                        principalTable: "dispatch_proposal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dispatch_notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatch_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    delivery_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatch_notification", x => x.id);
                    table.ForeignKey(
                        name: "FK_dispatch_notification_dispatch_event_event_id",
                        column: x => x.event_id,
                        principalTable: "dispatch_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dispatch_notification_dispatch_request_dispatch_request_id",
                        column: x => x.dispatch_request_id,
                        principalTable: "dispatch_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dispatch_notification_user_account_recipient_user_id",
                        column: x => x.recipient_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_technician_appointment_cross_zone_authorization_id",
                table: "technician_appointment",
                column: "cross_zone_authorization_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_assignment_source",
                table: "technician_appointment",
                sql: "assignment_source IS NULL OR assignment_source IN ('SELF_ACCEPTED', 'SALES_ASSIGNED', 'MANAGER_OVERRIDE', 'MANAGER_DISPATCH')");

            migrationBuilder.CreateIndex(
                name: "ix_property_site_province_commune",
                table: "property_site",
                columns: new[] { "province_code", "commune_code" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_site_commune_code",
                table: "property_site",
                sql: "commune_code IS NULL OR (province_code IS NOT NULL AND length(commune_code) = 5)");

            migrationBuilder.CreateIndex(
                name: "ux_administrative_catalog_import_dataset_key",
                table: "administrative_catalog_import",
                column: "dataset_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_administrative_commune_province_name",
                table: "administrative_commune",
                columns: new[] { "province_code", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_administrative_commune_province_search_name",
                table: "administrative_commune",
                columns: new[] { "province_code", "search_name" });

            migrationBuilder.CreateIndex(
                name: "ix_administrative_legacy_commune_map_commune_search_name",
                table: "administrative_legacy_commune_map",
                column: "legacy_commune_search_name");

            migrationBuilder.CreateIndex(
                name: "ix_administrative_legacy_commune_map_district_search_name",
                table: "administrative_legacy_commune_map",
                column: "legacy_district_search_name");

            migrationBuilder.CreateIndex(
                name: "ix_administrative_legacy_commune_map_legacy_commune_code",
                table: "administrative_legacy_commune_map",
                column: "legacy_commune_code");

            migrationBuilder.CreateIndex(
                name: "ix_administrative_legacy_commune_map_legacy_district_code",
                table: "administrative_legacy_commune_map",
                column: "legacy_district_code");

            migrationBuilder.CreateIndex(
                name: "ux_administrative_legacy_commune_map_commune_match_key",
                table: "administrative_legacy_commune_map",
                columns: new[] { "commune_code", "match_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cross_zone_authorization_authorized_by_user_id",
                table: "cross_zone_authorization",
                column: "authorized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_cross_zone_authorization_dispatch_request_id",
                table: "cross_zone_authorization",
                column: "dispatch_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_cross_zone_authorization_installation_project_id",
                table: "cross_zone_authorization",
                column: "installation_project_id");

            migrationBuilder.CreateIndex(
                name: "IX_cross_zone_authorization_target_zone_id",
                table: "cross_zone_authorization",
                column: "target_zone_id");

            migrationBuilder.CreateIndex(
                name: "ix_cross_zone_authorization_technician_authorized_at",
                table: "cross_zone_authorization",
                columns: new[] { "technician_user_id", "authorized_at" });

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_event_proposal_id",
                table: "dispatch_event",
                column: "proposal_id");

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_event_request_created_at",
                table: "dispatch_event",
                columns: new[] { "dispatch_request_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_notification_dispatch_request_id",
                table: "dispatch_notification",
                column: "dispatch_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_notification_event_id",
                table: "dispatch_notification",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_notification_recipient_created_at",
                table: "dispatch_notification",
                columns: new[] { "recipient_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_preferred_window_request_starts_at",
                table: "dispatch_preferred_window",
                columns: new[] { "dispatch_request_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_proposal_cross_zone_authorization_id",
                table: "dispatch_proposal",
                column: "cross_zone_authorization_id");

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_proposal_proposed_by_user_id",
                table: "dispatch_proposal",
                column: "proposed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_proposal_request_created_at",
                table: "dispatch_proposal",
                columns: new[] { "dispatch_request_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_proposal_status_expires_at",
                table: "dispatch_proposal",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_proposal_technician_user_id",
                table: "dispatch_proposal",
                column: "technician_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_dispatch_proposal_accepted",
                table: "dispatch_proposal",
                column: "dispatch_request_id",
                unique: true,
                filter: "status = 'ACCEPTED'");

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_request_appointment_id",
                table: "dispatch_request",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_request_customer_user_id",
                table: "dispatch_request",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_request_operational_zone_id",
                table: "dispatch_request",
                column: "operational_zone_id");

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_request_property_site_id",
                table: "dispatch_request",
                column: "property_site_id");

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_request_released_appointment_id",
                table: "dispatch_request",
                column: "released_appointment_id");

            migrationBuilder.CreateIndex(
                name: "IX_dispatch_request_resolved_by_user_id",
                table: "dispatch_request",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_request_status_escalation_due_at",
                table: "dispatch_request",
                columns: new[] { "status", "escalation_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_request_status_review_due_at",
                table: "dispatch_request",
                columns: new[] { "status", "review_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_request_survey_request_created_at",
                table: "dispatch_request",
                columns: new[] { "survey_request_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_dispatch_request_active_survey_request",
                table: "dispatch_request",
                column: "survey_request_id",
                unique: true,
                filter: "status IN ('OPEN', 'PROPOSAL_PENDING')");

            migrationBuilder.CreateIndex(
                name: "IX_operational_zone_created_by_user_id",
                table: "operational_zone",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_operational_zone_responsible_manager_user_id",
                table: "operational_zone",
                column: "responsible_manager_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_operational_zone_updated_by_user_id",
                table: "operational_zone",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_operational_zone_code",
                table: "operational_zone",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_operational_zone_commune_assigned_by_user_id",
                table: "operational_zone_commune",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_operational_zone_commune_removed_by_user_id",
                table: "operational_zone_commune",
                column: "removed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_operational_zone_commune_zone_removed_at",
                table: "operational_zone_commune",
                columns: new[] { "zone_id", "removed_at" });

            migrationBuilder.CreateIndex(
                name: "ux_operational_zone_commune_active",
                table: "operational_zone_commune",
                column: "commune_code",
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_system_setting_updated_by_user_id",
                table: "system_setting",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_system_setting_revision_changed_by",
                table: "system_setting_revision",
                column: "changed_by");

            migrationBuilder.CreateIndex(
                name: "ix_system_setting_revision_version",
                table: "system_setting_revision",
                column: "version");

            migrationBuilder.CreateIndex(
                name: "ux_system_setting_revision_key_version",
                table: "system_setting_revision",
                columns: new[] { "key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_system_settings_version_updated_by_user_id",
                table: "system_settings_version",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_zone_membership_assigned_by_user_id",
                table: "technician_zone_membership",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_zone_membership_removed_by_user_id",
                table: "technician_zone_membership",
                column: "removed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_technician_zone_membership_zone_id",
                table: "technician_zone_membership",
                column: "zone_id");

            migrationBuilder.CreateIndex(
                name: "ux_technician_zone_membership_active",
                table: "technician_zone_membership",
                columns: new[] { "technician_user_id", "zone_id" },
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_installation_project_operational_zone",
                table: "installation_project",
                column: "operational_zone_id",
                principalTable: "operational_zone",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_property_site_administrative_commune",
                table: "property_site",
                columns: new[] { "province_code", "commune_code" },
                principalTable: "administrative_commune",
                principalColumns: new[] { "province_code", "code" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_technician_appointment_cross_zone_authorization",
                table: "technician_appointment",
                column: "cross_zone_authorization_id",
                principalTable: "cross_zone_authorization",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_technician_appointment_operational_zone",
                table: "technician_appointment",
                column: "operational_zone_id",
                principalTable: "operational_zone",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // New tables follow the Supabase hardening policy: RLS on, no policies (deny for anon/authenticated);
            // the table owner used by the API and RAG is not subject to RLS.
            migrationBuilder.Sql("""
                ALTER TABLE public.administrative_catalog_import ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.administrative_commune ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.administrative_legacy_commune_map ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.administrative_province ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.cross_zone_authorization ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.dispatch_event ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.dispatch_notification ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.dispatch_preferred_window ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.dispatch_proposal ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.dispatch_request ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.operational_zone ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.operational_zone_commune ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.system_setting ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.system_setting_revision ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.system_settings_version ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.technician_zone_membership ENABLE ROW LEVEL SECURITY;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                        REVOKE ALL ON public.administrative_catalog_import, public.administrative_commune, public.administrative_legacy_commune_map, public.administrative_province, public.cross_zone_authorization, public.dispatch_event, public.dispatch_notification, public.dispatch_preferred_window, public.dispatch_proposal, public.dispatch_request, public.operational_zone, public.operational_zone_commune, public.system_setting, public.system_setting_revision, public.system_settings_version, public.technician_zone_membership FROM anon;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                        REVOKE ALL ON public.administrative_catalog_import, public.administrative_commune, public.administrative_legacy_commune_map, public.administrative_province, public.cross_zone_authorization, public.dispatch_event, public.dispatch_notification, public.dispatch_preferred_window, public.dispatch_proposal, public.dispatch_request, public.operational_zone, public.operational_zone_commune, public.system_setting, public.system_setting_revision, public.system_settings_version, public.technician_zone_membership FROM authenticated;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);

            migrationBuilder.DropForeignKey(
                name: "fk_installation_project_operational_zone",
                table: "installation_project");

            migrationBuilder.DropForeignKey(
                name: "fk_property_site_administrative_commune",
                table: "property_site");

            migrationBuilder.DropForeignKey(
                name: "fk_technician_appointment_cross_zone_authorization",
                table: "technician_appointment");

            migrationBuilder.DropForeignKey(
                name: "fk_technician_appointment_operational_zone",
                table: "technician_appointment");

            migrationBuilder.DropTable(
                name: "administrative_catalog_import");

            migrationBuilder.DropTable(
                name: "administrative_legacy_commune_map");

            migrationBuilder.DropTable(
                name: "dispatch_notification");

            migrationBuilder.DropTable(
                name: "dispatch_preferred_window");

            migrationBuilder.DropTable(
                name: "operational_zone_commune");

            migrationBuilder.DropTable(
                name: "system_setting");

            migrationBuilder.DropTable(
                name: "system_setting_revision");

            migrationBuilder.DropTable(
                name: "system_settings_version");

            migrationBuilder.DropTable(
                name: "technician_zone_membership");

            migrationBuilder.DropTable(
                name: "dispatch_event");

            migrationBuilder.DropTable(
                name: "administrative_commune");

            migrationBuilder.DropTable(
                name: "dispatch_proposal");

            migrationBuilder.DropTable(
                name: "administrative_province");

            migrationBuilder.DropTable(
                name: "cross_zone_authorization");

            migrationBuilder.DropTable(
                name: "dispatch_request");

            migrationBuilder.DropTable(
                name: "operational_zone");

            migrationBuilder.DropIndex(
                name: "IX_technician_appointment_cross_zone_authorization_id",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_assignment_source",
                table: "technician_appointment");

            migrationBuilder.DropIndex(
                name: "ix_property_site_province_commune",
                table: "property_site");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_site_commune_code",
                table: "property_site");

            migrationBuilder.DropColumn(
                name: "acceptance_reminder_sent_at",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "acceptance_window_minutes",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "cross_zone_authorization_id",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "settings_version",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "commune_code",
                table: "property_site");

            migrationBuilder.DropIndex(
                name: "ix_technician_appointment_operational_zone_id",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "operational_zone_id",
                table: "technician_appointment");

            migrationBuilder.DropIndex(
                name: "IX_installation_project_operational_zone_id",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "operational_zone_id",
                table: "installation_project");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_assignment_source",
                table: "technician_appointment",
                sql: "assignment_source IS NULL OR assignment_source IN ('SELF_ACCEPTED', 'SALES_ASSIGNED', 'MANAGER_OVERRIDE')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_site_district_code",
                table: "property_site",
                sql: "district_code IS NULL OR (province_code IS NOT NULL AND district_code LIKE (province_code || '.%') AND district_code NOT LIKE '%.%.%')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_site_ward_code",
                table: "property_site",
                sql: "ward_code IS NULL OR (district_code IS NOT NULL AND ward_code LIKE (district_code || '.%') AND ward_code NOT LIKE '%.%.%.%')");

}
    }
}
