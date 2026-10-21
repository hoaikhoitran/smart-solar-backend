# Docker hand-off for frontend developers

Two compose files live at the repository root:

| File | Use |
|---|---|
| `docker-compose.pull.yml` | Pull pre-built images (no .NET/Python toolchain needed). **Recommended for FE.** |
| `docker-compose.yml` | Build the API (and RAG service) from source. |

Services: `rabbitmq`, `redis`, `api` (http://localhost:8080, Swagger at `/swagger` in Development)
and, with the `rag` profile, `rag-service` (internal only — no host port).

## 1. First run

```bash
cp .env.example .env        # then fill the values you were given (never commit .env)
docker compose -f docker-compose.pull.yml pull
docker compose -f docker-compose.pull.yml up -d                  # API without the chatbot
docker compose -f docker-compose.pull.yml --profile rag up -d    # API + chatbot service
docker compose -f docker-compose.pull.yml ps
```

The database is the shared development PostgreSQL configured in `.env`
(`ConnectionStrings__DefaultConnection`); compose does not start a database.

## 2. Installation Flow / chatbot availability

* `Features__InstallationWorkflowEnabled=false` / `Features__KnowledgeBaseEnabled=false` (default): Installation Flow and chatbot endpoints answer
  `503 FEATURE_PENDING_MIGRATION`. This is expected until the backend owner applies the Installation Flow
  database migration. Flow 1 works normally.
* Once enabled, the chatbot additionally needs `Rag__Enabled=true`, the `rag` profile and the same
  secret in `Rag__ServiceSharedSecret` and `RAG_SHARED_SECRET`. `.env.example` is set up for the real
  embedding model (OpenAI `text-embedding-3-small`), which needs `RAG_EMBEDDING_API_KEY`. Without a key,
  set `RAG_EMBEDDING_PROVIDER=hashing` and leave `RAG_EMBEDDING_MODEL` / `RAG_EMBEDDING_BASE_URL` empty
  (with `RAG_LLM_PROVIDER=extractive`): the chatbot then runs fully offline with low-quality, quote-style
  answers — good enough to build the UI, not representative of production.
* Payments need PayOS sandbox credentials (`Providers__PayOS__*`). Without them,
  `POST …/payments` returns `503 PAYMENT_PROVIDER_NOT_CONFIGURED`.
* File uploads need an S3 bucket the FE can upload to (`Storage__*`). With `Storage__S3__Enabled=false`
  registered files stay `PENDING_VERIFICATION` and cannot be used by Installation Flow actions.

## 3. Choosing image versions

`docker-compose.pull.yml` reads `RAG_IMAGE` and `RAG_IMAGE_TAG` (defaults
`hoaikhoitran/smartsolar-rag-service:latest`). Pin a tag for reproducible environments. The API image
is `hoaikhoitran/smartsolar-api:latest`.

## 4. Troubleshooting

| Symptom | Check |
|---|---|
| `rag-service` restarts | `docker compose logs rag-service` → "Invalid RAG service configuration" names the missing key (values are never printed). Secret must be ≥ 32 characters. |
| Chat answers 503 `RAG_SERVICE_UNAVAILABLE` | `rag` profile not started, secrets differ, or `Rag__Enabled=false`. |
| 429 | Per-user rate limits (see `guides/installation-workflow-api-integration.md`); chat also has daily quotas. |
| 401 on everything | Expired access token; refresh via `/api/auth/refresh`. |

API reference for the screens: [installation-workflow-api-integration.md](installation-workflow-api-integration.md), [knowledge-base-api-integration.md](knowledge-base-api-integration.md); business rules:
[installation-workflow-business-flow.md](installation-workflow-business-flow.md).
