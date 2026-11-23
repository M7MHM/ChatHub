# ChatHub messaging: transactional outbox and RabbitMQ

The API publishes **integration events** to RabbitMQ so that other services (today: the [notification service](NOTIFICATIONS.md) that emails digests of unread messages) can react to what happens in ChatHub without calling the API or reading its database. Events are written to an **outbox table in the same transaction as the business change** and published by a background service. The contracts live in `ChatHub.Contracts`, a class library with no dependency on the other ChatHub projects, so a consumer can reference it on its own.

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
| `user.registered` | `UserRegistered` | `UserProfile.Register` (`UserRegisteredEvent`), i.e. `Register` | `userId`, `email` (null for phone-only accounts), `displayName`, `emailVerified`, `emailNotificationsEnabled`, `unsubscribeLink` |
| `user.profile-updated` | `UserProfileUpdated` | `UpdateProfile`, `ChangeEmail`, `ChangePhoneNumber` (`UserProfileUpdatedEvent`), `VerifyEmail` (`EmailVerifiedEvent`), `UpdateEmailPreferences` and `Unsubscribe` (`EmailNotificationsChangedEvent`), a new unsubscribe token (`UnsubscribeTokenIssuedEvent`), only when something changed | `userId`, `email`, `displayName`, `emailVerified`, `emailNotificationsEnabled` (current values), `unsubscribeLink` (only when a new link was issued, otherwise null) |
| `user.deleted` | `UserDeleted` | `UserProfile.Delete` (`UserDeletedEvent`), i.e. `DeleteMyAccount` | `userId` |
| `user.status-changed` | `UserStatusChanged` | `UserProfile.GoOnline` / `GoOffline` (`UserStatusChangedEvent`), i.e. the first connection and the last disconnection across replicas, and account deletion while online | `userId`, `isOnline` (the change happened at `occurredAtUtc`) |
| `message.edited` | `MessageEdited` | `Message.EditContent` (`MessageEditedEvent`), i.e. `EditMessage`, only when the text changed | `chatMessageId`, `conversationId`, `preview` (same rules as `message.sent`), `editedAtUtc` |
| `message.deleted` | `MessageDeleted` | `Message.Delete` (`MessageDeletedEvent`), i.e. `DeleteMessage`, which deletes for everyone | `chatMessageId`, `conversationId` |
| `conversation.member-left` | `MemberLeft` | `Conversation.LeaveMember` (`MemberLeftEvent`), i.e. `LeaveConversation` and account deletion (once per group) | `conversationId`, `userId` |
| `conversation.member-kicked` | `MemberKicked` | `Conversation.KickMember` (`MemberKickedEvent`) | `conversationId`, `userId`, `kickedByUserId` |
| `conversation.mute-changed` | `ConversationMuteChanged` | `ConversationMember.Mute` / `Unmute` (`MemberMuteChangedEvent`, new), only when the state changes | `conversationId`, `userId`, `isMuted` |
| `user.email-verification-requested` | `EmailVerificationRequested` | `UserProfile.RequestEmailVerification` (`EmailVerificationRequestedEvent`), i.e. `Register` with an email and `RequestEmailVerification` | `userId`, `email`, `displayName`, `verificationLink`, `expiresAtUtc` |

Details a consumer can rely on:

- **Recipients** are the conversation's active members (not left, not kicked) whose account is not deleted, minus the sender. Muted members are recipients too and are also listed in `mutedRecipientUserIds`, so a notifier can skip them without needing mute events.
- **Preview** is the message on one line (whitespace runs collapsed), cut to 120 characters with `…` (never splitting a surrogate pair). Attachments use the same placeholders as the conversation list: `[Image]`, `[File]`, `[Voice]` (the caption is not included).
- **Timestamps** are UTC with microsecond precision, the precision PostgreSQL stores. `readUpToUtc` is the `CreatedAtUtc` of the last read message, so "message M is read by U" is exactly `M.sentAtUtc <= readUpToUtc` for the latest `ConversationRead` of U in that conversation.
- `MessageRoutes.RoutingKeyOf(event)` and `MessageRoutes.EventTypeOf(routingKey)` map between records and routing keys.
- **User state is one event.** Verification and the email preference travel in `UserProfileUpdated` rather than in events of their own, so a consumer applies them with the same "newest `occurredAtUtc` wins" rule as the address. A separate `EmailVerified` event could arrive after a newer address change and mark the new, unproven address as verified. Several changes to one user in one save produce a single `UserProfileUpdated`.
- **`unsubscribeLink` is not state.** The API stores only a hash of the unsubscribe token, so it can put the link in an event only when it creates one: at registration, during the backfill, or when the token is rotated. `null` means "no new link, keep the one you have". Consumers keep the newest link by `occurredAtUtc`, independently of the profile fields.
- **New fields have safe defaults.** The new parameters of `UserRegistered` and `UserProfileUpdated` are optional (`emailVerified = false`, `emailNotificationsEnabled = true`, `unsubscribeLink = null`), so payloads written before the change still deserialize, and an old event never turns notifications off.
- **Links** start with `App:PublicApiBaseUrl` (default `http://localhost:8080`): `{base}/api/auth/verify-email?token=…` and `{base}/api/email/unsubscribe?token=…`.
- Not published: system messages (no flow creates them; they are not user messages, so their edits and deletions are not published either), deliveries, conversation creation, group info changes, members added and role changes. [NOTIFICATIONS.md](NOTIFICATIONS.md#domain-events-we-intentionally-do-not-publish) explains each against the digest rules. Users created with `UserProfile.Create` (seeder, tests) are not "registrations".
- `occurredAtUtc` of `MemberLeft`, `MemberKicked`, `ConversationMuteChanged` and `UserStatusChanged` is the moment of the change; consumers order these by it (a leave or mute covers every message sent up to then, presence is newest-wins). Several mute changes of one member in one save produce one event with the final state; several presence changes of one user, one event.

Enrichment happens inside the save: `MessageSent` needs one query (members of the affected conversations joined to their users), whatever the number of messages or members; the other events are built from the tracked entities with no query. The raw verification and unsubscribe tokens reach the mapper only through the in-memory domain events (`EmailVerificationRequestedEvent`, `UnsubscribeTokenIssuedEvent`); the database stores their SHA-256 hashes.

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

**Events need a bound queue.** The API does not declare queues: with nothing bound to `chathub.events`, each publish is returned and retried (backoff up to 5 minutes), and `/health/ready` turns `Degraded` once the oldest pending event is older than `BacklogAlertMinutes`. The notification service declares and binds the durable `chathub.notifications` queue when it starts. After it has started once, events are routable even while it is stopped: they wait in that queue. Before that, nothing is lost either: the backlog drains into the queue as soon as it is bound, so consumers must expect old events after an outage. To run without the notification service, bind a queue by hand (see [Inspecting messages](#inspecting-messages-in-the-rabbitmq-ui)) or set `MESSAGING_ENABLED=false`.

## Topology

- The API declares only the exchange `chathub.events` (type `topic`, durable, not auto-deleted).
- Consumers own their queues and bindings. The notification service declares `chathub.notifications` (durable, bound to all twelve routing keys, manual acks, prefetch), a TTL retry queue and a dead-letter queue behind `chathub.events.dlx`; see [NOTIFICATIONS.md](NOTIFICATIONS.md#rabbitmq-topology). They are classic queues: on this single-node broker a quorum queue adds Raft overhead without adding safety. Switch to quorum queues when RabbitMQ runs as a cluster.
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

docker-compose: the `rabbitmq` service (`rabbitmq:4-management`) has a fixed hostname (RabbitMQ stores its data per node name), the `chathub-rabbitmq` volume, a TCP health check on the AMQP port, and the management UI on `127.0.0.1:15672` only. The first health check, `rabbitmq-diagnostics`, started an Erlang VM per probe, using up to 2.4 cores each time (see [PERFORMANCE.md](PERFORMANCE.md#outbox-impact-2026-11-22)). AMQP (5672) is reachable inside the compose network only. The API only waits for RabbitMQ to be *started* (`service_started`, not `service_healthy`): the chat starts and works while RabbitMQ is down or unhealthy, and events wait in the outbox. This was checked by stopping RabbitMQ, restarting the API, sending a message (accepted, outbox row pending, readiness `Degraded`) and starting RabbitMQ again: the digest email arrived about 10 s later. Credentials come from `.env` (`RABBITMQ_USER`, `RABBITMQ_PASSWORD`, default `chathub`/`chathub`) and are applied only when the volume is first created.

## Health

`/health/ready` (and `/health`) gain two checks, both reported as `Degraded` rather than `Unhealthy`, like Redis: the API keeps working without them.

- `rabbitmq`: the publisher's connection and channel are open. The description carries the last connection error.
- `outbox`: `Degraded` when the oldest pending row is older than `BacklogAlertMinutes`; the description gives the number of pending rows and the age.

`loadtest/run.ps1` waits for `Healthy`. With the notification service running, its queue keeps the backlog at zero; without it, bind a queue or set `MESSAGING_ENABLED=false`.

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

The notification service keeps its own copy of every user's email address and display name (`Recipients` in `chathub_notifications`), and clears it when it consumes `user.deleted`.

`UserRegistered`, `UserProfileUpdated` and `EmailVerificationRequested` contain the email address and display name, and two of them carry secrets: `verificationLink` (a single-use token valid for 24 hours) and `unsubscribeLink` (a token that only turns email digests off). Once the broker has confirmed one of these rows, the publisher replaces its `Payload` with `{}` in the same update that sets `ProcessedAtUtc` (`OutboxMessage.RedactedOncePublished`), so the outbox keeps neither addresses nor tokens after publishing; the row itself (id, type, timestamps) stays for `RetentionDays` for diagnostics. Pending rows keep their payload until they are published. Other processed rows are deleted after `RetentionDays`. Deleting an account does not remove messages already in consumer queues: consumers must handle `user.deleted` by erasing what they stored about the user.

Why the raw tokens may pass through the outbox and RabbitMQ: a verification token works once and for 24 hours, and an unsubscribe token can do nothing but turn digests off; both travel only on the internal compose network (AMQP is not published on the host), RabbitMQ deletes a message once the consumer acks it, and the outbox redacts the payload as soon as it is published. The alternative, letting the worker read the ChatHub database or share a signing key, would widen access more than these short-lived, low-power tokens do.

## Tests

- `tests/ChatHub.Application.IntegrationTests/Features/OutboxTests.cs`: mapping (recipients, muted, deleted and departed members, previews, read pointer, user events with verification, preference and unsubscribe link, `EmailVerificationRequested` with a link to the configured API, edits and deletions, leave, kick and mute changes, presence, account deletion leaving its groups, events that are not published), atomicity (rollback and failed save leave no row), one enrichment query regardless of group size, cache invalidation still working, and messaging disabled.
- `…/Features/OutboxPublisherTests.cs` (PostgreSQL + RabbitMQ containers): delivery with AMQP properties, payload redaction after publishing, unroutable messages with exponential backoff, lease expiry, two concurrent publishers without duplicates, no claim while the broker is unreachable, broker stop/start with the background loop, cleanup.
- `tests/ChatHub.Api.IntegrationTests/MessagingTests.cs`: readiness with RabbitMQ, HTTP send → message on a bound queue, `Degraded` backlog, startup validation.
