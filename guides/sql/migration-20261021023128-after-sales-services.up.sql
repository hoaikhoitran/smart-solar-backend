START TRANSACTION;

DROP INDEX ux_installation_assignment_current;

ALTER TABLE file_reference DROP CONSTRAINT ck_file_reference_purpose;

ALTER TABLE payment_event ALTER COLUMN contract_id DROP NOT NULL;

ALTER TABLE payment_attempt ALTER COLUMN contract_id DROP NOT NULL;

ALTER TABLE installed_asset ADD replaces_asset_id uuid;

ALTER TABLE installed_asset ADD service_request_id uuid;

ALTER TABLE installed_asset ADD specifications character varying(500);

ALTER TABLE installed_asset ADD status character varying(20) NOT NULL DEFAULT 'ACTIVE';

ALTER TABLE installation_assignment ADD service_visit_id uuid;

ALTER TABLE customer_contract ADD warranty_months integer;

CREATE TABLE after_sales_event (
    id uuid NOT NULL,
    subject_type character varying(30) NOT NULL,
    subject_id uuid NOT NULL,
    event_type character varying(50) NOT NULL,
    actor_user_id uuid,
    detail character varying(2000),
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_after_sales_event" PRIMARY KEY (id),
    CONSTRAINT fk_after_sales_event_actor FOREIGN KEY (actor_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE service_package (
    id uuid NOT NULL,
    kind character varying(30) NOT NULL,
    name character varying(150) NOT NULL,
    description character varying(2000),
    price numeric(18,2) NOT NULL,
    duration_months integer NOT NULL,
    visit_months character varying(200),
    covered_disaster_types character varying(200),
    coverage_percent numeric(5,2),
    per_incident_limit numeric(18,2),
    term_limit numeric(18,2),
    is_active boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_service_package" PRIMARY KEY (id),
    CONSTRAINT ck_service_package_disaster CHECK (kind <> 'DISASTER_PROTECTION' OR (covered_disaster_types IS NOT NULL AND coverage_percent BETWEEN 0 AND 100 AND per_incident_limit >= 0 AND term_limit >= per_incident_limit)),
    CONSTRAINT ck_service_package_kind CHECK (kind IN ('MAINTENANCE', 'DISASTER_PROTECTION')),
    CONSTRAINT ck_service_package_maintenance CHECK (kind <> 'MAINTENANCE' OR visit_months IS NOT NULL),
    CONSTRAINT ck_service_package_price CHECK (price > 0 AND duration_months BETWEEN 1 AND 120)
);

CREATE TABLE warranty_option (
    id uuid NOT NULL,
    kind character varying(20) NOT NULL,
    months integer NOT NULL,
    is_default boolean NOT NULL,
    is_active boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_warranty_option" PRIMARY KEY (id),
    CONSTRAINT ck_warranty_option_kind CHECK (kind IN ('INSTALLATION', 'REPAIR')),
    CONSTRAINT ck_warranty_option_months CHECK (months BETWEEN 1 AND 240)
);

CREATE TABLE service_subscription (
    id uuid NOT NULL,
    customer_user_id uuid NOT NULL,
    contract_id uuid NOT NULL,
    project_id uuid,
    package_id uuid NOT NULL,
    kind character varying(30) NOT NULL,
    status character varying(30) NOT NULL,
    package_name character varying(150) NOT NULL,
    price numeric(18,2) NOT NULL,
    duration_months integer NOT NULL,
    visit_months character varying(200),
    covered_disaster_types character varying(200),
    coverage_percent numeric(5,2),
    per_incident_limit numeric(18,2),
    term_limit numeric(18,2),
    starts_at timestamp with time zone,
    ends_at timestamp with time zone,
    coverage_starts_at timestamp with time zone,
    renews_subscription_id uuid,
    current_payment_attempt_id uuid,
    checkout_expires_at timestamp with time zone,
    paid_at timestamp with time zone,
    activated_at timestamp with time zone,
    support_reserved numeric(18,2) NOT NULL,
    support_consumed numeric(18,2) NOT NULL,
    cancellation_requested_at timestamp with time zone,
    cancellation_reason character varying(500),
    cancelled_at timestamp with time zone,
    expiry_reminder_sent_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_service_subscription" PRIMARY KEY (id),
    CONSTRAINT ck_service_subscription_active CHECK (status <> 'ACTIVE' OR (starts_at IS NOT NULL AND paid_at IS NOT NULL AND project_id IS NOT NULL)),
    CONSTRAINT ck_service_subscription_kind CHECK (kind IN ('MAINTENANCE', 'DISASTER_PROTECTION')),
    CONSTRAINT ck_service_subscription_price CHECK (price > 0 AND duration_months BETWEEN 1 AND 120),
    CONSTRAINT ck_service_subscription_status CHECK (status IN ('AWAITING_PAYMENT', 'CHECKOUT_EXPIRED', 'PAID', 'ACTIVE', 'EXPIRED', 'CANCELLATION_REQUESTED', 'CANCELLED')),
    CONSTRAINT ck_service_subscription_support CHECK (support_reserved >= 0 AND support_consumed >= 0 AND (term_limit IS NULL OR support_reserved + support_consumed <= term_limit)),
    CONSTRAINT ck_service_subscription_term CHECK ((starts_at IS NULL) = (ends_at IS NULL) AND (ends_at IS NULL OR ends_at > starts_at)),
    CONSTRAINT fk_service_subscription_contract FOREIGN KEY (contract_id) REFERENCES customer_contract (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_subscription_customer FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_subscription_package FOREIGN KEY (package_id) REFERENCES service_package (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_subscription_project FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_subscription_renews FOREIGN KEY (renews_subscription_id) REFERENCES service_subscription (id) ON DELETE RESTRICT
);

CREATE TABLE maintenance_entitlement (
    id uuid NOT NULL,
    subscription_id uuid NOT NULL,
    plan_month integer NOT NULL,
    period_starts_at timestamp with time zone NOT NULL,
    period_ends_at timestamp with time zone NOT NULL,
    status character varying(30) NOT NULL,
    current_request_id uuid,
    extended_until timestamp with time zone,
    notified_at timestamp with time zone,
    follow_up_created_at timestamp with time zone,
    consumed_at timestamp with time zone,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_maintenance_entitlement" PRIMARY KEY (id),
    CONSTRAINT ck_maintenance_entitlement_consumed CHECK ((status = 'CONSUMED') = (consumed_at IS NOT NULL)),
    CONSTRAINT ck_maintenance_entitlement_period CHECK (plan_month >= 1 AND period_ends_at > period_starts_at),
    CONSTRAINT ck_maintenance_entitlement_status CHECK (status IN ('AVAILABLE', 'BOOKED', 'CONSUMED', 'NOT_COMPLETED', 'EXPIRED', 'EXPIRED_UNFULFILLED')),
    CONSTRAINT fk_maintenance_entitlement_subscription FOREIGN KEY (subscription_id) REFERENCES service_subscription (id) ON DELETE RESTRICT
);

CREATE TABLE service_request (
    id uuid NOT NULL,
    request_number character varying(30) NOT NULL,
    kind character varying(30) NOT NULL,
    status character varying(30) NOT NULL,
    customer_user_id uuid NOT NULL,
    project_id uuid NOT NULL,
    property_site_id uuid NOT NULL,
    description character varying(2000) NOT NULL,
    suspected_asset_id uuid,
    preferred_starts_at timestamp with time zone,
    installation_warranty_ends_at timestamp with time zone,
    within_installation_warranty boolean NOT NULL,
    warranty_source_request_id uuid,
    within_repair_warranty boolean NOT NULL,
    maintenance_entitlement_id uuid,
    origin_request_id uuid,
    duplicate_of_request_id uuid,
    outcome character varying(30),
    customer_decision_reason character varying(1000),
    acceptance_requested_at timestamp with time zone,
    last_acceptance_reminder_at timestamp with time zone,
    completed_at timestamp with time zone,
    repair_warranty_months integer,
    repair_warranty_starts_at timestamp with time zone,
    repair_warranty_ends_at timestamp with time zone,
    cancellation_requested_at timestamp with time zone,
    cancellation_reason character varying(1000),
    cancelled_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_service_request" PRIMARY KEY (id),
    CONSTRAINT ck_service_request_cancelled CHECK (status <> 'CANCELLED' OR cancelled_at IS NOT NULL),
    CONSTRAINT ck_service_request_kind CHECK (kind IN ('FAULT', 'MAINTENANCE', 'ONE_TIME_MAINTENANCE')),
    CONSTRAINT ck_service_request_maintenance_link CHECK ((kind = 'MAINTENANCE') = (maintenance_entitlement_id IS NOT NULL)),
    CONSTRAINT ck_service_request_repair_warranty CHECK ((repair_warranty_starts_at IS NULL) = (repair_warranty_ends_at IS NULL) AND (repair_warranty_ends_at IS NULL OR repair_warranty_ends_at > repair_warranty_starts_at)),
    CONSTRAINT ck_service_request_status CHECK (status IN ('SUBMITTED', 'SCHEDULED', 'IN_PROGRESS', 'AWAITING_DECISION', 'AWAITING_QUOTATION', 'AWAITING_CUSTOMER', 'READY_FOR_WORK', 'PENDING_CUSTOMER_ACCEPTANCE', 'DISPUTED', 'REWORK_REQUIRED', 'COMPLETED', 'NOT_COMPLETED', 'CANCELLED', 'CLOSED_DUPLICATE')),
    CONSTRAINT fk_service_request_customer FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_request_duplicate_of FOREIGN KEY (duplicate_of_request_id) REFERENCES service_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_request_entitlement FOREIGN KEY (maintenance_entitlement_id) REFERENCES maintenance_entitlement (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_request_origin FOREIGN KEY (origin_request_id) REFERENCES service_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_request_project FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_request_property_site FOREIGN KEY (property_site_id) REFERENCES property_site (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_request_suspected_asset FOREIGN KEY (suspected_asset_id) REFERENCES installed_asset (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_request_warranty_source FOREIGN KEY (warranty_source_request_id) REFERENCES service_request (id) ON DELETE RESTRICT
);

CREATE TABLE after_sales_notification (
    id uuid NOT NULL,
    recipient_user_id uuid NOT NULL,
    type character varying(50) NOT NULL,
    message character varying(500) NOT NULL,
    request_id uuid,
    subscription_id uuid,
    created_at timestamp with time zone NOT NULL,
    read_at timestamp with time zone,
    CONSTRAINT "PK_after_sales_notification" PRIMARY KEY (id),
    CONSTRAINT fk_after_sales_notification_recipient FOREIGN KEY (recipient_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_after_sales_notification_request FOREIGN KEY (request_id) REFERENCES service_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_after_sales_notification_subscription FOREIGN KEY (subscription_id) REFERENCES service_subscription (id) ON DELETE RESTRICT
);

CREATE TABLE disaster_claim (
    id uuid NOT NULL,
    request_id uuid NOT NULL,
    subscription_id uuid,
    disaster_type character varying(20) NOT NULL,
    incident_occurred_at timestamp with time zone NOT NULL,
    status character varying(30) NOT NULL,
    decision_reason character varying(1000),
    decided_by_user_id uuid,
    decided_at timestamp with time zone,
    reconsideration_requested_at timestamp with time zone,
    reconsideration_note character varying(1000),
    first_decision_reason character varying(1000),
    coverage_percent numeric(5,2) NOT NULL,
    per_incident_limit numeric(18,2) NOT NULL,
    support_reserved numeric(18,2) NOT NULL,
    support_consumed numeric(18,2) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_disaster_claim" PRIMARY KEY (id),
    CONSTRAINT ck_disaster_claim_status CHECK (status IN ('NOT_ELIGIBLE', 'SUBMITTED', 'APPROVED', 'REJECTED', 'RECONSIDERATION_REQUESTED')),
    CONSTRAINT ck_disaster_claim_support CHECK (coverage_percent BETWEEN 0 AND 100 AND support_reserved >= 0 AND support_consumed >= 0 AND support_reserved + support_consumed <= per_incident_limit),
    CONSTRAINT ck_disaster_claim_type CHECK (disaster_type IN ('STORM', 'FLOOD', 'LIGHTNING')),
    CONSTRAINT fk_disaster_claim_decided_by FOREIGN KEY (decided_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_disaster_claim_request FOREIGN KEY (request_id) REFERENCES service_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_disaster_claim_subscription FOREIGN KEY (subscription_id) REFERENCES service_subscription (id) ON DELETE RESTRICT
);

CREATE TABLE sales_task (
    id uuid NOT NULL,
    task_type character varying(30) NOT NULL,
    status character varying(20) NOT NULL,
    request_id uuid,
    entitlement_id uuid,
    customer_user_id uuid NOT NULL,
    assigned_sales_user_id uuid,
    claimed_at timestamp with time zone,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    outcome character varying(30),
    note character varying(2000),
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_sales_task" PRIMARY KEY (id),
    CONSTRAINT ck_sales_task_owner CHECK (status = 'OPEN' OR status = 'CANCELLED' OR assigned_sales_user_id IS NOT NULL),
    CONSTRAINT ck_sales_task_status CHECK (status IN ('OPEN', 'CLAIMED', 'IN_PROGRESS', 'DONE', 'CANCELLED')),
    CONSTRAINT ck_sales_task_type CHECK (task_type IN ('REPAIR_QUOTATION', 'MAINTENANCE_QUOTATION', 'MAINTENANCE_FOLLOW_UP', 'ACCEPTANCE_FOLLOW_UP')),
    CONSTRAINT fk_sales_task_assignee FOREIGN KEY (assigned_sales_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_sales_task_customer FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_sales_task_entitlement FOREIGN KEY (entitlement_id) REFERENCES maintenance_entitlement (id) ON DELETE RESTRICT,
    CONSTRAINT fk_sales_task_request FOREIGN KEY (request_id) REFERENCES service_request (id) ON DELETE RESTRICT
);

CREATE TABLE service_visit (
    id uuid NOT NULL,
    request_id uuid NOT NULL,
    purpose character varying(20) NOT NULL,
    status character varying(20) NOT NULL,
    technician_user_id uuid,
    starts_at timestamp with time zone NOT NULL,
    ends_at timestamp with time zone NOT NULL,
    company_reschedule_reason character varying(500),
    customer_confirmation_required boolean NOT NULL,
    customer_confirmed_at timestamp with time zone,
    started_at timestamp with time zone,
    report character varying(4000),
    reported_at timestamp with time zone,
    finding_note character varying(2000),
    assigned_by_user_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_service_visit" PRIMARY KEY (id),
    CONSTRAINT ck_service_visit_purpose CHECK (purpose IN ('INSPECTION', 'REPAIR', 'MAINTENANCE', 'REWORK')),
    CONSTRAINT ck_service_visit_range CHECK (ends_at > starts_at),
    CONSTRAINT ck_service_visit_reported CHECK (status <> 'REPORTED' OR (reported_at IS NOT NULL AND report IS NOT NULL)),
    CONSTRAINT ck_service_visit_status CHECK (status IN ('SCHEDULED', 'IN_PROGRESS', 'REPORTED', 'CUSTOMER_ABSENT', 'DECLINED', 'CANCELLED')),
    CONSTRAINT fk_service_visit_assigned_by FOREIGN KEY (assigned_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_visit_request FOREIGN KEY (request_id) REFERENCES service_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_visit_technician FOREIGN KEY (technician_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE service_quotation (
    id uuid NOT NULL,
    request_id uuid NOT NULL,
    version integer NOT NULL,
    is_supplementary boolean NOT NULL,
    supersedes_quotation_id uuid,
    status character varying(20) NOT NULL,
    sales_user_id uuid NOT NULL,
    lines jsonb NOT NULL,
    parts_amount numeric(18,2) NOT NULL,
    labor_amount numeric(18,2) NOT NULL,
    total_amount numeric(18,2) NOT NULL,
    disaster_support_amount numeric(18,2) NOT NULL,
    customer_amount numeric(18,2) NOT NULL,
    disaster_claim_id uuid,
    repair_warranty_months integer,
    note character varying(1000),
    issued_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    accepted_at timestamp with time zone,
    rejected_at timestamp with time zone,
    paid_at timestamp with time zone,
    current_payment_attempt_id uuid,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_service_quotation" PRIMARY KEY (id),
    CONSTRAINT ck_service_quotation_amounts CHECK (parts_amount >= 0 AND labor_amount >= 0 AND total_amount = parts_amount + labor_amount AND total_amount > 0 AND disaster_support_amount >= 0 AND disaster_support_amount <= total_amount AND customer_amount = total_amount - disaster_support_amount),
    CONSTRAINT ck_service_quotation_expiry CHECK (expires_at > issued_at),
    CONSTRAINT ck_service_quotation_paid CHECK (status <> 'PAID' OR paid_at IS NOT NULL),
    CONSTRAINT ck_service_quotation_status CHECK (status IN ('ISSUED', 'ACCEPTED', 'PAID', 'REJECTED', 'SUPERSEDED', 'EXPIRED', 'CANCELLED')),
    CONSTRAINT fk_service_quotation_claim FOREIGN KEY (disaster_claim_id) REFERENCES disaster_claim (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_quotation_request FOREIGN KEY (request_id) REFERENCES service_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_quotation_sales FOREIGN KEY (sales_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_quotation_supersedes FOREIGN KEY (supersedes_quotation_id) REFERENCES service_quotation (id) ON DELETE RESTRICT
);

CREATE TABLE service_evidence (
    id uuid NOT NULL,
    request_id uuid NOT NULL,
    visit_id uuid,
    file_id uuid NOT NULL,
    caption character varying(300),
    added_by_user_id uuid NOT NULL,
    added_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_service_evidence" PRIMARY KEY (id),
    CONSTRAINT fk_service_evidence_added_by FOREIGN KEY (added_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_evidence_file FOREIGN KEY (file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_evidence_request FOREIGN KEY (request_id) REFERENCES service_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_evidence_visit FOREIGN KEY (visit_id) REFERENCES service_visit (id) ON DELETE RESTRICT
);

CREATE TABLE payment_attempt_item (
    id uuid NOT NULL,
    payment_attempt_id uuid NOT NULL,
    item_type character varying(30) NOT NULL,
    contract_id uuid,
    subscription_id uuid,
    quotation_id uuid,
    amount numeric(18,2) NOT NULL,
    status character varying(20) NOT NULL,
    note character varying(300),
    CONSTRAINT "PK_payment_attempt_item" PRIMARY KEY (id),
    CONSTRAINT ck_payment_attempt_item_amount CHECK (amount > 0),
    CONSTRAINT ck_payment_attempt_item_status CHECK (status IN ('PENDING', 'SETTLED', 'REVIEW_REQUIRED')),
    CONSTRAINT ck_payment_attempt_item_target CHECK ((CASE WHEN contract_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN subscription_id IS NULL THEN 0 ELSE 1 END) + (CASE WHEN quotation_id IS NULL THEN 0 ELSE 1 END) = 1),
    CONSTRAINT ck_payment_attempt_item_type CHECK (item_type IN ('CONTRACT', 'MAINTENANCE_PACKAGE', 'DISASTER_PACKAGE', 'REPAIR_QUOTATION', 'MAINTENANCE_QUOTATION')),
    CONSTRAINT fk_payment_attempt_item_attempt FOREIGN KEY (payment_attempt_id) REFERENCES payment_attempt (id) ON DELETE RESTRICT,
    CONSTRAINT fk_payment_attempt_item_contract FOREIGN KEY (contract_id) REFERENCES customer_contract (id) ON DELETE RESTRICT,
    CONSTRAINT fk_payment_attempt_item_quotation FOREIGN KEY (quotation_id) REFERENCES service_quotation (id) ON DELETE RESTRICT,
    CONSTRAINT fk_payment_attempt_item_subscription FOREIGN KEY (subscription_id) REFERENCES service_subscription (id) ON DELETE RESTRICT
);

CREATE TABLE refund_record (
    id uuid NOT NULL,
    customer_user_id uuid NOT NULL,
    payment_attempt_id uuid,
    subscription_id uuid,
    quotation_id uuid,
    request_id uuid,
    original_amount numeric(18,2) NOT NULL,
    approved_amount numeric(18,2),
    reason character varying(1000) NOT NULL,
    status character varying(20) NOT NULL,
    approved_by_user_id uuid,
    approved_at timestamp with time zone,
    decision_note character varying(1000),
    recorded_by_user_id uuid,
    refunded_at timestamp with time zone,
    refund_reference character varying(100),
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_refund_record" PRIMARY KEY (id),
    CONSTRAINT ck_refund_record_amounts CHECK (original_amount >= 0 AND (approved_amount IS NULL OR approved_amount BETWEEN 0 AND original_amount)),
    CONSTRAINT ck_refund_record_refunded CHECK (status <> 'REFUNDED' OR (refunded_at IS NOT NULL AND recorded_by_user_id IS NOT NULL AND approved_amount IS NOT NULL AND approved_by_user_id IS NOT NULL)),
    CONSTRAINT ck_refund_record_status CHECK (status IN ('PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'REFUNDED')),
    CONSTRAINT fk_refund_record_approved_by FOREIGN KEY (approved_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_refund_record_customer FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_refund_record_payment_attempt FOREIGN KEY (payment_attempt_id) REFERENCES payment_attempt (id) ON DELETE RESTRICT,
    CONSTRAINT fk_refund_record_quotation FOREIGN KEY (quotation_id) REFERENCES service_quotation (id) ON DELETE RESTRICT,
    CONSTRAINT fk_refund_record_recorded_by FOREIGN KEY (recorded_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_refund_record_request FOREIGN KEY (request_id) REFERENCES service_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_refund_record_subscription FOREIGN KEY (subscription_id) REFERENCES service_subscription (id) ON DELETE RESTRICT
);

CREATE TABLE service_item (
    id uuid NOT NULL,
    request_id uuid NOT NULL,
    asset_id uuid,
    description character varying(500) NOT NULL,
    diagnosis character varying(2000) NOT NULL,
    required_parts character varying(1000),
    coverage character varying(30) NOT NULL,
    status character varying(20) NOT NULL,
    is_supplementary boolean NOT NULL,
    quotation_id uuid,
    coverage_reason character varying(1000),
    work_result character varying(2000),
    parts_used character varying(1000),
    reported_by_user_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_service_item" PRIMARY KEY (id),
    CONSTRAINT ck_service_item_coverage CHECK (coverage IN ('PENDING', 'INSTALLATION_WARRANTY', 'REPAIR_WARRANTY', 'DISASTER_PROTECTION', 'CHARGEABLE')),
    CONSTRAINT ck_service_item_status CHECK (status IN ('OPEN', 'QUOTED', 'AUTHORIZED', 'DECLINED', 'DONE')),
    CONSTRAINT fk_service_item_asset FOREIGN KEY (asset_id) REFERENCES installed_asset (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_item_quotation FOREIGN KEY (quotation_id) REFERENCES service_quotation (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_item_reported_by FOREIGN KEY (reported_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_item_request FOREIGN KEY (request_id) REFERENCES service_request (id) ON DELETE RESTRICT
);

CREATE INDEX "IX_installed_asset_replaces_asset_id" ON installed_asset (replaces_asset_id);

CREATE INDEX "IX_installed_asset_service_request_id" ON installed_asset (service_request_id);

ALTER TABLE installed_asset ADD CONSTRAINT ck_installed_asset_status CHECK (status IN ('ACTIVE', 'PENDING_ACCEPTANCE', 'REPLACED'));

CREATE UNIQUE INDEX ux_installation_assignment_current ON installation_assignment (project_id) WHERE status IN ('PENDING', 'ACCEPTED') AND service_visit_id IS NULL;

CREATE UNIQUE INDEX ux_installation_assignment_current_visit ON installation_assignment (service_visit_id) WHERE status IN ('PENDING', 'ACCEPTED') AND service_visit_id IS NOT NULL;

ALTER TABLE file_reference ADD CONSTRAINT ck_file_reference_purpose CHECK (purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT', 'SERVICE_EVIDENCE'));

CREATE INDEX "IX_after_sales_event_actor_user_id" ON after_sales_event (actor_user_id);

CREATE INDEX ix_after_sales_event_subject ON after_sales_event (subject_type, subject_id, created_at);

CREATE INDEX ix_after_sales_notification_recipient ON after_sales_notification (recipient_user_id, created_at);

CREATE INDEX "IX_after_sales_notification_request_id" ON after_sales_notification (request_id);

CREATE INDEX "IX_after_sales_notification_subscription_id" ON after_sales_notification (subscription_id);

CREATE INDEX "IX_disaster_claim_decided_by_user_id" ON disaster_claim (decided_by_user_id);

CREATE INDEX ix_disaster_claim_subscription ON disaster_claim (subscription_id);

CREATE UNIQUE INDEX ux_disaster_claim_request ON disaster_claim (request_id);

CREATE INDEX "IX_maintenance_entitlement_current_request_id" ON maintenance_entitlement (current_request_id);

CREATE INDEX ix_maintenance_entitlement_status_period ON maintenance_entitlement (status, period_starts_at);

CREATE UNIQUE INDEX ux_maintenance_entitlement_subscription_month ON maintenance_entitlement (subscription_id, plan_month);

CREATE INDEX ix_payment_attempt_item_attempt ON payment_attempt_item (payment_attempt_id);

CREATE INDEX "IX_payment_attempt_item_contract_id" ON payment_attempt_item (contract_id);

CREATE INDEX ix_payment_attempt_item_quotation ON payment_attempt_item (quotation_id);

CREATE INDEX ix_payment_attempt_item_subscription ON payment_attempt_item (subscription_id);

CREATE INDEX "IX_refund_record_approved_by_user_id" ON refund_record (approved_by_user_id);

CREATE INDEX "IX_refund_record_customer_user_id" ON refund_record (customer_user_id);

CREATE INDEX "IX_refund_record_payment_attempt_id" ON refund_record (payment_attempt_id);

CREATE INDEX "IX_refund_record_quotation_id" ON refund_record (quotation_id);

CREATE INDEX "IX_refund_record_recorded_by_user_id" ON refund_record (recorded_by_user_id);

CREATE INDEX ix_refund_record_refunded_at ON refund_record (refunded_at);

CREATE INDEX "IX_refund_record_request_id" ON refund_record (request_id);

CREATE INDEX ix_refund_record_status_created_at ON refund_record (status, created_at);

CREATE UNIQUE INDEX ux_payment_attempt_item_attempt_contract ON payment_attempt_item (payment_attempt_id, contract_id) WHERE contract_id IS NOT NULL;

CREATE UNIQUE INDEX ux_payment_attempt_item_attempt_quotation ON payment_attempt_item (payment_attempt_id, quotation_id) WHERE quotation_id IS NOT NULL;

CREATE UNIQUE INDEX ux_payment_attempt_item_attempt_subscription ON payment_attempt_item (payment_attempt_id, subscription_id) WHERE subscription_id IS NOT NULL;

CREATE UNIQUE INDEX ux_payment_attempt_item_settled_quotation ON payment_attempt_item (quotation_id) WHERE quotation_id IS NOT NULL AND status = 'SETTLED';

CREATE UNIQUE INDEX ux_payment_attempt_item_settled_subscription ON payment_attempt_item (subscription_id) WHERE subscription_id IS NOT NULL AND status = 'SETTLED';

CREATE INDEX "IX_refund_record_subscription_id" ON refund_record (subscription_id);

CREATE INDEX ix_sales_task_assignee_status ON sales_task (assigned_sales_user_id, status);

CREATE INDEX "IX_sales_task_customer_user_id" ON sales_task (customer_user_id);

CREATE INDEX ix_sales_task_status_created_at ON sales_task (status, created_at);

CREATE UNIQUE INDEX ux_sales_task_open_entitlement ON sales_task (entitlement_id, task_type) WHERE entitlement_id IS NOT NULL AND status IN ('OPEN', 'CLAIMED', 'IN_PROGRESS');

CREATE UNIQUE INDEX ux_sales_task_open_request ON sales_task (request_id, task_type) WHERE request_id IS NOT NULL AND status IN ('OPEN', 'CLAIMED', 'IN_PROGRESS');

CREATE INDEX "IX_service_evidence_added_by_user_id" ON service_evidence (added_by_user_id);

CREATE INDEX ix_service_evidence_request ON service_evidence (request_id);

CREATE INDEX "IX_service_evidence_visit_id" ON service_evidence (visit_id);

CREATE UNIQUE INDEX ux_service_evidence_file ON service_evidence (file_id);

CREATE INDEX "IX_service_item_asset_id" ON service_item (asset_id);

CREATE INDEX "IX_service_item_quotation_id" ON service_item (quotation_id);

CREATE INDEX "IX_service_item_reported_by_user_id" ON service_item (reported_by_user_id);

CREATE INDEX ix_service_item_request ON service_item (request_id);

CREATE INDEX ix_service_package_kind_active ON service_package (kind, is_active);

CREATE INDEX "IX_service_quotation_current_payment_attempt_id" ON service_quotation (current_payment_attempt_id);

CREATE INDEX "IX_service_quotation_disaster_claim_id" ON service_quotation (disaster_claim_id);

CREATE INDEX "IX_service_quotation_sales_user_id" ON service_quotation (sales_user_id);

CREATE INDEX ix_service_quotation_status_expires_at ON service_quotation (status, expires_at);

CREATE INDEX "IX_service_quotation_supersedes_quotation_id" ON service_quotation (supersedes_quotation_id);

CREATE UNIQUE INDEX ux_service_quotation_one_open ON service_quotation (request_id) WHERE status IN ('ISSUED', 'ACCEPTED');

CREATE UNIQUE INDEX ux_service_quotation_request_version ON service_quotation (request_id, version);

CREATE INDEX ix_service_request_customer_created_at ON service_request (customer_user_id, created_at);

CREATE INDEX "IX_service_request_duplicate_of_request_id" ON service_request (duplicate_of_request_id);

CREATE INDEX "IX_service_request_origin_request_id" ON service_request (origin_request_id);

CREATE INDEX ix_service_request_project_status ON service_request (project_id, status);

CREATE INDEX "IX_service_request_property_site_id" ON service_request (property_site_id);

CREATE INDEX ix_service_request_status_updated_at ON service_request (status, updated_at);

CREATE INDEX "IX_service_request_suspected_asset_id" ON service_request (suspected_asset_id);

CREATE INDEX "IX_service_request_warranty_source_request_id" ON service_request (warranty_source_request_id);

CREATE UNIQUE INDEX ux_service_request_live_entitlement ON service_request (maintenance_entitlement_id) WHERE maintenance_entitlement_id IS NOT NULL AND status NOT IN ('COMPLETED', 'NOT_COMPLETED', 'CANCELLED', 'CLOSED_DUPLICATE');

CREATE UNIQUE INDEX ux_service_request_number ON service_request (request_number);

CREATE INDEX ix_service_subscription_contract_kind_status ON service_subscription (contract_id, kind, status);

CREATE INDEX "IX_service_subscription_current_payment_attempt_id" ON service_subscription (current_payment_attempt_id);

CREATE INDEX ix_service_subscription_customer_created_at ON service_subscription (customer_user_id, created_at);

CREATE INDEX "IX_service_subscription_package_id" ON service_subscription (package_id);

CREATE INDEX "IX_service_subscription_project_id" ON service_subscription (project_id);

CREATE INDEX "IX_service_subscription_renews_subscription_id" ON service_subscription (renews_subscription_id);

CREATE INDEX ix_service_subscription_status_starts_at ON service_subscription (status, starts_at);

CREATE INDEX "IX_service_visit_assigned_by_user_id" ON service_visit (assigned_by_user_id);

CREATE INDEX ix_service_visit_request ON service_visit (request_id);

CREATE INDEX ix_service_visit_technician_starts_at ON service_visit (technician_user_id, starts_at);

CREATE UNIQUE INDEX ux_warranty_option_kind_months ON warranty_option (kind, months);

CREATE UNIQUE INDEX ux_warranty_option_one_default ON warranty_option (kind) WHERE is_default;

ALTER TABLE installation_assignment ADD CONSTRAINT fk_installation_assignment_service_visit FOREIGN KEY (service_visit_id) REFERENCES service_visit (id) ON DELETE RESTRICT;

ALTER TABLE installed_asset ADD CONSTRAINT fk_installed_asset_replaces FOREIGN KEY (replaces_asset_id) REFERENCES installed_asset (id) ON DELETE RESTRICT;

ALTER TABLE installed_asset ADD CONSTRAINT fk_installed_asset_service_request FOREIGN KEY (service_request_id) REFERENCES service_request (id) ON DELETE RESTRICT;

ALTER TABLE maintenance_entitlement ADD CONSTRAINT fk_maintenance_entitlement_current_request FOREIGN KEY (current_request_id) REFERENCES service_request (id) ON DELETE RESTRICT;

ALTER TABLE service_quotation ADD CONSTRAINT fk_service_quotation_current_payment_attempt FOREIGN KEY (current_payment_attempt_id) REFERENCES payment_attempt (id) ON DELETE RESTRICT;

ALTER TABLE service_subscription ADD CONSTRAINT fk_service_subscription_current_payment_attempt FOREIGN KEY (current_payment_attempt_id) REFERENCES payment_attempt (id) ON DELETE RESTRICT;

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

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261021023128_AfterSalesServices', '8.0.32');

COMMIT;

