START TRANSACTION;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_assignment_source;

ALTER TABLE property_site DROP CONSTRAINT ck_property_site_district_code;

ALTER TABLE property_site DROP CONSTRAINT ck_property_site_ward_code;

ALTER TABLE technician_appointment ADD operational_zone_id uuid;

CREATE INDEX ix_technician_appointment_operational_zone_id ON technician_appointment (operational_zone_id);

ALTER TABLE installation_project ADD operational_zone_id uuid;

CREATE INDEX "IX_installation_project_operational_zone_id" ON installation_project (operational_zone_id);

ALTER TABLE technician_appointment ADD acceptance_reminder_sent_at timestamp with time zone;

ALTER TABLE technician_appointment ADD acceptance_window_minutes integer;

ALTER TABLE technician_appointment ADD cross_zone_authorization_id uuid;

ALTER TABLE technician_appointment ADD settings_version integer;

ALTER TABLE property_site ADD commune_code character varying(5);

CREATE TABLE administrative_catalog_import (
    id uuid NOT NULL,
    dataset_key character varying(100) NOT NULL,
    source_url character varying(500) NOT NULL,
    source_sha256 character varying(64) NOT NULL,
    effective_from date NOT NULL,
    files_sha256 jsonb NOT NULL,
    counts jsonb NOT NULL,
    status character varying(20) NOT NULL,
    validated_at timestamp with time zone NOT NULL,
    applied_at timestamp with time zone,
    applied_by character varying(200),
    notes character varying(4000),
    CONSTRAINT "PK_administrative_catalog_import" PRIMARY KEY (id),
    CONSTRAINT ck_administrative_catalog_import_applied CHECK ((status = 'APPLIED') = (applied_at IS NOT NULL)),
    CONSTRAINT ck_administrative_catalog_import_status CHECK (status IN ('VALIDATED', 'APPLIED', 'REJECTED'))
);

CREATE TABLE administrative_province (
    code character varying(2) NOT NULL,
    name character varying(150) NOT NULL,
    unit_type character varying(20) NOT NULL,
    valid_from date NOT NULL,
    valid_to date,
    dataset_key character varying(100) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_administrative_province" PRIMARY KEY (code),
    CONSTRAINT ck_administrative_province_code CHECK (length(code) = 2 AND substr(code, 1, 1) BETWEEN '0' AND '9' AND substr(code, 2, 1) BETWEEN '0' AND '9'),
    CONSTRAINT ck_administrative_province_unit_type CHECK (unit_type IN ('PROVINCE', 'MUNICIPALITY')),
    CONSTRAINT ck_administrative_province_validity CHECK (valid_to IS NULL OR valid_to > valid_from)
);

CREATE TABLE operational_zone (
    id uuid NOT NULL,
    code character varying(50) NOT NULL,
    name character varying(150) NOT NULL,
    is_active boolean NOT NULL,
    is_serviceable boolean NOT NULL DEFAULT TRUE,
    responsible_manager_user_id uuid,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    created_by_user_id uuid,
    updated_by_user_id uuid,
    revision integer NOT NULL,
    CONSTRAINT "PK_operational_zone" PRIMARY KEY (id),
    CONSTRAINT ck_operational_zone_code CHECK (length(code) BETWEEN 2 AND 50 AND code = upper(code) AND code NOT LIKE '% %' AND code NOT LIKE '-%'),
    CONSTRAINT fk_operational_zone_created_by FOREIGN KEY (created_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_operational_zone_responsible_manager FOREIGN KEY (responsible_manager_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_operational_zone_updated_by FOREIGN KEY (updated_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE system_setting (
    key character varying(100) NOT NULL,
    value_int integer NOT NULL,
    unit character varying(20) NOT NULL,
    version integer NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    updated_by_user_id uuid,
    revision integer NOT NULL,
    CONSTRAINT "PK_system_setting" PRIMARY KEY (key),
    CONSTRAINT ck_system_setting_key CHECK (length(key) > 0),
    CONSTRAINT ck_system_setting_unit CHECK (unit IN ('MINUTES', 'HOURS', 'DAYS')),
    CONSTRAINT ck_system_setting_version CHECK (version >= 1),
    CONSTRAINT fk_system_setting_user_account_updated_by_user_id FOREIGN KEY (updated_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE system_setting_revision (
    id uuid NOT NULL,
    key character varying(100) NOT NULL,
    version integer NOT NULL,
    old_value integer NOT NULL,
    new_value integer NOT NULL,
    unit character varying(20) NOT NULL,
    changed_by uuid NOT NULL,
    changed_at timestamp with time zone NOT NULL,
    reason character varying(500),
    CONSTRAINT "PK_system_setting_revision" PRIMARY KEY (id),
    CONSTRAINT ck_system_setting_revision_unit CHECK (unit IN ('MINUTES', 'HOURS', 'DAYS')),
    CONSTRAINT ck_system_setting_revision_version CHECK (version >= 1),
    CONSTRAINT fk_system_setting_revision_user_account_changed_by FOREIGN KEY (changed_by) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE system_settings_version (
    id integer NOT NULL,
    version integer NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    updated_by_user_id uuid,
    revision integer NOT NULL,
    CONSTRAINT "PK_system_settings_version" PRIMARY KEY (id),
    CONSTRAINT ck_system_settings_version_singleton CHECK (id = 1),
    CONSTRAINT ck_system_settings_version_version CHECK (version >= 1),
    CONSTRAINT fk_system_settings_version_user_account_updated_by_user_id FOREIGN KEY (updated_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE administrative_commune (
    code character varying(5) NOT NULL,
    province_code character varying(2) NOT NULL,
    name character varying(150) NOT NULL,
    search_name character varying(150) NOT NULL,
    unit_type character varying(20) NOT NULL,
    valid_from date NOT NULL,
    valid_to date,
    dataset_key character varying(100) NOT NULL,
    CONSTRAINT "PK_administrative_commune" PRIMARY KEY (code),
    CONSTRAINT ux_administrative_commune_province_code UNIQUE (province_code, code),
    CONSTRAINT ck_administrative_commune_code CHECK (length(code) = 5 AND substr(code, 1, 1) BETWEEN '0' AND '9' AND substr(code, 2, 1) BETWEEN '0' AND '9' AND substr(code, 3, 1) BETWEEN '0' AND '9' AND substr(code, 4, 1) BETWEEN '0' AND '9' AND substr(code, 5, 1) BETWEEN '0' AND '9'),
    CONSTRAINT ck_administrative_commune_unit_type CHECK (unit_type IN ('WARD', 'COMMUNE', 'SPECIAL_ZONE')),
    CONSTRAINT ck_administrative_commune_validity CHECK (valid_to IS NULL OR valid_to > valid_from),
    CONSTRAINT fk_administrative_commune_province FOREIGN KEY (province_code) REFERENCES administrative_province (code) ON DELETE RESTRICT
);

CREATE TABLE dispatch_request (
    id uuid NOT NULL,
    survey_request_id uuid NOT NULL,
    property_site_id uuid NOT NULL,
    customer_user_id uuid NOT NULL,
    operational_zone_id uuid,
    reason character varying(40) NOT NULL,
    status character varying(30) NOT NULL,
    customer_note character varying(1000),
    review_sla_minutes integer NOT NULL,
    review_due_at timestamp with time zone NOT NULL,
    escalation_due_at timestamp with time zone NOT NULL,
    reminder_interval_minutes integer NOT NULL,
    settings_version integer NOT NULL,
    overdue_at timestamp with time zone,
    escalated_at timestamp with time zone,
    last_reminder_at timestamp with time zone,
    reminder_count integer NOT NULL,
    resolved_at timestamp with time zone,
    resolved_by_user_id uuid,
    resolution character varying(30),
    resolution_reason character varying(500),
    appointment_id uuid,
    released_appointment_id uuid,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_dispatch_request" PRIMARY KEY (id),
    CONSTRAINT ck_dispatch_request_reason CHECK (reason IN ('NO_ZONE_COVERAGE', 'ZONE_NOT_SERVICEABLE', 'NO_ZONE_TECHNICIAN', 'NO_AVAILABLE_TECHNICIAN', 'SCHEDULE_CONFLICT', 'ASSIGNMENT_RELEASED')),
    CONSTRAINT ck_dispatch_request_reminders CHECK (reminder_interval_minutes >= 0 AND reminder_count >= 0),
    CONSTRAINT ck_dispatch_request_resolution CHECK (resolution IS NULL OR resolution IN ('ASSIGNED', 'UNSERVICEABLE', 'CLOSED_BY_STAFF', 'CANCELLED_BY_CUSTOMER')),
    CONSTRAINT ck_dispatch_request_resolved CHECK ((status IN ('OPEN', 'PROPOSAL_PENDING')) = (resolved_at IS NULL AND resolution IS NULL)),
    CONSTRAINT ck_dispatch_request_sla CHECK (review_sla_minutes > 0 AND review_due_at > created_at AND escalation_due_at >= review_due_at),
    CONSTRAINT ck_dispatch_request_status CHECK (status IN ('OPEN', 'PROPOSAL_PENDING', 'ASSIGNED', 'UNSERVICEABLE', 'CLOSED')),
    CONSTRAINT ck_dispatch_request_terminal CHECK ((status <> 'ASSIGNED' OR (resolution = 'ASSIGNED' AND appointment_id IS NOT NULL)) AND (status <> 'UNSERVICEABLE' OR resolution = 'UNSERVICEABLE') AND (status <> 'CLOSED' OR resolution IN ('CLOSED_BY_STAFF', 'CANCELLED_BY_CUSTOMER'))),
    CONSTRAINT "FK_dispatch_request_property_site_property_site_id" FOREIGN KEY (property_site_id) REFERENCES property_site (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_dispatch_request_survey_request_survey_request_id" FOREIGN KEY (survey_request_id) REFERENCES survey_request (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_dispatch_request_user_account_customer_user_id" FOREIGN KEY (customer_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_dispatch_request_user_account_resolved_by_user_id" FOREIGN KEY (resolved_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_dispatch_request_appointment FOREIGN KEY (appointment_id) REFERENCES technician_appointment (id) ON DELETE RESTRICT,
    CONSTRAINT fk_dispatch_request_operational_zone FOREIGN KEY (operational_zone_id) REFERENCES operational_zone (id) ON DELETE RESTRICT,
    CONSTRAINT fk_dispatch_request_released_appointment FOREIGN KEY (released_appointment_id) REFERENCES technician_appointment (id) ON DELETE RESTRICT
);

CREATE TABLE technician_zone_membership (
    id uuid NOT NULL,
    technician_user_id uuid NOT NULL,
    zone_id uuid NOT NULL,
    assigned_by_user_id uuid,
    assigned_at timestamp with time zone NOT NULL,
    removed_at timestamp with time zone,
    removed_by_user_id uuid,
    CONSTRAINT "PK_technician_zone_membership" PRIMARY KEY (id),
    CONSTRAINT ck_technician_zone_membership_removed CHECK (removed_at IS NULL OR removed_by_user_id IS NOT NULL),
    CONSTRAINT fk_technician_zone_membership_assigned_by FOREIGN KEY (assigned_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_technician_zone_membership_removed_by FOREIGN KEY (removed_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_technician_zone_membership_technician_profile FOREIGN KEY (technician_user_id) REFERENCES technician_profile (user_id) ON DELETE RESTRICT,
    CONSTRAINT fk_technician_zone_membership_zone FOREIGN KEY (zone_id) REFERENCES operational_zone (id) ON DELETE RESTRICT
);

CREATE TABLE administrative_legacy_commune_map (
    id uuid NOT NULL,
    commune_code character varying(5) NOT NULL,
    legacy_commune_code character varying(5),
    legacy_commune_name character varying(150),
    legacy_commune_search_name character varying(150),
    legacy_district_code character varying(3) NOT NULL,
    legacy_district_name character varying(150) NOT NULL,
    legacy_district_search_name character varying(150) NOT NULL,
    legacy_province_code character varying(2) NOT NULL,
    legacy_province_name character varying(150) NOT NULL,
    merge_type character varying(20) NOT NULL,
    source_note character varying(500),
    match_key character varying(12) NOT NULL,
    dataset_key character varying(100) NOT NULL,
    CONSTRAINT "PK_administrative_legacy_commune_map" PRIMARY KEY (id),
    CONSTRAINT ck_administrative_legacy_commune_map_commune_pair CHECK ((legacy_commune_code IS NULL) = (legacy_commune_name IS NULL) AND (legacy_commune_name IS NULL) = (legacy_commune_search_name IS NULL)),
    CONSTRAINT ck_administrative_legacy_commune_map_legacy_commune_code CHECK (legacy_commune_code IS NULL OR (length(legacy_commune_code) = 5 AND substr(legacy_commune_code, 1, 1) BETWEEN '0' AND '9' AND substr(legacy_commune_code, 2, 1) BETWEEN '0' AND '9' AND substr(legacy_commune_code, 3, 1) BETWEEN '0' AND '9' AND substr(legacy_commune_code, 4, 1) BETWEEN '0' AND '9' AND substr(legacy_commune_code, 5, 1) BETWEEN '0' AND '9')),
    CONSTRAINT ck_administrative_legacy_commune_map_legacy_district_code CHECK (length(legacy_district_code) = 3 AND substr(legacy_district_code, 1, 1) BETWEEN '0' AND '9' AND substr(legacy_district_code, 2, 1) BETWEEN '0' AND '9' AND substr(legacy_district_code, 3, 1) BETWEEN '0' AND '9'),
    CONSTRAINT ck_administrative_legacy_commune_map_legacy_province_code CHECK (length(legacy_province_code) = 2 AND substr(legacy_province_code, 1, 1) BETWEEN '0' AND '9' AND substr(legacy_province_code, 2, 1) BETWEEN '0' AND '9'),
    CONSTRAINT ck_administrative_legacy_commune_map_match_key CHECK (match_key = legacy_district_code || ':' || COALESCE(legacy_commune_code, '-')),
    CONSTRAINT ck_administrative_legacy_commune_map_merge_type CHECK (merge_type IN ('WHOLE', 'PARTIAL', 'UNCHANGED')),
    CONSTRAINT fk_administrative_legacy_commune_map_commune FOREIGN KEY (commune_code) REFERENCES administrative_commune (code) ON DELETE RESTRICT
);

CREATE TABLE operational_zone_commune (
    id uuid NOT NULL,
    zone_id uuid NOT NULL,
    commune_code character varying(5) NOT NULL,
    assigned_by_user_id uuid NOT NULL,
    assigned_at timestamp with time zone NOT NULL,
    removed_at timestamp with time zone,
    removed_by_user_id uuid,
    removal_reason character varying(500),
    CONSTRAINT "PK_operational_zone_commune" PRIMARY KEY (id),
    CONSTRAINT ck_operational_zone_commune_removed CHECK ((removed_at IS NULL) = (removed_by_user_id IS NULL)),
    CONSTRAINT fk_operational_zone_commune_assigned_by FOREIGN KEY (assigned_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_operational_zone_commune_commune FOREIGN KEY (commune_code) REFERENCES administrative_commune (code) ON DELETE RESTRICT,
    CONSTRAINT fk_operational_zone_commune_removed_by FOREIGN KEY (removed_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_operational_zone_commune_zone FOREIGN KEY (zone_id) REFERENCES operational_zone (id) ON DELETE RESTRICT
);

CREATE TABLE cross_zone_authorization (
    id uuid NOT NULL,
    dispatch_request_id uuid,
    installation_project_id uuid,
    technician_user_id uuid NOT NULL,
    target_zone_id uuid,
    reason character varying(500) NOT NULL,
    authorized_by_user_id uuid NOT NULL,
    authorized_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_cross_zone_authorization" PRIMARY KEY (id),
    CONSTRAINT ck_cross_zone_authorization_reason CHECK (length(reason) > 0),
    CONSTRAINT ck_cross_zone_authorization_subject CHECK ((dispatch_request_id IS NULL) <> (installation_project_id IS NULL)),
    CONSTRAINT "FK_cross_zone_authorization_dispatch_request_dispatch_request_~" FOREIGN KEY (dispatch_request_id) REFERENCES dispatch_request (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_cross_zone_authorization_user_account_authorized_by_user_id" FOREIGN KEY (authorized_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_cross_zone_authorization_user_account_technician_user_id" FOREIGN KEY (technician_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_cross_zone_authorization_installation_project FOREIGN KEY (installation_project_id) REFERENCES installation_project (id) ON DELETE RESTRICT,
    CONSTRAINT fk_cross_zone_authorization_target_zone FOREIGN KEY (target_zone_id) REFERENCES operational_zone (id) ON DELETE RESTRICT
);

CREATE TABLE dispatch_preferred_window (
    id uuid NOT NULL,
    dispatch_request_id uuid NOT NULL,
    starts_at timestamp with time zone NOT NULL,
    ends_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_dispatch_preferred_window" PRIMARY KEY (id),
    CONSTRAINT ck_dispatch_preferred_window_range CHECK (ends_at > starts_at),
    CONSTRAINT fk_dispatch_preferred_window_dispatch_request FOREIGN KEY (dispatch_request_id) REFERENCES dispatch_request (id) ON DELETE RESTRICT
);

CREATE TABLE dispatch_proposal (
    id uuid NOT NULL,
    dispatch_request_id uuid NOT NULL,
    technician_user_id uuid NOT NULL,
    starts_at timestamp with time zone NOT NULL,
    ends_at timestamp with time zone NOT NULL,
    cross_zone boolean NOT NULL,
    cross_zone_authorization_id uuid,
    proposed_by_user_id uuid NOT NULL,
    note character varying(500),
    created_at timestamp with time zone NOT NULL,
    response_window_minutes integer NOT NULL,
    settings_version integer NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    status character varying(20) NOT NULL,
    responded_at timestamp with time zone,
    failure_reason character varying(500),
    revision integer NOT NULL,
    CONSTRAINT "PK_dispatch_proposal" PRIMARY KEY (id),
    CONSTRAINT ck_dispatch_proposal_cross_zone CHECK (cross_zone = (cross_zone_authorization_id IS NOT NULL)),
    CONSTRAINT ck_dispatch_proposal_expiry CHECK (expires_at <= starts_at AND expires_at > created_at AND response_window_minutes > 0),
    CONSTRAINT ck_dispatch_proposal_range CHECK (ends_at > starts_at),
    CONSTRAINT ck_dispatch_proposal_responded CHECK (status IN ('PROPOSED', 'EXPIRED') OR responded_at IS NOT NULL),
    CONSTRAINT ck_dispatch_proposal_status CHECK (status IN ('PROPOSED', 'ACCEPTED', 'DECLINED', 'EXPIRED', 'WITHDRAWN', 'FAILED')),
    CONSTRAINT "FK_dispatch_proposal_dispatch_request_dispatch_request_id" FOREIGN KEY (dispatch_request_id) REFERENCES dispatch_request (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_dispatch_proposal_user_account_proposed_by_user_id" FOREIGN KEY (proposed_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_dispatch_proposal_user_account_technician_user_id" FOREIGN KEY (technician_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_dispatch_proposal_cross_zone_authorization FOREIGN KEY (cross_zone_authorization_id) REFERENCES cross_zone_authorization (id) ON DELETE RESTRICT
);

CREATE TABLE dispatch_event (
    id uuid NOT NULL,
    dispatch_request_id uuid NOT NULL,
    proposal_id uuid,
    event_type character varying(40) NOT NULL,
    actor_user_id uuid,
    detail character varying(1000),
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_dispatch_event" PRIMARY KEY (id),
    CONSTRAINT "FK_dispatch_event_dispatch_request_dispatch_request_id" FOREIGN KEY (dispatch_request_id) REFERENCES dispatch_request (id) ON DELETE RESTRICT,
    CONSTRAINT fk_dispatch_event_proposal FOREIGN KEY (proposal_id) REFERENCES dispatch_proposal (id) ON DELETE RESTRICT
);

CREATE TABLE dispatch_notification (
    id uuid NOT NULL,
    recipient_user_id uuid NOT NULL,
    dispatch_request_id uuid NOT NULL,
    event_id uuid NOT NULL,
    type character varying(60) NOT NULL,
    delivery_status character varying(30) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_dispatch_notification" PRIMARY KEY (id),
    CONSTRAINT "FK_dispatch_notification_dispatch_event_event_id" FOREIGN KEY (event_id) REFERENCES dispatch_event (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_dispatch_notification_dispatch_request_dispatch_request_id" FOREIGN KEY (dispatch_request_id) REFERENCES dispatch_request (id) ON DELETE RESTRICT,
    CONSTRAINT "FK_dispatch_notification_user_account_recipient_user_id" FOREIGN KEY (recipient_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE INDEX "IX_technician_appointment_cross_zone_authorization_id" ON technician_appointment (cross_zone_authorization_id);

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_assignment_source CHECK (assignment_source IS NULL OR assignment_source IN ('SELF_ACCEPTED', 'SALES_ASSIGNED', 'MANAGER_OVERRIDE', 'MANAGER_DISPATCH'));

CREATE INDEX ix_property_site_province_commune ON property_site (province_code, commune_code);

ALTER TABLE property_site ADD CONSTRAINT ck_property_site_commune_code CHECK (commune_code IS NULL OR (province_code IS NOT NULL AND length(commune_code) = 5));

CREATE UNIQUE INDEX ux_administrative_catalog_import_dataset_key ON administrative_catalog_import (dataset_key);

CREATE INDEX ix_administrative_commune_province_name ON administrative_commune (province_code, name);

CREATE INDEX ix_administrative_commune_province_search_name ON administrative_commune (province_code, search_name);

CREATE INDEX ix_administrative_legacy_commune_map_commune_search_name ON administrative_legacy_commune_map (legacy_commune_search_name);

CREATE INDEX ix_administrative_legacy_commune_map_district_search_name ON administrative_legacy_commune_map (legacy_district_search_name);

CREATE INDEX ix_administrative_legacy_commune_map_legacy_commune_code ON administrative_legacy_commune_map (legacy_commune_code);

CREATE INDEX ix_administrative_legacy_commune_map_legacy_district_code ON administrative_legacy_commune_map (legacy_district_code);

CREATE UNIQUE INDEX ux_administrative_legacy_commune_map_commune_match_key ON administrative_legacy_commune_map (commune_code, match_key);

CREATE INDEX "IX_cross_zone_authorization_authorized_by_user_id" ON cross_zone_authorization (authorized_by_user_id);

CREATE INDEX ix_cross_zone_authorization_dispatch_request_id ON cross_zone_authorization (dispatch_request_id);

CREATE INDEX ix_cross_zone_authorization_installation_project_id ON cross_zone_authorization (installation_project_id);

CREATE INDEX "IX_cross_zone_authorization_target_zone_id" ON cross_zone_authorization (target_zone_id);

CREATE INDEX ix_cross_zone_authorization_technician_authorized_at ON cross_zone_authorization (technician_user_id, authorized_at);

CREATE INDEX "IX_dispatch_event_proposal_id" ON dispatch_event (proposal_id);

CREATE INDEX ix_dispatch_event_request_created_at ON dispatch_event (dispatch_request_id, created_at);

CREATE INDEX "IX_dispatch_notification_dispatch_request_id" ON dispatch_notification (dispatch_request_id);

CREATE INDEX ix_dispatch_notification_event_id ON dispatch_notification (event_id);

CREATE INDEX ix_dispatch_notification_recipient_created_at ON dispatch_notification (recipient_user_id, created_at);

CREATE INDEX ix_dispatch_preferred_window_request_starts_at ON dispatch_preferred_window (dispatch_request_id, starts_at);

CREATE INDEX "IX_dispatch_proposal_cross_zone_authorization_id" ON dispatch_proposal (cross_zone_authorization_id);

CREATE INDEX "IX_dispatch_proposal_proposed_by_user_id" ON dispatch_proposal (proposed_by_user_id);

CREATE INDEX ix_dispatch_proposal_request_created_at ON dispatch_proposal (dispatch_request_id, created_at);

CREATE INDEX ix_dispatch_proposal_status_expires_at ON dispatch_proposal (status, expires_at);

CREATE INDEX "IX_dispatch_proposal_technician_user_id" ON dispatch_proposal (technician_user_id);

CREATE UNIQUE INDEX ux_dispatch_proposal_accepted ON dispatch_proposal (dispatch_request_id) WHERE status = 'ACCEPTED';

CREATE INDEX "IX_dispatch_request_appointment_id" ON dispatch_request (appointment_id);

CREATE INDEX "IX_dispatch_request_customer_user_id" ON dispatch_request (customer_user_id);

CREATE INDEX ix_dispatch_request_operational_zone_id ON dispatch_request (operational_zone_id);

CREATE INDEX "IX_dispatch_request_property_site_id" ON dispatch_request (property_site_id);

CREATE INDEX ix_dispatch_request_released_appointment_id ON dispatch_request (released_appointment_id);

CREATE INDEX "IX_dispatch_request_resolved_by_user_id" ON dispatch_request (resolved_by_user_id);

CREATE INDEX ix_dispatch_request_status_escalation_due_at ON dispatch_request (status, escalation_due_at);

CREATE INDEX ix_dispatch_request_status_review_due_at ON dispatch_request (status, review_due_at);

CREATE INDEX ix_dispatch_request_survey_request_created_at ON dispatch_request (survey_request_id, created_at);

CREATE UNIQUE INDEX ux_dispatch_request_active_survey_request ON dispatch_request (survey_request_id) WHERE status IN ('OPEN', 'PROPOSAL_PENDING');

CREATE INDEX "IX_operational_zone_created_by_user_id" ON operational_zone (created_by_user_id);

CREATE INDEX "IX_operational_zone_responsible_manager_user_id" ON operational_zone (responsible_manager_user_id);

CREATE INDEX "IX_operational_zone_updated_by_user_id" ON operational_zone (updated_by_user_id);

CREATE UNIQUE INDEX ux_operational_zone_code ON operational_zone (code);

CREATE INDEX "IX_operational_zone_commune_assigned_by_user_id" ON operational_zone_commune (assigned_by_user_id);

CREATE INDEX "IX_operational_zone_commune_removed_by_user_id" ON operational_zone_commune (removed_by_user_id);

CREATE INDEX ix_operational_zone_commune_zone_removed_at ON operational_zone_commune (zone_id, removed_at);

CREATE UNIQUE INDEX ux_operational_zone_commune_active ON operational_zone_commune (commune_code) WHERE removed_at IS NULL;

CREATE INDEX ix_system_setting_updated_by_user_id ON system_setting (updated_by_user_id);

CREATE INDEX ix_system_setting_revision_changed_by ON system_setting_revision (changed_by);

CREATE INDEX ix_system_setting_revision_version ON system_setting_revision (version);

CREATE UNIQUE INDEX ux_system_setting_revision_key_version ON system_setting_revision (key, version);

CREATE INDEX ix_system_settings_version_updated_by_user_id ON system_settings_version (updated_by_user_id);

CREATE INDEX "IX_technician_zone_membership_assigned_by_user_id" ON technician_zone_membership (assigned_by_user_id);

CREATE INDEX "IX_technician_zone_membership_removed_by_user_id" ON technician_zone_membership (removed_by_user_id);

CREATE INDEX ix_technician_zone_membership_zone_id ON technician_zone_membership (zone_id);

CREATE UNIQUE INDEX ux_technician_zone_membership_active ON technician_zone_membership (technician_user_id, zone_id) WHERE removed_at IS NULL;

ALTER TABLE installation_project ADD CONSTRAINT fk_installation_project_operational_zone FOREIGN KEY (operational_zone_id) REFERENCES operational_zone (id) ON DELETE RESTRICT;

ALTER TABLE property_site ADD CONSTRAINT fk_property_site_administrative_commune FOREIGN KEY (province_code, commune_code) REFERENCES administrative_commune (province_code, code) ON DELETE RESTRICT;

ALTER TABLE technician_appointment ADD CONSTRAINT fk_technician_appointment_cross_zone_authorization FOREIGN KEY (cross_zone_authorization_id) REFERENCES cross_zone_authorization (id) ON DELETE RESTRICT;

ALTER TABLE technician_appointment ADD CONSTRAINT fk_technician_appointment_operational_zone FOREIGN KEY (operational_zone_id) REFERENCES operational_zone (id) ON DELETE RESTRICT;

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

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261020051352_AdministrativeLocationsZonesDispatchAndSettings', '8.0.32');

COMMIT;

