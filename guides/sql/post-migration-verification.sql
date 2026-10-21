-- =====================================================================================================================
-- Post-migration (+ hardening) verification — READ ONLY (rolled back).
-- Run as the migration role after guides/deployment-readiness.md §3 steps 7–8:
--   psql "<connection>" -X -At -f post-migration-verification.sql
-- Every line is `check | value | OK|FAIL|INFO`. Any FAIL = stop and use the rollback column of the deployment plan.
-- =====================================================================================================================
BEGIN READ ONLY;

SELECT 'history', count(*) || ' / ' || max("MigrationId"),
       CASE WHEN count(*) = 13 AND max("MigrationId") = '20261016161547_BusinessRulesFlow1And2Redesign' THEN 'OK' ELSE 'FAIL' END
FROM "__EFMigrationsHistory";
SELECT 'new_tables', count(*)::text, CASE WHEN count(*) = 8 THEN 'OK' ELSE 'FAIL' END
FROM unnest(ARRAY['service_area', 'technician_service_area', 'survey_request_event', 'selected_simulation', 'product_image',
                  'installation_assignment', 'installation_evidence', 'rag_configuration']) t WHERE to_regclass('public.' || t) IS NOT NULL;
SELECT 'retained_legacy_tables', count(*)::text, CASE WHEN count(*) = 8 THEN 'OK' ELSE 'FAIL' END
FROM unnest(ARRAY['installation_milestone', 'installation_milestone_evidence', 'installation_shift', 'installation_shift_assignment',
                  'installation_team_member', 'solar_simulation', 'technician_schedule_recalculation', 'technician_travel_estimate']) t
WHERE to_regclass('public.' || t) IS NOT NULL;
SELECT 'exclusion_constraints', string_agg(conname, ',' ORDER BY conname),
       CASE WHEN count(*) FILTER (WHERE conname IN ('ex_technician_appointment_no_overlap', 'ex_installation_assignment_no_overlap')) = 2 THEN 'OK' ELSE 'FAIL' END
FROM pg_constraint WHERE contype = 'x';
SELECT 'invalid_constraints', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'public' AND NOT c.convalidated;
SELECT 'invalid_indexes', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END FROM pg_index WHERE NOT indisvalid;

-- Data mapping.
SELECT 'appointment_vocabulary', coalesce(string_agg(DISTINCT status, ','), '-'),
       CASE WHEN bool_and(status IN ('PENDING_ACCEPTANCE', 'ESCALATED', 'ACCEPTED', 'ASSIGNED', 'COMPLETED', 'CANCELLED')) IS NOT FALSE THEN 'OK' ELSE 'FAIL' END
FROM technician_appointment;
SELECT 'pending_without_holder', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM technician_appointment WHERE status = 'PENDING_ACCEPTANCE' AND reserved_technician_user_id IS NULL;
SELECT 'confirmed_consistency', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM technician_appointment WHERE status IN ('ACCEPTED', 'ASSIGNED', 'COMPLETED')
  AND (assigned_technician_user_id IS DISTINCT FROM reserved_technician_user_id OR assignment_source IS NULL OR assigned_at IS NULL);
SELECT 'survey_request_vocabulary', coalesce(string_agg(DISTINCT status, ','), '-'),
       CASE WHEN bool_and(status IN ('Pending', 'Scheduled', 'Completed', 'Cancelled')) IS NOT FALSE THEN 'OK' ELSE 'FAIL' END FROM survey_request;
SELECT 'scheduled_has_confirmed_appointment', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM survey_request s WHERE s.status = 'Scheduled' AND NOT EXISTS (
    SELECT 1 FROM technician_appointment a WHERE a.survey_request_id = s.id AND a.status IN ('ACCEPTED', 'ASSIGNED', 'COMPLETED'));
SELECT 'selection_summaries', (SELECT count(*) FROM selected_simulation) || ' / ' || (SELECT count(*) FROM pre_survey WHERE selected_simulation_id IS NOT NULL),
       CASE WHEN (SELECT count(*) FROM selected_simulation) = (SELECT count(*) FROM pre_survey WHERE selected_simulation_id IS NOT NULL) THEN 'OK' ELSE 'FAIL' END;
SELECT 'one_primary_image_per_product', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM (SELECT product_id FROM product_image GROUP BY product_id HAVING count(*) FILTER (WHERE is_primary) <> 1) x;
SELECT 'products_with_legacy_image_only', count(*)::text, 'INFO re-add through the gallery API'
FROM product p WHERE NULLIF(btrim(p.image_url), '') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM product_image i WHERE i.product_id = p.id);

-- Security (after the hardening).
WITH t AS MATERIALIZED (SELECT c.oid, c.relrowsecurity FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p'))
SELECT 'client_role_access', 'anon=' || count(*) FILTER (WHERE has_table_privilege('anon', oid, 'SELECT,INSERT,UPDATE,DELETE'))
       || ' authenticated=' || count(*) FILTER (WHERE has_table_privilege('authenticated', oid, 'SELECT,INSERT,UPDATE,DELETE'))
       || ' rls=' || count(*) FILTER (WHERE relrowsecurity) || '/' || count(*),
       CASE WHEN count(*) FILTER (WHERE has_table_privilege('anon', oid, 'SELECT,INSERT,UPDATE,DELETE')
                                      OR has_table_privilege('authenticated', oid, 'SELECT,INSERT,UPDATE,DELETE')) = 0
             AND bool_and(relrowsecurity) THEN 'OK' ELSE 'FAIL' END
FROM t;
SELECT 'column_acls_client_roles', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace, LATERAL aclexplode(a.attacl) x
WHERE n.nspname = 'public' AND a.attnum > 0 AND a.attacl IS NOT NULL AND x.grantee IN (SELECT oid FROM pg_roles WHERE rolname IN ('anon', 'authenticated'));
SELECT 'default_acl_client_roles', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM pg_default_acl d JOIN pg_namespace n ON n.oid = d.defaclnamespace
WHERE n.nspname = 'public' AND d.defaclrole = current_user::regrole
  AND EXISTS (SELECT 1 FROM unnest(d.defaclacl) a WHERE split_part(a::text, '=', 1) IN ('anon', 'authenticated'));

ROLLBACK;
