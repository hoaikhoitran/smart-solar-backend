-- =====================================================================================================================
-- Pre-migration checks for 20261016161547_BusinessRulesFlow1And2Redesign — READ ONLY (rolled back).
-- Run as the migration role:  psql "<connection>" -X -At -f pre-migration-checks.sql
-- Every line is `check | value | OK|FAIL|INFO`. Any FAIL = stop (see guides/deployment-readiness.md §3 step 5).
-- =====================================================================================================================
BEGIN READ ONLY;

SELECT 'history_count', count(*)::text, CASE WHEN count(*) = 12 THEN 'OK' ELSE 'FAIL' END FROM "__EFMigrationsHistory";
SELECT 'history_last', max("MigrationId"), CASE WHEN max("MigrationId") = '20261013182132_InstallationWorkflowAndKnowledgeBase' THEN 'OK' ELSE 'FAIL' END
FROM "__EFMigrationsHistory";
SELECT 'new_tables_absent', coalesce(string_agg(t, ','), 'none'), CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL' END
FROM unnest(ARRAY['service_area', 'technician_service_area', 'survey_request_event', 'selected_simulation', 'product_image',
                  'installation_assignment', 'installation_evidence', 'rag_configuration']) t WHERE to_regclass('public.' || t) IS NOT NULL;
SELECT 'btree_gist', coalesce((SELECT n.nspname || ' ' || e.extversion FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
                               WHERE e.extname = 'btree_gist'), 'missing'),
       CASE WHEN EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'btree_gist') THEN 'OK' ELSE 'FAIL' END;
SELECT 'owner_of_all_public_tables', current_user, CASE WHEN NOT EXISTS (
    SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relowner <> current_user::regrole) THEN 'OK' ELSE 'FAIL' END;

-- Data the migration refuses to guess (its guard raises): must be 0.
SELECT 'installation_unmappable',
       ((SELECT count(*) FROM installation_project WHERE status <> 'PLANNING')
        + (SELECT count(*) FROM installation_shift_assignment WHERE status = 'ACTIVE')
        + (SELECT count(*) FROM installation_team_member WHERE removed_at IS NULL))::text,
       CASE WHEN (SELECT count(*) FROM installation_project WHERE status <> 'PLANNING') = 0
             AND (SELECT count(*) FROM installation_shift_assignment WHERE status = 'ACTIVE') = 0
             AND (SELECT count(*) FROM installation_team_member WHERE removed_at IS NULL) = 0 THEN 'OK' ELSE 'FAIL' END;

-- Informational (expected values for Supabase Dev on 2026-10-19 in brackets).
SELECT 'appointments_by_status', string_agg(status || '=' || n, ',' ORDER BY status), 'INFO [ACKNOWLEDGED=2,CANCELLED=1]'
FROM (SELECT status, count(*) n FROM technician_appointment GROUP BY status) x;
SELECT 'open_legacy_bookings', count(*)::text, 'INFO [0] (BOOKED → PENDING_ACCEPTANCE without service area)' FROM technician_appointment WHERE status = 'BOOKED';
SELECT 'survey_requests_by_status', string_agg(status || '=' || n, ',' ORDER BY status), 'INFO [Assigned=8,Pending=1,Scheduled=2]'
FROM (SELECT status, count(*) n FROM survey_request GROUP BY status) x;
SELECT 'selected_simulations', count(*)::text, 'INFO [11] (= selected_simulation rows after the migration)' FROM pre_survey WHERE selected_simulation_id IS NOT NULL;
SELECT 'product_images_not_migrated', count(*)::text,
       CASE WHEN count(*) = 0 THEN 'OK' ELSE 'INFO [1] re-add these images through the gallery API after the migration' END
FROM product WHERE NULLIF(btrim(image_url), '') IS NOT NULL AND NOT (btrim(image_url) LIKE 'https://%' AND length(btrim(image_url)) <= 1000);
SELECT 'sites_without_codes', count(*)::text, 'INFO [34] customers must set administrative codes before booking' FROM property_site;
-- Writers still connected (exclude this session and Supabase internals).
SELECT 'other_client_sessions', count(*)::text, CASE WHEN count(*) = 0 THEN 'OK' ELSE 'FAIL (stop API/RAG instances first)' END
FROM pg_stat_activity
WHERE datname = current_database() AND pid <> pg_backend_pid() AND usename = current_user
  AND coalesce(application_name, '') NOT IN ('Supavisor', 'pg_net 0.20.4', 'smartsolar-readonly-audit')
  AND application_name NOT LIKE 'pg_net%';

ROLLBACK;
