START TRANSACTION;

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

ALTER TABLE installation_project DROP CONSTRAINT fk_installation_project_responsible_technician;

ALTER TABLE installation_project DROP CONSTRAINT fk_installation_project_service_area;

ALTER TABLE pre_survey DROP CONSTRAINT fk_pre_survey_selected_simulation;

ALTER TABLE technician_appointment DROP CONSTRAINT fk_technician_appointment_service_area;

DROP TABLE installation_assignment;

DROP TABLE installation_evidence;

DROP TABLE product_image;

DROP TABLE rag_configuration;

DROP TABLE selected_simulation;

DROP TABLE survey_request_event;

DROP TABLE technician_service_area;

DROP TABLE service_area;

DROP INDEX ix_technician_appointment_service_area_id;

DROP INDEX ix_technician_appointment_status_deadline;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_assignment;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_assignment_reason;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_assignment_source;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_completed_at;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_escalated_unassigned;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_pending_holder;

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_status;

ALTER TABLE survey_request DROP CONSTRAINT ck_survey_request_status;

ALTER TABLE property_site DROP CONSTRAINT ck_property_site_district_code;

ALTER TABLE property_site DROP CONSTRAINT ck_property_site_province_code;

ALTER TABLE property_site DROP CONSTRAINT ck_property_site_ward_code;

ALTER TABLE knowledge_ingestion_job DROP CONSTRAINT ck_knowledge_ingestion_job_chunking;

ALTER TABLE knowledge_ingestion_job DROP CONSTRAINT ck_knowledge_ingestion_job_config_version;

ALTER TABLE knowledge_document_version DROP CONSTRAINT ck_knowledge_document_version_indexed_chunking;

ALTER TABLE knowledge_document_version DROP CONSTRAINT ck_knowledge_document_version_indexed_config;

DROP INDEX ix_installation_project_responsible_technician;

DROP INDEX "IX_installation_project_service_area_id";

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_accepted;

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_assigned;

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_cancelled;

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_round;

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_schedule;

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_started;

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_status;

ALTER TABLE installation_project DROP CONSTRAINT ck_installation_project_unassigned;

ALTER TABLE technician_appointment DROP COLUMN acceptance_deadline_at;

ALTER TABLE technician_appointment DROP COLUMN accepted_at;

ALTER TABLE technician_appointment DROP COLUMN assignment_reason;

ALTER TABLE technician_appointment DROP COLUMN assignment_source;

ALTER TABLE technician_appointment DROP COLUMN completed_at;

ALTER TABLE technician_appointment DROP COLUMN escalated_at;

ALTER TABLE technician_appointment DROP COLUMN service_area_id;

ALTER TABLE property_site DROP COLUMN district_code;

ALTER TABLE property_site DROP COLUMN province_code;

ALTER TABLE property_site DROP COLUMN ward_code;

ALTER TABLE knowledge_ingestion_job DROP COLUMN chunk_characters;

ALTER TABLE knowledge_ingestion_job DROP COLUMN chunk_overlap;

ALTER TABLE knowledge_ingestion_job DROP COLUMN config_version;

ALTER TABLE knowledge_document_version DROP COLUMN indexed_chunk_characters;

ALTER TABLE knowledge_document_version DROP COLUMN indexed_chunk_overlap;

ALTER TABLE knowledge_document_version DROP COLUMN indexed_config_version;

ALTER TABLE knowledge_document_version DROP COLUMN last_ingest_error;

ALTER TABLE knowledge_document_version DROP COLUMN last_ingest_status;

ALTER TABLE installation_project DROP COLUMN cancellation_reason;

ALTER TABLE installation_project DROP COLUMN cancelled_at;

ALTER TABLE installation_project DROP COLUMN responsible_technician_user_id;

ALTER TABLE installation_project DROP COLUMN scheduled_ends_at;

ALTER TABLE installation_project DROP COLUMN scheduled_starts_at;

ALTER TABLE installation_project DROP COLUMN service_area_id;

ALTER TABLE installation_project DROP COLUMN technician_accepted_at;

ALTER TABLE installation_event DROP COLUMN from_status;

ALTER TABLE installation_event DROP COLUMN to_status;

UPDATE technician_appointment SET status = 'BOOKED', assigned_technician_user_id = NULL
WHERE status IN ('PENDING_ACCEPTANCE', 'ESCALATED');
UPDATE technician_appointment SET status = 'ASSIGNED' WHERE status IN ('ACCEPTED', 'COMPLETED');
-- Acknowledged Sales assignments and claimed-but-unscheduled requests are restored exactly from kept data.
UPDATE technician_appointment SET status = 'ACKNOWLEDGED' WHERE status = 'ASSIGNED' AND acknowledged_at IS NOT NULL;
UPDATE survey_request SET status = 'Assigned' WHERE status = 'Pending' AND assigned_sale_id IS NOT NULL;
UPDATE installation_project SET status = 'PLANNING' WHERE status = 'PENDING_ASSIGNMENT';

UPDATE technician_appointment SET reserved_technician_user_id = '00000000-0000-0000-0000-000000000000' WHERE reserved_technician_user_id IS NULL;
ALTER TABLE technician_appointment ALTER COLUMN reserved_technician_user_id SET NOT NULL;
ALTER TABLE technician_appointment ALTER COLUMN reserved_technician_user_id SET DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE technician_profile ADD CONSTRAINT ck_technician_profile_base_paired CHECK ((base_latitude IS NULL) = (base_longitude IS NULL));

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_acknowledged CHECK (status <> 'ACKNOWLEDGED' OR acknowledged_at IS NOT NULL);

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_assignment CHECK (status NOT IN ('ASSIGNED', 'ACKNOWLEDGED') OR (assigned_technician_user_id IS NOT NULL AND assigned_technician_user_id = reserved_technician_user_id AND assigned_at IS NOT NULL));

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_status CHECK (status IN ('BOOKED', 'ASSIGNED', 'ACKNOWLEDGED', 'CANCELLED'));

ALTER TABLE installation_project ADD CONSTRAINT ck_installation_project_status CHECK (status IN ('PLANNING', 'SCHEDULED', 'IN_PROGRESS', 'PENDING_CUSTOMER_ACCEPTANCE', 'REWORK_REQUIRED', 'COMPLETED'));

ALTER TABLE pre_survey ADD CONSTRAINT fk_pre_survey_selected_simulation_same_pre_survey FOREIGN KEY (id, selected_simulation_id) REFERENCES solar_simulation (pre_survey_id, id) ON DELETE RESTRICT;

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261016161547_BusinessRulesFlow1And2Redesign';

COMMIT;

