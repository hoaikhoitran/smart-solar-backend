using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AfterSalesServices : Migration
    {
        /// <summary>
        /// PostgreSQL-only invariants EF cannot model: at most one term of a kind covers any instant for one contract (installation; btree_gist,
        /// already installed by AddTechnicianScheduling, provides "=" on uuid/text),
        /// a CONTRACT payment line always names the same contract as its checkout (so the existing one-payable / one-paid indexes on
        /// payment_attempt.contract_id also cover combined checkouts; EF cannot model a key over the nullable contract_id),
        /// and the Supabase hardening policy for every new table (RLS on, no policies, no client-role grants).
        /// </summary>
        public const string UpManualSql = """
            CREATE EXTENSION IF NOT EXISTS btree_gist;
            ALTER TABLE service_subscription ADD CONSTRAINT ex_service_subscription_no_overlap
                EXCLUDE USING gist (contract_id WITH =, kind WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
                WHERE (starts_at IS NOT NULL AND status IN ('PAID', 'ACTIVE', 'CANCELLATION_REQUESTED'));
            ALTER TABLE payment_attempt ADD CONSTRAINT ux_payment_attempt_id_contract UNIQUE (id, contract_id);
            ALTER TABLE payment_attempt_item ADD CONSTRAINT fk_payment_attempt_item_attempt_contract
                FOREIGN KEY (payment_attempt_id, contract_id) REFERENCES payment_attempt (id, contract_id) ON DELETE RESTRICT;
            ALTER TABLE public.after_sales_event ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.after_sales_notification ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.disaster_claim ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.maintenance_entitlement ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.payment_attempt_item ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.refund_record ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.sales_task ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.service_evidence ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.service_item ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.service_package ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.service_quotation ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.service_request ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.service_subscription ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.service_visit ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.warranty_option ENABLE ROW LEVEL SECURITY;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                    REVOKE ALL ON public.after_sales_event, public.after_sales_notification, public.disaster_claim, public.maintenance_entitlement, public.payment_attempt_item, public.refund_record, public.sales_task, public.service_evidence, public.service_item, public.service_package, public.service_quotation, public.service_request, public.service_subscription, public.service_visit, public.warranty_option FROM anon;
                END IF;
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                    REVOKE ALL ON public.after_sales_event, public.after_sales_notification, public.disaster_claim, public.maintenance_entitlement, public.payment_attempt_item, public.refund_record, public.sales_task, public.service_evidence, public.service_item, public.service_package, public.service_quotation, public.service_request, public.service_subscription, public.service_visit, public.warranty_option FROM authenticated;
                END IF;
            END $$;
            """;

        /// <summary>
        /// Down refuses (instead of failing half-way) when after-sales-only checkouts exist: payment_attempt.contract_id could not become
        /// NOT NULL again. Restore from a backup instead.
        /// </summary>
        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM payment_attempt WHERE contract_id IS NULL) OR EXISTS (SELECT 1 FROM payment_event WHERE contract_id IS NULL) THEN
                    RAISE EXCEPTION 'Down refused: after-sales checkouts without a contract exist; the previous schema cannot represent them. Restore from backup instead.';
                END IF;
            END $$;
            """;

        /// <summary>Removes what <see cref="UpManualSql"/> added to the pre-existing payment_attempt table (runs after the guard).</summary>
        public const string DownManualSql = """
            ALTER TABLE payment_attempt_item DROP CONSTRAINT IF EXISTS fk_payment_attempt_item_attempt_contract;
            ALTER TABLE payment_attempt DROP CONSTRAINT IF EXISTS ux_payment_attempt_id_contract;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_installation_assignment_current",
                table: "installation_assignment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference");

            migrationBuilder.AlterColumn<Guid>(
                name: "contract_id",
                table: "payment_event",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "contract_id",
                table: "payment_attempt",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "replaces_asset_id",
                table: "installed_asset",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "service_request_id",
                table: "installed_asset",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "specifications",
                table: "installed_asset",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "installed_asset",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "ACTIVE");

            migrationBuilder.AddColumn<Guid>(
                name: "service_visit_id",
                table: "installation_assignment",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "warranty_months",
                table: "customer_contract",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "after_sales_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_after_sales_event", x => x.id);
                    table.ForeignKey(
                        name: "fk_after_sales_event_actor",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_package",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    duration_months = table.Column<int>(type: "integer", nullable: false),
                    visit_months = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    covered_disaster_types = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    coverage_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    per_incident_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    term_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_package", x => x.id);
                    table.CheckConstraint("ck_service_package_disaster", "kind <> 'DISASTER_PROTECTION' OR (covered_disaster_types IS NOT NULL AND coverage_percent BETWEEN 0 AND 100 AND per_incident_limit >= 0 AND term_limit >= per_incident_limit)");
                    table.CheckConstraint("ck_service_package_kind", "kind IN ('MAINTENANCE', 'DISASTER_PROTECTION')");
                    table.CheckConstraint("ck_service_package_maintenance", "kind <> 'MAINTENANCE' OR visit_months IS NOT NULL");
                    table.CheckConstraint("ck_service_package_price", "price > 0 AND duration_months BETWEEN 1 AND 120");
                });

            migrationBuilder.CreateTable(
                name: "warranty_option",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    months = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warranty_option", x => x.id);
                    table.CheckConstraint("ck_warranty_option_kind", "kind IN ('INSTALLATION', 'REPAIR')");
                    table.CheckConstraint("ck_warranty_option_months", "months BETWEEN 1 AND 240");
                });

            migrationBuilder.CreateTable(
                name: "service_subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    package_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    duration_months = table.Column<int>(type: "integer", nullable: false),
                    visit_months = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    covered_disaster_types = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    coverage_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    per_incident_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    term_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    coverage_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    renews_subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    checkout_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    support_reserved = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    support_consumed = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    cancellation_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expiry_reminder_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_subscription", x => x.id);
                    table.CheckConstraint("ck_service_subscription_active", "status <> 'ACTIVE' OR (starts_at IS NOT NULL AND paid_at IS NOT NULL AND project_id IS NOT NULL)");
                    table.CheckConstraint("ck_service_subscription_kind", "kind IN ('MAINTENANCE', 'DISASTER_PROTECTION')");
                    table.CheckConstraint("ck_service_subscription_price", "price > 0 AND duration_months BETWEEN 1 AND 120");
                    table.CheckConstraint("ck_service_subscription_status", "status IN ('AWAITING_PAYMENT', 'CHECKOUT_EXPIRED', 'PAID', 'ACTIVE', 'EXPIRED', 'CANCELLATION_REQUESTED', 'CANCELLED')");
                    table.CheckConstraint("ck_service_subscription_support", "support_reserved >= 0 AND support_consumed >= 0 AND (term_limit IS NULL OR support_reserved + support_consumed <= term_limit)");
                    table.CheckConstraint("ck_service_subscription_term", "(starts_at IS NULL) = (ends_at IS NULL) AND (ends_at IS NULL OR ends_at > starts_at)");
                    table.ForeignKey(
                        name: "fk_service_subscription_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_subscription_customer",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_subscription_package",
                        column: x => x.package_id,
                        principalTable: "service_package",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_subscription_project",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_subscription_renews",
                        column: x => x.renews_subscription_id,
                        principalTable: "service_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_entitlement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_month = table.Column<int>(type: "integer", nullable: false),
                    period_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    current_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    extended_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    follow_up_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_entitlement", x => x.id);
                    table.CheckConstraint("ck_maintenance_entitlement_consumed", "(status = 'CONSUMED') = (consumed_at IS NOT NULL)");
                    table.CheckConstraint("ck_maintenance_entitlement_period", "plan_month >= 1 AND period_ends_at > period_starts_at");
                    table.CheckConstraint("ck_maintenance_entitlement_status", "status IN ('AVAILABLE', 'BOOKED', 'CONSUMED', 'NOT_COMPLETED', 'EXPIRED', 'EXPIRED_UNFULFILLED')");
                    table.ForeignKey(
                        name: "fk_maintenance_entitlement_subscription",
                        column: x => x.subscription_id,
                        principalTable: "service_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_request",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    suspected_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    preferred_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    installation_warranty_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    within_installation_warranty = table.Column<bool>(type: "boolean", nullable: false),
                    warranty_source_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    within_repair_warranty = table.Column<bool>(type: "boolean", nullable: false),
                    maintenance_entitlement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duplicate_of_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    customer_decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    acceptance_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_acceptance_reminder_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    repair_warranty_months = table.Column<int>(type: "integer", nullable: true),
                    repair_warranty_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    repair_warranty_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_request", x => x.id);
                    table.CheckConstraint("ck_service_request_cancelled", "status <> 'CANCELLED' OR cancelled_at IS NOT NULL");
                    table.CheckConstraint("ck_service_request_kind", "kind IN ('FAULT', 'MAINTENANCE', 'ONE_TIME_MAINTENANCE')");
                    table.CheckConstraint("ck_service_request_maintenance_link", "(kind = 'MAINTENANCE') = (maintenance_entitlement_id IS NOT NULL)");
                    table.CheckConstraint("ck_service_request_repair_warranty", "(repair_warranty_starts_at IS NULL) = (repair_warranty_ends_at IS NULL) AND (repair_warranty_ends_at IS NULL OR repair_warranty_ends_at > repair_warranty_starts_at)");
                    table.CheckConstraint("ck_service_request_status", "status IN ('SUBMITTED', 'SCHEDULED', 'IN_PROGRESS', 'AWAITING_DECISION', 'AWAITING_QUOTATION', 'AWAITING_CUSTOMER', 'READY_FOR_WORK', 'PENDING_CUSTOMER_ACCEPTANCE', 'DISPUTED', 'REWORK_REQUIRED', 'COMPLETED', 'NOT_COMPLETED', 'CANCELLED', 'CLOSED_DUPLICATE')");
                    table.ForeignKey(
                        name: "fk_service_request_customer",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_request_duplicate_of",
                        column: x => x.duplicate_of_request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_request_entitlement",
                        column: x => x.maintenance_entitlement_id,
                        principalTable: "maintenance_entitlement",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_request_origin",
                        column: x => x.origin_request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_request_project",
                        column: x => x.project_id,
                        principalTable: "installation_project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_request_property_site",
                        column: x => x.property_site_id,
                        principalTable: "property_site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_request_suspected_asset",
                        column: x => x.suspected_asset_id,
                        principalTable: "installed_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_request_warranty_source",
                        column: x => x.warranty_source_request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "after_sales_notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_after_sales_notification", x => x.id);
                    table.ForeignKey(
                        name: "fk_after_sales_notification_recipient",
                        column: x => x.recipient_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_after_sales_notification_request",
                        column: x => x.request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_after_sales_notification_subscription",
                        column: x => x.subscription_id,
                        principalTable: "service_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "disaster_claim",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    disaster_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    incident_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconsideration_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconsideration_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    first_decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    coverage_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    per_incident_limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    support_reserved = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    support_consumed = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_disaster_claim", x => x.id);
                    table.CheckConstraint("ck_disaster_claim_status", "status IN ('NOT_ELIGIBLE', 'SUBMITTED', 'APPROVED', 'REJECTED', 'RECONSIDERATION_REQUESTED')");
                    table.CheckConstraint("ck_disaster_claim_support", "coverage_percent BETWEEN 0 AND 100 AND support_reserved >= 0 AND support_consumed >= 0 AND support_reserved + support_consumed <= per_incident_limit");
                    table.CheckConstraint("ck_disaster_claim_type", "disaster_type IN ('STORM', 'FLOOD', 'LIGHTNING')");
                    table.ForeignKey(
                        name: "fk_disaster_claim_decided_by",
                        column: x => x.decided_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_disaster_claim_request",
                        column: x => x.request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_disaster_claim_subscription",
                        column: x => x.subscription_id,
                        principalTable: "service_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entitlement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_sales_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales_task", x => x.id);
                    table.CheckConstraint("ck_sales_task_owner", "status = 'OPEN' OR status = 'CANCELLED' OR assigned_sales_user_id IS NOT NULL");
                    table.CheckConstraint("ck_sales_task_status", "status IN ('OPEN', 'CLAIMED', 'IN_PROGRESS', 'DONE', 'CANCELLED')");
                    table.CheckConstraint("ck_sales_task_type", "task_type IN ('REPAIR_QUOTATION', 'MAINTENANCE_QUOTATION', 'MAINTENANCE_FOLLOW_UP', 'ACCEPTANCE_FOLLOW_UP')");
                    table.ForeignKey(
                        name: "fk_sales_task_assignee",
                        column: x => x.assigned_sales_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_task_customer",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_task_entitlement",
                        column: x => x.entitlement_id,
                        principalTable: "maintenance_entitlement",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_task_request",
                        column: x => x.request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_visit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    technician_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    company_reschedule_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    customer_confirmation_required = table.Column<bool>(type: "boolean", nullable: false),
                    customer_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    report = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finding_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_visit", x => x.id);
                    table.CheckConstraint("ck_service_visit_purpose", "purpose IN ('INSPECTION', 'REPAIR', 'MAINTENANCE', 'REWORK')");
                    table.CheckConstraint("ck_service_visit_range", "ends_at > starts_at");
                    table.CheckConstraint("ck_service_visit_reported", "status <> 'REPORTED' OR (reported_at IS NOT NULL AND report IS NOT NULL)");
                    table.CheckConstraint("ck_service_visit_status", "status IN ('SCHEDULED', 'IN_PROGRESS', 'REPORTED', 'CUSTOMER_ABSENT', 'DECLINED', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_service_visit_assigned_by",
                        column: x => x.assigned_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_visit_request",
                        column: x => x.request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_visit_technician",
                        column: x => x.technician_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_quotation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_supplementary = table.Column<bool>(type: "boolean", nullable: false),
                    supersedes_quotation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sales_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    parts_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    labor_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    disaster_support_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    customer_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    disaster_claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    repair_warranty_months = table.Column<int>(type: "integer", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    current_payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_quotation", x => x.id);
                    table.CheckConstraint("ck_service_quotation_amounts", "parts_amount >= 0 AND labor_amount >= 0 AND total_amount = parts_amount + labor_amount AND total_amount > 0 AND disaster_support_amount >= 0 AND disaster_support_amount <= total_amount AND customer_amount = total_amount - disaster_support_amount");
                    table.CheckConstraint("ck_service_quotation_expiry", "expires_at > issued_at");
                    table.CheckConstraint("ck_service_quotation_paid", "status <> 'PAID' OR paid_at IS NOT NULL");
                    table.CheckConstraint("ck_service_quotation_status", "status IN ('ISSUED', 'ACCEPTED', 'PAID', 'REJECTED', 'SUPERSEDED', 'EXPIRED', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_service_quotation_claim",
                        column: x => x.disaster_claim_id,
                        principalTable: "disaster_claim",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_quotation_request",
                        column: x => x.request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_quotation_sales",
                        column: x => x.sales_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_quotation_supersedes",
                        column: x => x.supersedes_quotation_id,
                        principalTable: "service_quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caption = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_evidence", x => x.id);
                    table.ForeignKey(
                        name: "fk_service_evidence_added_by",
                        column: x => x.added_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_evidence_file",
                        column: x => x.file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_evidence_request",
                        column: x => x.request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_evidence_visit",
                        column: x => x.visit_id,
                        principalTable: "service_visit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_attempt_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempt_item", x => x.id);
                    table.CheckConstraint("ck_payment_attempt_item_amount", "amount > 0");
                    table.CheckConstraint("ck_payment_attempt_item_status", "status IN ('PENDING', 'SETTLED', 'REVIEW_REQUIRED')");
                    table.CheckConstraint("ck_payment_attempt_item_target", "(CASE WHEN contract_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN subscription_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN quotation_id IS NULL THEN 0 ELSE 1 END) = 1");
                    table.CheckConstraint("ck_payment_attempt_item_type", "item_type IN ('CONTRACT', 'MAINTENANCE_PACKAGE', 'DISASTER_PACKAGE', 'REPAIR_QUOTATION', 'MAINTENANCE_QUOTATION')");
                    table.ForeignKey(
                        name: "fk_payment_attempt_item_attempt",
                        column: x => x.payment_attempt_id,
                        principalTable: "payment_attempt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_attempt_item_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_attempt_item_quotation",
                        column: x => x.quotation_id,
                        principalTable: "service_quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_attempt_item_subscription",
                        column: x => x.subscription_id,
                        principalTable: "service_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refund_record",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    original_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    approved_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refund_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refund_record", x => x.id);
                    table.CheckConstraint("ck_refund_record_amounts", "original_amount >= 0 AND (approved_amount IS NULL OR approved_amount BETWEEN 0 AND original_amount)");
                    table.CheckConstraint("ck_refund_record_refunded", "status <> 'REFUNDED' OR (refunded_at IS NOT NULL AND recorded_by_user_id IS NOT NULL AND approved_amount IS NOT NULL AND approved_by_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_refund_record_status", "status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'REFUNDED')");
                    table.ForeignKey(
                        name: "fk_refund_record_approved_by",
                        column: x => x.approved_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_record_customer",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_record_payment_attempt",
                        column: x => x.payment_attempt_id,
                        principalTable: "payment_attempt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_record_quotation",
                        column: x => x.quotation_id,
                        principalTable: "service_quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_record_recorded_by",
                        column: x => x.recorded_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_record_request",
                        column: x => x.request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refund_record_subscription",
                        column: x => x.subscription_id,
                        principalTable: "service_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    diagnosis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    required_parts = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    coverage = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_supplementary = table.Column<bool>(type: "boolean", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    coverage_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    work_result = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    parts_used = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_item", x => x.id);
                    table.CheckConstraint("ck_service_item_coverage", "coverage IN ('PENDING', 'INSTALLATION_WARRANTY', 'REPAIR_WARRANTY', 'DISASTER_PROTECTION', 'CHARGEABLE')");
                    table.CheckConstraint("ck_service_item_status", "status IN ('OPEN', 'QUOTED', 'AUTHORIZED', 'DECLINED', 'DONE')");
                    table.ForeignKey(
                        name: "fk_service_item_asset",
                        column: x => x.asset_id,
                        principalTable: "installed_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_item_quotation",
                        column: x => x.quotation_id,
                        principalTable: "service_quotation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_item_reported_by",
                        column: x => x.reported_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_item_request",
                        column: x => x.request_id,
                        principalTable: "service_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_installed_asset_replaces_asset_id",
                table: "installed_asset",
                column: "replaces_asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_installed_asset_service_request_id",
                table: "installed_asset",
                column: "service_request_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_installed_asset_status",
                table: "installed_asset",
                sql: "status IN ('ACTIVE', 'PENDING_ACCEPTANCE', 'REPLACED')");

            migrationBuilder.CreateIndex(
                name: "ux_installation_assignment_current",
                table: "installation_assignment",
                column: "project_id",
                unique: true,
                filter: "status IN ('PENDING', 'ACCEPTED') AND service_visit_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_installation_assignment_current_visit",
                table: "installation_assignment",
                column: "service_visit_id",
                unique: true,
                filter: "status IN ('PENDING', 'ACCEPTED') AND service_visit_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference",
                sql: "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT', 'SERVICE_EVIDENCE')");

            migrationBuilder.CreateIndex(
                name: "IX_after_sales_event_actor_user_id",
                table: "after_sales_event",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_after_sales_event_subject",
                table: "after_sales_event",
                columns: new[] { "subject_type", "subject_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_after_sales_notification_recipient",
                table: "after_sales_notification",
                columns: new[] { "recipient_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_after_sales_notification_request_id",
                table: "after_sales_notification",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_after_sales_notification_subscription_id",
                table: "after_sales_notification",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_disaster_claim_decided_by_user_id",
                table: "disaster_claim",
                column: "decided_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_disaster_claim_subscription",
                table: "disaster_claim",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ux_disaster_claim_request",
                table: "disaster_claim",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_entitlement_current_request_id",
                table: "maintenance_entitlement",
                column: "current_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_entitlement_status_period",
                table: "maintenance_entitlement",
                columns: new[] { "status", "period_starts_at" });

            migrationBuilder.CreateIndex(
                name: "ux_maintenance_entitlement_subscription_month",
                table: "maintenance_entitlement",
                columns: new[] { "subscription_id", "plan_month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempt_item_attempt",
                table: "payment_attempt_item",
                column: "payment_attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempt_item_contract_id",
                table: "payment_attempt_item",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempt_item_quotation",
                table: "payment_attempt_item",
                column: "quotation_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempt_item_subscription",
                table: "payment_attempt_item",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_record_approved_by_user_id",
                table: "refund_record",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_record_customer_user_id",
                table: "refund_record",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_record_payment_attempt_id",
                table: "refund_record",
                column: "payment_attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_record_quotation_id",
                table: "refund_record",
                column: "quotation_id");

            migrationBuilder.CreateIndex(
                name: "IX_refund_record_recorded_by_user_id",
                table: "refund_record",
                column: "recorded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_record_refunded_at",
                table: "refund_record",
                column: "refunded_at");

            migrationBuilder.CreateIndex(
                name: "IX_refund_record_request_id",
                table: "refund_record",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_refund_record_status_created_at",
                table: "refund_record",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_item_attempt_contract",
                table: "payment_attempt_item",
                columns: new[] { "payment_attempt_id", "contract_id" },
                unique: true,
                filter: "contract_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_item_attempt_quotation",
                table: "payment_attempt_item",
                columns: new[] { "payment_attempt_id", "quotation_id" },
                unique: true,
                filter: "quotation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_item_attempt_subscription",
                table: "payment_attempt_item",
                columns: new[] { "payment_attempt_id", "subscription_id" },
                unique: true,
                filter: "subscription_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_item_settled_quotation",
                table: "payment_attempt_item",
                column: "quotation_id",
                unique: true,
                filter: "quotation_id IS NOT NULL AND status = 'SETTLED'");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_item_settled_subscription",
                table: "payment_attempt_item",
                column: "subscription_id",
                unique: true,
                filter: "subscription_id IS NOT NULL AND status = 'SETTLED'");

            migrationBuilder.CreateIndex(
                name: "IX_refund_record_subscription_id",
                table: "refund_record",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_task_assignee_status",
                table: "sales_task",
                columns: new[] { "assigned_sales_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_sales_task_customer_user_id",
                table: "sales_task",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_task_status_created_at",
                table: "sales_task",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_task_open_entitlement",
                table: "sales_task",
                columns: new[] { "entitlement_id", "task_type" },
                unique: true,
                filter: "entitlement_id IS NOT NULL AND status IN ('OPEN', 'CLAIMED', 'IN_PROGRESS')");

            migrationBuilder.CreateIndex(
                name: "ux_sales_task_open_request",
                table: "sales_task",
                columns: new[] { "request_id", "task_type" },
                unique: true,
                filter: "request_id IS NOT NULL AND status IN ('OPEN', 'CLAIMED', 'IN_PROGRESS')");

            migrationBuilder.CreateIndex(
                name: "IX_service_evidence_added_by_user_id",
                table: "service_evidence",
                column: "added_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_evidence_request",
                table: "service_evidence",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_evidence_visit_id",
                table: "service_evidence",
                column: "visit_id");

            migrationBuilder.CreateIndex(
                name: "ux_service_evidence_file",
                table: "service_evidence",
                column: "file_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_item_asset_id",
                table: "service_item",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_item_quotation_id",
                table: "service_item",
                column: "quotation_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_item_reported_by_user_id",
                table: "service_item",
                column: "reported_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_item_request",
                table: "service_item",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_package_kind_active",
                table: "service_package",
                columns: new[] { "kind", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_service_quotation_current_payment_attempt_id",
                table: "service_quotation",
                column: "current_payment_attempt_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_quotation_disaster_claim_id",
                table: "service_quotation",
                column: "disaster_claim_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_quotation_sales_user_id",
                table: "service_quotation",
                column: "sales_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_quotation_status_expires_at",
                table: "service_quotation",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_service_quotation_supersedes_quotation_id",
                table: "service_quotation",
                column: "supersedes_quotation_id");

            migrationBuilder.CreateIndex(
                name: "ux_service_quotation_one_open",
                table: "service_quotation",
                column: "request_id",
                unique: true,
                filter: "status IN ('ISSUED', 'ACCEPTED')");

            migrationBuilder.CreateIndex(
                name: "ux_service_quotation_request_version",
                table: "service_quotation",
                columns: new[] { "request_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_request_customer_created_at",
                table: "service_request",
                columns: new[] { "customer_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_service_request_duplicate_of_request_id",
                table: "service_request",
                column: "duplicate_of_request_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_request_origin_request_id",
                table: "service_request",
                column: "origin_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_request_project_status",
                table: "service_request",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_service_request_property_site_id",
                table: "service_request",
                column: "property_site_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_request_status_updated_at",
                table: "service_request",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_service_request_suspected_asset_id",
                table: "service_request",
                column: "suspected_asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_request_warranty_source_request_id",
                table: "service_request",
                column: "warranty_source_request_id");

            migrationBuilder.CreateIndex(
                name: "ux_service_request_live_entitlement",
                table: "service_request",
                column: "maintenance_entitlement_id",
                unique: true,
                filter: "maintenance_entitlement_id IS NOT NULL AND status NOT IN ('COMPLETED', 'NOT_COMPLETED', 'CANCELLED', 'CLOSED_DUPLICATE')");

            migrationBuilder.CreateIndex(
                name: "ux_service_request_number",
                table: "service_request",
                column: "request_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_subscription_contract_kind_status",
                table: "service_subscription",
                columns: new[] { "contract_id", "kind", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_service_subscription_current_payment_attempt_id",
                table: "service_subscription",
                column: "current_payment_attempt_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_subscription_customer_created_at",
                table: "service_subscription",
                columns: new[] { "customer_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_service_subscription_package_id",
                table: "service_subscription",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_subscription_project_id",
                table: "service_subscription",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_service_subscription_renews_subscription_id",
                table: "service_subscription",
                column: "renews_subscription_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_subscription_status_starts_at",
                table: "service_subscription",
                columns: new[] { "status", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "IX_service_visit_assigned_by_user_id",
                table: "service_visit",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_visit_request",
                table: "service_visit",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_visit_technician_starts_at",
                table: "service_visit",
                columns: new[] { "technician_user_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ux_warranty_option_kind_months",
                table: "warranty_option",
                columns: new[] { "kind", "months" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_warranty_option_one_default",
                table: "warranty_option",
                column: "kind",
                unique: true,
                filter: "is_default");

            migrationBuilder.AddForeignKey(
                name: "fk_installation_assignment_service_visit",
                table: "installation_assignment",
                column: "service_visit_id",
                principalTable: "service_visit",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_installed_asset_replaces",
                table: "installed_asset",
                column: "replaces_asset_id",
                principalTable: "installed_asset",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_installed_asset_service_request",
                table: "installed_asset",
                column: "service_request_id",
                principalTable: "service_request",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_maintenance_entitlement_current_request",
                table: "maintenance_entitlement",
                column: "current_request_id",
                principalTable: "service_request",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_service_quotation_current_payment_attempt",
                table: "service_quotation",
                column: "current_payment_attempt_id",
                principalTable: "payment_attempt",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_service_subscription_current_payment_attempt",
                table: "service_subscription",
                column: "current_payment_attempt_id",
                principalTable: "payment_attempt",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(UpManualSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);
            migrationBuilder.Sql(DownManualSql);

            // Breaks the maintenance_entitlement <-> service_request cycle before the tables are dropped.
            migrationBuilder.DropForeignKey(
                name: "fk_maintenance_entitlement_current_request",
                table: "maintenance_entitlement");

            migrationBuilder.DropForeignKey(
                name: "fk_installation_assignment_service_visit",
                table: "installation_assignment");

            migrationBuilder.DropForeignKey(
                name: "fk_installed_asset_replaces",
                table: "installed_asset");

            migrationBuilder.DropForeignKey(
                name: "fk_installed_asset_service_request",
                table: "installed_asset");

            migrationBuilder.DropTable(
                name: "after_sales_event");

            migrationBuilder.DropTable(
                name: "after_sales_notification");

            migrationBuilder.DropTable(
                name: "payment_attempt_item");

            migrationBuilder.DropTable(
                name: "refund_record");

            migrationBuilder.DropTable(
                name: "sales_task");

            migrationBuilder.DropTable(
                name: "service_evidence");

            migrationBuilder.DropTable(
                name: "service_item");

            migrationBuilder.DropTable(
                name: "warranty_option");

            migrationBuilder.DropTable(
                name: "service_visit");

            migrationBuilder.DropTable(
                name: "service_quotation");

            migrationBuilder.DropTable(
                name: "disaster_claim");

            migrationBuilder.DropTable(
                name: "service_request");

            migrationBuilder.DropTable(
                name: "maintenance_entitlement");

            migrationBuilder.DropTable(
                name: "service_subscription");

            migrationBuilder.DropTable(
                name: "service_package");

            migrationBuilder.DropIndex(
                name: "IX_installed_asset_replaces_asset_id",
                table: "installed_asset");

            migrationBuilder.DropIndex(
                name: "IX_installed_asset_service_request_id",
                table: "installed_asset");

            migrationBuilder.DropCheckConstraint(
                name: "ck_installed_asset_status",
                table: "installed_asset");

            migrationBuilder.DropIndex(
                name: "ux_installation_assignment_current",
                table: "installation_assignment");

            migrationBuilder.DropIndex(
                name: "ux_installation_assignment_current_visit",
                table: "installation_assignment");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference");

            migrationBuilder.DropColumn(
                name: "replaces_asset_id",
                table: "installed_asset");

            migrationBuilder.DropColumn(
                name: "service_request_id",
                table: "installed_asset");

            migrationBuilder.DropColumn(
                name: "specifications",
                table: "installed_asset");

            migrationBuilder.DropColumn(
                name: "status",
                table: "installed_asset");

            migrationBuilder.DropColumn(
                name: "service_visit_id",
                table: "installation_assignment");

            migrationBuilder.DropColumn(
                name: "warranty_months",
                table: "customer_contract");

            // No default: the original columns had none, and DownGuardSql has already refused any NULL contract_id.
            migrationBuilder.AlterColumn<Guid>(
                name: "contract_id",
                table: "payment_event",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "contract_id",
                table: "payment_attempt",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_installation_assignment_current",
                table: "installation_assignment",
                column: "project_id",
                unique: true,
                filter: "status IN ('PENDING', 'ACCEPTED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference",
                sql: "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT')");
        }
    }
}
