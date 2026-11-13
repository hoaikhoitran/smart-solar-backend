-- =====================================================================================
-- Rollback of sql/knowledge_chunk_local_1024.sql — back to the OpenAI 1536-dimension chunk store.
-- STATUS: PENDING_MANUAL_MIGRATION — review only. Run only together with switching the RAG service back
-- to RAG_EMBEDDING_PROVIDER=openai / RAG_EMBEDDING_DIMENSION=1536.
--
-- Chunks written by the local model since the switch are kept as knowledge_chunk_local_1024. Versions
-- ingested only after the switch have no OpenAI chunks: reindex them (reindex-stale with
-- Rag__EmbeddingModel=text-embedding-3-small) after the rollback.
-- =====================================================================================

BEGIN;

ALTER TABLE knowledge_chunk RENAME TO knowledge_chunk_local_1024;
ALTER TABLE knowledge_chunk_local_1024 RENAME CONSTRAINT knowledge_chunk_pkey TO knowledge_chunk_local_1024_pkey;
ALTER TABLE knowledge_chunk_local_1024 RENAME CONSTRAINT ux_knowledge_chunk_version_index TO ux_knowledge_chunk_local_1024_version_index;
ALTER INDEX ix_knowledge_chunk_embedding_hnsw RENAME TO ix_knowledge_chunk_local_1024_embedding_hnsw;

ALTER TABLE knowledge_chunk_openai_1536 RENAME TO knowledge_chunk;
ALTER TABLE knowledge_chunk RENAME CONSTRAINT knowledge_chunk_openai_1536_pkey TO knowledge_chunk_pkey;
ALTER TABLE knowledge_chunk RENAME CONSTRAINT ux_knowledge_chunk_openai_1536_version_index TO ux_knowledge_chunk_version_index;
ALTER INDEX ix_knowledge_chunk_openai_1536_embedding_hnsw RENAME TO ix_knowledge_chunk_embedding_hnsw;

COMMIT;
