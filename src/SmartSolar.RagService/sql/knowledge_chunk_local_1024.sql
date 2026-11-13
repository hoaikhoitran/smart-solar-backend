-- =====================================================================================
-- SmartSolar RAG — switch the vector chunk store to local embeddings (1024 dimensions)
-- STATUS: PENDING_MANUAL_MIGRATION — review only. NOT executed by any code. Apply manually on an
-- approved database, in a maintenance window, AFTER sql/knowledge_chunk.sql was applied.
--
-- Why: the local model qwen3-embedding:0.6b (Ollama) returns 1024-dimensional vectors; the current
-- column is vector(1536) (OpenAI text-embedding-3-small). Vectors are never truncated or padded, and
-- a populated vector(1536) column cannot be converted, so this script KEEPS the current table under a
-- new name (rollback = sql/knowledge_chunk_local_1024_rollback.sql) and creates an empty 1024 table.
-- Chunks are derived data: the originals stay in S3, and every indexed version must be re-ingested
-- with the local model afterwards (POST /api/admin/rag-configuration/reindex-stale with
-- Rag__EmbeddingModel=qwen3-embedding:0.6b on the API). Until a version is re-ingested the chatbot
-- answers "not enough information" for it.
--
-- Pre-checks (read only):
--   SELECT extversion FROM pg_extension WHERE extname = 'vector';            -- HNSW needs >= 0.5.0
--   SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a
--    WHERE a.attrelid = to_regclass('knowledge_chunk') AND a.attname = 'embedding';   -- expect vector(1536)
--   SELECT embedding_model, count(*) FROM knowledge_chunk GROUP BY 1;
--   SELECT to_regclass('knowledge_chunk_openai_1536');                       -- expect NULL
-- =====================================================================================

BEGIN;

-- Keep the OpenAI chunks (rollback path). Index and constraint names are schema-wide, so they move too.
ALTER TABLE knowledge_chunk RENAME TO knowledge_chunk_openai_1536;
ALTER TABLE knowledge_chunk_openai_1536 RENAME CONSTRAINT knowledge_chunk_pkey TO knowledge_chunk_openai_1536_pkey;
ALTER TABLE knowledge_chunk_openai_1536 RENAME CONSTRAINT ux_knowledge_chunk_version_index TO ux_knowledge_chunk_openai_1536_version_index;
ALTER INDEX ix_knowledge_chunk_embedding_hnsw RENAME TO ix_knowledge_chunk_openai_1536_embedding_hnsw;

-- Same table as sql/knowledge_chunk.sql except the vector size.
CREATE TABLE knowledge_chunk (
    chunk_id        varchar(100) PRIMARY KEY,
    version_id      uuid         NOT NULL REFERENCES knowledge_document_version (id) ON DELETE RESTRICT,
    document_id     uuid         NOT NULL REFERENCES knowledge_document (id) ON DELETE RESTRICT,
    chunk_index     integer      NOT NULL CHECK (chunk_index >= 0),
    page_number     integer      NULL CHECK (page_number IS NULL OR page_number >= 1),
    section         varchar(200) NULL,
    content         text         NOT NULL CHECK (length(content) BETWEEN 1 AND 20000),
    content_sha256  char(64)     NOT NULL,
    embedding       vector(1024) NOT NULL,
    embedding_model varchar(100) NOT NULL CHECK (length(embedding_model) > 0),
    created_at      timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ux_knowledge_chunk_version_index UNIQUE (version_id, chunk_index)
);

CREATE INDEX ix_knowledge_chunk_embedding_hnsw
    ON knowledge_chunk USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);

-- If the RAG service uses a dedicated role (see sql/knowledge_chunk.sql):
-- GRANT SELECT, INSERT, DELETE ON knowledge_chunk TO smartsolar_rag;

COMMIT;

-- Post-checks: the column reads vector(1024); knowledge_chunk_openai_1536 keeps every previous row.
-- Drop knowledge_chunk_openai_1536 only after the reindex is complete and rollback is no longer wanted (separate approval).
