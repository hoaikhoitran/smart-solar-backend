# RAG with local models (Ollama) — configuration, migration, measurements, rollout

Audience: the backend team, DevOps for the VPS (Ubuntu 24.04, 3 vCPU Xeon Platinum 8260, 5.8 GiB RAM, no swap, no GPU
confirmed) and the reviewer of this change.

Status: implemented and tested in the local working tree only. Nothing was committed, pushed, deployed or migrated.

## 1. Why the service needed an OpenAI key

* The RAG service had only two real providers: `openai` for embeddings (`text-embedding-3-small`, 1536 dimensions, API key
  required) and `openai` for generation (OpenAI-compatible `/chat/completions`, `Authorization: Bearer` key required).
* The only alternatives were the development stand-ins `hashing` and `extractive`, which production validation refuses.
* An unset `RAG_EMBEDDING_PROVIDER` means `openai`, and the service refuses to start without its key.
* The vector table is fixed at `vector(1536)`, the size of the OpenAI model.
* On the API side, the admin model registry (`Rag:GenerationModels`) shipped only `gpt-4.1-mini`, `gpt-4.1` and `gpt-4o-mini`.
  An ACTIVE admin configuration naming one of them is sent with every chat request.

## 2. What changed

| File | Change |
|---|---|
| `src/SmartSolar.RagService/app/config.py` | Providers `ollama` for embeddings and generation. They need a base URL and **no API key**; production accepts them, and still refuses `hashing`/`extractive`. Known local model `qwen3-embedding:0.6b` = 1024 dimensions. pgvector accepts only dimensions that have an approved schema script (1536, 1024). New `RAG_LLM_CONTEXT_TOKENS` (must cover context + answer) and `RAG_OLLAMA_KEEP_ALIVE`. |
| `app/embeddings.py` | `OllamaEmbedder`: native `POST /api/embed` with `truncate: false` (an over-long text fails instead of being embedded partially). Count, model, dimension and finite values are checked; timeout, transport and HTTP errors become `ProviderError`. Never falls back to another model. |
| `app/llm.py` | `OllamaChatModel`: native `POST /api/chat` with `stream: false`, **`think: false`**, `options.num_ctx`, `num_predict` and `temperature` (omitted when null). Any `<think>` block is removed from the answer. Errors and malformed replies become `ProviderError` (the API answers 503 / "assistant unavailable"). |
| `sql/knowledge_chunk_local_1024.sql` (+ `_rollback.sql`) | Keeps the OpenAI chunks as `knowledge_chunk_openai_1536` and creates an empty `knowledge_chunk` with `vector(1024)` and the same constraints and HNSW index. The rollback swaps them back. Manual migration, not run by any code. |
| `docker-compose.pull.yml` | `ollama` service (profile `rag`): no published port, only on network `rag-llm` (shared with `rag-service` only), model volume `ollama_models`, 2 CPU / 4 GB limits, one request at a time, prompt-cache cap `LLAMA_ARG_CACHE_RAM=256`. One-shot `ollama-models`: pulls the models whose provider is `ollama` on the first start and only checks them afterwards; `rag-service` starts after it exits 0 and its healthcheck is `/health/ready`. Redis and RabbitMQ published on 127.0.0.1 only. `API_IMAGE`/`API_IMAGE_TAG` to pin the API image. Pass-through of the new `RAG_*` variables (`RAG_OLLAMA_NUM_THREAD` defaults to 2 = `OLLAMA_CPUS`). |
| `app/main.py` | `/health/ready` is ready only when the vector table has the configured dimension **and** every Ollama model is pulled (`GET /api/tags`). |
| `src/SmartSolar.Modules/KnowledgeBase/*` | An ACTIVE configuration whose model is **not in the registry** is no longer sent: model, temperature and token parameter are left to `RAG_LLM_MODEL`, with a warning. Optional `Rag:EmbeddingModel`: `reindex-stale` also queues versions embedded with another model. |
| `src/SmartSolar.Api/appsettings.json` | `Rag:GenerationModels` is now `[]` (the same three cloud models still apply through the code defaults). See the compatibility note in §4. |
| `bench/bench_local_rag.py` | Reproducible benchmark with the service's own adapters and prompt builder (§6). |
| `.env.example`, `guides/rag-architecture.md`, `guides/rag-deployment.md` | Configuration documented. |

**Unchanged:**
* REST API and chat contract (the FE needs no change for this);
* ingestion, versioning, publish and unpublish;
* S3 integrity check, pgvector search and citation validation;
* audience filtering, HMAC authentication, quotas, rate limits and audit;
* the INSUFFICIENT_CONTEXT answer when no source fits.

## 3. Ollama API contract (checked, not assumed)

The OpenAI-compatible endpoint of Ollama was **not** reused:
* it cannot set the context window (`num_ctx`), and Ollama silently cuts the **start** of a prompt that does not fit, which
  is the system prompt;
* it cannot switch off Qwen3's thinking mode.

The native API can do both.

| Call | Request | Response used |
|---|---|---|
| `POST /api/embed` | `{model, input: [..], truncate: false, keep_alive}` | `{model, embeddings: [[..]]}` |
| `POST /api/chat` | `{model, messages, stream: false, think: false, keep_alive, options: {num_ctx, num_predict, temperature}}` | `{model, message: {content}, prompt_eval_count, eval_count, done_reason}` |

Live verification against a real Ollama server: see §6. The opt-in test is
`RAG_LIVE_OLLAMA_URL=http://127.0.0.1:11434 pytest tests/test_ollama.py -k live`.

## 4. Configuration

RAG service (`.env`, see `.env.example`):

```
RAG_ENV=production
RAG_EMBEDDING_PROVIDER=ollama   RAG_EMBEDDING_MODEL=qwen3-embedding:0.6b   RAG_EMBEDDING_DIMENSION=1024   RAG_EMBEDDING_BASE_URL=http://ollama:11434
RAG_LLM_PROVIDER=ollama         RAG_LLM_MODEL=qwen3:1.7b                   RAG_LLM_BASE_URL=http://ollama:11434
RAG_LLM_ALLOWED_MODELS=qwen3:1.7b
RAG_LLM_CONTEXT_TOKENS=8192     RAG_MAX_CONTEXT_TOKENS=3000   RAG_MAX_OUTPUT_TOKENS=700   # ceilings; the API budget decides per request (§6)
RAG_MAX_CONCURRENT_GENERATIONS=1   RAG_MAX_CONCURRENT_INGESTIONS=1   RAG_PROVIDER_TIMEOUT_SECONDS=120   RAG_EMBEDDING_BATCH_SIZE=8
```

API (`.env`):

```
Rag__GenerationModel=qwen3:1.7b
Rag__GenerationModels__0__Id=qwen3:1.7b
Rag__GenerationModels__0__DisplayName=Qwen3 1.7B (local)
Rag__GenerationModels__0__SupportsTemperature=true
Rag__GenerationModels__0__TokenParameter=max_tokens
Rag__GenerationModels__0__MaxOutputTokensLimit=1000
Rag__EmbeddingModel=qwen3-embedding:0.6b
```

* **Compatibility note:** a configured `Rag:GenerationModels` list now **replaces** the built-in cloud list; it is no longer
  merged by index with `appsettings.json`. A deployment that appended a model at index 3 must list all models it wants from
  index 0. In this repository only the endpoint tests did that, and they were adjusted.
* **Timeouts:** `Rag__RequestTimeoutSeconds` (60 s, at most 120) bounds a chat answer end to end. A local CPU answer that takes
  longer fails as "assistant unavailable". Do not raise it to hide a failed NFR (§6).

## 5. Migration and reindex (manual, approved window)

1. **Back up** the database (or snapshot it). Read the pre-checks at the top of `sql/knowledge_chunk_local_1024.sql`.
2. **Start Ollama; the models are pulled automatically** by the one-shot `ollama-models` service into the `ollama_models`
   volume (exact commands: §8.2). The running API is untouched meanwhile.
3. **Apply the migration:** `sql/knowledge_chunk_local_1024.sql`. The old chunks stay in `knowledge_chunk_openai_1536`.
4. **Switch the configuration:** set the RAG and API values of §4, then `docker compose … up -d rag-service api`.
   * Check `GET /health/ready` of the RAG service: `ready` means the column is `vector(1024)`.
5. **Admin reindex:** `POST /api/admin/rag-configuration/reindex-stale` queues every indexed version embedded with another model.
   * Watch the jobs in the knowledge base screen.
   * Until a version is re-ingested, the chatbot answers "not enough information" for it.
6. **Admin model configuration:** in the RAG configuration screen, activate a configuration with `qwen3:1.7b`. Until then the
   old ACTIVE row's model is not sent and `RAG_LLM_MODEL` answers.

**Rollback:**
* restore the previous `.env` values (OpenAI);
* run `sql/knowledge_chunk_local_1024_rollback.sql` (the OpenAI chunks come back unchanged; local chunks are kept as
  `knowledge_chunk_local_1024`);
* `up -d rag-service api`; `reindex-stale` with `Rag__EmbeddingModel=text-embedding-3-small` for versions ingested only
  after the switch;
* stop `ollama` (its volume can stay).

Both scripts were run on a disposable PostgreSQL 17 + pgvector (`tests/test_pgvector.py::test_local_1024_migration_…`):
* no row is lost in either direction;
* the old dimension is reported not ready;
* a 1536 vector is refused by the 1024 store;
* the index names are restored by the rollback.

## 6. Resource and latency measurements

**Where measured:** NOT on the VPS (no access, and deploying was not approved). Measured on a MacBook (Apple M5), with a
separate Ollama 0.40.12 server forced to CPU only (`num_gpu: 0`, `num_thread: 3`; the runner command line showed `-ngl 0 -t 3`).
Apple M5 cores are faster per core than a Xeon Platinum 8260 vCPU, so **every latency below is a lower bound for the VPS**.
Memory is process RSS (sampled every second). Prompts were the service's real prompt (`build_messages`) with **different
sources each run**, because Ollama caches repeated prompt prefixes.

**Embeddings** (`qwen3-embedding:0.6b`, measured):

| Measure | Value |
|---|---|
| Dimension returned | **1024** (L2-normalised); `truncate:false` over-long input → HTTP 400; unknown model → HTTP 404 |
| Ingestion | **0.89–1.04 s per 2,400-character chunk** (batch 8) |
| Query embedding (warm) | 15–30 ms |
| Cold load | 0.5–1.1 s |
| Runner RSS | about 0.6 GB while embedding; 1.2 GB once loaded with its 4,096 context |

At about 1 s per chunk, the 270 s ingest deadline covers roughly 250 chunks (about 600,000 characters) on this CPU, and fewer
on the VPS. Larger documents fail every attempt. Raise `RAG_INGEST_DEADLINE_SECONDS` and `Rag__IngestTimeoutSeconds` together
(the deadline must stay below the API timeout) if such documents are needed.

**Generation** (warm, 10 runs, distinct prompts; NFR p95 ≤ 10 s):

| Model | Sources / answer budget | Prompt tokens | p50 | p95 | NFR on this CPU | 2 concurrent (second waits) | Answer quality |
|---|---|---|---|---|---|---|---|
| qwen3:1.7b | 3,000 / 700 (API default) | ≈2,370 | 13.69 s | **14.74 s** | **FAIL** | 26.95 s | correct |
| qwen3:1.7b | 1,500 / 300 | ≈940 | 4.42 s | 5.24 s | PASS here; **VPS NOT MEASURED** | 9.13 s | correct |
| qwen3:0.6b | 3,000 / 700 | ≈2,380 | 6.90 s | 7.24 s | PASS here | 13.56 s | **wrong**: gave the inverter 12 years (source: 5) |
| qwen3:0.6b | 1,500 / 300 | ≈940 | 1.53 s | 1.88 s | PASS here | 3.07 s | **unusable**: INSUFFICIENT_CONTEXT although the source answered |

* Cold load of `qwen3:1.7b`: 1.5–2.0 s.
* Prompt evaluation dominates (answers are about 50–60 tokens).
* Retrieval (embedding plus pgvector) takes 13–19 ms.

**Conclusions:**
* **The NFR is FAIL for the default configuration.** Even on this faster CPU, `qwen3:1.7b` with the API defaults has
  p95 14.7 s.
* **Reduced budget:** `Rag__MaxContextTokens=1500` and `Rag__MaxOutputTokens=300` (or the admin configuration) pass here at
  5.2 s. On the VPS it must be measured with `bench/bench_local_rag.py`; with the slower cores it may or may not stay under 10 s.
* **Concurrency:** `OLLAMA_NUM_PARALLEL=1` serialises answers, so a second simultaneous question waits for the first.
  `RAG_MAX_CONCURRENT_GENERATIONS=1` plus `RAG_QUEUE_WAIT_SECONDS` turns a long queue into 503 "busy" instead of piling up
  memory.
* **`qwen3:0.6b` is not recommended:** it is faster, but it gave a factually wrong answer and an unjustified "no information"
  in two of two checks.

**Memory** (measured, CPU-only):

| Situation | Peak RSS |
|---|---|
| Both models loaded, prompt cache capped (`LLAMA_ARG_CACHE_RAM=256`) | about 2.9–3.5 GB in total for all Ollama processes (generation runner 2.3–2.7 GB with `num_ctx` 8192; embedding runner about 1.2 GB) |
| **Without the cap** | the generation runner grew to **5.0 GB** over about 24 distinct prompts: Ollama 0.40.12 starts llama-server without `--cache-ram`, whose default is 8 GiB |
| With the cap, 21 prompts on one runner | peak 2.35 GB, no growth |

Caveat on the capped run: the Mac slept during that run, so its timings were discarded and its late RSS samples may reflect
memory compression.

**What this means for the 5.8 GiB VPS:**
* Measured available memory was 4.5 GiB with the current services. Ollama needs about 3.5 GB (limit 4 GB in compose), and the
  RAG service is limited to 1 GB.
* That leaves well under 1 GB of headroom with **no swap**, so the OOM risk is real.
* Before production:
  * confirm with `docker stats` during `bench_local_rag.py` on the VPS that the runner honours `LLAMA_ARG_CACHE_RAM`;
  * consider a swap file (operations decision);
  * consider `RAG_LLM_CONTEXT_TOKENS` 4096 together with the reduced budgets (a smaller KV cache).

**Not measured:**
* anything on the VPS (CPU, RAM, latency);
* the effect on the .NET API, Redis and RabbitMQ;
* load beyond 2 concurrent questions;
* GPU.

**Reproduce on the VPS:** the commands are at the top of `src/SmartSolar.RagService/bench/bench_local_rag.py` (run inside
`rag-service`, with `docker stats` sampling in parallel).

## 7. Security

* **Ollama port:** 11434 is never published. Only `rag-service` shares its network, and Ollama has no authentication of its
  own, so it must stay private.
* **Unchanged:** no API key is needed or logged; HMAC between API and RAG service is unchanged.
* **Published ports:** only the API (8080, needed by the Vercel `/api` proxy). RabbitMQ (5672, 15672) and Redis (6379), which
  used to be published on all interfaces, are now bound to 127.0.0.1. Docker-published ports bypass UFW, so a firewall rule
  would not have protected them. `ollama` (11434) and `rag-service` (8000) are not published.
  * The VPS copy of the compose file may differ from the repository: diff before replacing it (§8.2), and check with `ss`
    and `nc` afterwards (§8.4).
* **Startup and readiness verified with Docker Compose** (MacBook, `ollama/ollama:latest`, test project, then removed):
  * **First `up`:** both models pulled, then `rag-service` started and became healthy.
  * **`down`/`up`:** "model present" for both, 0 pull requests, the stack was up in 7 s.
  * **A deleted model:** `/health/ready` answered 503 and the next `up` re-pulled only that model.
  * **Inside the Linux container:** limits 2 CPU / 4 GiB with no port bindings; the runner (`/usr/lib/ollama/llama-server`)
    had `LLAMA_ARG_CACHE_RAM=256` in its environment and ran with `-t 2` (from `RAG_OLLAMA_NUM_THREAD`), although the
    container sees every host core.
* **CPU threads:** latency in §6 was measured with **3** threads. With `OLLAMA_CPUS=2` / `RAG_OLLAMA_NUM_THREAD=2` (one vCPU
  left for the API, Redis and RabbitMQ) it will be slower; it was not measured with 2. Setting both to 3 gives Ollama every
  vCPU at the expense of the other services. Decide after the VPS benchmark (§8.6).

## 8. Deploy runbook (MacBook → VPS)

Nothing in this section was executed against the VPS or a shared database. Every step needs the owner's approval; secrets are
only typed at prompts or read from root-only files, never echoed or put on a command line.

### 8.1 MacBook: build, push, prepare files

```bash
cd ~/smart-solar-backend
git status --short                       # the images are built from this working tree (uncommitted changes included)
TAG="$(git rev-parse --short HEAD)-rag-local-$(date +%Y%m%d%H%M)"; echo "$TAG"
OUT=~/smartsolar-deploy/$TAG; mkdir -p "$OUT"

# Tests first (RAG: a virtualenv with requirements-dev.txt)
(cd src/SmartSolar.RagService && python -m pytest -q && ruff check .)
DOTNET_ROOT=~/.dotnet8 ~/.dotnet8/dotnet test tests/SmartSolar.Tests

# linux/amd64 images (the VPS is x86_64, the MacBook arm64). Only the version tag is pushed, not :latest, so nothing pulls the
# new API before its migration. .dockerignore keeps .env out of both images.
docker login -u hoaikhoitran             # access token at the prompt
docker buildx build --builder smartsolar-multiarch --platform linux/amd64 \
  -t hoaikhoitran/smartsolar-api:$TAG --push .
docker buildx build --builder smartsolar-multiarch --platform linux/amd64 \
  -t hoaikhoitran/smartsolar-rag-service:$TAG --push src/SmartSolar.RagService
docker buildx imagetools inspect hoaikhoitran/smartsolar-api:$TAG | grep -E "^(Name|MediaType)|Platform"
docker buildx imagetools inspect hoaikhoitran/smartsolar-rag-service:$TAG | grep -E "^(Name|MediaType)|Platform"

# EF migration SQL, idempotent (applies only what the target database is missing; no database connection is made here)
ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=1;Database=design;Username=design;Password=design" \
Jwt__SigningKey="design-time-only-signing-key-not-a-secret-000" \
Frontend__EmailVerificationUrl=http://localhost/verify Frontend__PasswordResetUrl=http://localhost/reset \
DOTNET_ROOT=~/.dotnet8 PATH=~/.dotnet8:$PATH ~/.dotnet/tools/dotnet-ef migrations script --idempotent \
  --project src/SmartSolar.Infrastructure --startup-project src/SmartSolar.Api -o "$OUT/ef-migrations.sql"

cp docker-compose.pull.yml src/SmartSolar.RagService/sql/knowledge_chunk_local_1024.sql \
   src/SmartSolar.RagService/sql/knowledge_chunk_local_1024_rollback.sql src/SmartSolar.RagService/bench/bench_local_rag.py "$OUT/"
(cd "$OUT" && shasum -a 256 * > SHA256SUMS)

VPS=root@160.187.1.242
ssh $VPS "mkdir -p /root/app/deploy/$TAG"
scp "$OUT"/* $VPS:/root/app/deploy/$TAG/
```

The idempotent script was checked on a disposable PostgreSQL 17:
* `psql -v ON_ERROR_STOP=1 -f` applied 22 migrations;
* a second run changed nothing;
* the UTF-8 BOM at its start is harmless.

Do **not** add `--single-transaction`. Each migration carries its own `START TRANSACTION`/`COMMIT`; under psql's outer
transaction the first `COMMIT` would end it early.

### 8.2 VPS: prepare (no downtime)

```bash
ssh root@160.187.1.242
cd /root/app
read -r -p "Tag printed on the MacBook: " TAG; DIR=/root/app/deploy/$TAG; TS=$(date +%Y%m%d%H%M)
C="docker compose -f docker-compose.pull.yml --profile rag"
(cd $DIR && sha256sum -c SHA256SUMS)

# Back up the deployment files and record what runs now (rollback reference)
cp -p docker-compose.pull.yml docker-compose.pull.yml.bak-$TS
install -m 600 .env .env.bak-$TS
$C images > deploy/images-before-$TS.txt; $C ps -a > deploy/ps-before-$TS.txt

# Compare the new compose file with the one in use (the VPS copy may differ from the repository), then put it in place
diff -u docker-compose.pull.yml $DIR/docker-compose.pull.yml | less
cp $DIR/docker-compose.pull.yml docker-compose.pull.yml
```

Edit `.env` with `nano .env`; the values are not printed here:
* **API image:** `API_IMAGE_TAG=<TAG>`.
* **RAG image:** `RAG_IMAGE_TAG=<TAG>`.
* **RAG and API settings:** the values of §4.
* **Resources:** `OLLAMA_CPUS=2.0` with `RAG_OLLAMA_NUM_THREAD=2`, `OLLAMA_MEMORY_LIMIT=4096m`, `LLAMA_ARG_CACHE_RAM=256`.

Then check only that the keys exist, not their values:

```bash
grep -o -E '^(API_IMAGE_TAG|RAG_IMAGE_TAG|RAG_EMBEDDING_PROVIDER|RAG_EMBEDDING_MODEL|RAG_EMBEDDING_DIMENSION|RAG_LLM_PROVIDER|RAG_LLM_MODEL|RAG_LLM_BASE_URL|RAG_EMBEDDING_BASE_URL|RAG_OLLAMA_NUM_THREAD|Rag__EmbeddingModel|Rag__GenerationModels__0__Id)=' .env
$C config --quiet && echo "compose config OK"

# Pull images, start Ollama and download the two models into the ollama_models volume (the running API is untouched)
$C pull
$C up -d ollama ollama-models
until [ "$(docker inspect -f '{{.State.Status}}' $($C ps -a -q ollama-models))" = exited ]; do sleep 10; done
docker inspect -f 'ollama-models exit code: {{.State.ExitCode}}' $($C ps -a -q ollama-models)    # must be 0
$C logs ollama-models | grep -E "model present|pulling model|local models ready"
$C exec -T ollama ollama list
```

### 8.3 VPS: database backup and migration (maintenance window, writers stopped)

Database credentials go in a root-only file:
* **Contents:** `PGHOST`, `PGPORT`, `PGDATABASE`, `PGUSER`, `PGPASSWORD`, `PGSSLMODE=require`.
* **Connection type:** the owner role. On Supabase use the **direct or session** connection (port 5432); the transaction
  pooler cannot run `pg_dump`.

```bash
install -m 600 /dev/null /root/app/deploy/pg.env && nano /root/app/deploy/pg.env
PG="docker run --rm --env-file /root/app/deploy/pg.env -v /root/app/deploy:/deploy postgres:17"

$C stop api rag-service                   # no writer during backup and migration (chat answers "unavailable" meanwhile)

# 1. Backup + checksum + readable listing
$PG pg_dump -Fc --no-owner -f /deploy/backup-$TS.dump
sha256sum /root/app/deploy/backup-$TS.dump | tee /root/app/deploy/backup-$TS.dump.sha256
$PG pg_restore -l /deploy/backup-$TS.dump | head -5

# 2. Which EF migrations the database already has (read only)
$PG psql -v ON_ERROR_STOP=1 -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 12'

# 3. EF migrations (applies only the missing ones; each migration is atomic; stops at the first error)
$PG psql -v ON_ERROR_STOP=1 -f /deploy/$TAG/ef-migrations.sql > /root/app/deploy/ef-apply-$TS.log 2>&1; echo "exit=$?"
grep -E "ERROR|FATAL" /root/app/deploy/ef-apply-$TS.log | head

# 4. Local-embedding chunk table (keeps the OpenAI chunks as knowledge_chunk_openai_1536). Read its pre-checks first.
$PG psql -v ON_ERROR_STOP=1 -c "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a WHERE a.attrelid = to_regclass('knowledge_chunk') AND a.attname = 'embedding'"
$PG psql -v ON_ERROR_STOP=1 -f /deploy/$TAG/knowledge_chunk_local_1024.sql
$PG psql -At -c "SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a WHERE a.attrelid = to_regclass('knowledge_chunk') AND a.attname = 'embedding'"   # vector(1024)
```

Skip step 4 when `knowledge_chunk` does not exist yet; then create it from `sql/knowledge_chunk.sql` with `vector(1024)`
instead. If the RAG service connects with a dedicated role, run the `GRANT` at the end of the script.

### 8.4 VPS: start, health checks

```bash
$C up -d                                  # api + rag-service on the new tags; ollama-models re-checks (no download)
$C ps -a

curl -fsS http://127.0.0.1:8080/health/live; echo
curl -fsS http://127.0.0.1:8080/health/ready; echo           # 503 SCHEMA_NOT_READY = a migration is missing
$C exec -T rag-service python -c "import urllib.request; print(urllib.request.urlopen('http://127.0.0.1:8000/health/ready', timeout=5).read().decode())"
docker inspect -f '{{.State.Health.Status}}' $($C ps -q rag-service)       # healthy
$C logs --since 15m api | grep -E "fail:|crit:|SCHEMA_NOT_READY" | tail -20
$C logs --since 15m rag-service | grep -E "ERROR|WARNING" | tail -20

# Exposure: only 8080 on all interfaces; 5672/15672/6379 on 127.0.0.1; 8000 and 11434 not published at all
ss -ltnp | grep -E ':(8080|5672|15672|6379|8000|11434)\b'
docker ps --format '{{.Names}}\t{{.Ports}}'
```

From the MacBook, all of these must fail except 8080:

```bash
for p in 8080 6379 5672 15672 11434 8000; do nc -z -w 5 160.187.1.242 $p && echo "$p OPEN" || echo "$p closed"; done
```

### 8.5 VPS: reindex and model configuration (Admin)

```bash
read -r -p "Admin email: " ADMIN_EMAIL; read -r -s -p "Admin password: " ADMIN_PASSWORD; echo
TOKEN=$(ADMIN_EMAIL="$ADMIN_EMAIL" ADMIN_PASSWORD="$ADMIN_PASSWORD" python3 -c 'import json,os; print(json.dumps({"email": os.environ["ADMIN_EMAIL"], "password": os.environ["ADMIN_PASSWORD"]}))' \
  | curl -fsS -X POST http://127.0.0.1:8080/api/auth/login -H 'Content-Type: application/json' -d @- \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["accessToken"])')
unset ADMIN_PASSWORD
API() { curl -fsS -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' "$@"; }

# Reindex every version embedded with the old model (Rag__EmbeddingModel must be set); jobs run in the background
API -X POST http://127.0.0.1:8080/api/admin/rag-configuration/reindex-stale | python3 -m json.tool
# Progress: Admin → Kho tri thức AI (jobs per document), or:
API http://127.0.0.1:8080/api/admin/knowledge-documents | python3 -c 'import json,sys; [print(d["status"], d["title"]) for d in json.load(sys.stdin)["data"]]'

# Activate a configuration with the local model. CTX/OUT: the current defaults are 3000/700 (NFR FAIL even on a faster CPU,
# §6); 1500/300 passed only on the MacBook and must be benchmarked on this VPS first (§8.6). Changing them is a product decision.
CTX=3000; OUT=700
DRAFT=$(API -X PUT http://127.0.0.1:8080/api/admin/rag-configuration/draft \
  -d "{\"generationModel\":\"qwen3:1.7b\",\"temperature\":0.1,\"maxContextTokens\":$CTX,\"maxOutputTokens\":$OUT,\"notes\":\"local qwen3:1.7b\"}" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["revision"])')
API -X POST http://127.0.0.1:8080/api/admin/rag-configuration/draft/activate -d "{\"expectedRevision\":$DRAFT}" | python3 -m json.tool | head -20
unset TOKEN
```

`PUT /draft` refuses with 409 when a draft already exists: send that draft's revision as `expectedRevision`, or discard it in
the Admin screen.

### 8.6 VPS: benchmark (decides the NFR; run in a quiet window)

```bash
while true; do docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}'; sleep 2; done > /root/app/deploy/stats-$TS.log &
$C exec -T -e BENCH_RUNS=10 -e BENCH_CONCURRENCY=2 -e BENCH_SKIP_COLD=0 rag-service python - < $DIR/bench_local_rag.py | tee /root/app/deploy/bench-$TS.json
$C exec -T -e BENCH_RUNS=10 -e BENCH_CONTEXT_TOKENS=1500 -e BENCH_OUTPUT_TOKENS=300 -e BENCH_SKIP_COLD=1 rag-service python - < $DIR/bench_local_rag.py | tee /root/app/deploy/bench-1500-$TS.json
kill %1
free -m; sort -k3 -h /root/app/deploy/stats-$TS.log | tail -5
```

The NFR is PASS only if `warmGeneration.p95` ≤ 10 s **on the VPS**. Watch `free -m` and the stats log for memory pressure
(no swap).

### 8.7 Rollback

```bash
cd /root/app; C="docker compose -f docker-compose.pull.yml --profile rag"; PG="docker run --rm --env-file /root/app/deploy/pg.env -v /root/app/deploy:/deploy postgres:17"
$C stop api rag-service
$PG psql -v ON_ERROR_STOP=1 -f /deploy/$TAG/knowledge_chunk_local_1024_rollback.sql          # OpenAI chunks back, unchanged
cp -p docker-compose.pull.yml.bak-$TS docker-compose.pull.yml; install -m 600 .env.bak-$TS .env
$C up -d api rag-service; $C stop ollama
```

* **EF migrations** are not reversed by this rollback. The previous API image must not run on a schema it does not know.
  * If the previous image predates migrations that were applied in §8.3, restore the backup instead
    (`$PG sh -c 'pg_restore --clean --if-exists --no-owner -d "$PGDATABASE" /deploy/backup-'$TS'.dump'`, with approval). That loses
    writes made after the backup.
* **Models:** the `ollama_models` volume can stay for a later attempt.

## 9. Answer quality (prompt v2) and latency diagnostics (2026-11-13)

### 9.1 Why answers were bare values

Production evidence: "Tấm pin TOPCon SS-PV-T580 có hiệu suất bao nhiêu?" → `[1] 22,5%` (grounded, valid citation). The `v1` system
prompt (`app/prompting.py`) ends with **"Trả lời ngắn gọn bằng ngôn ngữ của câu hỏi"** and demands a `[n]` after every idea; a
1.7B model follows that literally and returns the value alone. Nothing in the .NET side shortens answers (`ChatService` only
trims and re-validates citations).

**Change:** a new template **`v2`** (`PROMPT_TEMPLATES["v2"]`, `v1` untouched for exact rollback): complete sentences with the
object, value, unit and conditions; every supported part of a multi-part question in order, naming the parts the sources do
not cover **in words** (the `INSUFFICIENT_CONTEXT` sentinel stays reserved for "no source answers at all", because any
occurrence of it makes the whole answer ungrounded); all steps, conditions and exceptions for process/policy questions;
bullets only for three or more items; length proportional to the question and the evidence; a source's own "test / demo /
not official" wording must be repeated; the same no-invention, injection-hardening and citation rules as `v1`.

**Selecting it on the deployed system** (the API sends the ACTIVE configuration's `promptVersion` with every request; the RAG
service's `RAG_PROMPT_VERSION` only applies when the API sends none):

1. Deploy the RAG image (new template) and the API image (`Rag:PromptVersions` now `["v1","v2"]`, so the Admin catalog offers `v2`).
2. As an Admin (token from a `read -s` prompt, never echoed):

```bash
API=https://<api-host>; read -r -s -p "Admin access token: " TOKEN; echo
curl -fsS -H "Authorization: Bearer $TOKEN" "$API/api/admin/rag-configuration/models" | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["promptVersions"])'
# Draft based on the ACTIVE configuration, changing only the prompt version, then activate it.
DRAFT=$(curl -fsS -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"promptVersion":"v2","notes":"Complete natural answers (prompt v2)"}' "$API/api/admin/rag-configuration/draft")
REV=$(printf '%s' "$DRAFT" | python3 -c 'import json,sys; print(json.load(sys.stdin)["data"]["revision"])')
curl -fsS -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "{\"expectedRevision\":$REV}" \
  "$API/api/admin/rag-configuration/draft/activate" | python3 -c 'import json,sys; a=json.load(sys.stdin)["data"]["active"]; print(a["version"], a["promptVersion"])'
unset TOKEN
```

   A prompt change does not change chunks, so no reindex follows. Rollback: `POST /api/admin/rag-configuration/rollback`
   with the previous version number.

Expected shape for the test PDF (only when the source says so): "Theo tài liệu thử nghiệm SmartSolar, tấm pin TOPCon SS-PV-T580
có hiệu suất 22,5% [1]. Đây là dữ liệu giả lập dùng để kiểm thử, không phải thông số sản phẩm chính thức [1]." **Not verified
with the real model here** (see 9.3): whether `qwen3:1.7b` follows `v2` as intended must be read off the first answers on the
VPS; if it over-elaborates, lower `MaxOutputTokens` in the same Admin configuration rather than editing `v2`.

### 9.2 Where the 48 seconds go

* The service calls Ollama's **native `/api/chat`** with **`think: false`** on every request (`OllamaChatModel.payload`; tests
  `test_chat_request_disables_thinking_and_sets_the_context_window` and
  `test_thinking_stays_off_whatever_generation_options_the_api_sends`). The deployed image is the one that calls `/api/embed`
  (same change set), so thinking was already off for the 48 s answer; the non-empty `[1] 22,5%` is consistent with that (your
  `think:true` control produced 64 tokens of reasoning and an empty answer).
* **Prompt evaluation dominates.** §6 measured ≈2,370 prompt tokens for the API defaults (3,000 context tokens + system prompt)
  at 13.7 s on an M5 core with 3 threads. The VPS runs 2 threads on slower Xeon cores; 48.3 s for the same prompt size is
  within a 3–4× factor of that. The short-prompt control (6.38 s for 21 tokens) says generation itself runs at a few tokens per
  second there.
* **New diagnostics (this change):** the answer response and the log line now carry Ollama's own breakdown — `loadMs`,
  `promptEvalMs`, `evalMs`, `totalMs`, prompt and completion token counts, and tokens/s
  (`Answered (grounded=…, retrieval=…ms, generation=…ms, promptTokens=…, completionTokens=…, load=…ms, promptEval=…ms,
  eval=…ms, … tok/s)`). No question text is logged. The bench script reports the same medians.
* **Cost of `v2`:** the system prompt grows by ≈330 estimated tokens. It is the first message, so llama-server's prompt cache
  reuses it across requests (paid once per model load, and after any request with a different prefix — there is none). The
  real cost of `v2` is **longer answers**: at the VPS's generation rate every extra 30 output tokens costs roughly 5–10 s. This
  is the trade-off to measure (9.3), not to assume.
* **Levers that do reduce the time (configuration, no code):** `maxContextTokens` 1,500 and `topK` 4 in the Admin configuration
  (§6: ≈940 prompt tokens, 4.4 s p50 on the M5 — the VPS figure must be measured); fewer history messages
  (`Rag__MaxHistoryMessages`, each kept turn adds up to 1,500 characters to the prompt); `OLLAMA_CPUS=3` with
  `RAG_OLLAMA_NUM_THREAD=3` if the API, Redis and RabbitMQ can spare the core. `num_ctx` stays 8,192 (covers 6,000 + 1,500 +
  question/history); `MaxOutputTokens` 700 is not reached by `v2` answers of this kind, so nothing is truncated.

### 9.3 Benchmark status: BLOCKED here, procedure for the VPS

The MacBook has no `qwen3:1.7b` / `qwen3-embedding:0.6b` and pulling them was not approved, so **no before/after latency or
quality numbers for `v2` exist yet**. Known "before" (production, `v1`): retrieval 832 ms, generation 48,301 ms, answer
`[1] 22,5%`. "After": **NOT MEASURED**.

On the VPS, in a quiet window, with the models warm (`BENCH_SKIP_COLD=1`), run the same questions under both prompts and
sample memory at the same time:

```bash
cd /root/app && C="docker compose -f docker-compose.pull.yml --profile rag"
( while true; do docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}'; sleep 2; done ) > /root/app/stats-$(date +%Y%m%d%H%M).log 2>&1 &
STATS=$!
for V in v1 v2; do
  $C exec -T -e BENCH_SKIP_COLD=1 -e BENCH_RUNS=10 -e BENCH_CONCURRENCY=0 -e BENCH_PROMPT_VERSION=$V rag-service python - < src/SmartSolar.RagService/bench/bench_local_rag.py \
    > /root/app/bench-$V-$(date +%Y%m%d%H%M).json
done
kill $STATS
```

Read, per prompt version: `warmGeneration.p50/p95` (end to end for the generation step), `ollama.promptEvalMsMedian`,
`ollama.evalMsMedian`, `ollama.tokensPerSecond`, `promptTokensReported`, `completionTokensMean`, `sampleAnswer` (completeness and
citations), `loadMsMedian`, and the peak `ollama` memory from the stats log. Then ask the real test questions through the chat
endpoint and compare the service log lines (`retrieval=`, `generation=`, `promptEval=`, `eval=`). The NFR (p95 ≤ 10 s) is decided by
these numbers, not by this document.

