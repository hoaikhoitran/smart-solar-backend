-- =====================================================================================
-- SmartSolar RAG — vector chunk store (owned by the Python RAG service, not by EF Core)
-- STATUS: PENDING_MANUAL_MIGRATION — review only. NOT executed by any code or by the
-- implementation work. Apply manually on an approved database after review, AFTER the
-- EF migration that creates knowledge_document / knowledge_document_version.
--
-- Embeddings: OpenAI text-embedding-3-small, 1536 dimensions (RAG_EMBEDDING_MODEL /
-- RAG_EMBEDDING_DIMENSION; the service refuses any other combination). Cosine distance with
-- an HNSW index (pgvector >= 0.5.0; HNSW supports up to 2000 dimensions for `vector`).
-- Changing the model means reindexing every version (chunks are tagged with embedding_model
-- and retrieval only reads the configured model). Changing the dimension also changes this
-- column: a new schema review and migration.
--
-- Pre-checks (read only) on the target database:
--   SELECT version();
--   SELECT name, default_version, installed_version FROM pg_available_extensions WHERE name = 'vector';
--   SELECT extname, extversion, extnamespace::regnamespace FROM pg_extension WHERE extname = 'vector';
--   SHOW search_path;
-- Supabase: enable the extension from the dashboard (Database → Extensions) or with
--   CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA extensions;
-- It then lives in the `extensions` schema: the role applying this script AND the RAG service role
-- must have `extensions` in their search_path (Supabase's default for postgres/authenticated roles),
-- otherwise the unqualified `vector` type and `vector_cosine_ops` operator class below do not resolve.
-- The statement below is then a no-op (IF NOT EXISTS matches the extension name in any schema).
-- =====================================================================================

CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS knowledge_chunk (
    chunk_id        varchar(100) PRIMARY KEY,
    version_id      uuid         NOT NULL REFERENCES knowledge_document_version (id) ON DELETE RESTRICT,
    document_id     uuid         NOT NULL REFERENCES knowledge_document (id) ON DELETE RESTRICT,
    chunk_index     integer      NOT NULL CHECK (chunk_index >= 0),
    page_number     integer      NULL CHECK (page_number IS NULL OR page_number >= 1),
    section         varchar(200) NULL,
    content         text         NOT NULL CHECK (length(content) BETWEEN 1 AND 20000),
    content_sha256  char(64)     NOT NULL,
    embedding       vector(1536) NOT NULL,
    embedding_model varchar(100) NOT NULL CHECK (length(embedding_model) > 0),
    created_at      timestamptz  NOT NULL DEFAULT now(),
    -- Also serves lookups and deletes by version_id (leading column): no separate version index.
    CONSTRAINT ux_knowledge_chunk_version_index UNIQUE (version_id, chunk_index)
);

-- Cosine distance (`<=>`), matching the service's retrieval query.
CREATE INDEX IF NOT EXISTS ix_knowledge_chunk_embedding_hnsw
    ON knowledge_chunk USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);

-- Least privilege: the RAG service role needs only these rights, on this table only.
-- CREATE ROLE smartsolar_rag LOGIN PASSWORD '<set-out-of-band>';
-- GRANT SELECT, INSERT, DELETE ON knowledge_chunk TO smartsolar_rag;
