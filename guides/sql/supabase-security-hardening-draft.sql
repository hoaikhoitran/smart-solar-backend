-- =====================================================================================================================
-- DRAFT — Supabase security hardening for the SmartSolar database — DO NOT RUN WITHOUT EXPLICIT WRITTEN APPROVAL
--
-- Status: NOT APPLIED anywhere remote. Tested on a disposable local PostgreSQL that emulates Supabase's anon /
-- authenticated / service_role roles and default privileges (guides/supabase-security.md §4).
--
-- Why: on Supabase Dev every table in `public` grants SELECT/INSERT/UPDATE/DELETE to `anon` and `authenticated`
-- (Supabase default privileges) and only refresh_token has RLS. If the Data API (PostgREST) exposes `public`, anyone
-- holding the project's anon key — which is public by design — can read and change every row (user_account password
-- hashes, tokens, payments, customers, ...).
--
-- What it does (idempotent, one transaction):
--   1. revokes every privilege of `anon` and `authenticated` on tables, sequences and functions in `public`;
--   2. stops `postgres` default privileges from granting them on future objects;
--   3. enables RLS (no policies = deny for every non-owner, non-BYPASSRLS role) on every table in `public`
--      as a second line of defence. The API connects as the table owner (`postgres`), which RLS does not restrict.
-- What it does NOT do: touch `service_role` (server-side key, bypasses RLS anyway — keep it secret), drop anything,
-- change data, or alter Supabase-managed schemas (auth, storage, realtime, extensions).
--
-- Preconditions: backup taken; the API connects with the owner role (verified: `postgres` owns all public tables);
-- no frontend uses supabase-js / the Data API directly against these tables (SmartSolar's FE talks only to the .NET API).
-- Rollback: guides/supabase-security.md §6 (re-grant + disable RLS), or restore the backup.
-- =====================================================================================================================

BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        RAISE EXCEPTION 'hardening refused: this does not look like a Supabase database (roles anon/authenticated missing)';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relowner <> current_user::regrole) THEN
        RAISE EXCEPTION 'hardening refused: run it as the owner of every public table';
    END IF;
END $$;

-- 1. Existing objects.
REVOKE ALL ON ALL TABLES    IN SCHEMA public FROM anon, authenticated;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM anon, authenticated;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM anon, authenticated;
-- Note: the 188 functions in public on Supabase Dev are btree_gist extension functions owned by supabase_admin,
-- none SECURITY DEFINER; they stay executable through PostgreSQL's default PUBLIC grant (postgres cannot change that,
-- and it is harmless: pure operator-support functions with no table access). The line above is a no-op there.

-- 2. Future objects created by the migration role (= current_user, i.e. postgres on Supabase; checked above).
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES    FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM anon, authenticated;
-- Note: default privileges owned by `supabase_admin` can only be changed by supabase_admin (Supabase support /
-- dashboard). They apply only to objects supabase_admin creates; SmartSolar's migrations create objects as postgres.

-- 3. RLS as defence in depth (owner and BYPASSRLS roles are unaffected; no policies = deny for client roles).
--    Tables created by LATER migrations do not get RLS automatically (their grants are already safe thanks to step 2):
--    re-run this block after every migration, or add ENABLE ROW LEVEL SECURITY to new migrations.
DO $$
DECLARE t record;
BEGIN
    FOR t IN SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relrowsecurity
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t.relname);
    END LOOP;
END $$;

-- Column-level grants survive REVOKE ... ON TABLE. Supabase Dev has none (pg_attribute.attacl, 2026-10-19); refuse to
-- commit if one exists for a client role, so it is handled explicitly instead of silently staying open.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace,
                      LATERAL aclexplode(a.attacl) x
               WHERE n.nspname = 'public' AND a.attnum > 0 AND a.attacl IS NOT NULL
                 AND x.grantee IN (SELECT oid FROM pg_roles WHERE rolname IN ('anon', 'authenticated'))) THEN
        RAISE EXCEPTION 'hardening refused: column-level grants to anon/authenticated exist; revoke them explicitly first';
    END IF;
END $$;


-- Fail-safe verification
DO $$
DECLARE
    anon_count integer;
    authenticated_count integer;
    rls_ok boolean;
BEGIN
    SELECT
        count(*) FILTER (
            WHERE has_table_privilege(
                'anon', oid, 'SELECT,INSERT,UPDATE,DELETE'
            )
        ),
        count(*) FILTER (
            WHERE has_table_privilege(
                'authenticated', oid, 'SELECT,INSERT,UPDATE,DELETE'
            )
        ),
        coalesce(bool_and(relrowsecurity), false)
    INTO anon_count, authenticated_count, rls_ok
    FROM pg_class
    WHERE relnamespace = 'public'::regnamespace
      AND relkind IN ('r', 'p');

    IF anon_count <> 0
       OR authenticated_count <> 0
       OR NOT rls_ok THEN
        RAISE EXCEPTION 'Security verification failed';
    END IF;
END $$;

COMMIT;

-- Facts behind the design (Supabase Dev, read-only 2026-10-19): no PUBLIC grants on public tables; anon's access comes
-- only from its direct grants; authenticator is NOINHERIT (it only SET ROLEs per request); postgres inherits
-- anon/authenticated/service_role but owns every table, so revoking the client roles does not affect it.
-- Default privileges also exist for objects created by supabase_admin (e.g. tables created through Dashboard tools);
-- postgres cannot change them: create tables only through migrations and re-run supabase-security-audit.sql after any
-- Dashboard schema change.

-- Optional, separate approval: service_role keeps full access (56/56 tables + BYPASSRLS). SmartSolar's code never uses it;
-- revoke it only after confirming no Supabase server feature (Edge Functions, integrations) relies on it:
--   REVOKE ALL ON ALL TABLES IN SCHEMA public FROM service_role;
--   ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM service_role;

-- Optional, separate approval: a least-privilege role for the RAG service instead of the owner role.
--   CREATE ROLE smartsolar_rag LOGIN PASSWORD '<from secret manager>' NOINHERIT;
--   GRANT USAGE ON SCHEMA public, extensions TO smartsolar_rag;
--   GRANT SELECT, INSERT, DELETE ON public.knowledge_chunk TO smartsolar_rag;
--   CREATE POLICY knowledge_chunk_rag ON public.knowledge_chunk TO smartsolar_rag USING (true) WITH CHECK (true);
