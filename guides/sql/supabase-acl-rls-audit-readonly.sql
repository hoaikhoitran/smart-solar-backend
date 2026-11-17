-- =====================================================================================================================
-- SmartSolar — Supabase ACL / RLS audit. READ ONLY (2026-11-16, full-project production audit, findings DB-01..DB-04).
-- Every statement is a SELECT on catalogs; the whole script runs in a read-only transaction that is rolled back.
-- Run as the owner role the API uses:  psql "<connection>" -X -At -v ON_ERROR_STOP=1 -f supabase-acl-rls-audit-readonly.sql
-- Nothing here changes privileges. Hardening stays the reviewed draft guides/sql/supabase-security-hardening-draft.sql.
-- =====================================================================================================================
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '30s';

-- 0. Context: who runs this, which roles exist.
SELECT 'whoami', current_user, session_user, r.rolsuper, r.rolbypassrls FROM pg_roles r WHERE r.rolname = current_user;
SELECT 'role', rolname, rolcanlogin, rolinherit, rolbypassrls, rolsuper
FROM pg_roles WHERE rolname IN ('anon', 'authenticated', 'service_role', 'authenticator', 'postgres', 'supabase_admin', 'rag_service')
ORDER BY rolname;

-- 1. Table privileges of the client roles for EVERY public table (SELECT/INSERT/UPDATE/DELETE/TRUNCATE/REFERENCES/TRIGGER),
--    including what PUBLIC grants. Expected for SmartSolar: no row with any true value.
WITH t AS MATERIALIZED (
    SELECT c.oid, c.relname, c.relrowsecurity, c.relforcerowsecurity
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'f')),
r(role) AS (VALUES ('anon'), ('authenticated'), ('public'))
SELECT 'table_priv', t.relname, r.role,
       has_table_privilege(CASE r.role WHEN 'public' THEN 'public' ELSE r.role END, t.oid, 'SELECT')     AS sel,
       has_table_privilege(CASE r.role WHEN 'public' THEN 'public' ELSE r.role END, t.oid, 'INSERT')     AS ins,
       has_table_privilege(CASE r.role WHEN 'public' THEN 'public' ELSE r.role END, t.oid, 'UPDATE')     AS upd,
       has_table_privilege(CASE r.role WHEN 'public' THEN 'public' ELSE r.role END, t.oid, 'DELETE')     AS del,
       has_table_privilege(CASE r.role WHEN 'public' THEN 'public' ELSE r.role END, t.oid, 'TRUNCATE')   AS trunc,
       has_table_privilege(CASE r.role WHEN 'public' THEN 'public' ELSE r.role END, t.oid, 'REFERENCES') AS refs,
       has_table_privilege(CASE r.role WHEN 'public' THEN 'public' ELSE r.role END, t.oid, 'TRIGGER')    AS trig,
       t.relrowsecurity AS rls, t.relforcerowsecurity AS force_rls
FROM t CROSS JOIN r
WHERE has_table_privilege(CASE r.role WHEN 'public' THEN 'public' ELSE r.role END, t.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
ORDER BY t.relname, r.role;

-- 1a. Summary per role (0 expected for anon / authenticated / public).
WITH t AS MATERIALIZED (SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p'))
SELECT 'table_priv_summary', role,
       count(*) FILTER (WHERE has_table_privilege(role, oid, 'SELECT')) AS sel,
       count(*) FILTER (WHERE has_table_privilege(role, oid, 'INSERT')) AS ins,
       count(*) FILTER (WHERE has_table_privilege(role, oid, 'UPDATE')) AS upd,
       count(*) FILTER (WHERE has_table_privilege(role, oid, 'DELETE')) AS del,
       count(*) AS tables
FROM t CROSS JOIN (VALUES ('anon'), ('authenticated'), ('public'), ('service_role')) v(role)
WHERE EXISTS (SELECT 1 FROM pg_roles WHERE rolname = v.role) OR v.role = 'public'
GROUP BY role ORDER BY role;

-- 1b. The tables created after the 2026-10-20 RLS convention whose migrations do NOT enable RLS (audit finding DB-01).
SELECT 'post_convention_table', c.relname, c.relrowsecurity AS rls,
       has_table_privilege('anon', c.oid, 'SELECT, INSERT, UPDATE, DELETE') AS anon_any,
       has_table_privilege('authenticated', c.oid, 'SELECT, INSERT, UPDATE, DELETE') AS authenticated_any,
       pg_get_userbyid(c.relowner) AS owner
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relname IN ('knowledge_document_audience_change', 'installation_equipment_substitution',
      'installation_equipment_substitution_item', 'iot_device', 'iot_device_binding', 'iot_telemetry_reading', 'password_reset_dispatch')
ORDER BY c.relname;

-- 2. Column-level ACLs (a column grant survives a table-level REVOKE). Expected: no rows.
SELECT 'column_acl', c.relname, a.attname, a.attacl::text
FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND a.attacl IS NOT NULL AND a.attnum > 0 AND NOT a.attisdropped
ORDER BY c.relname, a.attname;

-- 3. Raw table ACLs (who granted what to whom), for tables that grant anything to a client role or PUBLIC.
SELECT 'table_acl', c.relname, acl.grantee::regrole::text AS grantee, acl.privilege_type, acl.is_grantable
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace, LATERAL aclexplode(c.relacl) acl
WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm')
  AND (acl.grantee = 0 OR acl.grantee IN (SELECT oid FROM pg_roles WHERE rolname IN ('anon', 'authenticated')))
ORDER BY c.relname, grantee, privilege_type;

-- 4. Sequences (identity / serial) reachable by client roles. Expected: no rows.
SELECT 'sequence_priv', c.relname, role, has_sequence_privilege(role, c.oid, 'USAGE') AS usage, has_sequence_privilege(role, c.oid, 'UPDATE') AS upd
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace CROSS JOIN (VALUES ('anon'), ('authenticated')) v(role)
WHERE n.nspname = 'public' AND c.relkind = 'S' AND has_sequence_privilege(role, c.oid, 'USAGE, SELECT, UPDATE');

-- 5. Default privileges future objects receive (any grant to anon / authenticated / PUBLIC re-exposes the next migration's tables).
SELECT 'default_acl', pg_get_userbyid(d.defaclrole) AS creator, n.nspname, d.defaclobjtype,
       acl.grantee::regrole::text AS grantee, acl.privilege_type
FROM pg_default_acl d LEFT JOIN pg_namespace n ON n.oid = d.defaclnamespace, LATERAL aclexplode(d.defaclacl) acl
WHERE n.nspname IS NULL OR n.nspname = 'public'
ORDER BY creator, defaclobjtype, grantee, privilege_type;

-- 6. RLS and policy coverage: tables with RLS off, and every policy (a permissive policy for anon/authenticated would be a leak).
SELECT 'rls_off', c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relrowsecurity ORDER BY c.relname;
SELECT 'rls_summary', count(*) AS tables, count(*) FILTER (WHERE c.relrowsecurity) AS rls_on, count(*) FILTER (WHERE c.relforcerowsecurity) AS force_rls
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p');
SELECT 'policy', schemaname, tablename, policyname, permissive, roles::text, cmd, qual, with_check
FROM pg_policies WHERE schemaname = 'public' ORDER BY tablename, policyname;

-- 7. Functions: SECURITY DEFINER anywhere outside Supabase-managed schemas, and EXECUTE for anon/authenticated in public.
SELECT 'security_definer', n.nspname, p.proname, pg_get_userbyid(p.proowner) AS owner,
       has_function_privilege('anon', p.oid, 'EXECUTE') AS anon_exec, has_function_privilege('authenticated', p.oid, 'EXECUTE') AS auth_exec,
       p.proconfig::text AS config
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'auth', 'storage', 'realtime', 'graphql', 'graphql_public',
      'extensions', 'pgsodium', 'pgsodium_masks', 'vault', 'supabase_functions', 'net', 'pgbouncer', 'cron', 'supabase_migrations')
ORDER BY n.nspname, p.proname;
SELECT 'public_function_exec', count(*) AS functions,
       count(*) FILTER (WHERE has_function_privilege('anon', p.oid, 'EXECUTE')) AS anon_exec,
       count(*) FILTER (WHERE has_function_privilege('authenticated', p.oid, 'EXECUTE')) AS authenticated_exec,
       count(*) FILTER (WHERE p.prosecdef) AS security_definer
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public';

-- 8. Schema privileges (CREATE on public for a client role would let it plant objects).
SELECT 'schema_priv', n.nspname, role, has_schema_privilege(role, n.oid, 'USAGE') AS usage, has_schema_privilege(role, n.oid, 'CREATE') AS "create"
FROM pg_namespace n CROSS JOIN (VALUES ('anon'), ('authenticated'), ('public')) v(role)
WHERE n.nspname IN ('public', 'extensions');

-- 9. Data API exposure. When pgrst.db_schemas is not on the authenticator role it is a project setting (Dashboard → Settings → API):
--    record "NOT VERIFIED" in that case. Also live PostgREST sessions.
SELECT 'pgrst_config', rolname, (SELECT string_agg(x, ' | ') FROM unnest(rolconfig) x WHERE x LIKE 'pgrst.%') FROM pg_roles WHERE rolname = 'authenticator';
SELECT 'pgrst_sessions', usename, application_name, count(*) FROM pg_stat_activity WHERE usename = 'authenticator' GROUP BY 1, 2, 3;
SELECT 'role_membership', m.roleid::regrole::text AS role, m.member::regrole::text AS member, m.admin_option
FROM pg_auth_members m WHERE m.member::regrole::text IN ('authenticator', 'postgres') ORDER BY 1, 2;

-- 10. Other ways out of the API: realtime publication, triggers, foreign tables, views in public.
SELECT 'publication_table', p.pubname, pt.schemaname, pt.tablename FROM pg_publication p JOIN pg_publication_tables pt ON pt.pubname = p.pubname
WHERE pt.schemaname = 'public';
SELECT 'user_trigger', event_object_table, trigger_name FROM information_schema.triggers WHERE trigger_schema = 'public';
SELECT 'view_or_foreign', c.relname, c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind IN ('v', 'm', 'f');

-- 11. Privileges of the application and RAG connection roles (who the services really are). Replace nothing: it reads the roles
--     that hold LOGIN and own or can write SmartSolar tables.
SELECT 'writer_role', r.rolname, r.rolcanlogin, r.rolbypassrls,
       count(*) FILTER (WHERE has_table_privilege(r.oid, c.oid, 'INSERT, UPDATE, DELETE')) AS writable_tables
FROM pg_roles r CROSS JOIN pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind = 'r' AND r.rolcanlogin
GROUP BY r.rolname, r.rolcanlogin, r.rolbypassrls HAVING count(*) FILTER (WHERE has_table_privilege(r.oid, c.oid, 'INSERT, UPDATE, DELETE')) > 0
ORDER BY r.rolname;

-- 12. Sensitive columns at rest (names only, no data): tables a client role could read if grants regress.
SELECT 'sensitive_column', c.relname, a.attname
FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
  AND a.attname IN ('password_hash', 'token_hash', 'secret_hash', 'reset_url', 'email', 'phone', 'checksum_key')
ORDER BY c.relname, a.attname;

-- 13. Migration history and schema drift indicators.
SELECT 'ef_history', count(*), max("MigrationId") FROM "__EFMigrationsHistory";
SELECT 'invalid_constraint', conrelid::regclass::text, conname FROM pg_constraint WHERE NOT convalidated;
SELECT 'invalid_index', indexrelid::regclass::text FROM pg_index WHERE NOT indisvalid;

ROLLBACK;
