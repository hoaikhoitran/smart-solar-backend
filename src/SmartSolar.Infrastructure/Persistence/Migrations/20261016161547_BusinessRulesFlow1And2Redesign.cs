using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessRulesFlow1And2Redesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_pre_survey_selected_simulation_same_pre_survey",
                table: "pre_survey");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_profile_base_paired",
                table: "technician_profile");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_acknowledged",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_assignment",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_status",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_status",
                table: "installation_project");

            migrationBuilder.AlterColumn<Guid>(
                name: "reserved_technician_user_id",
                table: "technician_appointment",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "acceptance_deadline_at",
                table: "technician_appointment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "accepted_at",
                table: "technician_appointment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assignment_reason",
                table: "technician_appointment",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assignment_source",
                table: "technician_appointment",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "technician_appointment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "escalated_at",
                table: "technician_appointment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "service_area_id",
                table: "technician_appointment",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "district_code",
                table: "property_site",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "province_code",
                table: "property_site",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ward_code",
                table: "property_site",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "chunk_characters",
                table: "knowledge_ingestion_job",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "chunk_overlap",
                table: "knowledge_ingestion_job",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "config_version",
                table: "knowledge_ingestion_job",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "indexed_chunk_characters",
                table: "knowledge_document_version",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "indexed_chunk_overlap",
                table: "knowledge_document_version",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "indexed_config_version",
                table: "knowledge_document_version",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_ingest_error",
                table: "knowledge_document_version",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_ingest_status",
                table: "knowledge_document_version",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                table: "installation_project",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                table: "installation_project",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "responsible_technician_user_id",
                table: "installation_project",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_ends_at",
                table: "installation_project",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_starts_at",
                table: "installation_project",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "service_area_id",
                table: "installation_project",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "technician_accepted_at",
                table: "installation_project",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "from_status",
                table: "installation_event",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "to_status",
                table: "installation_event",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "installation_assignment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assignment_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    response_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_assignment", x => x.id);
                    table.CheckConstraint("ck_installation_assignment_closed", "(status IN ('PENDING', 'ACCEPTED')) = (closed_at IS NULL)");
                    table.CheckConstraint("ck_installation_assignment_declined", "status <> 'DECLINED' OR response_reason IS NOT NULL");
                    table.CheckConstraint("ck_installation_assignment_range", "ends_at > starts_at");
                    table.CheckConstraint("ck_installation_assignment_responded", "status NOT IN ('ACCEPTED', 'DECLINED') OR responded_at IS NOT NULL");
                    table.CheckConstraint("ck_installation_assignment_status", "status IN ('PENDING', 'ACCEPTED', 'DECLINED', 'REPLACED', 'COMPLETED', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_installation_assignment_assigned_by",
                        column: x => x.assigned_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_assignment_project",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_assignment_technician_profile",
                        column: x => x.technician_user_id,
                        principalTable: "technician_profile",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caption = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_evidence", x => x.id);
                    table.CheckConstraint("ck_installation_evidence_round", "round >= 1");
                    table.ForeignKey(
                        name: "fk_installation_evidence_added_by",
                        column: x => x.added_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_evidence_file_reference",
                        column: x => x.file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_installation_evidence_project",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_image",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    alt_text = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_image", x => x.id);
                    table.CheckConstraint("ck_product_image_sort_order_non_negative", "sort_order >= 0");
                    table.CheckConstraint("ck_product_image_url_https", "url LIKE 'https://%'");
                    table.ForeignKey(
                        name: "fk_product_image_product",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_image_user_account_created_by",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rag_configuration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    generation_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    temperature = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: true),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: false),
                    chunk_characters = table.Column<int>(type: "integer", nullable: false),
                    chunk_overlap = table.Column<int>(type: "integer", nullable: false),
                    top_k = table.Column<int>(type: "integer", nullable: false),
                    min_score = table.Column<decimal>(type: "numeric(4,3)", precision: 4, scale: 3, nullable: false),
                    max_context_tokens = table.Column<int>(type: "integer", nullable: false),
                    max_history_messages = table.Column<int>(type: "integer", nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    based_on_version = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_configuration", x => x.id);
                    table.CheckConstraint("ck_rag_configuration_activated", "(status = 'DRAFT') = (activated_at IS NULL) AND (activated_at IS NULL) = (activated_by_user_id IS NULL)");
                    table.CheckConstraint("ck_rag_configuration_chunking", "chunk_characters BETWEEN 400 AND 8000 AND chunk_overlap >= 0 AND chunk_overlap * 2 <= chunk_characters AND chunk_overlap < chunk_characters");
                    table.CheckConstraint("ck_rag_configuration_max_context_tokens", "max_context_tokens BETWEEN 500 AND 6000");
                    table.CheckConstraint("ck_rag_configuration_max_history_messages", "max_history_messages BETWEEN 0 AND 20");
                    table.CheckConstraint("ck_rag_configuration_max_output_tokens", "max_output_tokens BETWEEN 64 AND 1500");
                    table.CheckConstraint("ck_rag_configuration_min_score", "min_score >= 0 AND min_score <= 1");
                    table.CheckConstraint("ck_rag_configuration_model", "length(generation_model) > 0 AND length(prompt_version) > 0");
                    table.CheckConstraint("ck_rag_configuration_status", "status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED')");
                    table.CheckConstraint("ck_rag_configuration_temperature", "temperature IS NULL OR (temperature >= 0 AND temperature <= 1)");
                    table.CheckConstraint("ck_rag_configuration_top_k", "top_k BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_rag_configuration_version", "version >= 1 AND (based_on_version IS NULL OR based_on_version >= 1)");
                    table.ForeignKey(
                        name: "fk_rag_configuration_user_account_activated_by_user_id",
                        column: x => x.activated_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_rag_configuration_user_account_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "selected_simulation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pre_survey_id = table.Column<Guid>(type: "uuid", nullable: false),
                    simulation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selected_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pre_survey_geometry_version = table.Column<int>(type: "integer", nullable: false),
                    algorithm_version = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    energy_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    mounting_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_sku = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    product_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    panel_count = table.Column<int>(type: "integer", nullable: false),
                    installed_capacity_kwp = table.Column<decimal>(type: "numeric", nullable: false),
                    annual_energy_kwh = table.Column<decimal>(type: "numeric", nullable: true),
                    specific_yield_kwh_per_kwp_year = table.Column<decimal>(type: "numeric", nullable: true),
                    panel_tilt_degree = table.Column<decimal>(type: "numeric", nullable: false),
                    panel_azimuth_degree = table.Column<decimal>(type: "numeric", nullable: false),
                    snapshot_bucket = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    snapshot_object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    snapshot_sha256 = table.Column<string>(type: "char(64)", fixedLength: true, maxLength: 64, nullable: true),
                    snapshot_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_selected_simulation", x => x.id);
                    table.UniqueConstraint("ak_selected_simulation_pre_survey_id_simulation_id", x => new { x.pre_survey_id, x.simulation_id });
                    table.CheckConstraint("ck_selected_simulation_capacity_non_negative", "installed_capacity_kwp >= 0");
                    table.CheckConstraint("ck_selected_simulation_energy_status", "energy_status IN ('SUCCEEDED', 'NOT_APPLICABLE', 'UNAVAILABLE', 'FAILED')");
                    table.CheckConstraint("ck_selected_simulation_geometry_version_non_negative", "pre_survey_geometry_version >= 0");
                    table.CheckConstraint("ck_selected_simulation_mounting_type", "mounting_type IN ('FLUSH', 'RACK')");
                    table.CheckConstraint("ck_selected_simulation_panel_count_non_negative", "panel_count >= 0");
                    table.CheckConstraint("ck_selected_simulation_snapshot_complete", "(snapshot_bucket IS NULL AND snapshot_object_key IS NULL AND snapshot_sha256 IS NULL AND snapshot_size_bytes IS NULL) OR (snapshot_bucket IS NOT NULL AND snapshot_object_key IS NOT NULL AND snapshot_sha256 IS NOT NULL AND snapshot_size_bytes IS NOT NULL AND snapshot_size_bytes > 0)");
                    table.CheckConstraint("ck_selected_simulation_status", "status IN ('COMPLETED', 'PARTIALLY_COMPLETED')");
                    table.ForeignKey(
                        name: "fk_selected_simulation_pre_survey",
                        column: x => x.pre_survey_id,
                        principalTable: "pre_survey",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_selected_simulation_product",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_selected_simulation_user_account",
                        column: x => x.selected_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_area",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    responsible_manager_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_area", x => x.id);
                    table.CheckConstraint("ck_service_area_code_depth", "(level = 'PROVINCE' AND code NOT LIKE '%.%') OR (level = 'DISTRICT' AND code LIKE '%.%' AND code NOT LIKE '%.%.%') OR (level = 'WARD' AND code LIKE '%.%.%' AND code NOT LIKE '%.%.%.%')");
                    table.CheckConstraint("ck_service_area_level", "level IN ('PROVINCE', 'DISTRICT', 'WARD')");
                    table.CheckConstraint("ck_service_area_parent", "(level = 'PROVINCE') = (parent_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_service_area_parent",
                        column: x => x.parent_id,
                        principalTable: "service_area",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_area_responsible_manager",
                        column: x => x.responsible_manager_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "survey_request_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_sale_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_sale_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_request_event", x => x.id);
                    table.CheckConstraint("ck_survey_request_event_type", "event_type IN ('SALES_OWNER_CHANGED')");
                    table.ForeignKey(
                        name: "fk_survey_request_event_actor",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_survey_request_event_new_sale",
                        column: x => x.new_sale_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_survey_request_event_previous_sale",
                        column: x => x.previous_sale_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_survey_request_event_survey_request",
                        column: x => x.survey_request_id,
                        principalTable: "survey_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technician_service_area",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_area_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_service_area", x => x.id);
                    table.CheckConstraint("ck_technician_service_area_removed", "removed_at IS NULL OR removed_by_user_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_technician_service_area_assigned_by",
                        column: x => x.assigned_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_technician_service_area_removed_by",
                        column: x => x.removed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_technician_service_area_service_area",
                        column: x => x.service_area_id,
                        principalTable: "service_area",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_technician_service_area_technician_profile",
                        column: x => x.technician_user_id,
                        principalTable: "technician_profile",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_technician_appointment_service_area_id",
                table: "technician_appointment",
                column: "service_area_id");

            migrationBuilder.CreateIndex(
                name: "ix_technician_appointment_status_deadline",
                table: "technician_appointment",
                columns: new[] { "status", "acceptance_deadline_at" });

            migrationBuilder.Sql(UpDataSql);

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_assignment",
                table: "technician_appointment",
                sql: "status NOT IN ('ACCEPTED', 'ASSIGNED', 'COMPLETED') OR (assigned_technician_user_id IS NOT NULL AND assigned_technician_user_id = reserved_technician_user_id AND assigned_at IS NOT NULL AND assignment_source IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_assignment_reason",
                table: "technician_appointment",
                sql: "status <> 'ASSIGNED' OR assignment_reason IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_assignment_source",
                table: "technician_appointment",
                sql: "assignment_source IS NULL OR assignment_source IN ('SELF_ACCEPTED', 'SALES_ASSIGNED', 'MANAGER_OVERRIDE')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_completed_at",
                table: "technician_appointment",
                sql: "status <> 'COMPLETED' OR completed_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_escalated_unassigned",
                table: "technician_appointment",
                sql: "status <> 'ESCALATED' OR assigned_technician_user_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_pending_holder",
                table: "technician_appointment",
                sql: "status <> 'PENDING_ACCEPTANCE' OR (reserved_technician_user_id IS NOT NULL AND assigned_technician_user_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_status",
                table: "technician_appointment",
                sql: "status IN ('PENDING_ACCEPTANCE', 'ESCALATED', 'ACCEPTED', 'ASSIGNED', 'COMPLETED', 'CANCELLED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_survey_request_status",
                table: "survey_request",
                sql: "status IN ('Pending', 'Scheduled', 'Completed', 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_site_district_code",
                table: "property_site",
                sql: "district_code IS NULL OR (province_code IS NOT NULL AND district_code LIKE (province_code || '.%') AND district_code NOT LIKE '%.%.%')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_site_province_code",
                table: "property_site",
                sql: "province_code IS NULL OR (province_code NOT LIKE '%.%' AND length(province_code) BETWEEN 1 AND 3)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_site_ward_code",
                table: "property_site",
                sql: "ward_code IS NULL OR (district_code IS NOT NULL AND ward_code LIKE (district_code || '.%') AND ward_code NOT LIKE '%.%.%.%')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_knowledge_ingestion_job_chunking",
                table: "knowledge_ingestion_job",
                sql: "(chunk_characters IS NULL) = (chunk_overlap IS NULL) AND (chunk_characters IS NULL OR (chunk_characters > 0 AND chunk_overlap >= 0 AND chunk_overlap < chunk_characters))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_knowledge_ingestion_job_config_version",
                table: "knowledge_ingestion_job",
                sql: "config_version IS NULL OR config_version >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_knowledge_document_version_indexed_chunking",
                table: "knowledge_document_version",
                sql: "(indexed_chunk_characters IS NULL) = (indexed_chunk_overlap IS NULL) AND (indexed_chunk_characters IS NULL OR (indexed_chunk_characters > 0 AND indexed_chunk_overlap >= 0 AND indexed_chunk_overlap < indexed_chunk_characters))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_knowledge_document_version_indexed_config",
                table: "knowledge_document_version",
                sql: "indexed_config_version IS NULL OR indexed_config_version >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_installation_project_responsible_technician",
                table: "installation_project",
                column: "responsible_technician_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_project_service_area_id",
                table: "installation_project",
                column: "service_area_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_accepted",
                table: "installation_project",
                sql: "NOT status IN ('SCHEDULED', 'PENDING_CUSTOMER_ACCEPTANCE') OR technician_accepted_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_assigned",
                table: "installation_project",
                sql: "NOT status IN ('PENDING_TECHNICIAN_ACCEPTANCE', 'SCHEDULED', 'PENDING_CUSTOMER_ACCEPTANCE') OR (responsible_technician_user_id IS NOT NULL AND scheduled_starts_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_cancelled",
                table: "installation_project",
                sql: "(status = 'CANCELLED') = (cancelled_at IS NOT NULL) AND (status <> 'CANCELLED' OR cancellation_reason IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_round",
                table: "installation_project",
                sql: "acceptance_round >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_schedule",
                table: "installation_project",
                sql: "(scheduled_starts_at IS NULL) = (scheduled_ends_at IS NULL) AND (scheduled_ends_at IS NULL OR scheduled_ends_at > scheduled_starts_at)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_started",
                table: "installation_project",
                sql: "NOT status IN ('IN_PROGRESS', 'PENDING_CUSTOMER_ACCEPTANCE', 'REWORK_REQUIRED', 'COMPLETED') OR started_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_status",
                table: "installation_project",
                sql: "status IN ('PENDING_ASSIGNMENT', 'PENDING_TECHNICIAN_ACCEPTANCE', 'SCHEDULED', 'IN_PROGRESS', 'PENDING_CUSTOMER_ACCEPTANCE', 'REWORK_REQUIRED', 'COMPLETED', 'CANCELLED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_unassigned",
                table: "installation_project",
                sql: "status <> 'PENDING_ASSIGNMENT' OR (responsible_technician_user_id IS NULL AND technician_accepted_at IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_installation_assignment_assigned_by_user_id",
                table: "installation_assignment",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_assignment_project_assigned_at",
                table: "installation_assignment",
                columns: new[] { "project_id", "assigned_at" });

            migrationBuilder.CreateIndex(
                name: "ix_installation_assignment_technician_starts_at",
                table: "installation_assignment",
                columns: new[] { "technician_user_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ux_installation_assignment_current",
                table: "installation_assignment",
                column: "project_id",
                unique: true,
                filter: "status IN ('PENDING', 'ACCEPTED')");

            migrationBuilder.CreateIndex(
                name: "IX_installation_evidence_added_by_user_id",
                table: "installation_evidence",
                column: "added_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_evidence_project_round",
                table: "installation_evidence",
                columns: new[] { "project_id", "round" });

            migrationBuilder.CreateIndex(
                name: "ux_installation_evidence_file",
                table: "installation_evidence",
                column: "file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_image_created_by_user_id",
                table: "product_image",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_product_image_one_primary",
                table: "product_image",
                column: "product_id",
                unique: true,
                filter: "\"is_primary\"");

            migrationBuilder.CreateIndex(
                name: "ux_product_image_product_sort",
                table: "product_image",
                columns: new[] { "product_id", "sort_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_image_product_url",
                table: "product_image",
                columns: new[] { "product_id", "url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rag_configuration_activated_by_user_id",
                table: "rag_configuration",
                column: "activated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_rag_configuration_created_by_user_id",
                table: "rag_configuration",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_rag_configuration_one_active",
                table: "rag_configuration",
                column: "status",
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "ux_rag_configuration_one_draft",
                table: "rag_configuration",
                column: "status",
                unique: true,
                filter: "status = 'DRAFT'");

            migrationBuilder.CreateIndex(
                name: "ux_rag_configuration_version",
                table: "rag_configuration",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_selected_simulation_product",
                table: "selected_simulation",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_selected_simulation_selected_by",
                table: "selected_simulation",
                column: "selected_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_selected_simulation_pre_survey",
                table: "selected_simulation",
                column: "pre_survey_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_selected_simulation_simulation",
                table: "selected_simulation",
                column: "simulation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_area_parent_id",
                table: "service_area",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_area_responsible_manager_user_id",
                table: "service_area",
                column: "responsible_manager_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_service_area_code",
                table: "service_area",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_request_event_actor_user_id",
                table: "survey_request_event",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_survey_request_event_new_sale_user_id",
                table: "survey_request_event",
                column: "new_sale_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_survey_request_event_previous_sale_user_id",
                table: "survey_request_event",
                column: "previous_sale_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_survey_request_event_survey_request_created_at",
                table: "survey_request_event",
                columns: new[] { "survey_request_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_technician_service_area_assigned_by_user_id",
                table: "technician_service_area",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_technician_service_area_removed_by_user_id",
                table: "technician_service_area",
                column: "removed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_technician_service_area_service_area_id",
                table: "technician_service_area",
                column: "service_area_id");

            migrationBuilder.CreateIndex(
                name: "ux_technician_service_area_active",
                table: "technician_service_area",
                columns: new[] { "technician_user_id", "service_area_id" },
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_installation_project_responsible_technician",
                table: "installation_project",
                column: "responsible_technician_user_id",
                principalTable: "user_account",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_installation_project_service_area",
                table: "installation_project",
                column: "service_area_id",
                principalTable: "service_area",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_pre_survey_selected_simulation",
                table: "pre_survey",
                columns: new[] { "id", "selected_simulation_id" },
                principalTable: "selected_simulation",
                principalColumns: new[] { "pre_survey_id", "simulation_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_technician_appointment_service_area",
                table: "technician_appointment",
                column: "service_area_id",
                principalTable: "service_area",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(UpManualSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);

            migrationBuilder.DropForeignKey(
                name: "fk_installation_project_responsible_technician",
                table: "installation_project");

            migrationBuilder.DropForeignKey(
                name: "fk_installation_project_service_area",
                table: "installation_project");

            migrationBuilder.DropForeignKey(
                name: "fk_pre_survey_selected_simulation",
                table: "pre_survey");

            migrationBuilder.DropForeignKey(
                name: "fk_technician_appointment_service_area",
                table: "technician_appointment");

            migrationBuilder.DropTable(
                name: "installation_assignment");

            migrationBuilder.DropTable(
                name: "installation_evidence");

            migrationBuilder.DropTable(
                name: "product_image");

            migrationBuilder.DropTable(
                name: "rag_configuration");

            migrationBuilder.DropTable(
                name: "selected_simulation");

            migrationBuilder.DropTable(
                name: "survey_request_event");

            migrationBuilder.DropTable(
                name: "technician_service_area");

            migrationBuilder.DropTable(
                name: "service_area");

            migrationBuilder.DropIndex(
                name: "ix_technician_appointment_service_area_id",
                table: "technician_appointment");

            migrationBuilder.DropIndex(
                name: "ix_technician_appointment_status_deadline",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_assignment",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_assignment_reason",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_assignment_source",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_completed_at",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_escalated_unassigned",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_pending_holder",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technician_appointment_status",
                table: "technician_appointment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_survey_request_status",
                table: "survey_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_site_district_code",
                table: "property_site");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_site_province_code",
                table: "property_site");

            migrationBuilder.DropCheckConstraint(
                name: "ck_property_site_ward_code",
                table: "property_site");

            migrationBuilder.DropCheckConstraint(
                name: "ck_knowledge_ingestion_job_chunking",
                table: "knowledge_ingestion_job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_knowledge_ingestion_job_config_version",
                table: "knowledge_ingestion_job");

            migrationBuilder.DropCheckConstraint(
                name: "ck_knowledge_document_version_indexed_chunking",
                table: "knowledge_document_version");

            migrationBuilder.DropCheckConstraint(
                name: "ck_knowledge_document_version_indexed_config",
                table: "knowledge_document_version");

            migrationBuilder.DropIndex(
                name: "ix_installation_project_responsible_technician",
                table: "installation_project");

            migrationBuilder.DropIndex(
                name: "IX_installation_project_service_area_id",
                table: "installation_project");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_accepted",
                table: "installation_project");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_assigned",
                table: "installation_project");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_cancelled",
                table: "installation_project");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_round",
                table: "installation_project");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_schedule",
                table: "installation_project");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_started",
                table: "installation_project");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_status",
                table: "installation_project");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installation_project_unassigned",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "acceptance_deadline_at",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "accepted_at",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "assignment_reason",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "assignment_source",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "escalated_at",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "service_area_id",
                table: "technician_appointment");

            migrationBuilder.DropColumn(
                name: "district_code",
                table: "property_site");

            migrationBuilder.DropColumn(
                name: "province_code",
                table: "property_site");

            migrationBuilder.DropColumn(
                name: "ward_code",
                table: "property_site");

            migrationBuilder.DropColumn(
                name: "chunk_characters",
                table: "knowledge_ingestion_job");

            migrationBuilder.DropColumn(
                name: "chunk_overlap",
                table: "knowledge_ingestion_job");

            migrationBuilder.DropColumn(
                name: "config_version",
                table: "knowledge_ingestion_job");

            migrationBuilder.DropColumn(
                name: "indexed_chunk_characters",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "indexed_chunk_overlap",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "indexed_config_version",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "last_ingest_error",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "last_ingest_status",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "responsible_technician_user_id",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "scheduled_ends_at",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "scheduled_starts_at",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "service_area_id",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "technician_accepted_at",
                table: "installation_project");

            migrationBuilder.DropColumn(
                name: "from_status",
                table: "installation_event");

            migrationBuilder.DropColumn(
                name: "to_status",
                table: "installation_event");

            migrationBuilder.Sql(DownDataSql);

            migrationBuilder.AlterColumn<Guid>(
                name: "reserved_technician_user_id",
                table: "technician_appointment",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_profile_base_paired",
                table: "technician_profile",
                sql: "(base_latitude IS NULL) = (base_longitude IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_acknowledged",
                table: "technician_appointment",
                sql: "status <> 'ACKNOWLEDGED' OR acknowledged_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_assignment",
                table: "technician_appointment",
                sql: "status NOT IN ('ASSIGNED', 'ACKNOWLEDGED') OR (assigned_technician_user_id IS NOT NULL AND assigned_technician_user_id = reserved_technician_user_id AND assigned_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_technician_appointment_status",
                table: "technician_appointment",
                sql: "status IN ('BOOKED', 'ASSIGNED', 'ACKNOWLEDGED', 'CANCELLED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installation_project_status",
                table: "installation_project",
                sql: "status IN ('PLANNING', 'SCHEDULED', 'IN_PROGRESS', 'PENDING_CUSTOMER_ACCEPTANCE', 'REWORK_REQUIRED', 'COMPLETED')");

            migrationBuilder.AddForeignKey(
                name: "fk_pre_survey_selected_simulation_same_pre_survey",
                table: "pre_survey",
                columns: new[] { "id", "selected_simulation_id" },
                principalTable: "solar_simulation",
                principalColumns: new[] { "pre_survey_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        // ------------------------------------------------------------------------------------------------------------
        // Reviewed manual SQL. This migration is NON-DESTRUCTIVE: obsolete tables and columns (Vietmap travel estimates,
        // schedule recalculation, full solar_simulation snapshots, installation teams/shifts/milestones, technician base
        // location, product.image_url) stay in the database untouched. Dropping them is a separate, approval-gated cleanup.
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>Runs after the new columns/tables exist and before any new check constraint is added.</summary>
        public const string UpDataSql = """
            -- Installation Flow: the redesign has no teams/shifts. Refuse to guess a responsible technician for existing work.
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM installation_project WHERE status <> 'PLANNING')
                   OR EXISTS (SELECT 1 FROM installation_shift_assignment WHERE status = 'ACTIVE')
                   OR EXISTS (SELECT 1 FROM installation_team_member WHERE removed_at IS NULL) THEN
                    RAISE EXCEPTION 'installation_* tables contain team/shift data; the responsible-technician redesign needs a manual data decision';
                END IF;
            END $$;
            UPDATE installation_project SET status = 'PENDING_ASSIGNMENT' WHERE status = 'PLANNING';

            -- Flow 1: BOOKED -> PENDING_ACCEPTANCE (the reserved technician becomes the capacity holder; never an acceptance).
            UPDATE technician_appointment
            SET status = 'PENDING_ACCEPTANCE',
                acceptance_deadline_at = LEAST(booked_at + interval '240 minutes', starts_at - interval '120 minutes'),
                assigned_technician_user_id = NULL
            WHERE status = 'BOOKED';

            -- Flow 1: ASSIGNED / ACKNOWLEDGED (both were Sales assignments) -> ASSIGNED; acknowledged_at is kept as history.
            UPDATE technician_appointment
            SET status = 'ASSIGNED',
                assignment_source = 'SALES_ASSIGNED',
                assignment_reason = 'LEGACY_SALES_ASSIGNMENT',
                assigned_at = COALESCE(assigned_at, updated_at),
                acceptance_deadline_at = COALESCE(acceptance_deadline_at, LEAST(booked_at + interval '240 minutes', starts_at - interval '120 minutes'))
            WHERE status IN ('ASSIGNED', 'ACKNOWLEDGED');

            -- Flow 1: appointments whose on-site survey is completed -> COMPLETED.
            UPDATE technician_appointment a
            SET status = 'COMPLETED',
                completed_at = COALESCE(r.completed_at, a.updated_at)
            FROM onsite_survey_report r
            WHERE r.appointment_id = a.id
              AND r.status = 'COMPLETED'
              AND a.status = 'ASSIGNED';

            -- Flow 1: claiming no longer changes the request status (commercial owner kept in assigned_sale_id).
            UPDATE survey_request SET status = 'Pending' WHERE status IN ('Assigned', 'Reviewing');
            -- A pending request whose appointment is confirmed (legacy Assigned + ASSIGNED appointment) is Scheduled.
            UPDATE survey_request s
            SET status = 'Scheduled',
                scheduled_at = COALESCE(s.scheduled_at, (SELECT a.starts_at FROM technician_appointment a
                                                          WHERE a.survey_request_id = s.id AND a.status IN ('ACCEPTED', 'ASSIGNED')
                                                            AND a.replaced_by_appointment_id IS NULL
                                                          ORDER BY a.starts_at DESC LIMIT 1))
            WHERE s.status = 'Pending'
              AND EXISTS (SELECT 1 FROM technician_appointment a
                          WHERE a.survey_request_id = s.id AND a.status IN ('ACCEPTED', 'ASSIGNED')
                            AND a.replaced_by_appointment_id IS NULL);
            UPDATE survey_request s
            SET status = 'Pending', scheduled_at = NULL
            WHERE s.status = 'Scheduled'
              AND NOT EXISTS (SELECT 1 FROM technician_appointment a
                              WHERE a.survey_request_id = s.id AND a.status IN ('ACCEPTED', 'ASSIGNED', 'COMPLETED'));

            -- Solar simulation: keep a compact summary of every selected simulation (full snapshots stay in solar_simulation).
            INSERT INTO selected_simulation (
                id, pre_survey_id, simulation_id, selected_by_user_id, selected_at,
                pre_survey_geometry_version, algorithm_version, status, energy_status, mounting_type,
                product_id, product_sku, product_name, panel_count, installed_capacity_kwp,
                annual_energy_kwh, specific_yield_kwh_per_kwp_year, panel_tilt_degree, panel_azimuth_degree,
                snapshot_bucket, snapshot_object_key, snapshot_sha256, snapshot_size_bytes, created_at)
            SELECT
                gen_random_uuid(), s.pre_survey_id, s.id, s.created_by_user_id, GREATEST(p.updated_at, s.created_at),
                s.pre_survey_geometry_version, s.algorithm_version, s.status, s.energy_status, s.mounting_type,
                s.product_id, s.product_sku, s.product_name, s.panel_count, s.installed_capacity_kwp,
                s.annual_energy_kwh, s.specific_yield_kwh_per_kwp_year, s.panel_tilt_degree, s.panel_azimuth_degree,
                NULL, NULL, NULL, NULL, s.created_at
            FROM pre_survey p
            JOIN solar_simulation s ON s.pre_survey_id = p.id AND s.id = p.selected_simulation_id
            WHERE p.selected_simulation_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM selected_simulation x WHERE x.pre_survey_id = p.id);

            -- Product catalog: the legacy single image becomes the primary gallery image (https URLs only; others stay in image_url).
            DO $$
            DECLARE skipped integer;
            BEGIN
                SELECT count(*) INTO skipped FROM product p
                WHERE NULLIF(btrim(p.image_url), '') IS NOT NULL
                  AND NOT (btrim(p.image_url) LIKE 'https://%' AND length(btrim(p.image_url)) <= 1000);
                IF skipped > 0 THEN
                    RAISE NOTICE 'product image backfill: % product(s) have a non-https image_url; left in product.image_url only.', skipped;
                END IF;
            END $$;
            INSERT INTO product_image (id, product_id, url, alt_text, sort_order, is_primary, created_at, created_by_user_id)
            SELECT gen_random_uuid(), p.id, btrim(p.image_url), NULL, 0, TRUE, p.created_at, NULL
            FROM product p
            WHERE NULLIF(btrim(p.image_url), '') IS NOT NULL
              AND btrim(p.image_url) LIKE 'https://%'
              AND length(btrim(p.image_url)) <= 1000
              AND NOT EXISTS (SELECT 1 FROM product_image pi WHERE pi.product_id = p.id);
            """;

        /// <summary>Database backstop: one technician never holds two current installation assignments that overlap.</summary>
        public const string UpManualSql = """
            CREATE EXTENSION IF NOT EXISTS btree_gist;
            ALTER TABLE installation_assignment
                ADD CONSTRAINT ex_installation_assignment_no_overlap
                EXCLUDE USING gist (technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
                WHERE (status IN ('PENDING', 'ACCEPTED'));
            """;

        /// <summary>Down refuses data the previous schema cannot represent instead of silently losing it.</summary>
        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM technician_appointment WHERE reserved_technician_user_id IS NULL) THEN
                    RAISE EXCEPTION 'Down refused: appointments without a capacity holder cannot be represented by the previous schema';
                END IF;
                IF EXISTS (SELECT 1 FROM installation_assignment)
                   OR EXISTS (SELECT 1 FROM installation_project WHERE status <> 'PENDING_ASSIGNMENT') THEN
                    RAISE EXCEPTION 'Down refused: installation projects use the responsible-technician model';
                END IF;
                IF EXISTS (SELECT 1 FROM pre_survey p
                           WHERE p.selected_simulation_id IS NOT NULL
                             AND NOT EXISTS (SELECT 1 FROM solar_simulation s WHERE s.id = p.selected_simulation_id AND s.pre_survey_id = p.id)) THEN
                    RAISE EXCEPTION 'Down refused: selections made after this migration have no solar_simulation row';
                END IF;
            END $$;
            ALTER TABLE installation_assignment DROP CONSTRAINT IF EXISTS ex_installation_assignment_no_overlap;
            -- Keep the legacy single image in step with the gallery's primary image.
            UPDATE product p SET image_url = i.url FROM product_image i WHERE i.product_id = p.id AND i.is_primary;
            """;

        /// <summary>Runs after the new check constraints are dropped and before the previous ones are restored.</summary>
        public const string DownDataSql = """
            UPDATE technician_appointment SET status = 'BOOKED', assigned_technician_user_id = NULL
            WHERE status IN ('PENDING_ACCEPTANCE', 'ESCALATED');
            UPDATE technician_appointment SET status = 'ASSIGNED' WHERE status IN ('ACCEPTED', 'COMPLETED');
            -- Acknowledged Sales assignments and claimed-but-unscheduled requests are restored exactly from kept data.
            UPDATE technician_appointment SET status = 'ACKNOWLEDGED' WHERE status = 'ASSIGNED' AND acknowledged_at IS NOT NULL;
            UPDATE survey_request SET status = 'Assigned' WHERE status = 'Pending' AND assigned_sale_id IS NOT NULL;
            UPDATE installation_project SET status = 'PLANNING' WHERE status = 'PENDING_ASSIGNMENT';
            """;
    }
}
