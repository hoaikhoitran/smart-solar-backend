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

RAG service:

| Variable | Production value |
|---|---|
| `RAG_ENV` | `production` (enables strict validation) |
| `RAG_SHARED_SECRET` | same secret as the API |
| `RAG_VECTOR_STORE` / `RAG_DATABASE_URL` | `pgvector` / `postgresql://smartsolar_rag:…@host:5432/db?sslmode=require` |
| `RAG_STORAGE` / `RAG_S3_REGION` / `RAG_S3_ALLOWED_BUCKETS` | `s3` / region / bucket list |
| `RAG_EMBEDDING_PROVIDER` / `_MODEL` / `_DIMENSION` / `_BASE_URL` / `_API_KEY` | `openai` / `text-embedding-3-small` / `1536` / `https://api.openai.com/v1` / key (secret) |
| `RAG_LLM_PROVIDER` / `_MODEL` / `_BASE_URL` / `_API_KEY` | `openai` / model / endpoint / key |
| `RAG_WORKERS` | 2–4 (pgvector only) |
| Optional | `RAG_MAX_CONCURRENT_GENERATIONS`, `RAG_MAX_CONCURRENT_INGESTIONS`, `RAG_MIN_SCORE`, `RAG_CHUNK_CHARACTERS`, `RAG_MAX_DOCUMENT_BYTES`, `RAG_PROVIDER_TIMEOUT_SECONDS` |

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

## 5. Local verification

```bash
cd src/SmartSolar.RagService
python -m venv .venv && . .venv/bin/activate && pip install -r requirements-dev.txt
pytest -q          # offline; pgvector test runs only with RAG_TEST_DATABASE_URL (local disposable DB)
ruff check .
docker build -t smartsolar-rag-service:local .
```
