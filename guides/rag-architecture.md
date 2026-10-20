# SmartSolar knowledge chatbot (RAG) — architecture

## 1. Responsibilities

| Concern | Owner |
|---|---|
| Authentication, roles, which documents are visible (published version of published documents only) | **.NET API** |
| Document metadata, versions, publish/unpublish/archive, audit | .NET API (`knowledge_document`, `knowledge_document_version`) |
| Durable ingestion queue, retries with backoff, leases | .NET API (`knowledge_ingestion_job`, `KnowledgeIngestionWorker`) |
| Chat sessions, messages, citations, idempotency | .NET API (`chat_session`, `chat_message`, `chat_citation`) |
| Cost control: per-user/global daily token + request quotas, per-user concurrency | .NET API (`rag_usage_daily`, atomic conditional updates) + HTTP rate limit |
| Download from S3, integrity check, text extraction, chunking, embeddings | **FastAPI RAG service** (`src/SmartSolar.RagService`) |
| Vector storage and similarity search | RAG service (`knowledge_chunk`, pgvector) |
| Prompting, generation, citation validation | RAG service |

The model only produces text. It has no tools and cannot modify contracts, payments, schedules or any data.

## 2. Ingestion

```
Admin: upload PDF/DOCX/TXT/MD to S3 (FE) → POST /api/files (verified, SHA-256)
     → POST /api/admin/knowledge-documents  ─┐ (document + version 1 + INGEST job, one transaction)
KnowledgeIngestionWorker (every 15 s, any instance)
     → lease job (conditional UPDATE … WHERE status IN (PENDING, RETRY_WAITING) OR lease expired)
     → POST /internal/v1/ingest  (no DB transaction open during the call)
RAG service: GET object → SHA-256 must equal the verified checksum → extract → chunk → embed
     → DELETE + INSERT chunks of that version in ONE transaction (retries never duplicate)
     → { status: READY | OCR_REQUIRED | UNSUPPORTED_CONTENT | INTEGRITY_FAILED | FAILED }
API: READY → version READY_FOR_REVIEW; FAILED/timeout/unexpected error → RETRY_WAITING (30 s·2^(n-1), ≤ 1 h, max 5)
     → FAILED (admin retry). Attempts are counted at lease time, so a crashed worker still uses one; an
     expired lease is re-leased by any worker, and the outcome is fenced by the lease (a stale worker cannot
     overwrite the worker that took over). Service shutdown releases the lease without using the attempt.
Admin reviews → publish (old published version → SUPERSEDED atomically)
```

* Scanned PDFs (≈ no text layer) → `OCR_REQUIRED`; OCR is not performed (known limitation).
* Limits: 50 MB object, 1 000 pages, 3 M characters, 5 000 chunks, DOCX uncompressed ≤ 200 MB (zip-bomb guard), encrypted PDFs rejected.
* Chunks: size/overlap from the job's configuration snapshot (default 2 400 / 300, admin range 400–8 000, overlap ≤ half),
  split on paragraphs → sentences → hard cut; page and heading kept. The service echoes `configVersion`, `chunkCharacters`,
  `chunkOverlap`; on READY the API stores them as the version's `indexed_*` columns.
* A version holding live chunks (READY_FOR_REVIEW, PUBLISHED or WITHDRAWN) is never demoted by a reindex (manual, reindex-stale
  or retry): it is not set to PROCESSING/REGISTERED while the job runs, and an OCR_REQUIRED / UNSUPPORTED_CONTENT /
  INTEGRITY_FAILED / FAILED outcome (or an unexpected error / exhausted attempts) keeps its status, metadata and old chunks (the
  service only replaces chunks on success); the outcome goes to `last_ingest_status/error`, and a READY outcome refreshes the
  chunk metadata and `indexed_*`. REGISTERED / FAILED / OCR_REQUIRED / UNSUPPORTED_CONTENT versions keep the classic lifecycle
  (manual reindex resets them to REGISTERED).
* Unpublish is immediate (retrieval filter); archive also queues `DELETE_VECTORS`.

## 3. Answering

```
POST /api/chat/messages
 1. validate (≤ 8 000 chars), session ownership, clientMessageId replay → stored answer (no new cost)
 2. reserve quota in one transaction (global and user ledger rows locked, global first): expired reservations of the user are
    charged at their estimate, then the concurrency slots and daily request/token allowances are checked and a leased
    reservation (request timeout + 60 s) is created. Completion replaces it by the real usage, idempotently.
 3. allowed = published versions (API decides); none → "insufficient" answer without calling the service
 4. POST /internal/v1/answer { question, last N turns, allowedVersions, topK, maxContextTokens, maxOutputTokens, configVersion,
                               generationModel, temperature (null = omit), tokenParameter, minScore, promptVersion }
    (values of the effective admin configuration, §8; the service's RAG_MAX_* ceilings still clamp them)
    service: embed → vector search filtered by version_id = ANY(allowed) (re-filtered in code) → min score
             → numbered <source> blocks within the token budget → LLM → keep only [n] citations that exist
             → grounded only if not INSUFFICIENT_CONTEXT and ≥ 1 valid citation
 5. API re-checks the allowed versions after generation (a document unpublished meanwhile is never cited)
 6. persist question + answer + citations in one transaction; reconcile usage (finally) with real token counts
```

Not grounded → the stored answer is the fixed Vietnamese "insufficient information" text, never the model's guess.

## 4. Prompt-injection hardening

* Retrieved text and history are **data**: wrapped in `<source id="n">`, any `<source>`, `<system>`,
  `<assistant>`, `<user>`, `<instructions>` markup inside them is neutralised so it cannot close or forge blocks.
* The system prompt forbids following instructions found in sources/history, inventing prices,
  specifications, warranty or legal terms, and revealing the system prompt.
* Output is accepted only with valid citations; citations are mapped back to API-approved versions.
* Only Admin-uploaded, Admin-published documents are retrievable; customers cannot add knowledge.

## 5. Service-to-service security

* The RAG service has **no public port** (compose `expose` only) and no OpenAPI/docs endpoints.
* Every `/internal/*` call is signed: `X-SmartSolar-Timestamp` (unix s) and
  `X-SmartSolar-Signature = hex(HMAC-SHA256(secret, timestamp + "." + rawBody))`; ±300 s window,
  constant-time compare, one-time use (per-process replay cache), body ≤ 256 KB, strict schemas.
  Source IP is not trusted.
* Secrets (`RAG_SHARED_SECRET`/`Rag__ServiceSharedSecret`, LLM/embedding keys, AWS) come only from the
  environment; the service refuses to start with a secret shorter than 32 characters, and in
  `RAG_ENV=production` refuses development stand-ins (memory store, local storage, hashing/extractive).
* Question text is not logged; logs carry ids, statuses, counts and timings only.

## 6. Resource protection

See [rate-limiting.md](rate-limiting.md). In short: `rag-chat`, document registration and reindexing are
**cluster-wide** limits in Redis (falling back to per-instance limits if Redis fails); daily tokens,
requests and concurrent generations are enforced in PostgreSQL; the RAG service caps its own concurrency
and stops ingestion at its deadline. Timeouts: chat 60 s, ingest 300 s (service deadline 270 s), provider 45 s.

## 7. Providers

| Setting | Development | Production |
|---|---|---|
| `RAG_VECTOR_STORE` | `memory` | `pgvector` |
| `RAG_STORAGE` | `local` / `s3` | `s3` (read-only IAM on the knowledge prefix) |
| `RAG_EMBEDDING_PROVIDER` | `hashing` (offline, deterministic, low quality; only with `RAG_ENV=development`/`test`) | `openai` |
| `RAG_EMBEDDING_MODEL` / `_DIMENSION` | `hashing-dev` / 1536 (any size with the memory store) | **`text-embedding-3-small` / 1536** (the only accepted pair) |
| `RAG_LLM_PROVIDER` | `extractive` (quotes the best source, offline) | `openai` (any OpenAI-compatible `/chat/completions`) |

### Embeddings

* Model: OpenAI `text-embedding-3-small`, requested with `dimensions: 1536` and `encoding_format: float`;
  stored in `knowledge_chunk.embedding vector(1536)` with an HNSW `vector_cosine_ops` index; retrieval
  orders by cosine distance (`<=>`) and reports `1 − distance` as the score.
* The service refuses to start with any other model, a dimension other than 1536, a pgvector store whose
  dimension differs from the schema, or the offline provider outside development/test. An unset
  `RAG_EMBEDDING_PROVIDER` means `openai`: without an API key the service does not start (no silent fallback).
* Every provider response is checked before storage: vector count, `model` field, exactly 1536 finite
  numbers. Wrong dimensions are also refused by the store (before SQL) and by the column itself.
  `GET /health/ready` reports not ready if the `knowledge_chunk.embedding` column is not `vector(1536)`.
* Timeouts (`RAG_PROVIDER_TIMEOUT_SECONDS`, 45 s), HTTP errors and contract violations fail the ingestion
  attempt (the API retries with backoff); the service never switches to another model or provider.
* Documents and queries use the same embedder; chunks are tagged with `embedding_model` and retrieval only
  reads the configured model, so vectors from different models never mix.
* **Changing the model later:** reindex every version (`POST /api/admin/knowledge-documents/{id}/reindex`);
  until a version is reindexed it is not retrievable with the new model. A different **dimension** is a
  schema change (new column/table, schema review, migration) plus a full reindex.
* Live check (opt-in, costs a few tokens): `RAG_LIVE_EMBEDDING_SMOKE=1 RAG_EMBEDDING_API_KEY=… pytest tests/test_embeddings.py -k live`.

## 8. Admin runtime configuration

Admins tune the chatbot without a redeploy (`/api/admin/rag-configuration`, see
[knowledge-base-api-integration.md](knowledge-base-api-integration.md)). Table `rag_configuration` holds versions
DRAFT → ACTIVE → SUPERSEDED (partial unique indexes: one ACTIVE, one DRAFT; check constraints on every range):
generation model, temperature (null for models without it), max output tokens, chunk characters/overlap, top-k, min score,
max context tokens, max history messages, prompt version, notes, revision. No secrets and no URLs.

* **Registry** — `Rag:GenerationModels` (id, displayName, supportsTemperature, tokenParameter `max_tokens` |
  `max_completion_tokens`, maxOutputTokensLimit; defaults gpt-4.1-mini, gpt-4.1, gpt-4o-mini) and `Rag:PromptVersions`
  (`["v1"]`; the templates live in the RAG service, `prompting.PROMPT_TEMPLATES`). Invalid entries fail startup.
* **Bounds** — `Rag:ConfigurationLimits` can only narrow the hard bounds: chunk 400–8 000, overlap 0–chunk/2 (< chunk),
  top-k 1–10, min score 0–1, context 500–6 000, output 64–min(model limit, 1 500), temperature 0–1, history 0–20.
* **Effective configuration** — the ACTIVE row, or version 0 = bootstrap values from `Rag:*` (`GenerationModel`, `Temperature`,
  `TopK`, `MaxContextTokens`, `MaxOutputTokens`, `ChunkCharacters`, `ChunkOverlap`, `MinScore`, `MaxHistoryMessages`,
  `PromptVersion`). Read per request through a process cache (`Rag:ConfigurationCacheSeconds`, ≤ 15 s), invalidated locally on
  activation/rollback; other instances follow within the TTL. Chat uses it for retrieval/generation and the quota estimate.
  **Bootstrap generation:** while no row is ACTIVE the answer request does not send `generationModel`, `temperature` or
  `tokenParameter` at all, so the RAG service's environment defaults apply (`RAG_LLM_MODEL`, `RAG_LLM_TEMPERATURE`,
  `max_tokens`); `Rag:GenerationModel`/`Rag:Temperature` are then only the values shown to admins as the starting point of a
  draft. All other bootstrap values (top-k, context, output budget, min score, history, prompt version, chunking) are sent.
  Only an ACTIVE admin configuration overrides the model, temperature (explicit JSON `null` = omit it for models without
  temperature support) and token parameter. When `RAG_LLM_ALLOWED_MODELS` is set it must contain **every** model of
  `Rag:GenerationModels`, otherwise activating a configuration naming a missing model makes every chat request fail (422).
* **Activation** — one transaction: the old ACTIVE row becomes SUPERSEDED (own statement), then the draft becomes ACTIVE;
  revisions (concurrency tokens) reject stale or concurrent activations. Rollback copies an older version into a new
  ACTIVE version (history is never rewritten).
* **Ingestion snapshot** — an INGEST job stores `config_version`, `chunk_characters`, `chunk_overlap` when queued and always runs
  with them, even after another activation. `reindex-stale` queues INGEST jobs (with the active snapshot) for READY_FOR_REVIEW /
  PUBLISHED versions of non-archived documents whose `indexed_chunk_*` differ (or were never echoed), skipping versions with an
  open job. Published versions stay live meanwhile.
* **Not configurable** — the embedding model/dimension (environment, `text-embedding-3-small`/1536); retrieval keeps filtering
  on `embedding_model`. HMAC signing, quotas, citation validation and grounding are unchanged.
