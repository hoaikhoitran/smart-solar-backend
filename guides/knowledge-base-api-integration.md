# Knowledge base (chatbot) — API integration (frontend)

The knowledge base is independent of the Installation Flow. Envelope, JWT and error mapping are the
same as in [installation-workflow-api-integration.md](installation-workflow-api-integration.md).
While `Features__KnowledgeBaseEnabled=false` every endpoint below answers **503 `FEATURE_PENDING_MIGRATION`**.
Knowledge files are registered with `POST /api/files` (purpose `KNOWLEDGE_DOCUMENT`); `/api/files` is
available when either feature is enabled. Architecture: [rag-architecture.md](rag-architecture.md).

## Endpoints

| Role | Method | Path | Body |
|---|---|---|---|
| any signed-in user | POST | `/api/chat/messages` | `{ message (≤ 8000 chars), sessionId?, clientMessageId? (≤ 64, makes retries idempotent) }` → `{ sessionId, question, answer: { content, status, citations: [{ rank, documentId, documentTitle, versionId, versionNumber, page, section, score, snippet }] }, grounded, notice }` |
| any signed-in user | GET | `/api/chat/sessions`, `/api/chat/sessions/{id}` | own sessions only |
| Admin | GET/POST | `/api/admin/knowledge-documents` | register `{ fileId, title, category, tags }` |
| Admin | GET/PUT | `/api/admin/knowledge-documents/{id}` | metadata `{ revision, title, category, tags }` |
| Admin | POST | `…/{id}/versions` `{ fileId }` · `…/publish` `{ versionId, revision }` · `…/unpublish` `{ revision }` · `…/reindex` `{ versionId }` · `…/jobs/{jobId}/retry` · `…/archive` `{ revision }` | |

Categories: PRODUCT, WARRANTY, POLICY, INSTALLATION_GUIDE, FAQ, MAINTENANCE, OTHER.
`grounded=false` means the assistant had no supporting published document; show `answer.content` as is.
Always display `notice` and the citations.

## Rate limits and quotas (per user; ★ cluster-wide in Redis; quotas in PostgreSQL — see [rate-limiting.md](rate-limiting.md))

| Policy | Default | Applies to |
|---|---|---|
| rag-chat ★ | 10 per min | chat (plus DB daily quotas: 100 requests and 200 000 tokens per user, 5 000 000 tokens global, 2 concurrent generations per user) |
| rag-history | 60 per min | chat history |
| rag-admin-read / register ★ / publish | 60 / 10 / 10 per min | knowledge admin |
| rag-admin-reindex ★ | 3 per 10 min | reindex / retry |
