# Rate limiting and resource protection

Three independent layers protect the API. They are deliberately separate:

| Layer | Purpose | Where |
|---|---|---|
| HTTP rate limits | Request floods per user (per IP when anonymous) | ASP.NET rate limiter, policies below |
| Business quotas and idempotency | Cost and correctness, regardless of how many requests pass | PostgreSQL (RAG token/request ledger, payment attempt cap, unique indexes) |
| Webhook authenticity | Only PayOS can confirm payments; replays are harmless | HMAC signature + payload-hash deduplication |

All 429 responses keep the standard envelope (`AUTH_TOO_MANY_REQUESTS`) and a `Retry-After` header.

## 1. Cluster-wide (Redis) vs per-instance policies

When `Redis__Enabled=true`, the **expensive** policies count in one shared Redis counter, so a user gets
the limit in total across all API replicas. Every other policy counts per API instance (N replicas ⇒ up
to N × the limit), which is acceptable for cheap reads and writes.

| Policy | Default | Partition | Scope |
|---|---|---|---|
| `rag-chat` | 10 / min | user | **cluster-wide** |
| `rag-admin-register` (document registration, new versions → ingestion) | 10 / min | user | **cluster-wide** |
| `rag-admin-reindex` (reindex, retry job) | 3 / 10 min | user | **cluster-wide** |
| `files-write` (S3 reference registration and verification) | 20 / min | user | **cluster-wide** |
| `installation-preview` | 15 / min | user | **cluster-wide** |
| `installation-scheduling` (confirm schedule, leader, milestones) | 10 / min | user | **cluster-wide** |
| `payment-create` | 5 / 10 min | user + contract | **cluster-wide** |
| `payos-webhook` | 600 / min | source IP | per instance (abuse guard only) |
| all other Installation Flow, file-read, chat-history and admin-read policies | see `appsettings.json` | user | per instance |
| `system-settings-read` (scheduling settings + history, Admin/Manager) | 60 / min | user | per instance |
| `system-settings-write` (Admin settings update) | 10 / min | user | per instance |
| administrative-units lookups (anonymous: provinces, communes, legacy lookup) | existing `catalog-read` | IP when anonymous | per instance |
| operational zones, technician zones, dispatch requests (customer + staff) | existing `scheduling-read` / `scheduling-write` | user | per instance |
| existing auth, catalog, simulation, scheduling, admin-users policies | unchanged | unchanged | per instance |

Every value is configurable under `RateLimiting:<Section>:PermitLimit` / `WindowMinutes`.

Implementation: `DistributedFixedWindowRateLimiter` + `RedisRateLimitCounter`. One Lua script performs
`INCR` + `PEXPIRE` atomically and derives the window from Redis' own clock (`TIME`), so replicas with
skewed clocks still share a window. Keys are `{Redis:InstanceName}ratelimit:{policy}:{partition}:{window}`
and expire with their window. Standalone or replicated Redis is supported; Redis Cluster is not.

## 2. Redis failure policy: fail to local

If Redis is disabled, unreachable, errors, or takes longer than **250 ms**, the request is decided by a
per-instance fixed window with the **same** limit:

* requests never fail because Redis is down (no 500s, no blanket rejection);
* limits keep holding per replica, so the worst case equals the pre-Redis behaviour (N × limit);
* the expensive operations remain bounded by their PostgreSQL quotas (RAG daily tokens/requests and
  concurrent generations, payment attempts per contract) and by the RAG service's own concurrency caps;
* the fallback is logged as a warning at most once a minute per process.

The Redis connection is created lazily with `AbortOnConnectFail=false`, so the API starts while Redis is down.

## 3. PayOS webhook

The webhook is anonymous and never subject to customer limits. Its only throttle is a generous per-IP
guard (600 requests per minute by default, far above PayOS' retry rate). Behind a reverse proxy, configure
`ForwardedHeaders:KnownProxies` so the real client IP is used; otherwise all traffic shares the proxy IP.
Authenticity comes from the HMAC signature (invalid signatures are rejected and nothing is stored), and a
replayed valid delivery is deduplicated by payload hash, so it cannot apply a payment twice.

## 4. RAG cost controls (PostgreSQL)

* Per user per day: 100 questions and 200 000 tokens; globally 5 000 000 tokens; at most 2 generations
  in flight per user (`Rag:*`).
* Each question holds a **leased reservation** (`rag_usage_reservation`) for its token estimate while it
  runs. Reservation, limit checks and completion are single transactions with the user and global ledger
  rows locked (global first), so concurrent replicas cannot overspend and counters never go negative.
* A crashed or abandoned request frees its concurrency slot when the lease expires and is charged at its
  estimate (never forgiven, never reset by a restart). A completion arriving later is ignored (no double charge).
* The FastAPI service additionally caps concurrent generations (8) and ingestions (2) per process and
  stops an ingestion at `RAG_INGEST_DEADLINE_SECONDS` (270 s, below the API's 300 s call timeout).
