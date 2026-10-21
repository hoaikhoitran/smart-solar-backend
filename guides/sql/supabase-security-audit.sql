-- =====================================================================================================================
-- Supabase security audit — READ ONLY. Safe to run at any time (the transaction is read-only and rolled back).
-- Run as the migration/owner role (postgres) with psql:  psql "<connection>" -X -At -f supabase-security-audit.sql
-- Findings on Supabase Dev (2026-10-17) are recorded in guides/supabase-security.md.
-- =====================================================================================================================
BEGIN READ ONLY;

-- Who am I (the role the API and migrations use)?
SELECT 'role', current_user, r.rolsuper, r.rolcreaterole, r.rolbypassrls FROM pg_roles r WHERE r.rolname = current_user;

-- Tables in public: RLS, owners, and what anon / authenticated may do.
-- MATERIALIZED: the planner must not evaluate privilege functions on catalog rows outside public.
WITH t AS MATERIALIZED (
    SELECT c.oid, c.relname, c.relrowsecurity, pg_get_userbyid(c.relowner) AS owner
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p'))
SELECT 'public_tables', count(*),
       'rls_on', count(*) FILTER (WHERE relrowsecurity),
       'owners', string_agg(DISTINCT owner, ','),
       'anon_select', count(*) FILTER (WHERE has_table_privilege('anon', oid, 'SELECT')),
       'anon_write', count(*) FILTER (WHERE has_table_privilege('anon', oid, 'INSERT,UPDATE,DELETE')),
       'authenticated_select', count(*) FILTER (WHERE has_table_privilege('authenticated', oid, 'SELECT')),
       'authenticated_write', count(*) FILTER (WHERE has_table_privilege('authenticated', oid, 'INSERT,UPDATE,DELETE'))
FROM t;

-- Per-table detail (tables a client role can reach).
WITH t AS MATERIALIZED (
    SELECT c.oid, c.relname, c.relrowsecurity FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p'))
SELECT 'table', relname, relrowsecurity AS rls,
       has_table_privilege('anon', oid, 'SELECT') AS anon_select,
       has_table_privilege('anon', oid, 'INSERT,UPDATE,DELETE') AS anon_write,
       has_table_privilege('authenticated', oid, 'SELECT') AS auth_select
FROM t ORDER BY relname;

SELECT 'policies', schemaname, tablename, policyname, roles::text, cmd FROM pg_policies WHERE schemaname = 'public' ORDER BY tablename;

WITH f AS MATERIALIZED (SELECT p.oid FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public')
SELECT 'functions_anon_execute', count(*) FILTER (WHERE has_function_privilege('anon', oid, 'EXECUTE')), count(*) FROM f;
-- A SECURITY DEFINER function executable by anon would run with its owner's rights: must be 0.
SELECT 'public_functions', count(*), 'security_definer', count(*) FILTER (WHERE p.prosecdef),
       'from_extension', count(*) FILTER (WHERE EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e'))
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public';

SELECT 'schema_usage', r, has_schema_privilege(r, 'public', 'USAGE') AS usage, has_schema_privilege(r, 'public', 'CREATE') AS "create"
FROM unnest(ARRAY['anon', 'authenticated', 'service_role']) r;

-- Privileges future objects will get automatically.
SELECT 'default_acl', defaclrole::regrole, defaclobjtype,
       (SELECT string_agg(split_part(a::text, '=', 1), ',') FROM unnest(defaclacl) a) AS grantees
FROM pg_default_acl d JOIN pg_namespace n ON n.oid = d.defaclnamespace WHERE n.nspname = 'public' ORDER BY 2, 3;

-- PostgREST (Data API) schema exposure, when configured on the authenticator role (otherwise: project settings).
SELECT 'pgrst', rolname, (SELECT string_agg(x, ' | ') FROM unnest(rolconfig) x WHERE x LIKE 'pgrst.%')
FROM pg_roles WHERE rolname = 'authenticator';

-- Extensions the migrations depend on.
SELECT 'extension', extname, n.nspname, extversion FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
WHERE extname IN ('btree_gist', 'vector', 'pgcrypto') ORDER BY 2;
SELECT 'gist_uuid_ops', n.nspname FROM pg_opclass o JOIN pg_am a ON a.oid = o.opcmethod JOIN pg_namespace n ON n.oid = o.opcnamespace
WHERE a.amname = 'gist' AND o.opcname = 'gist_uuid_ops';
SELECT 'search_path', current_setting('search_path');

ROLLBACK;
