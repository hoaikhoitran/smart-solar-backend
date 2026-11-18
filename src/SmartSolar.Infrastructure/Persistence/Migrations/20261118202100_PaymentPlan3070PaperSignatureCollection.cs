using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartSolar.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 30/70 payment plan (2026-11-19), additive: payment_plan / terms_version on customer_contract (NULL = legacy full payment, every existing row),
    /// contract_installment, contract_signature_set, contract_signed_page, contract_collection_case / _activity, contract_access_audit, the
    /// CONTRACT_INSTALLMENT payment line, customer acceptance columns and three file purposes. PostgreSQL triggers protect the plan against the older
    /// API still running during a rolling deploy and against manual SQL: a 30/70 contract can never get a 100% contract payment attempt, its status
    /// follows DRAFT → AWAITING_SIGNATURE (deposit PAID) → SIGNED (pages FINALIZED) → PAID (every installment PAID) or DRAFT → DISCARDED, and an
    /// installment is PAID only with exactly one SETTLED line of its amount. Down refuses while any 30/70 data exists (nothing is changed); the only
    /// information Down drops without refusing is installation_acceptance.decided_by_role (a label also derivable from decided_by_user_id).
    /// </summary>
    public partial class PaymentPlan3070PaperSignatureCollection : Migration
    {
        public const string GuardsSql = """
            CREATE OR REPLACE FUNCTION ss_payment_plan_no_full_contract_payment() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.contract_id IS NOT NULL AND EXISTS (SELECT 1 FROM customer_contract c WHERE c.id = NEW.contract_id AND c.payment_plan IS NOT NULL) THEN
                    RAISE EXCEPTION 'contract % is paid in installments (30/70 plan); a 100%% contract payment attempt is not allowed', NEW.contract_id
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_plan_no_full_contract_payment';
                END IF;
                RETURN NEW;
            END $$;

            CREATE TRIGGER trg_payment_attempt_payment_plan_guard BEFORE INSERT OR UPDATE OF contract_id ON payment_attempt
                FOR EACH ROW EXECUTE FUNCTION ss_payment_plan_no_full_contract_payment();

            CREATE OR REPLACE FUNCTION ss_payment_plan_contract_transition() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.payment_plan IS DISTINCT FROM OLD.payment_plan THEN
                    RAISE EXCEPTION 'the payment plan of contract % cannot change', OLD.id USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_plan_contract_transition';
                END IF;
                IF NEW.payment_plan IS NULL OR NEW.status = OLD.status THEN
                    RETURN NULL;
                END IF;
                IF NOT ((OLD.status = 'DRAFT' AND NEW.status IN ('AWAITING_SIGNATURE', 'DISCARDED'))
                        OR (OLD.status = 'AWAITING_SIGNATURE' AND NEW.status = 'SIGNED')
                        OR (OLD.status = 'SIGNED' AND NEW.status = 'PAID')) THEN
                    RAISE EXCEPTION '30/70 contract %: % -> % is not allowed', OLD.id, OLD.status, NEW.status
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_plan_contract_transition';
                END IF;
                IF NEW.status = 'AWAITING_SIGNATURE' AND NOT EXISTS (SELECT 1 FROM contract_installment i WHERE i.contract_id = NEW.id AND i.kind = 'DEPOSIT' AND i.status = 'PAID') THEN
                    RAISE EXCEPTION '30/70 contract %: the deposit is not paid', NEW.id USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_plan_contract_transition';
                END IF;
                IF NEW.status = 'SIGNED' AND NOT EXISTS (SELECT 1 FROM contract_signature_set s WHERE s.contract_id = NEW.id AND s.status = 'FINALIZED') THEN
                    RAISE EXCEPTION '30/70 contract %: no finalized signed pages', NEW.id USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_plan_contract_transition';
                END IF;
                IF NEW.status = 'PAID' AND (NOT EXISTS (SELECT 1 FROM contract_installment i WHERE i.contract_id = NEW.id)
                        OR EXISTS (SELECT 1 FROM contract_installment i WHERE i.contract_id = NEW.id AND i.status <> 'PAID')) THEN
                    RAISE EXCEPTION '30/70 contract %: not every installment is paid', NEW.id USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_payment_plan_contract_transition';
                END IF;
                RETURN NULL;
            END $$;

            -- Deferred: checked at commit, after every row of the business step (installment, signature set, contract) was written.
            CREATE CONSTRAINT TRIGGER trg_customer_contract_payment_plan_transition AFTER UPDATE ON customer_contract
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ss_payment_plan_contract_transition();

            CREATE OR REPLACE FUNCTION ss_payment_plan_installment_settled() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.status = 'PAID' AND (SELECT count(*) FROM payment_attempt_item l JOIN payment_attempt a ON a.id = l.payment_attempt_id
                        WHERE l.contract_installment_id = NEW.id AND l.status = 'SETTLED' AND l.amount = NEW.amount AND a.status = 'PAID'
                          AND a.id = NEW.settled_payment_attempt_id) <> 1 THEN
                    RAISE EXCEPTION 'installment % is PAID without exactly one settled verified payment line of its amount', NEW.id
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_contract_installment_settled_by_payment';
                END IF;
                IF TG_OP = 'UPDATE' AND OLD.status = 'PAID' AND NEW.status <> 'PAID' THEN
                    RAISE EXCEPTION 'installment % was paid; it cannot become %', NEW.id, NEW.status
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_contract_installment_settled_by_payment';
                END IF;
                IF TG_OP = 'UPDATE' AND (NEW.amount <> OLD.amount OR NEW.contract_id <> OLD.contract_id OR NEW.sequence <> OLD.sequence) THEN
                    RAISE EXCEPTION 'installment % amount / contract / sequence are frozen', NEW.id
                        USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_contract_installment_settled_by_payment';
                END IF;
                RETURN NULL;
            END $$;

            CREATE CONSTRAINT TRIGGER trg_contract_installment_settled AFTER INSERT OR UPDATE ON contract_installment
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ss_payment_plan_installment_settled();
            """;

        public const string DropGuardsSql = """
            DROP TRIGGER IF EXISTS trg_contract_installment_settled ON contract_installment;
            DROP FUNCTION IF EXISTS ss_payment_plan_installment_settled();
            DROP TRIGGER IF EXISTS trg_customer_contract_payment_plan_transition ON customer_contract;
            DROP FUNCTION IF EXISTS ss_payment_plan_contract_transition();
            DROP TRIGGER IF EXISTS trg_payment_attempt_payment_plan_guard ON payment_attempt;
            DROP FUNCTION IF EXISTS ss_payment_plan_no_full_contract_payment();
            """;

        public const string DownGuardSql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM customer_contract WHERE payment_plan IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM contract_installment)
                   OR EXISTS (SELECT 1 FROM contract_signature_set)
                   OR EXISTS (SELECT 1 FROM contract_collection_case)
                   OR EXISTS (SELECT 1 FROM contract_access_audit)
                   OR EXISTS (SELECT 1 FROM payment_attempt_item WHERE contract_installment_id IS NOT NULL OR item_type = 'CONTRACT_INSTALLMENT')
                   OR EXISTS (SELECT 1 FROM installation_acceptance WHERE evidence_file_ids IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM file_reference WHERE purpose IN ('SIGNED_CONTRACT_PAGE', 'ACCEPTANCE_EVIDENCE', 'COLLECTION_EVIDENCE')) THEN
                    RAISE EXCEPTION '30/70 payment-plan data exists: export it and decide before rolling back (nothing was changed)';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_target",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_type",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_published_document",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_signed_review",
                table: "customer_contract");

            migrationBuilder.AddColumn<Guid>(
                name: "contract_installment_id",
                table: "payment_attempt_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decided_by_role",
                table: "installation_acceptance",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "evidence_file_ids",
                table: "installation_acceptance",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_plan",
                table: "customer_contract",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "terms_version",
                table: "customer_contract",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "contract_access_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_access_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "contract_installment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    paid_late = table.Column<bool>(type: "boolean", nullable: false),
                    current_payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settled_payment_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_installment", x => x.id);
                    table.CheckConstraint("ck_contract_installment_amount", "amount > 0 AND percent > 0 AND percent < 100");
                    table.CheckConstraint("ck_contract_installment_balance_due", "kind <> 'BALANCE' OR status NOT IN ('DUE', 'PAID') OR due_at IS NOT NULL");
                    table.CheckConstraint("ck_contract_installment_kind", "kind IN ('DEPOSIT', 'BALANCE')");
                    table.CheckConstraint("ck_contract_installment_opened", "status NOT IN ('DUE', 'PAID') OR opened_at IS NOT NULL");
                    table.CheckConstraint("ck_contract_installment_paid", "(status = 'PAID') = (paid_at IS NOT NULL AND paid_amount IS NOT NULL AND settled_payment_attempt_id IS NOT NULL)");
                    table.CheckConstraint("ck_contract_installment_paid_amount", "paid_amount IS NULL OR paid_amount = amount");
                    table.CheckConstraint("ck_contract_installment_sequence", "(sequence = 1 AND kind = 'DEPOSIT') OR (sequence = 2 AND kind = 'BALANCE')");
                    table.CheckConstraint("ck_contract_installment_status", "status IN ('PENDING', 'DUE', 'PAID', 'CANCELLED')");
                    table.ForeignKey(
                        name: "fk_contract_installment_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contract_signature_set",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    expected_page_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    signed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finalized_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_signature_set", x => x.id);
                    table.CheckConstraint("ck_contract_signature_set_finalized", "(status = 'FINALIZED') = (finalized_at IS NOT NULL AND finalized_by_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_contract_signature_set_pages", "expected_page_count BETWEEN 1 AND 50");
                    table.CheckConstraint("ck_contract_signature_set_status", "status IN ('UPLOADING', 'FINALIZED', 'SUPERSEDED')");
                    table.ForeignKey(
                        name: "fk_contract_signature_set_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_signature_set_created_by",
                        column: x => x.created_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_signature_set_finalized_by",
                        column: x => x.finalized_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contract_collection_case",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    amount_due = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    promised_payment_date = table.Column<DateOnly>(type: "date", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_collection_case", x => x.id);
                    table.CheckConstraint("ck_contract_collection_case_amount", "amount_due > 0");
                    table.CheckConstraint("ck_contract_collection_case_resolved", "(status = 'RESOLVED') = (resolved_at IS NOT NULL AND resolution IS NOT NULL)");
                    table.CheckConstraint("ck_contract_collection_case_status", "status IN ('OVERDUE', 'SALES_CONTACT_REQUIRED', 'CONTACTED', 'NEGOTIATION_IN_PROGRESS', 'AGREEMENT_RECORDED', 'ESCALATED_TO_MANAGER', 'LEGAL_REVIEW_REQUIRED', 'RESOLVED')");
                    table.ForeignKey(
                        name: "fk_contract_collection_case_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_collection_case_customer",
                        column: x => x.customer_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_collection_case_installment",
                        column: x => x.installment_id,
                        principalTable: "contract_installment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_collection_case_resolved_by",
                        column: x => x.resolved_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contract_signed_page",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    signature_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replaced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_signed_page", x => x.id);
                    table.CheckConstraint("ck_contract_signed_page_number", "page_number BETWEEN 1 AND 50");
                    table.ForeignKey(
                        name: "fk_contract_signed_page_contract",
                        column: x => x.contract_id,
                        principalTable: "customer_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_signed_page_file",
                        column: x => x.file_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_signed_page_set",
                        column: x => x.signature_set_id,
                        principalTable: "contract_signature_set",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_signed_page_uploaded_by",
                        column: x => x.uploaded_by_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "contract_collection_activity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    promised_payment_date = table.Column<DateOnly>(type: "date", nullable: true),
                    evidence_file_ids = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_collection_activity", x => x.id);
                    table.CheckConstraint("ck_contract_collection_activity_kind", "kind IN ('OPENED', 'CONTACT', 'NEGOTIATION', 'AGREEMENT', 'NOTE', 'ESCALATE', 'LEGAL_REVIEW', 'RESOLVE', 'PAID')");
                    table.ForeignKey(
                        name: "fk_contract_collection_activity_actor",
                        column: x => x.actor_user_id,
                        principalTable: "user_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contract_collection_activity_case",
                        column: x => x.case_id,
                        principalTable: "contract_collection_case",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempt_item_contract_installment",
                table: "payment_attempt_item",
                column: "contract_installment_id");

            migrationBuilder.CreateIndex(
                name: "ux_payment_attempt_item_settled_contract_installment",
                table: "payment_attempt_item",
                column: "contract_installment_id",
                unique: true,
                filter: "contract_installment_id IS NOT NULL AND status = 'SETTLED'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_contract_installment",
                table: "payment_attempt_item",
                sql: "(item_type = 'CONTRACT_INSTALLMENT') = (contract_installment_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_target",
                table: "payment_attempt_item",
                sql: "(CASE WHEN contract_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN subscription_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN quotation_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN installation_quotation_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN contract_installment_id IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_type",
                table: "payment_attempt_item",
                sql: "item_type IN ('CONTRACT', 'MAINTENANCE_PACKAGE', 'DISASTER_PACKAGE', 'REPAIR_QUOTATION', 'MAINTENANCE_QUOTATION', 'INSTALLATION_QUOTATION', 'CONTRACT_INSTALLMENT')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference",
                sql: "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT', 'SERVICE_EVIDENCE', 'PRODUCT_IMAGE', 'PACKAGE_IMAGE', 'PAYMENT_EVIDENCE', 'SIGNED_CONTRACT_PAGE', 'ACCEPTANCE_EVIDENCE', 'COLLECTION_EVIDENCE')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_payment_plan",
                table: "customer_contract",
                sql: "payment_plan IS NULL OR payment_plan IN ('DEPOSIT_30_BALANCE_70')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_published_document",
                table: "customer_contract",
                sql: "status IN ('DRAFT', 'ISSUED', 'SUPERSEDED', 'DISCARDED') OR (official_document_file_id IS NOT NULL AND official_document_sha256 IS NOT NULL) OR payment_plan IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_signed_review",
                table: "customer_contract",
                sql: "status NOT IN ('SIGNED', 'PAID') OR (signed_document_file_id IS NOT NULL AND reviewed_by_user_id IS NOT NULL AND signed_at IS NOT NULL) OR (payment_plan IS NOT NULL AND signed_at IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_contract_access_audit_actor",
                table: "contract_access_audit",
                columns: new[] { "actor_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_contract_access_audit_contract",
                table: "contract_access_audit",
                columns: new[] { "contract_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_collection_activity_actor_user_id",
                table: "contract_collection_activity",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_contract_collection_activity_case",
                table: "contract_collection_activity",
                columns: new[] { "case_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_contract_collection_case_contract",
                table: "contract_collection_case",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_contract_collection_case_customer_user_id",
                table: "contract_collection_case",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_contract_collection_case_resolved_by_user_id",
                table: "contract_collection_case",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_contract_collection_case_status_opened",
                table: "contract_collection_case",
                columns: new[] { "status", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ux_contract_collection_case_one_open",
                table: "contract_collection_case",
                column: "installment_id",
                unique: true,
                filter: "status <> 'RESOLVED'");

            migrationBuilder.CreateIndex(
                name: "ix_contract_installment_status_due",
                table: "contract_installment",
                columns: new[] { "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ux_contract_installment_contract_sequence",
                table: "contract_installment",
                columns: new[] { "contract_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contract_signature_set_created_by_user_id",
                table: "contract_signature_set",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_contract_signature_set_finalized_by_user_id",
                table: "contract_signature_set",
                column: "finalized_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_contract_signature_set_contract_version",
                table: "contract_signature_set",
                columns: new[] { "contract_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_contract_signature_set_one_current",
                table: "contract_signature_set",
                column: "contract_id",
                unique: true,
                filter: "status <> 'SUPERSEDED'");

            migrationBuilder.CreateIndex(
                name: "ix_contract_signed_page_contract",
                table: "contract_signed_page",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_contract_signed_page_uploaded_by_user_id",
                table: "contract_signed_page",
                column: "uploaded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_contract_signed_page_current",
                table: "contract_signed_page",
                columns: new[] { "signature_set_id", "page_number" },
                unique: true,
                filter: "replaced_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_contract_signed_page_file",
                table: "contract_signed_page",
                column: "file_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_payment_attempt_item_contract_installment",
                table: "payment_attempt_item",
                column: "contract_installment_id",
                principalTable: "contract_installment",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(GuardsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownGuardSql);
            migrationBuilder.Sql(DropGuardsSql);
            migrationBuilder.DropForeignKey(
                name: "fk_payment_attempt_item_contract_installment",
                table: "payment_attempt_item");

            migrationBuilder.DropTable(
                name: "contract_access_audit");

            migrationBuilder.DropTable(
                name: "contract_collection_activity");

            migrationBuilder.DropTable(
                name: "contract_signed_page");

            migrationBuilder.DropTable(
                name: "contract_collection_case");

            migrationBuilder.DropTable(
                name: "contract_signature_set");

            migrationBuilder.DropTable(
                name: "contract_installment");

            migrationBuilder.DropIndex(
                name: "ix_payment_attempt_item_contract_installment",
                table: "payment_attempt_item");

            migrationBuilder.DropIndex(
                name: "ux_payment_attempt_item_settled_contract_installment",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_contract_installment",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_target",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_attempt_item_type",
                table: "payment_attempt_item");

            migrationBuilder.DropCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_payment_plan",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_published_document",
                table: "customer_contract");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_contract_signed_review",
                table: "customer_contract");

            migrationBuilder.DropColumn(
                name: "contract_installment_id",
                table: "payment_attempt_item");

            migrationBuilder.DropColumn(
                name: "decided_by_role",
                table: "installation_acceptance");

            migrationBuilder.DropColumn(
                name: "evidence_file_ids",
                table: "installation_acceptance");

            migrationBuilder.DropColumn(
                name: "payment_plan",
                table: "customer_contract");

            migrationBuilder.DropColumn(
                name: "terms_version",
                table: "customer_contract");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_target",
                table: "payment_attempt_item",
                sql: "(CASE WHEN contract_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN subscription_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN quotation_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN installation_quotation_id IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_attempt_item_type",
                table: "payment_attempt_item",
                sql: "item_type IN ('CONTRACT', 'MAINTENANCE_PACKAGE', 'DISASTER_PACKAGE', 'REPAIR_QUOTATION', 'MAINTENANCE_QUOTATION', 'INSTALLATION_QUOTATION')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_file_reference_purpose",
                table: "file_reference",
                sql: "purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT', 'SERVICE_EVIDENCE', 'PRODUCT_IMAGE', 'PACKAGE_IMAGE', 'PAYMENT_EVIDENCE')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_published_document",
                table: "customer_contract",
                sql: "status IN ('DRAFT', 'ISSUED', 'SUPERSEDED', 'DISCARDED') OR (official_document_file_id IS NOT NULL AND official_document_sha256 IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_contract_signed_review",
                table: "customer_contract",
                sql: "status NOT IN ('SIGNED', 'PAID') OR (signed_document_file_id IS NOT NULL AND reviewed_by_user_id IS NOT NULL AND signed_at IS NOT NULL)");
        }
    }
}
