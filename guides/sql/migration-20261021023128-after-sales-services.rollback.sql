START TRANSACTION;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM payment_attempt WHERE contract_id IS NULL) OR EXISTS (SELECT 1 FROM payment_event WHERE contract_id IS NULL) THEN
        RAISE EXCEPTION 'Down refused: after-sales checkouts without a contract exist; the previous schema cannot represent them. Restore from backup instead.';
    END IF;
END $$;

ALTER TABLE payment_attempt_item DROP CONSTRAINT IF EXISTS fk_payment_attempt_item_attempt_contract;
ALTER TABLE payment_attempt DROP CONSTRAINT IF EXISTS ux_payment_attempt_id_contract;

ALTER TABLE maintenance_entitlement DROP CONSTRAINT fk_maintenance_entitlement_current_request;

ALTER TABLE installation_assignment DROP CONSTRAINT fk_installation_assignment_service_visit;

ALTER TABLE installed_asset DROP CONSTRAINT fk_installed_asset_replaces;

ALTER TABLE installed_asset DROP CONSTRAINT fk_installed_asset_service_request;

DROP TABLE after_sales_event;

DROP TABLE after_sales_notification;

DROP TABLE payment_attempt_item;

DROP TABLE refund_record;

DROP TABLE sales_task;

DROP TABLE service_evidence;

DROP TABLE service_item;

DROP TABLE warranty_option;

DROP TABLE service_visit;

DROP TABLE service_quotation;

DROP TABLE disaster_claim;

DROP TABLE service_request;

DROP TABLE maintenance_entitlement;

DROP TABLE service_subscription;

DROP TABLE service_package;

DROP INDEX "IX_installed_asset_replaces_asset_id";

DROP INDEX "IX_installed_asset_service_request_id";

ALTER TABLE installed_asset DROP CONSTRAINT ck_installed_asset_status;

DROP INDEX ux_installation_assignment_current;

DROP INDEX ux_installation_assignment_current_visit;

ALTER TABLE file_reference DROP CONSTRAINT ck_file_reference_purpose;

ALTER TABLE installed_asset DROP COLUMN replaces_asset_id;

ALTER TABLE installed_asset DROP COLUMN service_request_id;

ALTER TABLE installed_asset DROP COLUMN specifications;

ALTER TABLE installed_asset DROP COLUMN status;

ALTER TABLE installation_assignment DROP COLUMN service_visit_id;

ALTER TABLE customer_contract DROP COLUMN warranty_months;

ALTER TABLE payment_event ALTER COLUMN contract_id SET NOT NULL;

ALTER TABLE payment_attempt ALTER COLUMN contract_id SET NOT NULL;

CREATE UNIQUE INDEX ux_installation_assignment_current ON installation_assignment (project_id) WHERE status IN ('PENDING', 'ACCEPTED');

ALTER TABLE file_reference ADD CONSTRAINT ck_file_reference_purpose CHECK (purpose IN ('ONSITE_SURVEY_EVIDENCE', 'QUOTATION_DOCUMENT', 'CONTRACT_DOCUMENT', 'SIGNED_CONTRACT', 'INSTALLATION_EVIDENCE', 'KNOWLEDGE_DOCUMENT', 'PROJECT_DOCUMENT'));

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261021023128_AfterSalesServices';

COMMIT;

