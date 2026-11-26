# ChatHub

[![CI](https://github.com/M7MHM/ChatHub/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/M7MHM/ChatHub/actions/workflows/ci.yml)

A real-time chat backend: direct and group conversations, messages with attachments, replies, read and delivery receipts, presence and typing over SignalR. A separate worker emails digests of unread messages and sends verification and password reset emails. It is built with .NET 10 and Clean Architecture, and it runs as two API replicas behind nginx.

## Features

- **Accounts**: registration with a display name, a unique username and an email or an Egyptian mobile number; sign-in with email, phone or username; JWT access tokens with rotating refresh tokens; change password; delete account.
- **Usernames**: 3-30 characters (`a-z`, `0-9`, `_`, `.`), stored lowercase, reserved names refused, freed when an account is deleted; a live availability check (`GET /api/users/username-availability`); one change every 14 days (`PUT /api/users/me/username`).
- **Email**: address verification, one-click unsubscribe, and forgot/reset password (`POST /api/auth/forgot-password`, `POST /api/auth/reset-password`) with links to the web client and no account enumeration.
- **Conversations**: direct chats and groups with owners, admins and members; mute; clear chat; group info and images.
- **Messages**: text, images, files and voice notes; replies; edit and delete for everyone; read and delivery receipts; cursor paging.
- **Real time**: SignalR events for messages, receipts, membership, profiles, typing and presence, across replicas through Redis.
- **Presence privacy**: hide your online status and last seen (`PUT /api/users/me/privacy`); like WhatsApp, whoever hides theirs cannot see anyone else's. The server applies it to every response and event.
- **Users**: profiles with avatars, search by name, username, email or phone, blocking.
- **Notifications worker**: digests of unread messages and transactional emails, with sending limits and suppressed domains.

## Stack

- .NET 10, ASP.NET Core (controllers and SignalR), MediatR, FluentValidation
- PostgreSQL 17 through EF Core 10 (Npgsql)
- Redis 8: second-level cache for `HybridCache`, the SignalR backplane, and connection tracking across replicas
- RabbitMQ 4: integration events published from a transactional outbox
- nginx in front of the API replicas; Mailpit catches email locally
- Tests: xUnit, Shouldly, Testcontainers, Respawn. Load tests: k6

| Project | Contents |
|---|---|
| `ChatHub.Domain` | Aggregates, value objects and their invariants; no dependencies |
| `ChatHub.Application` | Use cases as MediatR commands and queries, validation, cache keys |
| `ChatHub.Infrastructure` | EF Core, Redis, SignalR hub, RabbitMQ outbox publisher, identity, file storage |
| `ChatHub.WebAPI` | HTTP endpoints, hub endpoint, health checks, rate limiting |
| `ChatHub.Contracts` | Integration event contracts, shared with consumers |
| `ChatHub.Notifications` | Worker that consumes the events and sends email; has its own database |
| `tools/ChatHub.Seeder` | Seeds test data and backfills the notification service |

## Run it locally

You need Docker.

```bash
cp .env.example .env     # replace every "replace-me"; secrets need 32+ bytes: openssl rand -base64 48
docker compose up -d --build
```

Compose runs both migrators first, then the API replicas, the worker and nginx.

| What | Where |
|---|---|
| API (nginx, two replicas) | http://localhost:8080, readiness at `/health/ready` |
| RabbitMQ management | http://127.0.0.1:15672 |
| Mailpit (every email sent) | http://127.0.0.1:8025 |

Seed data (1,000 users, 600 chats, 50k messages): `docker compose --profile seed run --rm seeder --confirm`. The OpenAPI document (`/openapi/v1.json`) and the Scalar UI (`/scalar`) are only mapped when `ASPNETCORE_ENVIRONMENT=Development`.

## Tests

```bash
dotnet test ChatHub.slnx
```

Docker must be running. The integration tests start their own PostgreSQL, Redis, RabbitMQ and Mailpit containers with Testcontainers, so they need no local database and no fixed ports. Tests that use Windows ACLs are skipped on other operating systems. [tests/COVERAGE.md](tests/COVERAGE.md) describes what each project covers.

## CI

[GitHub Actions](.github/workflows/ci.yml) builds and tests every pull request to `main` and every push to `main`, then builds all five Docker images. Pushes to `main` also publish the images to `ghcr.io/<owner>/chathub-*`. Dependabot proposes NuGet, Docker and Actions updates weekly. See [docs/CI.md](docs/CI.md).

## Production

One small server runs [docker-compose.prod.yml](docker-compose.prod.yml) behind a shared Caddy proxy ([deploy/edge](deploy/edge)) with automatic HTTPS. `deploy/deploy.sh <tag>` rolls out a CI-built image tag without downtime, `deploy/rollback.sh` goes back, and `deploy/backup.sh` / `deploy/restore.sh` handle backups. A manual [Deploy workflow](.github/workflows/deploy.yml) can run the deploy over SSH once its secrets are set. Server setup, DNS, email, the memory budget and the local rehearsal results are in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Docs

- [docs/MESSAGING.md](docs/MESSAGING.md): transactional outbox and RabbitMQ
- [docs/NOTIFICATIONS.md](docs/NOTIFICATIONS.md): the notification worker, digests, verification and password reset emails, sending limits
- [docs/PERFORMANCE.md](docs/PERFORMANCE.md): load-test results and the tuning behind them
- [docs/CI.md](docs/CI.md): the CI pipeline, images and branch protection
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md): server setup, deploys, rollbacks, backups and operations
- [tests/COVERAGE.md](tests/COVERAGE.md): test projects and what they prove
- [loadtest/README.md](loadtest/README.md): running the k6 load tests
