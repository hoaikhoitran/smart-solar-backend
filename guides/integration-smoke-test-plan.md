# Real-service smoke test plan — S3, OpenAI/RAG, PayOS

Status: **plan only — none of these tests has been run against a real service.** Everything below is covered today by
fakes/mocks and local tests only. Each block needs its own approval because it costs money, writes to an external
system, or both. Run them on Supabase Dev **after** the deployment in [deployment-readiness.md](deployment-readiness.md),
with feature flags enabled only for the duration of the test, and with dedicated test accounts (never real customers).

Common preconditions: API of this branch running against Dev; test users `smoke-customer`, `smoke-customer-2`,
`smoke-technician`, `smoke-technician-2`, `smoke-manager`, `smoke-admin` (created through the admin API, not SQL);
one service area covering the test site; secrets supplied through the environment, never pasted into tickets or logs.
Record for every step: request, HTTP status, `error.code`, trace id, timestamp. Never record presigned URLs, tokens or keys.

## 1. AWS S3 — installation evidence

Preconditions: private bucket, Block Public Access on, versioning on; FE-upload principal allowed to `PutObject` under
`installations/<userId>/`; backend credential (`Storage__S3__*`) with only `s3:GetObject` + `s3:GetObjectVersion`
(+ `s3:ListBucket` on the prefix if a missing object must be reported as 410 instead of 503);
`Storage__S3__Enabled=true`, `Storage__S3__PresignedUrlTtlSeconds=60` for the expiry test.
Cost/impact: a few small objects in the Dev bucket (deleted at the end).

| # | Step | Expected |
|---|---|---|
| S3-1 | Technician uploads a small JPEG to `installations/<technicianUserId>/smoke-1.jpg` (FE flow or `aws s3 cp` with the FE principal) | object exists, versioned |
| S3-2 | `POST /api/files` (purpose `INSTALLATION_EVIDENCE`, objectKey, fileName, contentType, sizeBytes, checksumSha256) as the technician | 201, status `VERIFIED`; verified size/type/SHA-256 equal the object (`aws s3api head-object` metadata) |
| S3-3 | Wrong key prefix / other user's prefix / wrong content type | 400 `VALIDATION_FAILED` / file `REJECTED` |
| S3-4 | Attach as evidence on an IN_PROGRESS test project (`POST …/installation-projects/{id}/evidence`) | 200; the same file twice → 409 `FILE_ALREADY_LINKED` |
| S3-5 | Customer `GET …/customers/me/installation-projects/{id}/evidence` | lists the photo (no bucket/key in the body) |
| S3-6 | Customer `GET …/evidence/{evidenceId}/url?disposition=inline`, open the URL in a browser | 200 JSON with `url`, `expiresAt`; image renders inline; response header `Cache-Control: no-store` |
| S3-7 | Same with `disposition=attachment` | browser downloads with the sanitized file name |
| S3-8 | Wait > 60 s, reuse the URL | S3 answers 403 `AccessDenied`/`Request has expired`; a new URL works |
| S3-9 | `smoke-customer-2` asks for the URL of S3-6 | 404 `INSTALLATION_PROJECT_NOT_FOUND`; uninvolved `smoke-technician-2` → 404; customer route with a technician token → 403 |
| S3-10 | Tamper with the URL (change key or `versionId`) | S3 403 (signature mismatch) |
| S3-11 | Delete the object version, request a URL again | 410 `INSTALLATION_EVIDENCE_OBJECT_MISSING` (503 if the credential lacks `ListBucket`) |
| S3-12 | API logs of the whole run | contain evidence/project ids only — no URL, key, credential |
| Cleanup | delete the smoke objects and the test project data per the test-data procedure | — |

## 2. OpenAI / RAG

Preconditions: RAG service of this branch deployed (before or with the API); `RAG_ENV`, `RAG_VECTOR_STORE=pgvector`,
`RAG_DATABASE_URL`, `RAG_STORAGE=s3` + read-only S3 credential, `RAG_EMBEDDING_PROVIDER=openai`,
`RAG_EMBEDDING_MODEL=text-embedding-3-small`, `RAG_EMBEDDING_DIMENSION=1536`, `RAG_EMBEDDING_API_KEY`,
`RAG_LLM_PROVIDER=openai` with `RAG_LLM_BASE_URL` + `RAG_LLM_API_KEY`; if `RAG_LLM_ALLOWED_MODELS` is set it must contain
every model of `Rag:GenerationModels` (`gpt-4.1-mini`, `gpt-4.1`, `gpt-4o-mini`); same shared secret on both sides;
`Rag__Enabled=true`, `Features__KnowledgeBaseEnabled=true`.
Cost/impact: one small document (≈ 1–3 pages) → a few thousand embedding tokens; ≤ 10 chat answers with
`maxOutputTokens` ≤ 700 → well under 50k tokens in total. Set a hard spending limit on the OpenAI project first.

| # | Step | Expected |
|---|---|---|
| AI-0 | `GET /v1/models` with the LLM key (no token cost) | every allowlisted model is available to the account; otherwise remove it from `Rag:GenerationModels` before AI-4 |
| AI-1 | RAG `GET /health/ready` (inside the network) | 200 ready (embedding column is `vector(1536)`) |
| AI-2 | Admin registers a small PDF/TXT knowledge document (verified file) | ingestion job `COMPLETED`; version `READY_FOR_REVIEW`; `indexed_config_version` set; chunks have `embedding_model = text-embedding-3-small` |
| AI-3 | Admin `PUT /api/admin/rag-configuration/draft` with an invalid model / chunk 300 / overlap ≥ chunk | 400 `RAG_CONFIG_VALIDATION_FAILED` with field details |
| AI-4 | Draft with `gpt-4.1-mini`, chunk 1200/150, activate | 200; `versionsNeedingReindex` > 0 |
| AI-5 | `POST /api/admin/rag-configuration/reindex-stale`; while it runs, publish/chat still uses the old chunks | 202; after completion the version's indexed chunk settings are 1200/150; chunk count changed; no duplicate chunks |
| AI-6 | Publish, then customer `POST /api/chat/messages` with an in-document question | grounded answer with valid citations; `chat_message.model` = active model |
| AI-7 | Out-of-document question | fixed "insufficient information" answer, `grounded=false` |
| AI-8 | Activate a config with `gpt-4.1` (or a reasoning-style model if added with `max_completion_tokens`) and ask again | answer generated; FastAPI sent the correct token parameter (service log shows parameter names, not content) |
| AI-9 | Invalid LLM key on the RAG service (temporary) | chat → 503 `RAG_SERVICE_UNAVAILABLE`; quota reservation released |
| AI-10 | Rollback to the previous configuration | new active version copied from the target |
| Cleanup | archive the smoke document (queues `DELETE_VECTORS`), restore the intended configuration | — |

## 3. PayOS

Preconditions: PayOS test merchant credentials (`Providers__PayOS__ClientId/ApiKey/ChecksumKey`, `Enabled=true`),
`Payments__ReturnUrl/CancelUrl`, a public HTTPS webhook URL routed to `POST /api/payments/payos/webhook`, a test contract
in status `SIGNED` with a minimal amount. **Whether PayOS offers a sandbox/test channel for this merchant is NOT VERIFIED** —
the code uses `https://api-merchant.payos.vn`. Confirm with PayOS first; without a test channel, only steps P-1, P-2, P-5,
P-7 are allowed (they create and cancel a link without paying). No real payment is ever made.

| # | Step | Expected |
|---|---|---|
| P-1 | Customer `POST /api/customers/me/contracts/{id}/payments` | 200/201 with `checkoutUrl`, `qrCode`, `orderCode`, `expiresAt`; a second call returns the same attempt (idempotent) |
| P-2 | PayOS dashboard / `GET v2/payment-requests/{orderCode}` | amount and description match; status PENDING |
| P-3 | (test channel only) complete the test payment | PayOS sends the webhook; signature valid; attempt PAID; contract `PAID`; `contract_event` "full payment verified" |
| P-4 | (test channel only) replay the same webhook body | 200, no second state change (duplicate ignored) |
| P-5 | POST a webhook with a modified amount or signature (from a test client) | 400 `PAYMENT_WEBHOOK_INVALID_SIGNATURE`; nothing changes (no attempt/contract update) |
| P-6 | (test channel only) failed/cancelled payment | attempt `CANCELLED`/`FAILED`; contract stays `SIGNED`; a new attempt can be created |
| P-7 | Customer `POST …/payments/cancel`, then reconciliation (`GET …/payments`) | PayOS link cancelled; attempt `CANCELLED`; contract unchanged |
| P-8 | Reconciliation worker after a webhook outage (block the webhook route, pay in the test channel, re-open) | worker reconciles to PAID exactly once |

## 4. Exit criteria

All rows of a block PASS, no 5xx except where expected, no secret or URL in logs, cleanup done, results attached to the
release ticket. A failed block keeps its feature flag (`InstallationWorkflow` / `KnowledgeBase`) off in Dev.
