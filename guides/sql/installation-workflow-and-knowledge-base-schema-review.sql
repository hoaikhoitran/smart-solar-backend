-- =====================================================================================
-- SmartSolar — Installation Flow (on-site survey, quotation, contract, payment, installation) + knowledge base (RAG)
-- SCHEMA REVIEW SCRIPT
--
-- STATUS: PENDING_MANUAL_MIGRATION
--   * Review only. It has NOT been executed against Supabase or any shared/production database.
--   * The EF migration 20261013182132_InstallationWorkflowAndKnowledgeBase implements exactly this script
--     (checked by Unit/Persistence/InstallationWorkflowMigrationTests). Superseded path, kept for reference:
--     review this script, then have the
--     owner create the EF migration (dotnet ef migrations add InstallationWorkflowAndKnowledgeBase) and apply it, or apply
--     this script manually, followed by the manual section at the bottom.
--   * Only NEW tables are created. No existing table, column, index or constraint is altered.
--   * Until it is applied, keep Features__InstallationWorkflowEnabled / Features__KnowledgeBaseEnabled false: their endpoints answer
--     503 FEATURE_PENDING_MIGRATION and Flow 1 never queries these tables.
--
-- The generated section is produced from the EF Core model (PostgreSQL provider) and is checked
-- for drift by tests/SmartSolar.Tests/Unit/Persistence/PendingSchemaReviewTests.cs.
-- The pgvector chunk table is owned by the Python RAG service: src/SmartSolar.RagService/sql/knowledge_chunk.sql
-- (apply it after this script, in the same approved change).
--
-- Suggested order: BEGIN; <generated>; <manual>; COMMIT;
-- =====================================================================================

-- <generated-from-ef-model>
CREATE TABLE payment_webhook_event (
    id uuid NOT NULL,
    provider character varying(20) NOT NULL,
    payload_sha256 character varying(64) NOT NULL,
    order_code bigint,
    provider_reference character varying(100),
    amount numeric(18,2),
    code character varying(10),
    signature_valid boolean NOT NULL,
    outcome character varying(20) NOT NULL,
    detail character varying(300),
    received_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_payment_webhook_event" PRIMARY KEY (id),
    CONSTRAINT ck_payment_webhook_event_outcome CHECK (outcome IN ('APPLIED', 'DUPLICATE', 'IGNORED', 'REJECTED'))
);

CREATE TABLE rag_usage_daily (
    user_id uuid NOT NULL,
    usage_date date NOT NULL,
    requests integer NOT NULL,
    input_tokens bigint NOT NULL,
    output_tokens bigint NOT NULL,
    estimated_tokens bigint NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_rag_usage_daily" PRIMARY KEY (user_id, usage_date),
    CONSTRAINT ck_rag_usage_daily_non_negative CHECK (requests >= 0 AND input_tokens >= 0 AND output_tokens >= 0 AND estimated_tokens >= 0)
);

CREATE TABLE chat_session (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    title character varying(100) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_chat_session" PRIMARY KEY (id),
    CONSTRAINT "FK_chat_session_user_account_user_id" FOREIGN KEY (user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE file_reference (
    id uuid NOT NULL,
    purpose character varying(40) NOT NULL,
    provider character varying(20) NOT NULL,
    bucket character varying(100) NOT NULL,
    object_key character varying(512) NOT NULL,
    original_file_name character varying(255) NOT NULL,
    declared_content_type character varying(100) NOT NULL,
    declared_size_bytes bigint NOT NULL,
    declared_checksum_sha256 character varying(64),
    object_version_id character varying(255),
    etag character varying(255),
    verified_content_type character varying(100),
    verified_size_bytes bigint,
    verified_checksum_sha256 character varying(64),
    status character varying(30) NOT NULL,
    rejection_reason character varying(300),
    verification_attempts integer NOT NULL,
    uploaded_by_user_id uuid NOT NULL,
    verified_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_file_reference" PRIMARY KEY (id),
    CONSTRAINT ck_file_reference_checksum_format CHECK ((verified_checksum_sha256 IS NULL OR (length(verified_checksum_sha256) = 64 AND verified_checksum_sha256 = lower(verified_checksum_sha256))) AND (declared_checksum_sha256 IS NULL OR (length(declared_checksum_sha256) = 64 AND declared_checksum_sha256 = lower(declared_checksum_sha256)))),
    CONSTRAINT ck_file_reference_purpose CHECK (purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT')),
    CONSTRAINT ck_file_reference_sizes CHECK (declared_size_bytes > 0 AND (verified_size_bytes IS NULL OR verified_size_bytes > 0)),
    CONSTRAINT ck_file_reference_status CHECK (status IN ('PENDING_VERIFICATION', 'VERIFIED', 'REJECTED')),
    CONSTRAINT ck_file_reference_verified_complete CHECK (status <> 'VERIFIED' OR (verified_content_type IS NOT NULL AND verified_size_bytes IS NOT NULL AND verified_at IS NOT NULL AND verified_checksum_sha256 IS NOT NULL)),
    CONSTRAINT "FK_file_reference_user_account_uploaded_by_user_id" FOREIGN KEY (uploaded_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE rag_usage_reservation (
    id uuid NOT NULL,
    user_id uuid NOT NULL,
    usage_date date NOT NULL,
    reserved_tokens bigint NOT NULL,
    created_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_rag_usage_reservation" PRIMARY KEY (id),
    CONSTRAINT ck_rag_usage_reservation_lease CHECK (expires_at > created_at),
    CONSTRAINT ck_rag_usage_reservation_tokens CHECK (reserved_tokens > 0),
    CONSTRAINT "FK_rag_usage_reservation_user_account_user_id" FOREIGN KEY (user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE chat_message (
    id uuid NOT NULL,
    session_id uuid NOT NULL,
    role character varying(20) NOT NULL,
    content character varying(20000) NOT NULL,
    status character varying(30) NOT NULL,
    client_message_id character varying(64),
    reply_to_message_id uuid,
    prompt_tokens integer,
    completion_tokens integer,
    model character varying(100),
    latency_ms integer,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_chat_message" PRIMARY KEY (id),
    CONSTRAINT ck_chat_message_role CHECK (role IN ('USER', 'ASSISTANT')),
    CONSTRAINT ck_chat_message_status CHECK (status IN ('COMPLETED', 'INSUFFICIENT_CONTEXT', 'FAILED')),
    CONSTRAINT "FK_chat_message_chat_message_reply_to_message_id" FOREIGN KEY (reply_to_message_id) REFERENCES chat_message (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_chat_message_chat_session_session_id" FOREIGN KEY (session_id) REFERENCES chat_session (id) ON DELETE RESTRICT
);

CREATE TABLE chat_citation (
    id uuid NOT NULL,
    message_id uuid NOT NULL,
    rank integer NOT NULL,
    document_id uuid NOT NULL,
    version_id uuid NOT NULL,
    chunk_id character varying(100) NOT NULL,
    document_title character varying(200) NOT NULL,
    version_number integer NOT NULL,
    page_number integer,
    section character varying(200),
    score double precision NOT NULL,
    snippet character varying(500),
    CONSTRAINT "PK_chat_citation" PRIMARY KEY (id),
    CONSTRAINT "FK_chat_citation_chat_message_message_id" FOREIGN KEY (message_id) REFERENCES chat_message (id) ON DELETE RESTRICT
);

CREATE TABLE contract_event (
    id uuid NOT NULL,
    contract_id uuid NOT NULL,
    event_type character varying(40) NOT NULL,
    from_status character varying(30),
    to_status character varying(30),
    actor_user_id uuid NOT NULL,
    detail character varying(500),
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_contract_event" PRIMARY KEY (id),
    CONSTRAINT "FK_contract_event_user_account_actor_user_id" FOREIGN KEY (actor_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE contract_signature_submission (
    id uuid NOT NULL,
    contract_id uuid NOT NULL,
    file_id uuid NOT NULL,
    file_sha256 character varying(64) NOT NULL,
    official_document_sha256 character varying(64) NOT NULL,
    submitted_by_user_id uuid NOT NULL,
    submitted_at timestamp with time zone NOT NULL,
    outcome character varying(20) NOT NULL,
    reviewed_by_user_id uuid,
    reviewed_at timestamp with time zone,
    rejection_reason character varying(2000),
    CONSTRAINT "PK_contract_signature_submission" PRIMARY KEY (id),
    CONSTRAINT ck_contract_signature_submission_outcome CHECK (outcome IN ('PENDING', 'APPROVED', 'REJECTED')),
    CONSTRAINT "FK_contract_signature_submission_file_reference_file_id" FOREIGN KEY (file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_contract_signature_submission_user_account_reviewed_by_user~" FOREIGN KEY (reviewed_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_contract_signature_submission_user_account_submitted_by_use~" FOREIGN KEY (submitted_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE customer_contract (
    id uuid NOT NULL,
    contract_number character varying(30) NOT NULL,
    version integer NOT NULL,
    previous_contract_id uuid,
    quotation_id uuid NOT NULL,
    quotation_version integer NOT NULL,
    survey_request_id uuid NOT NULL,
    customer_user_id uuid NOT NULL,
    sales_user_id uuid NOT NULL,
    status character varying(30) NOT NULL,
    currency character varying(3) NOT NULL,
    total_amount numeric(18,2) NOT NULL,
    pricing_snapshot jsonb NOT NULL,
    customer_snapshot jsonb NOT NULL,
    terms character varying(8000),
    canonical_hash character varying(64),
    official_document_file_id uuid,
    official_document_sha256 character varying(64),
    official_document_confirmed_by_user_id uuid,
    official_document_confirmed_at timestamp with time zone,
    signed_document_file_id uuid,
    signed_document_sha256 character varying(64),
    signed_submitted_at timestamp with time zone,
    reviewed_by_user_id uuid,
    reviewed_at timestamp with time zone,
    rejection_reason character varying(2000),
    issued_at timestamp with time zone,
    published_at timestamp with time zone,
    signed_at timestamp with time zone,
    paid_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_customer_contract" PRIMARY KEY (id),
    CONSTRAINT ck_customer_contract_amount CHECK (total_amount > 0 AND currency = 'VND'),
    CONSTRAINT ck_customer_contract_issued_hash CHECK (status = 'DRAFT' OR (canonical_hash IS NOT NULL AND issued_at IS NOT NULL)),
    CONSTRAINT ck_customer_contract_published_document CHECK (status IN ('DRAFT', 'ISSUED', 'SUPERSEDED') OR (official_document_file_id IS NOT NULL AND official_document_sha256 IS NOT NULL)),
    CONSTRAINT ck_customer_contract_signed_review CHECK (status NOT IN ('SIGNED', 'PAID') OR (signed_document_file_id IS NOT NULL AND reviewed_by_user_id IS NOT NULL AND signed_at IS NOT NULL)),
    CONSTRAINT ck_customer_contract_status CHECK (status IN ('DRAFT', 'ISSUED', 'AWAITING_SIGNATURE', 'UNDER_REVIEW', 'SIGNATURE_REJECTED', 'SIGNED', 'PAID', 'SUPERSEDED')),
    CONSTRAINT ck_customer_contract_version CHECK (version >= 1),
    CONSTRAINT "FK_customer_contract_customer_contract_previous_contract_id" FOREIGN KEY (previous_contract_id) REFERENCES customer_contract (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_customer_contract_file_reference_official_document_file_id" FOREIGN KEY (official_document_file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_customer_contract_file_reference_signed_document_file_id" FOREIGN KEY (signed_document_file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_customer_contract_user_account_customer_user_id" FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_customer_contract_user_account_official_document_confirmed_~" FOREIGN KEY (official_document_confirmed_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_customer_contract_user_account_reviewed_by_user_id" FOREIGN KEY (reviewed_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_customer_contract_user_account_sales_user_id" FOREIGN KEY (sales_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE payment_attempt (
    id uuid NOT NULL,
    contract_id uuid NOT NULL,
    customer_user_id uuid NOT NULL,
    provider character varying(20) NOT NULL,
    order_code bigint NOT NULL,
    amount numeric(18,2) NOT NULL,
    currency character varying(3) NOT NULL,
    description character varying(25) NOT NULL,
    status character varying(30) NOT NULL,
    provider_payment_link_id character varying(100),
    checkout_url character varying(500),
    qr_code character varying(1000),
    expires_at timestamp with time zone NOT NULL,
    provider_reference character varying(100),
    amount_paid numeric(18,2),
    provider_transaction_at timestamp with time zone,
    paid_at timestamp with time zone,
    failure_reason character varying(300),
    last_reconciled_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_payment_attempt" PRIMARY KEY (id),
    CONSTRAINT ck_payment_attempt_amount CHECK (amount > 0 AND currency = 'VND'),
    CONSTRAINT ck_payment_attempt_order_code CHECK (order_code > 0 AND order_code < 9007199254740991),
    CONSTRAINT ck_payment_attempt_paid_fields CHECK (status <> 'PAID' OR (paid_at IS NOT NULL AND amount_paid = amount)),
    CONSTRAINT ck_payment_attempt_status CHECK (status IN ('PENDING', 'PROCESSING', 'PAID', 'CANCELLED', 'EXPIRED', 'FAILED', 'REVIEW_REQUIRED', 'REFUND_REQUIRED')),
    CONSTRAINT "FK_payment_attempt_customer_contract_contract_id" FOREIGN KEY (contract_id) REFERENCES customer_contract (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_payment_attempt_user_account_customer_user_id" FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE payment_event (
    id uuid NOT NULL,
    payment_attempt_id uuid NOT NULL,
    contract_id uuid NOT NULL,
    event_type character varying(40) NOT NULL,
    from_status character varying(30),
    to_status character varying(30),
    source character varying(20) NOT NULL,
    detail character varying(300),
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_payment_event" PRIMARY KEY (id),
    CONSTRAINT "FK_payment_event_customer_contract_contract_id" FOREIGN KEY (contract_id) REFERENCES customer_contract (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_payment_event_payment_attempt_payment_attempt_id" FOREIGN KEY (payment_attempt_id) REFERENCES payment_attempt (id) ON DELETE RESTRICT
);

CREATE TABLE installation_acceptance (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    round integer NOT NULL,
    requested_by_user_id uuid NOT NULL,
    requested_at timestamp with time zone NOT NULL,
    completion_report character varying(8000) NOT NULL,
    decision character varying(30) NOT NULL,
    decided_by_user_id uuid,
    decided_at timestamp with time zone,
    reason character varying(2000),
    CONSTRAINT "PK_installation_acceptance" PRIMARY KEY (id),
    CONSTRAINT ck_installation_acceptance_decided CHECK ((decision = 'PENDING') = (decided_at IS NULL)),
    CONSTRAINT ck_installation_acceptance_decision CHECK (decision IN ('PENDING', 'ACCEPTED', 'REWORK_REQUESTED')),
    CONSTRAINT "FK_installation_acceptance_user_account_decided_by_user_id" FOREIGN KEY (decided_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_acceptance_user_account_requested_by_user_id" FOREIGN KEY (requested_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE installation_checklist_item (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    code character varying(50) NOT NULL,
    title character varying(200) NOT NULL,
    is_required boolean NOT NULL,
    status character varying(20) NOT NULL,
    evidence_file_id uuid,
    note character varying(1000),
    verified_by_user_id uuid,
    verified_at timestamp with time zone,
    CONSTRAINT "PK_installation_checklist_item" PRIMARY KEY (id),
    CONSTRAINT ck_installation_checklist_item_evidence CHECK (status <> 'SATISFIED' OR evidence_file_id IS NOT NULL),
    CONSTRAINT ck_installation_checklist_item_status CHECK (status IN ('PENDING', 'SATISFIED', 'NOT_APPLICABLE')),
    CONSTRAINT "FK_installation_checklist_item_file_reference_evidence_file_id" FOREIGN KEY (evidence_file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_checklist_item_user_account_verified_by_user_id" FOREIGN KEY (verified_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE installation_event (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    event_type character varying(40) NOT NULL,
    actor_user_id uuid NOT NULL,
    detail character varying(500),
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_installation_event" PRIMARY KEY (id),
    CONSTRAINT "FK_installation_event_user_account_actor_user_id" FOREIGN KEY (actor_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE installation_milestone (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    sort_order integer NOT NULL,
    code character varying(40) NOT NULL,
    name character varying(100) NOT NULL,
    description character varying(1000),
    weight_percent integer NOT NULL,
    is_mandatory boolean NOT NULL,
    status character varying(20) NOT NULL,
    progress_percent integer NOT NULL,
    notes character varying(2000),
    rework_round integer NOT NULL,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    completed_by_user_id uuid,
    revision integer NOT NULL,
    CONSTRAINT "PK_installation_milestone" PRIMARY KEY (id),
    CONSTRAINT ck_installation_milestone_completed CHECK (status <> 'COMPLETED' OR (completed_at IS NOT NULL AND completed_by_user_id IS NOT NULL)),
    CONSTRAINT ck_installation_milestone_numbers CHECK (weight_percent BETWEEN 0 AND 100 AND progress_percent BETWEEN 0 AND 100 AND rework_round >= 0),
    CONSTRAINT ck_installation_milestone_status CHECK (status IN ('NOT_STARTED', 'IN_PROGRESS', 'COMPLETED')),
    CONSTRAINT "FK_installation_milestone_user_account_completed_by_user_id" FOREIGN KEY (completed_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE installation_milestone_evidence (
    id uuid NOT NULL,
    milestone_id uuid NOT NULL,
    project_id uuid NOT NULL,
    file_id uuid NOT NULL,
    rework_round integer NOT NULL,
    caption character varying(300),
    added_by_user_id uuid NOT NULL,
    added_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_installation_milestone_evidence" PRIMARY KEY (id),
    CONSTRAINT "FK_installation_milestone_evidence_file_reference_file_id" FOREIGN KEY (file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_milestone_evidence_installation_milestone_mile~" FOREIGN KEY (milestone_id) REFERENCES installation_milestone (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_milestone_evidence_user_account_added_by_user_~" FOREIGN KEY (added_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE installation_project (
    id uuid NOT NULL,
    contract_id uuid NOT NULL,
    survey_request_id uuid NOT NULL,
    customer_user_id uuid NOT NULL,
    property_site_id uuid NOT NULL,
    manager_user_id uuid NOT NULL,
    status character varying(40) NOT NULL,
    planned_start_date date,
    planned_end_date date,
    started_at timestamp with time zone,
    acceptance_requested_at timestamp with time zone,
    completed_at timestamp with time zone,
    handover_at timestamp with time zone,
    acceptance_round integer NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_installation_project" PRIMARY KEY (id),
    CONSTRAINT ck_installation_project_completed CHECK (status <> 'COMPLETED' OR (completed_at IS NOT NULL AND handover_at IS NOT NULL)),
    CONSTRAINT ck_installation_project_dates CHECK (planned_end_date IS NULL OR planned_start_date IS NULL OR planned_end_date >= planned_start_date),
    CONSTRAINT ck_installation_project_status CHECK (status IN ('PLANNING', 'SCHEDULED', 'IN_PROGRESS', 'PENDING_CUSTOMER_ACCEPTANCE', 'REWORK_REQUIRED', 'COMPLETED')),
    CONSTRAINT "FK_installation_project_customer_contract_contract_id" FOREIGN KEY (contract_id) REFERENCES customer_contract (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_project_property_site_property_site_id" FOREIGN KEY (property_site_id) REFERENCES property_site (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_project_user_account_customer_user_id" FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_project_user_account_manager_user_id" FOREIGN KEY (manager_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE installation_shift (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    local_date date NOT NULL,
    local_start time without time zone NOT NULL,
    local_end time without time zone NOT NULL,
    starts_at timestamp with time zone NOT NULL,
    ends_at timestamp with time zone NOT NULL,
    status character varying(20) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_installation_shift" PRIMARY KEY (id),
    CONSTRAINT ck_installation_shift_range CHECK (ends_at > starts_at AND local_end > local_start),
    CONSTRAINT ck_installation_shift_status CHECK (status IN ('ACTIVE', 'CANCELLED')),
    CONSTRAINT "FK_installation_shift_installation_project_project_id" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT
);

CREATE TABLE installation_team_member (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    technician_user_id uuid NOT NULL,
    is_leader boolean NOT NULL,
    assigned_by_user_id uuid NOT NULL,
    assigned_at timestamp with time zone NOT NULL,
    removed_at timestamp with time zone,
    CONSTRAINT "PK_installation_team_member" PRIMARY KEY (id),
    CONSTRAINT ck_installation_team_member_leader_active CHECK (NOT is_leader OR removed_at IS NULL),
    CONSTRAINT "FK_installation_team_member_installation_project_project_id" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_team_member_user_account_assigned_by_user_id" FOREIGN KEY (assigned_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_team_member_user_account_technician_user_id" FOREIGN KEY (technician_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE installed_asset (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    asset_type character varying(20) NOT NULL,
    product_id uuid,
    manufacturer character varying(100) NOT NULL,
    model character varying(100) NOT NULL,
    serial_number character varying(100) NOT NULL,
    position_code character varying(50),
    warranty_months integer,
    registered_by_user_id uuid NOT NULL,
    registered_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_installed_asset" PRIMARY KEY (id),
    CONSTRAINT ck_installed_asset_type CHECK (asset_type IN ('PANEL', 'INVERTER', 'BATTERY', 'METER', 'OTHER')),
    CONSTRAINT ck_installed_asset_warranty CHECK (warranty_months IS NULL OR warranty_months BETWEEN 0 AND 600),
    CONSTRAINT "FK_installed_asset_installation_project_project_id" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installed_asset_product_product_id" FOREIGN KEY (product_id) REFERENCES product (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installed_asset_user_account_registered_by_user_id" FOREIGN KEY (registered_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE installation_shift_assignment (
    id uuid NOT NULL,
    shift_id uuid NOT NULL,
    project_id uuid NOT NULL,
    technician_user_id uuid NOT NULL,
    starts_at timestamp with time zone NOT NULL,
    ends_at timestamp with time zone NOT NULL,
    status character varying(20) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    released_at timestamp with time zone,
    CONSTRAINT "PK_installation_shift_assignment" PRIMARY KEY (id),
    CONSTRAINT ck_installation_shift_assignment_range CHECK (ends_at > starts_at),
    CONSTRAINT ck_installation_shift_assignment_released CHECK ((status = 'RELEASED') = (released_at IS NOT NULL)),
    CONSTRAINT ck_installation_shift_assignment_status CHECK (status IN ('ACTIVE', 'RELEASED')),
    CONSTRAINT "FK_installation_shift_assignment_installation_project_project_~" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_shift_assignment_installation_shift_shift_id" FOREIGN KEY (shift_id) REFERENCES installation_shift (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_installation_shift_assignment_user_account_technician_user_~" FOREIGN KEY (technician_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE knowledge_document (
    id uuid NOT NULL,
    title character varying(200) NOT NULL,
    category character varying(40) NOT NULL,
    tags character varying(1000),
    status character varying(30) NOT NULL,
    latest_version_id uuid,
    published_version_id uuid,
    created_by_user_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    archived_at timestamp with time zone,
    revision integer NOT NULL,
    CONSTRAINT "PK_knowledge_document" PRIMARY KEY (id),
    CONSTRAINT ck_knowledge_document_published CHECK ((status = 'PUBLISHED') = (published_version_id IS NOT NULL)),
    CONSTRAINT ck_knowledge_document_status CHECK (status IN ('REGISTERED', 'PROCESSING', 'READY_FOR_REVIEW', 'PUBLISHED', 'UNPUBLISHED', 'FAILED', 'ARCHIVED')),
    CONSTRAINT "FK_knowledge_document_user_account_created_by_user_id" FOREIGN KEY (created_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE knowledge_document_version (
    id uuid NOT NULL,
    document_id uuid NOT NULL,
    version_number integer NOT NULL,
    file_id uuid NOT NULL,
    status character varying(30) NOT NULL,
    page_count integer,
    chunk_count integer,
    extracted_characters integer,
    embedding_model character varying(100),
    embedding_dimension integer,
    failure_reason character varying(300),
    created_by_user_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    ingested_at timestamp with time zone,
    published_at timestamp with time zone,
    revision integer NOT NULL,
    CONSTRAINT "PK_knowledge_document_version" PRIMARY KEY (id),
    CONSTRAINT ak_knowledge_document_version_id_document UNIQUE (id, document_id),
    CONSTRAINT ck_knowledge_document_version_number CHECK (version_number >= 1),
    CONSTRAINT ck_knowledge_document_version_status CHECK (status IN ('REGISTERED', 'VERIFYING', 'PROCESSING', 'READY_FOR_REVIEW', 'PUBLISHED', 'SUPERSEDED', 'FAILED', 'OCR_REQUIRED', 'UNSUPPORTED_CONTENT', 'WITHDRAWN')),
    CONSTRAINT "FK_knowledge_document_version_file_reference_file_id" FOREIGN KEY (file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_knowledge_document_version_knowledge_document_document_id" FOREIGN KEY (document_id) REFERENCES knowledge_document (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_knowledge_document_version_user_account_created_by_user_id" FOREIGN KEY (created_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE knowledge_ingestion_job (
    id uuid NOT NULL,
    version_id uuid NOT NULL,
    kind character varying(20) NOT NULL,
    status character varying(20) NOT NULL,
    idempotency_key character varying(100) NOT NULL,
    attempts integer NOT NULL,
    max_attempts integer NOT NULL,
    next_attempt_at timestamp with time zone NOT NULL,
    locked_until timestamp with time zone,
    last_error character varying(300),
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    completed_at timestamp with time zone,
    CONSTRAINT "PK_knowledge_ingestion_job" PRIMARY KEY (id),
    CONSTRAINT ck_knowledge_ingestion_job_attempts CHECK (attempts >= 0 AND max_attempts >= 1),
    CONSTRAINT ck_knowledge_ingestion_job_kind CHECK (kind IN ('INGEST', 'DELETE_VECTORS')),
    CONSTRAINT ck_knowledge_ingestion_job_status CHECK (status IN ('PENDING', 'RUNNING', 'COMPLETED', 'FAILED', 'RETRY_WAITING')),
    CONSTRAINT "FK_knowledge_ingestion_job_knowledge_document_version_version_~" FOREIGN KEY (version_id) REFERENCES knowledge_document_version (id) ON DELETE RESTRICT
);

CREATE TABLE onsite_survey_amendment (
    id uuid NOT NULL,
    report_id uuid NOT NULL,
    amendment_number integer NOT NULL,
    author_user_id uuid NOT NULL,
    reason character varying(1000) NOT NULL,
    changes jsonb NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_onsite_survey_amendment" PRIMARY KEY (id),
    CONSTRAINT "FK_onsite_survey_amendment_user_account_author_user_id" FOREIGN KEY (author_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE onsite_survey_attachment (
    id uuid NOT NULL,
    report_id uuid NOT NULL,
    file_id uuid NOT NULL,
    category character varying(20) NOT NULL,
    caption character varying(300),
    added_by_user_id uuid NOT NULL,
    added_at timestamp with time zone NOT NULL,
    amendment_id uuid,
    CONSTRAINT "PK_onsite_survey_attachment" PRIMARY KEY (id),
    CONSTRAINT "FK_onsite_survey_attachment_file_reference_file_id" FOREIGN KEY (file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_onsite_survey_attachment_onsite_survey_amendment_amendment_~" FOREIGN KEY (amendment_id) REFERENCES onsite_survey_amendment (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_onsite_survey_attachment_user_account_added_by_user_id" FOREIGN KEY (added_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE onsite_survey_obstacle (
    id uuid NOT NULL,
    report_id uuid NOT NULL,
    sort_order integer NOT NULL,
    kind character varying(30) NOT NULL,
    description character varying(500),
    height_m numeric(6,2),
    area_m2 numeric(8,2),
    CONSTRAINT "PK_onsite_survey_obstacle" PRIMARY KEY (id),
    CONSTRAINT ck_onsite_survey_obstacle_sizes CHECK ((height_m IS NULL OR height_m >= 0) AND (area_m2 IS NULL OR area_m2 >= 0))
);

CREATE TABLE onsite_survey_report (
    id uuid NOT NULL,
    survey_request_id uuid NOT NULL,
    appointment_id uuid NOT NULL,
    technician_user_id uuid NOT NULL,
    status character varying(20) NOT NULL,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    roof_type character varying(50),
    roof_material character varying(50),
    roof_length_m numeric(8,2),
    roof_width_m numeric(8,2),
    usable_area_m2 numeric(10,2),
    tilt_degree numeric(5,2),
    azimuth_degree numeric(5,2),
    roof_condition character varying(20),
    electrical_cabinet_notes character varying(2000),
    electrical_connection_notes character varying(2000),
    installation_feasibility character varying(30),
    proposed_inverter_location character varying(500),
    proposed_panel_layout_notes character varying(2000),
    measured_latitude numeric(9,6),
    measured_longitude numeric(9,6),
    notes character varying(4000),
    version integer NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_onsite_survey_report" PRIMARY KEY (id),
    CONSTRAINT ck_onsite_survey_report_angles CHECK ((tilt_degree IS NULL OR tilt_degree BETWEEN 0 AND 90) AND (azimuth_degree IS NULL OR (azimuth_degree >= 0 AND azimuth_degree < 360))),
    CONSTRAINT ck_onsite_survey_report_completed CHECK (status <> 'COMPLETED' OR (completed_at IS NOT NULL AND started_at IS NOT NULL AND version >= 1)),
    CONSTRAINT ck_onsite_survey_report_dimensions CHECK ((roof_length_m IS NULL OR roof_length_m > 0) AND (roof_width_m IS NULL OR roof_width_m > 0) AND (usable_area_m2 IS NULL OR usable_area_m2 > 0)),
    CONSTRAINT ck_onsite_survey_report_location_paired CHECK ((measured_latitude IS NULL) = (measured_longitude IS NULL)),
    CONSTRAINT ck_onsite_survey_report_status CHECK (status IN ('DRAFT', 'IN_PROGRESS', 'COMPLETED')),
    CONSTRAINT "FK_onsite_survey_report_user_account_technician_user_id" FOREIGN KEY (technician_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE quotation (
    id uuid NOT NULL,
    quote_number character varying(30) NOT NULL,
    survey_request_id uuid NOT NULL,
    onsite_survey_report_id uuid NOT NULL,
    onsite_survey_version integer NOT NULL,
    version integer NOT NULL,
    previous_quotation_id uuid,
    status character varying(30) NOT NULL,
    customer_user_id uuid NOT NULL,
    sales_user_id uuid NOT NULL,
    currency character varying(3) NOT NULL,
    equipment_subtotal numeric(18,2) NOT NULL,
    labor_cost numeric(18,2) NOT NULL,
    transport_cost numeric(18,2) NOT NULL,
    other_service_cost numeric(18,2) NOT NULL,
    subtotal numeric(18,2) NOT NULL,
    discount_amount numeric(18,2) NOT NULL,
    tax_rate_percent numeric(5,2) NOT NULL,
    tax_amount numeric(18,2) NOT NULL,
    total_amount numeric(18,2) NOT NULL,
    valid_until timestamp with time zone,
    terms character varying(4000),
    notes character varying(2000),
    customer_snapshot jsonb,
    document_file_id uuid,
    issued_at timestamp with time zone,
    accepted_at timestamp with time zone,
    revision_requested_at timestamp with time zone,
    revision_reason character varying(2000),
    superseded_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_quotation" PRIMARY KEY (id),
    CONSTRAINT ck_quotation_amounts_non_negative CHECK (equipment_subtotal >= 0 AND labor_cost >= 0 AND transport_cost >= 0 AND other_service_cost >= 0 AND discount_amount >= 0 AND tax_amount >= 0),
    CONSTRAINT ck_quotation_currency CHECK (currency = 'VND'),
    CONSTRAINT ck_quotation_discount_bounded CHECK (discount_amount <= subtotal),
    CONSTRAINT ck_quotation_issued_fields CHECK (status = 'DRAFT' OR (issued_at IS NOT NULL AND valid_until IS NOT NULL AND customer_snapshot IS NOT NULL)),
    CONSTRAINT ck_quotation_status CHECK (status IN ('DRAFT', 'ISSUED', 'ACCEPTED', 'REVISION_REQUESTED', 'SUPERSEDED')),
    CONSTRAINT ck_quotation_tax_rate CHECK (tax_rate_percent BETWEEN 0 AND 20),
    CONSTRAINT ck_quotation_total_consistent CHECK (total_amount = subtotal - discount_amount + tax_amount),
    CONSTRAINT ck_quotation_version_positive CHECK (version >= 1),
    CONSTRAINT "FK_quotation_file_reference_document_file_id" FOREIGN KEY (document_file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_quotation_onsite_survey_report_onsite_survey_report_id" FOREIGN KEY (onsite_survey_report_id) REFERENCES onsite_survey_report (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_quotation_quotation_previous_quotation_id" FOREIGN KEY (previous_quotation_id) REFERENCES quotation (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_quotation_survey_request_survey_request_id" FOREIGN KEY (survey_request_id) REFERENCES survey_request (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_quotation_user_account_customer_user_id" FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_quotation_user_account_sales_user_id" FOREIGN KEY (sales_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE quotation_event (
    id uuid NOT NULL,
    quotation_id uuid NOT NULL,
    event_type character varying(40) NOT NULL,
    actor_user_id uuid NOT NULL,
    detail character varying(500),
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_quotation_event" PRIMARY KEY (id),
    CONSTRAINT "FK_quotation_event_quotation_quotation_id" FOREIGN KEY (quotation_id) REFERENCES quotation (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_quotation_event_user_account_actor_user_id" FOREIGN KEY (actor_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE quotation_line (
    id uuid NOT NULL,
    quotation_id uuid NOT NULL,
    line_number integer NOT NULL,
    product_id uuid,
    sku character varying(50),
    name character varying(255) NOT NULL,
    brand character varying(100),
    model character varying(100),
    unit character varying(20) NOT NULL,
    quantity numeric(12,3) NOT NULL,
    unit_price numeric(18,2) NOT NULL,
    catalog_unit_price numeric(18,2),
    line_total numeric(18,2) NOT NULL,
    description character varying(500),
    CONSTRAINT "PK_quotation_line" PRIMARY KEY (id),
    CONSTRAINT ck_quotation_line_prices CHECK (unit_price >= 0 AND line_total >= 0),
    CONSTRAINT ck_quotation_line_quantity CHECK (quantity > 0),
    CONSTRAINT "FK_quotation_line_product_product_id" FOREIGN KEY (product_id) REFERENCES product (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_quotation_line_quotation_quotation_id" FOREIGN KEY (quotation_id) REFERENCES quotation (id) ON DELETE CASCADE
);

CREATE INDEX "IX_chat_citation_document_id" ON chat_citation (document_id);

CREATE INDEX "IX_chat_citation_version_id" ON chat_citation (version_id);

CREATE UNIQUE INDEX ux_chat_citation_message_rank ON chat_citation (message_id, rank);

CREATE INDEX ix_chat_message_session_created_at ON chat_message (session_id, created_at);

CREATE UNIQUE INDEX ux_chat_message_client_id ON chat_message (session_id, client_message_id) WHERE client_message_id IS NOT NULL;

CREATE UNIQUE INDEX ux_chat_message_one_reply ON chat_message (reply_to_message_id) WHERE reply_to_message_id IS NOT NULL;

CREATE INDEX ix_chat_session_user_updated_at ON chat_session (user_id, updated_at);

CREATE INDEX "IX_contract_event_actor_user_id" ON contract_event (actor_user_id);

CREATE INDEX ix_contract_event_contract_created_at ON contract_event (contract_id, created_at);

CREATE INDEX "IX_contract_signature_submission_reviewed_by_user_id" ON contract_signature_submission (reviewed_by_user_id);

CREATE INDEX "IX_contract_signature_submission_submitted_by_user_id" ON contract_signature_submission (submitted_by_user_id);

CREATE UNIQUE INDEX ux_contract_signature_submission_file ON contract_signature_submission (file_id);

CREATE UNIQUE INDEX ux_contract_signature_submission_one_pending ON contract_signature_submission (contract_id, outcome) WHERE outcome = 'PENDING';

CREATE INDEX ix_customer_contract_customer_status ON customer_contract (customer_user_id, status);

CREATE INDEX "IX_customer_contract_official_document_confirmed_by_user_id" ON customer_contract (official_document_confirmed_by_user_id);

CREATE INDEX "IX_customer_contract_official_document_file_id" ON customer_contract (official_document_file_id);

CREATE INDEX "IX_customer_contract_previous_contract_id" ON customer_contract (previous_contract_id);

CREATE INDEX "IX_customer_contract_reviewed_by_user_id" ON customer_contract (reviewed_by_user_id);

CREATE INDEX "IX_customer_contract_sales_user_id" ON customer_contract (sales_user_id);

CREATE INDEX "IX_customer_contract_signed_document_file_id" ON customer_contract (signed_document_file_id);

CREATE INDEX ix_customer_contract_survey_request ON customer_contract (survey_request_id);

CREATE UNIQUE INDEX ux_customer_contract_number_version ON customer_contract (contract_number, version);

CREATE UNIQUE INDEX ux_customer_contract_one_draft ON customer_contract (quotation_id, status) WHERE status = 'DRAFT';

CREATE UNIQUE INDEX ux_customer_contract_one_live ON customer_contract (quotation_id) WHERE status NOT IN ('DRAFT', 'SUPERSEDED');

CREATE INDEX ix_file_reference_uploader_created_at ON file_reference (uploaded_by_user_id, created_at);

CREATE UNIQUE INDEX ux_file_reference_bucket_object_key ON file_reference (bucket, object_key);

CREATE INDEX "IX_installation_acceptance_decided_by_user_id" ON installation_acceptance (decided_by_user_id);

CREATE INDEX "IX_installation_acceptance_requested_by_user_id" ON installation_acceptance (requested_by_user_id);

CREATE UNIQUE INDEX ux_installation_acceptance_one_pending ON installation_acceptance (project_id, decision) WHERE decision = 'PENDING';

CREATE UNIQUE INDEX ux_installation_acceptance_project_round ON installation_acceptance (project_id, round);

CREATE INDEX "IX_installation_checklist_item_evidence_file_id" ON installation_checklist_item (evidence_file_id);

CREATE INDEX "IX_installation_checklist_item_verified_by_user_id" ON installation_checklist_item (verified_by_user_id);

CREATE UNIQUE INDEX ux_installation_checklist_item_project_code ON installation_checklist_item (project_id, code);

CREATE INDEX "IX_installation_event_actor_user_id" ON installation_event (actor_user_id);

CREATE INDEX ix_installation_event_project_created_at ON installation_event (project_id, created_at);

CREATE INDEX "IX_installation_milestone_completed_by_user_id" ON installation_milestone (completed_by_user_id);

CREATE UNIQUE INDEX ux_installation_milestone_project_code ON installation_milestone (project_id, code);

CREATE UNIQUE INDEX ux_installation_milestone_project_order ON installation_milestone (project_id, sort_order);

CREATE INDEX "IX_installation_milestone_evidence_added_by_user_id" ON installation_milestone_evidence (added_by_user_id);

CREATE INDEX ix_installation_milestone_evidence_milestone_round ON installation_milestone_evidence (milestone_id, rework_round);

CREATE INDEX "IX_installation_milestone_evidence_project_id" ON installation_milestone_evidence (project_id);

CREATE UNIQUE INDEX ux_installation_milestone_evidence_file ON installation_milestone_evidence (file_id);

CREATE INDEX ix_installation_project_customer_status ON installation_project (customer_user_id, status);

CREATE INDEX "IX_installation_project_manager_user_id" ON installation_project (manager_user_id);

CREATE INDEX "IX_installation_project_property_site_id" ON installation_project (property_site_id);

CREATE INDEX "IX_installation_project_survey_request_id" ON installation_project (survey_request_id);

CREATE UNIQUE INDEX ux_installation_project_contract ON installation_project (contract_id);

CREATE UNIQUE INDEX ux_installation_shift_project_start ON installation_shift (project_id, starts_at) WHERE status = 'ACTIVE';

CREATE INDEX "IX_installation_shift_assignment_project_id" ON installation_shift_assignment (project_id);

CREATE INDEX ix_installation_shift_assignment_technician_starts_at ON installation_shift_assignment (technician_user_id, starts_at);

CREATE UNIQUE INDEX ux_installation_shift_assignment_shift_technician ON installation_shift_assignment (shift_id, technician_user_id) WHERE status = 'ACTIVE';

CREATE INDEX "IX_installation_team_member_assigned_by_user_id" ON installation_team_member (assigned_by_user_id);

CREATE INDEX "IX_installation_team_member_technician_user_id" ON installation_team_member (technician_user_id);

CREATE UNIQUE INDEX ux_installation_team_member_active ON installation_team_member (project_id, technician_user_id) WHERE removed_at IS NULL;

CREATE UNIQUE INDEX ux_installation_team_member_one_leader ON installation_team_member (project_id, is_leader) WHERE is_leader AND removed_at IS NULL;

CREATE INDEX "IX_installed_asset_product_id" ON installed_asset (product_id);

CREATE INDEX ix_installed_asset_project ON installed_asset (project_id);

CREATE INDEX "IX_installed_asset_registered_by_user_id" ON installed_asset (registered_by_user_id);

CREATE UNIQUE INDEX ux_installed_asset_manufacturer_serial ON installed_asset (manufacturer, serial_number);

CREATE INDEX "IX_knowledge_document_created_by_user_id" ON knowledge_document (created_by_user_id);

CREATE INDEX ix_knowledge_document_latest_version ON knowledge_document (latest_version_id, id);

CREATE INDEX ix_knowledge_document_published_version ON knowledge_document (published_version_id, id);

CREATE INDEX ix_knowledge_document_status ON knowledge_document (status);

CREATE INDEX "IX_knowledge_document_version_created_by_user_id" ON knowledge_document_version (created_by_user_id);

CREATE UNIQUE INDEX ux_knowledge_document_version_file ON knowledge_document_version (file_id);

CREATE UNIQUE INDEX ux_knowledge_document_version_number ON knowledge_document_version (document_id, version_number);

CREATE UNIQUE INDEX ux_knowledge_document_version_one_published ON knowledge_document_version (document_id, status) WHERE status = 'PUBLISHED';

CREATE INDEX ix_knowledge_ingestion_job_due ON knowledge_ingestion_job (status, next_attempt_at);

CREATE INDEX "IX_knowledge_ingestion_job_version_id" ON knowledge_ingestion_job (version_id);

CREATE UNIQUE INDEX ux_knowledge_ingestion_job_idempotency ON knowledge_ingestion_job (idempotency_key);

CREATE INDEX "IX_onsite_survey_amendment_author_user_id" ON onsite_survey_amendment (author_user_id);

CREATE UNIQUE INDEX ux_onsite_survey_amendment_report_number ON onsite_survey_amendment (report_id, amendment_number);

CREATE INDEX "IX_onsite_survey_attachment_added_by_user_id" ON onsite_survey_attachment (added_by_user_id);

CREATE INDEX "IX_onsite_survey_attachment_amendment_id" ON onsite_survey_attachment (amendment_id);

CREATE INDEX ix_onsite_survey_attachment_report ON onsite_survey_attachment (report_id);

CREATE UNIQUE INDEX ux_onsite_survey_attachment_file ON onsite_survey_attachment (file_id);

CREATE UNIQUE INDEX ux_onsite_survey_obstacle_report_order ON onsite_survey_obstacle (report_id, sort_order);

CREATE INDEX ix_onsite_survey_report_appointment ON onsite_survey_report (appointment_id);

CREATE INDEX "IX_onsite_survey_report_technician_user_id" ON onsite_survey_report (technician_user_id);

CREATE UNIQUE INDEX ux_onsite_survey_report_survey_request ON onsite_survey_report (survey_request_id);

CREATE INDEX ix_payment_attempt_contract_created_at ON payment_attempt (contract_id, created_at);

CREATE INDEX "IX_payment_attempt_customer_user_id" ON payment_attempt (customer_user_id);

CREATE UNIQUE INDEX ux_payment_attempt_one_paid ON payment_attempt (contract_id, status) WHERE status = 'PAID';

CREATE UNIQUE INDEX ux_payment_attempt_one_payable ON payment_attempt (contract_id) WHERE status IN ('PENDING', 'PROCESSING');

CREATE UNIQUE INDEX ux_payment_attempt_order_code ON payment_attempt (order_code);

CREATE UNIQUE INDEX ux_payment_attempt_provider_reference ON payment_attempt (provider, provider_reference) WHERE provider_reference IS NOT NULL;

CREATE INDEX ix_payment_event_contract_created_at ON payment_event (contract_id, created_at);

CREATE INDEX "IX_payment_event_payment_attempt_id" ON payment_event (payment_attempt_id);

CREATE INDEX ix_payment_webhook_event_order_code ON payment_webhook_event (order_code);

CREATE UNIQUE INDEX ux_payment_webhook_event_payload ON payment_webhook_event (provider, payload_sha256);

CREATE INDEX ix_quotation_customer_status ON quotation (customer_user_id, status);

CREATE INDEX "IX_quotation_document_file_id" ON quotation (document_file_id);

CREATE INDEX "IX_quotation_onsite_survey_report_id" ON quotation (onsite_survey_report_id);

CREATE INDEX "IX_quotation_previous_quotation_id" ON quotation (previous_quotation_id);

CREATE INDEX "IX_quotation_sales_user_id" ON quotation (sales_user_id);

CREATE UNIQUE INDEX ux_quotation_number_version ON quotation (quote_number, version);

CREATE UNIQUE INDEX ux_quotation_one_draft ON quotation (survey_request_id) WHERE status = 'DRAFT';

CREATE UNIQUE INDEX ux_quotation_one_live ON quotation (survey_request_id, customer_user_id) WHERE status IN ('ISSUED', 'ACCEPTED');

CREATE UNIQUE INDEX ux_quotation_survey_request_version ON quotation (survey_request_id, version);

CREATE INDEX "IX_quotation_event_actor_user_id" ON quotation_event (actor_user_id);

CREATE INDEX ix_quotation_event_quotation_created_at ON quotation_event (quotation_id, created_at);

CREATE INDEX "IX_quotation_line_product_id" ON quotation_line (product_id);

CREATE UNIQUE INDEX ux_quotation_line_number ON quotation_line (quotation_id, line_number);

CREATE INDEX ix_rag_usage_reservation_date ON rag_usage_reservation (usage_date);

CREATE INDEX ix_rag_usage_reservation_user_date_expires ON rag_usage_reservation (user_id, usage_date, expires_at);

ALTER TABLE chat_citation ADD CONSTRAINT "FK_chat_citation_knowledge_document_document_id" FOREIGN KEY (document_id) REFERENCES knowledge_document (id) ON DELETE RESTRICT;

ALTER TABLE chat_citation ADD CONSTRAINT "FK_chat_citation_knowledge_document_version_version_id" FOREIGN KEY (version_id) REFERENCES knowledge_document_version (id) ON DELETE RESTRICT;

ALTER TABLE contract_event ADD CONSTRAINT "FK_contract_event_customer_contract_contract_id" FOREIGN KEY (contract_id) REFERENCES customer_contract (id) ON DELETE RESTRICT;

ALTER TABLE contract_signature_submission ADD CONSTRAINT "FK_contract_signature_submission_customer_contract_contract_id" FOREIGN KEY (contract_id) REFERENCES customer_contract (id) ON DELETE RESTRICT;

ALTER TABLE customer_contract ADD CONSTRAINT "FK_customer_contract_quotation_quotation_id" FOREIGN KEY (quotation_id) REFERENCES quotation (id) ON DELETE RESTRICT;

ALTER TABLE customer_contract ADD CONSTRAINT "FK_customer_contract_survey_request_survey_request_id" FOREIGN KEY (survey_request_id) REFERENCES survey_request (id) ON DELETE RESTRICT;

ALTER TABLE installation_acceptance ADD CONSTRAINT "FK_installation_acceptance_installation_project_project_id" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT;

ALTER TABLE installation_checklist_item ADD CONSTRAINT "FK_installation_checklist_item_installation_project_project_id" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT;

ALTER TABLE installation_event ADD CONSTRAINT "FK_installation_event_installation_project_project_id" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT;

ALTER TABLE installation_milestone ADD CONSTRAINT "FK_installation_milestone_installation_project_project_id" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT;

ALTER TABLE installation_milestone_evidence ADD CONSTRAINT "FK_installation_milestone_evidence_installation_project_projec~" FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT;

ALTER TABLE installation_project ADD CONSTRAINT "FK_installation_project_survey_request_survey_request_id" FOREIGN KEY (survey_request_id) REFERENCES survey_request (id) ON DELETE RESTRICT;

ALTER TABLE knowledge_document ADD CONSTRAINT fk_knowledge_document_latest_version FOREIGN KEY (latest_version_id, id) REFERENCES knowledge_document_version (id, document_id) ON DELETE RESTRICT;

ALTER TABLE knowledge_document ADD CONSTRAINT fk_knowledge_document_published_version FOREIGN KEY (published_version_id, id) REFERENCES knowledge_document_version (id, document_id) ON DELETE RESTRICT;

ALTER TABLE onsite_survey_amendment ADD CONSTRAINT "FK_onsite_survey_amendment_onsite_survey_report_report_id" FOREIGN KEY (report_id) REFERENCES onsite_survey_report (id) ON DELETE RESTRICT;

ALTER TABLE onsite_survey_attachment ADD CONSTRAINT "FK_onsite_survey_attachment_onsite_survey_report_report_id" FOREIGN KEY (report_id) REFERENCES onsite_survey_report (id) ON DELETE RESTRICT;

ALTER TABLE onsite_survey_obstacle ADD CONSTRAINT "FK_onsite_survey_obstacle_onsite_survey_report_report_id" FOREIGN KEY (report_id) REFERENCES onsite_survey_report (id) ON DELETE CASCADE;

ALTER TABLE onsite_survey_report ADD CONSTRAINT "FK_onsite_survey_report_survey_request_survey_request_id" FOREIGN KEY (survey_request_id) REFERENCES survey_request (id) ON DELETE RESTRICT;

ALTER TABLE onsite_survey_report ADD CONSTRAINT "FK_onsite_survey_report_technician_appointment_appointment_id" FOREIGN KEY (appointment_id) REFERENCES technician_appointment (id) ON DELETE RESTRICT;
-- </generated-from-ef-model>

-- <manual>
-- Constraints EF cannot express. Mirrors PendingSchemaScript.Manual (used by the PostgreSQL tests).
-- Installation crews: one technician can never be in two overlapping ACTIVE installation shifts.
-- (Survey appointments are serialised against installation planning through the technician_profile row lock.)
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE installation_shift_assignment
    ADD CONSTRAINT ex_installation_shift_assignment_no_overlap
    EXCLUDE USING gist (technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
    WHERE (status = 'ACTIVE');
-- </manual>
