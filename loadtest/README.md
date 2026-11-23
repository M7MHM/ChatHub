# Load tests

k6 scripts that run inside the compose network against nginx (`http://nginx:8080`), so they exercise the same path real
clients do: nginx → API replica(s) → PostgreSQL / Redis. Results and the measured numbers are in
[docs/PERFORMANCE.md](../docs/PERFORMANCE.md).

## 1. Seed the database

```bash
docker compose --profile seed run --rm seeder --confirm            # 1000 users, 600 chats, 50k messages
docker compose --profile seed run --rm seeder --confirm --reset    # delete seed data first, then seed again
```

Seeded users sign in as `seed-0001@chathub.test` … `seed-1000@chathub.test` with the password `Seed12345`.
A second run without `--reset` changes nothing.

### Seeded users and the notification service

The notification service only emails verified addresses and only knows users it received events for. The seeder writes
rows directly (no events), and data seeded before email verification existed is unverified. One run fixes both:

```bash
docker compose --profile seed run --rm seeder --confirm --verify-seeded --backfill-notifications
```

- `--verify-seeded` marks seed users (`@chathub.test`) whose email is not verified yet as verified at their creation
  time. No other user is touched. New seeds are created verified, so a fresh database only needs the backfill.
- `--backfill-notifications` then queues one `UserProfileUpdated` per user through the outbox (verified flag, email
  preference, and an unsubscribe link for users that have none), so the worker learns about every user.

On the dev database: 1,000 seed users verified (the 19 other users unchanged), 1,019 events queued with 1,017 new
unsubscribe links in 3.3 s, and all 1,000 seed users known to the worker as verified about 20 s later.

The worker never emails reserved domains such as `.test` in Production. For load tests run it as Development, so seeded
users' digests reach Mailpit, and keep in mind the [sending limits](../docs/NOTIFICATIONS.md#sending-limits) (20 a minute,
250 a day by default; `NOTIFICATIONS_MAX_*` raise them):

```powershell
$env:NOTIFICATIONS_ENVIRONMENT = 'Development'
```

## 2. Start the stack with the load-test override

`docker-compose.loadtest.yml` raises the auth rate limits (k6 logs in hundreds of users from one IP), limits every API
replica to 1 CPU and 512 MB, enables `pg_stat_statements` in PostgreSQL and adds the `k6` service (profile `loadtest`).

```powershell
$env:CACHING_ENABLED = 'true'      # 'false' runs the same code with a pass-through HybridCache
docker compose -f docker-compose.yml -f docker-compose.loadtest.yml up -d --scale api=1
```

## 3. Run k6

```bash
docker compose -f docker-compose.yml -f docker-compose.loadtest.yml --profile loadtest run --rm \
  -e SCENARIO=read-heavy -e RATE=30 -e RESULT_NAME=my-run k6 run -q chathub.js
```

| Variable | Default | Meaning |
|---|---|---|
| `SCENARIO` | `read-heavy` | `read-heavy`, `realtime`, or `read-heavy+realtime` |
| `USERS` | `200` | Seeded users logged in during `setup()` |
| `RATE` | `30` | read-heavy iterations per second in the steady phase (each iteration is 3 GETs, plus a POST 10% of the time) |
| `RAMP_UP` / `WARMUP` / `STEADY` / `RAMP_DOWN` | `30` / `30` / `120` / `15` | Phase lengths in seconds. Only the `steady` phase is measured and checked against thresholds |
| `WRITE_RATIO` | `0.1` | Share of iterations that also send a message |
| `LISTENERS` / `RT_RATE` / `RT_DURATION` | `50` / `10` / `120` | realtime: SignalR sockets, messages sent per second, send phase length |
| `P95_ME` / `P95_CONVERSATIONS` / `P95_MESSAGES` / `P95_SEND` / `P95_DELIVERY` | see `chathub.js` | p95 budgets in ms |

Each run writes `results/<RESULT_NAME>.json` (full k6 summary) and `results/<RESULT_NAME>.summary.json` (the numbers
used in the report). `results/` is gitignored.

## 4. Comparison matrix and max throughput

`run.ps1` does, for every run:

- recreates the API containers for the requested configuration (fresh connection pools) and waits until nginx reaches every replica;
- runs k6, then samples `docker stats` during the steady phase only (`<name>.stats.csv`, `<name>.cpu.json`);
- records the PostgreSQL backends per client right after the run (`<name>.connections.txt`), restarts the API so psql can get a connection, and saves the top SQL statements from `pg_stat_statements` (`<name>.pgstat.txt`);
- keeps Windows from entering idle standby while it runs, and if the machine still suspended during a run (lid closed), writes `<name>.INVALID.txt` and prints a red warning. Discard such runs: a frozen Docker VM produces timeouts and expired tokens, not real numbers.

`-StopNotifications` stops the notification worker after configuring the stack, for a control run without it (events
then wait in RabbitMQ). The notification runs of 2026-11-23 used:

```powershell
$env:NOTIFICATIONS_ENVIRONMENT = 'Development'
powershell -ExecutionPolicy Bypass -File loadtestun.ps1 -Mode single -Replicas 2 -Caching true -Scenario 'read-heavy+realtime' -Rate 70 -RampUp 20 -Warmup 60 -Steady 120 -RampDown 10 -ExtraEnv 'RT_DURATION=185' -Name notif3b-r2-70
powershell -ExecutionPolicy Bypass -File loadtestun.ps1 -Mode single -Replicas 2 -Caching true -Scenario 'read-heavy+realtime' -Rate 70 -RampUp 20 -Warmup 60 -Steady 120 -RampDown 10 -ExtraEnv 'RT_DURATION=185' -Name notif3-stopped-r2-70 -StopNotifications
```

`-MaxPoolSize` overrides Npgsql's `Maximum Pool Size` per API replica. Without it the compose default applies
(`DB_MAX_POOL_SIZE`, 30, sized against PostgreSQL's `max_connections`; see docker-compose.yml and docs/PERFORMANCE.md).

```powershell
# same load on: 1 replica no cache, 1 replica cache, 2 replicas cache
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode matrix -Rate 70 -RampUp 20 -Warmup 60 -Steady 120 -RampDown 10

# step the arrival rate up until a threshold breaks
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode max -Replicas 2 -Caching true -StepRates 100,140,180 -RampUp 20 -Warmup 60 -Steady 60 -RampDown 5

# realtime delivery latency (2 replicas, so senders and listeners are spread over both)
powershell -ExecutionPolicy Bypass -File loadtest\run.ps1 -Mode single -Replicas 2 -Caching true -Scenario realtime -Name realtime-r2-cache -ExtraEnv "RT_DURATION=60" -RampUp 15 -Warmup 0 -Steady 60
```

Restore the normal stack afterwards with `docker compose up -d` (base file only: 2 replicas, default limits).
