-- =====================================================================================================================
-- Post-migration (+ hardening) verification for 20261020051352_AdministrativeLocationsZonesDispatchAndSettings — READ ONLY.
--   psql "<connection>" -X -At -f post-migration-verification-zones-dispatch.sql
-- Every line is `check | value | OK|FAIL|INFO`. Any FAIL = stop; see guides/database-migration-administrative-zones-dispatch.md.
-- =====================================================================================================================
BEGIN READ ONLY;

SELECT 'history', count(*) || ' / ' || max("MigrationId"),
       CASE WHEN count(*) = 14 AND max("MigrationId") = '20261020051352_AdministrativeLocationsZonesDispatchAndSettings' THEN 'OK' ELSE 'FAIL' END
FROM "__EFMigrationsHistory";
SELECT 'new_tables_with_rls', count(*) || ' / rls ' || count(*) FILTER (WHERE c.relrowsecurity),
       CASE WHEN count(*) = 16 AND bool_and(c.relrowsecurity) THEN 'OK' ELSE 'FAIL' END
FROM unnest(ARRAY['administrative_catalog_import', 'administrative_commune', 'administrative_legacy_commune_map', 'administrative_province',
                  'cross_zone_authorization', 'dispatch_event', 'dispatch_notification', 'dispatch_preferred_window', 'dispatch_proposal',
                  'dispatch_request', 'operational_zone', 'operational_zone_commune', 'system_setting', 'system_setting_revision',
                  'system_settings_version', 'technician_zone_membership']) t
JOIN pg_class c ON c.oid = to_regclass('public.' || t);
SELECT 'retained_legacy', count(*)::text, CASE WHEN count(*) = 6 THEN 'OK' ELSE 'FAIL' END
FROM (SELECT 1 FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relname IN ('service_area', 'technician_service_area')
      UNION ALL
      SELECT 1 FROM information_schema.columns WHERE table_schema = 'public'
        AND ((table_name = 'property_site' AND column_name IN ('district_code', 'ward_code'))
          OR (table_name IN ('technician_appointment', 'installation_project') AND column_name = 'service_area_id'))) x;
SELECT 'exclusive_commune_index', count(*)::text, CASE WHEN count(*) = 1 THEN 'OK' ELSE 'FAIL' END
FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ux_operational_zone_commune_active' AND indexdef LIKE '%UNIQUE%removed_at IS NULL%';
SELECT 'assignment_source_check', pg_get_constraintdef(oid),
       CASE WHEN pg_get_constraintdef(oid) LIKE '%MANAGER_DISPATCH%' THEN 'OK' ELSE 'FAIL' END
FROM pg_constraint WHERE conname = 'ck_technician_appointment_assignment_source';
SELECT 'invalid_constraints', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM pg_constraint WHERE connamespace = 'public'::regnamespace AND NOT convalidated;
SELECT 'invalid_indexes', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END FROM pg_index WHERE NOT indisvalid;

-- Catalog (after the import CLI ran with --apply; INFO before that).
SELECT 'catalog', (SELECT count(*) FROM administrative_province) || ' province / ' || (SELECT count(*) FROM administrative_commune WHERE valid_to IS NULL)
       || ' communes / ' || (SELECT count(*) FROM administrative_legacy_commune_map) || ' legacy rows',
       CASE WHEN (SELECT count(*) FROM administrative_commune WHERE valid_to IS NULL) = 168
             AND (SELECT count(*) FROM administrative_legacy_commune_map) = 493 THEN 'OK' ELSE 'INFO run the catalog import' END;
SELECT 'one_active_zone_per_commune', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM (SELECT commune_code FROM operational_zone_commune WHERE removed_at IS NULL GROUP BY commune_code HAVING count(*) > 1) x;
SELECT 'sites_without_commune', count(*)::text, 'INFO customers confirm through the legacy lookup' FROM property_site WHERE commune_code IS NULL;

-- Security (after the hardening script).
WITH t AS MATERIALIZED (SELECT c.oid, c.relrowsecurity FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p'))
SELECT 'client_role_access', 'anon=' || count(*) FILTER (WHERE has_table_privilege('anon', oid, 'SELECT,INSERT,UPDATE,DELETE'))
       || ' authenticated=' || count(*) FILTER (WHERE has_table_privilege('authenticated', oid, 'SELECT,INSERT,UPDATE,DELETE'))
       || ' rls=' || count(*) FILTER (WHERE relrowsecurity) || '/' || count(*),
       CASE WHEN count(*) FILTER (WHERE has_table_privilege('anon', oid, 'SELECT,INSERT,UPDATE,DELETE')
                                      OR has_table_privilege('authenticated', oid, 'SELECT,INSERT,UPDATE,DELETE')) = 0
             AND bool_and(relrowsecurity) THEN 'OK' ELSE 'FAIL' END
FROM t;

ROLLBACK;
