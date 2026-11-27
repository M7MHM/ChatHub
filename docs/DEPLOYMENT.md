# Deploying ChatHub

ChatHub runs on one small DigitalOcean droplet next to another project (ParkingService), behind a shared Caddy proxy that terminates HTTPS. The frontend (Next.js) is on Vercel and talks to the API over HTTPS and WebSockets. Images come from GHCR, built by [CI](CI.md). Email goes through Brevo's SMTP relay.

Everything here was rehearsed locally with the production compose file, the real deploy scripts and k6 load ([Local rehearsal](#local-rehearsal)); nothing has been deployed to a real server yet.

- [Architecture](#architecture)
- [Memory budget](#memory-budget)
- [Server setup from zero](#server-setup-from-zero)
- [DNS](#dns)
- [Brevo (email)](#brevo-email)
- [Images: GHCR access](#images-ghcr-access)
- [First deploy](#first-deploy)
- [Frontend on Vercel](#frontend-on-vercel)
- [Deploying and rolling back](#deploying)
- [Migrations: expand and contract](#migrations-expand-and-contract)
- [Continuous deployment](#continuous-deployment)
- [Backups](#backups)
- [Operations](#operations)
- [When something breaks](#when-something-breaks)
- [Adding ParkingService](#adding-parkingservice)
- [Local rehearsal](#local-rehearsal)

## Architecture

```text
 browser (Vercel frontend)                               Brevo SMTP relay
        │ HTTPS + WebSocket (wss://<host>/hubs/chat)            ▲ 587 STARTTLS
        ▼                                                       │
 ┌─ edge (deploy/edge) ───────────┐   network "edge"   ┌─ chathub-prod (docker-compose.prod.yml) ──────────────┐
 │ Caddy :80 :443  172.31.0.2     │──────────────────▶│ api ×1..2 ──┬── postgres (chathub, chathub_notifications)│
 │ TLS, HSTS, zstd/gzip, logs     │  upstream list     │  (alias     ├── redis (cache, backplane, presence)       │
 │ ParkingService site (later)    │  written by        │  chathub-api)└── rabbitmq ── notifications worker ───────┘
 └────────────────────────────────┘  deploy/deploy.sh  └──── network "backend" (internal, no internet) ───────────┘
```

- **Only Caddy publishes ports** (80, 443/tcp, 443/udp). PostgreSQL, Redis, RabbitMQ and the API are on the internal `backend` network, which has no route to the internet; the worker also joins an `egress` network to reach Brevo. Nothing else is reachable from outside.
- **Caddy proxies straight to the API replicas.** The nginx container of the dev stack is not used in production; it would only add a hop. Load balancing is `least_conn` without sticky sessions, which is correct because SignalR state lives in Redis (backplane and connection tracker, see [PERFORMANCE.md](PERFORMANCE.md)) and the frontend connects with WebSockets and `skipNegotiation` ([Frontend on Vercel](#frontend-on-vercel)).
- **Why an explicit upstream list instead of Docker DNS.** Caddy can discover replicas through Docker DNS (`dynamic a`), but its active health checks [do not run for dynamic upstreams](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy), and Docker only drops a container from DNS once it has stopped. A replica could therefore not be taken out of rotation *before* it stops, so requests in flight to it during a deploy could fail. Instead `deploy/deploy.sh` writes the names of the healthy replicas to `deploy/edge/upstreams/chathub.caddy` and reloads Caddy gracefully (in-flight requests finish, WebSockets are kept for up to an hour). Static upstreams also get active health checks (`/health/live` every 10 s). In the rehearsal this gave 0 failed requests during every rolling deploy.
- **Real client IPs.** Caddy has a fixed address on the edge network (`EDGE_PROXY_IP`, default 172.31.0.2), outside the range Docker hands out. The API trusts `X-Forwarded-For` only from that address, and Caddy drops any `X-Forwarded-*` a client sends. So the per-IP rate limits see the real client and cannot be bypassed with a forged header (rehearsed: 260 logins with rotating forged `X-Forwarded-For` were still limited). The limits are kept per API replica in memory, so with 2 replicas a client effectively gets up to twice the configured limit.
- **What Caddy does not expose:** `/health/ready` and `/health` (they would reveal dependency status), and `/openapi/*` and `/scalar` unless `CHATHUB_DOCS_PUBLIC=true` in `deploy/edge/.env` *and* `OpenApi__Enabled` (`CHATHUB_DOCS_PUBLIC=true` in `.env.prod`). `/health/live` stays public for uptime monitors.
- **Access logs** go to Caddy's stdout as JSON, with the query string removed (`?access_token=` of WebSocket connections, verification and unsubscribe tokens) and without the Referer header. Authorization and Cookie headers are redacted by Caddy by default.
- **Secrets** live only in `.env.prod` (mode 600) and `deploy/edge/.env`. The compose file contains `${...}` references only. The migrators read their connection string from an environment variable, not a command-line argument, so it does not appear in `ps` on the host.

## Memory budget

Measured locally under the production compose file (Docker Desktop, 2026-11-25), with 1,000 seeded users and 50,000 messages, 2 API replicas, and k6 sending 20 iterations/s (about 63 requests/s, 10% writes) plus 20 WebSocket listeners through Caddy, during rolling deploys. `docker stats` samples every 5–8 s; "under load" is the highest sample.

| Container | Limit | Idle | Under load | Notes |
|---|---:|---:|---:|---|
| caddy (edge) | 96 MiB | 19 MiB | 51 MiB | shared with ParkingService |
| api (per replica) | 256 MiB | 127–135 MiB | 167 MiB | workstation GC (`DOTNET_gcServer=0`), heap capped at 75% of the limit |
| notifications | 160 MiB | 85 MiB | 92 MiB | |
| postgres | 256 MiB | 110 MiB | 220 MiB | `shared_buffers` 64 MB, `work_mem` 2 MB, `max_connections` 50; part of this is reclaimable page cache |
| rabbitmq | 256 MiB | 134 MiB | 137 MiB | memory alarm at 160 MiB; with the management plugin |
| redis | 64 MiB | 9 MiB | 12 MiB | `maxmemory 32mb`, `volatile-lru` |
| migrators | 256 MiB | – | 20 MiB | run for a few seconds per deploy |
| **ChatHub, 1 API replica** | 1,088 MiB | **~490 MiB** | **~680 MiB** | +~145 MiB while a deploy runs a second replica |
| **ChatHub, 2 API replicas** | 1,344 MiB | ~620 MiB | ~845 MiB | +~145 MiB during a deploy |

On a 2 GB droplet about 1.9 GiB is usable, and Ubuntu with Docker and containerd needs roughly 300 MiB (an estimate, not measured on a droplet). That leaves for ParkingService about **900 MiB with 1 API replica**, or about 750 MiB with 2, under load and outside deploys. The 2 GB swap file only absorbs short peaks.

**Default: 1 API replica (`API_REPLICAS=1`).** A second replica on a 1-vCPU droplet adds no CPU capacity (both share the same core); it only covers a crashed replica, and zero-downtime deploys do not need it because `deploy.sh` starts the new replica before it stops the old one. Set `API_REPLICAS=2` if ParkingService turns out to be small. The limits add up to more than the usage on purpose: they cap a runaway container instead of reserving memory. CPU numbers from the laptop do not transfer to a droplet; the rehearsal showed the API replicas reaching their 0.9-CPU limit at 20 iterations/s while a new replica was warming up, so expect latency to rise around deploys under heavy traffic.

## Server setup from zero

Droplet: **Ubuntu 24.04 LTS**, Basic, Regular SSD, **1 vCPU / 2 GB**, in the region closest to your users. Add your SSH public key in the create form (no password login). Enable the free monitoring agent.

As root, once:

```bash
# Swap: 2 GB, used only under memory pressure
fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
echo 'vm.swappiness=10' > /etc/sysctl.d/99-swappiness.conf && sysctl --system

# A non-root user with your SSH key
adduser --gecos "" deploy            # choose a password; it is only used for sudo
usermod -aG sudo deploy
rsync --archive --chown=deploy:deploy ~/.ssh /home/deploy

# SSH keys only, no root login
cat > /etc/ssh/sshd_config.d/10-hardening.conf <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
EOF
systemctl reload ssh

# Firewall: SSH, HTTP, HTTPS (and HTTP/3)
ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp && ufw --force enable

# Security updates every night, reboot at 04:00 UTC when a kernel update needs it
apt-get update && apt-get install -y unattended-upgrades
dpkg-reconfigure -plow unattended-upgrades
printf 'Unattended-Upgrade::Automatic-Reboot "true";\nUnattended-Upgrade::Automatic-Reboot-Time "04:00";\n' \
  > /etc/apt/apt.conf.d/52automatic-reboot
```

Open a **second** SSH session as `deploy` before you close the root session, to make sure the key works.

Docker (official repository), as `deploy`:

```bash
sudo apt-get install -y ca-certificates curl git
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update && sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker deploy    # log out and in again; membership of "docker" is equivalent to root
```

Docker publishes container ports past ufw. Only the edge stack publishes ports (80/443), so this is intended; do not add `ports:` to other services.

If the droplet has IPv6 and you publish an AAAA record, enable IPv6 in Docker (`"ipv6": true, "ip6tables": true` in `/etc/docker/daemon.json`), or Caddy sees IPv6 clients as Docker's gateway address and they share one rate limit. Without an AAAA record nothing is needed.

## DNS

The API needs a hostname that points at the droplet, so that Caddy can get a Let's Encrypt certificate. Any of these works; set the same name as `CHATHUB_HOST` in `deploy/edge/.env` and `.env.prod`:

| Option | How | Notes |
|---|---|---|
| **DuckDNS** (free) | Sign in at duckdns.org, create `chathub-api`, set its IPv4 to the droplet's address | `chathub-api.duckdns.org`. The droplet's IP is static, so no update cron is needed. |
| **sslip.io** (free, no account) | Nothing to register: `203-0-113-10.sslip.io` resolves to `203.0.113.10` | The name changes if the IP changes. |
| **Your own domain** | An `A` record `api.example.com` → droplet IP | Needed for good email deliverability (see Brevo). Move to it any time: change `CHATHUB_HOST` in both env files, `deploy/compose.sh up -d` and restart the edge. |

Check with `dig +short <host>` before starting Caddy. Let's Encrypt validates over port 80 or 443, so both must be open.

## Brevo (email)

1. Create a free Brevo account (300 emails a day; the worker's limits default to 250 a day and 20 a minute, see [NOTIFICATIONS.md](NOTIFICATIONS.md#sending-limits)).
2. **Sender**: *Senders, domains & dedicated IPs → Senders → Add a sender* and confirm the address. With your own domain, also *Domains → Add a domain* and create the DKIM, SPF and DMARC records Brevo shows (DKIM as TXT/CNAME, SPF as `include:` of Brevo's SPF in your existing record, and a `_dmarc` TXT record). Without a domain of your own, use a verified single sender; mail from a free-mail address relayed through Brevo is more likely to land in spam because it fails DMARC alignment.
3. **SMTP key**: *Settings → SMTP & API → SMTP → Generate a new SMTP key*. The page also shows the SMTP login (`…@smtp-brevo.com`). Put them in `.env.prod` as `SMTP_USER` / `SMTP_PASSWORD`, and the sender as `SMTP_FROM_ADDRESS`. Host `smtp-relay.brevo.com`, port 587, `StartTls` are the defaults.
4. The worker never sends to reserved domains (`.test`, `example.com`, …) in Production, so test with a real inbox.

## Images: GHCR access

CI pushes `ghcr.io/<owner>/chathub-{api,migrator,notifications,notifications-migrator,seeder}` tagged with the short commit SHA and `latest` ([CI.md](CI.md#job-docker)). The server pulls them in one of two ways:

- **Public packages (simplest):** on GitHub, your profile → *Packages* → each `chathub-*` package → *Package settings* → *Change visibility* → *Public*. The images contain no secrets.
- **Private packages:** create a classic personal access token with only `read:packages`, then on the server as `deploy`: `echo <token> | docker login ghcr.io -u <github-user> --password-stdin`.

## First deploy

```bash
sudo install -d -o deploy -g deploy /opt/chathub
sudo install -d -o deploy -g deploy -m 700 /var/backups/chathub
git clone https://github.com/M7MHM/ChatHub.git /opt/chathub && cd /opt/chathub

# 1. Edge proxy (once per server; ParkingService shares it)
cp deploy/edge/.env.example deploy/edge/.env && nano deploy/edge/.env      # ACME_EMAIL, CHATHUB_HOST
docker compose -f deploy/edge/compose.yml --env-file deploy/edge/.env up -d
docker logs -f edge-caddy-1          # wait for "certificate obtained successfully" for your host, then Ctrl+C

# 2. ChatHub settings
cp .env.prod.example .env.prod && chmod 600 .env.prod && nano .env.prod
#    generate the four secrets as described in the file; set CHATHUB_HOST, FRONTEND_ORIGIN, the SMTP values

# 3. Deploy a tag: the short SHA of a green CI run on main (Actions → CI → the run → docker jobs)
bash deploy/deploy.sh 3f9c2ab
```

`deploy.sh` pulls the images, starts PostgreSQL, Redis and RabbitMQ, runs both migrators, starts the API replicas one at a time and adds each to Caddy once healthy, then starts the worker. Then check from your laptop:

```bash
curl -sS https://<host>/health/live                  # Healthy
curl -sS -o /dev/null -w '%{http_code}\n' https://<host>/health/ready   # 404 (not public)
curl -sSI http://<host>/health/live | head -n 3      # 308 redirect to https
```

Register through the frontend and check that the verification email arrives. Finally install the backup timer ([Backups](#backups)).

Scripts are run with `bash deploy/<script>.sh`, so they do not need the executable bit. To call them as `deploy/deploy.sh`, run `chmod +x deploy/*.sh` once.

## Frontend on Vercel

Set these in the Vercel project (*Settings → Environment Variables*); the names are examples, use whatever your Next.js code reads:

| Variable | Value |
|---|---|
| `NEXT_PUBLIC_API_URL` | `https://<CHATHUB_HOST>` (requests go to `/api/...`) |
| `NEXT_PUBLIC_HUB_URL` | `https://<CHATHUB_HOST>/hubs/chat` |

The API must allow the frontend's origin: `FRONTEND_ORIGIN=https://<app>.vercel.app` in `.env.prod`, exactly as the browser sends it (scheme and host, no path or trailing slash). Preview deployments have other origins (`https://<app>-git-<branch>-<team>.vercel.app`); add one as `FRONTEND_EXTRA_ORIGIN` if you need it, then apply it with `bash deploy/deploy.sh "$(cat deploy/.state/current_tag)"`. Links in digest emails open `FRONTEND_ORIGIN`.

The SignalR client must connect with WebSockets and skip negotiation, because negotiate and connect can reach different replicas and there are no sticky sessions:

```ts
const connection = new HubConnectionBuilder()
  .withUrl(process.env.NEXT_PUBLIC_HUB_URL!, {
    accessTokenFactory: () => getAccessToken(),
    skipNegotiation: true,
    transport: HttpTransportType.WebSockets,
  })
  .withAutomaticReconnect()
  .build();
connection.onreconnected(() => refetchOpenConversation());
```

During a deploy the replica a client is connected to stops; the socket closes (code 1006) and `withAutomaticReconnect` reconnects within about a second (rehearsed). An event sent in that second can be missed, so refetch the open conversation in `onreconnected`. The access token travels in the `access_token` query parameter of the WebSocket URL; Caddy removes query strings from its logs.

## Deploying

```bash
bash deploy/deploy.sh <tag>                  # deploy a CI-built tag (short SHA)
bash deploy/deploy.sh --skip-migrations <tag>
bash deploy/rollback.sh                      # back to the previously deployed tag, without migrations
bash deploy/rollback.sh <tag>                # back to a specific tag
bash deploy/compose.sh ps                    # docker compose with .env.prod and the deployed tag
```

What `deploy.sh <tag>` does, in order (it is safe to run again; a second run with the same tag changes nothing):

1. Takes a lock (`deploy/.state/deploy.lock`) so two deploys cannot overlap, and checks that the edge proxy runs.
2. Pulls the four images of the tag. A missing tag stops here, before anything changed.
3. Starts PostgreSQL, Redis and RabbitMQ if they are not running and waits until they are healthy.
4. Runs the `migrator` and `notifications-migrator` containers (unless `--skip-migrations`).
5. For each API container on an older image or older settings (a fingerprint of the API's resolved compose configuration, stored as a container label): starts one new container, waits for its health check (`/health/ready`), adds it to Caddy, removes the old one from Caddy, waits `DRAIN_SECONDS` (5) for requests in flight, stops it gracefully (30 s) and removes it. If a new container does not become healthy within `HEALTH_TIMEOUT` (180 s), it is removed and the deploy stops; the old containers keep serving.
6. Recreates the notification worker on the new image (a few seconds without a consumer; events wait in RabbitMQ).
7. Records the tag in `deploy/.state/` (`current_tag`, `previous_tag`, `history`, and `tag.env`, which `deploy/compose.sh` uses), then removes local images of tags other than the current and the previous one.

A deploy took 80–90 s in the rehearsal (2 replicas). Rolling back runs the same steps with `--skip-migrations`.

**Changing settings** (`.env.prod`): edit the file, then `bash deploy/deploy.sh "$(cat deploy/.state/current_tag)"`. The API replicas are replaced one at a time with the new settings (rehearsed: 60 s for 2 replicas), the worker is recreated if its settings changed. Avoid plain `docker compose up -d` after a settings change: it recreates all API containers at once.

## Migrations: expand and contract

Old and new API containers run side by side during a deploy, and a rollback runs old code against the newest schema. So every migration must work with the code version before it:

- **Only additive changes in one release.** New tables, new nullable columns (or with defaults), new indexes (prefer `CREATE INDEX CONCURRENTLY` via `migrationBuilder.Sql` on large tables). Old code ignores them.
- **Renames take three releases.** Release 1 adds the new column and writes to both (backfill in the migration or a job). Release 2 reads from the new column and still writes both. Release 3 drops the old column, once no deployed or rollback-candidate version reads it.
- **Drops take two releases.** Release 1 stops reading and writing the column or table. Release 2 drops it.
- **Tightening constraints** (`NOT NULL`, unique) only after a release has backfilled and stopped writing violating rows.
- **No data rewrites that lock big tables** during a deploy; run long backfills in batches.
- `rollback.sh` never runs migrations. EF Core cannot "un-apply" a migration from the bundle; a real schema rollback means writing a new forward migration.

## Continuous deployment

[`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml) deploys a tag on demand (*Actions → Deploy → Run workflow*, input: the short SHA). It SSHes to the server and runs `bash deploy/deploy.sh <tag>`. Until it is configured it does nothing and reports "Deploy skipped". To enable it:

1. On the server, as `deploy`: `ssh-keygen -t ed25519 -f ~/ci_deploy -N ""`, append `~/ci_deploy.pub` to `~/.ssh/authorized_keys`, and keep the private key for the next step (then delete `~/ci_deploy` from the server).
2. On GitHub: *Settings → Environments → New environment* `production` (optionally with required reviewers). Add secrets:
   - `DEPLOY_SSH_HOST`: the droplet's IP or hostname
   - `DEPLOY_SSH_USER`: `deploy`
   - `DEPLOY_SSH_KEY`: the private key from step 1
   - `DEPLOY_SSH_KNOWN_HOSTS`: the output of `ssh-keyscan -t ed25519 <host>`, checked against the fingerprint the droplet console shows (`ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`)
3. Optional variables: `DEPLOY_PATH` (default `/opt/chathub`), `DEPLOY_SSH_PORT` (default 22).

The key can run anything `deploy` can, which includes Docker, so treat it like root access. The workflow refuses tags that are not plain `[A-Za-z0-9._-]` strings.

## Backups

`deploy/backup.sh` dumps both databases (`pg_dump` custom format, compressed, then read back with `pg_restore --list` to prove it is readable) and archives the uploaded files from the `storage` volume. Sets go to `BACKUP_DIR` (default `/var/backups/chathub`): `daily/` keeps 7, `weekly/` (Sunday's set) keeps 4. In the rehearsal a set took 8 s (3.1 MB for 1,000 users and 54,000 messages).

Install the daily timer (03:30 UTC):

```bash
sudo cp deploy/systemd/chathub-backup.service deploy/systemd/chathub-backup.timer /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now chathub-backup.timer
sudo systemctl start chathub-backup.service && journalctl -u chathub-backup.service -n 20
```

The backups sit on the same disk as the data, so copy them elsewhere too: enable DigitalOcean's weekly droplet backups, and/or pull them to your laptop regularly (`rsync -a deploy@<host>:/var/backups/chathub/ ./chathub-backups/`).

**Restore test** (do it monthly; rehearsed with identical row counts in every table):

```bash
bash deploy/restore.sh /var/backups/chathub/daily/chathub-<time>.dump --verify
bash deploy/restore.sh /var/backups/chathub/daily/chathub_notifications-<time>.dump --verify
```

`--verify` restores into a scratch database (`chathub_restore`), prints the row count of every table next to the live database, and drops the scratch database. Without `--verify` the scratch database is kept for you to inspect.

**Real restore** (data loss or corruption):

```bash
bash deploy/compose.sh stop api notifications
bash deploy/restore.sh /var/backups/chathub/daily/chathub-<time>.dump --into chathub --replace
bash deploy/restore.sh /var/backups/chathub/daily/chathub_notifications-<time>.dump --into chathub_notifications --replace
# uploaded files, if needed:
docker run --rm -v chathub-prod_storage:/data -v /var/backups/chathub/daily:/b:ro alpine \
  sh -c 'rm -rf /data/* && tar -C /data -xzf /b/storage-<time>.tar.gz && chown -R 1654:1654 /data'
bash deploy/deploy.sh "$(cat deploy/.state/current_tag)"   # starts api and the worker again
```

## Operations

| Task | Command |
|---|---|
| Status and health of every container | `bash deploy/compose.sh ps` |
| API / worker logs | `bash deploy/compose.sh logs -f --tail 100 api` (or `notifications`) |
| Access log | `docker logs -f edge-caddy-1` |
| Public liveness | `curl https://<host>/health/live` |
| Readiness with details (worker) | `bash deploy/compose.sh exec notifications bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080; printf "GET /health/ready HTTP/1.0\r\n\r\n" >&3; cat <&3'` |
| Memory and CPU | `docker stats --no-stream` |
| Database shell | `bash deploy/compose.sh exec postgres psql -U chathub` |
| RabbitMQ queues | `bash deploy/compose.sh exec rabbitmq rabbitmqctl list_queues name messages consumers` |
| Caddy config check / reload | `docker exec edge-caddy-1 caddy validate --config /etc/caddy/Caddyfile` / `caddy reload --config /etc/caddy/Caddyfile` |
| Disk | `df -h /` and `docker system df` |

Every container writes JSON logs capped at 3 × 10 MB. All services use `restart: unless-stopped`, so they come back after a reboot.

Editing the Caddyfile: change it, then `docker exec edge-caddy-1 caddy reload --config /etc/caddy/Caddyfile` (graceful). Changing `deploy/edge/.env` or `compose.yml` needs `docker compose -f deploy/edge/compose.yml --env-file deploy/edge/.env up -d`, which restarts Caddy and drops open WebSockets for a moment (clients reconnect).

## Guest mode in production

`POST /api/auth/guest` is public. Every visitor of the frontend can create an account with one click, so these settings in `.env.prod` bound what that costs:

| Setting (`.env.prod`) | Default | Effect |
|---|---|---|
| `GUEST_ENABLED` | `true` | `false` makes the endpoint answer 404 (the frontend should hide its button). Existing guests are still cleaned up. |
| `GUEST_LIFETIME_HOURS` | `24` | A guest, its tokens and its chats are deleted this long after it was created. |
| `GUEST_MAX_ACTIVE` | `500` | Live guests at once. Over it, `429 Guest.CapacityReached` until guests expire. Each guest costs about 30 rows (3 conversations, 8 memberships, 14 messages, a refresh token) plus the rows of what it does, so 500 guests are a few MB. The cap is checked before the insert, so a burst can overshoot it by the number of simultaneous requests. |
| `GUEST_CREATE_PER_IP_PER_HOUR` | `5` | Guests one client IP may create per hour (fixed window, per replica, real IP from Caddy's `X-Forwarded-For`). Offices and mobile carriers share IPs; raise it if real visitors hit the 429. |
| `BOT_ENABLED` | `true` | `false` stops ChatHub Bot's replies; guests still get their seeded chats. |

Also adjustable through the `Guest__*` and `Bot__*` variables (see `.env.example`): `Guest__CleanupIntervalMinutes` (5), `Bot__ReplyDelayMilliseconds` (1500), `Bot__MaxRepliesPerConversationPerMinute` (6) and `Bot__PollIntervalMilliseconds` (1000).

- **Cleanup.** Every replica runs the guest cleanup every `Guest__CleanupIntervalMinutes`. A PostgreSQL advisory lock (`pg_try_advisory_lock`) lets only one of them work at a time; the others skip that cycle. Each expired guest goes through the normal account deletion (tokens revoked, group memberships left, avatar marked deleted, `user.deleted` published, hub connections closed), then the conversations without any real user are deleted, together with their messages and bot jobs, and the guest's uploads are marked for the file cleanup. The log line is `Guest cleanup deleted N expired guest accounts.`
- **Personas.** The API creates the five demo personas at startup if they are missing (logged as `Created N demo personas.`), and the first guest sign-in does the same, so they exist in every environment without a seeder run. Their ids are fixed, so a restore or a second replica never duplicates them.
- **Bot jobs** live in `BotReplies`. Every replica polls for due jobs (`FOR UPDATE SKIP LOCKED`); the replica that received the message is also woken right when the job is due. Jobs older than a day are deleted.
- **Watching it.**
  ```sql
  SELECT count(*) FILTER (WHERE "GuestExpiresAtUtc" > now()) AS active, count(*) AS not_yet_deleted
  FROM "UserProfiles" WHERE "IsGuest" AND NOT "IsDeleted";
  SELECT count(*) FROM "BotReplies" WHERE "CompletedAtUtc" IS NULL;   -- pending bot replies, normally 0 or 1
  ```
- **Abuse.** Guests can only reach the personas, so spam cannot reach real users. Every guest message still goes through the outbox like any other, and the worker drops digests for guests and personas because it knows no address for them. If a flood shows up anyway, lower `GUEST_CREATE_PER_IP_PER_HOUR` or `GUEST_MAX_ACTIVE`, or set `GUEST_ENABLED=false`, then redeploy the current tag to apply it.

## When something breaks

| Symptom | Look at | Usual fix |
|---|---|---|
| Browser: certificate error, or Caddy logs `could not get certificate` | `docker logs edge-caddy-1`; `dig +short <host>`; ports 80/443 open in ufw and the DigitalOcean firewall | Fix DNS or the firewall; Caddy retries on its own. Let's Encrypt rate limits clear after an hour or a week depending on the limit. |
| 502 / 503 from Caddy | `bash deploy/compose.sh ps` (is an API container healthy?), `cat deploy/edge/upstreams/chathub.caddy` | `bash deploy/deploy.sh "$(cat deploy/.state/current_tag)"` re-creates missing replicas and rewrites the upstream list. |
| Deploy stops with "did not become healthy" | The API log lines printed by `deploy.sh` | Fix the cause (configuration, a migration); the old version is still serving. |
| CORS errors in the browser | `FRONTEND_ORIGIN` vs. the exact origin in the browser's error | Correct `.env.prod`, then `bash deploy/deploy.sh "$(cat deploy/.state/current_tag)"`. |
| SignalR fails to connect or keeps reconnecting | Browser console; the client must use `skipNegotiation` + WebSockets | See [Frontend on Vercel](#frontend-on-vercel). |
| Emails not sent | Worker log; readiness `sending` check (quota, backlog); Brevo dashboard → Transactional → Logs | Wrong SMTP key or unverified sender; daily limit reached; recipient on a reserved domain. |
| Containers restarting, `OOMKilled: true` in `docker inspect` | `docker stats`, `dmesg -T \| grep -i oom`, `free -m` | Lower `API_REPLICAS`, check ParkingService's usage, raise the limit of the container that hits it. |
| `too many clients already` from PostgreSQL | `POSTGRES_MAX_CONNECTIONS` vs. the pool budget in `.env.prod` | Keep `(API_REPLICAS + 1) × DB_MAX_POOL_SIZE + 10` below it. |
| RabbitMQ memory alarm, outbox health degraded | `rabbitmqctl status` (memory), the worker's health | A stuck or stopped worker; restart it. The alarm threshold is in `deploy/rabbitmq/10-chathub.conf`. |
| Login returns 429 for many users at once | All behind one NAT IP (office, school) | Raise `RATE_LIMIT_AUTH_STRICT`. |
| Disk full | `docker system df`, `/var/backups/chathub` | `docker image prune -a` (keeps images in use), check backup rotation. |

## Adding ParkingService

The edge stack is shared; ParkingService keeps its own compose project, network and database.

1. In ParkingService's compose file, attach its HTTP service to the external network with a unique alias, and publish no ports:

   ```yaml
   services:
     api:
       networks:
         default: {}
         edge:
           aliases: [parking-api]
   networks:
     edge:
       external: true
   ```

   If it reads `X-Forwarded-For`, trust only `172.31.0.2` (`EDGE_PROXY_IP`).
2. Uncomment the ParkingService block at the end of `deploy/edge/Caddyfile`, set `PARKING_HOST` (and its DNS record) in `deploy/edge/.env`, and apply with `docker compose -f deploy/edge/compose.yml --env-file deploy/edge/.env up -d`. For zero-downtime deploys of a scaled service, write an upstream file the way `deploy.sh` does (`deploy/edge/upstreams/parking.caddy` with `import /etc/caddy/upstreams/parking*.caddy`).
3. Give every ParkingService container a memory limit and keep the total within the [budget](#memory-budget).

## Local rehearsal

How the production setup was tested on a laptop, without a server, a public DNS name or real secrets. The same steps can be repeated before risky changes.

Setup: a local registry (`registry:2` on `127.0.0.1:5055`) holding images built from the working tree as `localhost:5055/chathub/chathub-*:rehearsal-a` and `:rehearsal-b`; the edge stack with `deploy/rehearsal/edge.env` (Caddy's internal CA via `EDGE_LOCAL_CERTS=local_certs`, host ports 8081/8443, hostname `chathub.rehearsal.test`) and `deploy/rehearsal/edge.rehearsal.yml`; the production stack through `deploy/deploy.sh` with `ENV_FILE=.env.rehearsal` (throwaway random secrets, `API_REPLICAS=2`, `RATE_LIMIT_AUTH_STRICT=100` so k6 can log in 60 users) and `COMPOSE_OVERRIDES=deploy/rehearsal/compose.rehearsal.yml` (Mailpit instead of Brevo, and the seeder); k6 ([`deploy/rehearsal/rolling.js`](../deploy/rehearsal/rolling.js)) in a container on the edge network, so all traffic went through Caddy with TLS.

```bash
export ENV_FILE=$PWD/.env.rehearsal COMPOSE_OVERRIDES=$PWD/deploy/rehearsal/compose.rehearsal.yml STATE_DIR=/tmp/chathub-rehearsal
docker compose -f deploy/edge/compose.yml -f deploy/rehearsal/edge.rehearsal.yml --env-file deploy/rehearsal/edge.env up -d
bash deploy/compose.sh up -d mailpit && bash deploy/deploy.sh rehearsal-a
bash deploy/compose.sh --profile seed run --rm seeder --confirm
docker run --rm --network edge -v "$PWD/deploy/rehearsal:/scripts:ro" -v /tmp/k6:/out -e RATE=20 -e DURATION=240 \
  grafana/k6:2.4.0 run /scripts/rolling.js &          # then, about 40 s later:
bash deploy/deploy.sh rehearsal-b
```

Results (2026-11-25):

| Check | Result |
|---|---|
| Functional, from the host through Caddy | register, login, direct chat, send and list messages, CORS for the frontend origin (and refused for another), verification email delivered to Mailpit with an `https://chathub.rehearsal.test/...` link that verifies the address through Caddy, HTTP→HTTPS redirect (308), HSTS, zstd compression, no `Server` header, `/health/ready`, `/health`, `/scalar`, `/openapi` answer 404, TLS 1.3 + HTTP/2 from Caddy's CA |
| Forged `X-Forwarded-For` | 260 failed logins with a different forged address each: 212 accepted, then 429 (100 per minute × 2 replicas): the forged header was ignored |
| Access log | 62 WebSocket requests logged as `/hubs/chat` without the query string; no `token=` anywhere |
| Rolling deploy a → b under load (k6: 20 it/s reads + 10% writes, 20 WebSocket listeners probing every 5 s) | **0 failed HTTP requests of 15,846**; 20 WebSocket disconnects (each listener once, when its replica stopped), **20 reconnects** (median 0.6 s, max 1.1 s); 935 of 936 probes delivered; deploy 87 s; p95 39 ms |
| Rollback b → a under the same load | **0 failed of 15,448**; 25 disconnects, 25 reconnects (max 5.4 s); 772 of 783 probes delivered; 175 s; p95 1.5 s while the laptop was busy |
| Deploy a → b again under load | **0 failed of 15,750**; 21 disconnects, 21 reconnects (max 1.0 s); 907 of 907 probes; 90 s; p95 235 ms (host CPU averaged 61%) |
| Same tag again | nothing replaced, 12 s |
| Settings change (`FRONTEND_EXTRA_ORIGIN` added), same tag | both replicas replaced one at a time with the new origin in 60 s; a further run and a manual `deploy/compose.sh up -d` left the containers alone |
| Broken release (API listening on the wrong port) | new container unhealthy → removed, deploy aborted with exit 1, the two old replicas kept serving, upstream list and recorded tag unchanged |
| Backup | both databases and the uploads in 8 s; dumps read back with `pg_restore --list` |
| Restore test | both dumps restored into scratch databases with **identical row counts in every table** (e.g. 54,024 messages, 1,006 users), scratch databases dropped |

WebSocket disconnects show as close code 1006: ASP.NET Core aborts upgraded connections when it shuts down instead of sending a close frame. Clients treat it like any other drop and reconnect. Latency numbers depend on the laptop's load and do not predict a droplet; failures and reconnects do.
