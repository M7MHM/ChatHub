# ChatHub performance

Measured with the k6 scripts in [loadtest/](../loadtest/README.md) against the docker-compose stack on one laptop. All numbers below are measured, from the k6 summaries and `docker stats` samples in `loadtest/results/` (gitignored) unless a line says otherwise.

The first part compares the fixes from 2026-11-22 with the original baseline. The baseline report follows unchanged under [Before: baseline (2026-11-21)](#before-baseline-2026-11-21). Same machine, same scripts, same thresholds and phases, same seed dataset, same per-replica limits (1 CPU, 512 MB).

## Before / After (2026-11-22)

- **Every matrix configuration now passes at 70 it/s.** Two replicas went from 23.6% errors and a p95 of 12.5 s to 0 errors and a p95 of 11 ms. Single-replica p95 dropped from ~100 ms to 7–8 ms.
- **One replica (cache on) sustains 100 it/s (310 req/s)** with p95 7 ms at 63% CPU (before: ≥ 80 it/s with p95 50 ms at 86%). It fails at 140 it/s, where its single CPU saturates.
- **Two replicas are at their edge at 140 it/s (434 req/s).** The search run failed there on p95 (277 ms for the conversation list). A repeat of the same step passed with p95 28 ms. Before, two replicas needed a manual pool override to work at all and failed at 140 it/s with p95 384 ms.
- **PostgreSQL does about a quarter of the work.** In configuration 1 the top-10 statements went from 69.5 s to ~18 s of database time per run, and PostgreSQL CPU from 67% to 22% of a core. The conversation list went from 4.33 ms to 0.92 ms mean execution, plus planning that is now skipped.
- **The pool can no longer exhaust PostgreSQL.** At most 30 backends per replica (before: 100, i.e. all of them), and 0 errors in every run.

### What changed

| # | Bottleneck | Change |
|---|---|---|
| 1 | Pool size vs `max_connections` | `docker-compose.yml` gives every api replica `Maximum Pool Size=${DB_MAX_POOL_SIZE:-30}` and starts PostgreSQL with `max_connections=${POSTGRES_MAX_CONNECTIONS:-100}`. The load-test override no longer replaces the connection string, and `run.ps1` no longer forces 100 (see [Connection budget](#connection-budget)). |
| 2 | Conversation list query | `ConversationListProjection`: the direct chat's other user is a correlated `LEFT JOIN LATERAL … LIMIT 1` per conversation on the page, and the unread count's lower bound is the later of visible-after and `LastReadMessageAtUtc`. No new index was needed (see [EXPLAIN](#conversation-list-explain-analyze-buffers)). |
| 2 | Planning on every execution | `Max Auto Prepare=${DB_MAX_AUTO_PREPARE:-50}` in the api connection string. `No Reset On Close` was evaluated and left off (see [Npgsql options](#npgsql-options-max-auto-prepare-and-no-reset-on-close)). |
| 4 | SQL logged per command | `appsettings.json`: `Microsoft.EntityFrameworkCore.Database.Command` = `Warning`; `appsettings.Development.json` keeps it at `Information`. During the re-measurement each Production replica wrote 32 log lines in 20 minutes, none of them SQL (before: ~2,700 SQL entries per replica per minute). |

Bottleneck 3 (Development from the local `.env`) is a local configuration issue and unchanged; the load-test override still forces Production. Bottleneck 5 (2–3 pool checkouts per request) is unchanged in code; its per-checkout reset was measured below. Bottleneck 6 (tail latency under the 1-CPU quota) is much smaller because each request needs less CPU: p99 at 70 it/s went from 464–1,135 ms to 14–48 ms.

### Same load: 70 iterations/s (≈217 req/s), 120 s steady

Latencies in ms (p50 / p95 / p99). CPU is the average (max) of `docker stats` samples, in % of one core. Before: pool 100 (Npgsql default), no prepared statements, SQL command logging on. After: compose defaults (pool 30, auto-prepare 50), no override.

| | 1. cache OFF, before | 1. cache OFF, after | 2. cache ON, before | 2. cache ON, after | 3. 2 replicas, before | 3. 2 replicas, after |
|---|---|---|---|---|---|---|
| Served req/s | 217.0 | 217.1 | 214.6 | 217.4 | 192.1 | 216.6 |
| Errors | 0.00% | 0.00% | 0.00% | 0.00% | **23.6%** | 0.00% |
| Dropped iterations | 0 | 0 | 104 (allowed 84) | 0 | 966 | 0 |
| `GET /me` | 3.6 / 89.5 / 498 | 2.7 / 5.2 / 36.4 | 2.4 / 90.8 / 1,477 | 1.5 / 3.0 / 30.3 | 2.9 / 729 / 1,239 | 1.6 / 5.2 / 39.3 |
| `GET /conversations` | 8.6 / 104.8 / 446 | 3.8 / 6.4 / 11.9 | 9.4 / 108.9 / 2,106 | 3.5 / 5.4 / 8.5 | 5,609 / 15,435 / 15,949 | 3.9 / 12.6 / 47.3 |
| `GET /…/messages` | 5.6 / 95.9 / 408 | 3.3 / 5.4 / 11.4 | 6.0 / 100.6 / 594 | 2.9 / 4.4 / 7.6 | 16.6 / 12,279 / 15,802 | 3.3 / 10.0 / 49.8 |
| `POST /…/messages` | 14.1 / 213.8 / 813 | 9.3 / 15.4 / 62.1 | 16.5 / 183.4 / 590 | 8.1 / 13.9 / 21.6 | 33.7 / 16,140 / 26,388 | 9.2 / 27.3 / 83.0 |
| All requests | 6.8 / 99.8 / 464 | 3.3 / 8.2 / 20.3 | 7.4 / 106.6 / 1,135 | 3.0 / 6.8 / 14.3 | 195 / 12,458 / 15,853 | 3.2 / 11.3 / 48.1 |
| API CPU | 75.0 (100.7) | 56.8 (99.9) | 71.6 (129.7) | 47.8 (98.1) | 100.6 (106.3) / 42.0 (103.2) | 30.8 (98.0) / 36.1 (86.2) |
| PostgreSQL CPU | 67.0 (111.8) | 22.3 (34.9) | 67.1 (299) | 14.7 (26.7) | 127 (176) | 20.5 (107.6) |
| Redis CPU | 1.0 (6.8) | 0.9 (4.1) | 4.9 (77.7) | 2.9 (7.3) | 3.0 (8.2) | 3.8 (19.9) |
| nginx CPU | 7.4 | 6.8 | 8.5 | 6.0 | 6.6 | 7.5 |
| PG backends after the run | 100 idle | 30 idle | 100 idle | 6 idle | 53 + 31 idle | 6 + 19 idle |
| Thresholds | pass | pass | fail (dropped) | pass | fail (errors, every p95, dropped) | pass |

The matrix was run twice after the fixes, because the first run could not overwrite three of its result files (see [Caveats](#caveats-for-the-re-measurement)); the table shows the second, complete run. The first run agrees within noise: all-request p95 7.9 / 6.2 / 5.7 ms, PostgreSQL CPU 21.6 / 14.5 / 14.0%, API CPU 57.7 / 51.1 / 29.3 + 29.8%, all thresholds passed. Configuration 3's higher p99 in the second run comes with a PostgreSQL CPU peak of 107.6% in one sample (first run: max 22.1%), i.e. a host burst like the one in the baseline.

### Capacity (max sustainable arrival rate)

After: compose defaults, cache ON, big steps (1 replica: 100, 140, …; 2 replicas: 140, 180, …), 60 s steady, stopping at the first failure.

| | Configuration | Rate (it/s) | Served req/s | Errors | p50 / p95 / p99 (all, ms) | API CPU avg | PG CPU avg | Verdict |
|---|---|---|---|---|---|---|---|---|
| Before | 1 replica, cache OFF | 40 | 124.1 | 0% | 5.8 / 11.8 / 16.7 | 41.5 | 35.6 | pass |
| Before | 1 replica, cache OFF | 80 | 248.1 | 0% | 7.0 / 49.9 / 93.9 | 86.1 | 81.8 | pass |
| Before | 1 replica, cache ON | 70 | 214.6 | 0% | 7.4 / 106.6 / 1,135 | 71.6 | 67.1 | fail (104 dropped) |
| Before | 2 replicas, cache ON, default pool (100) | 70 | 192.1 | 23.6% | 195 / 12,458 / 15,853 | 100.6 / 42.0 | 127 | **fail** |
| Before | 2 replicas, cache ON, pool 45 | 100 | 310.0 | 0% | 8.1 / 62.8 / 86.2 | 68.8 / 73.1 | 98.7 | pass |
| Before | 2 replicas, cache ON, pool 45 | 140 | 433.6 | 0% | 59.9 / 384.3 / 589.3 | 91.9 / 93.6 | 168.3 | fail (every p95 budget) |
| **After** | 1 replica, cache ON | 100 | 310.5 | 0% | 2.7 / 7.0 / 20.0 | 63.3 | 18.7 | pass |
| **After** | 1 replica, cache ON | 140 | 350.2 | 0% | 3,898 / 6,100 / 20,909 | 98.0 | 34.2 | fail (1,630 dropped, every p95) |
| **After** | 2 replicas, cache ON | 140 | 433.7 | 0% | 4.2 / 160.6 / 392.5 | 78.0 / 67.7 | 32.1 | fail (p95 conversations 276.7, messages 161.6) |
| **After** | 2 replicas, cache ON (repeat, outside the search) | 140 | 434.0 | 0% | 3.4 / 27.7 / 58.8 | 63.0 / 60.5 | 29.9 | pass |

Per endpoint after the fixes (p50 / p95 / p99, ms):

| Endpoint | 1 replica, 100 it/s | 1 replica, 140 it/s | 2 replicas, 140 it/s | 2 replicas, 140 it/s repeat |
|---|---|---|---|---|
| `GET /me` | 1.3 / 3.1 / 31.2 | 287 / 540 / 651 | 1.9 / 54.6 / 281.8 | 1.7 / 16.5 / 54.9 |
| `GET /conversations` | 3.2 / 5.3 / 10.9 | 4,569 / 6,023 / 6,208 | 6.1 / 276.7 / 418.9 | 4.0 / 39.4 / 58.7 |
| `GET /…/messages` | 2.8 / 4.4 / 9.4 | 4,385 / 5,967 / 6,216 | 5.0 / 161.6 / 389.4 | 3.8 / 15.0 / 57.0 |
| `POST /…/messages` | 8.2 / 13.7 / 79.4 | 18,958 / 25,408 / 27,316 | 13.0 / 203.0 / 706.5 | 10.3 / 67.5 / 81.9 |

Reading:

- **One replica:** between 100 and 140 it/s (310–434 req/s offered). At 100 it/s the replica used 63% of its CPU; 140 it/s needs more than its one core (98% average), so requests queue, k6 drops 1,630 iterations and latency reaches seconds. PostgreSQL is not the limit (34%). Before, the ceiling was "≥ 80 it/s" at 86% CPU, so the same replica now does more work for less CPU, mainly because SQL text is no longer logged and statements are prepared.
- **Two replicas:** about 140 it/s. The search's 140 it/s step failed, but its `docker stats` show both replicas at 75–100% CPU for the first ~45 s of the steady phase, then both dropping to 45–55% at the same moment (02:29:03) for the rest. 217 req/s per replica cost 48–57% in every other run, and PostgreSQL stayed at ~30% throughout. The repeat of the same step passed with p95 28 ms and 63% / 61% API CPU. Treat 140 it/s as the edge, not as comfortably sustainable. The search stopped at the first failure as specified, so higher steps were not tried.
- **PostgreSQL is no longer the shared bottleneck.** At 434 req/s it used 30–32% of a core, against 168% before at the same rate. The remaining limit is API CPU under the 1-CPU quotas, plus k6, nginx and PostgreSQL sharing the laptop with both replicas.

### Conversation list: EXPLAIN (ANALYZE, BUFFERS)

For `seed-0001`, the seed user with the most conversations (11: 5 direct, 6 groups), first page (`LIMIT 21`), with SQL text copied from EF Core's command log and the parameters inlined. Both plans were taken on the same data (63,469 messages, before the re-measurement runs), warm cache, repeated 5–6 times.

| | Before | After |
|---|---|---|
| Execution time | 5.3 ms shown (5.1–7.5 over 6 runs) | 1.4 ms shown (1.4–3.0 over 5 runs) |
| Planning time | 2.0–2.4 ms, on every execution | 2.0–2.8 ms when planned ad hoc; **0.016 ms** as a prepared statement with a generic plan (same plan shape, 1.4 ms execution) |
| Shared buffers hit | 1,248 | 1,034 |
| Other user (direct chats) | `WindowAgg` over a sort of a seq scan of **all 3,439 memberships** hash-joined to **all 1,011 users**: 3.6 ms, 100 buffers, grows with the whole database | `Index Only Scan` on `IX_ConversationMembers_ConversationId_UserId`: 2 index entries per direct chat, 0.006 ms per conversation; group chats skip it via a `One-Time Filter` on the type |
| Unread count | index range starts at visible-after: 126 entries read per conversation (43 filtered out) | range starts at the later of visible-after and the read pointer: 94 entries read (11 filtered out) |

`seed-0001` happens to have most of their messages unread, so the unread change is small for them. Across all 3,450 active memberships the unread count's index ranges shrink from **732,739 to 403,210 entries (−45%)**; 378,842 of those are actually unread, so the scan now reads little beyond what it counts.

The projection still produces a single SQL command. It returns the same results as before for every user: the list for all 1,011 users, paged with a page size of 3 (1,519 pages, so every cursor path is exercised), was byte-identical before and after the change. `ConversationListTests` and the rest of the suite pass unchanged. The other user needs no ordering: a direct chat always has exactly two different members (`Conversation.CreateDirect`, the unique `(ConversationId, UserId)` index, and `AddMember` rejects direct chats).

<details>
<summary>Before: full plan</summary>

```text
Sort  (cost=1558.02..1558.17 rows=63 width=1220) (actual time=5.022..5.036 rows=11 loops=1)
  Sort Key: s.c DESC, s."ConversationId" DESC
  Sort Method: quicksort  Memory: 27kB
  Buffers: shared hit=1248
  ->  Nested Loop Left Join  (cost=1237.20..1556.13 rows=63 width=1220) (actual time=4.351..5.007 rows=11 loops=1)
        Buffers: shared hit=1245
        ->  Merge Right Join  (cost=1236.62..1357.91 rows=63 width=1159) (actual time=4.313..4.882 rows=11 loops=1)
              Merge Cond: (c1."ConversationId" = s."Id0")
              Buffers: shared hit=1179
              ->  WindowAgg  (cost=378.58..455.93 rows=3439 width=597) (actual time=2.975..3.585 rows=581 loops=1)
                    Run Condition: (row_number() OVER (?) <= 1)
                    Buffers: shared hit=100
                    ->  Sort  (cost=378.56..387.15 rows=3439 width=585) (actual time=2.967..3.090 rows=2850 loops=1)
                          Sort Key: c1."ConversationId", c1."Id", u."Id"
                          Sort Method: quicksort  Memory: 419kB
                          Buffers: shared hit=100
                          ->  Hash Join  (cost=54.37..176.55 rows=3439 width=585) (actual time=0.408..1.416 rows=3439 loops=1)
                                Hash Cond: (c1."UserId" = u."Id")
                                Buffers: shared hit=100
                                ->  Seq Scan on "ConversationMembers" c1  (cost=0.00..113.12 rows=3439 width=48) (actual time=0.008..0.383 rows=3439 loops=1)
                                      Filter: ("UserId" <> '01a1fe64-50f7-7976-bd1c-f6e08f11b697'::uuid)
                                      Rows Removed by Filter: 11
                                      Buffers: shared hit=70
                                ->  Hash  (cost=40.83..40.83 rows=1083 width=553) (actual time=0.387..0.390 rows=1011 loops=1)
                                      Buckets: 2048  Batches: 1  Memory Usage: 89kB
                                      Buffers: shared hit=30
                                      ->  Seq Scan on "UserProfiles" u  (cost=0.00..40.83 rows=1083 width=553) (actual time=0.004..0.166 rows=1011 loops=1)
                                            Buffers: shared hit=30
              ->  Sort  (cost=858.04..858.07 rows=11 width=618) (actual time=1.247..1.254 rows=11 loops=1)
                    Sort Key: s."Id0"
                    Sort Method: quicksort  Memory: 26kB
                    Buffers: shared hit=1079
                    ->  Subquery Scan on s  (cost=58.41..857.85 rows=11 width=618) (actual time=0.489..1.230 rows=11 loops=1)
                          Buffers: shared hit=1079
                          ->  Limit  (cost=58.41..857.74 rows=11 width=618) (actual time=0.487..1.225 rows=11 loops=1)
                                Buffers: shared hit=1079
                                ->  Result  (cost=58.41..857.74 rows=11 width=618) (actual time=0.486..1.222 rows=11 loops=1)
                                      Buffers: shared hit=1079
                                      ->  Sort  (cost=58.41..58.44 rows=11 width=622) (actual time=0.191..0.195 rows=11 loops=1)
                                            Sort Key: (COALESCE(c0."LastMessageAtUtc", c0."CreatedAtUtc")) DESC, c."ConversationId" DESC
                                            Sort Method: quicksort  Memory: 26kB
                                            Buffers: shared hit=27
                                            ->  Hash Join  (cost=35.56..58.22 rows=11 width=622) (actual time=0.062..0.178 rows=11 loops=1)
                                                  Hash Cond: (c0."Id" = c."ConversationId")
                                                  Buffers: shared hit=27
                                                  ->  Seq Scan on "Conversations" c0  (cost=0.00..21.04 rows=604 width=580) (actual time=0.005..0.089 rows=604 loops=1)
                                                        Filter: (NOT "IsDeleted")
                                                        Buffers: shared hit=15
                                                  ->  Hash  (cost=35.42..35.42 rows=11 width=41) (actual time=0.029..0.031 rows=11 loops=1)
                                                        Buckets: 1024  Batches: 1  Memory Usage: 9kB
                                                        Buffers: shared hit=12
                                                        ->  Bitmap Heap Scan on "ConversationMembers" c  (cost=4.37..35.42 rows=11 width=41) (actual time=0.016..0.024 rows=11 loops=1)
                                                              Recheck Cond: ("UserId" = '01a1fe64-50f7-7976-bd1c-f6e08f11b697'::uuid)
                                                              Filter: "IsActive"
                                                              Heap Blocks: exact=10
                                                              Buffers: shared hit=12
                                                              ->  Bitmap Index Scan on "IX_ConversationMembers_UserId"  (cost=0.00..4.36 rows=11 width=0) (actual time=0.009..0.010 rows=11 loops=1)
                                                                    Index Cond: ("UserId" = '01a1fe64-50f7-7976-bd1c-f6e08f11b697'::uuid)
                                                                    Buffers: shared hit=2
                                      SubPlan 1
                                        ->  Aggregate  (cost=72.64..72.65 rows=1 width=4) (actual time=0.092..0.092 rows=1 loops=11)
                                              Buffers: shared hit=1052
                                              ->  Index Scan using "IX_Messages_ConversationId_CreatedAtUtc_Id" on "Messages" m0  (cost=0.42..72.62 rows=9 width=0) (actual time=0.018..0.087 rows=83 loops=11)
                                                    Index Cond: (("ConversationId" = c."ConversationId") AND ("CreatedAtUtc" > CASE WHEN ((c."ClearedAtUtc" IS NOT NULL) AND (c."ClearedAtUtc" > c."JoinedAtUtc")) THEN c."ClearedAtUtc" ELSE c."JoinedAtUtc" END))
                                                    Filter: ((NOT "IsDeleted") AND ("SenderId" <> '01a1fe64-50f7-7976-bd1c-f6e08f11b697'::uuid) AND ((c."LastReadMessageAtUtc" IS NULL) OR ("CreatedAtUtc" > c."LastReadMessageAtUtc")))
                                                    Rows Removed by Filter: 43
                                                    Buffers: shared hit=1052
        ->  Memoize  (cost=0.58..16.64 rows=1 width=93) (actual time=0.010..0.010 rows=1 loops=11)
              Cache Key: s."LastMessageId", s."ClearedAtUtc", s."JoinedAtUtc"
              Cache Mode: binary
              Hits: 0  Misses: 11  Evictions: 0  Overflows: 0  Memory Usage: 3kB
              Buffers: shared hit=66
              ->  Limit  (cost=0.57..16.63 rows=1 width=93) (actual time=0.009..0.009 rows=1 loops=11)
                    Buffers: shared hit=66
                    ->  Nested Loop  (cost=0.57..16.63 rows=1 width=93) (actual time=0.009..0.009 rows=1 loops=11)
                          Buffers: shared hit=66
                          ->  Index Scan using "PK_Messages" on "Messages" m  (cost=0.29..8.31 rows=1 width=73) (actual time=0.004..0.004 rows=1 loops=11)
                                Index Cond: ("Id" = s."LastMessageId")
                                Filter: ("CreatedAtUtc" > CASE WHEN ((s."ClearedAtUtc" IS NOT NULL) AND (s."ClearedAtUtc" > s."JoinedAtUtc")) THEN s."ClearedAtUtc" ELSE s."JoinedAtUtc" END)
                                Buffers: shared hit=33
                          ->  Index Scan using "PK_UserProfiles" on "UserProfiles" u0  (cost=0.28..8.29 rows=1 width=28) (actual time=0.003..0.003 rows=1 loops=11)
                                Index Cond: ("Id" = m."SenderId")
                                Buffers: shared hit=33
Planning:
  Buffers: shared hit=519
Planning Time: 1.984 ms
Execution Time: 5.334 ms
```

</details>

<details>
<summary>After: full plan</summary>

```text
Nested Loop Left Join  (cost=59.52..1071.26 rows=11 width=1219) (actual time=0.505..1.171 rows=11 loops=1)
  Buffers: shared hit=1034
  ->  Nested Loop Left Join  (cost=58.96..888.22 rows=11 width=1158) (actual time=0.481..1.090 rows=11 loops=1)
        Buffers: shared hit=968
        ->  Limit  (cost=58.39..857.49 rows=11 width=617) (actual time=0.476..1.012 rows=11 loops=1)
              Buffers: shared hit=942
              ->  Result  (cost=58.39..857.49 rows=11 width=617) (actual time=0.475..1.010 rows=11 loops=1)
                    Buffers: shared hit=942
                    ->  Sort  (cost=58.39..58.41 rows=11 width=621) (actual time=0.238..0.241 rows=11 loops=1)
                          Sort Key: (COALESCE(c0."LastMessageAtUtc", c0."CreatedAtUtc")) DESC, c."ConversationId" DESC
                          Sort Method: quicksort  Memory: 26kB
                          Buffers: shared hit=33
                          ->  Hash Join  (cost=35.56..58.20 rows=11 width=621) (actual time=0.084..0.208 rows=11 loops=1)
                                Hash Cond: (c0."Id" = c."ConversationId")
                                Buffers: shared hit=27
                                ->  Seq Scan on "Conversations" c0  (cost=0.00..21.04 rows=604 width=580) (actual time=0.007..0.099 rows=604 loops=1)
                                      Filter: (NOT "IsDeleted")
                                      Buffers: shared hit=15
                                ->  Hash  (cost=35.42..35.42 rows=11 width=57) (actual time=0.052..0.053 rows=11 loops=1)
                                      Buckets: 1024  Batches: 1  Memory Usage: 9kB
                                      Buffers: shared hit=12
                                      ->  Bitmap Heap Scan on "ConversationMembers" c  (cost=4.37..35.42 rows=11 width=57) (actual time=0.021..0.046 rows=11 loops=1)
                                            Recheck Cond: ("UserId" = '01a1fe64-50f7-7976-bd1c-f6e08f11b697'::uuid)
                                            Filter: "IsActive"
                                            Heap Blocks: exact=10
                                            Buffers: shared hit=12
                                            ->  Bitmap Index Scan on "IX_ConversationMembers_UserId"  (cost=0.00..4.36 rows=11 width=0) (actual time=0.010..0.010 rows=11 loops=1)
                                                  Index Cond: ("UserId" = '01a1fe64-50f7-7976-bd1c-f6e08f11b697'::uuid)
                                                  Buffers: shared hit=2
                    SubPlan 1
                      ->  Aggregate  (cost=72.62..72.63 rows=1 width=4) (actual time=0.069..0.069 rows=1 loops=11)
                            Buffers: shared hit=909
                            ->  Index Scan using "IX_Messages_ConversationId_CreatedAtUtc_Id" on "Messages" m0  (cost=0.42..72.55 rows=28 width=0) (actual time=0.006..0.063 rows=83 loops=11)
                                  Index Cond: (("ConversationId" = c."ConversationId") AND ("CreatedAtUtc" > CASE WHEN ((c."LastReadMessageAtUtc" IS NOT NULL) AND (c."LastReadMessageAtUtc" > CASE WHEN ((c."ClearedAtUtc" IS NOT NULL) AND (c."ClearedAtUtc" > c."JoinedAtUtc")) THEN c."ClearedAtUtc" ELSE c."JoinedAtUtc" END)) THEN c."LastReadMessageAtUtc" WHEN ((c."ClearedAtUtc" IS NOT NULL) AND (c."ClearedAtUtc" > c."JoinedAtUtc")) THEN c."ClearedAtUtc" ELSE c."JoinedAtUtc" END))
                                  Filter: ((NOT "IsDeleted") AND ("SenderId" <> '01a1fe64-50f7-7976-bd1c-f6e08f11b697'::uuid))
                                  Rows Removed by Filter: 11
                                  Buffers: shared hit=909
        ->  Memoize  (cost=0.57..8.18 rows=1 width=557) (actual time=0.006..0.006 rows=0 loops=11)
              Cache Key: c0."Type", c."ConversationId", c."UserId"
              Cache Mode: binary
              Hits: 0  Misses: 11  Evictions: 0  Overflows: 0  Memory Usage: 2kB
              Buffers: shared hit=26
              ->  Limit  (cost=0.56..8.17 rows=1 width=557) (actual time=0.005..0.005 rows=0 loops=11)
                    Buffers: shared hit=26
                    ->  Result  (cost=0.56..46.23 rows=6 width=557) (actual time=0.005..0.005 rows=0 loops=11)
                          One-Time Filter: (c0."Type" = 1)
                          Buffers: shared hit=26
                          ->  Nested Loop  (cost=0.56..46.23 rows=6 width=553) (actual time=0.010..0.010 rows=1 loops=5)
                                Buffers: shared hit=26
                                ->  Index Only Scan using "IX_ConversationMembers_ConversationId_UserId" on "ConversationMembers" c1  (cost=0.28..4.40 rows=6 width=16) (actual time=0.006..0.006 rows=1 loops=5)
                                      Index Cond: ("ConversationId" = c."ConversationId")
                                      Filter: ("UserId" <> c."UserId")
                                      Rows Removed by Filter: 1
                                      Heap Fetches: 0
                                      Buffers: shared hit=11
                                ->  Index Scan using "PK_UserProfiles" on "UserProfiles" u  (cost=0.28..6.96 rows=1 width=553) (actual time=0.004..0.004 rows=1 loops=5)
                                      Index Cond: ("Id" = c1."UserId")
                                      Buffers: shared hit=15
  ->  Limit  (cost=0.57..16.63 rows=1 width=93) (actual time=0.007..0.007 rows=1 loops=11)
        Buffers: shared hit=66
        ->  Nested Loop  (cost=0.57..16.63 rows=1 width=93) (actual time=0.006..0.006 rows=1 loops=11)
              Buffers: shared hit=66
              ->  Index Scan using "PK_Messages" on "Messages" m  (cost=0.29..8.31 rows=1 width=73) (actual time=0.003..0.003 rows=1 loops=11)
                    Index Cond: ("Id" = c0."LastMessageId")
                    Filter: ("CreatedAtUtc" > CASE WHEN ((c."ClearedAtUtc" IS NOT NULL) AND (c."ClearedAtUtc" > c."JoinedAtUtc")) THEN c."ClearedAtUtc" ELSE c."JoinedAtUtc" END)
                    Buffers: shared hit=33
              ->  Index Scan using "PK_UserProfiles" on "UserProfiles" u0  (cost=0.28..8.29 rows=1 width=28) (actual time=0.003..0.003 rows=1 loops=11)
                    Index Cond: ("Id" = m."SenderId")
                    Buffers: shared hit=33
Planning:
  Buffers: shared hit=579
Planning Time: 2.313 ms
Execution Time: 1.442 ms
```

</details>

No index was added: the new plan uses the existing `(ConversationId, UserId)` and `(ConversationId, CreatedAtUtc, Id)` indexes, so no migration was needed.

### Database time per run (configuration 1, `pg_stat_statements`, 120 s steady)

| Statement | Before: calls / mean ms / total ms | After: calls / mean ms / total ms |
|---|---|---|
| Conversation list | 13,637 / 4.33 / 59,116 | 13,859 / 0.92 / 12,802 |
| Messages page | 13,437 / 0.55 / 7,367 | 13,659 / 0.23 / 3,105 |
| Security stamp | 41,880 / 0.02 / 682 | 42,526 / 0.02 / 699 |
| Connection reset per checkout | 89,932 × `DISCARD ALL` / 1,221 | 90,944 × (`SET SESSION AUTHORIZATION DEFAULT`, `pg_advisory_unlock_all()`, `RESET ALL`, …) / ≥ 1,194 |

`mean ms` is execution only. Before, every list query was also planned (≈2 ms, not counted here); after, prepared statements skip planning. The dataset grows by ~1,400 messages per run, so the "after" runs read slightly more data (81,215 messages at the end against 63,469 when the EXPLAIN above was taken).

### Connection budget

```
api replicas × DB_MAX_POOL_SIZE + NOTIFICATIONS_DB_MAX_POOL_SIZE + headroom  ≤  POSTGRES_MAX_CONNECTIONS
          2  ×        30        +               5                +    35     ≤  100
```

- **Notifications worker:** at most four connections in use at once (consumer, dispatcher, retention cleanup, health check), so its pool is capped at 5.

- **Headroom** covers the migrator (1 connection), the seeder (1), psql / `pg_dump` / monitoring (a few), and a replica that is being recreated while the old one still holds its pool. The API connects as the PostgreSQL superuser, so `superuser_reserved_connections` adds nothing for it.
- **Why 30:** that is plenty for a 1-CPU replica. The most a replica held after any run was 30 (configuration 1 at 70 it/s, and the collapsed 140 it/s run, where the cap made requests wait instead of opening backends). At 434 req/s over two replicas they held 19 + 19 and 30 + 23. When every pooled connection is busy, Npgsql queues the request (up to the 15 s `Timeout`), which is cheaper than another PostgreSQL backend on a database that is already busy.
- **Scaling out:** with a third replica (3 × 30 = 90) raise `POSTGRES_MAX_CONNECTIONS` (e.g. to 130) or lower `DB_MAX_POOL_SIZE`, so the headroom stays. Past a few replicas, put PgBouncer in front of PostgreSQL instead of growing `max_connections`.
- All three values are environment variables with the defaults above (`docker-compose.yml`, `.env.example`). `run.ps1 -MaxPoolSize` still overrides the pool for experiments; without it the runs use the compose defaults.

### Npgsql options: Max Auto Prepare and No Reset On Close

Measured two ways: a client-side microbenchmark (one security-stamp lookup plus one conversation list, each on its own pooled connection like a request does; 4 interleaved rounds × 2,000 iterations per variant, against the compose PostgreSQL), and a load A/B run (1 replica, cache on, 80 it/s, 60 s steady, pool 30, new query; one run each).

| Variant | Microbench mean / p50 / p95 (ms) | Load: all p50 / p95 / p99 (ms) | Load: API CPU | Load: PostgreSQL CPU |
|---|---|---|---|---|
| Neither | 6.10 / 5.81 / 8.69 | 4.2 / 10.5 / 35.3 | 60.8 | 35.7 |
| `Max Auto Prepare=50` | 4.04 / 3.81 / 5.93 | 3.1 / 5.3 / 10.3 | 57.2 | **16.3** |
| Both | 3.66 / 3.46 / 5.41 | 3.4 / 9.3 / 31.3 | 67.3 | 15.2 |
| `No Reset On Close` only | 5.25 / 5.06 / 7.37 | – | – | – |

**`Max Auto Prepare`: enabled (50).**
- **Measured benefit:** it halves PostgreSQL CPU at the same load (35.7% → 16.3%) and cuts the microbenchmark by a third. PostgreSQL no longer plans the conversation list (≈2 ms) and the messages page on every execution.
- **Safe with EF Core:**
  - EF Core sends fully parameterized SQL, so the same few statement texts repeat. Npgsql prepares a statement after its fifth use and keeps at most 50 per physical connection (LRU).
  - The generic plan PostgreSQL then uses for the list query has the same shape as the custom plan (checked with `plan_cache_mode = force_generic_plan`: 0.016 ms planning, 1.4 ms execution).
  - All load runs since the change had 0 errors: over 270,000 requests in the measured steady phases alone, including ~16,000 message sends in transactions. The test suite does not exercise auto-prepare, because its connection strings come from Testcontainers.
- **Caveats:**
  - A migration that changes the columns of a table while replicas are running can make their prepared statements fail with "cached plan must not change result type". Here the migrator finishes before the API starts; after a live migration, restart the replicas.
  - PgBouncer in transaction mode needs its prepared-statement support (`max_prepared_statements`, 1.21+).
  - Set `DB_MAX_AUTO_PREPARE=0` to turn it off.

**`No Reset On Close`: not enabled.**
- **Why not:**
  - It skips the reset that Npgsql runs when a pooled connection is handed out again. With auto-prepare that reset is no longer `DISCARD ALL` (which would drop the prepared statements) but a sequence (`SET SESSION AUTHORIZATION DEFAULT`, `RESET ALL`, `pg_advisory_unlock_all()`, …).
  - Npgsql sends that sequence together with the next command, so it costs no extra round trip. It does cost PostgreSQL time: about 0.5 s per minute at 80 it/s in the A/B run, roughly 1% of a core. That is the most it could save.
  - The load A/B shows exactly that: PostgreSQL 16.3% → 15.2%, and latencies within run-to-run noise (p95 actually higher). The microbenchmark gain (~0.2 ms per checkout) does not show up under load.
- **The price:** session state would leak between requests. The code does not use `SET`, temp tables, advisory locks or `LISTEN` today, so it would be correct now, but any future use would silently leak into unrelated requests.
- **Revisit** if the reset ever shows up as a real cost, e.g. by making the stamp check and `GetMe` share the request's DbContext (bottleneck 5), which removes checkouts rather than their reset.

### Caveats for the re-measurement

- **Order of runs:**
  1. the A/B runs;
  2. matrix run 1;
  3. the 1-replica search;
  4. the 2-replica search;
  5. the 2-replica 140 it/s repeat;
  6. matrix run 2.
  
  None overlapped a standby (no `INVALID` file). The laptop ran load for about 90 minutes in a row; thermal throttling was not measured.
- **Matrix run 1 lost three files per configuration** (`stats.csv`, `connections.txt`, `pgstat.txt`): Windows refused to overwrite the baseline files of the same name ("Access to the path is denied"). A few minutes later they were writable again, and the cause was not found. Its k6 summaries and CPU averages were written and are quoted above. The baseline's files are kept in `loadtest/results/before-2026-11-21/`, matrix run 1 in `loadtest/results/after-matrix-run1/`.
- **The 140 it/s repeat for two replicas is outside the protocol** (stop at the first failure). It is there to show that the failure did not reproduce, not to claim a higher ceiling. Steps above 140 it/s were not run for two replicas.
- **Single samples.** Every capacity step and A/B variant ran once; treat p95/p99 differences under ~2× as noise, as in the baseline.

### Commands (after)

```powershell
docker compose -f docker-compose.yml -f docker-compose.loadtest.yml build api migrator

# Npgsql options A/B (1 replica, 80 it/s); DB_MAX_AUTO_PREPARE is pasted into the connection string, so it can carry a second option
$env:DB_MAX_AUTO_PREPARE = '0';                            powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode single -Replicas 1 -Caching true -Rate 80 -Name ab-none-r1-80 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5
$env:DB_MAX_AUTO_PREPARE = '50';                           powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode single -Replicas 1 -Caching true -Rate 80 -Name ab-prepare-r1-80 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5
$env:DB_MAX_AUTO_PREPARE = '50;No Reset On Close=true';    powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode single -Replicas 1 -Caching true -Rate 80 -Name ab-both-r1-80 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5
Remove-Item Env:DB_MAX_AUTO_PREPARE

# same-load matrix, compose defaults
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode matrix -Rate 70 -RampUp 20 -Warmup 60 -Steady 120 -RampDown 10

# capacity, compose defaults, stop at the first failure
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode max -Replicas 1 -Caching true -StepRates 100,140,180,220,260 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode max -Replicas 2 -Caching true -StepRates 140,180,220,260,300,340,380 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode single -Replicas 2 -Caching true -Rate 140 -Name max-r2-cache-true-140-repeat -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5
```

The A/B runs themselves were made before `DB_MAX_AUTO_PREPARE` existed, by appending the options to `DB_MAX_POOL_SIZE` (e.g. `30;Max Auto Prepare=50`), which produces the same connection string.

### Outbox impact (2026-11-22)

Transactional outbox + RabbitMQ publisher ([MESSAGING.md](MESSAGING.md)). 2 replicas, cache ON, 70 it/s, `read-heavy+realtime` (`RT_DURATION=185`, so the realtime senders cover the whole steady phase), same phases as the matrix. A temporary durable queue was bound to `#` so events were routable, as they will be once the notification worker exists. Single runs; the control (`MESSAGING_ENABLED=false`) ran on the same stack right before the messaging run.

| p95 (ms) | Matrix config 3, read-heavy only | Control, messaging off | Messaging on |
|---|---|---|---|
| `GET /me` | 5.2 | 3.7 | 3.8 |
| `GET /conversations` | 12.6 | 18.0 | 24.7 |
| `GET /…/messages` | 10.0 | 7.2 | 7.8 |
| `POST /…/messages` | 27.3 | 22.1 | 50.9 |
| All requests (p99) | 11.3 (48.1) | 10.6 (41.2) | 14.6 (61.2) |
| Realtime delivery p95 | – | 58 | 63 |
| API / PostgreSQL / RabbitMQ CPU | 31–36 / 20.5 / – | 41 / 21.6 / 1.1 | 43–45 / 27.2 / 1.5 |

0 errors and all thresholds passed in every run. 3,262 events were published exactly once, none retried; publish lag p50 417 ms, p95 907 ms (the 1 s poll interval). Per message sent, PostgreSQL does one enrichment query (0.43 ms mean) and one outbox INSERT (0.06 ms); the publisher's claim and update statements are not in the top 10. The send p95 rose 2.3× (22 → 51 ms), just above the ~2× noise band of single runs: likely a real but small cost of the extra query, INSERT and serialization on the send path, not confirmed by repeats. Reads are unchanged within noise. A first messaging run, with `rabbitmq-diagnostics` as the compose health check, measured an all-request p95 of 28.9 ms: every probe started an Erlang VM using up to 2.4 cores for a second or two, every ~12 s. The health check is now a TCP probe and RabbitMQ stays under 5% CPU.

### Notification service impact (2026-11-22)

Same scenario and parameters as the outbox runs above. The [notification service](NOTIFICATIONS.md) ran with production timings (120 s delay, 15 min cooldown) after the backfill, so every seeded recipient is known and has an email address. Single runs.

| p95 (ms) | Control, messaging off | Messaging on (phase 1) | Notifications running | Notifications stopped |
|---|---|---|---|---|
| `GET /me` | 3.7 | 3.8 | 34.2 | 3.9 |
| `GET /conversations` | 18.0 | 24.7 | 47.8 | 17.0 |
| `GET /…/messages` | 7.2 | 7.8 | 43.2 | 8.3 |
| `POST /…/messages` | 22.1 | 50.9 | 99.3 | 29.6 |
| All requests (p99) | 10.6 (41.2) | 14.6 (61.2) | 45.2 (102.3) | 12.9 (41.5) |
| Realtime delivery p95 | 58 | 63 | 96 | 44 |
| API / PostgreSQL CPU | 41 / 21.6 | 43–45 / 27.2 | 55–56 / 43.7 | 41–42 / 25.8 |
| Notifications / Mailpit CPU | – | – | 28.2 (max 162) / 7.5 | stopped / 0.3 |

0 errors, 0 lost realtime messages and all thresholds passed in every run.

- **The API's own messaging cost is small.** With the worker stopped (events queue in RabbitMQ), the send p95 is 1.34× the control. Per send, the API does one enrichment query (0.52 ms) and one outbox INSERT (0.07 ms); the publisher's claims are not in the top 10. The 2.3× of the phase 1 run did not reproduce, so it was most likely run-to-run noise.
- **The slowdown with the worker running is contention on the shared laptop and PostgreSQL server.** `GET /me` touches neither the outbox nor RabbitMQ, and its p95 went from 3.9 to 34 ms.
  - During the run the worker consumed 4,202 events. Its fold statement (0.74 ms mean) was the third-largest consumer of database time on the server.
  - It then sent 2,440 digests covering 51,086 messages, 2,063 of them in one minute, when the warm-up's digests came due. Each email opened its own SMTP connection.
  - This load test is close to the worst case: every recipient has an email address and nobody reads anything.
- Every run also shows the API ~20 points busier in the first 40 s of the steady phase than in the rest (warm-up after the replicas are recreated), so differences under ~2× remain noise.
- Not changed in code. The fixes are operational: give the worker its own CPU (another host, or a CPU limit like the API replicas'). If it must share, rate-limit sends or reuse one SMTP connection per batch; see [NOTIFICATIONS.md](NOTIFICATIONS.md).

### Notification limits and new events (2026-11-23)

The same scenario again (2 replicas, cache ON, 70 it/s, `read-heavy+realtime`, `RT_DURATION=185`, same phases), after the notification pass that added the sending limits (20 emails a minute, 15 of them digests; 250 a day, 200 digests), the reused SMTP connection, the worker's 0.25 CPU / 192M limit and pool of 5, and the new events (`message.deleted`, `message.edited`, leave/kick, mute, `user.status-changed`). The 1,000 seed users were marked verified and backfilled first (`--verify-seeded --backfill-notifications`), and the worker ran as Development so their digests were allowed (`NOTIFICATIONS_ENVIRONMENT=Development`), with production timings (120 s delay, 15 min cooldown). Result files `notif3-r2-70`, `notif3b-r2-70`, `notif3-stopped-r2-70`.

| p95 (ms) | 11-22 control, messaging off | 11-22 worker running | 11-22 worker stopped | A: worker, seed mail suppressed | **B: worker sending, limited** | C: worker stopped, right after B |
|---|---|---|---|---|---|---|
| `GET /me` | 3.7 | 34.2 | 3.9 | 34.3 | 78.5 | 1,397.7 |
| `GET /conversations` | 18.0 | 47.8 | 17.0 | 43.4 | 103.1 | 1,741.9 |
| `GET /…/messages` | 7.2 | 43.2 | 8.3 | 27.5 | 93.1 | 919.6 |
| `POST /…/messages` | 22.1 | 99.3 | 29.6 | 75.1 | 198.6 | 817.2 |
| All requests (p99) | 10.6 (41.2) | 45.2 (102.3) | 12.9 (41.5) | 37.2 (1,343.9) | 95.8 (221.1) | 1,226.4 (3,407.3) |
| Dropped iterations | – | – | – | 83 | 0 | 424 |
| Realtime delivery p95 | 58 | 96 | 44 | 55 | 308 | 3,555 |
| API / PostgreSQL CPU (avg %) | 41 / 21.6 | 55–56 / 43.7 | 41–42 / 25.8 | 45 / 35.3 | 61–66 / 50.8 | 90–96 / 65.7 |
| Worker CPU avg (max) / memory | – | 28.2 (162) / – | stopped | 5.9 (18.9) / 78 MiB | 11.5 (26.0) / 87 MiB (max 94) | stopped |
| Emails sent | – | 2,440 digests, 2,063 in one minute | – | 0 | 15 digests a minute | – |
| Thresholds | passed | passed | passed | passed | realtime p95 failed | 6 failed |

0 errors and 0 lost realtime messages in every run.

- **Run A is invalid for the worker comparison.** `.dockerignore` keeps `appsettings.Development.json` out of the image, so the worker in Development fell back to the reserved-domain list and suppressed every `@chathub.test` recipient: it consumed all 3,343 events but created no digest. All containers also spiked together at two moments (API at its 1-CPU cap, PostgreSQL 164%, Redis and nginx too), a host hiccup behind the 6.4 s maximum and the 83 dropped iterations. The default is now decided from the environment in code (see [NOTIFICATIONS.md](NOTIFICATIONS.md#suppressed-domains)), and run B is the re-run.
- **The host was not in the state of 11-22.** Run C, with the worker stopped, ran right after B and was 13× slower than B: the API needed 90–96% CPU for the same requests that took 41% in the 11-22 stopped run, k6 itself peaked at 5.2 cores, and the laptop had 1.3 GB of 15.7 GB RAM free with desktop applications open. The API latencies of this session therefore cannot be compared with the 11-22 baselines, nor reliably with each other; whether "worker running" now matches "worker stopped" could not be confirmed or refuted on this machine today. Repeat B and C back to back on an idle machine before drawing that conclusion.
- **What the runs do show about the worker:**
  - It stayed within its limit: 11.5% CPU on average with 5 of 58 samples at the 0.25 CPU cap (26%), against 28.2% average and 162% peaks in the 11-22 run without a limit; memory 87 MiB average, 94 MiB at most, of 192M.
  - The sending limit held: exactly 15 digests a minute during and after the run, instead of 2,063 in one minute. 2,347 due digests were still waiting at the end; with 200 digests a day, most of them would expire unsent after 24 h, which is the intended behaviour on Brevo's free tier (and `/health/ready` reports `sending` Degraded after 30 minutes).
  - One reused SMTP connection: Mailpit used 0.5% CPU, against 7.5% (max 29.5%) when every email opened its own connection.
  - In PostgreSQL the worker's fold statement took 0.80 ms on average (2.5 s in total, 0.74 ms in the 11-22 run); the new statements (message snapshot upsert, usage count, presence and per-recipient queries) are not in the top 10 (each under 0.5 s in total).
- **Presence volume:** 100 `UserStatusChanged` events per run, one per connect and one per disconnect of the 50 realtime listeners, against 3,201 `MessageSent`: 3% of the outbox and broker traffic. Read-heavy users do not open sockets, and the events are raised only on first-connection / last-disconnection transitions, not per request.

---

# Before: baseline (2026-11-21)

The original report, unchanged except for headings. "Bottlenecks observed" lists the state before the fixes above.

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

## Bottlenecks observed (before the fixes)

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
