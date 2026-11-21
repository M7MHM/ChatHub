# ChatHub performance (load test, 2026-11-21)

Measured with the k6 scripts in [loadtest/](../loadtest/README.md) against the docker-compose stack on one laptop. All numbers below are measured, from the k6 summaries and `docker stats` samples in `loadtest/results/` (gitignored) unless a line says otherwise.

## Summary

- **One API replica (1 CPU, 512 MB) sustains about 80 read-heavy iterations/s (≈250 req/s)** with p95 ≤ 100 ms, with or without the cache.
- **The cache changes little on this workload.** It removes the per-request security-stamp query and the GetMe query, which are about 60% of SQL statements but only about 1.5% of database time. The expensive part, the conversation list, is not cached.
- **With default settings a second replica makes things worse, not better.** Each replica's Npgsql pool may hold 100 connections, but PostgreSQL allows 100 in total. At 70 it/s one replica got 17,667 `53300: too many clients` errors, 23.6% of requests failed, and latency reached seconds.
- **With the pool capped at 45 per replica, two replicas sustain 100 it/s (310 req/s)** with p95 63 ms, and fail the latency budget at 140 it/s (434 req/s, still 0 errors). By then PostgreSQL, which shares the same laptop, is at 1.7 cores.
- **Realtime delivery across replicas:** 600/600 messages reached their target listener; send-to-receive latency p50 11 ms, p95 21 ms, p99 41 ms.
- **Main database bottleneck:** the conversation list query takes 85–88% of the time spent in the top-10 SQL statements. 70% of its execution is spent on a window function over *every* membership in the database.

## Machine and setup

| | |
|---|---|
| CPU | Intel Core i7-10850H @ 2.70 GHz, 6 cores / 12 threads |
| RAM | 15.7 GB |
| OS | Windows 11 Pro 10.0.26300 (build 26300) |
| Docker | Docker Desktop 29.4.1, WSL2 kernel 6.6.87.2; the Docker VM has 12 CPUs and 7.6 GiB |
| Images | `postgres:17`, `redis:8-alpine`, `nginx:1.30-alpine`, `grafana/k6:2.4.0`, API built from this repo (.NET 10) |

Every API replica is limited to **1 CPU and 512 MB** by `docker-compose.loadtest.yml`, so it behaves like a small separate server. PostgreSQL, Redis, nginx and k6 have no limits and share the remaining cores of the same laptop. k6 runs inside the compose network and calls `http://nginx:8080`, so every request goes through nginx.

The load-test override also:

- forces `ASPNETCORE_ENVIRONMENT=Production` (the local `.env` sets `Development`, see bottleneck 3);
- raises the auth rate limits to 1,000,000/min, because k6 logs in 200 users from one IP;
- loads `pg_stat_statements` into PostgreSQL.

## Dataset

`docker compose --profile seed run --rm seeder --confirm` (defaults, random seed 20261121) inserted in **15.9 s**:

| | |
|---|---|
| Users | 1,000 (`seed-0001@chathub.test` … `seed-1000@chathub.test`, password `Seed12345`) |
| Conversations | 500 direct + 100 groups (5–50 members, an owner and 1–3 admins), 3,442 memberships |
| Messages | 50,000 over the last 30 days. Per conversation: median 31, p90 200, max 1,286, 2 empty |
| Conversations per user | median 3, max 11; 20 users have none |
| Read state | 2,858 of 3,450 memberships have a read pointer; the rest have never read anything, so unread counts vary |
| Blocks | 20 |

A second seeder run printed "Seed users … already exist; nothing was inserted" and exited 0. Without `--confirm` it exits with code 2. The database also contained a handful of rows from earlier manual tests (8 memberships). Each load-test run adds about 1,000–1,400 messages through the 10% write share.

## Workload

`read-heavy` (the main scenario). `setup()` logs in 200 evenly spaced seeded users (191 have conversations) and fetches their conversation ids. Each iteration picks a random user and makes three or four requests:

1. `GET /api/users/me`
2. `GET /api/conversations` (first page, 20 items, with unread counts and last-message preview)
3. `GET /api/conversations/{id}/messages` for one of the user's conversations (30 messages)
4. 10% of iterations: `POST /api/conversations/{id}/messages`

That's about 3.1 requests per iteration. The executor is an **open model**: iterations start at a fixed arrival rate whether or not earlier ones have finished, so a slow server builds up a queue instead of quietly lowering the load. Every run has these phases:

- ramp-up (20 s);
- warm-up at the target rate (60 s), not measured, so JIT, connection pools and caches settle;
- **steady phase: 120 s in the matrix, 60 s in the capacity search; the only phase that is measured and checked**;
- ramp-down (5–10 s).

`docker stats` is sampled every ~2 s during the steady phase only.

### Thresholds (all on the steady phase)

| Metric | Budget | Why |
|---|---|---|
| `http_req_failed` | < 1% | Errors are never acceptable as a price for throughput |
| p95 `GET /api/users/me` | < 100 ms | Unloaded p95 was 5–7 ms |
| p95 `GET /api/conversations/{id}/messages` | < 150 ms | Unloaded p95 8–10 ms |
| p95 `GET /api/conversations` | < 200 ms | Unloaded p95 10–14 ms; the heaviest read, and still feels instant at 200 ms |
| p95 `POST …/messages` | < 250 ms | Unloaded p95 18–27 ms; a write plus event handlers |
| dropped iterations | ≤ 1% of planned | k6 could not start an iteration on time: the system is falling behind |
| checks | > 99% | Every response must have the expected status |
| realtime p95 delivery | < 250 ms | Unloaded it is ~20 ms |

The p95 budgets are roughly 10× the unloaded p95 measured in the first runs. That is well inside what a chat UI can afford, and they only break when requests start queueing, which is what "sustainable" should detect.

## Commands

```powershell
docker compose up -d --build
docker compose --profile seed run --rm seeder --confirm      # 15.9 s
docker compose --profile seed run --rm seeder --confirm      # no-op

# configuration 1 capacity probe (single runs, earlier version of run.ps1, see "Caveats")
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode single -Replicas 1 -Caching false -Rate 40 -Name probe-r1-nocache-40 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode max -Replicas 1 -Caching false -StepRates 60,80 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5

# same-load matrix
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode matrix -Rate 70 -RampUp 20 -Warmup 60 -Steady 120 -RampDown 10

# configuration 3 capacity, pool capped (configuration 3 with defaults already failed at 70 it/s in the matrix)
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode max -Replicas 2 -Caching true -MaxPoolSize 45 -StepRates 100,140,180 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5

# realtime
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode single -Replicas 2 -Caching true -MaxPoolSize 45 -Scenario realtime -Name realtime-r2-cache-pool45 -ExtraEnv "RT_DURATION=60" -RampUp 15 -Warmup 0 -Steady 60
```

## Results

### Same load: 70 iterations/s (≈217 req/s), 120 s steady

Latencies in ms (p50 / p95 / p99). CPU is the average (max) of `docker stats` samples, in % of one core.

| | 1. 1 replica, cache OFF | 2. 1 replica, cache ON | 3. 2 replicas, cache ON |
|---|---|---|---|
| Served req/s | 217.0 | 214.6 | 192.1 |
| Errors | 0.00% | 0.00% | **23.6%** |
| Dropped iterations | 0 | 104 (allowed 84) | 966 |
| `GET /me` | 3.6 / 89.5 / 498 | 2.4 / 90.8 / 1,477 | 2.9 / 729 / 1,239 |
| `GET /conversations` | 8.6 / 104.8 / 446 | 9.4 / 108.9 / 2,106 | 5,609 / 15,435 / 15,949 |
| `GET /…/messages` | 5.6 / 95.9 / 408 | 6.0 / 100.6 / 594 | 16.6 / 12,279 / 15,802 |
| `POST /…/messages` | 14.1 / 213.8 / 813 | 16.5 / 183.4 / 590 | 33.7 / 16,140 / 26,388 |
| All requests | 6.8 / 99.8 / 464 | 7.4 / 106.6 / 1,135 | 195 / 12,458 / 15,853 |
| API CPU | 75.0 (100.7) | 71.6 (129.7) | api-2 100.6 (106.3), api-3 42.0 (103.2) |
| PostgreSQL CPU | 67.0 (111.8) | 67.1 (299) | 127 (176) |
| Redis CPU | 1.0 (6.8) | 4.9 (77.7) | 3.0 (8.2) |
| nginx CPU | 7.4 | 8.5 | 6.6 |
| PG backends after the run | 100 idle (api) | 100 idle (api) | 53 + 31 idle |
| Thresholds | pass | fail (dropped iterations) | fail (errors, every p95, dropped) |

Configuration 2's tail and its dropped iterations come from one ~4 s burst at 17:40:13. In that single `docker stats` sample *every* container spiked at once: API 130%, PostgreSQL 200%, Redis 78%, nginx 55%, k6 75%. That is a host-level event on the laptop, not something the cache does. Without it, configurations 1 and 2 would be indistinguishable at p95.

### Capacity (max sustainable arrival rate)

| Configuration | Rate (it/s) | Served req/s | Errors | p50 / p95 / p99 (all, ms) | API CPU avg | PG CPU avg | Verdict |
|---|---|---|---|---|---|---|---|
| 1 replica, cache OFF | 40 | 124.1 | 0% | 5.8 / 11.8 / 16.7 | 41.5 | 35.6 | pass |
| 1 replica, cache OFF | 80 | 248.1 | 0% | 7.0 / 49.9 / 93.9 | 86.1 | 81.8 | pass |
| 1 replica, cache ON | 70 | 214.6 | 0% | 7.4 / 106.6 / 1,135 | 71.6 | 67.1 | fail (104 dropped, see above) |
| 2 replicas, cache ON, default pool (100) | 70 | 192.1 | 23.6% | 195 / 12,458 / 15,853 | 100.6 / 42.0 | 127 | **fail** |
| 2 replicas, cache ON, pool 45 | 100 | 310.0 | 0% | 8.1 / 62.8 / 86.2 | 68.8 / 73.1 | 98.7 | pass |
| 2 replicas, cache ON, pool 45 | 140 | 433.6 | 0% | 59.9 / 384.3 / 589.3 | 91.9 / 93.6 | 168.3 | fail (every p95 budget) |

Per endpoint for the two-replica (pool 45) steps:

| Endpoint | 100 it/s p50 / p95 / p99 | 140 it/s p50 / p95 / p99 |
|---|---|---|
| `GET /me` | 2.4 / 49.0 / 69.5 | 30.1 / 324.8 / 500.4 |
| `GET /conversations` | 12.0 / 68.4 / 89.5 | 84.5 / 400.1 / 606.9 |
| `GET /…/messages` | 7.1 / 63.0 / 79.8 | 60.2 / 381.2 / 584.1 |
| `POST /…/messages` | 19.9 / 99.1 / 173.1 | 121.8 / 653.5 / 1,067.9 |

Reading:

- **One replica:** ≥ 80 it/s (248 req/s). The search was not taken further (scope). At 80 it/s the API averaged 86% of its CPU, so the ceiling is close.
- **One replica with the cache:** only the matrix point exists, and it is spoiled by the host burst. Per your reduced scope it was not searched; given the cache's small effect it should be close to configuration 1.
- **Two replicas, default pool:** below 70 it/s, i.e. *worse* than one replica.
- **Two replicas, pool 45:** between 100 and 140 it/s (310–434 req/s). Every request still succeeded at 140 it/s, but p95 had grown 6×.

### Realtime delivery

2 replicas, cache ON, pool 45, k6 at 2.5% CPU:
- 50 SignalR WebSocket listeners: k6 speaks the SignalR JSON protocol directly (handshake, `0x1E` record separator, a ping every 10 s), with skip-negotiation;
- 10 REST message sends per second for 60 s, each into a conversation of a specific listener;
- nginx's log shows the 50 sockets split **25 / 25** over the two replicas, and the REST sends round-robin, so deliveries cross the Redis backplane.

| Sent | Reached the target listener | Lost | All deliveries (group fan-out) | Socket errors | p50 | p90 | p95 | p99 | max |
|---|---|---|---|---|---|---|---|---|---|
| 600 | 600 | 0 | 1,300 | 0 | 11 ms | 16 ms | 21 ms | 41 ms | 534 ms |

Latency is measured from just before the `POST` to the `MessageReceived` frame on the listener, in the same k6 process (one clock). It includes the REST request, the database insert, the domain event handler, the backplane publish and the WebSocket push.

## Interpretation

### What the cache changed

The cache covers three things: the security-stamp check on **every** authenticated request (Redis only, no local copy, by design so revocation is instant), the `GetMe` profile, and hub membership checks (not exercised here). `pg_stat_statements` for the two single-replica matrix runs:

| Statement | Cache OFF: calls / total DB ms | Cache ON: calls / total DB ms |
|---|---|---|
| Conversation list | 13,637 / 59,116 | 13,755 / 63,726 |
| Messages page | 13,437 / 7,367 | 13,555 / 7,508 |
| Security stamp `SELECT "IsDeleted", "SecurityStamp" …` | 41,880 / 682 | (not in top 10) |
| `GetMe` profile | 13,437 / 237 | (not in top 10) |
| `DISCARD ALL` (one per connection checkout) | 89,932 / 1,221 | 35,703 / 628 |

The cache removes about 55,000 statements per run and 60% of connection checkouts, which shows as a lower `/me` median (3.6 → 2.4 ms) and slightly lower API CPU (75 → 71.6%). But those queries were primary-key lookups worth under 1 s of database time against 59–64 s for the uncached conversation list. The stamp check also only moves from a PostgreSQL round trip to a Redis round trip. So on this workload the cache is not where the capacity comes from. It would matter more if the stamp or profile queries were expensive, or if the conversation list were cached.

### What the second replica changed

- **With defaults:** a collapse. The single replica alone ended every run holding **100 idle connections**, its full default pool, because Npgsql opens a new connection whenever every pooled one is busy and only prunes idle ones after 300 s. Short bursts (CPU-quota throttling, GC, k6 bursts) are enough to reach 100. With two replicas, api-3 got its connections first. api-2 then received **17,667 × `53300: sorry, too many clients already`** and 1,519 "connection pool has been exhausted" errors, while api-3 logged none. Every failed attempt cost a TCP connect, a backend fork and authentication on PostgreSQL (127% CPU), plus a large error log entry on api-2 (100% CPU). nginx kept sending api-2 half of the traffic, hence 23.6% errors and seconds of latency.
- **With the pool capped at 45:** load is balanced (69% / 73% API CPU at 100 it/s) and capacity grows from ≥ 80 to between 100 and 140 it/s. It does not double, for two reasons. PostgreSQL does the heavy part of every read (the conversation list), and its CPU grows with throughput: 99% at 310 req/s, 168% at 434 req/s. And PostgreSQL, k6 (up to 0.9 cores at 140 it/s) and both replicas compete for the same 6 physical cores.

### Limits of measuring on one laptop

- **Everything shares one machine.** k6 used 11–33% of a core on average, with peaks up to 0.9 cores in valid runs and 1.4 cores in the collapsed one. PostgreSQL is unlimited and competes with the API containers for the same physical cores. On real hardware PostgreSQL would have its own machine and the replicas would scale further.
- **Run-to-run variance in the tail is large.** Configuration 1 at 70 it/s (matrix) had p95 99.8 ms, while the same configuration at 80 it/s (capacity probe, a different run) had p95 49.9 ms. Medians were stable within about 1 ms across runs; treat p95/p99 differences under ~2× as noise.
- **The host interferes.** Windows background activity produced the 17:40:13 burst. Four earlier runs overlapped Modern Standby (the laptop went idle). A frozen Docker VM produced timeouts and, after resume, 100% 401s from expired 15-minute tokens. Those runs were detected from the Windows Kernel-Power log and discarded. `run.ps1` now prevents idle standby and marks any suspended run INVALID.
- **1-CPU quotas.** The quotas make each replica hit CFS throttling: average CPU of 70–86% hides 100% peaks, which is where the p99 tail comes from.
- **Single sample sizes.** Capacity steps used 60 s of steady load and the matrix 120 s, once each.

## Bottlenecks observed (not fixed)

1. **Connection pool size vs PostgreSQL `max_connections`.**
   - Npgsql's default `Maximum Pool Size` (100) per replica equals PostgreSQL's default `max_connections` (100). The API also connects as the superuser, so the reserved superuser slots don't help.
   - Evidence: 100 idle backends from the single replica after each single-replica run (`*.connections.txt`). psql itself was refused with "too many clients" after the first matrix run. Configuration 3 shows 17,667 × `53300` on one replica and 0 on the other.
   - Production docker-compose runs two replicas with these defaults.
   - Possible fixes: a per-replica `Maximum Pool Size` with replicas × pool < `max_connections` minus headroom, PgBouncer, a higher `max_connections`, or a non-superuser application role.
2. **The conversation list query.**
   - It takes 85–88% of the time spent in the top-10 statements (59.1 s of 69.5 s in matrix run 1), mean 4.3–5.2 ms, against 0.55 ms for a messages page.
   - `EXPLAIN (ANALYZE, BUFFERS)` for the user with the most conversations (11) shows 9.3 ms execution plus 2.6 ms planning. Of that:
     - **~6.5 ms** is the `OtherUser` sub-select: `ROW_NUMBER() OVER (PARTITION BY "ConversationId")` over a seq scan of **all 3,439 memberships** hash-joined to **all 1,011 users**, sorted (2,850 rows). Only then is it joined to the 11 conversations on the page. Its cost grows with the whole database, not with the user.
     - **~1.8 ms** is the unread `count(*)` (11 loops × 0.16 ms). The index range starts at `JoinedAtUtc`, because the `LastReadMessageAtUtc IS NULL OR …` predicate prevents using the read pointer. So it scans every visible message (83 on average), not just unread ones, and grows with chat history.
     - Every execution is planned again (2.6 ms), because the statement is not prepared.
3. **Development environment from `.env`.**
   - The local `.env` sets `ASPNETCORE_ENVIRONMENT=Development`, so `docker compose up` runs the API in Development.
   - At 20 it/s with caching off, one replica served only 33 req/s of ~62 offered, with p95 15.7 s, 278 dropped iterations and 99.5% CPU. Npgsql then timed out authenticating new connections.
   - The same load in Production: 61.7 req/s, p95 9.5 ms, 19.5% CPU, 0 dropped.
   - The cause was not isolated (Development enables, among other things, DI scope validation on every service resolution).
   - These two short probes (30 s steady) were run before the result folder was cleaned, so their numbers are from the console output, not from saved files. The load-test override now forces Production.
4. **SQL command logging in Production.**
   - `appsettings.json` keeps `Default: Information`, so EF Core logs every executed SQL command with its full text.
   - During a two-replica ramp-up (Production), each replica logged about 2,700 `Executed DbCommand` entries in one minute. In the Development probe one replica wrote 76,796 log lines (6 MB) in 20 minutes, 4,373 of them SQL command entries.
   - The CPU cost was not measured in isolation; on a 1-CPU replica it is not negligible.
5. **Several connection checkouts per request.** The stamp check and `GetMe` each open their own scope and DbContext, so one authenticated GET checks a connection out of the pool 2–3 times. Every checkout costs a `DISCARD ALL` round trip: 89,932 of them in matrix run 1, about 6.5 per iteration. Cheap per call, but it multiplies pool pressure, which feeds bottleneck 1.
6. **Tail latency under the 1-CPU quota.** At 70–80 it/s the replica's average CPU is 75–86% but its peaks hit 100%, and the p99 jumps from ~17 ms (40 it/s) to 90–500 ms. Part of this is the laptop setup (CFS throttling), part is real queueing.

## Caveats

- **Configuration-1 probes used an earlier `run.ps1`.**
  - The probes at 40 and 80 it/s did not recreate the replica before each run and had no standby guard.
  - Both were checked against the Windows Kernel-Power log afterwards and did not overlap a standby.
  - The 60 it/s step of the same search did overlap one and was discarded.
- **The matrix and the two-replica search used the final script:**
  - fresh replicas per configuration;
  - pool cleared after every run;
  - standby check.
- **Configuration 2 was not capacity-searched** (scope was reduced). The two-replica search was only run with the pool capped. With defaults it already fails at 70 it/s, below the first step.
- **Realtime was a single 60 s run at modest load** (50 sockets, 10 sends/s). It shows correctness and baseline latency across replicas, not realtime capacity.
