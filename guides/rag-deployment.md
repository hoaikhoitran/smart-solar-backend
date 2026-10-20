# RAG service — deployment

Image: `src/SmartSolar.RagService/Dockerfile` (`python:3.12-slim`, non-root uid 10001, healthcheck on
`/health/live`, `uvicorn app.main:app_factory --factory`). Nothing has been built for or pushed to a
registry by this change; publish `${RAG_IMAGE}:${RAG_IMAGE_TAG}` through your normal release process.

## 1. Prerequisites

1. Apply the PENDING_MANUAL_MIGRATION (`guides/sql/installation-workflow-and-knowledge-base-schema-review.sql`, then
   `src/SmartSolar.RagService/sql/knowledge_chunk.sql`: `vector(1536)` for `text-embedding-3-small`;
   on Supabase check the extension schema and `search_path` as described at the top of that file).
2. A PostgreSQL role for the service with only `SELECT, INSERT, DELETE` on `knowledge_chunk`.
3. Read-only S3 access to the knowledge bucket/prefix (IAM role preferred).
4. One shared secret (`openssl rand -hex 32`) configured on both sides.

## 2. Configuration

API (`.env`): `Rag__Enabled=true`, `Rag__ServiceBaseUrl=http://rag-service:8000`,
`Rag__ServiceSharedSecret=<secret>`, optional quota keys under `Rag__*` (see `appsettings.json`).
Admin runtime configuration (no secrets): `Rag__GenerationModels__N__*` (allowlist), `Rag__PromptVersions__N`,
`Rag__ConfigurationLimits__*`, `Rag__ConfigurationCacheSeconds` (≤ 15) and the bootstrap values used while no admin
configuration is active (`Rag__GenerationModel`, `Rag__Temperature`, `Rag__ChunkCharacters`, `Rag__ChunkOverlap`,
`Rag__MinScore`, `Rag__PromptVersion`, plus `Rag__TopK`, `Rag__MaxContextTokens`, `Rag__MaxOutputTokens`, `Rag__MaxHistoryMessages`).
Keep the bootstrap chunk values equal to `RAG_CHUNK_CHARACTERS`/`RAG_CHUNK_OVERLAP`.

RAG service:

| Variable | Production value |
|---|---|
| `RAG_ENV` | `production` (enables strict validation) |
| `RAG_SHARED_SECRET` | same secret as the API |
| `RAG_VECTOR_STORE` / `RAG_DATABASE_URL` | `pgvector` / `SET_VIA_ENVIRONMENT` |
| `RAG_STORAGE` / `RAG_S3_REGION` / `RAG_S3_ALLOWED_BUCKETS` | `s3` / region / bucket list |
| `RAG_EMBEDDING_PROVIDER` / `_MODEL` / `_DIMENSION` / `_BASE_URL` / `_API_KEY` | `openai` / `text-embedding-3-small` / `1536` / `https://api.openai.com/v1` / key (secret) |
| `RAG_LLM_PROVIDER` / `_MODEL` / `_BASE_URL` / `_API_KEY` | `openai` / model / endpoint / key |
| `RAG_WORKERS` | 2–4 (pgvector only) |
| `RAG_LLM_ALLOWED_MODELS` | optional comma list; when set, a request naming another model is rejected (422). When set it **must contain every model id of `Rag:GenerationModels`** (and `RAG_LLM_MODEL`), otherwise an admin can activate a model the service refuses and every chat request fails |
| `RAG_PROMPT_VERSION` | default prompt template when a request names none (`v1`) |
| Optional | `RAG_MAX_CONCURRENT_GENERATIONS`, `RAG_MAX_CONCURRENT_INGESTIONS`, `RAG_MIN_SCORE`, `RAG_CHUNK_CHARACTERS`, `RAG_CHUNK_OVERLAP`, `RAG_MAX_DOCUMENT_BYTES`, `RAG_PROVIDER_TIMEOUT_SECONDS`, ceilings `RAG_MAX_TOP_K` / `RAG_MAX_CONTEXT_TOKENS` / `RAG_MAX_OUTPUT_TOKENS` (clamp per-request values) |

Per-request fields (`configVersion`, chunk settings, `generationModel`, `temperature`, `tokenParameter`, `minScore`,
`promptVersion`) are optional: absent fields use these environment defaults, so an older API keeps working. Unknown fields
are rejected (422). Deploy the RAG service **before** (or together with) an API that sends the new fields: a newer API
against this service gets 422 for any field the service does not know yet. The reverse mix also runs but is misleading: a
service from before the admin RAG configuration (plain pydantic models, unknown fields silently ignored) accepts the new
fields, ignores them (environment defaults apply) and echoes no `configVersion`/chunk settings, so the API records nothing as
indexed and every version it indexes shows as stale until the service is upgraded and `reindex-stale` runs. While no admin RAG configuration is
ACTIVE (bootstrap) the API omits `generationModel`, `temperature` and `tokenParameter`, so `RAG_LLM_MODEL` and
`RAG_LLM_TEMPERATURE` decide the generation; `Rag__GenerationModel`/`Rag__Temperature` only take effect once an admin activates
a configuration (they seed the first draft).

## 3. Network

* Never publish port 8000 publicly. In compose it is `expose`d only; in other platforms keep it on a
  private network/security group reachable only from the API.
* The service needs egress to S3, PostgreSQL and the LLM/embedding provider.
* Health: `GET /health/live` (process), `GET /health/ready` (vector store reachable, `knowledge_chunk` exists and its
  `embedding` column is `vector(1536)`).

## 4. Rollout

1. Deploy the service with `Rag__Enabled=false` on the API; check `/health/ready`.
2. Enable `Rag__Enabled=true` (and `Features__KnowledgeBaseEnabled=true` once the migration is applied).
3. Admin registers and publishes documents; watch ingestion jobs in `GET /api/admin/knowledge-documents/{id}`.
4. Rotate the shared secret by updating both sides together (requests fail with 401 in between).
5. After activating a configuration with new chunk settings, call `POST /api/admin/rag-configuration/reindex-stale`
   (published versions keep serving their old chunks until each reindex succeeds).

## 5. Local verification

```bash
cd src/SmartSolar.RagService
python -m venv .venv && . .venv/bin/activate && pip install -r requirements-dev.txt
pytest -q          # offline; pgvector test runs only with RAG_TEST_DATABASE_URL (local disposable DB)
ruff check .
docker build -t smartsolar-rag-service:local .
```
