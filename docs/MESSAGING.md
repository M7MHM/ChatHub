# ChatHub messaging: transactional outbox and RabbitMQ

The API publishes **integration events** to RabbitMQ so that other services (first: the notification worker that sends email digests, phase 2) can react to what happens in ChatHub without calling the API or reading its database. Events are written to an **outbox table in the same transaction as the business change** and published by a background service. The contracts live in `ChatHub.Contracts`, a class library with no dependency on the other ChatHub projects, so a consumer can reference it on its own.

## Flow

```text
HTTP / SignalR request
  └─ command handler changes aggregates ──► db.SaveChangesAsync()
        ├─ OutboxInterceptor.SavingChangesAsync          (before the INSERT/UPDATEs)
        │     reads the domain events still attached to the tracked entities,
        │     maps the relevant ones to integration events (one enrichment query per event type),
        │     adds one OutboxMessages row per integration event to the same change set
        ├─ CacheInvalidationInterceptor.SavingChangesAsync  (collects cache keys, unchanged)
        ├─ EF Core writes everything in ONE transaction ──► COMMIT
        ├─ CacheInvalidationInterceptor.SavedChangesAsync    (invalidates, unchanged)
        └─ ApplicationDbContext publishes the domain events through MediatR (SignalR etc., unchanged)

OutboxPublisherService (BackgroundService, every API replica)
  loop:
    ensure the RabbitMQ connection + confirm channel (if this fails: back off, touch no rows)
    claim up to BatchSize pending rows       UPDATE … WHERE Id IN (SELECT … FOR UPDATE SKIP LOCKED) RETURNING …
    publish them all, then await all confirms (persistent, mandatory, MessageId = row Id)
    confirmed            → ProcessedAtUtc = now
    returned / nacked / timed out → Attempts + 1, LastError, LockedUntilUtc = now + backoff
    full batch → loop immediately; otherwise wait PollIntervalMilliseconds
  every CleanupIntervalMinutes: delete processed rows older than RetentionDays
```

Because the outbox rows are part of the business transaction, there are only two outcomes: the change and its events are both committed, or neither is (a rollback or a failed `SaveChanges` leaves no row; see `OutboxTests`). The post-save domain-event dispatch is unchanged, so SignalR and the other in-process handlers behave exactly as before.

`ApplicationDbContext.SaveChangesAsync` now clears the entities' domain events *after* the save instead of before it, so the interceptor can see them; it still copies them first and dispatches the copies, only after a successful save, as before. Only the async `SaveChangesAsync` path writes the outbox, the same path that dispatches domain events; the API never calls the synchronous `SaveChanges`.

The interceptor is registered before the cache-invalidation interceptor, so a mapping failure aborts the save before any cache key is collected, and the cache interceptor sees the final change set (it ignores outbox rows).

## Events

Exchange `chathub.events` (topic, durable). The AMQP `type` property is the record name, `message_id` is the event's `MessageId` (also the outbox row's primary key), `content_type` is `application/json`, delivery mode is persistent. The body is the record serialized with `MessageJson.Options` (`JsonSerializerDefaults.Web`: camelCase, enums as strings). Every event carries `messageId` (UUID v7) and `occurredAtUtc`.

| Routing key | Record | Raised by | Payload |
|---|---|---|---|
| `message.sent` | `MessageSent` | `Message.Create` (`MessageSentEvent`), i.e. `SendMessage` | `chatMessageId`, `conversationId`, `conversationType` (`Direct`/`Group`), `conversationTitle` (groups only), `senderId`, `senderDisplayName`, `preview`, `sentAtUtc`, `recipientUserIds`, `mutedRecipientUserIds` |
| `conversation.read` | `ConversationRead` | `ConversationMember.MarkAsRead` (`MessageReadEvent`), only when the read pointer moves forward | `conversationId`, `userId`, `readUpToUtc` |
| `user.registered` | `UserRegistered` | `UserProfile.Register` (`UserRegisteredEvent`), i.e. `Register` | `userId`, `email` (null for phone-only accounts), `displayName` |
| `user.profile-updated` | `UserProfileUpdated` | `UpdateProfile`, `ChangeEmail`, `ChangePhoneNumber` (`UserProfileUpdatedEvent`), only when something changed | `userId`, `email`, `displayName` (current values) |
| `user.deleted` | `UserDeleted` | `UserProfile.Delete` (`UserDeletedEvent`), i.e. `DeleteMyAccount` | `userId` |

Details a consumer can rely on:

- **Recipients** are the conversation's active members (not left, not kicked) whose account is not deleted, minus the sender. Muted members are recipients too and are also listed in `mutedRecipientUserIds`, so a notifier can skip them without needing mute events.
- **Preview** is the message on one line (whitespace runs collapsed), cut to 120 characters with `…` (never splitting a surrogate pair). Attachments use the same placeholders as the conversation list: `[Image]`, `[File]`, `[Voice]` (the caption is not included).
- **Timestamps** are UTC with microsecond precision, the precision PostgreSQL stores. `readUpToUtc` is the `CreatedAtUtc` of the last read message, so "message M is read by U" is exactly `M.sentAtUtc <= readUpToUtc` for the latest `ConversationRead` of U in that conversation.
- `MessageRoutes.RoutingKeyOf(event)` and `MessageRoutes.EventTypeOf(routingKey)` map between records and routing keys.
- Not published: system messages (no flow creates them; they are not user messages), edits, deletions, deliveries, presence and group-management events. Users created with `UserProfile.Create` (seeder, tests) are not "registrations".

Enrichment happens inside the save: `MessageSent` needs one query (members of the affected conversations joined to their users), whatever the number of messages or members; the other events are built from the tracked entities with no query.

## Why `FOR UPDATE SKIP LOCKED` and a lease

Every API replica runs a publisher. The claim is a single statement:

```sql
UPDATE "OutboxMessages" SET "LockedUntilUtc" = @now + lease
WHERE "Id" IN (
    SELECT "Id" FROM "OutboxMessages"
    WHERE "ProcessedAtUtc" IS NULL AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= @now)
    ORDER BY "OccurredAtUtc", "Id" LIMIT @batch
    FOR UPDATE SKIP LOCKED)
RETURNING "Id", "Type", "RoutingKey", "Payload", "OccurredAtUtc", "Attempts";
```

- `FOR UPDATE SKIP LOCKED` makes two replicas claiming at the same moment take **disjoint** rows instead of blocking on each other or both reading the same pending rows. A row another claim already updated is re-checked against the `WHERE` and skipped.
- The **lease** (`LockedUntilUtc`) keeps the claimed rows invisible to other replicas *after* the claim commits, while this replica publishes. Publishing does not hold a database transaction or connection open across network I/O to RabbitMQ, which matters with the 30-connection pool per replica.
- The lease is `2 × PublishTimeoutSeconds + 30 s` (60 s by default) and the publisher abandons a batch after `PublishTimeoutSeconds`, so a live replica always finishes or fails a batch well before its lease ends. If a replica dies mid-batch, its rows become claimable again when the lease expires.
- The same column doubles as the retry backoff: a failed row gets `LockedUntilUtc = now + min(MaxRetryDelaySeconds, 2^Attempts s)`.
- The partial index `IX_OutboxMessages_Pending (OccurredAtUtc) WHERE ProcessedAtUtc IS NULL` keeps the claim and the backlog check proportional to the pending rows, not the table. `IX_OutboxMessages_Processed` serves the cleanup.

`TwoPublishersRunningConcurrently_PublishEveryMessageExactlyOnce` drains 400 rows with two publishers on separate connections and checks every message reached the queue exactly once (with the `SKIP LOCKED`/lease conditions removed, the same test fails).

## Delivery guarantees

**At-least-once, unordered.** A message is marked processed only after the broker confirmed it, so nothing is lost while it is in the outbox. It can be delivered more than once when:

- the broker confirmed the publish but the `ProcessedAtUtc` update failed or the replica stopped before it ran (the lease expires, another replica publishes it again);
- a confirm did not arrive within `PublishTimeoutSeconds` although the broker had stored the message.

**Consumers must be idempotent.** Use the AMQP `message_id` / `messageId` as the deduplication key (for example, a processed-message table with a unique key, written in the same transaction as the consumer's own change). Do not rely on order: batches are published in `OccurredAtUtc` order, but two replicas, retries and backoff can reorder events. Where order matters, compare timestamps (`readUpToUtc`, `sentAtUtc`) instead of arrival order, and treat `UserProfileUpdated` as "the state at `occurredAtUtc`", keeping the newest.

The broker's publisher confirms guarantee that a confirmed persistent message routed to a durable queue survives a broker restart.

## Failure handling

| Situation | What happens |
|---|---|
| RabbitMQ unreachable | The publisher fails to connect **before** claiming, so rows are not touched (`Attempts` stays 0). It logs a warning and retries with backoff (1, 2, 4 … s, capped at `min(MaxRetryDelaySeconds, 30 s)`). The API keeps serving; events wait in the outbox. |
| Connection or channel lost mid-batch | Unconfirmed rows get `Attempts + 1` and backoff; the channel is discarded and reopened on the next loop. |
| `basic.return` (unroutable: no queue bound for the routing key) | Treated as a failure (`LastError` starts with `Unroutable`), retried with backoff. |
| `basic.nack` | Failure, retried with backoff. |
| No confirm within `PublishTimeoutSeconds` | Failure, channel discarded, retried with backoff (may cause a duplicate, see above). |
| PostgreSQL unavailable | The loop logs a warning and backs off; nothing is lost. |

There is no maximum number of attempts and no dead-letter state: an event that keeps failing stays pending with its `LastError` and shows up in the backlog health check.

**Until a consumer exists, every event is unroutable.** The API does not declare queues, so with nothing bound to `chathub.events` each publish is returned and retried (backoff up to 5 minutes), and `/health/ready` turns `Degraded` once the oldest pending event is older than `BacklogAlertMinutes`. Nothing is lost: when the phase 2 worker binds its queue, the backlog drains into it (consumers should therefore expect old events after an outage). To run without RabbitMQ consumers, bind a queue by hand (see [Inspecting messages](#inspecting-messages-in-the-rabbitmq-ui)) or set `MESSAGING_ENABLED=false`.

## Topology

- The API declares only the exchange `chathub.events` (type `topic`, durable, not auto-deleted).
- Consumers own their queues and bindings. The notification worker (phase 2) is expected to declare a durable (quorum) queue, e.g. `chathub.notifications`, bound to `message.sent`, `conversation.read` and `user.*`, with manual acks, a prefetch limit and its own dead-lettering.
- Routing keys are `<entity>.<event>`, so `user.*` or `#` bindings work as expected.

## Configuration

Section `Messaging` (environment variables `Messaging__<Name>`). When `Enabled` is `false` (the default in `appsettings.json`), neither the outbox interceptor nor the publisher is registered: no rows are written, nothing connects, and the RabbitMQ health checks are not added. When `true`, the settings are validated at startup and the API refuses to start if they are invalid.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` (`true` in docker-compose) | Write outbox rows and publish them |
| `Host` / `Port` / `VirtualHost` | `localhost` / `5672` / `/` | Broker address |
| `User` / `Password` | (required) | Broker credentials; docker-compose passes `RABBITMQ_USER` / `RABBITMQ_PASSWORD` |
| `BatchSize` | `100` (1–1000) | Rows claimed and published per round trip |
| `PollIntervalMilliseconds` | `1000` | Wait after a batch that was not full; a full batch loops immediately |
| `PublishTimeoutSeconds` | `15` | Max wait for a batch's confirms; the lease is 2× this + 30 s |
| `MaxRetryDelaySeconds` | `300` | Cap of the per-message backoff (and, up to 30 s, of reconnect attempts) |
| `RetentionDays` | `3` | Processed rows older than this are deleted |
| `CleanupIntervalMinutes` | `60` | How often each replica runs the cleanup (in batches of 5,000, `SKIP LOCKED`) |
| `BacklogAlertMinutes` | `5` | The outbox health check reports `Degraded` once the oldest pending event is older than this |

docker-compose: the `rabbitmq` service (`rabbitmq:4-management`) has a fixed hostname (RabbitMQ stores its data per node name), the `chathub-rabbitmq` volume, a `check_port_connectivity` health check, and the management UI on `127.0.0.1:15672` only. AMQP (5672) is reachable inside the compose network only. The API waits for it to be healthy. Credentials come from `.env` (`RABBITMQ_USER`, `RABBITMQ_PASSWORD`, default `chathub`/`chathub`) and are applied only when the volume is first created.

## Health

`/health/ready` (and `/health`) gain two checks, both reported as `Degraded` rather than `Unhealthy`, like Redis: the API keeps working without them.

- `rabbitmq`: the publisher's connection and channel are open. The description carries the last connection error.
- `outbox`: `Degraded` when the oldest pending row is older than `BacklogAlertMinutes`; the description gives the number of pending rows and the age.

`loadtest/run.ps1` waits for `Healthy`, so it needs a bound queue or `MESSAGING_ENABLED=false` while no consumer exists.

## Inspecting messages in the RabbitMQ UI

1. Open http://127.0.0.1:15672 and sign in with `RABBITMQ_USER` / `RABBITMQ_PASSWORD`.
2. **Connections**: each replica has one connection named `chathub-api <instance id> outbox`, with one channel in confirm mode.
3. **Exchanges → chathub.events**: the publish rate graph; *Bindings* lists the queues that receive events.
4. To look at events without a consumer: **Queues → Add a new queue** (e.g. `debug.events`, durable or transient), then on the queue page **Bindings → From exchange** `chathub.events`, routing key `#` (or `message.sent`, `user.*`, …). New events now route to it, and pending ones drain into it on their next retry.
5. On the queue page, **Get messages** (Ack mode *Nack message requeue true* to leave them in the queue) shows the properties (`message_id`, `type`, `delivery_mode: 2`, `content_type`) and the JSON payload.
6. Delete the debug queue afterwards; a queue without a consumer keeps growing.

The outbox itself:

```sql
-- pending, oldest first, with the last failure
SELECT "Id", "Type", "OccurredAtUtc", "Attempts", "LockedUntilUtc", "LastError"
FROM "OutboxMessages" WHERE "ProcessedAtUtc" IS NULL ORDER BY "OccurredAtUtc" LIMIT 50;

-- throughput of the last hour
SELECT date_trunc('minute', "ProcessedAtUtc") AS minute, count(*)
FROM "OutboxMessages" WHERE "ProcessedAtUtc" > now() - interval '1 hour' GROUP BY 1 ORDER BY 1;
```

## Data and privacy

`UserRegistered` and `UserProfileUpdated` contain the email address and display name. Processed rows are deleted after `RetentionDays`; pending rows stay until published. Deleting an account does not remove earlier outbox rows or messages already in consumer queues: consumers must handle `user.deleted` by erasing what they stored about the user.

## Tests

- `tests/ChatHub.Application.IntegrationTests/Features/OutboxTests.cs`: mapping (recipients, muted, deleted and departed members, previews, read pointer, user events, events that are not published), atomicity (rollback and failed save leave no row), one enrichment query regardless of group size, cache invalidation still working, and messaging disabled.
- `…/Features/OutboxPublisherTests.cs` (PostgreSQL + RabbitMQ containers): delivery with AMQP properties, unroutable messages with exponential backoff, lease expiry, two concurrent publishers without duplicates, no claim while the broker is unreachable, broker stop/start with the background loop, cleanup.
- `tests/ChatHub.Api.IntegrationTests/MessagingTests.cs`: readiness with RabbitMQ, HTTP send → message on a bound queue, `Degraded` backlog, startup validation.
