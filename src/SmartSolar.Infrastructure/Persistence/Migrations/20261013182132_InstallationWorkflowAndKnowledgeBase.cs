using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstallationWorkflowAndKnowledgeBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_session",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_session", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_session_user_account_user_id",
                        column: x => x.user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "file_reference",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    bucket = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    object_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    declared_content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    declared_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    declared_checksum_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    object_version_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    etag = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    verified_content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    verified_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    verified_checksum_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    verification_attempts = table.Column<int>(type: "integer", nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_reference", x => x.id);
                    table.CheckConstraint("ck_file_reference_checksum_format", "(verified_checksum_sha256 IS NULL OR (length(verified_checksum_sha256) = 64 AND verified_checksum_sha256 = lower(verified_checksum_sha256))) AND (declared_checksum_sha256 IS NULL OR (length(declared_checksum_sha256) = 64 AND declared_checksum_sha256 = lower(declared_checksum_sha256)))");
                    table.CheckConstraint("ck_file_reference_purpose", "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT')");
                    table.CheckConstraint("ck_file_reference_sizes", "declared_size_bytes > 0 AND (verified_size_bytes IS NULL OR verified_size_bytes > 0)");
                    table.CheckConstraint("ck_file_reference_status", "status IN ('PENDING_VERIFICATION', 'VERIFIED', 'REJECTED')");
                    table.CheckConstraint("ck_file_reference_verified_complete", "status <> 'VERIFIED' OR (verified_content_type IS NOT NULL AND verified_size_bytes IS NOT NULL AND verified_at IS NOT NULL AND verified_checksum_sha256 IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_file_reference_user_account_uploaded_by_user_id",
                        column: x => x.uploaded_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "onsite_survey_report",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    roof_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    roof_material = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    roof_length_m = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    roof_width_m = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    usable_area_m2 = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    tilt_degree = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    azimuth_degree = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    roof_condition = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    electrical_cabinet_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    electrical_connection_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    installation_feasibility = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    proposed_inverter_location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    proposed_panel_layout_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    measured_latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    measured_longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onsite_survey_report", x => x.id);
                    table.CheckConstraint("ck_onsite_survey_report_angles", "(tilt_degree IS NULL OR tilt_degree BETWEEN 0 AND 90) AND (azimuth_degree IS NULL OR (azimuth_degree >= 0 AND azimuth_degree < 360))");
                    table.CheckConstraint("ck_onsite_survey_report_completed", "status <> 'COMPLETED' OR (completed_at IS NOT NULL AND started_at IS NOT NULL AND version >= 1)");
                    table.CheckConstraint("ck_onsite_survey_report_dimensions", "(roof_length_m IS NULL OR roof_length_m > 0) AND (roof_width_m IS NULL OR roof_width_m > 0) AND (usable_area_m2 IS NULL OR usable_area_m2 > 0)");
                    table.CheckConstraint("ck_onsite_survey_report_location_paired", "(measured_latitude IS NULL) = (measured_longitude IS NULL)");
                    table.CheckConstraint("ck_onsite_survey_report_status", "status IN ('DRAFT', 'IN_PROGRESS', 'COMPLETED')");
                    table.ForeignKey(
                        name: "FK_onsite_survey_report_survey_request_survey_request_id",
                        column: x => x.survey_request_id,
                        principalTable: "survey_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_onsite_survey_report_technician_appointment_appointment_id",
                        column: x => x.appointment_id,
                        principalTable: "technician_appointment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_onsite_survey_report_user_account_technician_user_id",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_webhook_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_code = table.Column<long>(type: "bigint", nullable: true),
                    provider_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    signature_valid = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_webhook_event", x => x.id);
                    table.CheckConstraint("ck_payment_webhook_event_outcome", "outcome IN ('APPLIED', 'DUPLICATE', 'IGNORED', 'REJECTED')");
                });

            migrationBuilder.CreateTable(
                name: "rag_usage_daily",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    usage_date = table.Column<DateOnly>(type: "date", nullable: false),
                    requests = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    estimated_tokens = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_usage_daily", x => new { x.user_id, x.usage_date });
                    table.CheckConstraint("ck_rag_usage_daily_non_negative", "requests >= 0 AND input_tokens >= 0 AND output_tokens >= 0 AND estimated_tokens >= 0");
                });

            migrationBuilder.CreateTable(
                name: "rag_usage_reservation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    usage_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reserved_tokens = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_usage_reservation", x => x.id);
                    table.CheckConstraint("ck_rag_usage_reservation_lease", "expires_at > created_at");
                    table.CheckConstraint("ck_rag_usage_reservation_tokens", "reserved_tokens > 0");
                    table.ForeignKey(
                        name: "FK_rag_usage_reservation_user_account_user_id",
                        column: x => x.user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chat_message",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    client_message_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reply_to_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_message", x => x.id);
                    table.CheckConstraint("ck_chat_message_role", "role IN ('USER', 'ASSISTANT')");
                    table.CheckConstraint("ck_chat_message_status", "status IN ('COMPLETED', 'INSUFFICIENT_CONTEXT', 'FAILED')");
                    table.ForeignKey(
                        name: "FK_chat_message_chat_message_reply_to_message_id",
                        column: x => x.reply_to_message_id,
                        principalTable: "chat_message",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_message_chat_session_session_id",
                        column: x => x.session_id,
                        principalTable: "chat_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "onsite_survey_amendment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amendment_number = table.Column<int>(type: "integer", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onsite_survey_amendment", x => x.id);
                    table.ForeignKey(
                        name: "FK_onsite_survey_amendment_onsite_survey_report_report_id",
                        column: x => x.report_id,
                        principalTable: "onsite_survey_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_onsite_survey_amendment_user_account_author_user_id",
                        column: x => x.author_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "onsite_survey_obstacle",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    height_m = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    area_m2 = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onsite_survey_obstacle", x => x.id);
                    table.CheckConstraint("ck_onsite_survey_obstacle_sizes", "(height_m IS NULL OR height_m >= 0) AND (area_m2 IS NULL OR area_m2 >= 0)");
                    table.ForeignKey(
                        name: "FK_onsite_survey_obstacle_onsite_survey_report_report_id",
                        column: x => x.report_id,
                        principalTable: "onsite_survey_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quotation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    onsite_survey_report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    onsite_survey_version = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    previous_quotation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    equipment_subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    labor_cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    transport_cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    other_service_cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax_rate_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    terms = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    customer_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation", x => x.id);
                    table.CheckConstraint("ck_quotation_amounts_non_negative", "equipment_subtotal >= 0 AND labor_cost >= 0 AND transport_cost >= 0 AND other_service_cost >= 0 AND discount_amount >= 0 AND tax_amount >= 0");
                    table.CheckConstraint("ck_quotation_currency", "currency = 'VND'");
                    table.CheckConstraint("ck_quotation_discount_bounded", "discount_amount <= subtotal");
                    table.CheckConstraint("ck_quotation_issued_fields", "status = 'DRAFT' OR (issued_at IS NOT NULL AND valid_until IS NOT NULL AND customer_snapshot IS NOT NULL)");
                    table.CheckConstraint("ck_quotation_status", "status IN ('DRAFT', 'ISSUED', 'ACCEPTED', 'REVISION_REQUESTED', 'SUPERSEDED')");
                    table.CheckConstraint("ck_quotation_tax_rate", "tax_rate_percent BETWEEN 0 AND 20");
                    table.CheckConstraint("ck_quotation_total_consistent", "total_amount = subtotal - discount_amount + tax_amount");
                    table.CheckConstraint("ck_quotation_version_positive", "version >= 1");
                    table.ForeignKey(
                        name: "FK_quotation_file_reference_document_file_id",
                        column: x => x.document_file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotation_onsite_survey_report_onsite_survey_report_id",
                        column: x => x.onsite_survey_report_id,
                        principalTable: "onsite_survey_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotation_quotation_previous_quotation_id",
                        column: x => x.previous_quotation_id,
                        principalTable: "quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotation_survey_request_survey_request_id",
                        column: x => x.survey_request_id,
                        principalTable: "survey_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotation_user_account_customer_user_id",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotation_user_account_sales_user_id",
                        column: x => x.sales_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "onsite_survey_attachment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caption = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    amendment_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onsite_survey_attachment", x => x.id);
                    table.ForeignKey(
                        name: "FK_onsite_survey_attachment_file_reference_file_id",
                        column: x => x.file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_onsite_survey_attachment_onsite_survey_amendment_amendment_~",
                        column: x => x.amendment_id,
                        principalTable: "onsite_survey_amendment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_onsite_survey_attachment_onsite_survey_report_report_id",
                        column: x => x.report_id,
                        principalTable: "onsite_survey_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_onsite_survey_attachment_user_account_added_by_user_id",
                        column: x => x.added_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_contract",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    previous_contract_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_version = table.Column<int>(type: "integer", nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    pricing_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    customer_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    terms = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    canonical_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    official_document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    official_document_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    official_document_confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    official_document_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signed_document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    signed_document_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    signed_submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_contract", x => x.id);
                    table.CheckConstraint("ck_customer_contract_amount", "total_amount > 0 AND currency = 'VND'");
                    table.CheckConstraint("ck_customer_contract_issued_hash", "status = 'DRAFT' OR (canonical_hash IS NOT NULL AND issued_at IS NOT NULL)");
                    table.CheckConstraint("ck_customer_contract_published_document", "status IN ('DRAFT', 'ISSUED', 'SUPERSEDED') OR (official_document_file_id IS NOT NULL AND official_document_sha256 IS NOT NULL)");
                    table.CheckConstraint("ck_customer_contract_signed_review", "status NOT IN ('SIGNED', 'PAID') OR (signed_document_file_id IS NOT NULL AND reviewed_by_user_id IS NOT NULL AND signed_at IS NOT NULL)");
                    table.CheckConstraint("ck_customer_contract_status", "status IN ('DRAFT', 'ISSUED', 'AWAITING_SIGNATURE', 'UNDER_REVIEW', 'SIGNATURE_REJECTED', 'SIGNED', 'PAID', 'SUPERSEDED')");
                    table.CheckConstraint("ck_customer_contract_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_customer_contract_customer_contract_previous_contract_id",
                        column: x => x.previous_contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_contract_file_reference_official_document_file_id",
                        column: x => x.official_document_file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_contract_file_reference_signed_document_file_id",
                        column: x => x.signed_document_file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_contract_quotation_quotation_id",
                        column: x => x.quotation_id,
                        principalTable: "quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_contract_survey_request_survey_request_id",
                        column: x => x.survey_request_id,
                        principalTable: "survey_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_contract_user_account_customer_user_id",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_contract_user_account_official_document_confirmed_~",
                        column: x => x.official_document_confirmed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_contract_user_account_reviewed_by_user_id",
                        column: x => x.reviewed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_contract_user_account_sales_user_id",
                        column: x => x.sales_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quotation_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_quotation_event_quotation_quotation_id",
                        column: x => x.quotation_id,
                        principalTable: "quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotation_event_user_account_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quotation_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sku = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    catalog_unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_line", x => x.id);
                    table.CheckConstraint("ck_quotation_line_prices", "unit_price >= 0 AND line_total >= 0");
                    table.CheckConstraint("ck_quotation_line_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_quotation_line_product_product_id",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotation_line_quotation_quotation_id",
                        column: x => x.quotation_id,
                        principalTable: "quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contract_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_contract_event_customer_contract_contract_id",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_event_user_account_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contract_signature_submission",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    official_document_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_signature_submission", x => x.id);
                    table.CheckConstraint("ck_contract_signature_submission_outcome", "outcome IN ('PENDING', 'APPROVED', 'REJECTED')");
                    table.ForeignKey(
                        name: "FK_contract_signature_submission_customer_contract_contract_id",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_signature_submission_file_reference_file_id",
                        column: x => x.file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_signature_submission_user_account_reviewed_by_user~",
                        column: x => x.reviewed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contract_signature_submission_user_account_submitted_by_use~",
                        column: x => x.submitted_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_project",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    survey_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manager_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    planned_start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    planned_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acceptance_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    handover_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acceptance_round = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_project", x => x.id);
                    table.CheckConstraint("ck_installation_project_completed", "status <> 'COMPLETED' OR (completed_at IS NOT NULL AND handover_at IS NOT NULL)");
                    table.CheckConstraint("ck_installation_project_dates", "planned_end_date IS NULL OR planned_start_date IS NULL OR planned_end_date >= planned_start_date");
                    table.CheckConstraint("ck_installation_project_status", "status IN ('PLANNING', 'SCHEDULED', 'IN_PROGRESS', 'PENDING_CUSTOMER_ACCEPTANCE', 'REWORK_REQUIRED', 'COMPLETED')");
                    table.ForeignKey(
                        name: "FK_installation_project_customer_contract_contract_id",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_project_property_site_property_site_id",
                        column: x => x.property_site_id,
                        principalTable: "property_site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_project_survey_request_survey_request_id",
                        column: x => x.survey_request_id,
                        principalTable: "survey_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_project_user_account_customer_user_id",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_project_user_account_manager_user_id",
                        column: x => x.manager_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_attempt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    order_code = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    description = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    provider_payment_link_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    checkout_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    qr_code = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    amount_paid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    provider_transaction_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    last_reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempt", x => x.id);
                    table.CheckConstraint("ck_payment_attempt_amount", "amount > 0 AND currency = 'VND'");
                    table.CheckConstraint("ck_payment_attempt_order_code", "order_code > 0 AND order_code < 9007199254740991");
                    table.CheckConstraint("ck_payment_attempt_paid_fields", "status <> 'PAID' OR (paid_at IS NOT NULL AND amount_paid = amount)");
                    table.CheckConstraint("ck_payment_attempt_status", "status IN ('PENDING', 'PROCESSING', 'PAID', 'CANCELLED', 'EXPIRED', 'FAILED', 'REVIEW_REQUIRED', 'REFUND_REQUIRED')");
                    table.ForeignKey(
                        name: "FK_payment_attempt_customer_contract_contract_id",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_attempt_user_account_customer_user_id",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_acceptance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completion_report = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_acceptance", x => x.id);
                    table.CheckConstraint("ck_installation_acceptance_decided", "(decision = 'PENDING') = (decided_at IS NULL)");
                    table.CheckConstraint("ck_installation_acceptance_decision", "decision IN ('PENDING', 'ACCEPTED', 'REWORK_REQUESTED')");
                    table.ForeignKey(
                        name: "FK_installation_acceptance_installation_project_project_id",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_acceptance_user_account_decided_by_user_id",
                        column: x => x.decided_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_acceptance_user_account_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_checklist_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    evidence_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    verified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_checklist_item", x => x.id);
                    table.CheckConstraint("ck_installation_checklist_item_evidence", "status <> 'SATISFIED' OR evidence_file_id IS NOT NULL");
                    table.CheckConstraint("ck_installation_checklist_item_status", "status IN ('PENDING', 'SATISFIED', 'NOT_APPLICABLE')");
                    table.ForeignKey(
                        name: "FK_installation_checklist_item_file_reference_evidence_file_id",
                        column: x => x.evidence_file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_checklist_item_installation_project_project_id",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_checklist_item_user_account_verified_by_user_id",
                        column: x => x.verified_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_installation_event_installation_project_project_id",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_event_user_account_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_milestone",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    weight_percent = table.Column<int>(type: "integer", nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    progress_percent = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    rework_round = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_milestone", x => x.id);
                    table.CheckConstraint("ck_installation_milestone_completed", "status <> 'COMPLETED' OR (completed_at IS NOT NULL AND completed_by_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_installation_milestone_numbers", "weight_percent BETWEEN 0 AND 100 AND progress_percent BETWEEN 0 AND 100 AND rework_round >= 0");
                    table.CheckConstraint("ck_installation_milestone_status", "status IN ('NOT_STARTED', 'IN_PROGRESS', 'COMPLETED')");
                    table.ForeignKey(
                        name: "FK_installation_milestone_installation_project_project_id",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_milestone_user_account_completed_by_user_id",
                        column: x => x.completed_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_shift",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    local_start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    local_end = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_shift", x => x.id);
                    table.CheckConstraint("ck_installation_shift_range", "ends_at > starts_at AND local_end > local_start");
                    table.CheckConstraint("ck_installation_shift_status", "status IN ('ACTIVE', 'CANCELLED')");
                    table.ForeignKey(
                        name: "FK_installation_shift_installation_project_project_id",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_team_member",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_leader = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_team_member", x => x.id);
                    table.CheckConstraint("ck_installation_team_member_leader_active", "NOT is_leader OR removed_at IS NULL");
                    table.ForeignKey(
                        name: "FK_installation_team_member_installation_project_project_id",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_team_member_user_account_assigned_by_user_id",
                        column: x => x.assigned_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_team_member_user_account_technician_user_id",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installed_asset",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    serial_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    position_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    warranty_months = table.Column<int>(type: "integer", nullable: true),
                    registered_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installed_asset", x => x.id);
                    table.CheckConstraint("ck_installed_asset_type", "asset_type IN ('PANEL', 'INVERTER', 'BATTERY', 'METER', 'OTHER')");
                    table.CheckConstraint("ck_installed_asset_warranty", "warranty_months IS NULL OR warranty_months BETWEEN 0 AND 600");
                    table.ForeignKey(
                        name: "FK_installed_asset_installation_project_project_id",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installed_asset_product_product_id",
                        column: x => x.product_id,
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installed_asset_user_account_registered_by_user_id",
                        column: x => x.registered_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_payment_event_customer_contract_contract_id",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_event_payment_attempt_payment_attempt_id",
                        column: x => x.payment_attempt_id,
                        principalTable: "payment_attempt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_milestone_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rework_round = table.Column<int>(type: "integer", nullable: false),
                    caption = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_milestone_evidence", x => x.id);
                    table.ForeignKey(
                        name: "FK_installation_milestone_evidence_file_reference_file_id",
                        column: x => x.file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_milestone_evidence_installation_milestone_mile~",
                        column: x => x.milestone_id,
                        principalTable: "installation_milestone",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_milestone_evidence_installation_project_projec~",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_milestone_evidence_user_account_added_by_user_~",
                        column: x => x.added_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "installation_shift_assignment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation_shift_assignment", x => x.id);
                    table.CheckConstraint("ck_installation_shift_assignment_range", "ends_at > starts_at");
                    table.CheckConstraint("ck_installation_shift_assignment_released", "(status = 'RELEASED') = (released_at IS NOT NULL)");
                    table.CheckConstraint("ck_installation_shift_assignment_status", "status IN ('ACTIVE', 'RELEASED')");
                    table.ForeignKey(
                        name: "FK_installation_shift_assignment_installation_project_project_~",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_shift_assignment_installation_shift_shift_id",
                        column: x => x.shift_id,
                        principalTable: "installation_shift",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_installation_shift_assignment_user_account_technician_user_~",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chat_citation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    document_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: true),
                    section = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    snippet = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_citation", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_citation_chat_message_message_id",
                        column: x => x.message_id,
                        principalTable: "chat_message",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_document",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tags = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    latest_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_document", x => x.id);
                    table.CheckConstraint("ck_knowledge_document_published", "(status = 'PUBLISHED') = (published_version_id IS NOT NULL)");
                    table.CheckConstraint("ck_knowledge_document_status", "status IN ('REGISTERED', 'PROCESSING', 'READY_FOR_REVIEW', 'PUBLISHED', 'UNPUBLISHED', 'FAILED', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "FK_knowledge_document_user_account_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_document_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    chunk_count = table.Column<int>(type: "integer", nullable: true),
                    extracted_characters = table.Column<int>(type: "integer", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    embedding_dimension = table.Column<int>(type: "integer", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_document_version", x => x.id);
                    table.UniqueConstraint("ak_knowledge_document_version_id_document", x => new { x.id, x.document_id });
                    table.CheckConstraint("ck_knowledge_document_version_number", "version_number >= 1");
                    table.CheckConstraint("ck_knowledge_document_version_status", "status IN ('REGISTERED', 'VERIFYING', 'PROCESSING', 'READY_FOR_REVIEW', 'PUBLISHED', 'SUPERSEDED', 'FAILED', 'OCR_REQUIRED', 'UNSUPPORTED_CONTENT', 'WITHDRAWN')");
                    table.ForeignKey(
                        name: "FK_knowledge_document_version_file_reference_file_id",
                        column: x => x.file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledge_document_version_knowledge_document_document_id",
                        column: x => x.document_id,
                        principalTable: "knowledge_document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledge_document_version_user_account_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_ingestion_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_ingestion_job", x => x.id);
                    table.CheckConstraint("ck_knowledge_ingestion_job_attempts", "attempts >= 0 AND max_attempts >= 1");
                    table.CheckConstraint("ck_knowledge_ingestion_job_kind", "kind IN ('INGEST', 'DELETE_VECTORS')");
                    table.CheckConstraint("ck_knowledge_ingestion_job_status", "status IN ('PENDING', 'RUNNING', 'COMPLETED', 'FAILED', 'RETRY_WAITING')");
                    table.ForeignKey(
                        name: "FK_knowledge_ingestion_job_knowledge_document_version_version_~",
                        column: x => x.version_id,
                        principalTable: "knowledge_document_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_citation_document_id",
                table: "chat_citation",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_citation_version_id",
                table: "chat_citation",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ux_chat_citation_message_rank",
                table: "chat_citation",
                columns: new[] { "message_id", "rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_message_session_created_at",
                table: "chat_message",
                columns: new[] { "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_chat_message_client_id",
                table: "chat_message",
                columns: new[] { "session_id", "client_message_id" },
                unique: true,
                filter: "client_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_chat_message_one_reply",
                table: "chat_message",
                column: "reply_to_message_id",
                unique: true,
                filter: "reply_to_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_chat_session_user_updated_at",
                table: "chat_session",
                columns: new[] { "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_event_actor_user_id",
                table: "contract_event",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_contract_event_contract_created_at",
                table: "contract_event",
                columns: new[] { "contract_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_signature_submission_reviewed_by_user_id",
                table: "contract_signature_submission",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_contract_signature_submission_submitted_by_user_id",
                table: "contract_signature_submission",
                column: "submitted_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_contract_signature_submission_file",
                table: "contract_signature_submission",
                column: "file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_contract_signature_submission_one_pending",
                table: "contract_signature_submission",
                columns: new[] { "contract_id", "outcome" },
                unique: true,
                filter: "outcome = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "ix_customer_contract_customer_status",
                table: "customer_contract",
                columns: new[] { "customer_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_contract_official_document_confirmed_by_user_id",
                table: "customer_contract",
                column: "official_document_confirmed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_contract_official_document_file_id",
                table: "customer_contract",
                column: "official_document_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_contract_previous_contract_id",
                table: "customer_contract",
                column: "previous_contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_contract_reviewed_by_user_id",
                table: "customer_contract",
                column: "reviewed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_contract_sales_user_id",
                table: "customer_contract",
                column: "sales_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_customer_contract_signed_document_file_id",
                table: "customer_contract",
                column: "signed_document_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_contract_survey_request",
                table: "customer_contract",
                column: "survey_request_id");

            migrationBuilder.CreateIndex(
                name: "ux_customer_contract_number_version",
                table: "customer_contract",
                columns: new[] { "contract_number", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_customer_contract_one_draft",
                table: "customer_contract",
                columns: new[] { "quotation_id", "status" },
                unique: true,
                filter: "status = 'DRAFT'");

            migrationBuilder.CreateIndex(
                name: "ux_customer_contract_one_live",
                table: "customer_contract",
                column: "quotation_id",
                unique: true,
                filter: "status NOT IN ('DRAFT', 'SUPERSEDED')");

            migrationBuilder.CreateIndex(
                name: "ix_file_reference_uploader_created_at",
                table: "file_reference",
                columns: new[] { "uploaded_by_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_file_reference_bucket_object_key",
                table: "file_reference",
                columns: new[] { "bucket", "object_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_installation_acceptance_decided_by_user_id",
                table: "installation_acceptance",
                column: "decided_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_acceptance_requested_by_user_id",
                table: "installation_acceptance",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_installation_acceptance_one_pending",
                table: "installation_acceptance",
                columns: new[] { "project_id", "decision" },
                unique: true,
                filter: "decision = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "ux_installation_acceptance_project_round",
                table: "installation_acceptance",
                columns: new[] { "project_id", "round" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_installation_checklist_item_evidence_file_id",
                table: "installation_checklist_item",
                column: "evidence_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_checklist_item_verified_by_user_id",
                table: "installation_checklist_item",
                column: "verified_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_installation_checklist_item_project_code",
                table: "installation_checklist_item",
                columns: new[] { "project_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_installation_event_actor_user_id",
                table: "installation_event",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_event_project_created_at",
                table: "installation_event",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_installation_milestone_completed_by_user_id",
                table: "installation_milestone",
                column: "completed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_installation_milestone_project_code",
                table: "installation_milestone",
                columns: new[] { "project_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_installation_milestone_project_order",
                table: "installation_milestone",
                columns: new[] { "project_id", "sort_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_installation_milestone_evidence_added_by_user_id",
                table: "installation_milestone_evidence",
                column: "added_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_milestone_evidence_milestone_round",
                table: "installation_milestone_evidence",
                columns: new[] { "milestone_id", "rework_round" });

            migrationBuilder.CreateIndex(
                name: "IX_installation_milestone_evidence_project_id",
                table: "installation_milestone_evidence",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_installation_milestone_evidence_file",
                table: "installation_milestone_evidence",
                column: "file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_installation_project_customer_status",
                table: "installation_project",
                columns: new[] { "customer_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_installation_project_manager_user_id",
                table: "installation_project",
                column: "manager_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_project_property_site_id",
                table: "installation_project",
                column: "property_site_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_project_survey_request_id",
                table: "installation_project",
                column: "survey_request_id");

            migrationBuilder.CreateIndex(
                name: "ux_installation_project_contract",
                table: "installation_project",
                column: "contract_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_installation_shift_project_start",
                table: "installation_shift",
                columns: new[] { "project_id", "starts_at" },
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "IX_installation_shift_assignment_project_id",
                table: "installation_shift_assignment",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_installation_shift_assignment_technician_starts_at",
                table: "installation_shift_assignment",
                columns: new[] { "technician_user_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ux_installation_shift_assignment_shift_technician",
                table: "installation_shift_assignment",
                columns: new[] { "shift_id", "technician_user_id" },
                unique: true,
                filter: "status = 'ACTIVE'");

            migrationBuilder.CreateIndex(
                name: "IX_installation_team_member_assigned_by_user_id",
                table: "installation_team_member",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_installation_team_member_technician_user_id",
                table: "installation_team_member",
                column: "technician_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_installation_team_member_active",
                table: "installation_team_member",
                columns: new[] { "project_id", "technician_user_id" },
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_installation_team_member_one_leader",
                table: "installation_team_member",
                columns: new[] { "project_id", "is_leader" },
                unique: true,
                filter: "is_leader AND removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_installed_asset_product_id",
                table: "installed_asset",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_installed_asset_project",
                table: "installed_asset",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_installed_asset_registered_by_user_id",
                table: "installed_asset",
                column: "registered_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_installed_asset_manufacturer_serial",
                table: "installed_asset",
                columns: new[] { "manufacturer", "serial_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_created_by_user_id",
                table: "knowledge_document",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_document_latest_version",
                table: "knowledge_document",
                columns: new[] { "latest_version_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_document_published_version",
                table: "knowledge_document",
                columns: new[] { "published_version_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_document_status",
                table: "knowledge_document",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_version_created_by_user_id",
                table: "knowledge_document_version",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_document_version_file",
                table: "knowledge_document_version",
                column: "file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_document_version_number",
                table: "knowledge_document_version",
                columns: new[] { "document_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_document_version_one_published",
                table: "knowledge_document_version",
                columns: new[] { "document_id", "status" },
                unique: true,
                filter: "status = 'PUBLISHED'");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_ingestion_job_due",
                table: "knowledge_ingestion_job",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_ingestion_job_version_id",
                table: "knowledge_ingestion_job",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_ingestion_job_idempotency",
                table: "knowledge_ingestion_job",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_onsite_survey_amendment_author_user_id",
                table: "onsite_survey_amendment",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_onsite_survey_amendment_report_number",
                table: "onsite_survey_amendment",
                columns: new[] { "report_id", "amendment_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_onsite_survey_attachment_added_by_user_id",
                table: "onsite_survey_attachment",
                column: "added_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_onsite_survey_attachment_amendment_id",
                table: "onsite_survey_attachment",
                column: "amendment_id");

            migrationBuilder.CreateIndex(
                name: "ix_onsite_survey_attachment_report",
                table: "onsite_survey_attachment",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "ux_onsite_survey_attachment_file",
                table: "onsite_survey_attachment",
                column: "file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_onsite_survey_obstacle_report_order",
                table: "onsite_survey_obstacle",
                columns: new[] { "report_id", "sort_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_onsite_survey_report_appointment",
                table: "onsite_survey_report",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "IX_onsite_survey_report_technician_user_id",
                table: "onsite_survey_report",
                column: "technician_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_onsite_survey_report_survey_request",
                table: "onsite_survey_report",
                column: "survey_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempt_contract_created_at",
                table: "payment_attempt",
                columns: new[] { "contract_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_customer_user_id",
                table: "payment_attempt",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_one_paid",
                table: "payment_attempt",
                columns: new[] { "contract_id", "status" },
                unique: true,
                filter: "status = 'PAID'");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_one_payable",
                table: "payment_attempt",
                column: "contract_id",
                unique: true,
                filter: "status IN ('PENDING', 'PROCESSING')");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_order_code",
                table: "payment_attempt",
                column: "order_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_provider_reference",
                table: "payment_attempt",
                columns: new[] { "provider", "provider_reference" },
                unique: true,
                filter: "provider_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_event_contract_created_at",
                table: "payment_event",
                columns: new[] { "contract_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_event_payment_attempt_id",
                table: "payment_event",
                column: "payment_attempt_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_webhook_event_order_code",
                table: "payment_webhook_event",
                column: "order_code");

            migrationBuilder.CreateIndex(
                name: "ux_payment_webhook_event_payload",
                table: "payment_webhook_event",
                columns: new[] { "provider", "payload_sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quotation_customer_status",
                table: "quotation",
                columns: new[] { "customer_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_quotation_document_file_id",
                table: "quotation",
                column: "document_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_quotation_onsite_survey_report_id",
                table: "quotation",
                column: "onsite_survey_report_id");

            migrationBuilder.CreateIndex(
                name: "IX_quotation_previous_quotation_id",
                table: "quotation",
                column: "previous_quotation_id");

            migrationBuilder.CreateIndex(
                name: "IX_quotation_sales_user_id",
                table: "quotation",
                column: "sales_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_quotation_number_version",
                table: "quotation",
                columns: new[] { "quote_number", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_quotation_one_draft",
                table: "quotation",
                column: "survey_request_id",
                unique: true,
                filter: "status = 'DRAFT'");

            migrationBuilder.CreateIndex(
                name: "ux_quotation_one_live",
                table: "quotation",
                columns: new[] { "survey_request_id", "customer_user_id" },
                unique: true,
                filter: "status IN ('ISSUED', 'ACCEPTED')");

            migrationBuilder.CreateIndex(
                name: "ux_quotation_survey_request_version",
                table: "quotation",
                columns: new[] { "survey_request_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quotation_event_actor_user_id",
                table: "quotation_event",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_quotation_event_quotation_created_at",
                table: "quotation_event",
                columns: new[] { "quotation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_quotation_line_product_id",
                table: "quotation_line",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_quotation_line_number",
                table: "quotation_line",
                columns: new[] { "quotation_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rag_usage_reservation_date",
                table: "rag_usage_reservation",
                column: "usage_date");

            migrationBuilder.CreateIndex(
                name: "ix_rag_usage_reservation_user_date_expires",
                table: "rag_usage_reservation",
                columns: new[] { "user_id", "usage_date", "expires_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_chat_citation_knowledge_document_document_id",
                table: "chat_citation",
                column: "document_id",
                principalTable: "knowledge_document",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_chat_citation_knowledge_document_version_version_id",
                table: "chat_citation",
                column: "version_id",
                principalTable: "knowledge_document_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_knowledge_document_latest_version",
                table: "knowledge_document",
                columns: new[] { "latest_version_id", "id" },
                principalTable: "knowledge_document_version",
                principalColumns: new[] { "id", "document_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_knowledge_document_published_version",
                table: "knowledge_document",
                columns: new[] { "published_version_id", "id" },
                principalTable: "knowledge_document_version",
                principalColumns: new[] { "id", "document_id" },
                onDelete: ReferentialAction.Restrict);

            // Hand-written (EF Core cannot model exclusion constraints): a technician can never hold two
            // ACTIVE installation shift assignments whose [starts_at, ends_at) intervals overlap, whatever
            // the write path. Survey appointments are serialised against installation planning through the
            // technician_profile row lock. btree_gist (already required by AddTechnicianScheduling) provides
            // the "=" operator class for uuid inside a GiST index. Identical to the manual section of
            // guides/sql/installation-workflow-and-knowledge-base-schema-review.sql.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql(
                "ALTER TABLE installation_shift_assignment ADD CONSTRAINT ex_installation_shift_assignment_no_overlap " +
                "EXCLUDE USING gist (technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&) " +
                "WHERE (status = 'ACTIVE');");

            // knowledge_chunk (pgvector) is NOT part of this migration: it is owned by the RAG service and
            // applied separately from src/SmartSolar.RagService/sql/knowledge_chunk.sql after this migration.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the tables created by Up() are dropped; no existing table is touched. Drop the RAG
            // service's knowledge_chunk table first if it was created (it references knowledge tables).
            // The btree_gist extension is left installed: ex_technician_appointment_no_overlap depends on it.
            migrationBuilder.Sql("ALTER TABLE installation_shift_assignment DROP CONSTRAINT IF EXISTS ex_installation_shift_assignment_no_overlap;");

            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_document_version_knowledge_document_document_id",
                table: "knowledge_document_version");

            migrationBuilder.DropTable(
                name: "chat_citation");

            migrationBuilder.DropTable(
                name: "contract_event");

            migrationBuilder.DropTable(
                name: "contract_signature_submission");

            migrationBuilder.DropTable(
                name: "installation_acceptance");

            migrationBuilder.DropTable(
                name: "installation_checklist_item");

            migrationBuilder.DropTable(
                name: "installation_event");

            migrationBuilder.DropTable(
                name: "installation_milestone_evidence");

            migrationBuilder.DropTable(
                name: "installation_shift_assignment");

            migrationBuilder.DropTable(
                name: "installation_team_member");

            migrationBuilder.DropTable(
                name: "installed_asset");

            migrationBuilder.DropTable(
                name: "knowledge_ingestion_job");

            migrationBuilder.DropTable(
                name: "onsite_survey_attachment");

            migrationBuilder.DropTable(
                name: "onsite_survey_obstacle");

            migrationBuilder.DropTable(
                name: "payment_event");

            migrationBuilder.DropTable(
                name: "payment_webhook_event");

            migrationBuilder.DropTable(
                name: "quotation_event");

            migrationBuilder.DropTable(
                name: "quotation_line");

            migrationBuilder.DropTable(
                name: "rag_usage_daily");

            migrationBuilder.DropTable(
                name: "rag_usage_reservation");

            migrationBuilder.DropTable(
                name: "chat_message");

            migrationBuilder.DropTable(
                name: "installation_milestone");

            migrationBuilder.DropTable(
                name: "installation_shift");

            migrationBuilder.DropTable(
                name: "onsite_survey_amendment");

            migrationBuilder.DropTable(
                name: "payment_attempt");

            migrationBuilder.DropTable(
                name: "chat_session");

            migrationBuilder.DropTable(
                name: "installation_project");

            migrationBuilder.DropTable(
                name: "customer_contract");

            migrationBuilder.DropTable(
                name: "quotation");

            migrationBuilder.DropTable(
                name: "onsite_survey_report");

            migrationBuilder.DropTable(
                name: "knowledge_document");

            migrationBuilder.DropTable(
                name: "knowledge_document_version");

            migrationBuilder.DropTable(
                name: "file_reference");
        }
    }
}
