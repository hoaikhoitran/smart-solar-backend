START TRANSACTION;

ALTER TABLE pre_survey DROP CONSTRAINT fk_pre_survey_selected_simulation_same_pre_survey;

ALTER TABLE technician_profile DROP CONSTRAINT ck_technician_profile_base_paired;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_acknowledged;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_assignment;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_status;

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_status;

ALTER TABLE technician_appointment ALTER COLUMN reserved_technician_user_id DROP NOT NULL;

ALTER TABLE technician_appointment ADD acceptance_deadline_at timestamp with time zone;

ALTER TABLE technician_appointment ADD accepted_at timestamp with time zone;

ALTER TABLE technician_appointment ADD assignment_reason character varying(500);

ALTER TABLE technician_appointment ADD assignment_source character varying(30);

ALTER TABLE technician_appointment ADD completed_at timestamp with time zone;

ALTER TABLE technician_appointment ADD escalated_at timestamp with time zone;

ALTER TABLE technician_appointment ADD service_area_id uuid;

ALTER TABLE property_site ADD district_code character varying(50);

ALTER TABLE property_site ADD province_code character varying(50);

ALTER TABLE property_site ADD ward_code character varying(50);

ALTER TABLE knowledge_ingestion_job ADD chunk_characters integer;

ALTER TABLE knowledge_ingestion_job ADD chunk_overlap integer;

ALTER TABLE knowledge_ingestion_job ADD config_version integer;

ALTER TABLE knowledge_document_version ADD indexed_chunk_characters integer;

ALTER TABLE knowledge_document_version ADD indexed_chunk_overlap integer;

ALTER TABLE knowledge_document_version ADD indexed_config_version integer;

ALTER TABLE knowledge_document_version ADD last_ingest_error character varying(300);

ALTER TABLE knowledge_document_version ADD last_ingest_status character varying(30);

ALTER TABLE installation_project ADD cancellation_reason character varying(500);

ALTER TABLE installation_project ADD cancelled_at timestamp with time zone;

ALTER TABLE installation_project ADD responsible_technician_user_id uuid;

ALTER TABLE installation_project ADD scheduled_ends_at timestamp with time zone;

ALTER TABLE installation_project ADD scheduled_starts_at timestamp with time zone;

ALTER TABLE installation_project ADD service_area_id uuid;

ALTER TABLE installation_project ADD technician_accepted_at timestamp with time zone;

ALTER TABLE installation_event ADD from_status character varying(40);

ALTER TABLE installation_event ADD to_status character varying(40);

CREATE TABLE installation_assignment (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    technician_user_id uuid NOT NULL,
    starts_at timestamp with time zone NOT NULL,
    ends_at timestamp with time zone NOT NULL,
    status character varying(20) NOT NULL,
    assigned_by_user_id uuid NOT NULL,
    assigned_at timestamp with time zone NOT NULL,
    assignment_reason character varying(500),
    responded_at timestamp with time zone,
    response_reason character varying(500),
    closed_at timestamp with time zone,
    CONSTRAINT "PK_installation_assignment" PRIMARY KEY (id),
    CONSTRAINT ck_installation_assignment_closed CHECK ((status IN ('PENDING', 'ACCEPTED')) = (closed_at IS NULL)),
    CONSTRAINT ck_installation_assignment_declined CHECK (status <> 'DECLINED' OR response_reason IS NOT NULL),
    CONSTRAINT ck_installation_assignment_range CHECK (ends_at > starts_at),
    CONSTRAINT ck_installation_assignment_responded CHECK (status NOT IN ('ACCEPTED', 'DECLINED') OR responded_at IS NOT NULL),
    CONSTRAINT ck_installation_assignment_status CHECK (status IN ('PENDING', 'ACCEPTED', 'DECLINED', 'REPLACED', 'COMPLETED', 'CANCELLED')),
    CONSTRAINT fk_installation_assignment_assigned_by FOREIGN KEY (assigned_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_installation_assignment_project FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT,
    CONSTRAINT fk_installation_assignment_technician_profile FOREIGN KEY (technician_user_id) REFERENCES technician_profile (user_id) ON DELETE RESTRICT
);

CREATE TABLE installation_evidence (
    id uuid NOT NULL,
    project_id uuid NOT NULL,
    round integer NOT NULL,
    file_id uuid NOT NULL,
    caption character varying(300),
    added_by_user_id uuid NOT NULL,
    added_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_installation_evidence" PRIMARY KEY (id),
    CONSTRAINT ck_installation_evidence_round CHECK (round >= 1),
    CONSTRAINT fk_installation_evidence_added_by FOREIGN KEY (added_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_installation_evidence_file_reference FOREIGN KEY (file_id) REFERENCES file_reference (id) ON DELETE RESTRICT,
    CONSTRAINT fk_installation_evidence_project FOREIGN KEY (project_id) REFERENCES installation_project (id) ON DELETE RESTRICT
);

CREATE TABLE product_image (
    id uuid NOT NULL,
    product_id uuid NOT NULL,
    url character varying(1000) NOT NULL,
    alt_text character varying(255),
    sort_order integer NOT NULL,
    is_primary boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    created_by_user_id uuid,
    CONSTRAINT "PK_product_image" PRIMARY KEY (id),
    CONSTRAINT ck_product_image_sort_order_non_negative CHECK (sort_order >= 0),
    CONSTRAINT ck_product_image_url_https CHECK (url LIKE 'https://%'),
    CONSTRAINT fk_product_image_product FOREIGN KEY (product_id) REFERENCES product (id) ON DELETE RESTRICT,
    CONSTRAINT fk_product_image_user_account_created_by FOREIGN KEY (created_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE rag_configuration (
    id uuid NOT NULL,
    version integer NOT NULL,
    status character varying(20) NOT NULL,
    generation_model character varying(100) NOT NULL,
    temperature numeric(3,2),
    max_output_tokens integer NOT NULL,
    chunk_characters integer NOT NULL,
    chunk_overlap integer NOT NULL,
    top_k integer NOT NULL,
    min_score numeric(4,3) NOT NULL,
    max_context_tokens integer NOT NULL,
    max_history_messages integer NOT NULL,
    prompt_version character varying(20) NOT NULL,
    notes character varying(500),
    based_on_version integer,
    created_by_user_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    activated_by_user_id uuid,
    activated_at timestamp with time zone,
    revision integer NOT NULL,
    CONSTRAINT "PK_rag_configuration" PRIMARY KEY (id),
    CONSTRAINT ck_rag_configuration_activated CHECK ((status = 'DRAFT') = (activated_at IS NULL) AND (activated_at IS NULL) = (activated_by_user_id IS NULL)),
    CONSTRAINT ck_rag_configuration_chunking CHECK (chunk_characters BETWEEN 400 AND 8000 AND chunk_overlap >= 0 AND chunk_overlap * 2 <= chunk_characters AND chunk_overlap < chunk_characters),
    CONSTRAINT ck_rag_configuration_max_context_tokens CHECK (max_context_tokens BETWEEN 500 AND 6000),
    CONSTRAINT ck_rag_configuration_max_history_messages CHECK (max_history_messages BETWEEN 0 AND 20),
    CONSTRAINT ck_rag_configuration_max_output_tokens CHECK (max_output_tokens BETWEEN 64 AND 1500),
    CONSTRAINT ck_rag_configuration_min_score CHECK (min_score >= 0 AND min_score <= 1),
    CONSTRAINT ck_rag_configuration_model CHECK (length(generation_model) > 0 AND length(prompt_version) > 0),
    CONSTRAINT ck_rag_configuration_status CHECK (status IN ('DRAFT', 'ACTIVE', 'SUPERSEDED')),
    CONSTRAINT ck_rag_configuration_temperature CHECK (temperature IS NULL OR (temperature >= 0 AND temperature <= 1)),
    CONSTRAINT ck_rag_configuration_top_k CHECK (top_k BETWEEN 1 AND 10),
    CONSTRAINT ck_rag_configuration_version CHECK (version >= 1 AND (based_on_version IS NULL OR based_on_version >= 1)),
    CONSTRAINT fk_rag_configuration_user_account_activated_by_user_id FOREIGN KEY (activated_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_rag_configuration_user_account_created_by_user_id FOREIGN KEY (created_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE selected_simulation (
    id uuid NOT NULL,
    pre_survey_id uuid NOT NULL,
    simulation_id uuid NOT NULL,
    selected_by_user_id uuid NOT NULL,
    selected_at timestamp with time zone NOT NULL,
    pre_survey_geometry_version integer NOT NULL,
    algorithm_version character varying(30) NOT NULL,
    status character varying(30) NOT NULL,
    energy_status character varying(30) NOT NULL,
    mounting_type character varying(30) NOT NULL,
    product_id uuid NOT NULL,
    product_sku character varying(50) NOT NULL,
    product_name character varying(255) NOT NULL,
    panel_count integer NOT NULL,
    installed_capacity_kwp numeric NOT NULL,
    annual_energy_kwh numeric,
    specific_yield_kwh_per_kwp_year numeric,
    panel_tilt_degree numeric NOT NULL,
    panel_azimuth_degree numeric NOT NULL,
    snapshot_bucket character varying(63),
    snapshot_object_key character varying(1024),
    snapshot_sha256 char(64),
    snapshot_size_bytes bigint,
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_selected_simulation" PRIMARY KEY (id),
    CONSTRAINT ak_selected_simulation_pre_survey_id_simulation_id UNIQUE (pre_survey_id, simulation_id),
    CONSTRAINT ck_selected_simulation_capacity_non_negative CHECK (installed_capacity_kwp >= 0),
    CONSTRAINT ck_selected_simulation_energy_status CHECK (energy_status IN ('SUCCEEDED', 'NOT_APPLICABLE', 'UNAVAILABLE', 'FAILED')),
    CONSTRAINT ck_selected_simulation_geometry_version_non_negative CHECK (pre_survey_geometry_version >= 0),
    CONSTRAINT ck_selected_simulation_mounting_type CHECK (mounting_type IN ('FLUSH', 'RACK')),
    CONSTRAINT ck_selected_simulation_panel_count_non_negative CHECK (panel_count >= 0),
    CONSTRAINT ck_selected_simulation_snapshot_complete CHECK ((snapshot_bucket IS NULL AND snapshot_object_key IS NULL AND snapshot_sha256 IS NULL AND snapshot_size_bytes IS NULL) OR (snapshot_bucket IS NOT NULL AND snapshot_object_key IS NOT NULL AND snapshot_sha256 IS NOT NULL AND snapshot_size_bytes IS NOT NULL AND snapshot_size_bytes > 0)),
    CONSTRAINT ck_selected_simulation_status CHECK (status IN ('COMPLETED', 'PARTIALLY_COMPLETED')),
    CONSTRAINT fk_selected_simulation_pre_survey FOREIGN KEY (pre_survey_id) REFERENCES pre_survey (id) ON DELETE RESTRICT,
    CONSTRAINT fk_selected_simulation_product FOREIGN KEY (product_id) REFERENCES product (id) ON DELETE RESTRICT,
    CONSTRAINT fk_selected_simulation_user_account FOREIGN KEY (selected_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE service_area (
    id uuid NOT NULL,
    code character varying(50) NOT NULL,
    level character varying(20) NOT NULL,
    parent_id uuid,
    name character varying(150) NOT NULL,
    is_active boolean NOT NULL,
    responsible_manager_user_id uuid,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    revision integer NOT NULL,
    CONSTRAINT "PK_service_area" PRIMARY KEY (id),
    CONSTRAINT ck_service_area_code_depth CHECK ((level = 'PROVINCE' AND code NOT LIKE '%.%') OR (level = 'DISTRICT' AND code LIKE '%.%' AND code NOT LIKE '%.%.%') OR (level = 'WARD' AND code LIKE '%.%.%' AND code NOT LIKE '%.%.%.%')),
    CONSTRAINT ck_service_area_level CHECK (level IN ('PROVINCE', 'DISTRICT', 'WARD')),
    CONSTRAINT ck_service_area_parent CHECK ((level = 'PROVINCE') = (parent_id IS NULL)),
    CONSTRAINT fk_service_area_parent FOREIGN KEY (parent_id) REFERENCES service_area (id) ON DELETE RESTRICT,
    CONSTRAINT fk_service_area_responsible_manager FOREIGN KEY (responsible_manager_user_id) REFERENCES user_account (id) ON DELETE RESTRICT
);

CREATE TABLE survey_request_event (
    id uuid NOT NULL,
    survey_request_id uuid NOT NULL,
    event_type character varying(40) NOT NULL,
    actor_user_id uuid,
    previous_sale_user_id uuid,
    new_sale_user_id uuid,
    reason character varying(500),
    created_at timestamp with time zone NOT NULL,
    CONSTRAINT "PK_survey_request_event" PRIMARY KEY (id),
    CONSTRAINT ck_survey_request_event_type CHECK (event_type IN ('SALES_OWNER_CHANGED')),
    CONSTRAINT fk_survey_request_event_actor FOREIGN KEY (actor_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_survey_request_event_new_sale FOREIGN KEY (new_sale_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_survey_request_event_previous_sale FOREIGN KEY (previous_sale_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_survey_request_event_survey_request FOREIGN KEY (survey_request_id) REFERENCES survey_request (id) ON DELETE RESTRICT
);

CREATE TABLE technician_service_area (
    id uuid NOT NULL,
    technician_user_id uuid NOT NULL,
    service_area_id uuid NOT NULL,
    assigned_by_user_id uuid,
    assigned_at timestamp with time zone NOT NULL,
    removed_at timestamp with time zone,
    removed_by_user_id uuid,
    CONSTRAINT "PK_technician_service_area" PRIMARY KEY (id),
    CONSTRAINT ck_technician_service_area_removed CHECK (removed_at IS NULL OR removed_by_user_id IS NOT NULL),
    CONSTRAINT fk_technician_service_area_assigned_by FOREIGN KEY (assigned_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_technician_service_area_removed_by FOREIGN KEY (removed_by_user_id) REFERENCES user_account (id) ON DELETE RESTRICT,
    CONSTRAINT fk_technician_service_area_service_area FOREIGN KEY (service_area_id) REFERENCES service_area (id) ON DELETE RESTRICT,
    CONSTRAINT fk_technician_service_area_technician_profile FOREIGN KEY (technician_user_id) REFERENCES technician_profile (user_id) ON DELETE RESTRICT
);

CREATE INDEX ix_technician_appointment_service_area_id ON technician_appointment (service_area_id);

CREATE INDEX ix_technician_appointment_status_deadline ON technician_appointment (status, acceptance_deadline_at);

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

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_assignment CHECK (status NOT IN ('ACCEPTED', 'ASSIGNED', 'COMPLETED') OR (assigned_technician_user_id IS NOT NULL AND assigned_technician_user_id = reserved_technician_user_id AND assigned_at IS NOT NULL AND assignment_source IS NOT NULL));

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_assignment_reason CHECK (status <> 'ASSIGNED' OR assignment_reason IS NOT NULL);

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_assignment_source CHECK (assignment_source IS NULL OR assignment_source IN ('SELF_ACCEPTED', 'SALES_ASSIGNED', 'MANAGER_OVERRIDE'));

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_completed_at CHECK (status <> 'COMPLETED' OR completed_at IS NOT NULL);

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_escalated_unassigned CHECK (status <> 'ESCALATED' OR assigned_technician_user_id IS NULL);

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_pending_holder CHECK (status <> 'PENDING_ACCEPTANCE' OR (reserved_technician_user_id IS NOT NULL AND assigned_technician_user_id IS NULL));

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_status CHECK (status IN ('PENDING_ACCEPTANCE', 'ESCALATED', 'ACCEPTED', 'ASSIGNED', 'COMPLETED', 'CANCELLED'));

ALTER TABLE survey_request ADD CONSTRAINT ck_survey_request_status CHECK (status IN ('Pending', 'Scheduled', 'Completed', 'Cancelled'));

ALTER TABLE property_site ADD CONSTRAINT ck_property_site_district_code CHECK (district_code IS NULL OR (province_code IS NOT NULL AND district_code LIKE (province_code || '.%') AND district_code NOT LIKE '%.%.%'));

ALTER TABLE property_site ADD CONSTRAINT ck_property_site_province_code CHECK (province_code IS NULL OR (province_code NOT LIKE '%.%' AND length(province_code) BETWEEN 1 AND 3));

ALTER TABLE property_site ADD CONSTRAINT ck_property_site_ward_code CHECK (ward_code IS NULL OR (district_code IS NOT NULL AND ward_code LIKE (district_code || '.%') AND ward_code NOT LIKE '%.%.%.%'));

ALTER TABLE knowledge_ingestion_job ADD CONSTRAINT ck_knowledge_ingestion_job_chunking CHECK ((chunk_characters IS NULL) = (chunk_overlap IS NULL) AND (chunk_characters IS NULL OR (chunk_characters > 0 AND chunk_overlap >= 0 AND chunk_overlap < chunk_characters)));

ALTER TABLE knowledge_ingestion_job ADD CONSTRAINT ck_knowledge_ingestion_job_config_version CHECK (config_version IS NULL OR config_version >= 0);

ALTER TABLE knowledge_document_version ADD CONSTRAINT ck_knowledge_document_version_indexed_chunking CHECK ((indexed_chunk_characters IS NULL) = (indexed_chunk_overlap IS NULL) AND (indexed_chunk_characters IS NULL OR (indexed_chunk_characters > 0 AND indexed_chunk_overlap >= 0 AND indexed_chunk_overlap < indexed_chunk_characters)));

ALTER TABLE knowledge_document_version ADD CONSTRAINT ck_knowledge_document_version_indexed_config CHECK (indexed_config_version IS NULL OR indexed_config_version >= 0);

CREATE INDEX ix_installation_project_responsible_technician ON installation_project (responsible_technician_user_id);

CREATE INDEX "IX_installation_project_service_area_id" ON installation_project (service_area_id);

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_accepted CHECK (NOT status IN ('SCHEDULED', 'PENDING_CUSTOMER_ACCEPTANCE') OR technician_accepted_at IS NOT NULL);

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_assigned CHECK (NOT status IN ('PENDING_TECHNICIAN_ACCEPTANCE', 'SCHEDULED', 'PENDING_CUSTOMER_ACCEPTANCE') OR (responsible_technician_user_id IS NOT NULL AND scheduled_starts_at IS NOT NULL));

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_cancelled CHECK ((status = 'CANCELLED') = (cancelled_at IS NOT NULL) AND (status <> 'CANCELLED' OR cancellation_reason IS NOT NULL));

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_round CHECK (acceptance_round >= 0);

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_schedule CHECK ((scheduled_starts_at IS NULL) = (scheduled_ends_at IS NULL) AND (scheduled_ends_at IS NULL OR scheduled_ends_at > scheduled_starts_at));

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_started CHECK (NOT status IN ('IN_PROGRESS', 'PENDING_CUSTOMER_ACCEPTANCE', 'REWORK_REQUIRED', 'COMPLETED') OR started_at IS NOT NULL);

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_status CHECK (status IN ('PENDING_ASSIGNMENT', 'PENDING_TECHNICIAN_ACCEPTANCE', 'SCHEDULED', 'IN_PROGRESS', 'PENDING_CUSTOMER_ACCEPTANCE', 'REWORK_REQUIRED', 'COMPLETED', 'CANCELLED'));

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_unassigned CHECK (status <> 'PENDING_ASSIGNMENT' OR (responsible_technician_user_id IS NULL AND technician_accepted_at IS NULL));

CREATE INDEX "IX_installation_assignment_assigned_by_user_id" ON installation_assignment (assigned_by_user_id);

CREATE INDEX ix_installation_assignment_project_assigned_at ON installation_assignment (project_id, assigned_at);

CREATE INDEX ix_installation_assignment_technician_starts_at ON installation_assignment (technician_user_id, starts_at);

CREATE UNIQUE INDEX ux_installation_assignment_current ON installation_assignment (project_id) WHERE status IN ('PENDING', 'ACCEPTED');

CREATE INDEX "IX_installation_evidence_added_by_user_id" ON installation_evidence (added_by_user_id);

CREATE INDEX ix_installation_evidence_project_round ON installation_evidence (project_id, round);

CREATE UNIQUE INDEX ux_installation_evidence_file ON installation_evidence (file_id);

CREATE INDEX ix_product_image_created_by_user_id ON product_image (created_by_user_id);

CREATE UNIQUE INDEX ux_product_image_one_primary ON product_image (product_id) WHERE "is_primary";

CREATE UNIQUE INDEX ux_product_image_product_sort ON product_image (product_id, sort_order);

CREATE UNIQUE INDEX ux_product_image_product_url ON product_image (product_id, url);

CREATE INDEX ix_rag_configuration_activated_by_user_id ON rag_configuration (activated_by_user_id);

CREATE INDEX ix_rag_configuration_created_by_user_id ON rag_configuration (created_by_user_id);

CREATE UNIQUE INDEX ux_rag_configuration_one_active ON rag_configuration (status) WHERE status = 'ACTIVE';

CREATE UNIQUE INDEX ux_rag_configuration_one_draft ON rag_configuration (status) WHERE status = 'DRAFT';

CREATE UNIQUE INDEX ux_rag_configuration_version ON rag_configuration (version);

CREATE INDEX ix_selected_simulation_product ON selected_simulation (product_id);

CREATE INDEX ix_selected_simulation_selected_by ON selected_simulation (selected_by_user_id);

CREATE UNIQUE INDEX ux_selected_simulation_pre_survey ON selected_simulation (pre_survey_id);

CREATE UNIQUE INDEX ux_selected_simulation_simulation ON selected_simulation (simulation_id);

CREATE INDEX ix_service_area_parent_id ON service_area (parent_id);

CREATE INDEX "IX_service_area_responsible_manager_user_id" ON service_area (responsible_manager_user_id);

CREATE UNIQUE INDEX ux_service_area_code ON service_area (code);

CREATE INDEX "IX_survey_request_event_actor_user_id" ON survey_request_event (actor_user_id);

CREATE INDEX "IX_survey_request_event_new_sale_user_id" ON survey_request_event (new_sale_user_id);

CREATE INDEX "IX_survey_request_event_previous_sale_user_id" ON survey_request_event (previous_sale_user_id);

CREATE INDEX ix_survey_request_event_survey_request_created_at ON survey_request_event (survey_request_id, created_at);

CREATE INDEX "IX_technician_service_area_assigned_by_user_id" ON technician_service_area (assigned_by_user_id);

CREATE INDEX "IX_technician_service_area_removed_by_user_id" ON technician_service_area (removed_by_user_id);

CREATE INDEX ix_technician_service_area_service_area_id ON technician_service_area (service_area_id);

CREATE UNIQUE INDEX ux_technician_service_area_active ON technician_service_area (technician_user_id, service_area_id) WHERE removed_at IS NULL;

ALTER TABLE installation_project ADD CONSTRAINT fk_installation_project_responsible_technician FOREIGN KEY (responsible_technician_user_id) REFERENCES user_account (id) ON DELETE RESTRICT;

ALTER TABLE installation_project ADD CONSTRAINT fk_installation_project_service_area FOREIGN KEY (service_area_id) REFERENCES service_area (id) ON DELETE RESTRICT;

ALTER TABLE pre_survey ADD CONSTRAINT fk_pre_survey_selected_simulation FOREIGN KEY (id, selected_simulation_id) REFERENCES selected_simulation (pre_survey_id, simulation_id) ON DELETE RESTRICT;

ALTER TABLE technician_appointment ADD CONSTRAINT fk_technician_appointment_service_area FOREIGN KEY (service_area_id) REFERENCES service_area (id) ON DELETE RESTRICT;

CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE installation_assignment
    ADD CONSTRAINT ex_installation_assignment_no_overlap
    EXCLUDE USING gist (technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
    WHERE (status IN ('PENDING', 'ACCEPTED'));

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261016161547_BusinessRulesFlow1And2Redesign', '8.0.31');

COMMIT;

