START TRANSACTION;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM technician_appointment WHERE assignment_source = 'MANAGER_DISPATCH') THEN
        RAISE EXCEPTION 'Down refused: technician_appointment rows with assignment_source MANAGER_DISPATCH exist; '
                        'the previous schema cannot represent them. Restore from backup instead.';
    END IF;
END $$;

ALTER TABLE installation_project DROP CONSTRAINT fk_installation_project_operational_zone;

ALTER TABLE property_site DROP CONSTRAINT fk_property_site_administrative_commune;

ALTER TABLE technician_appointment DROP CONSTRAINT fk_technician_appointment_cross_zone_authorization;

ALTER TABLE technician_appointment DROP CONSTRAINT fk_technician_appointment_operational_zone;

DROP TABLE administrative_catalog_import;

DROP TABLE administrative_legacy_commune_map;

DROP TABLE dispatch_notification;

DROP TABLE dispatch_preferred_window;

DROP TABLE operational_zone_commune;

DROP TABLE system_setting;

DROP TABLE system_setting_revision;

DROP TABLE system_settings_version;

DROP TABLE technician_zone_membership;

DROP TABLE dispatch_event;

DROP TABLE administrative_commune;

DROP TABLE dispatch_proposal;

DROP TABLE administrative_province;

DROP TABLE cross_zone_authorization;

DROP TABLE dispatch_request;

DROP TABLE operational_zone;

DROP INDEX "IX_technician_appointment_cross_zone_authorization_id";

ALTER TABLE technician_appointment DROP CONSTRAINT ck_technician_appointment_assignment_source;

DROP INDEX ix_property_site_province_commune;

ALTER TABLE property_site DROP CONSTRAINT ck_property_site_commune_code;

ALTER TABLE technician_appointment DROP COLUMN acceptance_reminder_sent_at;

ALTER TABLE technician_appointment DROP COLUMN acceptance_window_minutes;

ALTER TABLE technician_appointment DROP COLUMN cross_zone_authorization_id;

ALTER TABLE technician_appointment DROP COLUMN settings_version;

ALTER TABLE property_site DROP COLUMN commune_code;

DROP INDEX ix_technician_appointment_operational_zone_id;

ALTER TABLE technician_appointment DROP COLUMN operational_zone_id;

DROP INDEX "IX_installation_project_operational_zone_id";

ALTER TABLE installation_project DROP COLUMN operational_zone_id;

ALTER TABLE technician_appointment ADD CONSTRAINT ck_technician_appointment_assignment_source CHECK (assignment_source IS NULL OR assignment_source IN ('SELF_ACCEPTED', 'SALES_ASSIGNED', 'MANAGER_OVERRIDE'));

ALTER TABLE property_site ADD CONSTRAINT ck_property_site_district_code CHECK (district_code IS NULL OR (province_code IS NOT NULL AND district_code LIKE (province_code || '.%') AND district_code NOT LIKE '%.%.%'));

ALTER TABLE property_site ADD CONSTRAINT ck_property_site_ward_code CHECK (ward_code IS NULL OR (district_code IS NOT NULL AND ward_code LIKE (district_code || '.%') AND ward_code NOT LIKE '%.%.%.%'));

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261020051352_AdministrativeLocationsZonesDispatchAndSettings';

COMMIT;

