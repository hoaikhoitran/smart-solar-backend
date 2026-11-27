START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    ALTER TABLE cash_receipt_submission ALTER COLUMN contract_installment_id DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    ALTER TABLE cash_receipt_submission ALTER COLUMN contract_id DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    ALTER TABLE cash_receipt_submission ADD kind character varying(30) NOT NULL DEFAULT 'INSTALLMENT';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    ALTER TABLE cash_receipt_submission ADD service_quotation_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    ALTER TABLE cash_receipt_submission ADD service_request_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    CREATE INDEX ix_cash_receipt_submission_service_quotation ON cash_receipt_submission (service_quotation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    CREATE INDEX ix_cash_receipt_submission_service_request_status ON cash_receipt_submission (service_request_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    ALTER TABLE cash_receipt_submission ADD CONSTRAINT ck_cash_receipt_submission_context CHECK ((kind = 'INSTALLMENT' AND contract_id IS NOT NULL AND contract_installment_id IS NOT NULL AND service_quotation_id IS NULL AND service_request_id IS NULL) OR (kind = 'SERVICE_QUOTATION' AND service_quotation_id IS NOT NULL AND service_request_id IS NOT NULL AND contract_id IS NULL AND contract_installment_id IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    ALTER TABLE cash_receipt_submission ADD CONSTRAINT fk_cash_receipt_submission_service_quotation FOREIGN KEY (service_quotation_id) REFERENCES service_quotation (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    ALTER TABLE cash_receipt_submission ADD CONSTRAINT fk_cash_receipt_submission_service_request FOREIGN KEY (service_request_id) REFERENCES service_request (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261127124918_UnifiedCashReceipts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261127124918_UnifiedCashReceipts', '8.0.34');
    END IF;
END $EF$;
COMMIT;

